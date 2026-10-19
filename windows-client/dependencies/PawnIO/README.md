# PawnIO 2.2.0 安装包记录

本目录保留本机经用户许可安装的官方签名安装包 `PawnIO-2.2.0-setup.exe`。该 exe 已由忽略规则排除，不作为源码提交；本说明也不触发安装或后台启动。

来源为 [PawnIO 官网](https://pawnio.eu/)提供的 [PawnIO.Setup 2.2.0 发布](https://github.com/namazso/PawnIO.Setup/releases/tag/2.2.0)，使用正式签名分发，不使用无签名版本。

| 项目 | 核验结果 |
|---|---|
| 文件版本 | 2.2.0.0 |
| 文件大小 | 3,410,960 字节 |
| Authenticode | Valid，签名主体 `namazso.eu` |
| 签名证书指纹 | `F380DCC9F706E2756A5047B832FFE719E1BC35F5` |
| SHA-256 | `1F519A22E47187F70A1379A48CA604981C4FCF694F4E65B734AAA74A9FBA3032` |
| GitHub release digest | `sha256:1f519a22e47187f70a1379a48ca604981c4fcf694f4e65b734aaa74a9fba3032`，与安装包相同 |

用户接受默认 `C:\Program Files\PawnIO` 位置，2026-10-18 安装器返回退出码 0。本机 Windows 驱动包为 `oem16.inf`，驱动签名主体为 Microsoft Windows Hardware Compatibility Publisher；这是驱动包核验，不应与安装器 `namazso.eu` 签名混为一项。其他电脑的 INF 分配名称可能不同。

2026-10-19 管理员后台已确认 PawnIO 2.2.0.0 设备可访问，并取得真实 CPU Package、24 个核心 Clock 及 25 项 CPU Voltage 读数。本机 Intel 电压项按 VID 请求电压标注，仅用于本机显示，不作为实测 VCore 上报。实际关闭并重开普通前端后，同一管理员后台继续采样，没有重复启动或再次 UAC；记录见 `windows-client/artifacts/cpu-metrics-fix-20261018/native-acceptance-20261019.json`。

程序复用已有后台；没有后台且已安装 PawnIO 时，普通前端只为后台申请 UAC。驱动安装不意味着普通权限进程已具备硬件访问能力。没有修改 UAC、系统安全策略或设备权限，也没有配置提升权限的登录任务。完整代码与验收记录见 [CPU 指标修复说明](../../docs/CPU-METRICS-FIX-2026-10-19.md)。
