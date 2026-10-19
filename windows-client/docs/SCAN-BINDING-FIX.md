# 2026-10-17：扫描结果列表的只读属性绑定异常

## 已核实的现场

交接记录在仓库根目录的 `docs/hardware-debug-notes.md`。本记录仅处理 Windows 端；不调整固件、串口控制台、命名管道安全设置或 Windows 信任区域。

实际前端日志：

```text
C:\Users\zen\AppData\Roaming\SmartFanNext\frontend-logs\smartfan-20261017.jsonl
```

截至本轮开始读取时，该文件有 30 条记录。其中第 10～27 行为 18 次相同的界面异常：首条时间 `2026-10-17 07:43:11.5881617 +08:00`，末条时间 `07:43:11.7798149 +08:00`。

```text
System.Windows.Markup.XamlParseException:
无法对“SmartFan.Core.Ble.BleDeviceInfo”类型的只读属性“Address”
进行 TwoWay 或 OneWayToSource 绑定。
  -> System.InvalidOperationException
  -> MS.Internal.Data.PropertyPathWorker.CheckReadOnly
  -> BindingExpression.Activate / FrameworkTemplate.LoadTemplateXaml
  -> VirtualizingStackPanel.MeasureChild
```

扫描从 `07:43:05.5457` 开始，在 `07:43:11.5775` 结束，紧接着在结果项创建和布局时触发异常。因此这里的直接故障是扫描结果模板的绑定方向，不是没有完成 BLE 扫描。

修复前还使用 C 盘旧版前端实际点击了一次扫描，在 `07:50:19` 再次复现同类问题并新增 19 条错误。因此当天日志保留了共 37 条修复前错误；后面的验收按新版启动时间划分，不用全天总错误数判断修复结果。

后台日志位于：

```text
C:\Users\zen\AppData\Roaming\SmartFanNext\logs\agent-20261017.jsonl
```

后台也记录了 `07:43:05.5456115` 开始扫描、`07:43:11.5769837` 扫描结束。开始排查时，后台 PID 为 `23988`，实际程序路径为 `C:\Users\zen\AppData\Local\SmartFanNext\app\SmartFan.Agent.exe`，仍在运行。前端之后的模板异常和 IPC 推送取消不能等同于后台进程已经退出。

## 修复与验证要求

`BleDeviceInfo.Address` 是根据数值地址生成的只读显示属性。设备列表中的地址和信号强度仅供显示，模板必须显式使用 `Mode=OneWay`。不要为了满足错误的回写绑定而给计算属性增加无意义的 setter。

空列表启动不能验证这个问题。回归检查应给真实扫描列表模板放入至少一个 `BleDeviceInfo`，实际执行模板实例化、测量和布局，确认地址可见且没有绑定异常。真机验收还需要在已部署的 C 盘前端点击扫描并显示结果；完成情况以本轮测试结果为准。

### 已完成的验证

- WPF 离线回归命令：`dotnet run --project windows-client/SmartFan.Ui.Tests/SmartFan.Ui.Tests.csproj -c UiVerification`，164 项检查通过，0 警告、0 错误。
- 负对照使用真实只读 `BleDeviceInfo.Address` 和旧式 `Run.Text` 默认绑定，能复现相同的 `InvalidOperationException`，证明检查确实覆盖了这类错误。
- 修复后的实际 `MainWindow` 高级页列表模板，在 Nebula、Light 两个主题下分别填入真实设备模型，按 2 项 → 1 项 → 2 项三轮实例化、测量和布局；设备名、地址、RSSI 文本均符合预期，没有调度器异常。
- 27 个诊断接口行的真实计算属性同样已物化并检查，避免只修地址后又遇到其他只读字段回写问题。这些检查没有扫描 BLE、启动采样器、附着后台或显示桌面窗口。
- `tools/publish.ps1 -Deploy` 已成功执行。C 盘新版前端 PID 为 `15456`，启动时没有安全警告；已有后台仍为 PID `23988`，没有因前端更新重启。

### 已完成的实际桌面验收

2026-10-17 从 C 盘运行新版前端 PID `15456`，启动时间为 `07:52:42.6635333 +08:00`：

| 操作 | 时间 | 实际结果 |
|---|---|---|
| 第一轮 BLE 扫描 | `07:53:14` → `07:53:20` | 找到 1 台 ESP，设备项正常显示，没有错误对话框 |
| 第二轮 BLE 扫描 | `07:53:42` → `07:53:48` | 清空并重新生成列表项后仍正常显示，没有错误对话框 |
| 打开日志目录 | 两轮扫描后 | 点击窗口底部按钮，资源管理器打开实际 `frontend-logs` 目录，可见当天 JSONL 文件 |

两轮发现的设备地址为 `84:C7:BB:56:6A:16`，验收时界面显示 RSSI `-25 dBm`。设备名称略去，不影响地址和信号强度绑定验证。

本次新前端启动以来的 11 条日志记录中，`Error = 0`、`Warning = 0`。这只统计新启动后的记录，修复前的历史异常仍完整保留。后台始终保持 PID `23988`，未因部署、前端重开或扫描重启。

日志入口的资源管理器面包屑为 `Roaming / SmartFanNext / frontend-logs`，目录内包含 `smartfan-20261017.jsonl`。该实际桌面验收与 164 项离线 WPF 回归分别记录；本次扫描没有写入风扇或灯光配置，也不作为最终 PCB 物理控制验收。

## 在哪里找错误日志

| 内容 | 默认目录 | 文件 |
|---|---|---|
| 界面、绑定、前端操作异常 | `%APPDATA%\SmartFanNext\frontend-logs` | `smartfan-yyyyMMdd.jsonl` |
| BLE、后台采样和 IPC | 实际 `agent.json` 同目录的 `logs` | `agent-yyyyMMdd.jsonl` 及轮转归档 |
| 显式开启的渲染诊断 | 前端日志目录 | `render-*.summary.json`、`render-*.samples.json` |

前端界面的“运行日志”是有长度上限的摘要；磁盘 JSONL 中的 `detail` 保留异常类型和堆栈。界面内“导出日志”导出前端日志；后台日志需要查看对应后台目录。自选 `--data-dir` 时，前端日志位于所选目录的 `frontend-logs`；后台配置路径以实际运行实例报告的 `ConfigurationPath` 为准。

较早修复版曾在窗口底部加入“打开日志目录”按钮。本轮已按用户要求移除主页日志区；现在从“高级设置”或“接口自检”打开独立“运行日志与诊断”窗口，分别访问实际前后端日志目录。最新桌面验收见 [本轮界面与接口调整](CLIENT-REFINEMENT.md)。

全局界面异常提示也已加入发生时间和当天日志文件完整路径，方便从弹窗直接定位磁盘记录；完整堆栈继续写入 JSONL 的 `detail` 字段。

可直接读取当天前端日志：

```powershell
Get-Content "$env:APPDATA\SmartFanNext\frontend-logs\smartfan-20261017.jsonl" -Encoding UTF8
```

## 构建、部署与 Rider

交接记录已经通过同一二进制的 A/B 测试确认：这台电脑从 G 盘启动的程序有安全区域和后台管道访问问题；从用户本地 C 盘目录启动可用。不要通过扩大管道 ACL、关闭安全检查或修改全局信任区域来修复本次绑定问题。

正常更新流程保留 G 盘源码和发布产物，执行 `tools/publish.ps1 -Deploy`，再从 `%LOCALAPPDATA%\SmartFanNext\app\SmartFan.exe` 启动。Rider 直接运行 G 盘 `bin` 输出仍可能复现交接记录中的管道问题；可先发布部署，再运行 C 盘 exe，不必为这个绑定修复改动所有项目的构建输出目录。

部署只更新应用文件，不覆盖 `%APPDATA%\SmartFanNext` 中的配置和日志，不主动终止已有后台。如果运行中的后台锁住了需要更新的文件，应明确报告需要重启后台，不能静默留下混合版本或为了更新前端自动断开设备。
