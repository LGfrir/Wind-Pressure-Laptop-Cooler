# 硬件遥测来源与兼容范围

本轮将 CPU、Windows 系统指标、NVIDIA GPU 和已验证的厂商 EC 分成独立来源，再按指标选择有效读数。目标是让更多笔记本能够提供实际可读的数据；支持某一 CPU 架构或 GPU API，不表示每个品牌的温度、电压和风扇都能读取。

当前发布目标为 **Windows 10 build 19041 及以上 / Windows 11，x64**。没有承诺 Windows on Arm、其他操作系统或所有笔记本型号的兼容性。GPU 功能本轮仅支持 NVIDIA；没有 NVIDIA GPU 时，CPU 使用率、系统 RAM 和可用的 CPU 硬件来源仍独立运行，GPU 项显示不可用。

## 指标怎样选择来源

| 指标 | 主来源 | 后备与语义 |
| --- | --- | --- |
| CPU 温度 | LibreHardwareMonitor 0.9.6 的有效 CPU 封装/核心温度 | 仅已验证 ASUS G615LRG 提供只读 EC 后备。其他型号没有通用 EC 偏移或温度换算猜测；读不到就显示原因。 |
| CPU 使用率 | LHM 的 CPU Total/Total | Windows 英文 PDH `Processor Information(_Total)\% Processor Time`；PDH 不可用时，只有全机逻辑处理器数不超过 64 才使用 GetSystemTimes。保留真实 0%。 |
| CPU 硬件核心频率 | LHM 支持的有效逐核心频率 | 同一 CPU 封装内选择最高有效核心值；排除 Bus Speed、Average 和 Effective 名称，避免混用不同语义。硬件源不可用时可显示下面的 Windows 报告频率。 |
| CPU 电压 | 来源明确的 CPU 核心供电遥测 | 请求电压归类为 VID，明确显示“CPU 核心 VID”。VID 不等于实测 VCore，只在本机显示，不作为实测电压上报 ESP。没有可用来源时保持不可用。 |
| 系统物理内存 | Windows GlobalMemoryStatusEx | LHM 同一内存设备的 Memory Used + Memory Available；不把进程内存、提交内存或 GPU 共享内存当作系统物理 RAM。 |
| NVIDIA 温度、使用率、图形核心频率、显存 | 已证明同一物理卡身份、当次读取成功的独立 NVML 指标 | 同一卡的 LHM NVIDIA/NVAPI 为后备。每项选择独立进行，单项失败不要求其他项一起不可用；用户明确指定的温度传感器仍优先。 |
| 笔记本内置风扇 | LHM 可提供的 Fan 传感器，或已验证 ASUS EC 槽位 | 不承诺各厂商都开放 RPM，也不根据 PWM 或风扇占用率虚构转速。电脑内置风扇与 ESP32 底座转速分别显示。 |

Intel/AMD CPU 的温度、硬件频率、电压和功耗依赖 LHM 对对应硬件的实现及 PawnIO 访问条件。PawnIO 未安装或后台不能访问时，这些受保护的硬件读数会标为不可用；可通过 Windows 用户态接口取得的 CPU 使用率和 RAM 不因此关闭。上游具体实现见 [LHM IntelCpu](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/v0.9.6/LibreHardwareMonitorLib/Hardware/Cpu/IntelCpu.cs) 与 [LHM Amd17Cpu](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/v0.9.6/LibreHardwareMonitorLib/Hardware/Cpu/Amd17Cpu.cs)。

`CpuClockKind.HardwareCore` 表示 LHM 核心时钟条目的报告值，不是应用取得了逐次 MSR 成功证明。LHM 的部分 Intel/旧 AMD 路径在单次 MSR 失败时可能返回 TSC 频率，公共 Sensor API 不暴露这次回退；详情提示已明确这一限制。现有核心时钟上报保留原协议行为，不能据此声称每个样本均为直接 MSR 实测；WindowsReported 则单独标识并禁止作为该硬件时钟上报。

## Windows CPU 来源的准确含义

Windows 来源持有一个 PDH query，使用 [PdhAddEnglishCounterW](https://learn.microsoft.com/en-us/windows/win32/api/pdh/nf-pdh-pdhaddenglishcounterw) 添加英文计数器，避免依赖 Windows 显示语言。约每秒采样一次，不通过循环或 sleep 等待结果。区间计数器先预热；调用成功还必须检查每个值的 CStatus，缺失实例或无效区间不能成为有效读数。[PDH 格式与预热要求](https://learn.microsoft.com/en-us/windows/win32/api/pdh/nf-pdh-pdhgetformattedcountervalue)及[数据状态说明](https://learn.microsoft.com/en-us/windows/win32/perfctrs/checking-pdh-interface-return-values)。

CPU 使用率的后备表示忙碌时间百分比。GetSystemTimes 以相邻区间的 kernel + user 为总量，扣除 idle；kernel 已包含 idle，不能重复相加。微软说明超过 64 个逻辑处理器时，该 API 只返回调用线程主处理器组的统计，因此本实现不把它用于这类机器的全机后备。CPU 使用率在这种情况下依赖有效 PDH 数据，否则不可用。[GetSystemTimes](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-getsystemtimes)。忙碌时间百分比与任务管理器采用的性能加权 Processor Utility 语义有差异，不能要求数值逐帧相同。[微软对两类计数器的说明](https://learn.microsoft.com/en-us/troubleshoot/windows-client/performance/cpu-usage-exceeds-100)。

`/windows/cpu/clock/reported` 的名称为 **Windows CPU Reported Frequency**，界面显示“Windows 报告频率”。它只读取 PDH `Processor Information(*)\Processor Frequency` 原始 MHz，并对所有逻辑处理器叶实例求平均。必须与全机活动逻辑处理器数量一致、实例不重复且每个值均为有效正数；缺失、零值或部分覆盖返回不可用，不能以少数可读核心冒充全机平均。

这个值可能是系统提供的标称或请求频率，**不等于实测最高核心频率，也不是包含空闲时间的 Effective Clock**。它只供本机显示，TelemetryUploader 不将 WindowsReported 作为硬件核心频率发给 ESP。实际硬件核心来源仍优先，来源变化时界面标题和说明跟随变化。

`% Processor Performance` 只保留为原始诊断，不乘到 Processor Frequency 上。已报告 4200 MHz、性能为 150% 时，仍只报告 4200 MHz，不能因此生成 6300 MHz。没有 Microsoft primary 契约可保证这里的 Processor Frequency 就是性能百分比对应的 nominal MHz；PEP 本身也区分频率反馈与平台特定单位的性能反馈，不能据此建立通用换算。[PEP 反馈类型](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/pep_x/ns-pep_x-_pep_processor_feedback_counter)及[性能能力单位](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/pepfx/ns-pepfx-_pep_ppm_query_perf_capabilities)。

## RAM 的单位和失败处理

GlobalMemoryStatusEx 返回物理内存总量和可用量，已用量为 total − available。只有 total > 0 且 available ≤ total 才接受这一组结果；失败后再尝试 LHM 同一设备的 used + available，不将两个来源的一半拼在一起。Windows API 返回字节，采集层统一换算为 MiB；LHM Data 的 GiB 按 1024 换为 MiB，SmallData 保持 MiB。[GlobalMemoryStatusEx](https://learn.microsoft.com/en-us/windows/win32/api/sysinfoapi/nf-sysinfoapi-globalmemorystatusex)。

显存与系统内存分别保留，不混为同一容量。ESP 的 RAM/VRAM 协议单位仍需按 [固件接口清单](FIRMWARE-ALIGNMENT.md)确认，默认不发送这四项；本机正确显示容量不表示已完成固件单位对齐。

## NVIDIA 的独立来源和同卡匹配

NVML 直接调用已安装 NVIDIA 驱动提供的库。只从 Windows System32 和 Program Files 下的 NVIDIA Corporation/NVSMI 以完整路径查找 `nvml.dll`，依赖搜索范围受限；不使用当前目录、应用目录或 PATH 中来历不明的同名 DLL。标准与 DCH 驱动位置及 GeForce/RTX 支持限制见 [NVIDIA NVML 官方文档](https://docs.nvidia.com/deploy/nvml-api/nvml-api-reference.html)。

NVML 与 LHM/NVAPI 的枚举下标、名称相似或显卡数量相同，都不能证明是同一块卡。只有完整、唯一匹配的 PCI domain/bus/device/function、device ID 和 subsystem ID 才建立跨 API 别名；身份不明、重复、PnP 格式无法识别或设备枚举不完整时，保留独立身份，不跨卡拼接温度、负载、时钟和显存。

默认选择下，已匹配同一卡的 NVML 当次有效指标优先，缺失项再考虑该卡的 LHM 来源。这样能优先采用带明确单次调用结果的 NVML 读数；LHM/NVAPI 在部分 API 失败时可能保留先前 Value，因此后备来源仍会标明，不能把它宣称为 NVML 的当次成功样本。用户指定的 GPU/温度传感器不在消失后偷偷换成另一块卡。

温度、负载、图形核心 MHz、显存分别检查调用结果。NotSupported、缺少可选导出函数、权限不足或无数据，只让相应项为 null，不用 0 假装成功，也不因一个能力缺失停止全卡其他指标。驱动未就绪、GPU 丢失或需要重置时清理失效状态并延后初始化/枚举；正常采样约每秒一次，避免错误重试忙循环。退避针对初始化和设备恢复，单项 NotSupported 不会触发全卡反复重置。

本轮不实现 Intel/AMD GPU 的通用 PDH、WMI 或厂商采集。无 NVIDIA 或驱动不提供某个接口时，只关闭相关 GPU 能力，CPU/RAM 来源继续采样。

## 温度、电压与内置风扇的厂商边界

目前只有 **ASUS G615LRG** 的指定 EC 端点经过本机验证，提供 CPU 温度及 CPU/GPU/Middle 三颗内置风扇的只读后备。该来源不包含控制写入；不会把端点偏移推广到所有 ASUS 或其他品牌。其他笔记本依赖 LHM 已支持的硬件或厂商后续经验证的接口，缺失时明确显示不可用。

VID 是 CPU 请求的电压，不是主板实测供电；有效 VID 可以本机查看，但不会作为 VCore 上报。WMI `Win32_Processor.CurrentVoltage` 来自 SMBIOS 的静态信息，也不能当成通用实时 VCore 渠道。[Win32_Processor 属性来源](https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-processor)。

`fan_used[5]` 仍是电脑内置风扇槽位的 0/1 存在标志，不是利用率。安装但暂时读不到 RPM 的风扇保持不可用，不能补零冒充停转；ESP 底座转速由设备通知单独提供。

需要 PawnIO 时，从 [PawnIO 官网](https://pawnio.eu/)提供的官方数字签名发行版安装，由用户明确处理 Windows 权限提示。普通前端可连接管理员后台；硬件采样权限属于后台。不会用关闭驱动签名、改变系统安全策略或开放设备权限来声称获得兼容性。没有 PawnIO 不影响可用的系统 CPU/RAM 和独立 NVIDIA 用户态接口。

## 已覆盖的离线场景与实机范围

| 场景 | 覆盖层级 | 不能据此承诺的部分 |
| --- | --- | --- |
| Intel 混合 P/E 核、旧版编号/单核名称 | 传感器名称、核心频率选择、VID/VCore 语义 fixtures；当前 ASUS Intel 混合核机有已有硬件记录 | 尚未完成其他 Intel 笔记本的逐项实测及各品牌 EC/RPM 验证。 |
| AMD Family 10h / 17h 等 LHM 名称与供电遥测 | 温度别名、原始核心频率与 Effective 排除、VID/SVI2、PawnIO 缺失 fixtures | 尚无 AMD 实物笔记本的温度、频率、电压和风扇验收记录。 |
| Windows CPU 后备与 RAM 来源选择 | 首样本、0% 使用率、计数回退、完整 LP 平均、>64 边界、无效值及单位 fixtures | 尚无 >64 LP 实机记录，也未覆盖全部 Windows 语言和电源管理平台。 |
| NVIDIA NVML、同卡后备和缺失能力 | 可注入 API/身份 fixtures，包括单项失败、身份不完整/冲突、驱动恢复与缺少 NVIDIA | 尚未覆盖每款 GeForce/RTX，以及全部热插拔、休眠恢复的实机行为。 |
| ASUS G615LRG 的既有传感器 | 当前唯一已记录的笔记本实机：Intel CPU + NVIDIA GPU，LHM/PawnIO 与指定只读 EC 有历史证据；新增普通权限 NVML/PDH 通道已有本轮实测 | 不推广同一 EC 接口到其他型号/品牌；其他机器仍需独立验收。 |

已有本机结果、时间及权限环境见 [2026-10-19 CPU 指标修复记录](CPU-METRICS-FIX-2026-10-19.md)。它记录的是该次版本的实测，不能替代本轮新增来源的现场验证。

2026-10-19 本轮 Release 构建为 0 错误、0 警告，286 条服务断言、955 条离线 WPF 断言、60 条进程间通信断言通过。新增同秒 PnP 映射变化及移除的缓存失效回归已包含在服务测试中。界面断言是离线检查，不等于实际桌面呈现帧数或实机传感器支持率。

19:09 的最终源码普通权限探针实际读到 24/24 个逻辑处理器的 Windows 频率计数器（1800–2700 MHz，全机报告平均 2100 MHz），CPU 忙碌时间约 6.43%。NVIDIA NVML 独立枚举 1 张显卡，5/5 项有效：核心温度 57°C、负载 22%、图形核心 1590 MHz、已用显存 1388.28 MiB、总显存 12227 MiB。这些是当时样本，不是固定预设值；该探针不创建 LHM/MSR 采集器，不读取或更改生产配置，也不连接 ESP。

该独立探针没有传入 LHM PnP 节点，因此显示 0/1 个跨 API 身份匹配是预期结果；同卡匹配由随后的生产后台单独验证。证据位于 `artifacts/compatibility-20261019/`，包含构建、三类测试、最终平台探针及发布文件哈希核验。

19:09–19:14 运行版验收：管理员后台 PID 3744 正常读取 CPU Package、LHM 核心时钟与 VID，Windows 备用数据也保留可见；NVIDIA NVML 5/5 项有效，1/1 张卡通过 PCI 证据匹配到 `/gpu-nvidia/0`，主页 GPU 温度、使用率、频率和显存均采用这一来源。实际传感器窗口完成读取，显示 222/222 项有效；本轮没有验证该窗口的实时刷新开关或筛选操作。

关闭前端后，后台仍为同一 PID/BootId，样本由 62 增至 177；重新打开前端后增至 223，没有重新创建后台，首页仍显示原有组件与实时数据。审计中的 `configuration` 与更新前完全相同，ESP 保持断开且上报关闭。六个部署 exe/dll 与发布目录的 SHA-256 核对一致。当前程序已留在运行概览；这些结果只代表本机，不扩展为其他 Intel/AMD 笔记本、热插拔或全部 GPU 睡眠行为已实测。

## 在另一台笔记本上检查

先使用普通前端连接后台，在传感器窗口查看每项的来源、实际名称、单位、ReadStatus 和后台采样时间，再判断是传感器缺失、驱动未安装、后台权限不足，还是特定接口不支持。查看日志应使用界面显示的实际目录，不能只根据工具进程自己的 AppData 视图推断。

CPU 缺失不要求安装 NVIDIA 工具；GPU 缺失也不要求重装 CPU 驱动。需要报告兼容性问题时，保留机型、CPU/GPU 型号、Windows build、LHM/PawnIO 和显卡驱动版本，以及脱敏后的来源状态。真实故障应记录缺失项和原因，不通过演示数据、静态 SMBIOS 值或未经验证的换算填满界面。
