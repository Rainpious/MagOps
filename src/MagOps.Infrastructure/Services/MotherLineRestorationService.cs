using System.Text.Json;
using MagOps.Core.Domain;

namespace MagOps.Infrastructure.Services;

public sealed record MotherLineRestoreOptions(double CrossM = 10, double AxisDegrees = 3,
    double MaximumShortSpanM = 120, double MaximumBoundarySeconds = 2, double MaximumAlongGapM = 200)
{
    public void Validate()
    {
        if (!double.IsFinite(CrossM) || CrossM <= 0 || CrossM > 100 || !double.IsFinite(AxisDegrees) || AxisDegrees <= 0 || AxisDegrees > 15 ||
            !double.IsFinite(MaximumShortSpanM) || MaximumShortSpanM < 1 || MaximumShortSpanM > 1000 ||
            !double.IsFinite(MaximumBoundarySeconds) || MaximumBoundarySeconds <= 0 || MaximumBoundarySeconds > 8 ||
            !double.IsFinite(MaximumAlongGapM) || MaximumAlongGapM < 0 || MaximumAlongGapM > 10000)
            throw new InvalidDataException("自动恢复母线参数无效。");
    }
}

public sealed record MotherLineLegDecision(string LegId, bool Confirmed, bool Included, string QualityStatus, string? TaskId);
public sealed record MotherLineRestoreGroup(string Id, string TargetId, string TargetName, int TargetOrder,
    IReadOnlyList<string> LegIds, IReadOnlyList<string> ShortLegIds, IReadOnlyList<string> OriginalNames,
    string Status, string Reason, double CrossM, double AngleDegrees, double MaximumTimeGapSeconds)
{
    public IReadOnlyList<MotherLineLegDecision> Before { get; init; } = [];
    public IReadOnlyList<MotherLineLegDecision> After { get; init; } = [];
    public int AddedRecords { get; init; }
    public string BatchId { get; init; } = "";
    public bool AllowShortInclusion { get; init; } = true;
    public string SuspectKind { get; init; } = "";
    public IReadOnlyDictionary<string, int> TurnBoundaryRecords { get; init; } = new Dictionary<string, int>();
    public IReadOnlyList<PlannedSurveyLineTask> OriginalTasks { get; init; } = [];
    public IReadOnlyList<string> AnchorLegIds { get; init; } = [];
    public IReadOnlyList<string>? ProtectedBefore { get; init; }
    public IReadOnlyDictionary<string, string?> OriginalTaskByLeg { get; init; } = new Dictionary<string, string?>();
}
public sealed record MotherLineRestorationState
{
    public string ScopeKey { get; init; } = "";
    public bool Enabled { get; init; } = true;
    public MotherLineRestoreOptions Options { get; init; } = new();
    public IReadOnlyList<MotherLineRestoreGroup> Groups { get; init; } = [];
    public IReadOnlyList<string> RejectedGroupIds { get; init; } = [];
    public IReadOnlyList<string> ProtectedLegIds { get; init; } = [];
    public IReadOnlyList<string> ExcludedLegIds { get; init; } = [];
    public bool ProtectLegacySelection { get; init; }
    public string CheckedKey { get; init; } = "";
    public IReadOnlyList<UnassignedLineEvidence> UnassignedEvidence { get; init; } = [];
    public IReadOnlyList<string> RetainedIndependentLegIds { get; init; } = [];
    public string IndependentDecisionScope { get; init; } = "";
}

/// <summary>Suggest whole identity restorations using temporal neighbors and observed point corridors.
/// Never edits the audit, manufactures gap observations, chooses production samples or clusters by transitive proximity.</summary>
public static class MotherLineRestorationService
{
    public const string Version = "mother-line-bridge-1";
    private static double Span(FlightLeg leg) => leg.Samples.Count < 2 ? 0 :
        TrackSegmentationReferenceService.Distance(new(leg.Samples[0].Longitude, leg.Samples[0].Latitude),
            new(leg.Samples[^1].Longitude, leg.Samples[^1].Latitude));
    private static GeoCoordinate Point(FlightLegSample p) => new(p.Longitude, p.Latitude);
    private static (double Along, double Cross) Project(GeoCoordinate p, PlannedSurveyLineTask task)
    {
        var (x, y) = EndpointLineRecognizer.Delta(task.Start, p);
        var (dx, dy) = EndpointLineRecognizer.Delta(task.Start, task.End);
        double length = Math.Sqrt(dx * dx + dy * dy);
        return length > 0 ? ((x * dx + y * dy) / length, (x * dy - y * dx) / length) : (0, double.PositiveInfinity);
    }

    public static string CheckKey(string scope, SurveyExecutionResult execution, MotherLineRestorationState state,
        IEnumerable<string> included, IEnumerable<string> anomalyIds) => TrackSegmentationReferenceService.Hash(JsonSerializer.Serialize(new
        { Version, GroupVersion = MotherLineGroupingService.Version, scope, state.Options, state.ProtectedLegIds, state.ExcludedLegIds, state.RejectedGroupIds,
            Legs = execution.Legs.Select(l => new { l.FlightLegId, l.SurveyTaskStableId }), Included = included.Order().ToArray(), Anomalies = anomalyIds.Order().ToArray() }));

    public static IReadOnlyList<MotherLineRestoreGroup> Find(AuditProductionResult audit, MotherLineRestoreOptions options,
        IReadOnlySet<string> protectedLegIds, IReadOnlySet<string> includedLegIds, IReadOnlySet<string>? automaticShortIds = null)
    {
        options.Validate();
        var tasks = (audit.EffectiveTasks ?? []).ToDictionary(t => t.SurveyTaskStableId);
        var occupied = audit.Execution.Legs.Select(l => l.SurveyTaskStableId).OfType<string>().ToHashSet();
        var actual = tasks.Values.Where(t => t.GeometryOrigin == "ACTUAL_TRACK" && occupied.Contains(t.SurveyTaskStableId)).ToArray();
        if (actual.Length == 0) return [];
        double? spacing = audit.TrackSegmentation?.RecognitionOptions.ExpectedLineSpacingM is { } explicitSpacing && explicitSpacing > 0 ? explicitSpacing :
            audit.InferredAxisDegrees is { } axis ? IntermediateSegmentationEvidence.Estimate(actual, axis).SpacingM : null;
        double crossLimit = spacing is > 0 ? Math.Min(options.CrossM, spacing.Value * .2) : options.CrossM;
        bool Anchor(FlightLeg l) => l.SurveyTaskStableId is { } id && tasks.TryGetValue(id, out var t) && t.GeometryOrigin == "ACTUAL_TRACK" &&
            Span(l) >= 60 && l.Samples.Count >= 3 && !(automaticShortIds?.Contains(l.FlightLegId) ?? false);
        var anomalies = audit.ProductionAnomalies.Where(a => a.ProductionDecision != "USER_RESTORE").Select(a => a.FlightLegId).ToHashSet();
        var raw = new List<MotherLineRestoreGroup>();
        var all = audit.Execution.Legs;
        var streams = all.Where(l => l.Samples.Count > 0 && l.Samples.All(p => p.SourceFile == l.Samples[0].SourceFile))
            .GroupBy(l => l.Samples[0].SourceFile).Select(g => g.OrderBy(l => l.Samples[0].SourceRecord).ToArray()).ToList();
        // Cross-file neighbors can be reviewed together, but never auto-applied.
        if (streams.Count > 1) streams.Add(all.Where(l => l.Samples.Count > 0).OrderBy(l => l.StartUtc).ToArray());
        foreach (var stream in streams)
        {
            var ordered = stream;
            for (int i = 0; i < ordered.Length - 2; i++)
            {
                var left = ordered[i];
                if (!Anchor(left)) continue;
                int j = i + 1;
                var shorts = new List<FlightLeg>();
                while (j < ordered.Length && ordered[j].SurveyTaskStableId is null && shorts.Count < 20 && Span(ordered[j]) <= options.MaximumShortSpanM)
                { shorts.Add(ordered[j++]); }
                if (shorts.Count == 0 || j >= ordered.Length || !Anchor(ordered[j]) || shorts.Sum(Span) > options.MaximumShortSpanM) continue;
                var right = ordered[j];
                var ids = new[] { left.SurveyTaskStableId!, right.SurveyTaskStableId! }.ToHashSet();
                var members = all.Where(l => l.SurveyTaskStableId is { } id && ids.Contains(id)).Concat(shorts)
                    .DistinctBy(l => l.FlightLegId).ToArray();
                // Prefer the identity with more observed long-track support; ties use existing order/ID.
                var target = ids.Select(id => tasks[id]).OrderByDescending(t => all.Where(l => l.SurveyTaskStableId == t.SurveyTaskStableId).Sum(Span))
                    .ThenBy(t => t.Order).ThenBy(t => t.SurveyTaskStableId, StringComparer.Ordinal).First();
                var sequence = new[] { left }.Concat(shorts).Append(right).ToArray();
                var observations = sequence.SelectMany(l => l.Samples).ToArray();
                var reasons = new List<string>(); bool conflict = false;
                void Conflict(string reason) { conflict = true; reasons.Add(reason); }
                if (members.SelectMany(l => l.Samples).Select(p => p.SourceFile).Distinct().Count() > 1)
                    reasons.Add("跨来源组合，仅供整体核对");
                double angle = TrackSegmentationReferenceService.AxisDifference(SurveyDirection.Bearing(tasks[left.SurveyTaskStableId!].Start, tasks[left.SurveyTaskStableId!].End) ?? 0,
                    SurveyDirection.Bearing(tasks[right.SurveyTaskStableId!].Start, tasks[right.SurveyTaskStableId!].End) ?? 0);
                if (angle > options.AxisDegrees) Conflict("两端轴向不一致");
                double cross = members.SelectMany(l => l.Samples).Select(p => Math.Abs(Project(Point(p), target).Cross)).DefaultIfEmpty(double.PositiveInfinity).Max();
                if (cross > crossLimit) Conflict("组合内存在超出同轴走廊的记录");
                if (members.Any(l => l.RouteRole is "TRANSIT" or "CONNECTOR" || l.MotionKind.Length > 0 || l.TransitEvidence.Count > 0))
                    Conflict("存在连接、往返或停留证据");
                if (members.Any(l => protectedLegIds.Contains(l.FlightLegId))) Conflict("包含人工保护的归属或纳入决定");
                if (!includedLegIds.Contains(left.FlightLegId) || !includedLegIds.Contains(right.FlightLegId)) Conflict("两端未同时纳入，保持用户选择");
                if (members.Any(l => anomalies.Contains(l.FlightLegId))) Conflict("包含待复核或排除的异常区间");
                if (shorts.Any(l => l.Samples.Count < 3)) reasons.Add("短段观测不足3点");
                double timeGap = 0;
                for (int k = 1; k < observations.Length; k++)
                {
                    var a = observations[k - 1]; var b = observations[k];
                    double dt = (b.TimestampUtc - a.TimestampUtc).TotalSeconds;
                    timeGap = Math.Max(timeGap, dt);
                    if (dt < 0 || a.SourceFile == b.SourceFile && b.SourceRecord <= a.SourceRecord) Conflict("原始时间或记录顺序不连续");
                    else if (dt > options.MaximumBoundarySeconds || a.SourceFile == b.SourceFile && b.SourceRecord != a.SourceRecord + 1)
                        reasons.Add("存在时间或来源记录缺口");
                    if (TrackSegmentationReferenceService.Distance(Point(a), Point(b)) > Math.Max(15, Math.Max(0, dt) * 30))
                        reasons.Add("相邻观测距离异常或缺失");
                }
                double sign = Project(Point(left.Samples[^1]), target).Along >= Project(Point(left.Samples[0]), target).Along ? 1 : -1;
                if (observations.Zip(observations.Skip(1)).Any(pair =>
                    (Project(Point(pair.Second), target).Along - Project(Point(pair.First), target).Along) * sign < -1))
                    Conflict("沿线推进反向或明显重复重叠");
                var shortPoints = shorts.SelectMany(l => l.Samples).Select(Point).ToArray();
                var shortAlong = shortPoints.Select(p => Project(p, target).Along).ToArray();
                if (shortAlong.Max() - shortAlong.Min() < 2) reasons.Add("短段沿线跨度不足2米");
                foreach (var other in actual.Where(t => !ids.Contains(t.SurveyTaskStableId)))
                {
                    var crossValues = shortPoints.Select(p => Math.Abs(Project(p, other).Cross)).Order().ToArray();
                    if (crossValues[(int)Math.Ceiling(crossValues.Length * .95) - 1] > crossLimit) continue;
                    var range = shortPoints.Select(p => Project(p, other).Along).ToArray();
                    if (range.Max() >= -options.MaximumAlongGapM && range.Min() <= other.LengthM + options.MaximumAlongGapM)
                    { Conflict("存在另一条竞争母测线"); break; }
                }
                if (shorts.Sum(Span) > options.MaximumAlongGapM) reasons.Add("待衔接范围超过沿线缺口上限");
                string idKey = TrackSegmentationReferenceService.Hash(string.Join("|", sequence.Select(l => l.FlightLegId)) + "/" + string.Join("|", ids.Order()));
                var names = ids.Select(id => tasks[id]).OrderBy(t => t.Order).Select(t => t.LineName).ToArray();
                raw.Add(new(idKey, target.SurveyTaskStableId, target.LineName, target.Order,
                    members.Select(l => l.FlightLegId).Order().ToArray(), shorts.Select(l => l.FlightLegId).ToArray(), names,
                    conflict ? "CONFLICT" : reasons.Count > 0 ? "REVIEW" : "HIGH",
                    reasons.Count > 0 ? string.Join("；", reasons.Distinct()) : "同一来源时序连续、三段同轴且沿线推进一致、无竞争或用途冲突",
                    cross, angle, timeGap) { OriginalTaskByLeg = members.ToDictionary(l => l.FlightLegId, l => l.SurveyTaskStableId) });
            }
        }
        // Shared identities must be evaluated as a complete group. Do not resolve via pairwise chaining.
        raw = raw.DistinctBy(g => g.Id).ToList();
        return raw.Select(g => g.Status != "CONFLICT" && raw.Any(other => other.Id != g.Id && other.LegIds.Intersect(g.LegIds).Any())
            ? g with { Status = "REVIEW", Reason = g.Reason + "；与其他组合共享航段，需整体核对" }
            : g).ToArray();
    }
}
