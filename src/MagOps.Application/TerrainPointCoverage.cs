using MagOps.Infrastructure.Services;

namespace MagOps.Application;

public sealed record TerrainPointCoverageResult(int InputRows, int LocatedPoints, int InvalidCoordinateRows,
    int UncoveredPoints, IReadOnlyList<int> UncoveredPhysicalLines, IReadOnlyList<TerrainAsset> Candidates,
    IReadOnlyList<string> Warnings)
{
    public bool ReadOnly => true;
    public string CoverageMeaning => "瓦片边界覆盖原始点；不证明像元非 NoData，也不证明线间区域完整或高程基准正确。";
}

public sealed class TerrainPointCoverage
{
    public ApplicationResult<TerrainPointCoverageResult> Inspect(string input, string? mapping, CancellationToken cancellation = default)
    {
        var table = new IntermediateTableInspection().Inspect(input, mapping, cancellation);
        if (!table.Ok) return ApplicationResult<TerrainPointCoverageResult>.Failure(table.OperationId, table.Errors[0]);
        try
        {
            var rows = table.Result!.Rows;
            var points = rows.Where(r => r.Longitude.HasValue && r.Latitude.HasValue).ToArray();
            if (points.Length == 0) throw new ArgumentException("没有明确坐标的原始点。");
            var library = new TerrainLibraryService();
            var assets = library.Query(points.Min(p => p.Longitude!.Value), points.Min(p => p.Latitude!.Value),
                points.Max(p => p.Longitude!.Value), points.Max(p => p.Latitude!.Value));
            bool Covers(TerrainAsset a, IntermediateTableRow p) => p.Longitude >= a.Info.MinLongitude && p.Longitude <= a.Info.MaxLongitude &&
                p.Latitude >= a.Info.MinLatitude && p.Latitude <= a.Info.MaxLatitude;
            var candidates = assets.Where(a => points.Any(p => Covers(a, p))).ToArray();
            string root = library.Settings().DefaultDirectory;
            var available = candidates.Where(a => File.Exists(Path.Combine(root, a.RelativePath))).ToArray();
            var missing = points.Where(p => !available.Any(a => Covers(a, p))).Select(p => p.PhysicalLine).ToArray();
            var warnings = candidates.Except(available).Select(a => "瓦片文件缺失：" + a.OriginalName).ToList();
            if (missing.Length > 0) warnings.Add("部分点无现存瓦片覆盖；未自动下载或绑定。");
            return ApplicationResult<TerrainPointCoverageResult>.Success(table.OperationId, null,
                new(rows.Count, points.Length, rows.Count - points.Length, missing.Length, missing, candidates, warnings));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { return ApplicationResult<TerrainPointCoverageResult>.Failure(table.OperationId, new("VALIDATION_ERROR", ex.Message)); }
    }
}
