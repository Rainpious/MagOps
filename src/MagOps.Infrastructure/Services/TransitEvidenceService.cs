using MagOps.Core.Domain;

namespace MagOps.Infrastructure.Services;

/// <summary>Review hints only: no point deletion, reassignment or boundary changes.</summary>
public static class TransitEvidenceService
{
    public const string Version = "stationary-vertical-ground-context-4-motion-purpose-separation";
    public static object RuleSnapshot => new { Version,
        StationarySeconds = "TerminalStationarySeconds (default 5)", StationaryRadiusM = "TerminalStationaryRadiusM (default 2)",
        StationaryDefinition = "all raw points within a fixed radius; same source; adjacent elapsed time 0..1s",
        VerticalNeighbourSeconds = 15, GroundNeighbourSeconds = 180, NeighbourRadiusM = 50,
        MinimumTransitTraversalM = 80, MinimumTransitSeconds = 5,
        MotionDirectionMinimumSpeedMps = 1.5,
        Authority = "motion/context/hints only; no sample removal or automatic inclusion" };
    private sealed record Episode(string Source, DateTimeOffset Start, DateTimeOffset End, GeoCoordinate Position, double Height, double Radius);
    public static SurveyExecutionResult Annotate(SurveyExecutionResult execution, IReadOnlyList<UavMagCanonicalSample> samples,
        TrackSegmentationInput? input, WorkareaSpatialRange? range)
    {
        var observations = TrackMotionService.Measure(samples.Where(p => p.Longitude.HasValue && p.Latitude.HasValue)
            .Select(p => new SurveyTrackPoint(p.TimestampUtc, new(p.Longitude!.Value, p.Latitude!.Value), p.SourceFile, p.SourceRecord) { AltitudeM = p.AltitudeM }));
        var vertical = new List<Episode>(); var platforms = new List<Episode>();
        foreach (var stream in observations.GroupBy(o => o.Source))
        {
            var a = stream.ToArray(); int quiet = -1;
            for (int i = 0; i < a.Length; i++)
            {
                bool continuous = i == 0 || (a[i].Time - a[i - 1].Time).TotalSeconds == 1;
                bool stationary = a[i].SpeedMps is <= 1.5 && a[i].ClimbMps is { } c && Math.Abs(c) <= .5 && a[i].AltitudeM.HasValue;
                if (!continuous || !stationary)
                { AddPlatform(quiet, i - 1); quiet = -1; }
                if (stationary && quiet < 0) quiet = i;
                if (i + 5 >= a.Length || (a[i + 5].Time - a[i].Time).TotalSeconds != 5 ||
                    Enumerable.Range(i, 6).Any(j => !a[j].AltitudeM.HasValue)) continue;
                double net = a[i + 5].AltitudeM!.Value - a[i].AltitudeM!.Value;
                if (Math.Abs(net) < 5) continue;
                var increments = Enumerable.Range(i + 1, 5).Select(j => a[j].AltitudeM!.Value - a[j - 1].AltitudeM!.Value).Where(d => Math.Abs(d) >= .2).ToArray();
                if (increments.Length < 3 || increments.Count(d => Math.Sign(d) == Math.Sign(net)) < increments.Length * .8) continue;
                double radius = Enumerable.Range(i, 6).Max(j => TrackSegmentationReferenceService.Distance(a[i].Coordinate, a[j].Coordinate));
                if (radius <= 3) vertical.Add(new(stream.Key, a[i].Time, a[i + 5].Time, a[i].Coordinate, a[i].AltitudeM!.Value, radius));
            }
            AddPlatform(quiet, a.Length - 1);
            void AddPlatform(int first, int last)
            {
                if (first < 0 || last <= first || (a[last].Time - a[first].Time).TotalSeconds < 30) return;
                double[] h = Enumerable.Range(first, last - first + 1).Select(j => a[j].AltitudeM!.Value).ToArray();
                if (h.Max() - h.Min() > 5) return;
                if (Enumerable.Range(first, last - first + 1).Any(j =>
                    TrackSegmentationReferenceService.Distance(a[first].Coordinate, a[j].Coordinate) > 5)) return;
                platforms.Add(new(stream.Key, a[first].Time, a[last].Time, a[first].Coordinate, h.Average(), 0));
            }
        }
        var reliable = platforms.Where(p =>
        {
            var cluster = platforms.Where(q => TrackSegmentationReferenceService.Distance(q.Position, p.Position) <= 30).ToArray();
            return p.Height <= cluster.Min(q => q.Height) + 10 && cluster.Any(q => q != p &&
                Math.Abs((q.Start - p.Start).TotalSeconds) >= 180 && q.Height <= cluster.Min(v => v.Height) + 10);
        }).ToArray();
        var verticalBySource = vertical.GroupBy(e => e.Source).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);
        var groundBySource = reliable.GroupBy(e => e.Source).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);
        var lookup = samples.ToDictionary(p => (p.SourceFile, p.SourceRecord));
        var passByLeg = execution.Passes.ToDictionary(p => p.FlightLegId);
        var coveredByTask = execution.Passes.GroupBy(p => p.SurveyTaskStableId)
            .ToDictionary(g => g.Key, g => SurveyCoverage.Union(g.SelectMany(p => p.Coverage)).Sum(c => c.End - c.Start));
        var legs = execution.Legs.Select(leg =>
        {
            if (leg.Samples.Count == 0) return leg;
            var evidence = new List<string>();
            var context = new List<string>();
            var motionEvidence = new List<string>();
            string motionKind = "";
            var firstPosition = new GeoCoordinate(leg.Samples[0].Longitude, leg.Samples[0].Latitude);
            var lastPosition = new GeoCoordinate(leg.Samples[^1].Longitude, leg.Samples[^1].Latitude);
            var matching = input?.MatchOptions ?? new WholeLegMatchOptions();
            bool continuous = leg.Samples.All(p => p.SourceFile == leg.Samples[0].SourceFile) &&
                leg.Samples.Zip(leg.Samples.Skip(1)).All(pair =>
                    (pair.Second.TimestampUtc - pair.First.TimestampUtc).TotalSeconds is >= 0 and <= 1);
            bool stationary = continuous && (leg.EndUtc - leg.StartUtc).TotalSeconds >= matching.TerminalStationarySeconds &&
                leg.Samples.All(p => TrackSegmentationReferenceService.Distance(firstPosition, new(p.Longitude, p.Latitude)) <= matching.TerminalStationaryRadiusM);
            if (stationary)
            {
                motionKind = "STATIONARY";
                motionEvidence.Add($"连续水平停留至少{matching.TerminalStationarySeconds:g}秒，原始点半径不超过{matching.TerminalStationaryRadiusM:g}m；不据此自动删除数据");
                var heights = TrackMotionService.Measure(leg.Samples.Select(p => new SurveyTrackPoint(p.TimestampUtc,
                    new(p.Longitude, p.Latitude), p.SourceFile, p.SourceRecord) { AltitudeM = p.AltitudeM }))
                    .Where(o => o.AltitudeM.HasValue).Select(o => o.AltitudeM!.Value).ToArray();
                if (heights.Length >= 6)
                {
                    double net = heights[^1] - heights[0];
                    var increments = heights.Zip(heights.Skip(1)).Select(p => p.Second - p.First).Where(d => Math.Abs(d) >= .2).ToArray();
                    if (Math.Abs(net) >= matching.TerminalVerticalChangeM && increments.Length >= 3 &&
                        increments.Count(d => Math.Sign(d) == Math.Sign(net)) >= increments.Length * .8)
                    {
                        motionKind = "STATIONARY_VERTICAL";
                        motionEvidence.Add("水平停留伴随持续高度变化；原地升降待核验，高程瞬跳不能单独成立");
                    }
                }
            }
            foreach (var endpoint in new[] { leg.Samples[0], leg.Samples[^1] })
            {
                var position = new GeoCoordinate(endpoint.Longitude, endpoint.Latitude);
                var opposite = endpoint == leg.Samples[0] ? lastPosition : firstPosition;
                void AddHint(string text, GeoCoordinate eventPosition)
                {
                    // A neighbouring event is not the purpose of a tiny leg.
                    // A transit hint additionally needs observable travel away
                    // from that region; whole-route fan evidence remains separate.
                    if (!stationary && continuous && (leg.EndUtc - leg.StartUtc).TotalSeconds >= 5 &&
                        TrackSegmentationReferenceService.Distance(position, opposite) >= 80 &&
                        TrackSegmentationReferenceService.Distance(eventPosition, opposite) >= 80)
                        evidence.Add(text);
                    else context.Add(text + "；邻近事件提示，不单独确定往返用途");
                }
                if (verticalBySource.TryGetValue(endpoint.SourceFile, out var episodes))
                {
                    var nearby = episodes.Where(e => endpoint.TimestampUtc >= e.Start.AddSeconds(-15) && endpoint.TimestampUtc <= e.End.AddSeconds(15) &&
                        TrackSegmentationReferenceService.Distance(position, e.Position) <= 50).ToArray();
                    var strong = nearby.FirstOrDefault(e => e.Radius <= 2);
                    if (strong is not null) AddHint("端点附近水平移动不超过2m且持续升降（推断）", strong.Position);
                    else if (nearby.Length > 0) AddHint("端点附近水平移动2–3m且持续升降（漂移复核）", nearby[0].Position);
                }
                var ground = groundBySource.TryGetValue(endpoint.SourceFile, out var grounds) ? grounds.FirstOrDefault(e =>
                    endpoint.TimestampUtc >= e.Start.AddSeconds(-180) && endpoint.TimestampUtc <= e.End.AddSeconds(180) &&
                    TrackSegmentationReferenceService.Distance(position, e.Position) <= 50) : null;
                if (ground is not null) AddHint("接近重复低高度停留位置，时间邻近起降（推断）", ground.Position);
                foreach (var home in input?.KnownTakeoffPositions.Where(p => TrackSegmentationReferenceService.Distance(p, position) <= 50) ?? [])
                    AddHint("端点接近用户指定起降点", home);
            }
            if (range is not null)
            {
                int outside = 0, valid = 0;
                foreach (var p in leg.Samples)
                    if (lookup.TryGetValue((p.SourceFile, p.SourceRecord), out var original) && WorkareaSpatialService.TryProject(original, range, out double x, out double y))
                    { valid++; if (!range.ContainsBuffered(x, y, 5)) outside++; }
                if (valid > 0 && outside >= valid * .8)
                {
                    const string text = "至少80%轨迹位于已有作业范围外（5m缓冲）";
                    if (input?.Enabled == true) context.Add(text + "；范围来源待核实，不作为往返身份依据");
                    else evidence.Add(text);
                }
            }
            // Independent whole-line evidence outranks takeoff proximity.
            // An ACTUAL_TRACK self-fit 100% is deliberately insufficient.
            if (input is not null && passByLeg.TryGetValue(leg.FlightLegId, out var pass) &&
                input.ReferenceGeometry.Any(g => g.Id == pass.SurveyTaskStableId && g.Kind is "SURVEY" or "TIE") &&
                pass.PlannedCoverage >= .95 && pass.StartEndpointDistanceM <= input.MatchOptions.EndpointSearchM * 2 &&
                pass.EndEndpointDistanceM <= input.MatchOptions.EndpointSearchM * 2 &&
                pass.HeadingErrorDeg <= input.RecognitionOptions.AxisToleranceDegrees)
            {
                context.AddRange(evidence.Select(e => e + "；独立测线完整作业证据优先，提示保留"));
                evidence.Clear();
            }
            else if (input is not null && passByLeg.TryGetValue(leg.FlightLegId, out var continuation) &&
                input.ReferenceGeometry.Any(g => g.Id == continuation.SurveyTaskStableId && g.Kind is "SURVEY" or "TIE") &&
                continuation.ExecutionDirection == "FORWARD" && continuation.DurationSec >= 2 &&
                continuation.AlongTrackMonotonicity >= input.MatchOptions.MinimumMonotonicity &&
                continuation.HeadingErrorDeg <= input.RecognitionOptions.AxisToleranceDegrees &&
                continuation.MedianCrossTrackM <= input.MatchOptions.CorridorM &&
                (continuation.StartEndpointDistanceM <= input.MatchOptions.EndpointSearchM * 2 ||
                 continuation.EndEndpointDistanceM <= input.MatchOptions.EndpointSearchM * 2) &&
                coveredByTask.GetValueOrDefault(continuation.SurveyTaskStableId) >= .95)
            {
                // An independently matched continuation of a jointly completed
                // task is not transit merely because it starts near a climb.
                // Keep explicit takeoff-point/range evidence for review.
                var weak = evidence.Where(e => e.Contains("持续升降") || e.Contains("重复低高度")).ToArray();
                context.AddRange(weak.Select(e => e + "；独立参考续飞覆盖证据优先，提示保留"));
                evidence.RemoveAll(e => weak.Contains(e));
            }
            var hints = evidence.Distinct().ToArray();
            string originalReason = leg.ReviewReason.Replace(";SUSPECTED_TRANSIT_REVIEW", "", StringComparison.Ordinal);
            return leg with { TransitEvidence = hints, ContextEvidence = context.Distinct().ToArray(),
                MotionKind = motionKind, MotionEvidence = motionEvidence,
                ReviewReason = hints.Length > 0 ? originalReason + ";SUSPECTED_TRANSIT_REVIEW" : originalReason };
        }).ToArray();
        return execution with { Legs = legs };
    }
}
