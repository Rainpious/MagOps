using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MagOps.Core.Coordinates;
using MagOps.Core.Domain;
using MagOps.Infrastructure.Storage;

namespace MagOps.Infrastructure.Services;

public sealed record WorkareaScreeningGroup(string GroupId, string SourceFile,
    DateTimeOffset StartUtc, DateTimeOffset EndUtc, int PointCount, string Status,
    bool AutomaticallyExcluded, bool Excluded, int BufferedPointCount,
    [property: JsonIgnore] IReadOnlyList<UavMagCanonicalSample> Samples);
public sealed record WorkareaScreeningResult(string ContextFingerprint, string Fingerprint,
    IReadOnlyList<WorkareaScreeningGroup> Groups, [property: JsonIgnore] IReadOnlyList<UavMagCanonicalSample> IncludedSamples,
    IReadOnlyList<string> Warnings);

/// <summary>Current-workarea spatial authority and reversible full-point screening. Base is never spatially screened.</summary>
public sealed class WorkareaSpatialService
{
    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public static string ContextFingerprint(WorkareaScreeningInput input) =>
        Hash(JsonSerializer.Serialize(new { input.Range, input.Options, input.RouteFingerprint }));

    public static WorkareaSpatialRange FromCoordinates(string workareaPath, string source,
        IReadOnlyList<CoordinateConversionResult> points, ProjectionParameters projection)
    {
        if (points.Count < 2 || !projection.CentralMeridian.HasValue ||
            points.Any(p => !double.IsFinite(p.Easting) || !double.IsFinite(p.Northing)))
            throw new InvalidDataException("至少需要两个有效坐标及明确投影参数才能建立工区矩形。");
        var center = CoordinateGeometryMath.Centroid(points);
        double cx = center.Easting, cy = center.Northing;
        return new(workareaPath.Replace('\\', '/').Trim('/'), source, projection,
            points.Min(p => p.Easting), points.Min(p => p.Northing),
            points.Max(p => p.Easting), points.Max(p => p.Northing), cx, cy);
    }

    public static WorkareaSpatialRange? FromTasks(string workareaPath, IReadOnlyList<PlannedSurveyLineTask> tasks)
    {
        var coordinates = tasks.SelectMany(t => new[] { t.Start, t.End }).ToArray();
        if (coordinates.Length < 2 || coordinates.Any(p => !ValidCoordinate(p.Longitude, p.Latitude))) return null;
        double meridian = CoordinateService.SuggestGk3CentralMeridian(coordinates.Average(p => p.Longitude));
        if (coordinates.Any(p => Math.Abs(p.Longitude - meridian) > 6d)) return null;
        var projection = new ProjectionParameters(CentralMeridian: meridian);
        var projected = coordinates.Select(p => CoordinateService.Convert(p.Longitude.ToString(System.Globalization.CultureInfo.InvariantCulture),
            p.Latitude.ToString(System.Globalization.CultureInfo.InvariantCulture), CoordinateInputMode.DecimalDegrees, projection)).ToArray();
        var range = FromCoordinates(workareaPath, "当前工区设计测线", projected, projection);
        return range with { CentroidEasting = projected.Average(p => p.Easting), CentroidNorthing = projected.Average(p => p.Northing) };
    }

    public static bool ValidCoordinate(double longitude, double latitude) =>
        double.IsFinite(longitude) && double.IsFinite(latitude) && longitude is >= -180d and <= 180d && latitude is > -89d and < 89d;

    public static bool TryProject(UavMagCanonicalSample sample, WorkareaSpatialRange range, out double x, out double y)
    {
        x = y = 0;
        if (!sample.Longitude.HasValue || !sample.Latitude.HasValue ||
            !ValidCoordinate(sample.Longitude.Value, sample.Latitude.Value) ||
            sample.QualityFlags.Any(f => f.Contains("INVALID_COORD", StringComparison.OrdinalIgnoreCase) || f == "NO_GNSS")) return false;
        if (!range.Projection.CentralMeridian.HasValue || Math.Abs(sample.Longitude.Value - range.Projection.CentralMeridian.Value) > 30d)
            return false; // Do not extrapolate a local GK series across continents and silently exclude data.
        try
        {
            (x, y) = CoordinateProjectionService.Forward(sample.Longitude.Value, sample.Latitude.Value, range.Projection);
            return double.IsFinite(x) && double.IsFinite(y);
        }
        catch (ArgumentException) { return false; }
    }

    public WorkareaSpatialRange? LoadRange(string root, string workareaPath, JsonStore store) =>
        store.Read(ProjectPaths.Json(root, "workarea_spatial_ranges.json"), new List<WorkareaSpatialRange>())
            .FirstOrDefault(x => x.WorkareaPath.Equals(workareaPath.Replace('\\', '/').Trim('/'), StringComparison.OrdinalIgnoreCase));

    public void SaveRange(string root, WorkareaSpatialRange range, JsonStore store)
    {
        using var writeLock = WorkspaceFileLock.AcquireForRoot(root);
        var rows = store.Read(ProjectPaths.Json(root, "workarea_spatial_ranges.json"), new List<WorkareaSpatialRange>());
        rows.RemoveAll(x => x.WorkareaPath.Equals(range.WorkareaPath, StringComparison.OrdinalIgnoreCase));
        rows.Add(range); store.Write(ProjectPaths.Json(root, "workarea_spatial_ranges.json"), rows);
    }

    public WorkareaScreeningResult Screen(FieldMagFolderAnalysis analysis, WorkareaScreeningInput input)
    {
        if (!double.IsFinite(input.Options.CoarseDistanceM) || input.Options.CoarseDistanceM <= 0 ||
            !double.IsFinite(input.Options.BufferM) || input.Options.BufferM < 0)
            throw new ArgumentException("粗筛距离必须大于零，缓冲距离不能为负。");
        string context = ContextFingerprint(input);
        bool decisionsCurrent = input.SourceFingerprint == analysis.Fingerprint && input.ContextFingerprint == context;
        var decisions = (decisionsCurrent ? input.Decisions : []).GroupBy(d => d.GroupId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);
        var excludedSamples = (decisionsCurrent ? input.ExcludedSamples : []).Select(p => (p.SourceFile, p.SourceRecord)).ToHashSet();
        var warnings = new List<string>();
        if (!decisionsCurrent && (input.Decisions.Count > 0 || input.ExcludedSamples.Count > 0)) warnings.Add("数据或筛选范围已变化，旧排除/恢复决定已清除。");
        var range = input.Range;
        bool usableRange = range is not null && range.Projection is not null && range.Projection.CentralMeridian.HasValue &&
            double.IsFinite(range.Projection.CentralMeridian.Value) && range.Projection.CentralMeridian.Value is >= -180d and <= 180d &&
            double.IsFinite(range.MinEasting) && double.IsFinite(range.MaxEasting) &&
            double.IsFinite(range.MinNorthing) && double.IsFinite(range.MaxNorthing) &&
            range.MinEasting <= range.MaxEasting && range.MinNorthing <= range.MaxNorthing;
        if (!usableRange) warnings.Add("当前工区没有可用空间范围；保留轨迹供人工核验。");
        var groups = new List<WorkareaScreeningGroup>();
        foreach (var stream in analysis.UavSamples.GroupBy(p => p.SourceFile, StringComparer.OrdinalIgnoreCase))
        {
            var ordered = stream.OrderBy(p => p.TimestampUtc).ThenBy(p => p.SourceRecord).ToArray();
            int begin = 0;
            for (int i = 1; i <= ordered.Length; i++)
            {
                if (i < ordered.Length && (ordered[i].TimestampUtc - ordered[i - 1].TimestampUtc).TotalSeconds <= 900d) continue;
                var samples = ordered[begin..i]; begin = i;
                string id = Hash($"{stream.Key}|{samples[0].SourceRecord}|{samples[^1].SourceRecord}|{samples[0].TimestampUtc:O}|{samples[^1].TimestampUtc:O}")[..24];
                bool near = false, unknown = false; int valid = 0, buffered = 0;
                if (usableRange)
                {
                    foreach (var sample in samples)
                    {
                        if (!TryProject(sample, range!, out double x, out double y)) { unknown = true; continue; }
                        valid++;
                        if (!near && range!.DistanceSquared(x, y) <= input.Options.CoarseDistanceM * input.Options.CoarseDistanceM) near = true;
                        if (range!.ContainsBuffered(x, y, input.Options.BufferM)) buffered++;
                    }
                }
                // Missing/uncertain coordinates and tiny fragments are never grounds for automatic exclusion.
                bool autoExcluded = usableRange && valid >= 3 && !unknown && !near;
                bool excluded = decisions.TryGetValue(id, out var decision) ? decision.Decision == "EXCLUDE" : autoExcluded;
                string status = autoExcluded ? "远离当前工区" : !usableRange || unknown || valid < 3 ? "空间归属待检查" :
                    buffered > 0 ? "接近工区作业范围" : "工区外围轨迹";
                groups.Add(new(id, stream.Key, samples[0].TimestampUtc, samples[^1].TimestampUtc, samples.Length,
                    status, autoExcluded, excluded, buffered, samples));
            }
        }
        string fingerprint = Hash(analysis.Fingerprint + context + string.Join("|", groups.Select(g => $"{g.GroupId}:{g.Excluded}")) +
            JsonSerializer.Serialize(excludedSamples.OrderBy(p => p.SourceFile, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.SourceRecord)
                .Select(p => new { p.SourceFile, p.SourceRecord })));
        int uncertainGroups = groups.Count(g => !g.Excluded && g.Status == "空间归属待检查");
        if (uncertainGroups > 0) warnings.Add($"{uncertainGroups} 个轨迹片段缺少可靠空间证据，已保留供人工继续。");
        return new(context, fingerprint, groups, groups.Where(g => !g.Excluded).SelectMany(g => g.Samples)
            .Where(p => !excludedSamples.Contains((p.SourceFile, p.SourceRecord))).ToArray(), warnings);
    }

    public static FieldMagFolderAnalysis Apply(FieldMagFolderAnalysis source, WorkareaScreeningResult screened, string? timeZoneId = null) => new()
    {
        ProjectTimeZoneId = timeZoneId ?? source.ProjectTimeZoneId,
        SourceDirectory = source.SourceDirectory, Fingerprint = screened.Fingerprint,
        Files = source.Files, UavSamples = screened.IncludedSamples, BaseSamples = source.BaseSamples,
        GsmSessions = source.GsmSessions, QualityIssues = source.QualityIssues, GemLinkJobs = source.GemLinkJobs
    };

    public static FieldMagFolderAnalysis WithTimeZone(FieldMagFolderAnalysis source, string timeZoneId) => new()
    {
        ProjectTimeZoneId = timeZoneId, SourceDirectory = source.SourceDirectory, Fingerprint = source.Fingerprint,
        Files = source.Files, UavSamples = source.UavSamples, BaseSamples = source.BaseSamples,
        GsmSessions = source.GsmSessions, QualityIssues = source.QualityIssues, GemLinkJobs = source.GemLinkJobs
    };
}
