# CPU 温度采集：状态、依赖和本机验收

本文保留 2026-10-17～18 的来源排查过程。2026-10-19 已安装官方 PawnIO 并修复后台权限和 IPC，标准 CPU Package、逐核心频率及核心 VID 均取得真实读数；当前状态见 [最新 CPU 修复与实机记录](CPU-METRICS-FIX-2026-10-19.md)。

## 2026-10-17 本机结论：已增加并验证 ASUS 只读来源

最初的 LibreHardwareMonitor（LHM）链路因缺少 PawnIO，无法读取 CPU 温度。后续调查 ASUS 固件接口，已补齐本机专用的只读来源，**完成真实采样、发布版常驻后台及主页显示验收，并通过 CPU / GPU 温度向 ESP 写入、读回及恢复验证**。本机无需先安装 PawnIO 即可使用这个 CPU 温度来源。

## 最终发布与 ESP 通信验收

最终 GUI 和后台部署在 `%LOCALAPPDATA%\SmartFanNext\app`，受控重启后的后台 PID 为 `32080`。主页及后台实际显示 CPU **66 °C**、三颗电脑内置风扇 **5400 / 3100 / 4900 RPM**；CPU 温度状态为 `Available`，来源明确为 `ASUS EC CPU Temperature`。

2026-10-17 **13:04:41–13:04:43**，临时 C 盘诊断程序仅附着已有后台，在同一 BLE 连接（设备 `84:C7:BB:56:6A:16`，ConnectionGeneration `1`）完成以下测试：

| 温度接口 | 当前真实值 | 发送与读回的 double 小端 HEX | 恢复后的原始 HEX |
| --- | ---: | --- | --- |
| `laptop_cpu_temp` | 66 °C | `0000000000805040` | `0000000000000000` |
| `laptop_gpu_temp` | 49.28125 °C | `0000000000A44840` | `0000000000000000` |

两个接口均返回 `Verified`，独立读回与发送字节完全一致；最后在 `finally` 中恢复试验前的两个原始 8 字节值，并再次读回确认一致。报告为 **`PassedAndRestored`**，`RestorationVerified=true`，没有错误。

原始证据：`%LOCALAPPDATA%\SmartFanNext\diagnostics\temperature-roundtrip-20261017-130441.json`。试验时自动遥测为关闭状态，没有更改用户配置，也没有与常驻自动上报竞争；这验证了当前真实温度经后台到 ESP 的接口闭环，不等于长期自动上报或固件温控策略已经完成验收。

## ASUS 来源的隔离采样证据

本机 `ASUS ROG Strix G16 G615LRG` 的 `\\.\ATKACPI` 设备可由当前普通用户打开，DSTS 只读状态查询取得：

| 时间 | ASUS EC CPU 温度 | CPU 风扇 | GPU 风扇 | Middle 风扇 |
| --- | ---: | ---: | ---: | ---: |
| 12:43:38 | 78 °C | 5400 RPM | 4100 RPM | 4900 RPM |
| 12:43:40 | 75 °C | 5400 RPM | 4100 RPM | 4900 RPM |
| 12:43:42 | 76 °C | 5400 RPM | 4100 RPM | 4900 RPM |
| 12:43:44 | 75 °C | 5400 RPM | 4100 RPM | 4900 RPM |
| 12:43:46 | 77 °C | 5400 RPM | 4100 RPM | 4900 RPM |

集成后的 `HardwareMonitorService` 在 12:48:40 返回 CPU 温度 **83 °C**、LHM GPU 温度 **58.19 °C**、`FanPresent=[1,1,1,0,0]`、`FanRpm=[5400,4100,6000,null,null]`。温度会随负载变化；来源明确命名为 **ASUS EC CPU Temperature**，不冒充 CPU Package。

原始只读查询报告：`%LOCALAPPDATA%\SmartFanNext\diagnostics\asus-readonly-20261017.json`；集成采样报告：`hardware-asus-20261017.txt`。`tools/Test-AsusReadOnlySensors.ps1` 可重复查询这四个白名单端点。

实现只在已验证的 ASUS G615LRG 机型启用，LHM 仍优先选取有效 CPU 温度；缺失才使用 ASUS EC CPU 温度。GPU 保持原来的 LHM 来源。CPU / GPU / Middle 风扇使用固定 0 / 1 / 2 槽位，单位为端点低 16 位 × 100 RPM。零转速可以表示真实停转；不支持标志、异常范围和查询错误保留为不可用。已观察到的风扇在后续读取错误时保留存在状态、RPM 为 `null`；首次不能确认全部槽位时不自动上报存在数组。[G-Helper ASUS 端点与状态实现](https://github.com/seerge/g-helper/blob/main/app/AsusACPI.cs)、[温度读取与 RPM 单位](https://github.com/seerge/g-helper/blob/main/app/HardwareControl.cs)。

本机 WMI 类 `root\wmi:AsusAtkWmi_WMNB` 确实存在，但当前上下文枚举实例被拒绝；直接 ASUS ACPI 设备的只读 DSTS 查询成功。未调用设置命令、初始化命令、看门狗命令或通用 ACPI 热区替代接口。

## 最初 LHM 链路的真实缺口

隔离运行 `SmartFan.Services.Tests --hardware` 的结果：

- CPU 实际识别为 `Intel Core Ultra 9 290HX Plus`，总计枚举 162 个传感器。
- CPU Package、Core Max、P-Core / E-Core 及到 TjMax 差值共 51 个 CPU 温度传感器全部为 `null`；CPU 时钟和电压也无读数。
- NVIDIA GPU 温度有正常读数，说明总体采样、进程间传输之外的 CPU 驱动访问问题需要单独处理。
- LibreHardwareMonitor 实际版本 `0.9.6+3d331e3370efb858411f19511373eff65a218701`。
- 采样进程管理员权限为 `False`；未发现 PawnIO 安装记录或驱动服务。
- 打开 LHM 使用的 `\\?\GLOBALROOT\Device\PawnIO` 失败，Windows 错误 `2`（设备不存在）；结构化原因是 `DriverMissing`，并非“CPU 不支持”。
- 原始采样报告：`%LOCALAPPDATA%\SmartFanNext\diagnostics\hardware-20261017.txt`。

LHM 0.9.6 官方源码已经识别 Arrow Lake 型号 `0xC5 / 0xC6`，通过 PawnIO 读取 Intel MSR，传感器名字本身也已经正确枚举。因此，升级未知预发布版或反复手动换传感器不能解决目前已确认的缺驱动问题。[Intel CPU 实现](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/v0.9.6/LibreHardwareMonitorLib/Hardware/Cpu/IntelCpu.cs#L204)、[Intel MSR 访问](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/v0.9.6/LibreHardwareMonitorLib/PawnIo/IntelMsr.cs#L7)、[PawnIO 设备加载](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/v0.9.6/LibreHardwareMonitorLib/PawnIo/PawnIo.cs#L59)。

## 其他机型使用通用 LHM 来源时如何验收

当前已验证 ASUS 机型可以直接使用上面的原厂固件读取路线。以下安装步骤针对需要恢复 LHM CPU 温度、时钟或电压的场景，不是本机显示 CPU 温度的必备步骤。

1. 在 [PawnIO 官方网站](https://pawnio.eu/) 使用 Download Now 获取官方签名版本，完成系统安装。SmartFan 不会自动安装内核驱动。
2. 停止并重新启动 SmartFan **后台**。如果诊断为访问被拒绝，再以管理员身份启动后台；仅提升控制面板不会改变已经运行的后台权限。LHM 官方也说明部分传感器需要管理员权限。[LHM 权限说明](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/v0.9.6/README.md#administrator-rights)。
3. 在高级设置查看后台权限、PawnIO 安装版本和具体 CPU 温度诊断。安装信息每 30 秒刷新，LHM 的实际驱动会话需要重启后台重建。
4. 确认 `CpuTemperatureStatus.Code=Available`、真实 CPU 温度连续更新，并记录对应传感器 ID。优先使用 CPU Package，接着使用 AMD Tdie、Core Max 等可信绝对温度。
5. 连接 ESP 并启用 CPU 温度上报后，验证 `laptop_cpu_temp` 的真实接收和读回；这一步才是端到端验收。安装或读取仍失败时，保留诊断和后台日志，不把未知值填成 0。

本次未安装驱动、修改系统安全设置或修改固件。隔离诊断后，最终发布阶段由主任务受控重启 GUI 和后台，并完成上面的主页及温度接口验收。

## 新增诊断契约

`TelemetrySnapshot` 新增以下字段，旧 JSON 缺少字段时兼容：

- `CpuTemperatureStatus.Code`：`NotSampled`、`Available`、`DriverMissing`、`DriverUnavailable`、`PermissionRequired`、`InitializationFailed`、`NoCpuSensors`、`SelectedSensorUnavailable`、`NoSupportedSensor`、`SensorReadFailed`。
- `CpuTemperatureStatus.Message`：面向用户的原因和下一步；`SensorId`：实际选择或指定的传感器。
- `HardwareMonitor`：`ProviderVersion`、`IsAdministrator`、`PawnIoInstalled`、`PawnIoVersion`、`PawnIoDeviceAccessible`、`PawnIoDeviceErrorCode`。设备预检仅打开并立即关闭句柄，不发送驱动命令。
- `VendorSensorStatus`：ASUS 机型匹配与只读采样状态。`SensorNames["CPU 温度"]` 和 `CpuTemperatureStatus.Message` 明确实际来源；`SensorDescriptor.FanSlot` 定义已经验证的稳定风扇槽位。

真实有效读数优先于安装元数据，缺失驱动、访问拒绝、库初始化失败、失效手动选择、已枚举但未返回有效值分别诊断；错误状态只在变化时写入后台日志。

温度选择器支持新版 P-Core / E-Core 名称，并始终排除 `Distance to TjMax` 差值，防止把温差当成绝对温度。ASUS 固件提供的 CPU 专用温度与 CPU Package 分别命名；主板通用 ACPI 热区不作为 CPU 温度的替代。全部真实来源缺失时保持 `null`，不上报；正常 GPU 数据仍可独立上报。

## 回归验证

`dotnet run --project windows-client/SmartFan.Services.Tests -c VendorVerification`：101 条断言通过，覆盖混合核心回退、差值排除、非有限值过滤、驱动/权限/传感器失败分类、旧 JSON 兼容、ASUS 型号门控、状态标志和范围、零 RPM、标准 LHM 读数优先、固定风扇槽位及 CPU 缺失时不伪造遥测。

可用以下命令重新生成原始传感器和快照报告；请从已验证可以访问本地系统的 C 盘运行环境执行实际诊断：

```powershell
dotnet run --project windows-client/SmartFan.Services.Tests -c VendorVerification -- --hardware
```
