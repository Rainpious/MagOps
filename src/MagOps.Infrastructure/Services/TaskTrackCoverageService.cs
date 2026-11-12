using MagOps.Core.Domain;

namespace MagOps.Infrastructure.Services;

public sealed record TaskTrackCoverage(string TaskId, string LineName, string GeometryOrigin, double LengthM,
    int PassCount, IReadOnlyList<CoverageInterval> Covered, IReadOnlyList<CoverageInterval> Missing,
    double Ratio, double LargestGapM, string EvidenceScope);

public static class TaskTrackCoverageService
{
    public static IReadOnlyList<TaskTrackCoverage> Summarize(IReadOnlyList<PlannedSurveyLineTask> tasks,
        IReadOnlyList<SurveyLinePass> passes, string scope = "RECOGNIZED") => tasks.Select(task =>
    {
        var selected = passes.Where(p => p.SurveyTaskStableId == task.SurveyTaskStableId).ToArray();
        var covered = SurveyCoverage.Union(selected.SelectMany(p => p.Coverage));
        var missing = new List<CoverageInterval>(); double cursor = 0;
        foreach (var interval in covered)
        {
            if (interval.Start > cursor) missing.Add(new(cursor, interval.Start));
            cursor = interval.End;
        }
        if (cursor < 1) missing.Add(new(cursor, 1));
        return new TaskTrackCoverage(task.SurveyTaskStableId, task.LineName, task.GeometryOrigin, task.LengthM,
            selected.Length, covered, missing, covered.Sum(i => i.End - i.Start),
            missing.Select(i => (i.End - i.Start) * task.LengthM).DefaultIfEmpty(0).Max(), scope);
    }).ToArray();
}
