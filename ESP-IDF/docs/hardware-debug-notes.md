# 硬件调试记录：串口验证 + 致命报错修复

> 日期：2026-10-16　操作人：DSH（接管自 Codex 额度耗尽处）
> 环境：ESP32-S3 开发板（QFN56 rev v0.2，8MB PSRAM），COM7 = 原生 USB-Serial-JTAG

## 一、串口通道结论（重要，供你决定固件方向）

| 通道 | 固件现状 | PC 可达性 |
|---|---|---|
| **COM7 (USB-Serial-JTAG)** | `CONFIG_ESP_CONSOLE_SECONDARY_USB_SERIAL_JTAG=y` → **仅输出**（副控制台，boot 日志看得到，RX 无人读） | 只出不进：主机写不 ACK（`write_timeout` 超时实测） |
| **UART0（GPIO43/44）** | `CONFIG_ESP_CONSOLE_UART_DEFAULT=y`；`test.c` 的 `uart_debug_task` 读这里 | **PC 通过 USB 够不着**（除非板上有 CP2102/CH340 桥接芯片，当前设备列表里没有） |

- **已验证**：`esptool --chip esp32s3 -p COM7 chip_id` 成功 → ROM 引导器 SLIP 协议双向收发正常，物理链路 OK；基 MAC `84:c7:bb:56:6a:14`（BLE 地址 = 基 MAC+2）。
- **临时验证（已通过）**：给 `test.c` 加了 USJ 读取，应用层串口命令可达。**验证后已撤回并重烧干净固件**。
  - 闭环证据：PC 串口发 `'5'` → COM7 收到 ESP 日志 `UARTDBG: 收到串口命令 0x35`（9 次全对）→ BLE 读回 `light_mode` 6→7→6 完整可观测。
  - 测试脚本留在 `agent/tools/serial_loopback_test.py`（可重复跑）；烧录后健康检查用 `agent/tools/board_health_check.py`。
- **永久方案（二选一，需要你拍板）**：
  1. `CONFIG_ESP_CONSOLE_USB_SERIAL_JTAG=y`（主控制台换到 USJ，输入输出都在 COM7，`uart_debug_task` 仍需改读 USJ）；
  2. 保留现状，在 `test.c` 里长期读 USJ（`usb_serial_jtag_driver_install` + `usb_serial_jtag_read_bytes`）。

## 二、致命报错（已修复 + 已撤回临时代码）

**现象**：新固件烧录后无限重启。每次启动在 BLE 开始广播后约 20ms：
```
Guru Meditation Error: Core 0 panic'ed (LoadProhibited)
EXCVADDR: 0x00000004
Backtrace: usb_serial_jtag_read_bytes → uart_debug_task (test.c:59)
```

**根因**：`usb_serial_jtag_read_bytes()` 未检查驱动是否安装，直接解引用全局对象
`p_usb_serial_jtag_obj->rx_ring_buf`；对象为 NULL（0x4 即 NULL+成员偏移）。
而固件只开了"副控制台输出"，不会安装 USJ 驱动（副控制台走 LL 直写，所以日志正常）。

**修复**：读之前先 `usb_serial_jtag_driver_install(&cfg)`。
该改动与 CMakeLists 的 `esp_driver_usb_serial_jtag` REQUIRES 同属临时调试代码，
**串口验证完成后已全部撤回并重新烧录干净固件**。

## 三、发现但未改动（非致命，记录备查）

- 启动日志 `E (462) SH1106: [PROBE] I2C failed / INIT cmd 0xAE failed`：
  板子上没有 OLED，每次启动报两条错后继续跑。无害，但真实板子到手后可关注。
- `W (298) spi_flash: Detected size(16384k) larger than header(2048k)`：
  板载 flash 实际 16MB，工程按 2MB 用。无害。
- 编译警告（未改）：`oled_display_task.c` unused `debug`/`last_t`/`current_t`、
  `oled_menu.c:1671` unused `font`、`event_handler.c` `s_rpm` defined but not used、
  `main.c` `TAG` defined but not used。
- 烧录后板子运行的是**当前源码**（含此前未上电的灯光重构），非 Oct 12 旧版。

## 四、Windows 客户端收尾（Codex 遗留项，已处理）

- **安全警告弹窗**：G 盘被附件管理器判为 Internet 区（CheckPolicy=Prompt）。已实测：
  部署到 `%LOCALAPPDATA%\SmartFanNext\app` → FileZone=0、CheckPolicy=Enable，启动无弹窗。
  `tools/publish.ps1` 已加 `-Deploy` 开关固化此流程。
- **渲染性能**：灯效示意已改用 `CompositionTarget.Rendering`（原来是 40ms 定时器=25FPS）。
  实测（仪表盘页，15 秒探针）：渲染回调 64.6/s，帧间隔 P95 19.6ms、最大 31.5ms、
  0 帧 ≥50ms，CPU 0.24% —— 满帧无卡顿。摘要 JSON 在 `%APPDATA%\SmartFanNext\frontend-logs\`。
- **工具脚本修复**：`tools/Inspect-ProjectSecurity.ps1` 有多余 `}` 和中文注释编码吞行两个 bug，已修。
- 发布产物：`windows-client\artifacts\win-x64`（源码包）+ `%LOCALAPPDATA%\SmartFanNext\app`（免弹窗运行位）。

## 五、遗留问题（未修复，留给 Codex 继续）

### 5.1【现象】Rider 里运行客户端 → 前端显示"后台尚未接入"，连不上后台

前端日志（`%APPDATA%\SmartFanNext\frontend-logs\smartfan-*.jsonl`）原文：

```
23:29:36 准备启动后台：G:\...\SmartFan.App\bin\Debug\net10.0-windows10.0.19041.0\SmartFan.Agent.exe
23:29:36 后台启动 PID=38908
23:29:36 [Error] 后台启动退出码=0；
23:29:36 [Error] 后台启动后退出（0）：。日志位于 agent.json 同目录的 logs。
```

说明：新起的后台因单实例互斥量（`Local\<pipeName>`）被既有后台占用而退出 0（属正常设计），
但随后的**重试接入也失败**，于是抛 "后台启动后退出（0）"。

### 5.2【已确认的根因】程序从 G 盘启动 → 无法连接命名管道

严格 A/B（同一份 Debug 二进制，只改位置/工作目录，后台均为 PID 6140）：

| # | exe 位置 | 工作目录 | 结果 |
|---|---|---|---|
| ① | G: | G: | ❌ 无法连接 |
| ② | G: | C: | ❌ 无法连接 |
| ③ | C: | G: | ✅ 成功 |
| ④ | C: | C: | ✅ 成功 |

- 复现方式：`SmartFan.Agent.exe --probe`（该命令走的就是 `BackgroundClient.AttachAsync`）。
- **自包含发布版从 G 盘启动同样失败** → 与 Debug/Release、自包含/框架依赖无关。
- 反证：同一份 Debug 输出拷到 `%LOCALAPPDATA%\Temp\dbgapp` 后立刻可用；`--probe` 能读到
  完整 AgentStatus（ProcessId 6140、采样计数、实时温度）。
- 源码中**没有** `#if DEBUG` 之类分支；`app.manifest` 是普通 `asInvoker`。

### 5.3【佐证】G 盘在本机被当作不可信区域

- 附件管理器 `MapUrlToZone(MUTZ_ISFILE)`：G 盘文件 = **Zone 3（Internet）**；C 盘 dotnet.exe = Zone 0。
- Windows PowerShell（.NET Framework 宿主）`Assembly.LoadFrom` G 盘程序集 → `0x80131515`（不支持操作），
  换成 C 盘同一文件即可加载。
- G 盘本身正常：`fsutil fsinfo drivetype G:` = Fixed Drive、NTFS、卷标 `Project`；
  非 subst、非映射、非网络盘；Smart App Control = 0（关闭）；
  HKCU/HKLM 附件策略无自定义项（仅默认 `ScanWithAntiVirus=3`）。
- **可疑机制（未最终证实）**：G 盘启动的进程可能处于受限令牌/AppContainer 上下文，
  而 `AgentPipeServer` 用 `PipeOptions.CurrentUserOnly` 创建管道、ACL 只授予当前用户 SID，
  导致受限上下文客户端被拒绝访问。若需保留 G 盘工作流，可在 `AgentPipeServer`/`BackgroundClient`
  上试验改用显式 `PipeSecurity`（例如同时授予 `ALL APPLICATION PACKAGES`，需评估安全影响），
  并用 `--probe` 从 G 盘验证。**注意这是安全相关的取舍，需你确认后再改。**

### 5.4【结论 / 当前可用做法】

- ✅ **可用**：`C:\Users\zen\AppData\Local\SmartFanNext\app\SmartFan.exe`（本次已实测：启动即接入后台，
  日志 "后台运行中；关闭前端不会停止采样与蓝牙"，无安全警告弹窗）。
- ❌ **不可用**：从 G 盘启动的任何形式 —— 包括 `artifacts\win-x64\Start-SmartFan-Portable.cmd`
  （**更正此前建议**，该便携启动器在 G 盘，实测连不上后台）。
- 待你确认的候选修法（本次均**未实施**）：① `SmartFan.App` 增加 `Directory.Build.props`
  把构建输出改到 C 盘（源码留 G 盘）；② 把 `windows-client` 复制到 C 盘再调试；③ 按 5.3 改管道 ACL。
- 本次状态：未改动任何源码（`git diff` 对 `main/test.c`、`main/CMakeLists.txt` 为空）；
  仅执行过 `dotnet build`（Debug，含一次 `--no-incremental`）与临时文件拷贝。
- 临时残留（可随时删）：`%LOCALAPPDATA%\Temp\dbgapp\`、`%TEMP%\dbgload\`、`%TEMP%\probe*.txt`。
