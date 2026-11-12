using MagOps.Core.Domain;

namespace MagOps.Infrastructure.Services;

public static partial class MotherLineGroupingService
{
    private sealed record SpatialAxis(PlannedSurveyLineTask Task, FlightLeg[] Anchors,
        GeoCoordinate Origin, double Dx, double Dy, double Slope, double Offset)
    {
        public (double Along, double Cross) At(FlightLegSample sample)
        {
            var (x, y) = EndpointLineRecognizer.Delta(Origin, Point(sample));
            double along = x * Dx + y * Dy;
            return (along, (x * -Dy + y * Dx - Slope * along - Offset) / Math.Sqrt(1 + Slope * Slope));
        }
    }

    // Robust observed axis: short disturbed legs never establish an anchor.
    private static SpatialAxis FitAxis(PlannedSurveyLineTask task, FlightLeg[] anchors, double bearing)
    {
        double radians = bearing * Math.PI / 180, dx = Math.Sin(radians), dy = Math.Cos(radians);
        var origin = task.Start;
        var points = anchors.SelectMany(l => l.Samples).Select(p =>
        {
            var (x, y) = EndpointLineRecognizer.Delta(origin, Point(p));
            return (Along: x * dx + y * dy, Cross: x * -dy + y * dx);
        }).ToArray();
        var retained = points; double slope = 0, offset = 0;
        for (int iteration = 0; iteration < 3; iteration++)
        {
            double mx = retained.Average(p => p.Along), my = retained.Average(p => p.Cross);
            double variance = retained.Sum(p => (p.Along - mx) * (p.Along - mx));
            slope = variance > 1 ? retained.Sum(p => (p.Along - mx) * (p.Cross - my)) / variance : 0;
            offset = my - slope * mx;
            retained = points.OrderBy(p => Math.Abs(p.Cross - slope * p.Along - offset))
                .Take(Math.Max(3, (int)Math.Ceiling(points.Length * .9))).ToArray();
        }
        return new(task, anchors, origin, dx, dy, slope, offset);
    }

    private static IReadOnlyList<MotherLineRestoreGroup> SpatialCandidates(AuditProductionResult audit,
        MotherLineRestoreOptions options, IReadOnlySet<string> protection, IReadOnlySet<string> included,
        IReadOnlySet<string> automatic, IReadOnlySet<string> protectedAnchors, IReadOnlySet<string> reserved)
    {
        var tasks = (audit.EffectiveTasks ?? []).Where(t => t.GeometryOrigin == "ACTUAL_TRACK").ToArray();
        var anomalies = audit.ProductionAnomalies.Where(a => a.ProductionDecision != "USER_RESTORE").Select(a => a.FlightLegId).ToHashSet();
        bool GeometrySafe(FlightLeg l) => l.Samples.Count >= 3 && !anomalies.Contains(l.FlightLegId) &&
            l.RouteRole is not ("CONNECTOR" or "TRANSIT") && l.MotionKind.Length == 0 && l.TransitEvidence.Count == 0;
        bool Safe(FlightLeg l) => GeometrySafe(l) && !protection.Contains(l.FlightLegId);
        var anchors = audit.Execution.Legs.Where(l => l.SurveyTaskStableId is not null && GeometrySafe(l) &&
            (!protection.Contains(l.FlightLegId) || protectedAnchors.Contains(l.FlightLegId)) &&
            included.Contains(l.FlightLegId) && !automatic.Contains(l.FlightLegId) && Span(l) >= 60).ToArray();
        double bearing = audit.InferredAxisDegrees ?? (tasks.Length > 0 ? Bearing(tasks.OrderByDescending(t => t.LengthM).First()) : 0);
        var axes = tasks.Select(t => (Task: t, Legs: anchors.Where(l => l.SurveyTaskStableId == t.SurveyTaskStableId).ToArray()))
            .Where(x => x.Legs.Length > 0).Select(x => FitAxis(x.Task, x.Legs, bearing)).ToArray();
        double? spacing = audit.TrackSegmentation?.RecognitionOptions.ExpectedLineSpacingM;
        if (spacing is not > 0) spacing = IntermediateSegmentationEvidence.Estimate(tasks, bearing).SpacingM;
        // A review corridor tolerates local disturbance, but cannot reach a neighbour's midpoint.
        double coreLimit = spacing is > 0 ? Math.Min(options.CrossM, spacing.Value * .2) : options.CrossM;
        double limit = spacing is > 0 ? Math.Min(options.CrossM * 2, spacing.Value * .4) : options.CrossM;
        var groups = new List<MotherLineRestoreGroup>(); var consumed = new HashSet<string>(reserved);
        bool Fits(FlightLeg l, SpatialAxis axis)
        {
            var cross = l.Samples.Select(p => Math.Abs(axis.At(p).Cross)).Order().ToArray();
            return cross[cross.Length / 2] <= coreLimit && cross[^1] <= limit;
        }
        double Gap(FlightLeg l, SpatialAxis axis)
        {
            var p = l.Samples.Select(axis.At).ToArray();
            return axis.Anchors.Min(a =>
            {
                var q = a.Samples.Select(axis.At).ToArray();
                return Math.Max(0, Math.Max(p.Min(x => x.Along), q.Min(x => x.Along)) - Math.Min(p.Max(x => x.Along), q.Max(x => x.Along)));
            });
        }
        // Complete component against one fitted axis; no transitive pairwise drift.
        foreach (var axis in axes.OrderByDescending(a => a.Anchors.Sum(Span)).ThenBy(a => a.Task.Order))
        {
            if (axis.Anchors.Any(l => consumed.Contains(l.FlightLegId))) continue;
            var memberTasks = new HashSet<string> { axis.Task.SurveyTaskStableId };
            bool canMergeAxis = audit.Execution.Legs.Where(l => l.SurveyTaskStableId == axis.Task.SurveyTaskStableId).All(Safe);
            var eligible = axes.Where(a => canMergeAxis && a != axis).Where(other =>
            {
                var all = audit.Execution.Legs.Where(l => l.SurveyTaskStableId == other.Task.SurveyTaskStableId).ToArray();
                return all.Length > 0 && all.All(l => Safe(l) && !consumed.Contains(l.FlightLegId) && Fits(l, axis));
            }).OrderBy(a => a.Task.Order).ToArray();
            double RangeGap(FlightLeg first, FlightLeg second)
            {
                var a = first.Samples.Select(axis.At).Select(p => p.Along).ToArray();
                var b = second.Samples.Select(axis.At).Select(p => p.Along).ToArray();
                return Math.Max(0, Math.Max(a.Min(), b.Min()) - Math.Min(a.Max(), b.Max()));
            }
            bool expanded;
            do
            {
                expanded = false;
                foreach (var other in eligible.Where(a => !memberTasks.Contains(a.Task.SurveyTaskStableId)))
                    if (other.Anchors.Any(l => axes.Where(a => memberTasks.Contains(a.Task.SurveyTaskStableId))
                        .SelectMany(a => a.Anchors).Any(a => RangeGap(l, a) <= options.MaximumAlongGapM)))
                    { memberTasks.Add(other.Task.SurveyTaskStableId); expanded = true; }
            } while (expanded);
            var members = audit.Execution.Legs.Where(l => l.SurveyTaskStableId is { } id && memberTasks.Contains(id)).ToList();
            if (members.Any(l => !GeometrySafe(l) || memberTasks.Count > 1 && protection.Contains(l.FlightLegId) || !Fits(l, axis) || consumed.Contains(l.FlightLegId))) continue;
            var memberAnchors = axes.Where(a => memberTasks.Contains(a.Task.SurveyTaskStableId)).SelectMany(a => a.Anchors).ToArray();
            var memberAxis = axis with { Anchors = memberAnchors };
            var fragments = new List<FlightLeg>();
            foreach (var leg in audit.Execution.Legs.Where(l => l.SurveyTaskStableId is null && Safe(l) && !consumed.Contains(l.FlightLegId)))
            {
                if (!Fits(leg, axis) || Gap(leg, memberAxis) > options.MaximumAlongGapM) continue;
                var projected = leg.Samples.Select(axis.At).ToArray();
                double sign = projected[^1].Along >= projected[0].Along ? 1 : -1;
                if (projected.Zip(projected.Skip(1)).Any(p => (p.Second.Along - p.First.Along) * sign < -1)) continue;
                double nearEnd = memberAnchors.Min(a => new[] { leg.Samples[0], leg.Samples[^1] }.Min(p =>
                    new[] { a.Samples[0], a.Samples[^1] }.Min(q => TrackSegmentationReferenceService.Distance(Point(p), Point(q)))));
                if (nearEnd > options.MaximumAlongGapM) continue;
                double score = P95(projected.Select(p => Math.Abs(p.Cross)));
                // Compare all competing mothers, including ones that fail this corridor.
                if (axes.Where(a => !memberTasks.Contains(a.Task.SurveyTaskStableId)).Any(a =>
                    Gap(leg, a) <= options.MaximumAlongGapM && P95(leg.Samples.Select(p => Math.Abs(a.At(p).Cross))) <= score + coreLimit)) continue;
                fragments.Add(leg);
            }
            if (memberTasks.Count < 2 && fragments.Count == 0) continue;
            var changed = memberTasks.Count > 1 ? members.Concat(fragments).ToArray() : fragments.ToArray();
            var ids = changed.Select(l => l.FlightLegId).Order(StringComparer.Ordinal).ToArray();
            var original = tasks.Where(t => memberTasks.Contains(t.SurveyTaskStableId)).OrderBy(t => t.Order).ToArray();
            bool overlappingIdentity = memberTasks.Count > 1 && axes.Where(a => memberTasks.Contains(a.Task.SurveyTaskStableId))
                .Any(a => a != axis && a.Anchors.Any(other => axis.Anchors.Any(anchor =>
                {
                    var x = anchor.Samples.Select(p => axis.At(p).Along).ToArray(); var y = other.Samples.Select(p => axis.At(p).Along).ToArray();
                    double overlap = Math.Max(0, Math.Min(x.Max(), y.Max()) - Math.Max(x.Min(), y.Min()));
                    return overlap / Math.Max(1, Math.Min(x.Max() - x.Min(), y.Max() - y.Min())) >= .6;
                })));
            string suspectKind = overlappingIdentity ? "整线重叠·疑似重复覆盖" : "";
            groups.Add(new("spatial:" + TrackSegmentationReferenceService.Hash(string.Join("|", ids)), axis.Task.SurveyTaskStableId,
                axis.Task.LineName, axis.Task.Order, ids, fragments.Select(l => l.FlightLegId).ToArray(), original.Select(t => t.LineName).ToArray(),
                "REVIEW", $"完整母线公共轴核对：{memberTasks.Count}个已有身份、{fragments.Count}个未归属航段；主体横偏≤{coreLimit:0.##}m、全点走廊±{limit:0.##}m，比较相邻母线；局部方向扰动不单独否决。仅更改身份，纳入、质量和原观测保持，需批量确认。" + (suspectKind.Length > 0 ? $" 疑似类别：{suspectKind}，默认保留并等待人工决定。" : ""),
                changed.Max(l => P95(l.Samples.Select(p => Math.Abs(axis.At(p).Cross)))), 0, 0)
                { AllowShortInclusion = false, AnchorLegIds = memberAnchors.Select(l => l.FlightLegId).ToArray(),
                    SuspectKind = suspectKind,
                    OriginalTaskByLeg = changed.ToDictionary(l => l.FlightLegId, l => l.SurveyTaskStableId), OriginalTasks = original });
            consumed.UnionWith(ids);
        }
        return groups;
    }
}
