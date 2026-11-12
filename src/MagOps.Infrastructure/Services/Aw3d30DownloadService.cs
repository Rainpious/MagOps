using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using MagOps.Infrastructure.Storage;

namespace MagOps.Infrastructure.Services;

public sealed record Aw3d30Tile(int Longitude, int Latitude)
{
    private static string Name(int lon, int lat) => $"{(lat < 0 ? 'S' : 'N')}{Math.Abs(lat):000}{(lon < 0 ? 'W' : 'E')}{Math.Abs(lon):000}";
    public string Id => Name(Longitude, Latitude);
    public Uri DownloadUri => new($"https://www.eorc.jaxa.jp/ALOS/aw3d30/data/release_v2404/{Name((int)Math.Floor(Longitude / 5d) * 5, (int)Math.Floor(Latitude / 5d) * 5)}/{Id}.zip");
}
public sealed record TerrainSource(string Provider, string Release, string Model, string VerticalDatum,
    string Url, string ArchiveSha256, string EvidenceRelativePath, DateTimeOffset DownloadedAt);
public sealed record Aw3d30Receipt(string Schema, string TileId, TerrainSource Source, Dictionary<string, string> Files);

public sealed class Aw3d30DownloadService
{
    public const string Description = "JAXA AW3D30 · DSM 地表高程 · EGM96 · 米；保留原始分辨率和 NoData";
    public static IReadOnlyList<Aw3d30Tile> Plan(double west, double south, double east, double north)
    {
        if (!new[] { west, south, east, north }.All(double.IsFinite) || west < -180 || east > 180 || south < -90 || north > 90 || west >= east || south >= north)
            throw new ArgumentException("请填写有效的 WGS84 范围（西 < 东、南 < 北）；跨日期变更线请分两次下载。");
        int x0 = (int)Math.Floor(west), x1 = (int)Math.Ceiling(east), y0 = (int)Math.Floor(south), y1 = (int)Math.Ceiling(north);
        if ((x1 - x0) * (y1 - y0) > 25) throw new ArgumentException("单次最多 25 个一度瓦片，请缩小范围分批下载。");
        return Enumerable.Range(y0, y1 - y0).SelectMany(y => Enumerable.Range(x0, x1 - x0).Select(x => new Aw3d30Tile(x, y))).ToArray();
    }
    public async Task<IReadOnlyList<TerrainAsset>> DownloadAsync(IReadOnlyList<Aw3d30Tile> tiles, Aw3d30Login login,
        IProgress<string>? progress = null, CancellationToken token = default)
    {
        if (tiles.Count is 0 or > 25 || tiles.Any(t => t.Longitude is < -180 or >= 180 || t.Latitude is < -90 or >= 90))
            throw new ArgumentException("瓦片清单无效。");
        var library = new TerrainLibraryService(); string root = library.Settings().DefaultDirectory;
        if (string.IsNullOrWhiteSpace(root)) throw new InvalidOperationException("请先设置公共地形库。");
        // Redirects are rejected: authentication must never leave this fixed official endpoint.
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(15) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(login.UserName + ":" + login.Password)));
        var result = new List<TerrainAsset>();
        foreach (var tile in tiles.Distinct())
        {
            token.ThrowIfCancellationRequested(); progress?.Report($"{tile.Id}：正在下载；已完成 {result.Count} 个瓦片");
            string stage = Path.Combine(root, ".aw3d30-" + Guid.NewGuid().ToString("N") + ".pending"); Directory.CreateDirectory(stage);
            try
            {
                using var response = await client.GetAsync(tile.DownloadUri, HttpCompletionOption.ResponseHeadersRead, token);
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    throw new InvalidOperationException("AW3D30 账号验证失败；请核对账号。未自动重试，以免触发官方锁定。");
                if (response.StatusCode == HttpStatusCode.NotFound) throw new InvalidDataException($"{tile.Id} 官方没有可下载瓦片；海域或缺测区不能当作零高程。");
                if (!response.IsSuccessStatusCode) throw new IOException($"AW3D30 下载失败（HTTP {(int)response.StatusCode}）；已完成瓦片保留，可重新下载未完成范围。");
                if (response.Content.Headers.ContentLength > 256L * 1024 * 1024) throw new InvalidDataException("单瓦片下载超过 256 MB 限制。");
                string archive = Path.Combine(stage, "original.zip");
                await using (var input = await response.Content.ReadAsStreamAsync(token))
                await using (var output = File.Create(archive))
                {
                    var buffer = new byte[131072]; long total = 0; int count;
                    while ((count = await input.ReadAsync(buffer, token)) > 0)
                    {
                        total += count; if (total > 256L * 1024 * 1024) throw new InvalidDataException("单瓦片下载超过大小限制。");
                        await output.WriteAsync(buffer.AsMemory(0, count), token);
                        progress?.Report($"{tile.Id}：已接收 {total / 1048576d:F1} MB；已完成 {result.Count} 个瓦片");
                    }
                }
                result.Add(ImportArchive(archive, tile, library, token));
            }
            finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
        }
        return result;
    }
    public static TerrainAsset ImportArchive(string archive, Aw3d30Tile tile, TerrainLibraryService library, CancellationToken token = default)
    {
        if (tile.Longitude is < -180 or >= 180 || tile.Latitude is < -90 or >= 90) throw new ArgumentException("瓦片编号无效。");
        string root = library.Settings().DefaultDirectory;
        if (string.IsNullOrWhiteSpace(root)) throw new InvalidOperationException("请先设置公共地形库。");
        string stage = Path.Combine(root, ".aw3d30-extract-" + Guid.NewGuid().ToString("N") + ".pending"); Directory.CreateDirectory(stage);
        try
        {
            token.ThrowIfCancellationRequested();
            string archiveHash = GeneratedArtifactIdentity.Hash(archive);
            using (var zip = ZipFile.OpenRead(archive))
            {
                if (zip.Entries.Count > 64 || zip.Entries.Sum(e => e.Length) > 512L * 1024 * 1024) throw new InvalidDataException("AW3D30 压缩包超出瓦片大小限制。");
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in zip.Entries)
                {
                    token.ThrowIfCancellationRequested();
                    string name = entry.FullName.Replace('\\', '/');
                    if (!name.StartsWith(tile.Id + "/", StringComparison.Ordinal) || name.Contains(':') || name.Split('/').Any(p => p is "." or "..") || !names.Add(name))
                        throw new InvalidDataException("AW3D30 包含异常或重复的文件路径。");
                    if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000) throw new InvalidDataException("AW3D30 包不允许符号链接。");
                    string target = TerrainLibraryService.SafePath(stage, name);
                    if (name.EndsWith('/')) { if (name != tile.Id + "/") throw new InvalidDataException("瓦片包目录结构异常。"); Directory.CreateDirectory(target); continue; }
                    if (!new[] { "_DSM.tif", "_MSK.tif", "_STK.tif", "_HDR.txt", "_QAI.txt", "_LST.txt" }.Any(s => name == tile.Id + "/ALPSMLC30_" + tile.Id + s))
                        throw new InvalidDataException("AW3D30 包含未识别的文件；请核实官方格式是否发生变化。");
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    using var input = entry.Open(); using var output = File.Create(target);
                    var buffer = new byte[131072]; int count; long total = 0;
                    while ((count = input.Read(buffer)) > 0) { token.ThrowIfCancellationRequested(); total += count; if (total > entry.Length) throw new InvalidDataException("解压长度不一致。"); output.Write(buffer, 0, count); }
                    if (total != entry.Length) throw new InvalidDataException("压缩包不完整。");
                }
            }
            string stem = Path.Combine(stage, tile.Id, "ALPSMLC30_" + tile.Id);
            foreach (string suffix in new[] { "_DSM.tif", "_MSK.tif", "_STK.tif", "_HDR.txt", "_QAI.txt", "_LST.txt" })
                if (!File.Exists(stem + suffix)) throw new InvalidDataException("AW3D30 文件族不完整：" + suffix);
            if (!File.ReadAllText(stem + "_HDR.txt").Contains("EGM96", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("瓦片头文件未声明 EGM96，不能套用默认高程基准。");
            var dem = new GeoTiffDemSampler(stem + "_DSM.tif"); dem.ValidateForHeightCalculation();
            if (Math.Abs(dem.Info.MinLongitude - tile.Longitude) > .01 || Math.Abs(dem.Info.MinLatitude - tile.Latitude) > .01 ||
                Math.Abs(dem.Info.MaxLongitude - tile.Longitude - 1) > .01 || Math.Abs(dem.Info.MaxLatitude - tile.Latitude - 1) > .01 || dem.Info.NoDataValue != -9999)
                throw new InvalidDataException("瓦片坐标或 NoData 与 AW3D30 合同不符。");
            File.Copy(archive, Path.Combine(stage, "original.zip"));
            if (GeneratedArtifactIdentity.Hash(Path.Combine(stage, "original.zip")) != archiveHash) throw new InvalidDataException("原始下载包在导入期间发生变化。");
            File.WriteAllText(Path.Combine(stage, "source-credit.txt"),
                "Credit: AW3D30 (JAXA)\nDSM / EGM96 / metres. Original resolution and NoData retained.\n" +
                "Terms: https://earth.jaxa.jp/en/data/policy/\n" +
                "Credit JAXA and applicable supplementary data providers when publishing or distributing derivatives. Commercial use requires advance notification to JAXA under the current terms.\n" +
                "Supplementary source information is retained in the original MSK/HDR/QAI/LST files.\n");
            string relative = Path.Combine("sources", "AW3D30", tile.Id, archiveHash);
            var source = new TerrainSource("JAXA AW3D30", "release_v2404", "DSM", "EGM96", tile.DownloadUri.AbsoluteUri, archiveHash, Path.Combine(relative, "source.json"), DateTimeOffset.UtcNow);
            var hashes = Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories).ToDictionary(p => Path.GetRelativePath(stage, p), GeneratedArtifactIdentity.Hash);
            TerrainLibraryService.Write(Path.Combine(stage, "source.json"), new Aw3d30Receipt("magops-aw3d30-source-1", tile.Id, source, hashes));
            string committed = TerrainLibraryService.SafePath(root, relative);
            using (WorkspaceFileLock.AcquireDirectory(root))
            {
                token.ThrowIfCancellationRequested();
                if (!Directory.Exists(committed)) { Directory.CreateDirectory(Path.GetDirectoryName(committed)!); Directory.Move(stage, committed); }
                else foreach (var file in hashes)
                    if (!File.Exists(TerrainLibraryService.SafePath(committed, file.Key)) || GeneratedArtifactIdentity.Hash(TerrainLibraryService.SafePath(committed, file.Key)) != file.Value)
                        throw new InvalidDataException("已保存的 AW3D30 文件族缺失或变化；未覆盖。");
            }
            return library.Import([Path.Combine(committed, tile.Id, "ALPSMLC30_" + tile.Id + "_DSM.tif")], token, source).Single();
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
    }
}
