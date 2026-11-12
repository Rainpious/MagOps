using MagOps.Core.Domain;

namespace MagOps.Infrastructure.Services;

public sealed record ActualLinePair(string FirstId, string SecondId, string FirstName, string SecondName,
    double AxisDifferenceDegrees, double CrossDistanceM, double AlongGapM, double OverlapRatio, string Relation);

/// <summary>Read-only spatial proposals. No clustering, identity changes, point deletion or time stitching.</summary>
public static class ActualLineRegroupingService
{
    private sealed record Shape(PlannedSurveyLineTask Task, GeoCoordinate[] Points, double Bearing);

    public static IReadOnlyList<ActualLinePair> Suggest(IReadOnlyList<PlannedSurveyLineTask> tasks,
        SurveyExecutionResult execution, double crossThresholdM, double axisToleranceDegrees, double maxAlongGapM,
        double? spacingM = null)
    {
        if (!double.IsFinite(crossThresholdM) || crossThresholdM <= 0 ||
            !double.IsFinite(axisToleranceDegrees) || axisToleranceDegrees <= 0 || axisToleranceDegrees > 15 ||
            !double.IsFinite(maxAlongGapM) || maxAlongGapM < 0 ||
            spacingM is { } s && (!double.IsFinite(s) || s <= 0))
            throw new ArgumentOutOfRangeException(nameof(crossThresholdM), "归并建议参数无效。");
        // At most a fifth of independently estimated/entered spacing, never half-spacing proximity.
        double threshold = spacingM is > 0 ? Math.Min(crossThresholdM, spacingM.Value * .2) : crossThresholdM;
        var pointsByTask = execution.Legs.Where(l => l.SurveyTaskStableId is not null &&
                l.RouteRole is not ("TRANSIT" or "CONNECTOR") && l.MotionKind.Length == 0)
            .GroupBy(l => l.SurveyTaskStableId!).ToDictionary(g => g.Key,
                g => g.SelectMany(l => l.Samples).Select(p => new GeoCoordinate(p.Longitude, p.Latitude)).ToArray());
        var shapes = tasks.Where(t => t.GeometryOrigin == "ACTUAL_TRACK" && t.LengthM >= 60 && pointsByTask.ContainsKey(t.SurveyTaskStableId))
            .Select(t => new Shape(t, pointsByTask[t.SurveyTaskStableId], SurveyDirection.Bearing(t.Start, t.End) ?? 0))
            .Where(s => s.Points.Length >= 3).ToArray();
        var result = new List<ActualLinePair>();
        for (int i = 0; i < shapes.Length; i++)
        for (int j = i + 1; j < shapes.Length; j++)
        {
            var a = shapes[i]; var b = shapes[j];
            double angle = TrackSegmentationReferenceService.AxisDifference(a.Bearing, b.Bearing);
            if (angle > axisToleranceDegrees) continue;
            GeoCoordinate Mid(PlannedSurveyLineTask t) => new((t.Start.Longitude + t.End.Longitude) / 2, (t.Start.Latitude + t.End.Latitude) / 2);
            if (Math.Abs(Project(Mid(a.Task), b.Task).Cross) > threshold * 2 + 5 ||
                Math.Abs(Project(Mid(b.Task), a.Task).Cross) > threshold * 2 + 5) continue;
            // Test both point sets against the other axis; crossing lines and diverging fragments fail.
            double cross = Math.Max(P95Cross(a.Points, b.Task), P95Cross(b.Points, a.Task));
            if (cross > threshold) continue;
            var ar = Range(a.Points, a.Task); var br = Range(b.Points, a.Task);
            double overlap = Math.Max(0, Math.Min(ar.Max, br.Max) - Math.Max(ar.Min, br.Min));
            double gap = Math.Max(0, Math.Max(ar.Min, br.Min) - Math.Min(ar.Max, br.Max));
            if (gap > maxAlongGapM) continue;
            double ratio = overlap / Math.Max(1, Math.Min(ar.Max - ar.Min, br.Max - br.Min));
            result.Add(new(a.Task.SurveyTaskStableId, b.Task.SurveyTaskStableId, a.Task.LineName, b.Task.LineName,
                angle, cross, gap, ratio, ratio > .5 ? "明显重叠：可能重复飞行，归并后保留各趟次" : "同轴延伸或局部重叠：可核对是否同一母线"));
        }
        return result.OrderBy(r => r.CrossDistanceM).ThenBy(r => r.AlongGapM).ToArray();
    }

    private static (double Along, double Cross) Project(GeoCoordinate point, PlannedSurveyLineTask task)
    {
        var (x, y) = EndpointLineRecognizer.Delta(task.Start, point);
        var (dx, dy) = EndpointLineRecognizer.Delta(task.Start, task.End);
        double length = Math.Sqrt(dx * dx + dy * dy);
        return length > 0 ? ((x * dx + y * dy) / length, (x * dy - y * dx) / length) : (0, double.PositiveInfinity);
    }
    private static double P95Cross(GeoCoordinate[] points, PlannedSurveyLineTask task)
    {
        var distances = points.Select(p => Math.Abs(Project(p, task).Cross)).Order().ToArray();
        return distances[(int)Math.Ceiling(distances.Length * .95) - 1];
    }
    private static (double Min, double Max) Range(GeoCoordinate[] points, PlannedSurveyLineTask task)
    {
        var along = points.Select(p => Project(p, task).Along).ToArray();
        return (along.Min(), along.Max());
    }

    public static PlannedSurveyLineTask[] ExtendTargets(IReadOnlyList<PlannedSurveyLineTask> tasks,
        SurveyExecutionResult execution, IReadOnlyList<ManualFlightLegAssignment> assignments)
    {
        var legs = execution.Legs.ToDictionary(l => l.FlightLegId);
        var unique = assignments.GroupBy(a => a.FlightLegId).Where(g => g.Count() == 1).Select(g => g.Single()).ToArray();
        return tasks.Select(task =>
        {
            if (task.GeometryOrigin != "ACTUAL_TRACK") return task;
            var sources = unique.Where(a => a.SurveyTaskStableId == task.SurveyTaskStableId && legs.ContainsKey(a.FlightLegId)).ToArray();
            if (sources.Length == 0) return task;
            var points = sources.SelectMany(a => legs[a.FlightLegId].Samples).Select(p => new GeoCoordinate(p.Longitude, p.Latitude)).ToArray();
            if (points.Length == 0) return task;
            var (dx, dy) = EndpointLineRecognizer.Delta(task.Start, task.End);
            double length = Math.Sqrt(dx * dx + dy * dy);
            if (length < 1) return task;
            var range = Range(points, task);
            double min = Math.Min(0, range.Min), max = Math.Max(length, range.Max);
            if (min == 0 && max == length) return task;
            GeoCoordinate At(double distance) => new(task.Start.Longitude + (task.End.Longitude - task.Start.Longitude) * distance / length,
                task.Start.Latitude + (task.End.Latitude - task.Start.Latitude) * distance / length);
            return task with { Start = At(min), End = At(max), LengthM = max - min };
        }).ToArray();
    }

    public static SurveyExecutionResult Reproject(SurveyExecutionResult execution, IReadOnlyList<PlannedSurveyLineTask> tasks,
        IReadOnlyList<ManualFlightLegAssignment> assignments)
    {
        var targets = assignments.Select(a => a.SurveyTaskStableId).ToHashSet();
        var byId = tasks.Where(t => targets.Contains(t.SurveyTaskStableId) && t.GeometryOrigin == "ACTUAL_TRACK").ToDictionary(t => t.SurveyTaskStableId);
        var legs = execution.Legs.ToDictionary(l => l.FlightLegId);
        return execution with { Passes = execution.Passes.Select(p =>
        {
            if (!byId.TryGetValue(p.SurveyTaskStableId, out var task)) return p;
            var updated = WholeLegMatcherService.CreateManualAssignmentPass(legs[p.FlightLegId], task);
            return updated is null ? p : updated with { MatchMethod = p.MatchMethod, MatchConfidence = p.MatchConfidence,
                ExecutionDirection = p.ExecutionDirection, StartBoundary = p.StartBoundary, EndBoundary = p.EndBoundary };
        }).ToArray() };
    }
}
