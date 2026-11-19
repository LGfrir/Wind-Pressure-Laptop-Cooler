"""烧录后健康检查：扫描 + 连接 + 读 light_mode / fan_pwm。"""
import asyncio
import sys

from bleak import BleakClient, BleakScanner

ADDR = "84:C7:BB:56:6A:16"
MODE_UUID = "00000000-0000-6564-6f6d-5f746867696c"
PWM_UUID = "00000000-0000-776d-705f-6e61665f0000"  # fan_pwm（占位，若不存在会报错并跳过）


async def main() -> int:
    print("扫描板子 …", flush=True)
    found = await asyncio.wait_for(BleakScanner.discover(timeout=10.0, return_adv=True), timeout=12.0)
    device = next((d for addr, (d, _a) in found.items() if addr.upper() == ADDR), None)
    if device is None:
        print("[FAIL] 板子不在广播", flush=True)
        return 1
    print(f"在广播：{device.name!r}", flush=True)

    client = BleakClient(device, timeout=10.0)
    for attempt in range(3):
        try:
            await asyncio.wait_for(client.connect(), timeout=12.0)
            break
        except (OSError, asyncio.TimeoutError) as exc:
            print(f"  connect 重试 {attempt + 1}（{exc}）", flush=True)
            try:
                await client.disconnect()
            except Exception:
                pass
            await asyncio.sleep(2.0)
    else:
        print("[FAIL] 连接失败", flush=True)
        return 1

    try:
        data = await asyncio.wait_for(client.read_gatt_char(MODE_UUID), timeout=10.0)
        mode = int.from_bytes(data, "little", signed=True)
        print(f"light_mode = {mode}（0..8 有效）", flush=True)
        ok = 0 <= mode <= 8
        try:
            pwm = await asyncio.wait_for(client.read_gatt_char(PWM_UUID), timeout=10.0)
            print(f"fan_pwm 原始 = {pwm.hex()}", flush=True)
        except Exception as exc:
            print(f"fan_pwm 读取跳过（{type(exc).__name__}）", flush=True)
        print("[PASS] 板子健康：干净固件 + BLE 服务正常" if ok else f"[FAIL] 模式越界 {mode}", flush=True)
        return 0 if ok else 2
    finally:
        try:
            await client.disconnect()
        except Exception:
            pass


if __name__ == "__main__":
    try:
        sys.exit(asyncio.run(asyncio.wait_for(main(), timeout=90.0)))
    except (asyncio.TimeoutError, TimeoutError):
        print("[FAIL] 超时", flush=True)
        sys.exit(1)
