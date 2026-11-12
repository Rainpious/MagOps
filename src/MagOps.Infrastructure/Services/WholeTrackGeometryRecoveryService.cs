using System.Globalization;
using MagOps.Core.Domain;

namespace MagOps.Infrastructure.Services;

/// <summary>Second-pass whole-track matching. Keeps source points, existing identities and QC authority.</summary>
public static class WholeTrackGeometryRecoveryService
{
    public const string Version = "whole-track-geometry-recovery-1";
    public static object RuleSnapshot => new { Version, DefaultEnabled = true, RankingSamples = 256,
        FullPointVerification = true, MinimumSeconds = 5, MinimumMonotonicity = .9, MinimumMovingRatio = .5,
        MaximumStepM = 50, MaximumSpreadM = "max(6,CorridorM*2)", WinnerScoreRatio = .7,
        ExistingIdentity = "PRESERVE", QualityAuthority = "PENDING_QC" };
    public static SurveyExecutionResult Apply(SurveyExecutionResult execution,
        IReadOnlyList<PlannedSurveyLineTask> tasks, IReadOnlyList<MissionTimeWindow> windows,
        WholeLegMatchOptions options)
    {
        if (!options.EnableGeometryRecovery || tasks.Count == 0) return execution;
        Validate(options);
        var uniqueTasks = tasks.Where(t => t.GeometryOrigin != "ACTUAL_TRACK")
            .GroupBy(t => t.SurveyTaskStableId, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Key.Length > 0 && g.Count() == 1).Select(g => g.Single()).ToArray();
        if (uniqueTasks.Length == 0) return execution;
        var index = new SurveyTaskSpatialIndex(uniqueTasks, options.GeometryRecoverySearchM);
        var byId = uniqueTasks.ToDictionary(t => t.SurveyTaskStableId, StringComparer.OrdinalIgnoreCase);
        var windowMap = windows.ToDictionary(w => w.MissionExecutionId, StringComparer.OrdinalIgnoreCase);
        var passes = execution.Passes.ToList();
        int assigned = 0, suggested = 0;
        var legs = execution.Legs.Select(Recover).ToArray();
        return execution with { Legs = legs, Passes = passes.OrderBy(p => p.EndUtc).ThenBy(p => p.PassId, StringComparer.Ordinal).ToArray(),
            Warnings = execution.Warnings.Concat(assigned + suggested == 0 ? Array.Empty<string>() :
                new[] { $"{Version}: 补识别{assigned}段，竞争候选{suggested}段；依据已保存，质量与生产纳入仍分别复核。" }).Distinct().ToArray() };

        FlightLeg Recover(FlightLeg leg)
        {
            if (leg.SurveyTaskStableId is not null || leg.Classification != "SURVEY_CANDIDATE" ||
                leg.CandidateTaskStableIds.Count > 0 || leg.Samples.Count < 5 ||
                leg.RouteRole is "CONNECTOR" or "TRANSIT" || leg.TransitEvidence.Count > 0 ||
                leg.ReviewReason.Contains("CONNECTOR", StringComparison.Ordinal) ||
                leg.ReviewReason.Contains("EXPLICIT_AXIS_MISMATCH", StringComparison.Ordinal) ||
                !windowMap.TryGetValue(leg.MissionExecutionId, out var window)) return leg;
            var points = leg.Samples.Select(s => new SurveyTrackPoint(s.TimestampUtc,
                new(s.Longitude, s.Latitude), s.SourceFile, s.SourceRecord) { AltitudeM = s.AltitudeM }).ToArray();
            if (points.Any(p => !WorkareaSpatialService.ValidCoordinate(p.Coordinate.Longitude, p.Coordinate.Latitude) ||
                p.TimestampUtc < window.StartUtc || p.TimestampUtc > window.EndUtc ||
                window.SourceFiles.Count > 0 && !window.SourceFiles.Contains(p.SourceFile, StringComparer.OrdinalIgnoreCase))) return leg;
            for (int i = 1; i < points.Length; i++)
            {
                double dt = (points[i].TimestampUtc - points[i - 1].TimestampUtc).TotalSeconds;
                if (dt < 0 || dt > options.MaximumEvidenceGapSeconds ||
                    !string.Equals(points[i].SourceFile, points[i - 1].SourceFile, StringComparison.OrdinalIgnoreCase) ||
                    points[i].SourceRecord != points[i - 1].SourceRecord + 1) return leg;
            }
            if ((points[^1].TimestampUtc - points[0].TimestampUtc).TotalSeconds < 5) return leg;
            var ranked = index.Nearby(points).Where(t => window.SurveyTaskStableIds.Contains(t.SurveyTaskStableId, StringComparer.OrdinalIgnoreCase))
                .Select(t => Evaluate(points, t, options)).Where(e => e is not null).Select(e => e!)
                .OrderBy(e => e.Score).ThenBy(e => e.TaskId, StringComparer.Ordinal).Take(3).ToArray();
            if (ranked.Length == 0) return leg;
            var best = ranked[0];
            bool unique = ranked.Length == 1 ||
                ranked[1].MedianDistanceM - best.MedianDistanceM >= options.GeometryRecoveryCompetitionGapM &&
                best.Score <= ranked[1].Score * .7;
            var evidence = string.Format(CultureInfo.InvariantCulture,
                "整段几何补识别：{0}；距离中位数{1:0.##}m/P95 {2:0.##}m；有限范围{3:P1}；轴差{4:0.##}°；跨度{5:0.##}m；{6}。",
                best.TaskId, best.MedianDistanceM, best.P95DistanceM, best.FinitePointRatio, best.AxisErrorDeg, best.SpanM,
                unique ? "唯一几何对应，质量及用途仍需复核" : "邻线竞争，保留候选不改身份");
            var updated = leg with { GeometryCandidates = ranked,
                ContextEvidence = leg.ContextEvidence.Concat([evidence]).Distinct().ToArray() };
            if (!unique)
            {
                suggested++;
                return updated with { CandidateTaskStableIds = ranked.Select(e => e.TaskId).ToArray(),
                    ReviewReason = "GEOMETRY_COMPETITION_REVIEW" };
            }
            var task = byId[best.TaskId];
            updated = updated with { Classification = "SURVEY", SurveyTaskStableId = best.TaskId,
                ReviewReason = "GEOMETRY_RECOVERY_REVIEW" };
            var pass = WholeLegMatcherService.CreateManualAssignmentPass(updated, task, options);
            if (pass is null) return leg;
            double[] distances = points.Select(p => Project(task, p.Coordinate).Cross).ToArray();
            var motion = TrackMotionService.Measure(points);
            double speedOutside = motion.Count == 0 ? 0 : motion.Count(m => m.SpeedMps is { } speed &&
                (speed < options.MinimumFlightSpeedMps || speed > options.MaximumFlightSpeedMps)) / (double)motion.Count;
            var quality = new FlightQualityEvidence(distances.Count(d => Math.Abs(d) <= options.CoreM) / (double)distances.Length,
                distances.Count(d => Math.Abs(d) <= options.CorridorM) / (double)distances.Length, speedOutside,
                distances.Count(d => Math.Abs(d) > options.CorridorM), points.Any(p => p.AltitudeM.HasValue),
                ["整段几何补识别，不代表飞行质量合格；速度与用途由后续质量/上下文阶段核验。"])
            { MedianSignedCrossTrackM = Median(distances.Order().ToArray()),
                LateralSignConsistency = Math.Max(distances.Count(d => d >= 0), distances.Count(d => d <= 0)) / (double)distances.Length };
            ExecutionBoundaryEvidence Boundary(FlightLegSample s) => new("GEOMETRY_TRACK_LIMIT_REVIEW", s.TimestampUtc, s.TimestampUtc, s.SourceFile, s.SourceRecord);
            passes.Add(pass with { MatchMethod = "WHOLE_TRACK_GEOMETRY", MatchConfidence = Math.Clamp(1 / (1 + best.Score), .1, .9),
                ExecutionDirection = Project(task, points[^1].Coordinate).Along >= Project(task, points[0].Coordinate).Along ? "FORWARD" : "REVERSE",
                FlightQuality = quality, StartBoundary = Boundary(leg.Samples[0]), EndBoundary = Boundary(leg.Samples[^1]) });
            assigned++;
            return updated;
        }
    }

    private static GeometryRecoveryEvidence? Evaluate(SurveyTrackPoint[] points, PlannedSurveyLineTask task, WholeLegMatchOptions options)
    {
        var projections = points.Select(p => Project(task, p.Coordinate)).ToArray();
        if (projections.Any(p => !double.IsFinite(p.Along) || !double.IsFinite(p.Cross) || p.Length < 1)) return null;
        double length = projections[0].Length;
        // Finite endpoints are evidence, never an invitation to extend the reference.
        // Reject outside points from automatic identity rather than attaching transit tails.
        double ratio = projections.Count(p => p.Along >= 0 && p.Along <= length) / (double)points.Length;
        if (ratio < options.GeometryRecoveryMinimumFiniteRatio || projections.Any(p => p.Along < -.01 || p.Along > length + .01)) return null;
        double span = Math.Abs(projections[^1].Along - projections[0].Along);
        if (span < options.GeometryRecoveryMinimumSpanM) return null;
        var start = points[0].Coordinate; var end = points[^1].Coordinate;
        if (SurveyDirection.Bearing(start, end) is not { } heading || SurveyDirection.Bearing(task.Start, task.End) is not { } axis) return null;
        double error = TrackSegmentationReferenceService.AxisDifference(heading, axis);
        if (error > options.GeometryRecoveryMaximumAxisDegrees) return null;
        double sign = Math.Sign(projections[^1].Along - projections[0].Along);
        int moving = 0, forward = 0;
        for (int i = 1; i < points.Length; i++)
        {
            double delta = projections[i].Along - projections[i - 1].Along;
            if (Math.Abs(delta) < .1) continue;
            moving++; if (delta * sign > 0) forward++;
            var (x, y) = EndpointLineRecognizer.Delta(points[i - 1].Coordinate, points[i].Coordinate);
            // A straight-looking pair of endpoints must not conceal an interior detour or jump.
            if (Math.Sqrt(x * x + y * y) > 50) return null;
        }
        double monotonic = moving == 0 ? 0 : forward / (double)moving;
        if (monotonic < .9 || moving < points.Length * .5) return null;
        var all = projections.Select(p => Math.Abs(p.Cross)).Order().ToArray();
        if (all[^1] > options.GeometryRecoverySearchM) return null;
        int count = Math.Min(256, points.Length);
        var sample = Enumerable.Range(0, count).Select(i => projections[(int)((long)i * (points.Length - 1) / (count - 1))])
            .Select(p => Math.Abs(p.Cross)).Order().ToArray();
        double median = Median(sample), p95 = all[(int)Math.Ceiling(all.Length * .95) - 1];
        double spread = p95 - all[(int)Math.Floor((all.Length - 1) * .05)];
        if (spread > Math.Max(6, options.CorridorM * 2)) return null;
        double score = median / options.GeometryRecoverySearchM + p95 / options.GeometryRecoverySearchM * .5 +
            error / options.GeometryRecoveryMaximumAxisDegrees * .5 + (1 - monotonic);
        return new(task.SurveyTaskStableId, score, median, p95, ratio, error, monotonic, span, count, points.Length);
    }

    private static double Median(double[] values) => values.Length % 2 == 1 ? values[values.Length / 2] :
        (values[values.Length / 2 - 1] + values[values.Length / 2]) / 2;

    private static (double Along, double Cross, double Length) Project(PlannedSurveyLineTask task, GeoCoordinate point)
    {
        double unit = 111319.49, east = unit * Math.Cos((task.Start.Latitude + task.End.Latitude) * Math.PI / 360);
        double dx = (task.End.Longitude - task.Start.Longitude) * east, dy = (task.End.Latitude - task.Start.Latitude) * unit;
        double x = (point.Longitude - task.Start.Longitude) * east, y = (point.Latitude - task.Start.Latitude) * unit;
        double length = Math.Sqrt(dx * dx + dy * dy);
        return ((x * dx + y * dy) / length, (x * dy - y * dx) / length, length);
    }

    public static void Validate(WholeLegMatchOptions options)
    {
        if (!double.IsFinite(options.GeometryRecoverySearchM) || options.GeometryRecoverySearchM <= 0 || options.GeometryRecoverySearchM > 1000 ||
            !double.IsFinite(options.GeometryRecoveryMinimumSpanM) || options.GeometryRecoveryMinimumSpanM <= 0 ||
            !double.IsFinite(options.GeometryRecoveryMaximumAxisDegrees) || options.GeometryRecoveryMaximumAxisDegrees <= 0 || options.GeometryRecoveryMaximumAxisDegrees >= 90 ||
            !double.IsFinite(options.GeometryRecoveryMinimumFiniteRatio) || options.GeometryRecoveryMinimumFiniteRatio <= 0 || options.GeometryRecoveryMinimumFiniteRatio > 1 ||
            !double.IsFinite(options.GeometryRecoveryCompetitionGapM) || options.GeometryRecoveryCompetitionGapM <= 0)
            throw new InvalidDataException("整段几何补识别参数无效。");
    }
}
