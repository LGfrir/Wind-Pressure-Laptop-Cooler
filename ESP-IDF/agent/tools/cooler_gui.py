"""CoolerGUI —— LaptopCooler 板子联调用的 BLE 图形化测试工具

依赖
----
* bleak    BLE 库（Windows 上走 WinRT）：  pip install bleak
* tkinter  Python 自带，不用装

用法
----
    python agent/tools/cooler_gui.py
    python agent/tools/cooler_gui.py --addr 84:C7:BB:56:6A:16 --timeout 10

界面怎么用
----------
1. 「扫描设备」→ 列表里挑（双击/单击自动填 MAC）→「连接」；
   带 fan_svc 的板子会高亮成绿色。
2. 左边服务树里点一个特征，右边就能「读 / 写 / 订阅」。
3. 下面的「快捷测试」按服务分了三页（风扇 / 灯光 / 电脑信息），
   常用操作都做成按钮，不用手敲 UUID 和十六进制。
4. 所有收发都打在日志里：十六进制 + 自动按长度解释（u8/u16/u32/double）。

为什么开后台线程跑 asyncio
--------------------------
bleak 的 API 全是 async，而 tkinter 只能在主线程里碰。所以：
* 主线程     = tkinter 事件循环
* 后台线程   = asyncio 事件循环（AsyncBridge）
* 界面 → BLE ：bridge.submit(协程) 丢进后台 loop
* BLE  → 界面：一律不直接碰控件，只 self.post(fn, ...) 塞进队列，
  主线程每 50ms 取出来执行（_drain()）

关于 UUID 名字
-------------
固件里的自定义 UUID 是 BLE_UUID128_INIT 的字节数组，内容就是 ASCII 名字
（"fan_pwm" / "laptop_cpu_usage" / "cfg_lt_static" …），只是显示时字节序被
反过来了。uuid_name() 反过来就能拿到可读名字 —— 所以这个工具不维护 UUID
对照表，固件改名了它也能自动认出来。
"""

from __future__ import annotations

import argparse
import asyncio
import queue
import struct
import threading
import time
import tkinter as tk
from tkinter import filedialog, messagebox, scrolledtext, ttk

try:
    from .light_protocol import LIGHT_UPDATE_PERIODS_MS, validate_light_payload
except ImportError:
    from light_protocol import LIGHT_UPDATE_PERIODS_MS, validate_light_payload

try:
    from bleak import BleakClient, BleakScanner
    from bleak.backends.characteristic import BleakGATTCharacteristic
except ImportError as exc:  # 没装 bleak 时给个能看懂的提示，而不是一串 traceback
    _root = tk.Tk()
    _root.withdraw()
    messagebox.showerror(
        "缺少依赖",
        f"没装 bleak：{exc}\n\n请在命令行执行：\n    pip install bleak",
    )
    raise SystemExit(1)


# ============================================================
#  常量
# ============================================================

# 板子的 public MAC（串口启动日志里的 "Bluetooth MAC"），只是默认值，可以改
DEFAULT_ADDR = "84:C7:BB:56:6A:16"

# 广播里带的服务 UUID —— 客户端靠它找设备，不要靠设备名（名字改过好几次）
SVC_FAN = "00000000-0000-0000-0063-76735f6e6166"  # fan_svc

LIGHT_MODE_NAMES = {
    0: "OFF",
    1: "STATIC",
    2: "RAINBOW",
    3: "BREATH",
    4: "RANDOM_FLASH",
    5: "BLINK",
    6: "CUSTOM_BREATH",
    7: "CHASE",
    8: "ROUND",
}
FAN_MODE_NAMES = {0: "MANUAL", 1: "AUTO", 2: "FULL_SPEED"}

# 电脑信息服务的 13 个特征。
# 名字就是从固件 UUID 反解出来的（UUID 只有 16 字节，所以有几个被截断了：
# laptop_cpu_volta / laptop_gpu_tot_r 不是笔误）。
LAPTOP_FIELDS = [
    ("laptop_cpu_usage", "CPU 占用 %"),
    ("laptop_cpu_temp", "CPU 温度"),
    ("laptop_cpu_volta", "CPU 电压"),
    ("laptop_cpu_clock", "CPU 频率"),
    ("laptop_gpu_usage", "GPU 占用 %"),
    ("laptop_gpu_temp", "GPU 温度"),
    ("laptop_gpu_clock", "GPU 频率"),
    ("laptop_gpu_ram", "显存占用"),
    ("laptop_gpu_tot_r", "显存总量"),
    ("laptop_ram", "内存占用"),
    ("laptop_tot_ram", "内存总量"),
    ("laptop_fan_used", "风扇占用 x5"),
    ("laptop_fan_speed", "风扇转速 x5"),
]

# 灌测试数据用的一组值（按 LAPTOP_FIELDS 的顺序）
LAPTOP_FAKE_VALUES = [
    37.5,
    61.0,
    1.25,
    3600.0,
    22.5,
    48.0,
    1200.0,
    3072.0,
    8192.0,
    45.0,
    16384.0,
    30.0,
    2400.0,
]

# 写 PWM / 亮度时的快捷值
QUICK_VALUES = ["0", "25", "50", "75", "100"]

# 灯效配置（config_svc）的载荷格式，跟固件里 config_svc_callback 的注释一致
CFG_FORMAT_HINT = (
    "cfg_lt_static      [r][g][b]                    3B\n"
    "cfg_lt_rainbow     [switch_ms u16]              2B\n"
    "cfg_lt_breath      [switch_ms u16][r][g][b]     5B\n"
    "cfg_lt_randflash   [mode u8]                    1B\n"
    "cfg_lt_blink       [size=2][rgb]x2[period u16]  9B\n"
    "cfg_lt_custbreat   [size][rgb]xN[fades u16]xN   1+5N"
    "   （UUID 只有 16B，名字被截了）\n"
    "cfg_lt_chase       [size][rgb]xN                1+3N\n"
    "cfg_lt_round       [main rgb][sub rgb][range f32]\n"
    "                   [range_main f32][wait_ms i32][speed f32]  22B\n"
    "（多字节一律小端；所有渐变时间和闪烁间隔必须大于 0 ms）"
)


# ============================================================
#  小工具
# ============================================================


def uuid_name(uuid: str) -> str:
    """把固件的自定义 UUID 反解成 ASCII 名字，认不出来就返回空串。

    固件里写的是 BLE_UUID128_INIT(0x66,0x61,...)，也就是 "fan_pwm" 这样的
    ASCII + 补零；显示成 UUID 字符串时字节序是反的，所以这里反过来。
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


def hexdump(data: bytes) -> str:
    """bytes -> '64 00 12' 这种形式。"""
    return " ".join(f"{b:02X}" for b in data)


def parse_bytes(text: str, decimal: bool = False) -> bytes:
    """把用户输入解析成 bytes。

    decimal=False: 十六进制。支持 '64' / '0x64' / '64 00' / '6400' / '64,00'
    decimal=True : 十进制。支持 '100' / '100 0 255' / '100,0,255'
    """
    cleaned = (
        text.replace("0x", " ")
        .replace("0X", " ")
        .replace(",", " ")
        .replace("-", " ")
        .replace(":", " ")
    )
    parts = [p for p in cleaned.split() if p]
    if not parts:
        raise ValueError("写入内容是空的")

    try:
        if decimal:
            vals = [int(p, 10) for p in parts]
        else:
            if len(parts) == 1 and len(parts[0]) > 2 and len(parts[0]) % 2 == 0:
                parts = [parts[0][i : i + 2] for i in range(0, len(parts[0]), 2)]
            vals = [int(p, 16) for p in parts]
    except ValueError as exc:
        kind = "十进制" if decimal else "十六进制"
        raise ValueError(f"{kind}解析失败：{exc}") from None

    for v in vals:
        if not 0 <= v <= 255:
            raise ValueError(f"{v} 不是 1 个字节（0..255）")
    return bytes(vals)


def describe(data: bytes) -> str:
    """按长度把一包数据解释成人能看的数 —— 电脑信息服务发的是 double。"""
    n = len(data)
    parts = []
    if n == 1:
        parts.append(f"u8={data[0]}")
    elif n == 2:
        parts.append(f"u16={int.from_bytes(data, 'little')}")
    elif n == 4:
        parts.append(f"u32={int.from_bytes(data, 'little')}")
        parts.append(f"i32={int.from_bytes(data, 'little', signed=True)}")
        parts.append(f"f32={struct.unpack('<f', data)[0]:.6g}")
    elif n == 8:
        parts.append(f"u64={int.from_bytes(data, 'little')}")
        parts.append(f"double={struct.unpack('<d', data)[0]:.6g}")
    elif n == 40:
        parts.append(
            "double[5]=" + "  ".join(f"{v:.6g}" for v in struct.unpack("<5d", data))
        )
    if n and all(32 <= b < 127 for b in data):
        parts.append(f'ascii="{data.decode("ascii")}"')
    return "   ".join(parts)


def fmt_number(data: bytes) -> str:
    """电脑信息那几个特征专用：8 字节 = 1 个 double，40 字节 = 5 个 double。"""
    if len(data) == 8:
        return f"{struct.unpack('<d', data)[0]:.4g}"
    if len(data) == 40:
        return "  ".join(f"{v:.4g}" for v in struct.unpack("<5d", data))
    return hexdump(data)


def mode_names(mapping: dict) -> list[str]:
    """{'0': 'MANUAL', ...} 这种给 Combobox 用的字符串列表。"""
    return [f"{k} = {v}" for k, v in mapping.items()]


def mode_value(text: str, mapping: dict) -> int:
    """把 Combobox 选中的 '1 = AUTO' 解析回 1。"""
    head = text.split("=")[0].strip()
    value = int(head)
    if value not in mapping:
        raise ValueError(f"{value} 不是合法取值 {list(mapping)}")
    return value


# ============================================================
#  后台 asyncio 线程
# ============================================================


class AsyncBridge:
    """单独一个线程跑 asyncio 事件循环，专门伺候 bleak。"""

    def __init__(self) -> None:
        self.loop = asyncio.new_event_loop()
        self._thread = threading.Thread(target=self._run, name="ble-loop", daemon=True)
        self._thread.start()

    def _run(self) -> None:
        asyncio.set_event_loop(self.loop)
        self.loop.run_forever()

    def submit(self, coro):
        """把协程丢进后台 loop，返回 concurrent.futures.Future。"""
        return asyncio.run_coroutine_threadsafe(coro, self.loop)

    def stop(self) -> None:
        self.loop.call_soon_threadsafe(self.loop.stop)


# ============================================================
#  界面
# ============================================================


class App(tk.Tk):
    def __init__(self, default_addr: str, default_timeout: float) -> None:
        super().__init__()
        self.title("CoolerGUI —— LaptopCooler BLE 测试工具")
        self.geometry("1200x860")
        self.minsize(1000, 700)

        self.bridge = AsyncBridge()
        self.events: queue.Queue = queue.Queue()
        self.client: BleakClient | None = None
        self.chars: dict[str, BleakGATTCharacteristic] = {}  # uuid(小写) -> 特征对象
        self.cur_uuid: str | None = None
        self.poll_on = False  # 给后台线程读的普通变量
        self.laptop_labels: dict[str, ttk.Label] = {}
        self.laptop_entries: dict[str, ttk.Entry] = {}

        self._build_scan_panel(default_addr, default_timeout)
        self._build_main_panes()
        self._build_quick_panel()
        self._build_log_panel()

        self.protocol("WM_DELETE_WINDOW", self._on_close)
        self.after(50, self._drain)
        self.log("就绪。点「扫描设备」，或者直接填 MAC 再点「连接」。")

    # ---------------- 界面：四个区域 ----------------

    def _build_scan_panel(self, default_addr: str, default_timeout: float) -> None:
        box = ttk.LabelFrame(self, text="1. 扫描 / 连接")
        box.pack(fill="x", padx=8, pady=(8, 4))

        row = ttk.Frame(box)
        row.pack(fill="x", padx=6, pady=6)

        ttk.Label(row, text="扫描秒数").pack(side="left")
        self.var_timeout = tk.DoubleVar(value=default_timeout)
        ttk.Spinbox(
            row, from_=2, to=60, increment=1, width=5, textvariable=self.var_timeout
        ).pack(side="left", padx=(4, 10))

        ttk.Button(row, text="扫描设备", command=self.on_scan).pack(side="left")

        self.var_only_board = tk.BooleanVar(value=False)
        ttk.Checkbutton(
            row, text="列表里只留带 fan_svc 的", variable=self.var_only_board
        ).pack(side="left", padx=8)

        ttk.Label(row, text="MAC").pack(side="left")
        self.var_addr = tk.StringVar(value=default_addr)
        ttk.Entry(row, textvariable=self.var_addr, width=20).pack(side="left", padx=4)

        ttk.Button(row, text="连接", command=self.on_connect).pack(side="left")
        ttk.Button(row, text="断开", command=self.on_disconnect).pack(
            side="left", padx=4
        )

        self.lbl_state = ttk.Label(row, text="未连接", foreground="#a00")
        self.lbl_state.pack(side="left", padx=10)

        cols = ("name", "addr", "rssi", "svc")
        self.tree_dev = ttk.Treeview(box, columns=cols, show="headings", height=5)
        for col, title, width in (
            ("name", "名称", 210),
            ("addr", "地址", 150),
            ("rssi", "RSSI", 60),
            ("svc", "广播里的服务 UUID", 560),
        ):
            self.tree_dev.heading(col, text=title)
            self.tree_dev.column(col, width=width, anchor="w")
        self.tree_dev.pack(fill="x", padx=6, pady=(0, 6))
        self.tree_dev.tag_configure("board", background="#dff5df")
        self.tree_dev.bind("<<TreeviewSelect>>", self.on_pick_device)

    def _build_main_panes(self) -> None:
        pane = ttk.Panedwindow(self, orient="horizontal")
        pane.pack(fill="both", expand=True, padx=8, pady=4)

        # ---- 左：服务树 ----
        left = ttk.LabelFrame(pane, text="2. GATT 服务树")
        self.tree_gatt = ttk.Treeview(
            left, columns=("uuid", "props"), show="tree headings"
        )
        self.tree_gatt.heading("#0", text="服务 / 特征")
        self.tree_gatt.column("#0", width=210, anchor="w")
        self.tree_gatt.heading("uuid", text="UUID")
        self.tree_gatt.column("uuid", width=300, anchor="w")
        self.tree_gatt.heading("props", text="属性")
        self.tree_gatt.column("props", width=130, anchor="w")
        self.tree_gatt.pack(fill="both", expand=True, padx=4, pady=4)
        self.tree_gatt.bind("<<TreeviewSelect>>", self.on_pick_char)
        pane.add(left, weight=3)

        # ---- 右：读 / 写 / 订阅 ----
        right = ttk.LabelFrame(pane, text="3. 读 / 写 / 订阅")

        self.lbl_cur = ttk.Label(right, text="（还没选特征）", foreground="#036")
        self.lbl_cur.pack(anchor="w", padx=6, pady=(6, 2))

        row1 = ttk.Frame(right)
        row1.pack(fill="x", padx=6, pady=4)
        ttk.Button(row1, text="读一次", command=self.on_read).pack(side="left")
        ttk.Button(row1, text="订阅 NOTIFY", command=lambda: self.on_notify(True)).pack(
            side="left", padx=4
        )
        ttk.Button(row1, text="取消订阅", command=lambda: self.on_notify(False)).pack(
            side="left"
        )
        self.var_poll = tk.BooleanVar(value=False)
        ttk.Checkbutton(
            row1, text="自动读", variable=self.var_poll, command=self.on_poll_toggle
        ).pack(side="left", padx=(12, 2))
        self.var_poll_sec = tk.DoubleVar(value=2.0)
        ttk.Spinbox(
            row1,
            from_=0.2,
            to=60,
            increment=0.5,
            width=5,
            textvariable=self.var_poll_sec,
        ).pack(side="left")
        ttk.Label(row1, text="秒").pack(side="left")

        row2 = ttk.Frame(right)
        row2.pack(fill="x", padx=6, pady=4)
        ttk.Label(row2, text="写入").pack(side="left")
        self.var_write = tk.StringVar(value="")
        ttk.Entry(row2, textvariable=self.var_write, width=26).pack(side="left", padx=4)
        self.var_dec = tk.BooleanVar(value=False)
        ttk.Checkbutton(row2, text="十进制", variable=self.var_dec).pack(side="left")
        ttk.Button(row2, text="写", command=self.on_write).pack(side="left", padx=4)

        row3 = ttk.Frame(right)
        row3.pack(fill="x", padx=6, pady=(0, 6))
        ttk.Label(row3, text="快捷值").pack(side="left")
        for value in QUICK_VALUES:
            ttk.Button(
                row3,
                text=value,
                width=4,
                command=lambda v=value: self.on_write_quick(v),
            ).pack(side="left", padx=2)

        # 十六进制/十进制怎么填，写清楚省得每次试
        ttk.Label(
            right,
            foreground="#666",
            justify="left",
            text=(
                "十六进制：64 / 0x64 / 64 00 / 6400\n"
                "十进制：100 或勾上「十进制」后写 100\n"
                "（长度不对固件会直接拒，日志里能看到错误码）"
            ),
        ).pack(anchor="w", padx=6, pady=(0, 6))

        pane.add(right, weight=2)

    def _build_quick_panel(self) -> None:
        nb = ttk.Notebook(self)
        nb.pack(fill="x", padx=8, pady=4)

        fan_tab = ttk.Frame(nb)
        nb.add(fan_tab, text="风扇")
        self._build_fan_tab(fan_tab)

        light_tab = ttk.Frame(nb)
        nb.add(light_tab, text="灯光")
        self._build_light_tab(light_tab)

        laptop_tab = ttk.Frame(nb)
        nb.add(laptop_tab, text="电脑信息")
        self._build_laptop_tab(laptop_tab)

    def _build_fan_tab(self, tab: ttk.Frame) -> None:
        row = ttk.Frame(tab)
        row.pack(fill="x", padx=6, pady=6)

        ttk.Label(row, text="模式").pack(side="left")
        self.var_fan_mode = tk.StringVar(value=mode_names(FAN_MODE_NAMES)[0])
        ttk.Combobox(
            row,
            textvariable=self.var_fan_mode,
            width=16,
            state="readonly",
            values=mode_names(FAN_MODE_NAMES),
        ).pack(side="left", padx=4)
        ttk.Button(row, text="读模式", command=lambda: self.q_read("fan_mode")).pack(
            side="left"
        )
        ttk.Button(row, text="写模式", command=self.q_write_fan_mode).pack(
            side="left", padx=4
        )

        ttk.Label(row, text="    PWM").pack(side="left")
        self.var_fan_pwm = tk.IntVar(value=50)
        ttk.Scale(
            row,
            from_=0,
            to=100,
            variable=self.var_fan_pwm,
            orient="horizontal",
            length=160,
        ).pack(side="left", padx=4)
        self.lbl_fan_pwm = ttk.Label(row, textvariable=self.var_fan_pwm, width=4)
        self.lbl_fan_pwm.pack(side="left")
        ttk.Button(row, text="读 PWM", command=lambda: self.q_read("fan_pwm")).pack(
            side="left"
        )
        ttk.Button(row, text="写 PWM", command=self.q_write_fan_pwm).pack(
            side="left", padx=4
        )

        row2 = ttk.Frame(tab)
        row2.pack(fill="x", padx=6, pady=(0, 6))
        ttk.Label(row2, text="转速").pack(side="left")
        ttk.Button(row2, text="读转速", command=lambda: self.q_read("fan_rpm")).pack(
            side="left", padx=4
        )
        ttk.Button(
            row2,
            text="订阅转速（每秒推一次）",
            command=lambda: self.q_notify("fan_rpm", True),
        ).pack(side="left")
        ttk.Button(
            row2, text="取消订阅", command=lambda: self.q_notify("fan_rpm", False)
        ).pack(side="left", padx=4)
        ttk.Label(
            row2,
            foreground="#666",
            text="注：固件里 fan_get_rpm() 目前是 rand()%100 的调试桩",
        ).pack(side="left", padx=10)

    def _build_light_tab(self, tab: ttk.Frame) -> None:
        row = ttk.Frame(tab)
        row.pack(fill="x", padx=6, pady=6)

        ttk.Label(row, text="灯效模式").pack(side="left")
        self.var_light_mode = tk.StringVar(value=mode_names(LIGHT_MODE_NAMES)[0])
        ttk.Combobox(
            row,
            textvariable=self.var_light_mode,
            width=18,
            state="readonly",
            values=mode_names(LIGHT_MODE_NAMES),
        ).pack(side="left", padx=4)
        ttk.Button(row, text="读模式", command=lambda: self.q_read("light_mode")).pack(
            side="left"
        )
        ttk.Button(row, text="写模式", command=self.q_write_light_mode).pack(
            side="left", padx=4
        )

        ttk.Label(row, text="    亮度").pack(side="left")
        self.var_brightness = tk.IntVar(value=128)
        ttk.Scale(
            row,
            from_=0,
            to=255,
            variable=self.var_brightness,
            orient="horizontal",
            length=150,
        ).pack(side="left", padx=4)
        ttk.Label(row, textvariable=self.var_brightness, width=4).pack(side="left")
        ttk.Button(
            row, text="读亮度", command=lambda: self.q_read("light_brightness")
        ).pack(side="left")
        ttk.Button(row, text="写亮度", command=self.q_write_brightness).pack(
            side="left", padx=4
        )

        row2 = ttk.Frame(tab)
        row2.pack(fill="x", padx=6, pady=4)
        ttk.Label(row2, text="刷新周期 ms").pack(side="left")
        self.var_period = tk.IntVar(value=10)
        ttk.Combobox(row2, values=LIGHT_UPDATE_PERIODS_MS, state="readonly",
                     width=6, textvariable=self.var_period).pack(
            side="left", padx=4
        )
        ttk.Button(
            row2, text="读周期", command=lambda: self.q_read("light_update_per")
        ).pack(side="left")
        ttk.Button(row2, text="写周期", command=self.q_write_period).pack(
            side="left", padx=4
        )
        ttk.Label(
            row2, foreground="#666", text="10 / 20 / 50 / 100 / 200 ms；周期不落盘"
        ).pack(side="left", padx=10)

        row3 = ttk.Frame(tab)
        row3.pack(fill="x", padx=6, pady=(0, 6))
        ttk.Label(row3, text="灯效配置").pack(side="left")
        cfg_names = [
            "cfg_lt_static",
            "cfg_lt_rainbow",
            "cfg_lt_breath",
            "cfg_lt_randflash",
            "cfg_lt_blink",
            "cfg_lt_custbreat",
            "cfg_lt_chase",
            "cfg_lt_round",
        ]
        self.var_cfg_name = tk.StringVar(value=cfg_names[0])
        ttk.Combobox(
            row3,
            textvariable=self.var_cfg_name,
            width=18,
            state="readonly",
            values=cfg_names,
        ).pack(side="left", padx=4)
        ttk.Button(
            row3, text="读配置", command=lambda: self.q_read(self.var_cfg_name.get())
        ).pack(side="left")
        ttk.Button(
            row3,
            text="用上面的「写入」框写配置",
            command=lambda: self.q_write(self.var_cfg_name.get()),
        ).pack(side="left", padx=4)

        hint = ttk.Label(tab, foreground="#666", justify="left", text=CFG_FORMAT_HINT)
        hint.pack(anchor="w", padx=6, pady=(0, 6))

    def _build_laptop_tab(self, tab: ttk.Frame) -> None:
        top = ttk.Frame(tab)
        top.pack(fill="x", padx=6, pady=6)
        ttk.Button(top, text="一键读全部", command=self.q_read_all_laptop).pack(
            side="left"
        )
        ttk.Button(top, text="全部灌一组测试值", command=self.q_fake_all_laptop).pack(
            side="left", padx=6
        )
        ttk.Label(
            top,
            foreground="#666",
            text="这些特征是 PC 端往上写的（double，小端），板子只做缓存",
        ).pack(side="left", padx=10)

        grid = ttk.Frame(tab)
        grid.pack(fill="x", padx=6, pady=(0, 8))
        for col, title in enumerate(
            ("字段", "说明", "当前值", "", "写入值（double）", "")
        ):
            ttk.Label(grid, text=title, foreground="#036").grid(
                row=0, column=col, sticky="w", padx=4
            )

        for i, (name, desc) in enumerate(LAPTOP_FIELDS, start=1):
            ttk.Label(grid, text=name).grid(row=i, column=0, sticky="w", padx=4)
            ttk.Label(grid, text=desc).grid(row=i, column=1, sticky="w", padx=4)

            lbl = ttk.Label(grid, text="—", width=26, foreground="#063")
            lbl.grid(row=i, column=2, sticky="w", padx=4)
            self.laptop_labels[name] = lbl

            ttk.Button(
                grid, text="读", width=4, command=lambda n=name: self.q_read(n)
            ).grid(row=i, column=3, padx=2)

            entry = ttk.Entry(grid, width=14)
            entry.insert(0, f"{LAPTOP_FAKE_VALUES[i - 1]:g}")
            entry.grid(row=i, column=4, padx=4)
            self.laptop_entries[name] = entry

            ttk.Button(
                grid, text="写", width=4, command=lambda n=name: self.q_write_laptop(n)
            ).grid(row=i, column=5, padx=2)

    def _build_log_panel(self) -> None:
        box = ttk.LabelFrame(self, text="4. 日志")
        box.pack(fill="both", expand=True, padx=8, pady=(4, 8))

        self.txt_log = scrolledtext.ScrolledText(
            box, height=12, wrap="none", font=("Consolas", 9)
        )
        self.txt_log.pack(fill="both", expand=True, padx=6, pady=(6, 2))
        self.txt_log.configure(state="disabled")

        row = ttk.Frame(box)
        row.pack(fill="x", padx=6, pady=(0, 6))
        ttk.Button(row, text="清空", command=self.on_clear_log).pack(side="left")
        ttk.Button(row, text="保存到文件", command=self.on_save_log).pack(
            side="left", padx=4
        )

    # ---------------- 线程桥：日志 / 队列 ----------------

    def log(self, msg: str) -> None:
        stamp = time.strftime("%H:%M:%S")
        self.txt_log.configure(state="normal")
        self.txt_log.insert("end", f"[{stamp}] {msg}\n")
        self.txt_log.see("end")
        self.txt_log.configure(state="disabled")

    def post(self, fn, *args) -> None:
        """后台线程 → 主线程的唯一通道：只塞队列，不碰控件。"""
        self.events.put((fn, args))

    def _drain(self) -> None:
        while True:
            try:
                fn, args = self.events.get_nowait()
            except queue.Empty:
                break
            try:
                fn(*args)
            except Exception as exc:  # 界面回调自己出错也不能把界面打死
                self.log(f"[X] 界面回调出错: {exc!r}")
        self.after(50, self._drain)

    def spawn(self, coro, what: str) -> None:
        """把协程丢给后台 loop，异常统一打到日志（不让它静默消失）。"""
        fut = self.bridge.submit(coro)

        def _done(f):
            exc = f.exception()
            if exc is not None:
                self.post(self.log, f"[X] {what} 失败: {exc!r}")

        fut.add_done_callback(_done)

    # ---------------- 扫描 / 连接 ----------------

    def on_scan(self) -> None:
        if self.client is not None and self.client.is_connected:
            self.log("已经连上了，扫描前先「断开」")
            return
        timeout = float(self.var_timeout.get())
        self.log(f"扫描 {timeout:.0f} 秒 …")
        self.spawn(self._scan(timeout), "扫描")

    async def _scan(self, timeout: float) -> None:
        found = await BleakScanner.discover(timeout=timeout, return_adv=True)
        rows = []
        for addr, (dev, adv) in found.items():
            uuids = [u.lower() for u in (adv.service_uuids or [])]
            rows.append(
                (
                    dev.name or "?",
                    addr,
                    adv.rssi if adv.rssi is not None else 0,
                    ", ".join(uuids),
                    SVC_FAN.lower() in uuids,
                )
            )
        # 板子排最前，其次信号强的排前面
        rows.sort(key=lambda r: (not r[4], -r[2]))
        boards = sum(1 for r in rows if r[4])
        self.post(self._fill_devices, rows)
        self.post(
            self.log, f"扫到 {len(rows)} 个 BLE 设备，其中 {boards} 个广播里有 fan_svc"
        )

    def _fill_devices(self, rows) -> None:
        only = bool(self.var_only_board.get())
        self.tree_dev.delete(*self.tree_dev.get_children())
        for name, addr, rssi, uuids, is_board in rows:
            if only and not is_board:
                continue
            self.tree_dev.insert(
                "",
                "end",
                iid=addr,
                values=(name, addr, rssi, uuids),
                tags=("board" if is_board else "other",),
            )
        if not self.tree_dev.get_children():
            self.log("列表是空的：勾了「只看 fan_svc」但没扫到板子？")

    def on_pick_device(self, _event=None) -> None:
        sel = self.tree_dev.selection()
        if sel:
            self.var_addr.set(sel[0])

    def on_connect(self) -> None:
        addr = self.var_addr.get().strip()
        if not addr:
            messagebox.showwarning("提示", "先扫描选一个设备，或者直接填 MAC")
            return
        if self.client is not None:
            self.log("已经连上了，先「断开」")
            return
        self.log(f"连接 {addr} …")
        self.spawn(self._connect(addr), "连接")

    async def _connect(self, addr: str) -> None:
        client = BleakClient(addr, disconnected_callback=self._on_disconnected)
        await asyncio.wait_for(client.connect(), timeout=20.0)
        self.client = client
        mtu = getattr(client, "mtu_size", 0)
        self.post(self._on_connected, client.address, mtu)

    def _on_connected(self, addr: str, mtu: int) -> None:
        self.lbl_state.configure(text=f"已连接 {addr}  MTU={mtu}", foreground="#070")
        self.log(f"[OK] 已连接 {addr}，MTU={mtu}")
        self.refresh_tree()

    def on_disconnect(self) -> None:
        if self.client is None:
            self.log("本来就没连接")
            return
        self.poll_on = False
        self.var_poll.set(False)
        self.spawn(self._disconnect(), "断开")

    async def _disconnect(self) -> None:
        client, self.client = self.client, None
        if client is not None:
            await client.disconnect()

    def _on_disconnected(self, _client) -> None:
        """bleak 在后台线程回调这里（也可能是对端掉线）。"""
        self.client = None
        self.poll_on = False
        self.post(self._after_disconnect)

    def _after_disconnect(self) -> None:
        self.lbl_state.configure(text="未连接", foreground="#a00")
        self.var_poll.set(False)
        self.tree_gatt.delete(*self.tree_gatt.get_children())
        self.chars.clear()
        self.cur_uuid = None
        self.lbl_cur.configure(text="（还没选特征）")
        self.log("[!] 连接已断开")

    # ---------------- 服务树 ----------------

    def refresh_tree(self) -> None:
        if self.client is None:
            return
        services = list(self.client.services)
        self.tree_gatt.delete(*self.tree_gatt.get_children())
        self.chars.clear()
        for svc in services:
            sname = uuid_name(svc.uuid) or "(标准服务)"
            node = self.tree_gatt.insert(
                "", "end", text=sname, open=True, values=(str(svc.uuid), "service")
            )
            for ch in svc.characteristics:
                key = str(ch.uuid).lower()
                self.chars[key] = ch
                cname = uuid_name(ch.uuid) or "(标准特征)"
                self.tree_gatt.insert(
                    node,
                    "end",
                    iid=key,
                    text=f"  {cname}",
                    values=(str(ch.uuid), ",".join(ch.properties)),
                )
        self.log(f"服务树：{len(services)} 个服务 / {len(self.chars)} 个特征")

    def on_pick_char(self, _event=None) -> None:
        sel = self.tree_gatt.selection()
        if not sel or sel[0] not in self.chars:
            return  # 选到的是服务节点
        self.cur_uuid = sel[0]
        ch = self.chars[sel[0]]
        name = uuid_name(ch.uuid) or str(ch.uuid)
        self.lbl_cur.configure(text=f"{name}   [{','.join(ch.properties)}]")

    def _cur_char(self) -> BleakGATTCharacteristic | None:
        if self.client is None or not self.client.is_connected:
            self.log("[X] 还没连接")
            return None
        ch = self.chars.get(self.cur_uuid) if self.cur_uuid else None
        if ch is None:
            self.log("[X] 先在左边服务树里点一个特征")
            return None
        return ch

    # ---------------- 读 / 写 / 订阅 ----------------

    def on_read(self) -> None:
        ch = self._cur_char()
        if ch is not None:
            self.spawn(self._read(ch), f"读 {uuid_name(ch.uuid)}")

    def _connected_client(self) -> BleakClient:
        client = self.client
        if client is None or not client.is_connected:
            raise RuntimeError("还没连接或连接已断开")
        return client

    async def _read(self, ch: BleakGATTCharacteristic) -> None:
        client = self._connected_client()
        data = await asyncio.wait_for(client.read_gatt_char(ch), timeout=10.0)
        self.post(self._show_value, "读", ch.uuid, bytes(data))

    async def _write_async(self, ch: BleakGATTCharacteristic, payload: bytes) -> None:
        validate_light_payload(uuid_name(ch.uuid), payload)
        client = self._connected_client()
        await asyncio.wait_for(
            client.write_gatt_char(ch, payload, response=True), timeout=10.0
        )
        self.post(self._show_value, "写", ch.uuid, payload)

    def on_write(self) -> None:
        ch = self._cur_char()
        if ch is None:
            return
        self._write_text(ch, self.var_write.get(), bool(self.var_dec.get()))

    def on_write_quick(self, text: str) -> None:
        ch = self._cur_char()
        if ch is None:
            return
        # 快捷值是 0/25/50/75/100 这种十进制数，但 0..100 的十六进制写法
        # 对 u8 特征来说含义不同，所以这里按十进制解析
        self._write_text(ch, text, decimal=True)

    def _write_text(
        self, ch: BleakGATTCharacteristic, text: str, decimal: bool
    ) -> None:
        try:
            payload = parse_bytes(text, decimal=decimal)
        except ValueError as exc:
            self.log(f"[X] 写入内容有问题：{exc}")
            return
        self.spawn(self._write_async(ch, payload), f"写 {uuid_name(ch.uuid)}")

    def on_notify(self, on: bool) -> None:
        ch = self._cur_char()
        if ch is not None:
            self.spawn(self._notify(ch, on), "订阅" if on else "取消订阅")

    async def _notify(self, ch: BleakGATTCharacteristic, on: bool) -> None:
        client = self._connected_client()
        if on:
            await client.start_notify(ch, self._on_notify_data)
            self.post(self.log, f"已订阅 {uuid_name(ch.uuid)}")
        else:
            await client.stop_notify(ch)
            self.post(self.log, f"已取消订阅 {uuid_name(ch.uuid)}")

    def _on_notify_data(self, ch: BleakGATTCharacteristic, data: bytearray) -> None:
        """bleak 的通知回调（后台线程），只往队列里塞。"""
        self.post(self._show_value, "通知", ch.uuid, bytes(data))

    def on_poll_toggle(self) -> None:
        self.poll_on = bool(self.var_poll.get())
        if not self.poll_on:
            return
        ch = self._cur_char()
        if ch is None:
            self.var_poll.set(False)
            self.poll_on = False
            return
        # 间隔在主线程里读好再传进去 —— 后台线程不该碰 tkinter 的 Variable
        interval = max(0.2, float(self.var_poll_sec.get()))
        self.spawn(self._poll_loop(ch, interval), "自动读")

    async def _poll_loop(self, ch: BleakGATTCharacteristic, interval: float) -> None:
        while self.poll_on and self.client is not None and self.client.is_connected:
            try:
                data = await asyncio.wait_for(
                    self.client.read_gatt_char(ch), timeout=5.0
                )
                self.post(self._show_value, "自动读", ch.uuid, bytes(data))
            except Exception as exc:
                self.post(self.log, f"[X] 自动读失败，已停：{exc!r}")
                break
            await asyncio.sleep(interval)
        self.post(self.var_poll.set, False)
        self.poll_on = False

    def _show_value(self, action: str, uuid: str, data: bytes) -> None:
        name = uuid_name(uuid) or str(uuid)
        extra = describe(data)
        self.log(f"{action} {name}（{len(data)}B）: {hexdump(data)}")
        if extra:
            self.log(f"        └─ {extra}")
        # 电脑信息那几个顺手更新快捷面板里的「当前值」
        if name in self.laptop_labels:
            self.laptop_labels[name].configure(text=fmt_number(data))

    # ---------------- 快捷面板的动作 ----------------

    def _char_by_name(self, name: str) -> BleakGATTCharacteristic | None:
        for ch in self.chars.values():
            if uuid_name(ch.uuid) == name:
                return ch
        self.log(f"[X] 没找到特征 {name} —— 连上了吗？固件里改名了？")
        return None

    def q_read(self, name: str) -> None:
        ch = self._char_by_name(name)
        if ch is not None:
            self.spawn(self._read(ch), f"读 {name}")

    def q_write(self, name: str, payload: bytes | None = None) -> None:
        """payload 为 None 时用界面上「写入」框里的内容。"""
        ch = self._char_by_name(name)
        if ch is None:
            return
        if payload is None:
            self._write_text(ch, self.var_write.get(), bool(self.var_dec.get()))
        else:
            self.spawn(self._write_async(ch, payload), f"写 {name}")

    def q_notify(self, name: str, on: bool) -> None:
        ch = self._char_by_name(name)
        if ch is not None:
            self.spawn(
                self._notify(ch, on), f"订阅 {name}" if on else f"取消订阅 {name}"
            )

    def q_write_fan_mode(self) -> None:
        try:
            value = mode_value(self.var_fan_mode.get(), FAN_MODE_NAMES)
        except ValueError as exc:
            self.log(f"[X] {exc}")
            return
        self.q_write("fan_mode", struct.pack("<i", value))

    def q_write_fan_pwm(self) -> None:
        self.q_write("fan_pwm", bytes([int(self.var_fan_pwm.get()) & 0xFF]))

    def q_write_light_mode(self) -> None:
        try:
            value = mode_value(self.var_light_mode.get(), LIGHT_MODE_NAMES)
        except ValueError as exc:
            self.log(f"[X] {exc}")
            return
        self.q_write("light_mode", struct.pack("<i", value))

    def q_write_brightness(self) -> None:
        self.q_write("light_brightness", bytes([int(self.var_brightness.get()) & 0xFF]))

    def q_write_period(self) -> None:
        period = int(self.var_period.get())
        if period not in LIGHT_UPDATE_PERIODS_MS:
            self.log(f"[X] 刷新周期只接受 {LIGHT_UPDATE_PERIODS_MS} ms")
            return
        self.q_write("light_update_per", bytes([period]))

    # ---- 电脑信息 ----

    def q_read_all_laptop(self) -> None:
        for name, _desc in LAPTOP_FIELDS:
            self.q_read(name)

    def q_fake_all_laptop(self) -> None:
        """把一组假数据按 double 写给全部 13 个特征，方便先看板子行为。"""
        for (name, _desc), value in zip(LAPTOP_FIELDS, LAPTOP_FAKE_VALUES):
            self.q_write(name, struct.pack("<d", float(value)))

    def q_write_laptop(self, name: str) -> None:
        entry = self.laptop_entries.get(name)
        if entry is None:
            return
        try:
            value = float(entry.get().strip())
        except ValueError:
            self.log(f"[X] {name} 的写入值不是数字")
            return
        self.q_write(name, struct.pack("<d", value))

    # ---------------- 日志按钮 / 退出 ----------------

    def on_clear_log(self) -> None:
        self.txt_log.configure(state="normal")
        self.txt_log.delete("1.0", "end")
        self.txt_log.configure(state="disabled")

    def on_save_log(self) -> None:
        path = filedialog.asksaveasfilename(
            defaultextension=".log", filetypes=[("日志", "*.log"), ("全部", "*.*")]
        )
        if not path:
            return
        with open(path, "w", encoding="utf-8") as fp:
            fp.write(self.txt_log.get("1.0", "end"))
        self.log(f"日志已保存到 {path}")

    def _on_close(self) -> None:
        self.poll_on = False
        try:
            if self.client is not None and self.client.is_connected:
                self.bridge.submit(self.client.disconnect()).result(timeout=5)
        except Exception:
            pass
        self.bridge.stop()
        self.destroy()


# ============================================================
#  入口
# ============================================================


def main() -> int:
    ap = argparse.ArgumentParser(description="CoolerGUI —— LaptopCooler BLE 测试工具")
    ap.add_argument(
        "--addr", default=DEFAULT_ADDR, help=f"默认目标 MAC（默认 {DEFAULT_ADDR}）"
    )
    ap.add_argument("--timeout", type=float, default=8.0, help="默认扫描秒数（默认 8）")
    args = ap.parse_args()

    app = App(default_addr=args.addr, default_timeout=args.timeout)
    app.mainloop()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
