"""BLE 扫描诊断脚本 —— 用 bleak 走 Windows 原生 BLE (WinRT)。

用途：验证 PC 端能不能看到 LaptopCooler 板子，并查看广播包里到底带了什么。
后续 PC 端控制程序大概率也用 bleak，这个脚本可以当起点。

用法:
    python tools/ble_scan.py [扫描秒数]
"""

import asyncio
import sys

from bleak import BleakScanner

# 板子的 public MAC（串口启动日志里的 "Bluetooth MAC"）
TARGET_ADDR = "84:C7:BB:56:6A:16"


async def main() -> None:
    timeout = float(sys.argv[1]) if len(sys.argv) > 1 else 12.0

    print(f"扫描 {timeout:.0f} 秒...\n")
    found = await BleakScanner.discover(timeout=timeout, return_adv=True)

    if not found:
        print("没扫到任何 BLE 设备 —— 电脑蓝牙可能没开，或者没有 BLE 适配器。")
        return

    print(f"共扫到 {len(found)} 个设备\n")
    print(f"{'地址':<20}{'RSSI':>6}  名称")
    print("-" * 70)

    for addr, (dev, adv) in sorted(found.items()):
        rssi = adv.rssi if adv.rssi is not None else 0
        print(f"{addr:<20}{rssi:>6}  {dev.name}")

    print()
    print("=" * 70)

    hit = None
    for addr, pair in found.items():
        if addr.upper() == TARGET_ADDR.upper() or (
            pair[0].name and "yhrdsb" in pair[0].name.lower()
        ):
            hit = (addr, pair)
            break

    if hit is None:
        print(f"!!! 没找到板子 (MAC {TARGET_ADDR} 或名字 yhrdsb) !!!")
        return

    addr, (dev, adv) = hit
    print(f"*** 找到板子: {addr}  name={dev.name}  rssi={adv.rssi} ***")
    print()
    print("广播数据:")
    print(f"  local_name        : {adv.local_name}")
    print(f"  service_uuids     : {adv.service_uuids}")
    print(f"  manufacturer_data : {adv.manufacturer_data}")
    print(f"  tx_power          : {adv.tx_power}")
    print()
    if adv.service_uuids:
        print("  service_uuids 非空 = 广播里带了服务 UUID，客户端可按 UUID 过滤")
    else:
        print("  service_uuids 为空 = 广播包里没有服务 UUID（对应缺 9）")


asyncio.run(main())
