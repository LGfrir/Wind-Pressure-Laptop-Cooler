# 底座转速只有几十 RPM 的原因

2026-10-17 对当前源码和已经连接的后台做只读核对。**确定原因在固件的临时测试返回值，不是客户端选择了错误的电脑风扇传感器。** 本次未改固件、连接状态或风扇配置。

## 固件当前实现

`main/ModuleDriver/fan.c` 的 `fan_get_rpm()` 仍为：

```c
// 测试NOTIFY用，暂时将rpm改为随机值，之后恢复
uint32_t fan_get_rpm(void) {
    return rand() % 100;
    return fan.rpm;
}
```

所以当前对外返回的是 `0..99` 测试值；真正的 `fan.rpm` 返回语句不可达。GATT 的 `fan_rpm` 通知读取此函数，把结果作为 `uint32_t` 的 4 字节小端发送。

另一个 `fan_rpm_update()` 已有由测速脉冲和实际采样时间计算 `fan.rpm` 的代码。恢复返回真实字段之后，还需要在真实风扇硬件上验证每转脉冲数、测速接线和采样；目前不据此宣称实际测速已经通过。

## 已连接后台的只读结果

通过已部署 C 盘 `SmartFan.Agent.exe --probe` 读取现有会话，没有新建 BLE 连接：

| 时间（本地） | LastRpm |
| --- | ---: |
| 12:27:45 | 39 |
| 12:28:00 | 65 |
| 12:28:01 | 2 |
| 12:28:03 | 66 |

会话均为 PID `23988`、ConnectionGeneration `2`、ESP 地址 `84:C7:BB:56:6A:16`，通知时间连续更新，与源码的测试值范围一致。以上是后台已解码值，当前版本不保存 RPM 原始四字节，因此不把它当成原始数据抓包。

上述初次核查时，电脑硬件快照 `FanSensorsAvailable=false`、`FanRpm` 五项均为 `null`。后续接入 ASUS G615LRG 专用只读 EC 来源后，已在最终发布后台取得三颗内置风扇 5400 / 3100 / 4900 RPM（随实际运行变化）。两次记录反映不同采集版本，底座 RPM 与电脑内置风扇始终是两条独立来源：

- **底座 RPM**：ESP `fan_rpm` NOTIFY → `PacketCodec.DecodeUInt32`（小端）→ `RpmEventArgs` → 后台 IPC → 首页仪表盘与历史曲线。
- **电脑内置风扇**：LibreHardwareMonitor `Fan` 传感器或已验证机型的 ASUS EC 只读来源 → `TelemetrySnapshot.FanRpm[5]` → 主页内置风扇摘要；启用对应遥测项后才发送 `laptop_fan_speed`。本机三颗风扇来自 ASUS EC，详情见 [CPU 与传感器诊断](CPU-TEMPERATURE-DIAGNOSTICS.md)。

源码核对表明前一条链路没有 `/60`、`/100`、`/1000` 或最大量程换算，仪表盘数字直接显示接收的 RPM。仪表盘刻度只决定圆弧长度，不改变数字。

## 验证

离线协议断言新增 `60 09 00 00 → 2400 RPM`、`27 00 00 00 → 39 RPM`、四字节高位读取，以及拒绝 2 / 5 字节载荷。硬件端恢复真实返回值及真实风扇验收由用户另行确认，本次保持固件原样。
