using System.Text.Json;
using MagOps.Infrastructure.Storage;
namespace MagOps.Infrastructure.Services;

// Legacy shape remains readable. New selections refer to the shared library.
public sealed record WorkareaTerrainBinding(string Path, string Sha256);
public sealed class WorkareaTerrainDocument
{
    public string Schema { get; set; } = "magops-workarea-terrain-2";
    public Dictionary<string,WorkareaTerrainBinding> Legacy { get; set; } = [];
    public Dictionary<string,TerrainSelection> Selections { get; set; } = [];
}
public sealed class WorkareaTerrainService
{
    private static string Key(string workarea)=>workarea.Replace('\\','/').Trim('/').ToUpperInvariant();
    private static string Index(string root)=>ProjectPaths.Json(root,"workarea_terrain.json");
    private static WorkareaTerrainDocument Read(string root)
    {
        if(!File.Exists(Index(root)))return new();
        string json=File.ReadAllText(Index(root));using var doc=JsonDocument.Parse(json);
        if(doc.RootElement.TryGetProperty("Schema",out _)) {
            var result=JsonSerializer.Deserialize<WorkareaTerrainDocument>(json)!;
            if(result.Schema!="magops-workarea-terrain-2")throw new InvalidDataException("未知工区地形索引版本。");return result;
        }
        return new(){Legacy=JsonSerializer.Deserialize<Dictionary<string,WorkareaTerrainBinding>>(json)??[]};
    }
    private static string Manifest(string root,string workarea)=>ProjectPaths.Json(root,"terrain-"+Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Key(workarea))))[..20]+".terrain.json");
    public string Bind(string root,string workarea,TerrainSelection selection)
    {
        if(string.IsNullOrWhiteSpace(workarea))throw new InvalidOperationException("请先选择目标工区。");
        using var guard=WorkspaceFileLock.AcquireForRoot(root);
        string manifest=Manifest(root,workarea);
        // Validate staging before changing the active mapping.
        string staged=Path.Combine(ProjectPaths.DataDirectory(root),Guid.NewGuid().ToString("N")+".terrain.json");
        try {
            TerrainLibraryService.Write(staged,selection);new TerrainSetSampler(staged).ValidateForHeightCalculation();
            var doc=Read(root);doc.Selections[Key(workarea)]=selection;
            if(File.Exists(Index(root))&&!File.Exists(Index(root)+".v1-backup"))File.Copy(Index(root),Index(root)+".v1-backup",false);
            TerrainLibraryService.Write(Index(root),doc);TerrainLibraryService.Write(manifest,selection);return manifest;
        }finally{if(File.Exists(staged))File.Delete(staged);}
    }
    public string Archive(string root,string workarea,string source,FolderLayoutService layout)
    {
        if(source.EndsWith(".terrain.json",StringComparison.OrdinalIgnoreCase)) {
            var selection=TerrainLibraryService.Read<TerrainSelection>(source);
            if(!string.IsNullOrWhiteSpace(selection.PortableDirectory)) {
                var libraryService=new TerrainLibraryService();
                selection=libraryService.Select(libraryService.ImportPortableSelection(source),selection.OverlapOrderConfirmed);
            }
            return Bind(root,workarea,selection);
        }
        var library=new TerrainLibraryService();
        var imported=library.Import([source]);return Bind(root,workarea,library.Select(imported));
    }
    public string? Resolve(string root,string workarea,out bool hasBinding)
    {
        hasBinding=false;if(string.IsNullOrWhiteSpace(workarea))return null;
        var doc=Read(root);
        if(doc.Selections.TryGetValue(Key(workarea),out var selection)) {
            hasBinding=true;
            // Resolve all hashes before exposing the active selection.
            TerrainSetSampler.ResolveFiles(Manifest(root,workarea),selection);
            string manifest=Manifest(root,workarea);
            // The project index owns the mapping; this small manifest is a regenerable reader adapter.
            if(!File.Exists(manifest)||File.ReadAllText(manifest)!=JsonSerializer.Serialize(selection,new JsonSerializerOptions{WriteIndented=true})) {
                using var guard=WorkspaceFileLock.AcquireForRoot(root);TerrainLibraryService.Write(manifest,selection);
            }
            return manifest;
        }
        if(!doc.Legacy.TryGetValue(Key(workarea),out var old))return null;
        hasBinding=true;string path=TerrainLibraryService.SafePath(root,old.Path);
        if(!File.Exists(path)||GeneratedArtifactIdentity.Hash(path)!=old.Sha256)throw new InvalidDataException("旧工区地形缺失或变化，请重新选择。");return path;
    }
}
