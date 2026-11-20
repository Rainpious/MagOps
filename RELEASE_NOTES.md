# MagOps v0.9.59

- 地形下载默认使用 Copernicus DEM GLO-30 匿名公开源。可切换 GLO-90 或 JAXA AW3D30，并记住选择。
- 航线范围匹配、中间表原始点和离地高度页面共用下载设置。公共库核对已有瓦片后补下载，公开源缺片时标出瓦片编号。
- 航线与地形计算可直接读取 Copernicus 分块压缩 GeoTIFF。离线瓦片包保留原始文件、EGM2008 高程基准和来源校验信息。
- CLI 新增 `plan-dem` 和 `ensure-dem`。Windows x64 包含桌面程序和命令行工具，完整解压后运行 `MagOps.exe`。
