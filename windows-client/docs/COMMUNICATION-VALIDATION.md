# Windows 后台与 ESP32-S3 通信验证

验证时间：2026-10-16 20:56:56～20:56:58（Asia/Shanghai）。

结论：已使用真实开发板验证 PC 发包、ESP GATT 接收并更新数据、远端读取返回原始字节的完整链路。本次没有修改、构建或烧录 ESP 固件；没有更改风扇、灯光配置或 Flash。

## 实际链路

`CommunicationCheck → BackgroundClient（当前用户命名管道）→ 已运行的 SmartFan.Agent → Windows 原生 WinRT BLE → ESP32-S3 GATT → Uncached READ → 字节比较`

复用原有独立后台 PID `12048`，没有创建第二个 Native BLE 客户端。后台 BootId 为 `89402fe26c684f4e84af66ff7a5338fb`，使用配置 `G:\ESP32\LaptopCooler\windows-client\.local-data\agent.json`。测试前确认演示模式、自动上报、自动配置同步均关闭，避免与真实采样或场景应用竞争。

目标开发板地址：`84:C7:BB:56:6A:16`。实际 MTU 为 `256`；发现并校验 4 个服务、27 个特征。`laptop_svc` 的 Uncached 服务发现曾返回 `HRESULT 0x80070016`，Cached 后备策略成功；后续数据读取仍明确使用 `BluetoothCacheMode.Uncached`，没有把 PC 本地缓存当成接收证据。

## 发包与接收结果

使用 `laptop_cpu_temp` 特征：`00706d65-745f-7570-635f-706f7470616c`，格式为小端 binary64（8 字节）。测试值是明确标注的诊断载荷，**不是实际 CPU 温度采样**。

| 步骤 | 数值 | 发送 8 字节十六进制 | ESP 远端读回 | 结果 |
|---|---:|---|---|---|
| 读取原值 | 0 | — | `0000000000000000` | 已保存 |
| 诊断写入 1 | 67.3125 | `0000000000D45040` | `0000000000D45040` | 完全一致 |
| 诊断写入 2 | 42.15625 | `0000000000144540` | `0000000000144540` | 完全一致 |
| 诊断写入 3 | 73.0625 | `0000000000445240` | `0000000000445240` | 完全一致 |
| 恢复原值 | 0 | `0000000000000000` | `0000000000000000` | 恢复成功 |

每次写入均使用有应答 WRITE；Core 层立即读回校验后，验证工具再执行一次独立远端 READ，并再次比较全部 8 字节。三个不同的非默认值按顺序改变、准确返回，能够证明真实 ESP GATT 接口已经接收并保存该载荷，而不是仅显示“发送成功”。

测试开始时后台未连接设备；结束后已主动断开，恢复原先连接状态。硬件采样后台继续运行。

## 串口检查

| 串口 | 识别结果 |
|---|---|
| COM5 / COM6 | Windows 蓝牙虚拟串口，与 ESP USB 日志无关 |
| COM7 | USB 串行设备，VID `303A` / PID `1001`，ESP32-S3 USB Serial/JTAG |

以 115200 波特率、DTR/RTS 关闭读取 COM7 约 6 秒，没有收到输出。当前 `laptop_sys_callback` 成功分支也没有逐包串口日志，因此本次**未获得 UART 接收日志**。接收结论来自上述真实远端 GATT 读回，不把空串口日志计为通过。既然已能验证实际收包，无需为日志额外更换用户原有固件。

## 证据与复现

- `artifacts/hardware-validation/communication-check.json`：后台身份、配置路径、原值、发送/接收字节、恢复结果、时序事件和最终状态。
- `artifacts/hardware-validation/agent-before.json`：测试前后台状态。
- `artifacts/hardware-validation/serial-before.log`：串口原始读取结果（0 字节，未伪造输出）。
- `artifacts/hardware-validation/CommunicationCheck/`：完整验证程序源码，可使用 `dotnet run --project artifacts/hardware-validation/CommunicationCheck -- artifacts/hardware-validation/communication-check.json` 复现。

验证程序在 `finally` 中恢复读取到的原始值；若测试前自动上报、自动场景同步或演示模式开启，会明确拒绝运行，不擅自更改这些偏好。

## 验证范围

已确认后台代理、命名管道、原生 BLE、服务发现、标量 double 编码、ESP 接收缓存、远端读取和恢复流程正常。本次结果不代表真实风扇测速、PWM 输出、灯珠效果、OLED 显示、自动温控曲线、失联 10 秒后的固件策略或掉电持久化已通过。之前只读接口自检发现的固件不一致仍保留，未借此修改驱动。
