using System.Text.Json;
using MagOps.Infrastructure.Storage;
namespace MagOps.Infrastructure.Services;

public sealed record TerrainAsset(string Id, string RelativePath, string OriginalName, string Sha256,
    GeoTiffDemInfo Info, string HeightDatum = "未确认", string Unit = "m（待基准确认）")
{
    public TerrainSource? Source { get; init; }
}
public sealed class TerrainLibraryIndex
{
    public string Schema { get; set; } = "magops-terrain-library-1";
    public string LibraryId { get; set; } = Guid.NewGuid().ToString("N");
    public List<TerrainAsset> Assets { get; set; } = [];
}
public sealed record TerrainTileReference(string LibraryId, string AssetId, string Sha256, string RelativePath,
    string OriginalName, GeoTiffDemInfo Info, string HeightDatum)
{
    public TerrainSource? Source { get; init; }
}
public sealed class TerrainSelection
{
    public string Schema { get; set; } = "magops-terrain-selection-1";
    public List<TerrainTileReference> Tiles { get; set; } = [];
    public string PortableDirectory { get; set; } = "";
    public bool OverlapOrderConfirmed { get; set; }
}
public sealed class TerrainLibrarySettings
{
    public string DefaultDirectory { get; set; } = "";
    public Dictionary<string,string> Locations { get; set; } = [];
}
public sealed class TerrainLibraryService
{
    public static string? SettingsPathOverride { get; set; }
    public static string SettingsPath => SettingsPathOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"MagOps","terrain-libraries.json");
    public static string IndexPath(string root) => Path.Combine(root,"terrain-library.json");
    internal static T Read<T>(string path) where T:new() => File.Exists(path)
        ? JsonSerializer.Deserialize<T>(File.ReadAllText(path)) ?? throw new InvalidDataException("地形索引为空。") : new();
    internal static void Write<T>(string path,T value) => AtomicFileWriter.Write(path,s=>JsonSerializer.Serialize(s,value,new JsonSerializerOptions{WriteIndented=true}));
    public static TerrainSelection ReadSelection(string path)=>Read<TerrainSelection>(path);
    public static void SaveSelection(string path,TerrainSelection selection)=>Write(path,selection);
    public static IReadOnlyList<string> ResolveSelectionFiles(string path)=>TerrainSetSampler.ResolveFiles(path,Read<TerrainSelection>(path));
    public TerrainLibrarySettings Settings() => Read<TerrainLibrarySettings>(SettingsPath);
    public TerrainLibraryIndex Open(string root)
    {
        if(!File.Exists(IndexPath(root)))throw new InvalidDataException("未找到资料库索引，请重新连接原库。");
        var doc=Read<TerrainLibraryIndex>(IndexPath(root));
        if(doc.Schema!="magops-terrain-library-1"||string.IsNullOrWhiteSpace(doc.LibraryId))throw new InvalidDataException("不支持的地形资料库索引。");
        return doc;
    }
    public void SetDefault(string root)
    {
        root=Path.GetFullPath(root);Directory.CreateDirectory(root);
        using(var guard=WorkspaceFileLock.AcquireDirectory(root)) { if(!File.Exists(IndexPath(root)))Write(IndexPath(root),new TerrainLibraryIndex()); }
        var library=Open(root);
        using var settingsGuard=WorkspaceFileLock.AcquireDirectory(Path.GetDirectoryName(SettingsPath)!);
        var settings=Settings();settings.DefaultDirectory=root;settings.Locations[library.LibraryId]=root;Write(SettingsPath,settings);
    }
    public IReadOnlyList<TerrainAsset> Import(IEnumerable<string> sources, CancellationToken token=default, TerrainSource? sourceInfo=null)
    {
        string root=Settings().DefaultDirectory;
        if(string.IsNullOrWhiteSpace(root))throw new InvalidOperationException("请先设置默认地形资料库。");
        using var guard=WorkspaceFileLock.AcquireDirectory(root);
        var library=Open(root);var result=new List<TerrainAsset>();
        foreach(string source in sources) {
            token.ThrowIfCancellationRequested();
            var dem=new GeoTiffDemSampler(source);dem.ValidateForHeightCalculation();
            string hash=dem.ContentSha256;
            var existing=library.Assets.FirstOrDefault(a=>a.Sha256.Equals(hash,StringComparison.OrdinalIgnoreCase));
            if(existing is not null) {
                string existingPath=SafePath(root,existing.RelativePath);
                if(!File.Exists(existingPath)||GeneratedArtifactIdentity.Hash(existingPath)!=hash)throw new InvalidDataException("已有公共瓦片缺失或变化，请核对资料库，未自动替换。");
                if (sourceInfo is not null && existing.Source is null) { var updated=existing with { Source=sourceInfo, HeightDatum="EGM96", Unit="m" }; library.Assets[library.Assets.IndexOf(existing)]=updated; existing=updated; }
                result.Add(existing);continue;
            }
            string name=Path.GetFileName(source);
            bool alreadyManaged = sourceInfo is not null && Path.GetFullPath(source).StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            string relative=alreadyManaged ? Path.GetRelativePath(root, source) : Path.Combine("其他地形",name);
            string target=SafePath(root,relative);
            if(File.Exists(target)&&GeneratedArtifactIdentity.Hash(target)!=hash) {
                target=GlobalWorkspaceService.UniqueFile(target);relative=Path.GetRelativePath(root,target);
            }
            if(!File.Exists(target))AtomicFileWriter.Write(target,stream=>{using var input=File.OpenRead(source);input.CopyTo(stream);});
            if(GeneratedArtifactIdentity.Hash(target)!=hash)throw new InvalidDataException("导入期间瓦片发生变化，未登记。");
            var asset=new TerrainAsset(hash,relative,name,hash,dem.Info,sourceInfo?.VerticalDatum ?? dem.VerticalDatum,sourceInfo is null ? dem.VerticalUnit : "m") { Source=sourceInfo };library.Assets.Add(asset);result.Add(asset);
        }
        token.ThrowIfCancellationRequested();Write(IndexPath(root),library);return result;
    }
    public IReadOnlyList<TerrainAsset> ImportPortableSelection(string path, CancellationToken token = default)
    {
        var selection = ReadSelection(path);
        if (string.IsNullOrWhiteSpace(selection.PortableDirectory)) throw new InvalidDataException("需要离线瓦片包清单。");
        var files = ResolveSelectionFiles(path); var result = new List<TerrainAsset>();
        foreach (var pair in selection.Tiles.Zip(files))
        {
            token.ThrowIfCancellationRequested();
            if (pair.First.Source is { Provider: "JAXA AW3D30" } source)
            {
                string receipt = SafePath(Path.GetDirectoryName(Path.GetFullPath(path))!, source.EvidenceRelativePath);
                string archive = Path.Combine(Path.GetDirectoryName(receipt)!, "original.zip");
                if (!File.Exists(archive) || GeneratedArtifactIdentity.Hash(archive) != source.ArchiveSha256) throw new InvalidDataException("离线 AW3D30 原始包缺失或变化。");
                var tile = new Aw3d30Tile((int)Math.Round(pair.First.Info.MinLongitude), (int)Math.Round(pair.First.Info.MinLatitude));
                var asset = Aw3d30DownloadService.ImportArchive(archive, tile, this, token);
                if (asset.Sha256 != pair.First.Sha256) throw new InvalidDataException("离线包引用与原始 DSM 不一致。");
                result.Add(asset);
            }
            else result.AddRange(Import([pair.Second], token));
        }
        return result;
    }
    public IReadOnlyList<TerrainAsset> Query(double minLon,double minLat,double maxLon,double maxLat)
    {
        string root=Settings().DefaultDirectory;if(string.IsNullOrWhiteSpace(root))return [];
        return Open(root).Assets.Where(a=>a.Info.MaxLongitude>=minLon&&a.Info.MinLongitude<=maxLon&&a.Info.MaxLatitude>=minLat&&a.Info.MinLatitude<=maxLat).ToArray();
    }
    public TerrainSelection Select(IEnumerable<TerrainAsset> assets,bool overlapConfirmed=false)
    {
        var library=Open(Settings().DefaultDirectory);
        return new(){OverlapOrderConfirmed=overlapConfirmed,Tiles=assets.Select(a=>new TerrainTileReference(library.LibraryId,a.Id,a.Sha256,a.RelativePath,a.OriginalName,a.Info,a.HeightDatum) { Source=a.Source }).ToList()};
    }
    public string Resolve(TerrainTileReference tile)
    {
        var settings=Settings();
        if(!settings.Locations.TryGetValue(tile.LibraryId,out string? root)||!Directory.Exists(root))throw new InvalidDataException("地形资料库位置不可用，请重新连接原资料库；项目引用未替换。");
        var library=Open(root);if(library.LibraryId!=tile.LibraryId)throw new InvalidDataException("资料库身份不匹配。");
        var asset=library.Assets.SingleOrDefault(a=>a.Id==tile.AssetId&&a.Sha256==tile.Sha256)??throw new InvalidDataException("地形资产身份不匹配。");
        string path=SafePath(root,asset.RelativePath);
        if(!File.Exists(path)||GeneratedArtifactIdentity.Hash(path)!=tile.Sha256)throw new InvalidDataException("公共瓦片缺失或变化，请重新选择并核对成果。");
        return path;
    }
    public void Reconnect(string root)
    {
        root=Path.GetFullPath(root);if(!File.Exists(IndexPath(root)))throw new InvalidDataException("请选择原资料库目录，不能用空目录重新连接。");
        SetDefault(root);
    }
    public string ExportSelection(string selectionPath,string destination)
    {
        if(Directory.Exists(destination)&&Directory.EnumerateFileSystemEntries(destination).Any())throw new InvalidOperationException("打包目标必须为空目录，未覆盖已有内容。");
        new TerrainSetSampler(selectionPath).ValidateForHeightCalculation();
        var selection=Read<TerrainSelection>(selectionPath);
        var files=TerrainSetSampler.ResolveFiles(selectionPath,selection);
        Directory.CreateDirectory(destination);
        foreach(var pair in selection.Tiles.Zip(files)) {
            string target=SafePath(destination,Path.Combine("tiles",pair.First.Sha256+Path.GetExtension(pair.Second)));
            AtomicFileWriter.Write(target,s=>{using var input=File.OpenRead(pair.Second);input.CopyTo(s);});
            if(GeneratedArtifactIdentity.Hash(target)!=pair.First.Sha256)throw new InvalidDataException("打包期间瓦片变化，未写入完整集合清单。");
            if (pair.First.Source is { } source)
            {
                string originalRoot = !string.IsNullOrWhiteSpace(selection.PortableDirectory) ? Path.GetDirectoryName(Path.GetFullPath(selectionPath))!
                    : Settings().Locations.TryGetValue(pair.First.LibraryId, out string? location) ? location : throw new InvalidDataException("来源库不可用。");
                string receiptPath = SafePath(originalRoot, source.EvidenceRelativePath);
                var receipt = Read<Aw3d30ReceiptDocument>(receiptPath);
                if (receipt.Schema != "magops-aw3d30-source-1" || receipt.Files.Count == 0) throw new InvalidDataException("AW3D30 来源清单缺失或不完整。");
                string originalFamily = Path.GetDirectoryName(receiptPath)!;
                string newReceipt = SafePath(destination, source.EvidenceRelativePath);
                foreach (var item in receipt.Files)
                {
                    string original = SafePath(originalFamily, item.Key);
                    if (!File.Exists(original) || GeneratedArtifactIdentity.Hash(original) != item.Value) throw new InvalidDataException("AW3D30 来源文件变化，不能打包。");
                    string copied = SafePath(Path.GetDirectoryName(newReceipt)!, item.Key);
                    AtomicFileWriter.Write(copied, s => { using var input = File.OpenRead(original); input.CopyTo(s); });
                    if (GeneratedArtifactIdentity.Hash(copied) != item.Value) throw new InvalidDataException("AW3D30 来源复制校验失败。");
                }
                AtomicFileWriter.Write(newReceipt, s => { using var input = File.OpenRead(receiptPath); input.CopyTo(s); });
            }
        }
        var portable=new TerrainSelection{Tiles=selection.Tiles,PortableDirectory="tiles",OverlapOrderConfirmed=selection.OverlapOrderConfirmed};
        string manifest=Path.Combine(destination,"terrain.terrain.json");Write(manifest,portable);return manifest;
    }
    private sealed class Aw3d30ReceiptDocument
    {
        public string Schema { get; set; } = "";
        public Dictionary<string,string> Files { get; set; } = [];
    }
    internal static string SafePath(string root,string relative)
    {
        string full=Path.GetFullPath(Path.Combine(root,relative));string prefix=Path.GetFullPath(root).TrimEnd('\\','/')+Path.DirectorySeparatorChar;
        if(!full.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("地形相对路径越出资料目录。");return full;
    }
}
