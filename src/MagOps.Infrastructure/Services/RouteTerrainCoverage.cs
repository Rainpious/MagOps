using MagOps.Core.Routes;

namespace MagOps.Infrastructure.Services;

public sealed class RouteTerrainCoverage
{
    private readonly (double Lon, double Lat)[] _points;
    public TerrainCoverageRequest Extent { get; }
    public int SampleCount => _points.Length;

    public RouteTerrainCoverage(IEnumerable<(double Lon, double Lat)> boundary, IEnumerable<RoutePlanResult> plans,
        IEnumerable<(double Lon, double Lat)>? additionalSamples = null)
    {
        _points = boundary.Concat(plans.SelectMany(plan => plan.Sorties.SelectMany(sortie =>
            RouteExportService.BuildDjiRouteKmlPoints(sortie, plan.Mode, plan.CentralMeridian)))).Concat(additionalSamples ?? []).Distinct().ToArray();
        if (_points.Length == 0) throw new InvalidOperationException("缺少工区边界或可采样的航线。");
        Extent = new(_points.Min(p => p.Lon), _points.Min(p => p.Lat), _points.Max(p => p.Lon), _points.Max(p => p.Lat));
    }

    public TerrainCoverageRequest DownloadExtent(int resolution)
    {
        double dy = 1d / (resolution == 90 ? 1200 : 3600);
        double dx = dy * Math.Max(CopernicusTile.LongitudeFactor((int)Math.Floor(Extent.South)),
                                   CopernicusTile.LongitudeFactor((int)Math.Floor(Extent.North)));
        return new(Math.Max(-180, Extent.West - dx), Math.Max(-90, Extent.South - dy),
                   Math.Min(180, Extent.East + dx), Math.Min(90, Extent.North + dy));
    }

    public bool Covers(TerrainSetSampler dem, out string reason, CancellationToken token = default)
    {
        dem.ValidateForHeightCalculation();
        foreach (var p in _points)
        {
            token.ThrowIfCancellationRequested();
            try { dem.SampleBilinear(p.Lon, p.Lat); }
            catch (InvalidOperationException ex)
            {
                reason = $"地形采样失败（{p.Lon:F6}°, {p.Lat:F6}°）：{ex.Message}";
                return false;
            }
        }
        reason = "";
        return true;
    }

    public bool CoversFile(string path)
    {
        try { return Covers(new TerrainSetSampler(path), out _); }
        catch (Exception ex) when (ex is IOException or InvalidDataException) { return false; }
    }

    public TerrainSelection? FindLocal(TerrainLibraryService library)
    {
        var box = DownloadExtent(90);
        var assets = library.Query(box.West, box.South, box.East, box.North);
        var groups = assets.GroupBy(a => (a.Source?.Provider, a.HeightDatum, a.Info.PixelSizeLongitude, a.Info.PixelSizeLatitude))
            .OrderBy(g => g.Key.PixelSizeLatitude);
        foreach (var group in groups)
        {
            foreach (var asset in group.Where(a => a.Info.MinLongitude <= Extent.West && a.Info.MaxLongitude >= Extent.East &&
                                                    a.Info.MinLatitude <= Extent.South && a.Info.MaxLatitude >= Extent.North))
            {
                var single = library.Select([asset]);
                if (Usable(single)) return single;
            }
            if (group.Count() > 1)
            {
                var mosaic = library.Select(group);
                if (Usable(mosaic)) return mosaic;
            }
        }
        return null;
    }

    private bool Usable(TerrainSelection selection)
    {
        try { return Covers(new TerrainSetSampler(selection), out _); }
        catch (Exception ex) when (ex is IOException or InvalidDataException) { return false; }
    }

    public async Task<TerrainSelection> CompleteAsync(int resolution, IProgress<string> progress, CancellationToken token)
    {
        var assets = await CopernicusDem.EnsureAsync(DownloadExtent(resolution), resolution, progress, token).ConfigureAwait(false);
        var selection = new TerrainLibraryService().Select(assets);
        if (!Covers(new TerrainSetSampler(selection), out string reason, token)) throw new InvalidDataException(reason);
        return selection;
    }
}
