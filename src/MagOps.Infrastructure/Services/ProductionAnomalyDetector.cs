using MagOps.Core.Domain;

namespace MagOps.Infrastructure.Services;

public sealed record ProductionAnomalyDetectionOptions(
    double ObservationSeconds = 5d,
    double MinimumDurationSeconds = 5d,
    double HorizontalRadiusM = 2d)
{
    public void Validate()
    {
        if (new[] { ObservationSeconds, MinimumDurationSeconds, HorizontalRadiusM }
            .Any(v => !double.IsFinite(v) || v <= 0))
            throw new InvalidDataException("异常识别的观察窗、最短持续时间和水平半径必须为有限正数。");
    }
}

/// <summary>Source/time-local evidence only. Magnetic variations never prove vertical movement.
/// Detection runs across adjacent recognition legs, but never across missions, sources or gaps.</summary>
public static class ProductionAnomalyDetector
{
    public const string Version = "production-anomaly-1";
    private sealed record Point(FlightLegSample Sample, string LegId);

    public static IReadOnlyList<ProductionAnomalyInterval> Detect(
        IEnumerable<FlightLeg> legs, ProductionAnomalyDetectionOptions? options = null)
    {
        options ??= new();
        options.Validate();
        var result = new List<ProductionAnomalyInterval>();
        foreach (var mission in legs.GroupBy(l => l.MissionExecutionId))
        foreach (var source in mission.SelectMany(l => l.Samples.Select(s => new Point(s, l.FlightLegId)))
                     .GroupBy(p => p.Sample.SourceFile, StringComparer.OrdinalIgnoreCase))
        {
            Point[] a = source.OrderBy(p => p.Sample.TimestampUtc).ThenBy(p => p.Sample.SourceRecord).ToArray();
            int i = 0;
            int consumed = 0;
            while (i < a.Length)
            {
                int end = i;
                while (end + 1 < a.Length && Continuous(a[end].Sample, a[end + 1].Sample) &&
                       DistanceM(a[i].Sample, a[end + 1].Sample) <= options.HorizontalRadiusM) end++;
                if ((a[end].Sample.TimestampUtc - a[i].Sample.TimestampUtc).TotalSeconds <
                    Math.Max(options.MinimumDurationSeconds, options.ObservationSeconds))
                { i++; continue; }

                // Refit once to the median centre, then extend/backtrack the detected cluster.
                double lon = Median(a[i..(end + 1)].Select(p => p.Sample.Longitude));
                double lat = Median(a[i..(end + 1)].Select(p => p.Sample.Latitude));
                var centre = a[i].Sample with { Longitude = lon, Latitude = lat };
                int start = i;
                while (start > consumed && Continuous(a[start - 1].Sample, a[start].Sample) &&
                       DistanceM(centre, a[start - 1].Sample) <= options.HorizontalRadiusM) start--;
                while (end + 1 < a.Length && Continuous(a[end].Sample, a[end + 1].Sample) &&
                       DistanceM(centre, a[end + 1].Sample) <= options.HorizontalRadiusM) end++;
                var cluster = a[start..(end + 1)];
                double[] heights = cluster.Select(p => p.Sample.AltitudeM).OfType<double>().Where(double.IsFinite).ToArray();
                bool smoothVertical = SmoothVertical(cluster.Select(p => p.Sample).ToArray(), out int verticalConfirmed);
                int jumpAt = -1;
                for (int j = start + 1; j <= end; j++)
                    if (IsHeightJump(a[j - 1].Sample, a[j].Sample)) { jumpAt = j; break; }
                if (jumpAt < 0 && end + 1 < a.Length && Continuous(a[end].Sample, a[end + 1].Sample) &&
                    DistanceM(centre, a[end + 1].Sample) <= Math.Max(options.HorizontalRadiusM, 8d) &&
                    IsHeightJump(a[end].Sample, a[end + 1].Sample))
                    jumpAt = end + 1;
                bool heightJump = jumpAt >= 0;
                var dominant = cluster.GroupBy(p => (p.Sample.Longitude, p.Sample.Latitude)).OrderByDescending(g => g.Count()).First();
                double repeated = dominant.Count() / (double)cluster.Length;
                if (heightJump && repeated >= .95)
                    while (start < end && (a[start].Sample.Longitude, a[start].Sample.Latitude) != dominant.Key) start++;
                double clusterRadius = cluster.Max(p => DistanceM(centre, p.Sample));
                bool freezeJump = heightJump && repeated >= .95 &&
                    (a[jumpAt - 1].Sample.TimestampUtc - a[start].Sample.TimestampUtc).TotalSeconds >=
                    Math.Max(options.MinimumDurationSeconds, options.ObservationSeconds);
                bool confirmed = clusterRadius <= options.HorizontalRadiusM && (smoothVertical || freezeJump);
                string kind = freezeJump ? "POSITION_FREEZE_HEIGHT_JUMP" : smoothVertical ? "STATIONARY_VERTICAL" : "HORIZONTAL_STATIONARY_REVIEW";
                int last = freezeJump && confirmed ? jumpAt : end;
                // The jump row is evidence, not a recovered survey observation.
                var rows = a[start..(last + 1)].Select(p => new ExecutionSampleReference(p.Sample.SourceFile, p.Sample.SourceRecord, 0)).ToArray();
                string id = "anomaly-" + TrackSegmentationReferenceService.Hash(
                    $"{Version}|{mission.Key}|{source.Key}|{a[start].Sample.SourceRecord}|{a[last].Sample.SourceRecord}|{kind}")[..24];
                result.Add(new(id, a[start].LegId, a[start].Sample.TimestampUtc, a[last].Sample.TimestampUtc,
                    kind, $"水平半径 {options.HorizontalRadiusM:0.##}m，持续 {(a[end].Sample.TimestampUtc - a[start].Sample.TimestampUtc).TotalSeconds:0.0}s；" +
                    (smoothVertical ? "水平停留伴连续升降。" : heightJump ? "坐标冻结伴高程瞬跳；瞬跳不计为真实爬升。" : "补充证据不足，保留并待复核。"),
                    source.Key, a[start].Sample.SourceRecord, a[last].Sample.SourceRecord, rows.Length,
                    cluster.Max(p => DistanceM(centre, p.Sample)), heights.Length == 0 ? null : heights.Max() - heights.Min(), rows)
                {
                    ConfirmedUtc = confirmed && freezeJump ? a[last].Sample.TimestampUtc : smoothVertical
                        ? cluster[Math.Max(verticalConfirmed, Array.FindIndex(cluster, p => (p.Sample.TimestampUtc - a[start].Sample.TimestampUtc).TotalSeconds >= Math.Max(options.ObservationSeconds, options.MinimumDurationSeconds)))].Sample.TimestampUtc
                        : a[start].Sample.TimestampUtc.AddSeconds(Math.Max(options.ObservationSeconds, options.MinimumDurationSeconds)),
                    EvidenceStatus = confirmed ? "CONFIRMED" : "REVIEW_REQUIRED",
                    ProductionDecision = confirmed ? "AUTO_EXCLUDE" : "KEEP_PENDING_REVIEW"
                });
                i = last + 1;
                consumed = i;
            }
        }
        return result;
    }

    internal static bool Continuous(FlightLegSample a, FlightLegSample b) =>
        a.SourceFile.Equals(b.SourceFile, StringComparison.OrdinalIgnoreCase) &&
        b.SourceRecord == a.SourceRecord + 1 &&
        (b.TimestampUtc - a.TimestampUtc).TotalSeconds is >= 0 and <= 1;

    private static bool IsHeightJump(FlightLegSample a, FlightLegSample b) =>
        a.AltitudeM is { } h0 && b.AltitudeM is { } h1 && double.IsFinite(h0) && double.IsFinite(h1) &&
        (b.TimestampUtc - a.TimestampUtc).TotalSeconds is > 0 and <= 1 && Math.Abs(h1 - h0) > 20;

    private static bool SmoothVertical(FlightLegSample[] points, out int confirmed)
    {
        confirmed = 0;
        double up = 0, down = 0;
        for (int i = 1; i < points.Length; i++)
        {
            double dt = (points[i].TimestampUtc - points[i - 1].TimestampUtc).TotalSeconds;
            if (points[i].AltitudeM is not { } h1 || points[i - 1].AltitudeM is not { } h0 || !double.IsFinite(h0) || !double.IsFinite(h1) || dt <= 0 || Math.Abs(h1 - h0) / dt > 10)
            { up = down = 0; continue; }
            if (h1 >= h0) up += h1 - h0; else down += h0 - h1;
            if (Math.Max(up, down) >= 5 && Math.Min(up, down) <= Math.Max(up, down) * .2)
            { confirmed = i; return true; }
        }
        return false;
    }

    internal static double DistanceM(FlightLegSample a, FlightLegSample b)
    {
        double lat = (a.Latitude + b.Latitude) * Math.PI / 360;
        double dx = (b.Longitude - a.Longitude) * 111320 * Math.Cos(lat);
        double dy = (b.Latitude - a.Latitude) * 110540;
        return Math.Sqrt(dx * dx + dy * dy);
    }
    private static double Median(IEnumerable<double> values)
    { var a = values.Order().ToArray(); return (a[(a.Length - 1) / 2] + a[a.Length / 2]) / 2; }

    /// <summary>Reuse a confirmed recognizer cut, retaining evidence confirmation separately.
    /// Exclude only the supported cut-to-confirmation interval. Later unrecognised
    /// trajectories need independent evidence and cannot be deleted as a whole.</summary>
    public static IReadOnlyList<ProductionAnomalyInterval> ConfirmedDepartureTails(SurveyExecutionResult execution)
    {
        var result = new List<ProductionAnomalyInterval>();
        var streams = execution.Legs.SelectMany(l => l.Samples.Select(s =>
                (Sample: s, Leg: l.FlightLegId, Mission: l.MissionExecutionId)))
            .GroupBy(p => (p.Mission, p.Sample.SourceFile)).ToDictionary(g => g.Key,
                g => g.OrderBy(p => p.Sample.TimestampUtc).ThenBy(p => p.Sample.SourceRecord).ToArray());
        foreach (var pass in execution.Passes)
        {
            if (pass.EndBoundary is not { } boundary || boundary.Kind is not
                ("TERMINAL_TURN" or "TERMINAL_STATIONARY_VERTICAL" or "TERMINAL_STATIONARY_DEPARTURE" or "DEPARTURE_CONFIRMED")) continue;
            if (boundary.ConfirmedUtc <= boundary.CutUtc) continue;
            if (!streams.TryGetValue((pass.MissionExecutionId, boundary.SourceFile), out var stream)) continue;
            int After(DateTimeOffset time)
            {
                int lo = 0, hi = stream.Length;
                while (lo < hi) { int middle = lo + (hi - lo) / 2; if (stream[middle].Sample.TimestampUtc <= time) lo = middle + 1; else hi = middle; }
                return lo;
            }
            var points = stream[After(boundary.CutUtc)..After(boundary.ConfirmedUtc)];
            if (points.Length == 0 || points.All(p => p.Sample.TimestampUtc < boundary.ConfirmedUtc)) continue;
            int end = 0;
            while (end + 1 < points.Length && Continuous(points[end].Sample, points[end + 1].Sample)) end++;
            var tail = points[..(end + 1)];
            if (tail[^1].Sample.TimestampUtc < boundary.ConfirmedUtc) continue;
            string id = "anomaly-" + TrackSegmentationReferenceService.Hash($"{Version}|departure|{pass.PassId}|{tail[0].Sample.SourceRecord}|{tail[^1].Sample.SourceRecord}|{boundary.Kind}")[..24];
            result.Add(new(id, tail[0].Leg, tail[0].Sample.TimestampUtc, tail[^1].Sample.TimestampUtc,
                "CONFIRMED_DEPARTURE_INTERVAL", "已确认离开作业测线；扣除回溯切点到证据确认点的区间，后续未识别轨迹保留独立核验。",
                boundary.SourceFile, tail[0].Sample.SourceRecord, tail[^1].Sample.SourceRecord, tail.Length, 0, null,
                tail.Select(p => new ExecutionSampleReference(p.Sample.SourceFile, p.Sample.SourceRecord, 0)).ToArray())
                { ConfirmedUtc = boundary.ConfirmedUtc, EvidenceStatus = "CONFIRMED", ProductionDecision = "AUTO_EXCLUDE" });
        }
        return result;
    }
}
