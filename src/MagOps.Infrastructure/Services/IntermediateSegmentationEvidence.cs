using MagOps.Core.Domain;

namespace MagOps.Infrastructure.Services;

public sealed record SurveySpacingEstimate(double? SpacingM, int AlignedLines, int PairCount,
    int SupportingPairs, double? MedianDeviationM, string Message);

public static class IntermediateSegmentationEvidence
{
    public static string? Problem(TrackSegmentationInput? input)
    {
        if (input is not { Enabled: true }) return "中间 CSV 分线必须启用轨迹模式，并提供有效 KML 或明确的测线轴方向。";
        bool axis = input.AxisDegrees is { } a && double.IsFinite(a) && a >= 0 && a < 180;
        bool kml = TrackSegmentationReferenceService.IsAuthoritative(input) && input.ReferenceGeometry.Any(g =>
            g.Kind is "SURVEY" or "TIE" && WorkareaSpatialService.ValidCoordinate(g.Start.Longitude, g.Start.Latitude) &&
            WorkareaSpatialService.ValidCoordinate(g.End.Longitude, g.End.Latitude) && TrackSegmentationReferenceService.Distance(g.Start, g.End) > 1);
        if (!axis && !kml) return "请至少完成一项：导入含有效测线的 KML，或勾选并填写测线轴方向（北起顺时针，0≤方向<180°，双向有效）。";
        if (axis && kml && input.ReferenceGeometry.Any(g => g.Kind == "SURVEY" &&
            SurveyDirection.Bearing(g.Start, g.End) is { } bearing &&
            TrackSegmentationReferenceService.AxisDifference(bearing, input.AxisDegrees!.Value) > input.RecognitionOptions.AxisToleranceDegrees))
            return "KML 测线与指定轴方向存在冲突。请核对参考类别，或清空参考、取消指定方向，明确采用哪项依据。";
        return null;
    }

    /// <summary>Nearest overlapping parallel-axis separations, not an asserted design spacing.</summary>
    public static SurveySpacingEstimate Estimate(IEnumerable<PlannedSurveyLineTask> tasks, double axis)
    {
        var aligned = tasks.Where(t => t.LengthM >= 60 && SurveyDirection.Bearing(t.Start, t.End) is { } b &&
            TrackSegmentationReferenceService.AxisDifference(b, axis) <= 3).ToArray();
        if (aligned.Length < 4) return new(null, aligned.Length, 0, 0, null, "不足4条方向一致的长直线，暂不推荐线距。");
        var origin = aligned[0].Start;
        double radians = axis * Math.PI / 180;
        (double Along, double Across) Project(GeoCoordinate p)
        {
            double x = (p.Longitude - origin.Longitude) * 111320 * Math.Cos(origin.Latitude * Math.PI / 180);
            double y = (p.Latitude - origin.Latitude) * 111320;
            return (x * Math.Sin(radians) + y * Math.Cos(radians), x * Math.Cos(radians) - y * Math.Sin(radians));
        }
        var lines = aligned.Select(t =>
        {
            var a = Project(t.Start); var b = Project(t.End);
            return (Min: Math.Min(a.Along, b.Along), Max: Math.Max(a.Along, b.Along),
                Across: (a.Across + b.Across) / 2);
        }).ToArray();
        var pairs = new Dictionary<(int, int), double>();
        for (int i = 0; i < lines.Length; i++)
        {
            int nearest = -1; double distance = double.PositiveInfinity;
            for (int j = 0; j < lines.Length; j++)
            {
                if (i == j) continue;
                var a = lines[i]; var b = lines[j];
                double overlap = Math.Min(a.Max, b.Max) - Math.Max(a.Min, b.Min);
                double gap = Math.Abs(a.Across - b.Across);
                if (overlap < Math.Max(60, .2 * Math.Min(a.Max - a.Min, b.Max - b.Min)) || gap < 12 || gap > 5000) continue;
                if (gap < distance) { distance = gap; nearest = j; }
            }
            if (nearest >= 0) pairs[(Math.Min(i, nearest), Math.Max(i, nearest))] = distance;
        }
        var gaps = pairs.Values.Order().ToArray();
        if (gaps.Length < 3) return new(null, aligned.Length, gaps.Length, 0, null, "可比较的相邻重叠线对不足3组，暂不推荐线距。");
        double peak = gaps.OrderByDescending(g => gaps.Count(h => Math.Abs(h - g) <= Math.Max(2, g * .08))).ThenBy(g => g).First();
        var support = gaps.Where(g => Math.Abs(g - peak) <= Math.Max(2, peak * .08)).ToArray();
        double median = Median(support);
        double deviation = Median(support.Select(g => Math.Abs(g - median)).Order().ToArray());
        bool enough = support.Length >= 3 && support.Length >= gaps.Length * .5;
        return new(enough ? Math.Round(median, 1) : null, aligned.Length, gaps.Length, support.Length, Math.Round(deviation, 1),
            enough ? "相邻重叠轨迹的线距候选，需人工确认；可能受漏线、复飞、轨迹偏移影响。" : "线距分布未形成多数一致组，不自动推荐。");
    }
    private static double Median(double[] sorted) => sorted.Length % 2 == 0
        ? (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2 : sorted[sorted.Length / 2];
}
