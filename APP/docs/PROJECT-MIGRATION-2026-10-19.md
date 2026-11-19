# 2026-10-19 C# 客户端目录迁移

C# 客户端从 `G:\ESP32\LaptopCooler\windows-client` 整体迁移到 **`G:\Csharp\LaptopCoolerClient`**，不再嵌套于 ESP 固件目录中。Git 远程仍为 `https://github.com/LGfrir/WPLC-Client.git`，HEAD 仍为 `3dae0357b173c17f7af906a72392de5223a86be6`；迁移前未提交的传感器适配及界面修改完整保留，没有创建提交或推送。

## 完整性

迁移前记录 2182 个文件、777875094 字节的 SHA-256 清单。移入新目录后、修改迁移说明及重新生成构建缓存之前，全部文件哈希一致，Git HEAD、远程及工作区状态一致。包含 `.git`、未跟踪源码、`bin/obj`、发布包和测试证据；没有只迁移已提交文件。原目录在清空后移除。

证据位于 `artifacts/migration-20261019/`：`source-state.json`、`source-manifest.json` 和 `move-verification.json`。后续还原、构建会正常更新 `bin/obj`，因此初始清单记录的是迁移本身的完整性，不要求生成文件永久保持旧哈希。

## 开发入口

在 Rider 中打开 `G:\Csharp\LaptopCoolerClient\SmartFan.sln`。在该目录执行：

```powershell
dotnet restore SmartFan.sln --force
dotnet build SmartFan.sln -c Release --no-restore
.\tools\publish.ps1 -Deploy
```

项目引用及发布脚本均按仓库相对路径解析。`obj` 中的绝对路径缓存由新目录下的还原和构建重新生成；不需要把 ESP 源码加入 Rider 解决方案。以往设计稿、诊断 JSON 和测试日志中的旧绝对路径保留为历史证据；复现其命令时应从本仓库根目录使用当前项目相对路径。

已安装程序仍在 `%LOCALAPPDATA%\SmartFanNext\app`，用户配置仍由实际运行程序的 AppData 目录管理。源码迁移不迁移或覆盖用户配置，也不要求重新安装 PawnIO。发布部署仍使用原有脚本及运行位置。

新路径下已完成强制依赖还原和非增量 Release 构建，0 错误、0 警告；286 条服务、955 条离线 WPF、60 条进程间通信断言全部通过。对应日志为 `artifacts/migration-20261019/restore.txt`、`build.txt`、`services.txt`、`ui.txt` 和 `ipc.txt`。本次没有构建或烧录 ESP 固件。

## ESP 侧范围

按用户澄清，本次只迁移 C# 客户端。ESP 仓库中的 Python BLE 客户端、图形调试工具、灯光协议校验、串口/板级健康检查及固件通信接口均保留。ESP 的 `.gitignore` 中移除了旧 C# 客户端专用的 `.NET (pc_client)` 忽略段，README 标明独立仓库入口。未重写固件仓库历史；此前提交中的历史源码仍可追溯。
