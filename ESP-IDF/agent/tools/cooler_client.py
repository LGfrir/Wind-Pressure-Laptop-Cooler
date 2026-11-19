"""CoolerClient —— 极简 BLE 测试客户端（Python + bleak）

板子联调时用的最小验证工具：扫描 → 连接 → 列服务树 → 读 / 写 / 订阅。
这是调试工具，不是产品代码（产品客户端另建）。

用法:
    python agent/cooler_client.py                 # 扫描 + 连接 + 交互
    python agent/cooler_client.py --scan-only     # 只扫描，看广播
    python agent/cooler_client.py --addr AA:BB:CC:DD:EE:FF
    python agent/cooler_client.py --timeout 20
"""

from __future__ import annotations

import argparse
import asyncio
import struct
import sys

try:
    from .light_protocol import validate_light_payload
except ImportError:
    from light_protocol import validate_light_payload

from bleak import BleakClient, BleakScanner
from bleak.backends.characteristic import BleakGATTCharacteristic

# ============================================================
#  设备标识 —— UUID 变更时【只改这一段】
# ============================================================
#  广播包里带的是 fan_svc 的 UUID。客户端靠它找设备，不要靠设备名
#  （名字改过好几次了：cooler → yhrdsb → 连上尹浩荣jj断两根）
SVC_FAN   = "00000000-0000-0000-0063-76735f6e6166"   # fan_svc
SVC_LIGHT = "00000000-0000-0063-7673-5f746867696c"   # light_svc（不在广播里）

#  服务 UUID -> 好记的名字（只是给服务树标记用）
SVC_NAMES = {
    SVC_FAN.lower():   "fan_svc",
    SVC_LIGHT.lower(): "light_svc",
}

#  特征短别名（能直接用在 r / w / n 命令里）。
#
#  ⚠️ 固件那边这几个名字和实际内容还没对齐，正在改：
#     mode -> 现在读写的其实是【灯】的模式（本该属于 light_svc）
#     pwm  -> 现在读恒返回 0、写什么都不做（假特征，待修）
#     rpm  -> 有 NOTIFY，固件每秒推一次；但 flags 里没有 READ，
#             所以客户端主动 r rpm 会被板子拒（订阅那条路不受影响）
#
#  其它特征不用登记：固件 UUID 的内容就是 ASCII 名字，uuid_name() 会反解出来，
#  所以 light_brightness / cfg_lt_static / laptop_cpu_temp 这些可以直接写名字用。
CHR_ALIASES = {
    # ---- fan_svc ----
    "mode":     "00000000-0000-0000-6564-6f6d5f6e6166",   # fan_mode
    "pwm":      "00000000-0000-0000-006d-77705f6e6166",   # fan_pwm
    "rpm":      "00000000-0000-0000-006d-70725f6e6166",   # fan_rpm   ← 可订阅
    # ---- light_svc ----
    "light":    "00000000-0000-6564-6f6d-5f746867696c",   # light_mode
    "lightcfg": "69666e6f-635f-6564-6f6d-5f746867696c",   # light_mode_config
}

DEFAULT_ADDR = "84:C7:BB:56:6A:16"


# ============================================================
#  小工具
# ============================================================

def hexdump(data) -> str:
    """bytes -> '64 00 12' 这种形式。"""
    return " ".join(f"{b:02X}" for b in data)


def uuid_name(uuid: str) -> str:
    """把固件的自定义 UUID 反解成 ASCII 名字（认不出来返回空串）。

    固件里写的是 BLE_UUID128_INIT(0x66,0x61,...)，内容就是 "fan_pwm" 这种
    ASCII + 补零；显示成 UUID 字符串时字节序是反的，反过来即可。
    这样固件新加的特征不用再往 CHR_ALIASES 里补名字。
    """
    try:
        raw = bytes.fromhex(str(uuid).replace("-", ""))
    except ValueError:
        return ""
    if len(raw) != 16:
        return ""
    text = bytes(reversed(raw)).rstrip(b"\x00")
    if not text:
        return ""
    try:
        name = text.decode("ascii")
    except UnicodeDecodeError:
        return ""
    return name if all(32 <= ord(c) < 127 for c in name) else ""


def describe(data) -> str:
    """按长度解释一包数据。

    ⚠️ 固件里 laptop_* 那 13 个特征发的是 double（小端），8 字节；
    fan_used / fan_speed 是 5 个 double（40 字节）。
    """
    raw = bytes(data)
    n = len(raw)
    parts = []
    if n == 1:
        parts.append(f"u8={raw[0]}")
    elif n == 2:
        parts.append(f"u16={int.from_bytes(raw, 'little')}")
    elif n == 4:
        parts.append(f"u32={int.from_bytes(raw, 'little')}")
        parts.append(f"i32={int.from_bytes(raw, 'little', signed=True)}")
        parts.append(f"f32={struct.unpack('<f', raw)[0]:.6g}")
    elif n == 8:
        parts.append(f"double={struct.unpack('<d', raw)[0]:.6g}")
    elif n == 40:
        parts.append("double[5]=" + "  ".join(f"{v:.6g}" for v in struct.unpack("<5d", raw)))
    if n and all(32 <= b < 127 for b in raw):
        parts.append(f'ascii="{raw.decode("ascii")}"')
    return "  ".join(parts)


def parse_hex(text: str) -> bytes:
    """把用户输入解析成 bytes。

    支持: '64' / '0x64' / '64 00' / '6400' / '64,00'
    """
    cleaned = text.replace("0x", "").replace("0X", "").replace(",", " ").replace("-", " ")
    parts = [p for p in cleaned.split() if p]
    if len(parts) == 1 and len(parts[0]) > 2 and len(parts[0]) % 2 == 0:
        parts = [parts[0][i:i + 2] for i in range(0, len(parts[0]), 2)]
    return bytes(int(p, 16) for p in parts)


def alias_of(uuid: str) -> str:
    for name, val in CHR_ALIASES.items():
        if val.lower() == uuid.lower():
            return name
    return ""


# ============================================================
#  扫描 / 找设备
# ============================================================

async def find_device(addr: str, timeout: float):
    print(f"扫描 {timeout:.0f} 秒 …")
    found = await BleakScanner.discover(timeout=timeout, return_adv=True)
    print(f"扫到 {len(found)} 个 BLE 设备")

    by_svc = None
    by_mac = None
    for a, (dev, adv) in found.items():
        if SVC_FAN.lower() in [u.lower() for u in adv.service_uuids]:
            by_svc = (dev, adv)
        if a.upper() == addr.upper():
            by_mac = (dev, adv)

    if by_svc is not None:
        dev, adv = by_svc
        print(f"[OK] 按【服务 UUID】找到: {dev.address}  name={dev.name}  rssi={adv.rssi}")
        print(f"     服务 UUID : {adv.service_uuids}")
        return dev

    if by_mac is not None:
        dev, adv = by_mac
        print(f"[!] 服务 UUID 没匹配上，但按【MAC】找到: {dev.address}  rssi={adv.rssi}")
        print(f"     它广播的 service_uuids 是: {adv.service_uuids}")
        print("    → 说明广播包里没带服务 UUID（固件侧的缺 9 又回来了？）")
        return dev

    print("[X] 没找到板子")
    print(f"    期望 MAC      : {addr}")
    print(f"    期望服务 UUID : {SVC_FAN}")
    print("    检查: 板子上电了吗？广播里 service_uuids 里有上面这个吗？")
    return None


# ============================================================
#  GATT 服务树
# ============================================================

def print_tree(client: BleakClient) -> list:
    """打印服务树，返回按顺序排好的特征列表（给 r/w 命令当索引）。"""
    chars: list[BleakGATTCharacteristic] = []

    print()
    print("=" * 78)
    print("GATT 服务树")
    print("=" * 78)
    for svc in client.services:
        name = SVC_NAMES.get(svc.uuid.lower())
        mark = f"      <<< {name}" if name else ""
        print(f"\n服务 {svc.uuid}{mark}")
        for ch in svc.characteristics:
            idx = len(chars)
            chars.append(ch)
            alias = alias_of(ch.uuid) or uuid_name(ch.uuid)
            tag = f"  ({alias})" if alias else ""
            print(f"  [{idx}] {ch.uuid}{tag}   [{', '.join(ch.properties)}]")

    print()
    print("命令: r <序号|别名> 读 | w <序号|别名> <hex> 写 | "
          "n <序号|别名> 订阅 | info | l 重列 | q 退出")
    return chars


def resolve(token: str, chars: list):
    """把 '2' / 'pwm' / 完整 UUID 解析成特征。"""
    if token in CHR_ALIASES:
        return CHR_ALIASES[token]

    # 按序号
    try:
        return chars[int(token)]
    except (ValueError, IndexError):
        pass

    # 按完整 UUID
    t = token.strip().lower()
    for ch in chars:
        if ch.uuid.lower() == t:
            return ch

    # 按固件里反解出来的名字（fan_pwm / light_brightness / laptop_cpu_temp ...）
    for ch in chars:
        if uuid_name(ch.uuid).lower() == t:
            return ch

    return None


# ============================================================
#  交互
# ============================================================

async def repl(client: BleakClient) -> None:
    chars = print_tree(client)

    while True:
        try:
            # ⚠️ 必须丢到线程里读，不能用阻塞的 input()：
            #    input() 会把 asyncio 事件循环卡死 → bleak 收不到 BLE 通知。
            line = (await asyncio.to_thread(input, "\n> ")).strip()
        except (EOFError, KeyboardInterrupt):
            print()
            return

        if not line:
            continue

        cmd, *rest = line.split()

        # ---- 退出 / 重列 / 信息 ----
        if cmd in ("q", "quit", "exit"):
            return

        if cmd in ("l", "list"):
            chars = print_tree(client)
            continue

        if cmd == "info":
            print(f"地址     : {client.address}")
            print(f"名称     : {client.name}")
            print(f"MTU      : {client.mtu_size}")
            print(f"已连接   : {client.is_connected}")
            continue

        # ---- 读 ----
        if cmd in ("r", "read"):
            if not rest:
                print("用法: r <序号|别名>")
                continue
            target = resolve(rest[0], chars)
            if target is None:
                print(f"找不到特征: {rest[0]}")
                continue
            try:
                data = await client.read_gatt_char(target)
            except Exception as e:
                print(f"读取失败: {e!r}")
                continue
            print(f"读到 {len(data)} 字节: {hexdump(data)}")
            extra = describe(data)
            if extra:
                print(f"     解释: {extra}")
            continue

        # ---- 写 ----
        if cmd in ("w", "write"):
            if len(rest) < 2:
                print("用法: w <序号|别名> <hex>   例如: w pwm 64")
                continue
            target = resolve(rest[0], chars)
            if target is None:
                print(f"找不到特征: {rest[0]}")
                continue
            try:
                payload = parse_hex(" ".join(rest[1:]))
                validate_light_payload(uuid_name(getattr(target, "uuid", target)), payload)
            except ValueError as exc:
                print(f"写入参数无效: {exc}")
                continue
            try:
                await client.write_gatt_char(target, payload, response=True)
            except Exception as e:
                print(f"写入失败: {e!r}")
                continue
            print(f"写入 {len(payload)} 字节: {hexdump(payload)}")
            continue

        # ---- 订阅 NOTIFY / INDICATE ----
        if cmd in ("n", "notify"):
            if not rest:
                print("用法: n <序号|别名>")
                continue
            target = resolve(rest[0], chars)
            if target is None:
                print(f"找不到特征: {rest[0]}")
                continue
            def on_notify(_c, d) -> None:
                extra = describe(d)
                print(f"\n[通知] {hexdump(d)}")
                if extra:
                    print(f"        解释: {extra}")

            try:
                await client.start_notify(target, on_notify)
            except Exception as e:
                print(f"订阅失败: {e!r}")
                continue
            print("已订阅。通知会直接打印在上面。")
            continue

        print("可用命令: r <序号|别名> | w <序号|别名> <hex> | n <序号|别名> | info | l | q")


# ============================================================
#  入口
# ============================================================

async def main() -> int:
    ap = argparse.ArgumentParser(description="CoolerClient —— 极简 BLE 测试客户端")
    ap.add_argument("--addr", default=DEFAULT_ADDR,
                    help=f"目标 MAC（默认 {DEFAULT_ADDR}）")
    ap.add_argument("--timeout", type=float, default=12.0,
                    help="扫描秒数（默认 12）")
    ap.add_argument("--scan-only", action="store_true",
                    help="只扫描，不连接")
    args = ap.parse_args()

    if args.scan_only:
        await find_device(args.addr, args.timeout)
        return 0

    dev = await find_device(args.addr, args.timeout)
    if dev is None:
        return 1

    print(f"\n连接 {dev.address} …")
    client = BleakClient(dev)
    try:
        await client.connect()
        if not client.is_connected:
            print("[X] 连接失败")
            return 1
        print(f"[OK] 已连接    MTU={client.mtu_size}")

        await repl(client)
    except Exception as e:
        print(f"[X] 出错了: {e!r}")
        return 1
    finally:
        if client.is_connected:
            await client.disconnect()
            print("已断开")

    return 0


if __name__ == "__main__":
    try:
        sys.exit(asyncio.run(main()))
    except KeyboardInterrupt:
        print()
        sys.exit(130)
