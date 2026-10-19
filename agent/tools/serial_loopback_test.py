"""串口闭环验证：PC → COM 串口 → ESP32-S3 → BLE 读回。

原理（零固件改动）：
  固件 test.c 的 uart_debug_task 收到字节 '5' 时执行 s_light.mode++（0..8 循环）。

关键坑（已实锤）：
  ESP32-S3 的 USB-Serial-JTAG 在【打开串口时】若主机拉高 DTR/RTS 会触发
  USB_UART_CHIP_RESET（boot 日志 rst:0x15）。pyserial 默认开端口就拉 DTR。
  所以必须【open() 之前】设 dtr=False, rts=False，DCB 才会带
  DTR_CONTROL_DISABLE，从而不复位。

流程：
  0) 扫描拿设备对象
  1) 打开串口（不触发复位）→ 静默 1 秒自检，应无 boot 日志
  2) BLE 连接（全程一条）
  3) 读 light_mode 初值 → 串口发 1 个 '5' → 读回应 +1
  4) 再发 8 个 '5'（共 9 次）→ 读回应恢复初值
  5) 关串口、断 BLE

用法: python agent/tools/serial_loopback_test.py [COM口]
默认 COM7。
"""

import asyncio
import sys
import time

import serial
from bleak import BleakClient, BleakScanner

ADDR = "84:C7:BB:56:6A:16"
MODE_UUID = "00000000-0000-6564-6f6d-5f746867696c"  # light_mode (i32, 小端)
PORT = sys.argv[1] if len(sys.argv) > 1 else "COM7"


def log(msg: str) -> None:
    print(msg, flush=True)


def open_serial_no_reset() -> serial.Serial:
    """打开串口但不拉 DTR/RTS —— 避免复位 ESP32-S3。"""
    port = serial.Serial()
    port.port = PORT
    port.baudrate = 115200
    port.timeout = 0.3
    port.dtr = False  # 必须在 open() 之前设置
    port.rts = False
    port.open()
    return port


def read_port_text(port: serial.Serial, seconds: float) -> str:
    """读串口几秒，返回文本（用于捕获 ESP 副控制台日志）。"""
    buf = b""
    deadline = time.time() + seconds
    while time.time() < deadline:
        data = port.read(4096)
        if data:
            buf += data
    return buf.decode("utf-8", errors="replace").strip()


async def read_mode(client: BleakClient) -> int:
    last_err = None
    for attempt in range(4):
        try:
            data = await client.read_gatt_char(MODE_UUID)
            return int.from_bytes(data, byteorder="little", signed=True)
        except OSError as exc:
            last_err = exc
            log(f"    read 重试 {attempt + 1}（{exc}）")
            await asyncio.sleep(1.0)
    raise last_err


async def main() -> int:
    # 0) 扫描拿设备对象（WinRT 用设备对象连接更可靠）
    log("扫描板子 …")
    found = await asyncio.wait_for(BleakScanner.discover(timeout=10.0, return_adv=True), timeout=12.0)
    device = next((d for addr, (d, _a) in found.items() if addr.upper() == ADDR), None)
    if device is None:
        log("[FAIL] 扫不到板子 —— 可能被其它设备连着")
        return 1
    log(f"找到 {device.address} name={device.name!r}")

    # 1) 打开串口（不触发复位），静默自检
    port = open_serial_no_reset()
    boot_leak = port.read(4096)  # 最多等 1 秒
    if boot_leak:
        log(f"[WARN] 打开串口后仍读到 {len(boot_leak)} 字节（可能有复位日志？）: "
            f"{boot_leak[:80]!r}")
    else:
        log(f"串口 {PORT} 已打开（dtr/rts 关闭，无复位日志）")

    # 2) BLE 连接
    client = BleakClient(device, timeout=10.0)
    last_err = None
    for attempt in range(3):
        try:
            await asyncio.wait_for(client.connect(), timeout=12.0)
            break
        except (OSError, asyncio.TimeoutError) as exc:
            last_err = exc
            log(f"    connect 重试 {attempt + 1}（{exc}）")
            try:
                await client.disconnect()
            except Exception:
                pass
            await asyncio.sleep(2.0)
    else:
        log(f"[FAIL] 连接失败：{last_err}")
        port.close()
        return 1

    log(f"BLE 已连接 MTU={client.mtu_size}")
    try:
        before = await read_mode(client)
        log(f"[1] 串口发送前 light_mode = {before}")

        port.write(b"5")
        await asyncio.sleep(1.0)
        echo = read_port_text(port, 1.5)
        if echo:
            log(f"    [串口RX] {echo}")
        mid = await read_mode(client)
        log(f"[2] 串口发 1 次 '5' 后 light_mode = {mid}")
        if mid != (before + 1) % 9:
            log("[FAIL] 模式没变 —— 串口字节没有被固件接收（或 COM 口不对）")
            return 1

        port.write(b"5" * 8)
        await asyncio.sleep(1.0)
        echo = read_port_text(port, 1.5)
        if echo:
            log(f"    [串口RX] {echo}")
        after = await read_mode(client)
        log(f"[3] 共发 9 次 '5' 后 light_mode = {after}")
        if after != before:
            log(f"[FAIL] 模式没回到初值 {before}（实际 {after}）")
            return 1

        log(f"[PASS] 闭环验证成功：PC→{PORT}→ESP32-S3 收到命令→BLE 读回，"
            f"模式 {before}→{mid}→{after} 完整可观测")
        return 0
    finally:
        port.close()
        try:
            await client.disconnect()
        except Exception:
            pass


if __name__ == "__main__":
    try:
        sys.exit(asyncio.run(asyncio.wait_for(main(), timeout=90.0)))
    except (asyncio.TimeoutError, TimeoutError):
        log("[FAIL] 全局 90 秒超时")
        sys.exit(1)
