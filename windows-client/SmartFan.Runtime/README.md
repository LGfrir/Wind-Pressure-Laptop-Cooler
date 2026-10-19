SmartFan.Runtime 包含后台会话和前端代理，不引用 WPF。

- `AgentHost` 独占一个 `IBleClient`、一个 `IHardwareSampler` 和一个采样循环。
- `AgentPipeServer` 通过当前 Windows 用户专用的命名管道提供 RPC 与状态事件。
- `BackgroundClient` 实现 `IBleClient`，供前端替换原来的 `NativeBleClient`；`DisposeAsync()` 只关闭当前前端管道。
- `Models`、`Services` 已从 WPF 工程移入此处。为减少迁移改动，暂时保留 `SmartFan.App.Models/Services` 命名空间。

前端启动流程：先启动同目录的 `SmartFan.Agent.exe --config "完整路径\agent.json"`，再调用 `BackgroundClient.AttachAsync()`。后台已有实例时重复启动不会重置采样、连接或配置。`GetConfigurationAsync()` 取得后台真实配置；不要在接入后用前端默认值覆盖它。

`AgentConfiguration` 控制后台独立运行。默认 `EnabledTelemetry=false`、`AutoConnect=false`、`AutoUploadConfiguration=false`，因此首次启动只采样，不写实机。`ConfigureTelemetryAsync` 持久化采样/上报偏好，`SetDemoAsync(true)` 会关闭真实上报，关闭演示后不会自动重新开启上报。

`AgentStatus` 的 `BootId + ConnectionGeneration` 标识真正的设备连接。打开或重开窗口不会产生新 generation。前端可以用它避免重复弹出配置同步提示。`IsAdministrator` 表示后台进程本身的权限；只提升前端权限不会提升已经运行的后台。

命名管道每帧为 4 字节小端长度和 UTF-8 JSON，最大 1 MiB；每个请求有独立 Id、取消与超时。读写锁保证帧不会交错；关闭、超时或恶意客户端不会结束后台会话。真实 GATT 请求仍由 Core 层串行化。

`SmartFan.Agent --probe --output "status.json"` 可以在没有 UI 时读取后台状态。`SmartFan.Agent --stop` 或前端明确调用 `StopAgentAsync()` 才会停止后台：管道先回复 ACK，再发出 `ShutdownRequested`，由进程关闭 BLE 与采样器。关闭普通前端不会调用此方法。`--pipe` 可指定独立测试管道，`--run-for-seconds` 只用于有限时验证。后台默认日志位于配置文件同级 `logs` 中，按天保留 14 天、单文件 5 MiB、每天最多 8 个归档；生产启动配置默认位于 `%APPDATA%\SmartFanNext\agent.json`。

曲线、温度来源、10 秒失效后 20% PWM 等策略仍缺少对应固件接口，后台不会编造 UUID，也不会在 PC 端代替 ESP 执行调速闭环。自动场景上传仅在用户启用后，通过现有风扇/灯光接口执行。RAM/VRAM 协议单位未确认时，不上报四项内存字段。
