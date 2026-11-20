using System.Net;
using System.Text.Json;
using MagOps.Infrastructure.Storage;

namespace MagOps.Infrastructure.Services;

public sealed record CopernicusTile(int Longitude, int Latitude, int Resolution)
{
    public string Id => FormattableString.Invariant($"Copernicus_DSM_COG_{(Resolution == 30 ? 10 : 30)}_{(Latitude < 0 ? 'S' : 'N')}{Math.Abs(Latitude):00}_00_{(Longitude < 0 ? 'W' : 'E')}{Math.Abs(Longitude):000}_00_DEM");
    public Uri Url => new($"https://copernicus-dem-{Resolution}m.s3.eu-central-1.amazonaws.com/{Id}/{Id}.tif");
    internal int Rows => Resolution == 30 ? 3600 : 1200;
    internal static double LongitudeFactor(int row) => Math.Abs(row + 0.5) switch
    {
        < 50 => 1, < 60 => 1.5, < 70 => 2, < 80 => 3, < 85 => 5, _ => 10
    };
}

public static class CopernicusDem
{
    public const string Glo30 = "Copernicus DEM GLO-30";
    public const string Glo90 = "Copernicus DEM GLO-90";
    public const string Jaxa = "JAXA AW3D30";
    public const string Catalog = "https://registry.opendata.aws/copernicus-dem/";
    private const string Schema = "magops-copernicus-dem-1";
    private const long MaxBytes = 128L * 1024 * 1024;
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });

    public static IReadOnlyList<CopernicusTile> Plan(TerrainCoverageRequest request, int resolution = 30)
    {
        var (west, south, east, north) = request;
        if (resolution is not (30 or 90)) throw new ArgumentException("分辨率须为 30 或 90 米。");
        bool point = west == east && south == north;
        if (!new[] { west, south, east, north }.All(double.IsFinite) || west < -180 || east > 180 ||
            south < -90 || north > 90 || (point && (west == 180 || north == 90)) || (!point && (west >= east || south >= north)))
            throw new ArgumentException("请输入 WGS84 坐标点或西 < 东、南 < 北的范围。跨日期变更线请分两次。");
        double halfY = 0.5 / (resolution == 30 ? 3600 : 1200);
        int firstRow = point ? (int)Math.Ceiling(south - halfY) - 1 : (int)Math.Floor(south - halfY);
        int endRow = point ? firstRow + 1 : (int)Math.Ceiling(north - halfY);
        var tiles = new List<CopernicusTile>();
        for (int row = Math.Max(-90, firstRow); row < Math.Min(90, endRow); row++)
        {
            double halfX = halfY * CopernicusTile.LongitudeFactor(row);
            int start = (int)Math.Floor(west + halfX);
            int end = point ? start + 1 : Math.Min(start + 360, (int)Math.Ceiling(east + halfX));
            for (int col = start; col < end; col++)
            {
                if (tiles.Count == 25) throw new ArgumentException("单次最多 25 个一度瓦片，请缩小范围。");
                tiles.Add(new((col + 180) % 360 - 180, row, resolution));
            }
        }
        if (tiles.Count == 0) throw new InvalidDataException("坐标超出公开 COG 像元覆盖范围。");
        return tiles;
    }

    private static TerrainAsset? Cached(CopernicusTile tile, TerrainLibraryIndex index, string root)
    {
        var asset = index.Assets.FirstOrDefault(a => a.Source?.Url == tile.Url.AbsoluteUri);
        if (asset is null) return null;
        string path = TerrainLibraryService.SafePath(root, asset.RelativePath);
        if (!File.Exists(path) || GeneratedArtifactIdentity.Hash(path) != asset.Sha256)
            throw new InvalidDataException($"已登记的 {tile.Id} 瓦片缺失或变化，请修复公共库。");
        VerifyReceipt(root, asset.Source!, asset.Sha256);
        return asset;
    }

    public static int MissingCount(TerrainCoverageRequest request, int resolution)
    {
        var library = new TerrainLibraryService();
        string root = library.Settings().DefaultDirectory;
        var tiles = Plan(request, resolution);
        if (string.IsNullOrWhiteSpace(root)) return tiles.Count;
        var index = library.Open(root);
        return tiles.Count(t => Cached(t, index, root) is null);
    }

    public static async Task<IReadOnlyList<TerrainAsset>> EnsureAsync(TerrainCoverageRequest request, int resolution = 30,
        IProgress<string>? progress = null, CancellationToken token = default)
    {
        var tiles = Plan(request, resolution);
        var library = new TerrainLibraryService();
        string root = library.Settings().DefaultDirectory;
        if (string.IsNullOrWhiteSpace(root)) throw new InvalidOperationException("请先设置公共地形库。");
        var index = library.Open(root);
        var assets = new List<TerrainAsset>();
        foreach (var tile in tiles)
        {
            token.ThrowIfCancellationRequested();
            var existing = Cached(tile, index, root);
            if (existing is not null) { assets.Add(existing); continue; }
            progress?.Report($"匿名下载 {tile.Id}（{assets.Count + 1}/{tiles.Count}）");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromMinutes(10));
            using var response = await Client.GetAsync(tile.Url, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode is HttpStatusCode.NotFound)
                throw new InvalidDataException($"{tile.Id} 在该分辨率下无公开瓦片。请核查覆盖范围或另选数据源。");
            if (response.StatusCode != HttpStatusCode.OK) throw new IOException($"{tile.Id} 下载失败，HTTP {(int)response.StatusCode}。");
            long? expected = response.Content.Headers.ContentLength;
            if (expected is null or <= 0 or > MaxBytes) throw new InvalidDataException("瓦片长度异常。");
            string stage = Path.Combine(root, $".cop-dem-{Guid.NewGuid():N}.pending");
            try
            {
                await using (var output = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await using (var input = await response.Content.ReadAsStreamAsync(deadline.Token))
                {
                    byte[] buffer = new byte[131072];
                    int count;
                    while ((count = await input.ReadAsync(buffer, deadline.Token)) > 0)
                    {
                        if (output.Position + count > MaxBytes) throw new InvalidDataException("瓦片下载超出大小限制。");
                        await output.WriteAsync(buffer.AsMemory(0, count), deadline.Token);
                    }
                    if (expected.HasValue && output.Length != expected) throw new EndOfStreamException("瓦片下载不完整。");
                }
                assets.Add(Register(stage, tile, library, root, DateTimeOffset.UtcNow, token));
            }
            finally { File.Delete(stage); }
        }
        return assets;
    }

    internal sealed record Receipt(string Schema, CopernicusTile Tile, TerrainSource Source, string CatalogUrl, string Attribution);
    private static string Product(int resolution) => resolution == 30 ? Glo30 : Glo90;
    internal static CopernicusTile VerifyReceipt(string root, TerrainSource source, string hash)
    {
        string path = TerrainLibraryService.SafePath(root, source.EvidenceRelativePath);
        var receipt = JsonSerializer.Deserialize<Receipt>(File.ReadAllText(path));
        if (receipt?.Schema != Schema || receipt.Source != source || receipt.Tile is not { } tile ||
            tile.Resolution is not (30 or 90) || tile.Longitude is < -180 or >= 180 || tile.Latitude is < -90 or >= 90 ||
            source.Provider != Product(tile.Resolution) || source.VerticalDatum != "EGM2008" ||
            source.Url != tile.Url.AbsoluteUri || source.ArchiveSha256 != hash)
            throw new InvalidDataException("Copernicus DEM 来源记录与瓦片不符。");
        return tile;
    }

    internal static TerrainAsset Register(string path, CopernicusTile tile, TerrainLibraryService library,
        string root, DateTimeOffset downloaded, CancellationToken token)
    {
        var sampler = new GeoTiffDemSampler(path);
        sampler.ValidateForHeightCalculation();
        var info = sampler.Info;
        int width = (int)(tile.Rows / CopernicusTile.LongitudeFactor(tile.Latitude));
        var center = sampler.PixelCenter(0, 0);
        if (info.Width != width || info.Height != tile.Rows || Math.Abs(center.Lon - tile.Longitude) > 1e-8 ||
            Math.Abs(center.Lat - tile.Latitude - 1) > 1e-8 || Math.Abs(info.PixelSizeLongitude * width - 1) > 1e-8 ||
            Math.Abs(info.PixelSizeLatitude * tile.Rows - 1) > 1e-8)
            throw new InvalidDataException("Copernicus 瓦片尺寸、像元间距或定位与请求不符。");
        string hash = sampler.ContentSha256;
        string family = Path.Combine("CopernicusDEM", tile.Id, hash);
        string relative = Path.Combine(family, "source.json");
        string receiptPath = TerrainLibraryService.SafePath(root, relative);
        string tif = Path.Combine(Path.GetDirectoryName(receiptPath)!, tile.Id + ".tif");
        TerrainSource source;
        using (var guard = WorkspaceFileLock.AcquireDirectory(root))
        {
            token.ThrowIfCancellationRequested();
            source = new(Product(tile.Resolution), "2021 / AWS COG", "DSM", "EGM2008", tile.Url.AbsoluteUri, hash, relative, downloaded);
            Directory.CreateDirectory(Path.GetDirectoryName(tif)!);
            if (!File.Exists(tif)) AtomicFileWriter.Write(tif, output => { using var input = File.OpenRead(path); input.CopyTo(output); });
            if (GeneratedArtifactIdentity.Hash(tif) != hash) throw new InvalidDataException("公共库瓦片校验失败。");
            if (File.Exists(receiptPath))
            {
                source = JsonSerializer.Deserialize<Receipt>(File.ReadAllText(receiptPath))?.Source ?? throw new InvalidDataException("来源记录损坏。");
                VerifyReceipt(root, source, hash);
            }
            else TerrainLibraryService.Write(receiptPath, new Receipt(Schema, tile, source, Catalog,
                "© DLR e.V. 2010-2014 and © Airbus Defence and Space GmbH 2014-2018 provided under COPERNICUS by the European Union and ESA; all rights reserved."));
        }
        return library.ImportInto(root, [tif], token, source).Single();
    }
}
