# MagOps

面向磁测项目的 Windows 桌面工作台，将工区资料、坐标与空间处理、航线设计、外业记录和成果来源管理放在同一项目中。

MagOps is a Windows desktop workspace for magnetic-survey project organization, spatial data, flight-route planning and traceable survey artifacts.

## 下载与运行

从 [Releases](https://github.com/Rainpious/MagOps/releases) 下载 Windows x64 ZIP，完整解压后运行 `MagOps.exe`。发行包自带 .NET 运行时，无需单独安装；命令行工具位于 `cli/MagOps.Cli.exe`。当前版本为 0.9.55，仍在持续开发和实机验收。

## 主要功能

- 项目与工区管理：资料登记、文件入口、来源标识、版本与文件变化检查。
- 坐标与空间：坐标转换、空间文件检查、工区边界和面积处理。
- 航线设计：分区绘制、保存与重开恢复、航线生成和 KML 导出；重复检查线与独立交叉检查线分别管理。
- 踏勘参考：导入 OVKML 和已验证结构的 OVOBJ 轨迹，为工区切割提供道路参考。
- 地形：共享 GeoTIFF 地形库、项目引用、AW3D30 下载和缺失瓦片补齐。
- 外业与成果：设备和外业记录、地磁数据检查与日变试算、外部成果整包登记及来源核验。
- CLI：提供项目查询、数据检查、试算、地形下载及成果登记等命令；运行 `cli/MagOps.Cli.exe` 查看命令入口。

## 地形设置

在“绘制航线 → 地形库 → AW3D30 下载设置”中设置自己的 JAXA 账号及公共库路径。路径可复制，点击“更改…”选择其他库。“自动下载缺失地形”默认开启，已有手动设置会保留。账号凭据由 Windows 当前用户加密保存在本机，不随项目导出；本仓库及发行包不提供共享账号。

AW3D30 是地表模型（DSM），不等同于裸地 DTM。下载数据需遵守 [JAXA 数据政策](https://earth.jaxa.jp/en/data/policy/) 与来源标注要求；软件 MIT 协议不替代数据许可。

## 当前边界

- 起降点规划已冻结，前端不提供推荐或编辑入口。踏勘轨迹保留为工区切割参考。
- 日变处理等试算结果需要人工核验，不代表正式生产验收；质量提示也不替代飞行安全审查。
- 项目变更预览只生成影响信息，不执行目录删除、迁移等操作。
- OVOBJ 属于厂商格式，兼容性仅覆盖已实现和测试的结构，不保证所有版本。
- 网络下载可能遇到 TLS 或网络故障；离线已入库瓦片可复用。

## 从源码构建

需要 Windows x64 和 .NET 10 SDK（版本见 `global.json`）。

```powershell
dotnet build MagOps.CSharp.sln -c Release
dotnet run --project tests/MagOps.SmokeTests/MagOps.SmokeTests.csproj -c Release
dotnet run --project src/MagOps.Desktop/MagOps.Desktop.csproj -c Release
```

发布自带运行时的桌面程序：

```powershell
dotnet publish src/MagOps.Desktop/MagOps.Desktop.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o dist/MagOps
```

源码按 `Core`、`Infrastructure`、`Application`、`Reporting`、`Desktop` 和 `Cli` 分层。`tests/MagOps.SmokeTests` 是可独立运行的回归测试入口。公开模板和边界样例使用合成坐标；不包含实际项目资料、内部工作记录及私有验收脚本。

## 反馈与协议

通过 [Issues](https://github.com/Rainpious/MagOps/issues) 提交问题，请附软件版本、复现步骤和经过脱敏的示例，勿上传账号或业务原始数据。

MagOps 自有代码以 [MIT](LICENSE) 协议发布。第三方模型、运行时与外部数据的说明见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。
