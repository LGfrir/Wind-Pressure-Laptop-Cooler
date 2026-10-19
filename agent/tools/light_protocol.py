"""灯光写入校验，供 BLE 图形工具和命令行工具共用。

@file light_protocol.py
@author LG_frir
@date 2026-10-16
"""

import math
import struct


LIGHT_UPDATE_PERIODS_MS = (10, 20, 50, 100, 200)
LIGHT_CFG_MAX_COLORS = 9


def _require_length(name: str, payload: bytes, expected: int) -> None:
    if len(payload) != expected:
        raise ValueError(f"{name} 需要 {expected} 字节，当前是 {len(payload)} 字节")


def validate_light_payload(name: str, payload: bytes) -> None:
    """非法灯光参数在发送前抛出 ValueError；其他服务由各自协议处理。"""
    if name == "light_update_per":
        _require_length(name, payload, 1)
        if payload[0] not in LIGHT_UPDATE_PERIODS_MS:
            raise ValueError(f"刷新周期只接受 {LIGHT_UPDATE_PERIODS_MS} ms")
    elif name == "light_mode":
        _require_length(name, payload, 4)
        if not 0 <= struct.unpack("<i", payload)[0] <= 8:
            raise ValueError("灯光模式必须是 0..8")
    elif name == "light_brightness":
        _require_length(name, payload, 1)
    elif name == "cfg_lt_static":
        _require_length(name, payload, 3)
    elif name in ("cfg_lt_rainbow", "cfg_lt_breath"):
        _require_length(name, payload, 2 if name == "cfg_lt_rainbow" else 5)
        if struct.unpack_from("<H", payload)[0] == 0:
            raise ValueError("渐变时间不能为 0 ms")
    elif name == "cfg_lt_randflash":
        _require_length(name, payload, 1)
        if payload[0] not in (0, 1):
            raise ValueError("随机闪烁开关只能是 0 或 1")
    elif name == "cfg_lt_blink":
        _require_length(name, payload, 9)
        if payload[0] != 2:
            raise ValueError("闪烁模式需要 2 个颜色")
        if struct.unpack_from("<H", payload, 7)[0] == 0:
            raise ValueError("闪烁间隔不能为 0 ms")
    elif name in ("cfg_lt_custbreat", "cfg_lt_custbreath", "cfg_lt_chase"):
        if not payload or not 1 <= payload[0] <= LIGHT_CFG_MAX_COLORS:
            raise ValueError(f"颜色数量必须是 1..{LIGHT_CFG_MAX_COLORS}")
        count = payload[0]
        custom_breath = name != "cfg_lt_chase"
        _require_length(name, payload, 1 + count * (5 if custom_breath else 3))
        if custom_breath:
            durations = struct.unpack_from(f"<{count}H", payload, 1 + 3 * count)
            if any(duration == 0 for duration in durations):
                raise ValueError("每段渐变时间都必须大于 0 ms")
    elif name == "cfg_lt_round":
        _require_length(name, payload, 22)
        radius, inner_radius, wait_ms, speed = struct.unpack_from("<ffif", payload, 6)
        if not all(math.isfinite(value) for value in (radius, inner_radius, speed)):
            raise ValueError("环绕参数必须是有限数值")
        if radius <= 0 or not 0 <= inner_radius <= radius or wait_ms < 0 or speed < 0:
            raise ValueError("环绕参数需要 range>0、0<=range_main<=range、wait>=0、speed>=0")
