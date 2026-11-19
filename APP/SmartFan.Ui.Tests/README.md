离线 WPF 控件验收，使用 STA 线程和真实资源字典，不启动生产 `App`、不显示系统窗口、不连接 BLE、不读取硬件。

```powershell
dotnet run --project .\SmartFan.Ui.Tests\SmartFan.Ui.Tests.csproj
dotnet run --project .\SmartFan.Ui.Tests\SmartFan.Ui.Tests.csproj -- --render .\docs\screenshots
```

覆盖八种灯效的载荷与表单往返、1～9 色边界、颜色与时长校验、离线保存事件、场景间草稿隔离、撤销、曲线编辑、四套主题及动态模板解析、导航滑块宽度/位置、平滑滚动。渲染图仅用于控件布局检查，不能代替生产窗口和实机验收。

2026-10-17 本轮通过 311 项 WPF 检查，并增加以下回归：

- 仪表盘通过显式 `Metrics` 契约绑定指标，替换来源后更新数据；电脑内置风扇摘要与 ESP32 底座 RPM 分离。
- 主窗口不包含内联日志区，独立 `LogWindow` 正确显示已有日志、接收后台线程更新，并在关闭时释放订阅和忽略排队回调。
- `SensorInspectorWindow` 首次加载只读取一次已有后台，筛选不重读、手动刷新不重叠，区分无读数、真实零值、空结果与失败，并忽略关闭后的响应。
- 扫描列表排版、圆角及字体资源一致性；实际绘制的数字字形随全局字体变化。

这些检查使用注入的传感器结果和离线视图，不读取 ASUS EC 或生产硬件。同期协议 1236 项、服务 101 项、进程/IPC 集成 48 项由各自的测试项目验证；不能合并计为真机验收。当前发布包的最终部署和生产窗口验收应单独记录。

扫描回归会在真实 `MainWindow` 的高级页填充实际 `BleDeviceInfo`，连续三轮清空并重新填充设备集合，检查生成的列表行是否显示名称、计算得到的只读地址和 RSSI，并捕获 Dispatcher 异常。还会生成诊断页的真实模型行。测试首先执行一个预期失败的对照：默认 `Run.Text` 绑定只读 `Address` 必须复现原异常；修复后的生产模板必须保持零异常。因此启动输出中的 `Expected negative control` 表示对照验证成功。

测试项目链接原始主题资源，供生产 `AppearanceService` 的相对资源 URI 在测试入口程序中解析；没有复制主题内容或启动生产 `App`。运行中的 Debug 程序锁定输出时，可使用独立构建配置：

```powershell
dotnet run --project .\SmartFan.Ui.Tests\SmartFan.Ui.Tests.csproj -c UiVerification
```

接口展开解析及滚动回归已加入，完整 WPF 测试现通过 402 项：

```powershell
dotnet run --project .\SmartFan.Ui.Tests\SmartFan.Ui.Tests.csproj -c DiagnosticVerification --nologo
```

测试通过真实主窗口和自检表格，使用原生 Toggle 语义展开箭头，检查 RowDetails 实际生成的字段值、类型、偏移、只读绑定、选中行保留及多行独立展开。载荷场景覆盖更新、清除、尚未收到 RPM 通知、收到真实零值、收到空回复、无效 HEX、错误长度及恢复；并检查两种窗口尺寸下的表格、详情滚动和写入输入框布局。离屏测试不显示 OS 窗口，也不把最后一行远端虚拟化或真实鼠标滚动当作已验收。
