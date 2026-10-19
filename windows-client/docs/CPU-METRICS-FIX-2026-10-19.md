# 2026-10-19 CPU 频率、VID 与后台权限修复

本机已取得真实 CPU Package 温度、24 个核心频率和 CPU VID。2026-10-18 记录中的缺失状态属于安装和修复前证据；本次是在用户明确授权下安装官方 PawnIO 驱动、更新 Windows 客户端并完成实际采样，不使用演示值补齐。

## 原因与改动

本轮处理三个相连的问题：CPU MSR 访问依赖未满足；原电压选择未覆盖 LHM 的核心 VID 命名；普通前端连接管理员后台时，.NET 的管道身份判断把令牌 Owner 当作真实用户。

| 部分 | 当前行为 |
|---|---|
| PawnIO 与后台权限 | 安装官方签名 2.2.0.0。普通前端先尝试附着现有后台；确实没有后台且检测到 PawnIO 已安装时，仅为后台请求 UAC。 |
| CPU 频率 | 从现有 LHM 核心 Clock 传感器读取有限正值，主页显示有效核心的最高频率，不把标称频率当作即时采样值。 |
| CPU 电压选择 | `CpuVoltageSelection` 对 CPU 节点使用名称白名单，区分 `VCore`、`Vid` 和未知类型；Intel 的 `CPU Core` 也保守归为 VID。同一 CPU 包内优先明确 VCore，否则取最高有效核心 VID。 |
| 界面与遥测 | `CpuVoltageKind` 随快照传递；VID 显示为“CPU 核心 VID / 最高核心 VID”。`TelemetryUploader` 不将 VID 写入现有实测 CPU 电压字段。 |
| IPC | 管道按真实 User SID 授权，受保护 ACL 只允许该用户和 SYSTEM，拒绝 NETWORK；客户端验证服务端身份与权限，服务端验证客户端 SID。 |
| 启动与错误 | 已有后台优先复用，不重复 UAC；取消提权有明确提示，权限拒绝不会触发反复启动。前端本身保持普通权限。 |

本项目采用的 [LHM v0.9.6 IntelCpu 源码](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/v0.9.6/LibreHardwareMonitorLib/Hardware/Cpu/IntelCpu.cs)把核心 VID 命名为 P-Core/E-Core 等，通用 `CPU Core` 与逐核心条目读取同一类 MSR 电压编码。应用因此明确区分“请求电压”和主板实测 VCore，不仅按传感器名称中是否出现 VID 判断。

.NET 10 `CurrentUserOnly` 的 Owner/User 差异已有[上游问题记录](https://github.com/dotnet/runtime/issues/123903)。本次修复的是应用自身管道对象的权限和身份校验，没有关闭或绕过 UAC、放宽系统策略、开放 Everyone/Users 或允许网络客户端。

## 驱动来源与核验

用户接受默认 `C:\Program Files\PawnIO` 安装位置，安装器退出码为 0。安装包保留在 `windows-client/dependencies/PawnIO/PawnIO-2.2.0-setup.exe`，exe 已加入忽略规则；[依赖说明](../dependencies/PawnIO/README.md)记录获取和核验信息。

| 项目 | 本机核验结果 |
|---|---|
| 官方来源 | [PawnIO 官网](https://pawnio.eu/)链接的 [PawnIO.Setup 2.2.0 发布](https://github.com/namazso/PawnIO.Setup/releases/tag/2.2.0) |
| 安装包版本 | 2.2.0.0 |
| 安装包 Authenticode | Valid，签名主体 `namazso.eu` |
| 安装包 SHA-256 | `1F519A22E47187F70A1379A48CA604981C4FCF694F4E65B734AAA74A9FBA3032`，与 GitHub release digest 相同 |
| 本机驱动包 | `oem16.inf`；签名为 Microsoft Windows Hardware Compatibility Publisher |
| 运行时诊断 | `PawnIoInstalled=true`、`PawnIoVersion=2.2.0.0`、`PawnIoDeviceAccessible=true` |

安装包签名主体与 Windows 驱动包签名主体是不同核验项目；`oem16.inf` 是这台机器分配的名称，不是其他机器的固定文件名。未使用无签名版本，未修改设备访问权限来开放普通用户的直接硬件访问。官方分发形式见 [PawnIO 官网说明](https://pawnio.eu/)。

## 实机采样

时间均为 Asia/Shanghai。2026-10-19 12:13，普通前端 PID 31552 成功启动管理员后台 PID 32716。12:14 的首次审计显示后台管理员状态为 True，LHM 版本为 0.9.6，数据不是演示模式。

| 指标 | 首次审计结果 |
|---|---|
| CPU 型号 | Intel Core Ultra 9 290HX Plus |
| CPU Package | 68°C，来源 `/intelcpu/0/temperature/26` |
| 主页 CPU 频率 | 4261.163574 MHz |
| 核心 Clock | 24 个核心全部有正值，来自 P-Core/E-Core 的 MSR 采样 |
| CPU Voltage 条目 | 通用 CPU Core 加 24 个核心，共 25 项，全部有正值 |
| 主页电压类型与数值 | `CpuVoltageKind=Vid`，0.9783935547 V；属于请求电压，不是实测 VCore |

后续同一后台的样本 36、37、38 依次采集到约 4558.4536、4558.4546、4558.4551 MHz，VID 约 1.031006、1.031006、1.028442 V。频率和 VID 随采样变化，不能把这些观察值当作固定输出或标称参数。

关闭前端后，后台 PID 32716 与 BootId 保持不变，样本数继续达到 57。12:16 重新启动普通前端 PID 31984，成功复用同一后台，采样数增至 119。启动日志没有再次“准备启动后台”，也未再次出现 UAC；新快照为约 4261.1641 MHz、VID 0.97595215 V。该结果证明真实硬件采样不依赖前端存活，普通前端重开不会重建管理员后台。

硬件传感器窗口也已实际打开，显示 234/234 有效项，逐核心 Clock 可见。12:13 以后本次前端 Error 日志数量为 0。本次没有测试传感器窗口两秒实时刷新开关或筛选，不将打开并读取成功扩展为这些功能的实测结果。

本次前后审计的 `configuration` 完全相同，ESP 始终为 Disconnected，`EnabledTelemetry=false`，没有向设备应用设置或上报数据。当前确认的是电脑传感器与后台进程行为，不是最终 PCB 的风扇或灯条物理效果。

## 登录自启边界

`DesktopService` 仍使用 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 普通启动后台，没有提升权限的登录任务，本机当前未启用该选项。不能把本次由前端请求 UAC 成功启动，表述为登录时能够自动提升权限。

若以后启用现有普通登录项，后台可能先以普通权限运行；前端会按现有实例优先策略附着，不会自行重启或提升那个进程。需要硬件权限时，应明确停止旧普通后台，再由前端按启动策略申请管理员后台。

## 自动检查与证据

本版本通过 939 项 WPF、210 项服务、60 项 IPC 检查。新增覆盖包括电压白名单与 VID 分类、VID 遥测拦截、对应界面标签、同用户管道 ACL 和身份校验、启动权限选择与取消处理。六个主要程序/程序集部署后 SHA-256 与发布输出一致。

证据目录为 `windows-client/artifacts/cpu-metrics-fix-20261018/`。目录名沿用任务开始日期，以下实机数据日期为 **2026-10-19**：

- `after-driver-20261019.json`：安装后真实后台状态、来源、24 核心频率与 25 项电压清单。
- `live-samples-20261019.json`：同一后台连续采样的频率、VID、权限与驱动可用性。
- `frontend-closed-probe.json`：关闭前端后后台继续采样。
- `frontend-reopened-probe.json`、`native-acceptance-20261019.json`：普通前端重开复用同一管理员后台、采样数继续增加、无重复 UAC，以及实际传感器窗口验收汇总。
- `installer-verification.json`、`install-result.json`：安装包版本、签名、SHA-256、官方 digest 和安装退出结果；安装执行发生于 2026-10-18。
- `deployment-verification.json`、`ui-checks.txt`、`service-checks.txt`、`ipc-checks.txt`：部署哈希与自动检查输出。

本次未修改、构建或烧录 ESP32 固件；未把 VID 伪装为实测电压，没有改动后台的连接、遥测或自动上传配置。
