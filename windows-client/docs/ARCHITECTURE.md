# SmartFan 前端与后台

本项目使用两个独立的 Windows 进程。`SmartFan.exe` 提供 WPF 界面；`SmartFan.Agent.exe` 持有 BLE 连接、RPM 通知订阅、硬件采样和遥测上报。关闭或重开前端不会停止后台，也不会重新创建硬件采集器。

## 职责与依赖

```text
SmartFan.App / SmartFan.exe
  WPF、场景和灯效编辑器、主题、壁纸、曲线、文件对话框
        │ 当前用户命名管道，带请求 ID 的 JSON RPC
        ▼
SmartFan.Runtime / SmartFan.Agent.exe
  AgentHost：唯一 BLE 会话、唯一采样循环、状态及配置
        ├─ HardwareMonitorService → LibreHardwareMonitor 0.9.6 / PawnIO 2.2.0 / Windows 内存 API
        │                            / 已验证 ASUS G615LRG 只读 EC 后备
        ├─ TelemetryUploader → 缺失值过滤、diff、温度心跳
        └─ SmartFan.Core → WinRT BLE、27 个特征的编解码和校验
```

`SmartFan.Runtime` 是可测试的共享类库。出于兼容本轮拆分，模型和服务暂时保留 `SmartFan.App.Models`、`SmartFan.App.Services` 命名空间，但源码和实现已在 Runtime 项目中；它们不依赖 WPF 窗口。前端通过 `BackgroundClient : IBleClient` 使用这些能力，不在前端 new 一个真实 `NativeBleClient` 或硬件监控器。

`SmartFan.SelfTest` 是独立的协议/设备自检工具。它应在后台没有占用设备时直接测试 BLE，或通过前端自检页使用现有后台连接；测试工具的连接不代表支持多客户端同时控制 ESP。

## 前端视图与数据来源

主窗口将 `MainViewModel.DashboardData` 作为显式的 `Metrics` 依赖属性传给 `DashboardView`，再传给每个 `DashboardWidgetHost`。组件通过 `DashboardTelemetryData` 读取指标，不依赖父级偶然继承的 `DataContext`；替换指标源时，组件会改为展示新来源。电脑内置风扇使用独立的摘要和来源说明，ESP32 底座风扇使用 BLE RPM 通知，两者不混用。

主页不嵌入日志面板。“高级设置”和“接口自检”提供独立 `LogWindow` 入口，窗口按需订阅前端日志，关闭后解除订阅；日志目录、导出与短时界面调度测量也集中在该窗口。测量结果记录渲染回调与调度延迟，不等同于 GPU 呈现 FPS。

`SensorInspectorWindow` 首次加载时自动向已有后台读取一次传感器列表，之后可手动刷新或选择每两秒刷新；筛选只作用于当前结果，不创建新的硬件采样器。表格区分真实零值、无读数和未知单位，读取失败会提示并保留上次结果，关闭窗口后停止列表刷新并忽略尚未完成的响应。

## 进程生命周期

1. 后台独立启动时，先加载并验证 `agent.json`，恢复用户已明确启用的连接与遥测参数，然后建立唯一采样循环。
2. 前端启动时先尝试连接当前用户的后台，成功即复用，不请求 UAC。没有后台时检测 PawnIO 安装注册信息：普通前端且已安装驱动时，仅以 `runas` 启动 `SmartFan.Agent.exe`；否则沿用当前权限，再等待管道就绪。取消管理员提示会明确返回取消错误，已有后台拒绝访问时提示处理旧版本，不通过重复启动绕开拒绝。
3. 前端 attach 后读取后台当前状态、配置和最近的硬件快照。它不会用界面默认值覆盖后台配置，也不会再次创建采集器。
4. 关闭前端只断开该前端的管道。后台继续采样、接收 RPM 通知、按已有配置上报；是否仍有前端连接不影响后台设备会话。
5. 再次打开前端时，界面恢复当前会话的真实状态。前端重开不应再次触发“新连接同步配置”，也不应使设备断开重连或重置遥测 diff 缓存。
6. 用户主动断开设备或停止后台是另一项明确操作。停止后台才会取消采样、释放 BLE 和硬件监控资源。

默认配置只采集本机真实硬件状态，不主动连接设备、不自动上报。只有用户启用后的选项才会在后续后台启动时恢复。

## 两份配置文件

默认目录为 `%APPDATA%\SmartFanNext`，与旧版 `%APPDATA%\SmartFan` 分开。

前端显式使用 `--data-dir <目录>` 时，窗口设置和新启动后台的 `agent.json` 都使用该目录；发布包的 `Start-SmartFan-Portable.cmd` 等同于传入包内 `data` 子目录。便携模式禁用旧版偏好迁移，默认模式继续使用原有 AppData 规则。现有后台优先：附着已运行实例时读取 `AgentStatus.ConfigurationPath` 显示它实际使用的 JSON，不用新前端的目录假装替换正在运行的后台。

| 文件 | 内容 | 所有者 |
|---|---|---|
| `settings.json` | 外观、壁纸裁剪、窗口位置、场景、灯光配置、曲线草稿、UI 偏好 | 前端 |
| `agent.json` | 默认设备、自动连接/重连、上报开关/频率/项、传感器选择、自动同步场景及演示模式等后台启动参数 | 后台 |

两个文件都带 `SchemaVersion`。写入使用同目录临时文件、刷盘和原子替换；替换已有文件时保留 `.bak`。不能把不存在的固件接口写进 JSON 后宣称固件已支持。

前端修改后台参数时，通过 IPC 请求后台校验、原子保存并应用。后台正在运行时，修改磁盘上的 JSON 不等于已经修改内存配置；通过界面应用，或退出后台后修改文件再重启。不要同时让两个进程直接争写 `agent.json`。

以下是有效的最小启动配置示例，保持遥测关闭，避免未经用户选择就向设备发送电脑数据：

```json
{
  "SchemaVersion": 1,
  "EnabledTelemetry": false,
  "AutoConnect": false,
  "AutoReconnect": true,
  "ReconnectInitialSeconds": 2,
  "SavedDevice": null,
  "DemoMode": false,
  "AutoUploadConfiguration": false,
  "AutoUploadProfile": null,
  "Telemetry": {
    "Enabled": false,
    "IntervalMs": 1000,
    "RamWireUnit": "Unspecified",
    "SensorPreferences": {},
    "EnabledItems": {
      "laptop_cpu_temp": true,
      "laptop_gpu_temp": true
    }
  },
  "LogLevel": "Information"
}
```

`EnabledTelemetry` 是后台总开关，应用配置时会与嵌套的 `Telemetry.Enabled` 保持一致。演示模式不能与真实遥测开启同时存在。内存 wire unit 未确认时保持 `Unspecified`，客户端仍能在本机显示内存，但不会将 RAM/VRAM 数值猜成某个设备单位。

开启 `AutoUploadConfiguration` 时必须显式保存合法的 `AutoUploadProfile`，后台才会在新的物理连接建立后自动上传已有协议支持的风扇/灯光参数。它不会上传不存在的温控曲线特征。关闭自动同步时，由前端在新的设备连接后询问用户；同一后台连接的界面重开不会再次上传。

## 单独启动与排查

在 `windows-client` 目录发布部署后，可以独立启动后台，不需要先打开 WPF。本机 G 盘项目路径被判为 Internet 区，交接 A/B 测试还确认了从 G 盘启动时的管道访问失败；下面统一使用已验证的 C 盘部署目录，保留 G 盘源码：

```powershell
.\tools\publish.ps1 -Deploy
& "$env:LOCALAPPDATA\SmartFanNext\app\SmartFan.Agent.exe" --config "$env:APPDATA\SmartFanNext\agent.json"
```

后台首次运行会生成默认配置。也可只导出默认配置而不启动采集：

```powershell
& "$env:LOCALAPPDATA\SmartFanNext\app\SmartFan.Agent.exe" --print-default-config --output .\agent-example.json
```

查询当前后台状态不会创建第二个采集器：

```powershell
& "$env:LOCALAPPDATA\SmartFanNext\app\SmartFan.Agent.exe" --probe --output .\agent-status.json
```

状态包括 PID、`BootId`、`ConnectionGeneration`、采样次数、真实权限状态和最近快照。`BootId` 标识后台进程生命周期，`ConnectionGeneration` 标识新建的 BLE 连接；前端自身的启动不改变这两个值。`--pipe` 用于指定独立诊断管道，`--run-for-seconds N` 用于限时运行测试。正常运行同一用户/管道仅保留一个后台实例，重复启动会退出新实例，不替换已有会话。

后台日志写在所选配置文件同目录的 `logs` 子目录，保留 14 天，每个文件上限 5 MiB，每天最多保留 8 个轮转归档。前端日志在当前数据目录的 `frontend-logs` 子目录（默认 `%APPDATA%\SmartFanNext\frontend-logs`），两者应分别查看。前端显示的管理员标记来自后台进程，而不是当前窗口；只提升前端不能改变已经运行的硬件采样器权限。

当前机器从 G 盘启动编译后 exe 时曾无法写 AppData 或接入后台，而 dotnet 宿主行为不同。后续交接测试确认同一份程序从 C 盘运行可以接入后台。没有修改目录 ACL 或静默回退目录；便携模式只改变配置目录，不能解决从 G 盘启动 exe 的信任区域问题。2026-10-19 的 IPC 修复仅调整应用命名管道自身的 ACL 与身份校验，不改变 Windows 安全策略。

“后台登录自启”使用当前用户注册表 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 的 `SmartFanNext.Agent` 值，命令指向 `SmartFan.Agent.exe --config "…\agent.json"`，不会为登录启动打开控制面板。它仍以普通权限运行，没有提升权限的登录计划任务；本机当前未启用此选项。若普通登录后台先启动，前端会优先复用，不会自动替换为管理员进程，不能宣称登录后已具备 PawnIO 访问能力。前端托盘的“关闭控制面板”同样保留后台。新安装默认关闭窗口就退出前端；可显式开启“关闭窗口时最小化到托盘”，已保存的选择仍会保留。

需要明确停止后台时使用界面的“停止后台”操作，或运行以下命令。`--stop` 先得到确认响应，再让后台取消生命周期并释放 BLE/采样器；普通关闭前端不会发送停止请求。控制台手动启动的后台也可用 Ctrl+C 正常退出。

```powershell
& "$env:LOCALAPPDATA\SmartFanNext\app\SmartFan.Agent.exe" --stop
```

## 进程通信

管道名包含当前 Windows 用户 SID，协议版本为 v1，不监听 TCP 端口。2026-10-19 起不再依赖 .NET 10 的 `CurrentUserOnly`：该实现按令牌 Owner 而非 User 判断身份，UAC 提权后 Owner 可能变成 Administrators，使同一用户的普通前端无法连接管理员后台；上游问题见 [dotnet/runtime #123903](https://github.com/dotnet/runtime/issues/123903)。

服务端显式把管道所有者设为当前真实 User SID，使用受保护 DACL，只允许该用户和 SYSTEM，并显式拒绝 NETWORK，阻止通过网络访问同名管道。客户端连接本机 `.`，先核对服务端所有者、受保护 ACL、允许范围和网络拒绝项；使用 Identification 级别查询身份，不授予代理执行身份操作的权限。服务端读到第一帧后核对客户端 User SID，验证后才处理请求。该边界支持同一 Windows 用户的普通/管理员进程通信，不开放给 Everyone、Users 或不同账户，也不修改 UAC、系统安全策略或其他对象的 ACL。

每个消息由 4 字节小端长度和 UTF-8 JSON 组成，单帧最多 1 MiB。

消息包含类型、请求 ID、方法、载荷和错误信息。请求和响应按 ID 配对，事件独立传送；前端超时或取消时可发送该请求的取消消息。每个前端最多 32 个在途请求，后台对请求设置时间上限，事件发送也有独立超时。一个前端离开或发送非法帧时只清理该前端，不停止全局 BLE 或硬件采样。

主要方法为：

| 方法 | 作用 |
|---|---|
| `status` / `configuration` | 获取后台状态、快照及启动配置 |
| `configure` / `configureTelemetry` | 校验、保存并应用后台配置 |
| `demo` | 明确切换演示数据来源，禁止真实遥测 |
| `sensors` | 查询后台唯一采样器的传感器清单 |
| `scan` / `connect` / `disconnect` / `unpair` | 请求后台管理 BLE 生命周期 |
| `read` / `write` | 请求后台执行已定义 GATT 特征操作 |
| `shutdown` | 显式请求停止后台，确认响应发送后才触发退出 |

后台推送 `status`、`snapshot`、`rpm`、`device` 和 `diagnostic` 事件。配置 JSON、IPC 和 GATT 三层的版本及数据格式相互独立；IPC 的 JSON 不能直接当作 ESP32 的二进制载荷。

## 数据与控制

真实采样中不可用的值用 null 表示，界面显示“不可用”；不填假零、不悄悄切换演示。优先选择独显，并按白名单选择 CPU Package/Core Max、GPU Core 等温度；用户可手动指定实际传感器。

ASUS G615LRG 的只读 EC 后备通过已验证的机型和传感器映射补充 CPU 温度、CPU/GPU/辅助风扇，不写 EC 控制值，也不伪装成标准 CPU Package。较早的 CPU 75～83°C、CPU/GPU 风扇约 5400/4100 RPM、辅助风扇约 4900～6000 RPM 是历史采样值。2026-10-19 安装官方签名 PawnIO 2.2.0.0 并启动管理员后台后，LHM 0.9.6 的 CPU Package、24 个核心频率及 25 个 CPU Voltage 项已有真实读数；来源及数值见 [CPU 指标修复记录](CPU-METRICS-FIX-2026-10-19.md)。安装前限制保留在 [CPU-TEMPERATURE-DIAGNOSTICS.md](CPU-TEMPERATURE-DIAGNOSTICS.md)中。

`CpuVoltageSelection` 只接受 CPU 硬件节点上的有限正值，按名称白名单区分 VCore 和 VID，优先明确的 VCore；Intel `/intelcpu/…` 的 `CPU Core` 及 P-Core/E-Core 电压项按 VID 请求电压处理。有多个 VID 时取同一 CPU 包内最高有效值。`TelemetrySnapshot.CpuVoltageKind` 随读数传递，主页使用“CPU 核心 VID / 最高核心 VID”标签，来源与提示明确它不是主板实测 VCore。[LHM v0.9.6 的 IntelCpu 实现](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/v0.9.6/LibreHardwareMonitorLib/Hardware/Cpu/IntelCpu.cs)将这些项目从 MSR 读取；原来的名称不应直接等同于实测核心供电电压。

遥测先检查来源、有效期、上报项及内存单位。`CpuVoltageKind.Vid` 仅在本机显示，即使启用了 `laptop_cpu_volta` 也不按实测 CPU 电压上传；现有固件字段没有表达“请求电压”语义，不能混用。普通不变值跳过；温度最迟每 5 秒发送有效值作为心跳，为后续 ESP32 的 10 秒超时策略预留条件。重新连接真实设备时才清除 diff 缓存；前端重开不应触发缓存清除。

PC 不运行风扇调速闭环。温度曲线、温度来源和“10 秒无有效数据后 20% PWM”目前缺少固件配置接口，详见 [FIRMWARE-ALIGNMENT.md](FIRMWARE-ALIGNMENT.md)。曲线编辑和 JSON 导出可用不代表已经上传设备。

## 测试边界

`SmartFan.Services.Tests` 验证设置原子保存、导入校验、曲线边界、传感器选择、遥测单位/缺失数据/diff。`SmartFan.IntegrationTests` 使用真实命名管道和可注入的假 BLE/采样器，验证前端 attach/detach、后台连续运行、配置恢复、单采样器和请求隔离；它不会写入真实 ESP32。

```powershell
dotnet run --project .\SmartFan.SelfTest -- --offline
dotnet run --project .\SmartFan.Services.Tests
dotnet run --project .\SmartFan.IntegrationTests
dotnet run --project .\SmartFan.Ui.Tests
```

2026-10-17 本轮协议离线自检通过 1236 项检查、服务测试通过 101 项断言、IPC/生命周期集成测试通过 48 项断言，后者包含另一个 OS 进程中的后台夹具。还验证了后台按用户明确开启的选项仅在物理连接建立时上传支持的场景字段，前端重开不重复上传，不用 PC 目标 RPM 写入替代 ESP 温控；显式停止先确认后退出，日志轮转、保留期限及超长消息遵守上限。另有较早的生产 `SmartFan.Agent.exe` 真实硬件只读探测记录：`SmartFan.Agent/test-run/probe-first.json` 与 `probe-second.json` 的 PID 均为 38268、`BootId` 相同，采样次数从 17 增至 25。该生产探测未连接 BLE、未启用上报；它证明真实后台的持续采样，不能证明最终 PCB 控制效果。

开发板的 BLE 只读验收保存在 `SmartFan.SelfTest/ble-readonly-audit.json`。这个结果与进程集成测试分别报告；不能把假 BLE 的通过结果计入真实硬件验收。UI 关闭后台继续运行的进程测试，也不替代最终 PCB 的风扇转速与灯效物理测试。

2026-10-17 本轮 WPF 回归通过 311 项检查，新增显式 `Metrics` 契约、电脑内置风扇摘要、日志窗口生命周期、传感器窗口首次自动读取、字体及圆角布局检查。较早的扫描绑定修复版本已完成 C 盘前端两轮实际 BLE 扫描和日志目录入口验收；既有后台未重启。历史证据见 [SCAN-BINDING-FIX.md](SCAN-BINDING-FIX.md)。本轮发布输出已生成，但上述离线结果和历史扫描记录不代表当前发布包已完成最终部署、桌面视觉、GATT 写入或最终 PCB 控制验收。

构建与测试使用 Windows .NET 10 SDK。用户已授权 Windows 项目还原、构建、测试和发布；ESP32 驱动及固件保持原样。

2026-10-19 CPU 指标与跨权限 IPC 修复版本通过 939 项 WPF、210 项服务、60 项 IPC 检查，发布部署的六个主要文件哈希一致。实机已确认普通前端启动并连接管理员后台，CPU 指标随样本变化，关闭前端后同一后台继续采样；12:16 重新打开的普通前端复用原后台 PID 32716，样本数由关闭时 57 增至 119，没有再次启动后台或请求 UAC。传感器窗口实际显示 234/234 有效项和逐核心频率，未测试两秒实时刷新开关或筛选。本次 ESP 为 Disconnected、遥测关闭，前后后台配置相同，没有设备写入。证据汇总见 [CPU-METRICS-FIX-2026-10-19.md](CPU-METRICS-FIX-2026-10-19.md)。
