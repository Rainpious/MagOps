namespace MagOps.Infrastructure.Services;

public sealed record PlannedSurveyLineTask(
    int Order,
    int SortieOrder,
    string TaskId,
    string SortieName,
    string LineName,
    GeoCoordinate Start,
    GeoCoordinate End,
    double LengthM)
{
    /// <summary>Concise partition identity such as P01; blank for unsplit workareas.</summary>
    public string PartitionCode { get; init; } = "";

    /// <summary>Stable logical mother-line identity shared by clipped segments.</summary>
    public string MotherLineTaskId { get; init; } = "";

    /// <summary>Physical clipped segment identity; equals TaskId for current progress accounting.</summary>
    public string SegmentId { get; init; } = "";
    /// <summary>Display execution segment label, e.g. L63.S2.</summary>
    public string ExecutionSegmentId { get; init; } = "";
    /// <summary>Persistent opaque execution segment machine identity.</summary>
    public string ExecutionSegmentStableId { get; init; } = "";
    public string SurveyTaskStableId { get; init; } = "";
    public string SurveyLineStableId { get; init; } = "";
    public string MotherLineStableId { get; init; } = "";
    public bool IsQualityRepeat { get; init; }
    /// <summary>Empty for formal design, IMPORTED_REFERENCE or ACTUAL_TRACK for auxiliary geometry.</summary>
    public string GeometryOrigin { get; init; } = "";
}

public sealed record SurveyTrackPoint(
    DateTimeOffset TimestampUtc,
    GeoCoordinate Coordinate,
    string SourceFile,
    int SourceRecord)
{
    public double? AltitudeM { get; init; }
    /// <summary>Auxiliary evidence only; never a geometry or identity gate.</summary>
    public double? RawMagNt { get; init; }
}

public sealed record FieldSurveyLineProgressEstimate(
    int Order,
    string TaskId,
    string SortieName,
    string LineName,
    string Status,
    double PlannedLengthM,
    double MatchedLengthM,
    double CoverageRatio,
    double CrossTrackP95M,
    double MatchToleranceM,
    double MaxGapM,
    int MatchedPointCount,
    bool CompletedBefore,
    bool CompletedToday,
    string Evidence);

/// <summary>Progress consumes the same audit execution authority; no point-nearest-line matcher.</summary>
public sealed class SurveyLineProgressService
{
    public const string AlgorithmVersion = WholeLegMatcherService.AlgorithmVersion;
    public const double CompletionCoverageThreshold = 0.90d;
    public const double PartialCoverageThreshold = 0.20d;
    public IReadOnlyList<FieldSurveyLineProgressEstimate> Analyze(
        IReadOnlyList<PlannedSurveyLineTask> tasks, IEnumerable<SurveyTrackPoint> trackSource,
        ISet<string> completedBefore, IReadOnlyList<MissionTimeWindow>? missionWindows = null)
    {
        var execution = new WholeLegMatcherService().Match(tasks, trackSource, missionWindows ?? []);
        return tasks.Select(task =>
        {
            string id = string.IsNullOrEmpty(task.SurveyTaskStableId) ? task.TaskId : task.SurveyTaskStableId;
            var passes = execution.Passes.Where(p => p.SurveyTaskStableId == id).ToArray();
            var completion = MagOps.Core.Domain.SurveyCoverage.Calculate(id, task.LengthM,
                passes.SelectMany(p => p.Coverage));
            bool before = completedBefore.Contains(task.TaskId);
            return new FieldSurveyLineProgressEstimate(task.Order, task.TaskId, task.SortieName, task.LineName,
                missionWindows is null || missionWindows.Count == 0 ? "REVIEW_REQUIRED" : completion.Status,
                task.LengthM, task.LengthM * completion.CoverageRatio, completion.CoverageRatio,
                passes.Length == 0 ? 0 : passes.Max(p => p.P95CrossTrackM), 0,
                completion.LargestUncoveredGapM, passes.Sum(p => p.Samples.Count), before,
                !before && completion.Status == "COMPLETE", "AUDIT_ONLY; QC/生产准入另行确认");
        }).ToArray();
    }

}
