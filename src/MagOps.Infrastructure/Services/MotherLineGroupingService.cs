using MagOps.Core.Domain;

namespace MagOps.Infrastructure.Services;

public sealed record UnassignedLineEvidence(string LegId, string Status, string Reason,
    IReadOnlyList<string> CandidateIds, double? CrossM, double? AxisDegrees, bool Included);
public sealed record ActualLineNumber(string StableId, string OldLine, int OldOrder, string Line, int Order);

/// <summary>Whole-component validation. Connectivity collects candidates; it never grants identity.</summary>
public static partial class MotherLineGroupingService
{
    public const string Version = "whole-mother-line-7-terminal-coverage";
    private static GeoCoordinate Point(FlightLegSample p) => new(p.Longitude, p.Latitude);
    private static double Span(FlightLeg l) => l.Samples.Count < 2 ? 0 : TrackSegmentationReferenceService.Distance(Point(l.Samples[0]), Point(l.Samples[^1]));
    private static (double Along, double Cross) Project(GeoCoordinate p, PlannedSurveyLineTask t)
    {
        var (x, y) = EndpointLineRecognizer.Delta(t.Start, p); var (dx, dy) = EndpointLineRecognizer.Delta(t.Start, t.End);
        double len = Math.Sqrt(dx * dx + dy * dy);
        return len < 1 ? (0, double.PositiveInfinity) : ((x * dx + y * dy) / len, (x * dy - y * dx) / len);
    }
    private static double Bearing(PlannedSurveyLineTask t) => SurveyDirection.Bearing(t.Start, t.End) ?? 0;
    private static double P95(IEnumerable<double> values)
    { var a = values.Order().ToArray(); return a.Length == 0 ? double.PositiveInfinity : a[(int)Math.Ceiling(a.Length * .95) - 1]; }

    public static (IReadOnlyList<MotherLineRestoreGroup> Groups, IReadOnlyList<UnassignedLineEvidence> Evidence) Candidates(
        AuditProductionResult audit, MotherLineRestorationState state, IReadOnlySet<string> included)
    {
        var suppressed = state.Groups.Where(g => g.Status is "UNDONE" or "REJECTED").SelectMany(g => g.LegIds).ToHashSet();
        var protection = state.ProtectedLegIds.Concat(state.ExcludedLegIds).Concat(suppressed).ToHashSet();
        var whole = Groups(audit, state.Options, protection, included);
        var orphan = Unassigned(audit, state.Options, protection, included,
            state.Groups.Where(g => g.Status == "APPLIED_AUTO").SelectMany(g => g.ShortLegIds).ToHashSet());
        // A bridge may replace a whole group only if it covers every related member.
        // Partial shared bridges must not silently discard the rest of a component.
        var used = new HashSet<string>();
        var bridges = MotherLineRestorationService.Find(audit, state.Options, protection, included, state.Groups.Where(g => g.Status == "APPLIED_AUTO").SelectMany(g => g.ShortLegIds).ToHashSet())
            .Where(g => g.Status is "HIGH" or "REVIEW" && g.OriginalNames.Count > 1)
            .Select(g => g with { AllowShortInclusion = g.Status == "HIGH" }).OrderBy(g => g.Id, StringComparer.Ordinal).Where(g =>
            {
                var ids = g.LegIds.ToHashSet();
                if (ids.Overlaps(used) || whole.Any(w => w.LegIds.Any(ids.Contains) && !w.LegIds.All(ids.Contains))) return false;
                used.UnionWith(ids); return true;
            }).ToArray();
        var automatic = state.Groups.Where(g => g.Status == "APPLIED_AUTO").SelectMany(g => g.ShortLegIds).ToHashSet();
        var continuityIds = bridges.Where(g => g.Status == "HIGH").Concat(orphan.Groups.Where(g => g.Status == "HIGH"))
            .SelectMany(g => g.LegIds).ToHashSet();
        var endpoints = EndpointCandidates(audit, state.Options, protection, included, automatic, whole, continuityIds);
        // Endpoint evidence is evaluated per leg, so a neighbouring turn cannot
        // veto an otherwise valid fragment through the old source-block grouping.
        var endpointIds = endpoints.Groups.Where(g => g.Status is "HIGH" or "REVIEW").SelectMany(g => g.LegIds).ToHashSet();
        var result = bridges.Where(g => !g.LegIds.Any(endpointIds.Contains))
            .Concat(whole.Where(g => !g.LegIds.Any(used.Contains) && !g.LegIds.Any(endpointIds.Contains)))
            .Concat(orphan.Groups.Where(g => !g.LegIds.Any(used.Contains) && !g.LegIds.Any(endpointIds.Contains)))
            .Concat(endpoints.Groups)
            .Select(g => g with { OriginalTasks = (audit.EffectiveTasks ?? []).Where(t => g.OriginalTaskByLeg.Values.Contains(t.SurveyTaskStableId) || t.SurveyTaskStableId == g.TargetId).ToArray() }).ToArray();
        var spatial = SpatialCandidates(audit, state.Options, protection, included,
            state.Groups.Where(g => g.Status is "APPLIED_AUTO" or "APPLIED_MANUAL").SelectMany(g => g.ShortLegIds).ToHashSet(), state.ProtectedLegIds.Except(state.ExcludedLegIds).ToHashSet(),
            result.Where(g => g.Status == "HIGH").SelectMany(g => g.LegIds).ToHashSet());
        var spatialIds = spatial.SelectMany(g => g.LegIds).ToHashSet();
        // A replacement must cover the entire old command. Keep partial conflicts visible.
        spatial = spatial.Where(s => !result.Any(g => (g.Status == "REVIEW" && g.TargetId == s.TargetId && g.LegIds.Count == s.LegIds.Count && g.LegIds.All(s.LegIds.Contains)) || g.Status is "HIGH" or "REVIEW" &&
            g.LegIds.Any(s.LegIds.Contains) && !g.LegIds.All(s.LegIds.Contains))).ToArray();
        spatialIds = spatial.SelectMany(g => g.LegIds).ToHashSet();
        result = result.Where(g => !g.LegIds.Any(spatialIds.Contains)).Concat(spatial).ToArray();
        result = result.SelectMany(g => ClassifySuspects(g, audit, state.Options.MaximumAlongGapM)).ToArray();
        var endpointEvidence = endpoints.Evidence.ToDictionary(e => e.LegId);
        foreach (var g in result) foreach (string id in g.ShortLegIds)
            endpointEvidence[id] = new(id, g.Status, g.Reason, [g.TargetId], g.CrossM, null, included.Contains(id));
        return (result, orphan.Evidence.Select(e => endpointEvidence.GetValueOrDefault(e.LegId) ?? e)
            .Concat(endpoints.Evidence.Where(e => !orphan.Evidence.Any(o => o.LegId == e.LegId))).ToArray());
    }

    public static IReadOnlyList<MotherLineRestoreGroup> Groups(AuditProductionResult audit, MotherLineRestoreOptions options,
        IReadOnlySet<string> protectedIds, IReadOnlySet<string> included, IReadOnlyList<ActualLinePair>? pairs = null)
    {
        options.Validate();
        var tasks = (audit.EffectiveTasks ?? []).ToDictionary(t => t.SurveyTaskStableId);
        var legs = audit.Execution.Legs;
        var occupied = legs.Select(l => l.SurveyTaskStableId).OfType<string>().ToHashSet();
        double? spacing = audit.TrackSegmentation?.RecognitionOptions.ExpectedLineSpacingM;
        if (spacing is not > 0 && audit.InferredAxisDegrees is { } axis)
            spacing = IntermediateSegmentationEvidence.Estimate(tasks.Values.Where(t => occupied.Contains(t.SurveyTaskStableId)).ToArray(), axis).SpacingM;
        pairs ??= ActualLineRegroupingService.Suggest(tasks.Values.ToArray(), audit.Execution, options.CrossM, options.AxisDegrees, options.MaximumAlongGapM, spacing is > 0 ? spacing : null);
        double limit = spacing is > 0 ? Math.Min(options.CrossM, spacing.Value * .2) : options.CrossM;
        var adjacency = new Dictionary<string, HashSet<string>>();
        foreach (var p in pairs)
        {
            if (!tasks.ContainsKey(p.FirstId) || !tasks.ContainsKey(p.SecondId)) continue;
            if (!adjacency.ContainsKey(p.FirstId)) adjacency[p.FirstId] = [];
            if (!adjacency.ContainsKey(p.SecondId)) adjacency[p.SecondId] = [];
            adjacency[p.FirstId].Add(p.SecondId); adjacency[p.SecondId].Add(p.FirstId);
        }
        var groups = new List<MotherLineRestoreGroup>(); var seen = new HashSet<string>();
        foreach (string start in adjacency.Keys.Order(StringComparer.Ordinal))
        {
            if (!seen.Add(start)) continue;
            var members = new HashSet<string> { start }; var queue = new Queue<string>(); queue.Enqueue(start);
            while (queue.TryDequeue(out string? id)) foreach (string neighbor in adjacency[id])
                if (seen.Add(neighbor)) { members.Add(neighbor); queue.Enqueue(neighbor); }
            var selected = legs.Where(l => l.SurveyTaskStableId is { } id && members.Contains(id)).ToArray();
            var target = members.Select(id => tasks[id]).OrderByDescending(t => selected.Where(l => l.SurveyTaskStableId == t.SurveyTaskStableId).Sum(Span))
                .ThenBy(t => t.Order).ThenBy(t => t.SurveyTaskStableId, StringComparer.Ordinal).First();
            var errors = new List<string>();
            double cross = selected.Select(l => P95(l.Samples.Select(p => Math.Abs(Project(Point(p), target).Cross)))).DefaultIfEmpty(0).Max();
            double angle = members.Select(id => TrackSegmentationReferenceService.AxisDifference(Bearing(tasks[id]), Bearing(target))).Max();
            if (cross > limit || angle > options.AxisDegrees || selected.Any(l => l.Samples.Any(p => Math.Abs(Project(Point(p), target).Cross) > limit * 2))) errors.Add("关联候选不能通过同一公共轴验证，存在传递漂移或分叉");
            // Bound every member against every other axis, not just a permissive middle axis.
            if (selected.Any(l => members.Any(id => P95(l.Samples.Select(p => Math.Abs(Project(Point(p), tasks[id]).Cross))) > limit)))
                errors.Add("组合成员相互横偏超限，不能按链式关系归并");
            if (selected.Any(l => l.RouteRole is "CONNECTOR" or "TRANSIT" || l.MotionKind.Length > 0 || l.TransitEvidence.Count > 0)) errors.Add("包含连接、往返或停留用途证据");
            if (selected.Any(l => protectedIds.Contains(l.FlightLegId))) errors.Add("包含人工保护决定，请先明确解除保护");
            if (selected.Any(l => audit.ProductionAnomalies.Any(a => a.FlightLegId == l.FlightLegId && a.ProductionDecision != "USER_RESTORE"))) errors.Add("包含待复核或排除的异常区间");
            string key = "whole:" + TrackSegmentationReferenceService.Hash(string.Join("|", selected.Select(l => l.FlightLegId).Order()));
            // Pair proposals establish spatial identity, not continuous flight or inclusion. Review as a whole.
            groups.Add(new(key, target.SurveyTaskStableId, target.LineName, target.Order, selected.Select(l => l.FlightLegId).Order().ToArray(), [],
                members.Select(id => tasks[id]).OrderBy(t => t.Order).Select(t => t.LineName).ToArray(), errors.Count > 0 ? "CONFLICT" : "REVIEW",
                errors.Count > 0 ? string.Join("；", errors.Distinct()) : $"{members.Count}条关联测线通过整体公共轴检查；归并身份，保留各趟次和纳入选择", cross, angle, 0)
                { OriginalTaskByLeg = selected.ToDictionary(l => l.FlightLegId, l => l.SurveyTaskStableId), AllowShortInclusion = false });
            if (errors.Count > 0 && members.Count > 2)
            {
                // Deterministic disjoint complete-link subsets: no point joins merely
                // because it is near the last member in a drifting chain.
                var available = members.OrderBy(id => tasks[id].Order).ThenBy(id => id, StringComparer.Ordinal).ToList();
                while (available.Count > 0)
                {
                    var cluster = new List<string> { available[0] }; available.RemoveAt(0);
                    foreach (string candidate in available.ToArray())
                    {
                        bool valid = cluster.Any(id => adjacency[id].Contains(candidate)) && cluster.All(id => Groups(audit, options, protectedIds, included,
                            [new(id, candidate, tasks[id].LineName, tasks[candidate].LineName, 0, 0, 0, 0, "SUBSET")]).Single().Status == "REVIEW");
                        if (valid) { cluster.Add(candidate); available.Remove(candidate); }
                    }
                    if (cluster.Count < 2 || cluster.Count == members.Count) continue;
                    var subsetEdges = cluster.Skip(1).Select(id => new ActualLinePair(cluster[0], id,
                        tasks[cluster[0]].LineName, tasks[id].LineName, 0, 0, 0, 0, "SUBSET")).ToArray();
                    groups.AddRange(Groups(audit, options, protectedIds, included, subsetEdges)
                        .Where(g => g.Status == "REVIEW").Select(g => g with { Reason = "冲突组合内可独立核对的完整子组；" + g.Reason }));
                }
            }
        }
        return groups;
    }

    public static (IReadOnlyList<MotherLineRestoreGroup> Groups, IReadOnlyList<UnassignedLineEvidence> Evidence) Unassigned(
        AuditProductionResult audit, MotherLineRestoreOptions options, IReadOnlySet<string> protectedIds, IReadOnlySet<string> included, IReadOnlySet<string>? automaticShortIds = null)
    {
        options.Validate();
        var legs = audit.Execution.Legs;
        var byTask = legs.Where(l => l.SurveyTaskStableId is not null).GroupBy(l => l.SurveyTaskStableId!).ToDictionary(g => g.Key, g => g.ToArray());
        var tasks = (audit.EffectiveTasks ?? []).Where(t => t.GeometryOrigin == "ACTUAL_TRACK" && byTask.ContainsKey(t.SurveyTaskStableId)).ToArray();
        double? spacing = audit.TrackSegmentation?.RecognitionOptions.ExpectedLineSpacingM;
        if (spacing is not > 0 && audit.InferredAxisDegrees is { } axis) spacing = IntermediateSegmentationEvidence.Estimate(tasks, axis).SpacingM;
        double limit = spacing is > 0 ? Math.Min(options.CrossM, spacing.Value * .2) : options.CrossM;
        var proposals = new List<MotherLineRestoreGroup>(); var evidence = new List<UnassignedLineEvidence>();
        // Source-neighbor blocks include multiple consecutive fragments without recursive identity growth.
        var streams = legs.Where(l => l.Samples.Count > 0 && l.Samples.All(p => p.SourceFile == l.Samples[0].SourceFile))
            .GroupBy(l => l.Samples[0].SourceFile).Select(g => g.OrderBy(l => l.Samples[0].SourceRecord).ToArray());
        foreach (var stream in streams)
        for (int i = 0; i < stream.Length; i++)
        {
            if (stream[i].SurveyTaskStableId is not null) continue;
            int first = i;
            while (i + 1 < stream.Length && stream[i + 1].SurveyTaskStableId is null) i++;
            var block = stream[first..(i + 1)]; var points = block.SelectMany(l => l.Samples).ToArray();
            if (points.Length == 0) continue;
            double observedSpan = TrackSegmentationReferenceService.Distance(Point(points[0]), Point(points[^1]));
            double? observedAxis = points.Length >= 3 && observedSpan >= 2 ? SurveyDirection.Bearing(Point(points[0]), Point(points[^1])) : null;
            var near = tasks.Select(t => new { Task = t, Cross = P95(points.Select(p => Math.Abs(Project(Point(p), t).Cross))),
                Gap = Math.Max(0, Math.Max(-points.Max(p => Project(Point(p), t).Along), points.Min(p => Project(Point(p), t).Along) - t.LengthM)),
                Angle = observedAxis is { } a ? TrackSegmentationReferenceService.AxisDifference(a, Bearing(t)) : (double?)null })
                .Where(v => v.Cross <= limit && v.Gap <= options.MaximumAlongGapM && (v.Angle is null || v.Angle <= options.AxisDegrees)).OrderBy(v => v.Cross).ToArray();
            var reasons = new List<string>(); bool conflict = false;
            if (protectedIds.Overlaps(block.Select(l => l.FlightLegId))) { reasons.Add("人工排除或归属保护"); conflict = true; }
            if (block.Any(l => l.RouteRole is "TRANSIT" or "CONNECTOR" || l.MotionKind.Length > 0 || l.TransitEvidence.Count > 0)) { reasons.Add("存在连接、往返或停留证据"); conflict = true; }
            if (block.Any(l => audit.ProductionAnomalies.Any(a => a.FlightLegId == l.FlightLegId && a.ProductionDecision != "USER_RESTORE"))) { reasons.Add("点级异常待复核或排除"); conflict = true; }
            if (near.Length != 1) { reasons.Add(near.Length == 0 ? "未找到满足方向、走廊及沿线范围的母线" : "存在多个竞争母线：" + string.Join("/", near.Select(v => v.Task.LineName))); conflict = true; }
            if (block.Length > 20 || block.Sum(Span) > options.MaximumShortSpanM || observedSpan > options.MaximumShortSpanM) reasons.Add("短段组合跨度或数量超过自动衔接范围");
            if (observedAxis is null || block.Any(l => l.Samples.Count < 3)) reasons.Add("方向或点数证据不足");
            if (near.Length == 1)
            {
                var v = near[0]; var target = v.Task;
                var before = first > 0 ? stream[first - 1] : null; var after = i + 1 < stream.Length ? stream[i + 1] : null;
                bool Anchor(FlightLeg? l) => l?.SurveyTaskStableId == target.SurveyTaskStableId && Span(l) >= 60 && included.Contains(l.FlightLegId) && !(automaticShortIds?.Contains(l.FlightLegId) ?? false);
                bool left = Anchor(before), right = Anchor(after);
                if (!left && !right) reasons.Add("缺少同源、已纳入的相邻长段锚点；仅建议身份，不自动补选");
                var sequence = (left ? before!.Samples.TakeLast(1) : []).Concat(points).Concat(right ? after!.Samples.Take(1) : []).ToArray();
                double maxTime = 0; bool continuous = true;
                for (int j = 1; j < sequence.Length; j++)
                {
                    var a = sequence[j - 1]; var b = sequence[j]; double dt = (b.TimestampUtc - a.TimestampUtc).TotalSeconds;
                    maxTime = Math.Max(maxTime, dt);
                    if (dt < 0 || b.SourceRecord <= a.SourceRecord) { reasons.Add("时间或记录顺序倒退"); conflict = true; }
                    if (dt > options.MaximumBoundarySeconds || b.SourceRecord != a.SourceRecord + 1 ||
                        TrackSegmentationReferenceService.Distance(Point(a), Point(b)) > Math.Max(15, Math.Max(0, dt) * 30)) continuous = false;
                }
                if (!continuous) reasons.Add("时间、来源记录缺口或观测跳跃，不自动补选");
                double sign = Project(Point(sequence[^1]), target).Along >= Project(Point(sequence[0]), target).Along ? 1 : -1;
                if (sequence.Zip(sequence.Skip(1)).Any(p => (Project(Point(p.Second), target).Along - Project(Point(p.First), target).Along) * sign < -1))
                { reasons.Add("沿线反向或重复重叠，不是连续衔接"); conflict = true; }
                if (points.Any(p => Math.Abs(Project(Point(p), target).Cross) > limit * 2)) reasons.Add("存在离群横偏，需核对异常点");
                // Include only unassigned members in this command. Anchors remain immutable evidence.
                string id = "orphan:" + TrackSegmentationReferenceService.Hash(target.SurveyTaskStableId + "|" + string.Join("|", block.Select(l => l.FlightLegId)));
                string reason = reasons.Count > 0 ? string.Join("；", reasons.Distinct()) : $"{(left && right ? "两端" : "单端")}锚点、同源连续观测、唯一母线；横偏阈值{limit:0.##}m，轴差阈值{options.AxisDegrees:0.##}°";
                proposals.Add(new(id, target.SurveyTaskStableId, target.LineName, target.Order, block.Select(l => l.FlightLegId).ToArray(), block.Select(l => l.FlightLegId).ToArray(),
                    ["未归属"], conflict ? "CONFLICT" : reasons.Count > 0 ? "REVIEW" : "HIGH", reason, v.Cross, v.Angle ?? 0, maxTime)
                    { OriginalTaskByLeg = block.ToDictionary(l => l.FlightLegId, l => l.SurveyTaskStableId), AllowShortInclusion = reasons.Count == 0,
                        AnchorLegIds = new[] { left ? before!.FlightLegId : null, right ? after!.FlightLegId : null }.OfType<string>().ToArray() });
            }
            string status = conflict ? "CONFLICT" : reasons.Count > 0 ? "REVIEW" : "HIGH";
            foreach (var l in block) evidence.Add(new(l.FlightLegId, status, reasons.Count > 0 ? string.Join("；", reasons.Distinct()) : "同源相邻锚点、连续观测及唯一母线，可自动归属", near.Select(v => v.Task.SurveyTaskStableId).ToArray(), near.FirstOrDefault()?.Cross, near.FirstOrDefault()?.Angle, included.Contains(l.FlightLegId)));
        }
        var explained = evidence.Select(e => e.LegId).ToHashSet();
        foreach (var l in legs.Where(l => l.SurveyTaskStableId is null && !explained.Contains(l.FlightLegId)))
            evidence.Add(new(l.FlightLegId, "REVIEW", "无有效观测或跨来源航段，不能证明时序连续；需明确指定归属", [], null, null, included.Contains(l.FlightLegId)));
        return (proposals, evidence);
    }

    public static (PlannedSurveyLineTask[] Tasks, IReadOnlyList<ActualLineNumber> Numbers) Renumber(
        IReadOnlyList<PlannedSurveyLineTask> tasks, SurveyExecutionResult original, SurveyExecutionResult current)
    {
        var currentByLeg = current.Legs.ToDictionary(l => l.FlightLegId);
        var byTask = tasks.ToDictionary(t => t.SurveyTaskStableId);
        var occupied = current.Legs.Select(l => l.SurveyTaskStableId).OfType<string>().ToHashSet();
        var absorbed = original.Legs.Select(l => l.SurveyTaskStableId).OfType<string>().Distinct().Where(id =>
            !occupied.Contains(id) && byTask.TryGetValue(id, out var t) && t.GeometryOrigin == "ACTUAL_TRACK").ToHashSet();
        var remaining = tasks.Where(t => !absorbed.Contains(t.SurveyTaskStableId)).ToArray();
        var origins = original.Legs.Where(l => l.SurveyTaskStableId is { } id && byTask.ContainsKey(id) &&
            currentByLeg.TryGetValue(l.FlightLegId, out var now) && now.SurveyTaskStableId is not null)
            .GroupBy(l => currentByLeg[l.FlightLegId].SurveyTaskStableId!).ToDictionary(g => g.Key, g => g.Min(l => byTask[l.SurveyTaskStableId!].Order));
        int Key(PlannedSurveyLineTask t) => Math.Min(t.Order, origins.GetValueOrDefault(t.SurveyTaskStableId, t.Order));
        var order = remaining.Where(t => t.GeometryOrigin == "ACTUAL_TRACK").OrderBy(Key).ThenBy(t => t.SurveyTaskStableId, StringComparer.Ordinal).ToArray();
        var numbers = order.Select((t, i) => new ActualLineNumber(t.SurveyTaskStableId, t.LineName, t.Order, "r" + (i + 1), i + 1)).ToArray();
        var map = numbers.ToDictionary(n => n.StableId);
        var absorbedNumbers = absorbed.Order().SelectMany(id =>
        {
            var targets = original.Legs.Where(l => l.SurveyTaskStableId == id && currentByLeg.ContainsKey(l.FlightLegId))
                .Select(l => currentByLeg[l.FlightLegId].SurveyTaskStableId).OfType<string>().Distinct().ToArray();
            return targets.Where(map.ContainsKey).Select(target => new ActualLineNumber(id, byTask[id].LineName, byTask[id].Order, map[target].Line, map[target].Order));
        }).ToArray();
        return (remaining.Select(t => map.TryGetValue(t.SurveyTaskStableId, out var n) ? t with { LineName = n.Line, Order = n.Order } : t).ToArray(), numbers.Concat(absorbedNumbers).ToArray());
    }
}
