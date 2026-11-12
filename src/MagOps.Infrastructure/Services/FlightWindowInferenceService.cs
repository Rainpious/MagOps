using MagOps.Core.Domain;

namespace MagOps.Infrastructure.Services;

public sealed record InferredFlightWindow(
    MissionTimeWindow Window,
    string Status,
    double Confidence,
    string Evidence,
    int PointCount);

public sealed record FlightWindowInferenceResult(
    IReadOnlyList<InferredFlightWindow> Candidates,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Infers candidate physical flight windows from UAV telemetry. These are
/// evidence for review and never become confirmed Mission boundaries by
/// themselves.
/// </summary>
public sealed class FlightWindowInferenceService
{
    public const string AlgorithmVersion = "flight-window-inference-3-second-motion";

    public FlightWindowInferenceResult Infer(IReadOnlyList<UavMagCanonicalSample> samples)
    {
        // Overlapping sources are distinct streams, not one artificial time-interleaved flight.
        var sources = samples.GroupBy(p => p.SourceFile, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(p => p.TimestampUtc).ThenBy(p => p.SourceRecord).ToArray())
            .Where(g => g.Length > 0).OrderBy(g => g[0].TimestampUtc).ToArray();
        var lanes = new List<List<UavMagCanonicalSample>>();
        foreach (var source in sources)
        {
            var lane = lanes.FirstOrDefault(l => l[^1].TimestampUtc < source[0].TimestampUtc &&
                (source[0].TimestampUtc - l[^1].TimestampUtc).TotalSeconds <= 900d &&
                SpeedMps(l[^1], source[0]) <= 100d);
            if (lane is null) { lane = []; lanes.Add(lane); }
            lane.AddRange(source);
        }
        var inferred = lanes.Select(l => InferStream(l)).ToArray();
        int number = 0;
        var candidates = inferred.SelectMany(r => r.Candidates).OrderBy(c => c.Window.StartUtc)
            .Select(c => c with { Window = c.Window with { MissionExecutionId = $"inferred-flight-{++number:D3}" } }).ToArray();
        return new(candidates, inferred.SelectMany(r => r.Warnings).Distinct().ToArray());
    }

    private FlightWindowInferenceResult InferStream(IReadOnlyList<UavMagCanonicalSample> samples)
    {
        var points = samples.Where(x => x.Longitude.HasValue && x.Latitude.HasValue)
            .OrderBy(x => x.TimestampUtc)
            .ThenBy(x => x.SourceFile, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.SourceRecord)
            .ToArray();
        if (points.Length == 0)
            return new([], ["没有可用于推断飞行窗口的 UAV 坐标记录。"]);

        var groups = new List<List<UavMagCanonicalSample>>();
        var relaunches = RelaunchTimes(points);
        var current = new List<UavMagCanonicalSample> { points[0] };
        for (int i = 1; i < points.Length; i++)
        {
            double gap = (points[i].TimestampUtc - points[i - 1].TimestampUtc).TotalSeconds;
            // A battery swap is commonly only a few seconds. Do not use the
            // file boundary or a short pause as a flight boundary. A very long
            // gap remains a weak fallback for separate field sessions.
            bool longGap = gap > 900d || gap < 0d;
            bool returnAndRelaunch = points[i].TimestampUtc.ToUnixTimeSeconds() != points[i - 1].TimestampUtc.ToUnixTimeSeconds() &&
                relaunches.Contains((points[i].SourceFile, points[i].TimestampUtc.ToUnixTimeSeconds()));
            if (longGap || returnAndRelaunch)
            {
                groups.Add(current);
                current = [];
            }
            current.Add(points[i]);
        }
        groups.Add(current);

        var candidates = new List<InferredFlightWindow>();
        var warnings = new List<string>();
        int index = 0;
        foreach (var group in groups.Where(x => x.Count >= 3))
        {
            DateTimeOffset start = group[0].TimestampUtc;
            DateTimeOffset end = group[^1].TimestampUtc;
            bool hasAltitude = group.Any(x => x.AltitudeM.HasValue);
            bool hasFileBoundary = group.Select(x => x.SourceFile).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1;
            string evidence = string.Join("; ", new[]
            {
                hasFileBoundary ? "多个文件连续记录" : "连续时间记录",
                hasAltitude ? "包含高度变化证据" : "无高度字段",
                $"最长间隔 {LargestGap(group):0.#} 秒"
            });
            double confidence = hasAltitude ? 0.62d : 0.48d;
            string status = hasAltitude ? "INFERRED_REVIEW" : "INFERRED_LOW_CONFIDENCE";
            string id = $"inferred-flight-{++index:D3}";
            candidates.Add(new(new MissionTimeWindow(id, "", start, end)
            {
                SourceFiles = group.Select(p => p.SourceFile).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                TaskScopeSource = "AUTO"
            }, status, confidence, evidence, group.Count));
        }
        if (groups.Any(x => x.Count < 3))
            warnings.Add("存在少于 3 个坐标点的时间片，未生成飞行窗口。");
        if (candidates.Count == 0)
            warnings.Add("未能形成候选飞行窗口。");
        return new(candidates, warnings);
    }

    private static double LargestGap(IReadOnlyList<UavMagCanonicalSample> group)
    {
        double largest = 0d;
        for (int i = 1; i < group.Count; i++)
            largest = Math.Max(largest, (group[i].TimestampUtc - group[i - 1].TimestampUtc).TotalSeconds);
        return largest;
    }

    private static HashSet<(string, long)> RelaunchTimes(IReadOnlyList<UavMagCanonicalSample> points)
    {
        var result = new HashSet<(string, long)>();
        var observations = TrackMotionService.Measure(points.Select(p => new SurveyTrackPoint(p.TimestampUtc,
            new(p.Longitude!.Value, p.Latitude!.Value), p.SourceFile, p.SourceRecord) { AltitudeM = p.AltitudeM }));
        foreach (var stream in observations.GroupBy(o => o.Source))
        {
            var a = stream.ToArray();
            DateTimeOffset? quiet = null;
            int lastQuiet = -1;
            bool priorFlight = false;
            for (int i = 0; i < a.Length; i++)
            {
                if (i > 0 && (a[i].Time - a[i - 1].Time).TotalSeconds > 1) { quiet = null; priorFlight = false; }
                if (a[i].SpeedMps is <= 2) { quiet ??= a[i].Time; lastQuiet = i; }
                else if (a[i].SpeedMps is >= 4)
                {
                    if (quiet is { } q && priorFlight && lastQuiet >= 0 && (a[lastQuiet].Time - q).TotalSeconds >= 30 &&
                        GroundPlateau(q, lastQuiet + 1) &&
                        i + 3 < a.Length && (a[i + 3].Time - a[i].Time).TotalSeconds == 3 &&
                        Enumerable.Range(i, 4).All(j => a[j].SpeedMps is >= 4))
                        result.Add((stream.Key, a[i].Time.ToUnixTimeSeconds()));
                    quiet = null; priorFlight = true;
                }
                else if (a[i].SpeedMps is null || lastQuiet < 0 || (a[i].Time - a[lastQuiet].Time).TotalSeconds > 3) quiet = null;
            }
            bool GroundPlateau(DateTimeOffset begin, int end)
            {
                var platform = a.Take(end).Where(o => o.Time >= begin).ToArray();
                var heights = platform.Where(o => o.AltitudeM.HasValue).Select(o => o.AltitudeM!.Value).ToArray();
                return platform.Length >= 30 && heights.Length == platform.Length && heights.Max() - heights.Min() <= 5 &&
                    platform.All(o => TrackSegmentationReferenceService.Distance(platform[0].Coordinate, o.Coordinate) <= 3);
            }
        }
        return result;
    }

    private static double SpeedMps(UavMagCanonicalSample a, UavMagCanonicalSample b)
    {
        double seconds = (b.TimestampUtc - a.TimestampUtc).TotalSeconds;
        if (seconds <= 0d || !a.Longitude.HasValue || !a.Latitude.HasValue ||
            !b.Longitude.HasValue || !b.Latitude.HasValue)
            return double.PositiveInfinity;
        double lat = (a.Latitude.Value + b.Latitude.Value) * Math.PI / 360d;
        double east = (b.Longitude.Value - a.Longitude.Value) * 111_319.49d * Math.Cos(lat);
        double north = (b.Latitude.Value - a.Latitude.Value) * 111_319.49d;
        return Math.Sqrt(east * east + north * north) / seconds;
    }
}
