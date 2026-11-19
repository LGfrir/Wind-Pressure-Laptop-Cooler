# 2026-10-17 传感器逐项核对

首轮实测时间：23:21（北京时间）。从运行中的后台读取原始传感器清单、最近快照和实际后台配置；未新建第二个硬件采样器，未启用演示，未写入 ESP 配置。数值是当时的瞬时值，原始枚举与周期快照取样时间不同，变化少量数值属于正常现象。

实际识别的设备是 Intel Core Ultra 9 290HX Plus、NVIDIA GeForce RTX 5070 Ti Laptop GPU。CPU 名称来自采集库；没有按早先聊天中的型号硬编码。

| 参数 | 本机快照实测 | 采集源 / 结论 |
| --- | --- | --- |
| CPU 使用率 | 13.52% | LHM `CPU Total`，`/intelcpu/0/load/0`，正常；不是单个核心的占用率 |
| CPU 温度 | 89°C | ASUS EC `/asus-ec/cpu/temperature`，正常；不冒称 CPU Package 温度 |
| CPU 电压 | 无读数 | 枚举结果没有有效的 CPU Voltage 项，PawnIO 亦不可访问；不能用 GPU 电压或编造的默认值代替 |
| CPU 核心频率 | 无读数 | 已枚举到 24 个 P/E 核心频率项，但原始值全部为空；没有选错名称，也没有把标称频率冒充实时频率 |
| GPU 使用率 | 26% | NVIDIA `GPU Core`，`/gpu-nvidia/0/load/0`，正常；默认选择独显 |
| GPU 温度 | 66°C | NVIDIA `GPU Core`，`/gpu-nvidia/0/temperature/0`，正常；没有使用显存结温 |
| GPU 频率 | 1380 MHz | NVIDIA `GPU Core`，`/gpu-nvidia/0/clock/0`，正常；不是 GPU Memory 时钟 |
| 独显已用显存 | 4944 MiB | NVIDIA `GPU Memory Used`，`/gpu-nvidia/0/smalldata/1`，正常 |
| 独显总显存 | 12227 MiB | NVIDIA `GPU Memory Total`，`/gpu-nvidia/0/smalldata/2`，正常；没有用共享内存作为独显总显存 |
| 系统已用内存 | 20801.81 MiB | Windows `GlobalMemoryStatusEx`，正常 |
| 系统总内存 | 32129.29 MiB | Windows `GlobalMemoryStatusEx`，正常；显示的是 OS 可用物理容量 |
| 内置风扇存在标志 | 1,1,1,0,0 | 当前后台手动数量为 3；ASUS EC 也检测到了三个固定槽位 |
| 内置风扇 RPM | 6700,4200,6700 | 分别来自 ASUS EC CPU、GPU、Middle；后两个未安装槽位保持不可用，仅协议要求时填 0 |

13 项中，11 项本机数据可用；CPU 电压和 CPU 核心频率尚未读到。标准 LHM 的 CPU Package、各核心温度也为空，不过 ASUS EC 已为总览温度提供独立后备读数。硬件环境显示后台为普通权限、PawnIO 未安装、驱动句柄打开失败（Windows 错误 2）。单独把前端提升为管理员不会让后台获得驱动，也不能证明电压一定会在安装后支持；安装后必须重新核对。

另发现 LHM 的四项 CPU Power 在无 PawnIO 端点时返回了 0。对应 LHM 0.9.6 的 Intel MSR 采集依赖 PawnIO，这些 0 不能证明 CPU 实际功耗为零。本轮把该条件下的 Intel 温度、频率、功耗、电压标为不可读，附上驱动原因；真实的 0% CPU 占用、独显读数和 ASUS EC 读数保留。原始传感器窗口增加「状态 / 缺失原因」列。

主项目依赖的 [LHM 0.9.6 IntelCpu 源码](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/v0.9.6/LibreHardwareMonitorLib/Hardware/Cpu/IntelCpu.cs) 可核对 IntelMsr/PawnIO 依赖；[官方 README](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor#developer-information) 也说明部分传感器需要管理员权限。未自行安装驱动、降低安全设置或改变厂商 EC 控制。

## 本机读到与 ESP 接收到要分开核对

当前真实配置启用了上报，但本次启动未自动连接 ESP。除此之外：

- RAM/VRAM 的协议单位仍为 `Unspecified`，四项内存指标会被上报层明确跳过；本机 UI 的 GiB/MiB 显示正常。这是尚未确认的固件单位，不是传感器读取失败。
- `laptop_fan_used` 和 `laptop_fan_speed` 的上报开关目前关闭，本机仍正常采样。
- 无读数的 CPU 电压和频率不发送，避免用 0 伪装为测量结果。
- 散热底座的 `fan_rpm` 是 ESP 通知，与电脑内置风扇来源独立。本次没有以未完成的底座测速验收电脑风扇。

复查命令（只读、通过已有后台）：

```powershell
& "$env:LOCALAPPDATA\SmartFanNext\app\SmartFan.Agent.exe" --sensor-audit --output '.\sensor-audit.json'
```

输出包含原始 `sensors`、最近 `status.Snapshot`、实际 `configuration`。文件包含本机硬件和设备配置，留在本地即可。调试证据位于 `artifacts/widget-qa/sensor-audit.json`；后续的驱动原因验证另存 `sensor-audit-final.json`。

23:39 在最终部署版本重新读取：166 个原始传感器中 87 项有有效值；CPU 温度 87°C、GPU 温度约 62.72°C、GPU 核心频率 1410 MHz，三颗电脑风扇为 6300 / 4200 / 6100 RPM。四项 CPU Power 已显示空值和明确的 PawnIO 缺失原因。CPU 频率与电压仍为空，未用其他指标替代。前后端从 C 盘本地运行目录启动，部署 DLL 与发布包 SHA-256 一致。
