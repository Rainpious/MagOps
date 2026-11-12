using MagOps.Core.Domain;

namespace MagOps.Infrastructure.Services;

public static partial class MotherLineGroupingService
{
    private sealed record EndpointMatch(PlannedSurveyLineTask Task, FlightLeg Anchor, int Side, double Distance, double Cross, double Angle, double Score);

    private static (IReadOnlyList<MotherLineRestoreGroup> Groups, IReadOnlyList<UnassignedLineEvidence> Evidence) EndpointCandidates(
        AuditProductionResult audit, MotherLineRestoreOptions options, IReadOnlySet<string> protection,
        IReadOnlySet<string> included, IReadOnlySet<string> automatic, IReadOnlyList<MotherLineRestoreGroup> whole,
        IReadOnlySet<string> continuityIds)
    {
        var tasks = (audit.EffectiveTasks ?? []).Where(t => t.GeometryOrigin == "ACTUAL_TRACK").ToDictionary(t => t.SurveyTaskStableId);
        var legs = audit.Execution.Legs;
        var anomalies = audit.ProductionAnomalies.Where(a => a.ProductionDecision != "USER_RESTORE").Select(a => a.FlightLegId).ToHashSet();
        var anchors = legs.Where(l => l.SurveyTaskStableId is { } id && tasks.ContainsKey(id) && included.Contains(l.FlightLegId) &&
            !automatic.Contains(l.FlightLegId) && l.Samples.Count >= 3 && Span(l) >= 60 &&
            l.RouteRole is not ("CONNECTOR" or "TRANSIT") && l.MotionKind.Length == 0 && l.TransitEvidence.Count == 0 && !anomalies.Contains(l.FlightLegId)).ToArray();
        double? spacing = audit.TrackSegmentation?.RecognitionOptions.ExpectedLineSpacingM;
        if (spacing is not > 0 && audit.InferredAxisDegrees is { } axis)
            spacing = IntermediateSegmentationEvidence.Estimate(tasks.Values.Where(t => anchors.Any(l => l.SurveyTaskStableId == t.SurveyTaskStableId)).ToArray(), axis).SpacingM;
        double limit = spacing is > 0 ? Math.Min(options.CrossM, spacing.Value * .2) : options.CrossM;
        var proposals = new List<MotherLineRestoreGroup>(); var evidence = new List<UnassignedLineEvidence>();
        foreach (var leg in legs.Where(l => l.SurveyTaskStableId is null && !continuityIds.Contains(l.FlightLegId)).OrderBy(l => l.FlightLegId, StringComparer.Ordinal))
        {
            if (leg.Samples.Count < 3 || Span(leg) < 2) continue;
            var points = leg.Samples; double bearing = SurveyDirection.Bearing(Point(points[0]), Point(points[^1])) ?? 0;
            var matches = new List<EndpointMatch>();
            foreach (var anchor in anchors)
            {
                var distances = new[] { points[0], points[^1] }.Select(p => new[] { anchor.Samples[0], anchor.Samples[^1] }
                    .Min(a => TrackSegmentationReferenceService.Distance(Point(p), Point(a)))).ToArray();
                if (distances.Min() > options.MaximumAlongGapM) continue;
                var task = tasks[anchor.SurveyTaskStableId!];
                double angle = TrackSegmentationReferenceService.AxisDifference(bearing, Bearing(task));
                // Heading is confidence evidence, not an identity veto: a short local
                // bend may still be proven by two distinct anchors of one mother.
                var projected = points.Select(p => Project(Point(p), task)).ToArray();
                double cross = P95(projected.Select(p => Math.Abs(p.Cross)));
                if (cross > limit || projected.Any(p => Math.Abs(p.Cross) > 2 * limit)) continue;
                double sign = projected[^1].Along >= projected[0].Along ? 1 : -1;
                if (projected.Zip(projected.Skip(1)).Any(p => (p.Second.Along - p.First.Along) * sign < -1)) continue;
                var anchorRange = anchor.Samples.Select(p => Project(Point(p), task).Along).ToArray();
                if (Math.Min(projected.Max(p => p.Along), anchorRange.Max()) - Math.Max(projected.Min(p => p.Along), anchorRange.Min()) > limit) continue;
                for (int side = 0; side < 2; side++)
                {
                    var endpoint = side == 0 ? points[0] : points[^1];
                    double distance = distances[side];
                    if (distance > options.MaximumAlongGapM) continue;
                    // A short joins the end facing it, rather than an arbitrary nearest point on a long line.
                    double along = Project(Point(endpoint), task).Along;
                    double other = projected[side == 0 ? ^1 : 0].Along;
                    if (along >= anchorRange.Max() - limit && other < along - 1 || along <= anchorRange.Min() + limit && other > along + 1) continue;
                    double score = cross / limit + distance / Math.Max(1, options.MaximumAlongGapM) + angle / options.AxisDegrees;
                    matches.Add(new(task, anchor, side, distance, cross, angle, score));
                }
            }
            // Independently inspect the nearest eligible assigned leg at each
            // endpoint before whole-fragment geometry filters can discard it.
            var nearestBySide = new List<EndpointMatch[]>();
            for (int side = 0; side < 2; side++)
            {
                var endpoint = side == 0 ? points[0] : points[^1];
                var nearest = anchors.Select(anchor => new { Anchor = anchor, Distance = new[] { anchor.Samples[0], anchor.Samples[^1] }
                        .Min(a => TrackSegmentationReferenceService.Distance(Point(endpoint), Point(a))) })
                    .Where(a => a.Distance <= options.MaximumAlongGapM).Select(candidate =>
                {
                    var anchor = candidate.Anchor;
                    var task = tasks[anchor.SurveyTaskStableId!];
                    var projected = points.Select(p => Project(Point(p), task)).ToArray();
                    return new EndpointMatch(task, anchor, side, candidate.Distance,
                        P95(projected.Select(p => Math.Abs(p.Cross))),
                        TrackSegmentationReferenceService.AxisDifference(bearing, Bearing(task)),
                        candidate.Distance / Math.Max(1, options.MaximumAlongGapM));
                }).GroupBy(m => m.Task.SurveyTaskStableId)
                  .Select(g => g.OrderBy(m => m.Distance).ThenBy(m => m.Anchor.FlightLegId, StringComparer.Ordinal).First())
                  .OrderBy(m => m.Distance).ThenBy(m => m.Task.SurveyTaskStableId, StringComparer.Ordinal).ToArray();
                nearestBySide.Add(nearest);
            }
            bool forcedDoubleNearest = false;
            if (nearestBySide[0].Length > 0 && nearestBySide[1].Length > 0)
            {
                var leftNearest = nearestBySide[0][0]; var rightNearest = nearestBySide[1][0];
                bool leftAmbiguous = nearestBySide[0].Skip(1).Any(m => m.Distance <= leftNearest.Distance * 1.5 + .25);
                bool rightAmbiguous = nearestBySide[1].Skip(1).Any(m => m.Distance <= rightNearest.Distance * 1.5 + .25);
                forcedDoubleNearest = !leftAmbiguous && !rightAmbiguous &&
                    leftNearest.Task.SurveyTaskStableId == rightNearest.Task.SurveyTaskStableId &&
                    leftNearest.Anchor.FlightLegId != rightNearest.Anchor.FlightLegId;
                if (forcedDoubleNearest)
                {
                    // Nearest-neighbour identity has already been compared across
                    // all eligible mothers. Do not rank it again using geometry.
                    matches.Clear();
                    matches.Add(leftNearest); matches.Add(rightNearest);
                }
            }
            // Relax heading only for a pair of opposite endpoints backed by distinct
            // anchors of the very same stable mother identity. Single-end and
            // different-mother candidates retain the original heading threshold.
            var doubleAnchored = matches.GroupBy(m => m.Task.SurveyTaskStableId)
                .Where(g => g.Any(a => a.Side == 0 && g.Any(b => b.Side == 1 && a.Anchor.FlightLegId != b.Anchor.FlightLegId)))
                .Select(g => g.Key).ToHashSet();
            matches.RemoveAll(m => m.Angle > options.AxisDegrees && !doubleAnchored.Contains(m.Task.SurveyTaskStableId));
            var reasons = new List<string>(); bool blocked = false;
            if (protection.Contains(leg.FlightLegId)) { blocked = true; reasons.Add("人工排除、忽略或撤销保护"); }
            if (leg.RouteRole is "CONNECTOR" or "TRANSIT" || leg.MotionKind.Length > 0 || leg.TransitEvidence.Count > 0)
            { blocked = true; reasons.Add("连接、往返或停留用途保护"); }
            if (anomalies.Contains(leg.FlightLegId)) { blocked = true; reasons.Add("异常区间未恢复"); }
            if (points.Zip(points.Skip(1)).Any(p => p.Second.TimestampUtc < p.First.TimestampUtc && p.Second.SourceFile == p.First.SourceFile))
            { blocked = true; reasons.Add("段内时间倒退"); }
            var chosen = new List<EndpointMatch>(); bool competing = false;
            for (int side = 0; side < 2; side++)
            {
                var ranked = matches.Where(m => m.Side == side).GroupBy(m => m.Task.SurveyTaskStableId)
                    .Select(g => g.OrderBy(m => m.Score).ThenBy(m => m.Anchor.FlightLegId, StringComparer.Ordinal).First())
                    .OrderBy(m => m.Score).ThenBy(m => m.Task.SurveyTaskStableId, StringComparer.Ordinal).ToArray();
                if (ranked.Length == 0) continue;
                if (ranked.Length > 1 && ranked[1].Score < ranked[0].Score * 1.5 + .25) competing = true;
                else chosen.Add(ranked[0]);
            }
            var candidateIds = matches.Select(m => m.Task.SurveyTaskStableId).Distinct().Order().ToArray();
            if (candidateIds.Length == 0) continue; // Keep the original whole-block diagnostic if no endpoint is compatible.
            if (competing) reasons.Add("端点存在接近的竞争母线，最佳评分未达到1.5倍加0.25的分离要求");
            if (chosen.Count == 0) reasons.Add("端点无唯一目标");
            bool sameMotherTwoAnchors = chosen.Count == 2 && chosen[0].Side != chosen[1].Side &&
                chosen[0].Task.SurveyTaskStableId == chosen[1].Task.SurveyTaskStableId &&
                chosen[0].Anchor.FlightLegId != chosen[1].Anchor.FlightLegId;
            var ids = chosen.Select(m => m.Task.SurveyTaskStableId).ToHashSet();
            MotherLineRestoreGroup? merged = null;
            if (ids.Count > 1)
            {
                // Include the complete existing component, never discard shared members.
                bool expanded;
                do
                {
                    int previous = ids.Count;
                    foreach (var w in whole.Where(w => w.OriginalTaskByLeg.Values.OfType<string>().Any(ids.Contains)))
                        ids.UnionWith(w.OriginalTaskByLeg.Values.OfType<string>());
                    expanded = ids.Count != previous;
                } while (expanded);
                string first = ids.Order().First();
                var pairs = ids.Where(id => id != first).Select(id => new ActualLinePair(first, id, tasks[first].LineName, tasks[id].LineName, 0, 0, 0, 0, "ENDPOINT")).ToArray();
                var validated = Groups(audit, options, protection, included, pairs);
                merged = validated.FirstOrDefault(g => g.LegIds.Count == legs.Count(l => l.SurveyTaskStableId is { } t && ids.Contains(t)));
                if (merged?.Status != "REVIEW") { blocked = true; reasons.Add("两端指向不同母线，完整组合未通过验证：" + merged?.Reason); }
                if (ids.Any(id => P95(points.Select(p => Math.Abs(Project(Point(p), tasks[id]).Cross))) > limit ||
                    points.Any(p => Math.Abs(Project(Point(p), tasks[id]).Cross) > 2 * limit)))
                { blocked = true; reasons.Add("短段未同时满足两端母线走廊"); }
            }
            var best = chosen.OrderBy(m => m.Score).FirstOrDefault() ?? matches.OrderBy(m => m.Score).First();
            double displayedAngle = chosen.Count > 0 ? chosen.Max(m => m.Angle) : best.Angle;
            if (!sameMotherTwoAnchors && displayedAngle > options.AxisDegrees)
            { blocked = true; reasons.Add("局部偏折缺少同母线双端唯一锚点"); }
            if (sameMotherTwoAnchors && displayedAngle > options.AxisDegrees)
                reasons.Add($"同一母线的两个不同已归属段分别衔接两端；局部轴差{displayedAngle:0.##}°超过{options.AxisDegrees:0.##}°，仅提示复核");
            bool geometryReview = forcedDoubleNearest && chosen.Count == 2 && chosen.Any(m =>
            {
                var projected = points.Select(p => Project(Point(p), m.Task)).ToArray();
                double cross = P95(projected.Select(p => Math.Abs(p.Cross)));
                double[] range = m.Anchor.Samples.Select(p => Project(Point(p), m.Task).Along).ToArray();
                bool monotonic = !projected.Zip(projected.Skip(1)).Any(p =>
                    (p.Second.Along - p.First.Along) * (projected[^1].Along >= projected[0].Along ? 1 : -1) < -1);
                bool inRange = Math.Min(projected.Max(p => p.Along), range.Max()) - Math.Max(projected.Min(p => p.Along), range.Min()) <= limit;
                double along = projected[m.Side == 0 ? 0 : ^1].Along;
                double other = projected[m.Side == 0 ? ^1 : 0].Along;
                bool facing = !(along >= range.Max() - limit && other < along - 1 || along <= range.Min() + limit && other > along + 1);
                return cross > limit || projected.Any(p => Math.Abs(p.Cross) > 2 * limit) || !monotonic || !inRange || !facing;
            });
            if (forcedDoubleNearest) reasons.Add($"双端独立最近邻为同一母线的不同航段：{chosen[0].Anchor.FlightLegId}（{chosen[0].Distance:0.##}m）、{chosen[1].Anchor.FlightLegId}（{chosen[1].Distance:0.##}m）");
            if (geometryReview) reasons.Add("双端最近邻身份一致，但整段方向、走廊或沿线几何未全部通过，列为待合并候选供人工核对");
            string status = blocked ? "CONFLICT" : competing || chosen.Count == 0 || geometryReview ? "REVIEW" : included.Contains(leg.FlightLegId) ? "HIGH" : "REVIEW";
            if (!included.Contains(leg.FlightLegId)) reasons.Add("未纳入段仅建议身份，不自动补选");
            string reason = $"端点匹配：最近相容端点{best.Distance:0.##}m，走廊±{limit:0.##}m，轴差{displayedAngle:0.##}°；" +
                (reasons.Count == 0 ? "整段同轴且目标明确，自动补充身份，保留纳入与真实断口" : string.Join("；", reasons));
            evidence.Add(new(leg.FlightLegId, status, reason, candidateIds, best.Cross, displayedAngle, included.Contains(leg.FlightLegId)));
            if (chosen.Count == 0 || competing) continue;
            string target = merged?.TargetId ?? best.Task.SurveyTaskStableId;
            var members = (merged is not null ? legs.Where(l => merged.LegIds.Contains(l.FlightLegId)) : Enumerable.Empty<FlightLeg>()).Append(leg).ToArray();
            string key = "endpoint:" + TrackSegmentationReferenceService.Hash(string.Join("|", members.Select(l => l.FlightLegId).Order()) + "/" + target);
            proposals.Add(new(key, target, tasks[target].LineName, tasks[target].Order, members.Select(l => l.FlightLegId).ToArray(), [leg.FlightLegId],
                merged?.OriginalNames ?? ["未归属"], status, reason, best.Cross, displayedAngle, 0)
                { AllowShortInclusion = false, AnchorLegIds = chosen.Select(m => m.Anchor.FlightLegId).Distinct().ToArray(),
                    OriginalTaskByLeg = members.ToDictionary(l => l.FlightLegId, l => l.SurveyTaskStableId) });
        }
        // Combine overlapping commands before applying so multiple shorts cannot
        // overwrite shared endpoints in an order-dependent batch.
        var output = new List<MotherLineRestoreGroup>(); var pending = proposals.OrderBy(g => g.Id, StringComparer.Ordinal).ToList();
        while (pending.Count > 0)
        {
            var group = pending[0]; pending.RemoveAt(0); var component = new List<MotherLineRestoreGroup> { group };
            var members = group.LegIds.ToHashSet(); bool changed;
            do { changed = false; foreach (var other in pending.ToArray()) if (other.LegIds.Any(members.Contains))
                { pending.Remove(other); component.Add(other); members.UnionWith(other.LegIds); changed = true; } } while (changed);
            if (component.Count == 1) { output.Add(group); continue; }
            var targets = component.Select(g => g.TargetId).Distinct().ToArray();
            bool conflict = targets.Length != 1 || component.Any(g => g.Status == "CONFLICT");
            var target = tasks[group.TargetId];
            if (members.SelectMany(id => legs.Single(l => l.FlightLegId == id).Samples).Any(p => Math.Abs(Project(Point(p), target).Cross) > 2 * limit)) conflict = true;
            output.Add(group with { Id = "endpoint:" + TrackSegmentationReferenceService.Hash(string.Join("|", members.Order()) + "/" + group.TargetId),
                LegIds = members.Order().ToArray(), ShortLegIds = component.SelectMany(g => g.ShortLegIds).Distinct().ToArray(),
                AnchorLegIds = component.SelectMany(g => g.AnchorLegIds).Distinct().ToArray(),
                OriginalTaskByLeg = component.SelectMany(g => g.OriginalTaskByLeg).DistinctBy(p => p.Key).ToDictionary(p => p.Key,p => p.Value),
                Status = conflict ? "CONFLICT" : component.All(g => g.Status == "HIGH") ? "HIGH" : "REVIEW",
                Reason = conflict ? "端点组合存在竞争目标或整组偏离，保持原决定" : string.Join("；", component.Select(g => g.Reason).Distinct()) });
        }
        // Evidence must describe the final atomic command, including shared-member conflicts.
        var finalByLeg = output.SelectMany(g => g.ShortLegIds.Select(id => (id, group: g))).ToDictionary(p => p.id, p => p.group);
        return (output, evidence.Select(e => finalByLeg.TryGetValue(e.LegId, out var g)
            ? e with { Status = g.Status, Reason = g.Reason } : e).ToArray());
    }
}
