"""分离实验：定位"第二次 BLE 读挂起"的根因。

Test A: 纯 BLE —— 连接后连续读两次 light_mode（中间不碰串口）。
        A 挂 → 是 WinRT/bleak 侧问题；A 过 → 问题与串口写或模式切换相关。

Test B: 连接 + 开串口（不触发复位）+ 后台监视串口输出 + 写 '5' + 再读。
        若串口上出现 "rst:0x" boot 日志 → ESP 收到命令后重启了（比如 CHASE
        模式在无灯带的开发板上崩溃）。
"""

import asyncio
import inspect
import sys

import serial
from bleak import BleakClient, BleakScanner

ADDR = "84:C7:BB:56:6A:16"
MODE_UUID = "00000000-0000-6564-6f6d-5f746867696c"
PORT = sys.argv[1] if len(sys.argv) > 1 else "COM7"


def log(msg: str) -> None:
    print(msg, flush=True)


async def scan_device():
    log("扫描板子 …")
    found = await asyncio.wait_for(BleakScanner.discover(timeout=10.0, return_adv=True), timeout=12.0)
    device = next((d for addr, (d, _a) in found.items() if addr.upper() == ADDR), None)
    if device is None:
        log("[X] 板子不在广播")
        return None
    log(f"[OK] 在广播 name={device.name!r}")
    return device


async def connect_with_retry(device) -> BleakClient:
    client = BleakClient(device, timeout=8.0)
    for attempt in range(3):
        try:
            await asyncio.wait_for(client.connect(), timeout=10.0)
            log(f"BLE 已连接 MTU={client.mtu_size}")
            return client
        except (OSError, asyncio.TimeoutError) as exc:
            log(f"    connect 重试 {attempt + 1}（{exc}）")
            try:
                await client.disconnect()
            except Exception:
                pass
            await asyncio.sleep(2.0)
    raise RuntimeError("连接失败")


async def read_once(client: BleakClient, label: str):
    try:
        data = await asyncio.wait_for(client.read_gatt_char(MODE_UUID), timeout=10.0)
        log(f"{label}: light_mode = {int.from_bytes(data, 'little', signed=True)}")
        return True
    except (OSError, asyncio.TimeoutError) as exc:
        log(f"{label}: 读失败/超时（{type(exc).__name__}: {exc}）")
        return False


async def test_a() -> bool:
    log("=== Test A：纯 BLE 连续两次读（不碰串口）===")
    device = await scan_device()
    if device is None:
        return False
    client = await connect_with_retry(device)
    try:
        ok1 = await read_once(client, "  读#1")
        await asyncio.sleep(1.0)
        ok2 = await read_once(client, "  读#2")
        result = ok1 and ok2
        log("Test A " + ("PASS —— BLE 本身没问题，问题与串口/模式切换相关" if result else "FAIL —— 第二次读挂，问题在 BLE 侧"))
        return result
    finally:
        try:
            await client.disconnect()
        except Exception:
            pass


async def test_b() -> bool:
    log("=== Test B：写 '5' + 监视串口是否有复位日志 ===")
    device = await scan_device()
    if device is None:
        return False
    client = await connect_with_retry(device)

    port = serial.Serial()
    port.port = PORT
    port.baudrate = 115200
    port.timeout = 0.2
    port.dtr = False
    port.rts = False
    port.open()
    log(f"串口 {PORT} 已打开（不触发复位）")

    serial_log = bytearray()

    async def reader():
        loop = asyncio.get_running_loop()
        while True:
            try:
                data = await loop.run_in_executor(None, port.read, 4096)
            except Exception:
                return
            if data:
                serial_log.extend(data)
                text = data.decode("utf-8", errors="replace").rstrip()
                log(f"  [串口RX] {text[:120]}")

    reader_task = asyncio.create_task(reader())
    try:
        ok1 = await read_once(client, "  写前读")
        port.write(b"5")
        log("  已写 '5'")
        await asyncio.sleep(3.0)
        if serial_log:
            boot = b"rst:0x" in bytes(serial_log) or b"ESP-ROM" in bytes(serial_log)
            log(f"  串口 3 秒内收到 {len(serial_log)} 字节，复位日志={'有!' if boot else '无'}")
            if boot:
                log("  ★ ESP 收到 '5' 后重启了 —— 模式切换导致崩溃（无灯带时跑灯效）")
        else:
            log("  串口 3 秒内无输出（未重启）")
        ok2 = await read_once(client, "  写后读")
        result = ok1 and ok2
        log("Test B " + ("PASS" if result else "FAIL"))
        return result
    finally:
        reader_task.cancel()
        port.close()
        try:
            await client.disconnect()
        except Exception:
            pass


async def main() -> int:
    print("bleak.read_gatt_char 参数:", list(inspect.signature(BleakClient.read_gatt_char).parameters.keys()), flush=True)
    a = await asyncio.wait_for(test_a(), timeout=75.0)
    if a:
        b = await asyncio.wait_for(test_b(), timeout=75.0)
        return 0 if b else 2
    return 1


if __name__ == "__main__":
    try:
        sys.exit(asyncio.run(main()))
    except (asyncio.TimeoutError, TimeoutError):
        log("[FAIL] 实验超时")
        sys.exit(1)
