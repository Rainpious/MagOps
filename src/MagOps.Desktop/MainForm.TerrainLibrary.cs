using MagOps.Infrastructure.Services;
namespace MagOps.Desktop;
public sealed partial class MainForm
{
    private void DownloadAw3d30Terrain()
    {
        var points = GetCurrentRouteWorkareaLonLat();
        using var dialog = points.Count > 0
            ? new Aw3d30DownloadDialog(points.Min(p => p.Lon), points.Min(p => p.Lat), points.Max(p => p.Lon), points.Max(p => p.Lat))
            : new Aw3d30DownloadDialog();
        dialog.ShowDialog(this);
    }
    private void EnsureDefaultTerrainLibrary()
    {
        if(string.IsNullOrWhiteSpace(new TerrainLibraryService().Settings().DefaultDirectory))ConfigureDefaultTerrainLibrary();
    }
    private void ConfigureDefaultTerrainLibrary()
    {
        using var dialog=new FolderBrowserDialog { Description="设置默认公共地形资料库；选择原库可重新连接，原文件不移动",SelectedPath=new TerrainLibraryService().Settings().DefaultDirectory,ShowNewFolderButton=true };
        var files = FileInteractions.For(this, "terrain-library", projectLevel: true, global: true);
        files.Prepare(dialog, current: new TerrainLibraryService().Settings().DefaultDirectory);
        if(dialog.ShowDialog(this)!=DialogResult.OK)throw new OperationCanceledException();
        var library=new TerrainLibraryService();
        if(File.Exists(TerrainLibraryService.IndexPath(dialog.SelectedPath)))library.Reconnect(dialog.SelectedPath);
        else library.SetDefault(dialog.SelectedPath);
        files.DirectorySucceeded(dialog.SelectedPath);
        SetStatus("默认地形资料库："+dialog.SelectedPath+"；既有工区选用来源保持不变。");
    }
    private void MatchRouteTerrainTiles()
    {
        if(!IsProjectInitialized()||string.IsNullOrWhiteSpace(CurrentWorkareaRelativePath))throw new InvalidOperationException("请先选择项目与工区。");
        EnsureDefaultTerrainLibrary();var points=GetCurrentRouteWorkareaLonLat();
        if(points.Count==0)throw new InvalidOperationException("请先生成航线，获得当前范围后匹配；也可直接导入并多选瓦片。");
        var library=new TerrainLibraryService();
        if (Aw3d30DownloadPreferences.Load().DownloadMissingOnMatch)
        {
            var request = new TerrainCoverageRequest(Math.Max(-180, points.Min(p => p.Lon) - .001), Math.Max(-90, points.Min(p => p.Lat) - .001),
                Math.Min(180, points.Max(p => p.Lon) + .001), Math.Min(90, points.Max(p => p.Lat) + .001));
            var plan = BusyOperationDialog.Run(this, "检查本地地形", "核对 AW3D30 瓦片，已有完整瓦片直接复用…", () => new Aw3d30TerrainProvider().Inspect(request));
            if (plan.Missing.Count > 0)
            {
                using var download = new Aw3d30DownloadDialog(request.West, request.South, request.East, request.North) { StartDownloadOnShown = true };
                download.ShowDialog(this);
                if (!download.DownloadCompleted) throw new OperationCanceledException("补下载未完成，保留原工区地形选用。");
            }
        }
        var assets=library.Query(points.Min(p=>p.Lon),points.Min(p=>p.Lat),points.Max(p=>p.Lon),points.Max(p=>p.Lat));
        if(assets.Count==0)throw new InvalidOperationException("公共库没有覆盖当前范围的候选瓦片，请先导入所需GeoTIFF。");
        using var selectionDialog=new TerrainTileSelectionDialog(assets);
        if(selectionDialog.ShowDialog(this)!=DialogResult.OK)throw new OperationCanceledException();
        string manifest=new WorkareaTerrainService().Bind(Root,CurrentWorkareaRelativePath,library.Select(selectionDialog.SelectedAssets,true));
        ChooseRouteTerrainDem(manifest);
    }
    private void PackageRouteTerrainTiles()
    {
        string path=RequireRouteKmlTerrain();
        if(!path.EndsWith(".terrain.json",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("请先将旧地形导入公共库并绑定工区。");
        using var dialog=new FolderBrowserDialog{Description="选择空目录打包当前工区所需瓦片与引用（不会移动公共库文件）",ShowNewFolderButton=true};
        var files = FileInteractions.For(this, "terrain-package", "output"); files.Prepare(dialog);
        if(dialog.ShowDialog(this)!=DialogResult.OK)throw new OperationCanceledException();
        string result = new TerrainLibraryService().ExportSelection(path,dialog.SelectedPath);
        files.OutputSucceeded(dialog.SelectedPath, directory: true);
        SetStatus("已打包地形：" + result);
    }
}
