namespace MagOps.Infrastructure.Services;

public sealed class Aw3d30DownloadPreferences
{
    public string DownloadSource { get; set; } = CopernicusDem.Glo30;
    public bool DownloadMissingOnMatch { get; set; } = true;
    internal static string? StoragePathOverride { get; set; }
    public static string Path => StoragePathOverride ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MagOps", "aw3d30-settings.json");
    public static Aw3d30DownloadPreferences Load() => TerrainLibraryService.Read<Aw3d30DownloadPreferences>(Path);
    public void Save() => TerrainLibraryService.Write(Path, this);
}

public sealed record TerrainCoverageRequest(double West, double South, double East, double North);
public sealed record TerrainDownloadPlan(IReadOnlyList<Aw3d30Tile> Missing, IReadOnlyList<TerrainAsset> Available);

/// <summary>Reusable application entry: local-first, then authenticated official downloads.</summary>
public interface ITerrainDownloadProvider
{
    TerrainDownloadPlan Inspect(TerrainCoverageRequest request, CancellationToken token = default);
    Task<IReadOnlyList<TerrainAsset>> EnsureAvailableAsync(TerrainCoverageRequest request, IProgress<string>? progress = null, CancellationToken token = default);
}

public sealed class Aw3d30TerrainProvider : ITerrainDownloadProvider
{
    public TerrainDownloadPlan Inspect(TerrainCoverageRequest request, CancellationToken token = default)
    {
        var tiles = Aw3d30DownloadService.Plan(request.West, request.South, request.East, request.North);
        var library = new TerrainLibraryService(); string root = library.Settings().DefaultDirectory;
        var assets = string.IsNullOrWhiteSpace(root) ? [] : library.Open(root).Assets;
        var missing = new List<Aw3d30Tile>(); var available = new List<TerrainAsset>();
        foreach (var tile in tiles)
        {
            token.ThrowIfCancellationRequested();
            var matches = assets.Where(a => a.Source?.Provider == "JAXA AW3D30" && a.Source.Url == tile.DownloadUri.AbsoluteUri).ToArray();
            TerrainAsset? valid = null;
            foreach (var candidate in matches)
            {
                token.ThrowIfCancellationRequested();
                string path = TerrainLibraryService.SafePath(root, candidate.RelativePath);
                if (!File.Exists(path) || GeneratedArtifactIdentity.Hash(path) != candidate.Sha256)
                    throw new InvalidDataException($"已登记瓦片 {tile.Id} 缺失或被修改，请先修复或重新连接资料库；不会自动覆盖已有来源。");
                valid ??= candidate;
            }
            if (valid is null) missing.Add(tile); else available.Add(valid);
        }
        return new(missing, available);
    }
    public async Task<IReadOnlyList<TerrainAsset>> EnsureAvailableAsync(TerrainCoverageRequest request, IProgress<string>? progress = null, CancellationToken token = default)
    {
        var plan = Inspect(request, token);
        if (plan.Missing.Count == 0) { progress?.Report("所需 AW3D30 瓦片已在公共库中，无需联网。"); return plan.Available; }
        var login = Aw3d30Credentials.Load() ?? throw new InvalidOperationException("尚未保存 AW3D30 账号，请先在账号与下载窗口保存自己的账号。");
        progress?.Report($"复用 {plan.Available.Count} 个本地瓦片，补下载 {plan.Missing.Count} 个缺失瓦片。");
        var downloaded = await new Aw3d30DownloadService().DownloadAsync(plan.Missing, login, progress, token);
        return plan.Available.Concat(downloaded).ToArray();
    }
}
