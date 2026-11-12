namespace MagOps.Core.Domain;

public sealed record CoverageInterval(double Start, double End);

public sealed record PhysicalFlight(
    string PhysicalFlightId,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    string BoundaryStatus,
    IReadOnlyList<string> SourceFiles);

public sealed record MissionExecution(
    string MissionExecutionId,
    string PlannedSortieName,
    string PhysicalFlightId,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    string BoundaryStatus)
{
    public IReadOnlyList<string> SurveyTaskStableIds { get; init; } = [];
}

public sealed record FlightLeg(
    string FlightLegId,
    string MissionExecutionId,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    string Classification,
    string? SurveyTaskStableId,
    int PointCount,
    string ReviewReason)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<FlightLegSample> Samples { get; init; } = [];
    /// <summary>Competing proposals retained for review; these are not assigned identities.</summary>
    public IReadOnlyList<string> CandidateTaskStableIds { get; init; } = [];
    /// <summary>Second-pass review evidence; never grants assignment or production inclusion.</summary>
    public IReadOnlyList<LocalHitCandidateEvidence> LocalHitCandidates { get; init; } = [];
    public IReadOnlyList<GeometryRecoveryEvidence> GeometryCandidates { get; init; } = [];
    public IReadOnlyList<string> TransitEvidence { get; init; } = [];
    /// <summary>Non-blocking context: range conflicts and weak takeoff hints do not change identity.</summary>
    public IReadOnlyList<string> ContextEvidence { get; init; } = [];
    /// <summary>Observable motion, independent of task identity and route purpose.</summary>
    public string MotionKind { get; init; } = "";
    public IReadOnlyList<string> MotionEvidence { get; init; } = [];
    /// <summary>Whole-route purpose; independent of local spatial line assignment.</summary>
    public string RouteRole { get; init; } = "UNDETERMINED";
    public string ParentRouteId { get; init; } = "";
    public IReadOnlyList<string> RouteRoleEvidence { get; init; } = [];
}

public sealed record FlightLegSample(string SourceFile, int SourceRecord,
    DateTimeOffset TimestampUtc, double Longitude, double Latitude, double? AltitudeM = null);

public sealed record LocalHitCandidateEvidence(string TaskId, string GeometryOrigin,
    IReadOnlyList<string> SeedFlightLegIds, bool HasReverseSeed, double SupportedRatio,
    double ContinuousSpanM, double ContinuousSeconds, double MedianCrossTrackM,
    double AxisErrorDeg, bool ExtendsActualFit);

public sealed record GeometryRecoveryEvidence(string TaskId, double Score, double MedianDistanceM,
    double P95DistanceM, double FinitePointRatio, double AxisErrorDeg, double Monotonicity,
    double SpanM, int SampleCount, int InputCount);

public sealed record ExecutionBoundaryEvidence(string Kind, DateTimeOffset CutUtc,
    DateTimeOffset ConfirmedUtc, string SourceFile, int SourceRecord);

public sealed record FlightQualityEvidence(double CorePointRatio, double CorridorPointRatio,
    double SpeedOutsideRatio, int OutsideCorridorPoints, bool HasAltitude,
    IReadOnlyList<string> Warnings)
{
    /// <summary>Median signed cross-track offset relative to the directed design line.</summary>
    public double MedianSignedCrossTrackM { get; init; }

    /// <summary>Share of usable points on the dominant side of the design line.</summary>
    public double LateralSignConsistency { get; init; }
}

public sealed record SurveyLinePass(
    string PassId,
    string FlightLegId,
    string MissionExecutionId,
    string SurveyTaskStableId,
    IReadOnlyList<CoverageInterval> Coverage,
    double MedianCrossTrackM,
    double P95CrossTrackM,
    double AlongTrackMonotonicity,
    double PlannedCoverage,
    double StartEndpointDistanceM,
    double EndEndpointDistanceM,
    double HeadingErrorDeg,
    double DurationSec,
    double MatchConfidence,
    string QualityStatus,
    bool IsQualityRepeat,
    DateTimeOffset EndUtc,
    IReadOnlyList<string> SourceFiles)
{
    public IReadOnlyList<ExecutionSampleReference> Samples { get; init; } = [];
    /// <summary>Whether a person confirmed this pass-to-task assignment.</summary>
    public bool ReviewConfirmed { get; init; }
    public string MatchMethod { get; init; } = "";
    public string ExecutionDirection { get; init; } = "";
    public ExecutionBoundaryEvidence? StartBoundary { get; init; }
    public ExecutionBoundaryEvidence? EndBoundary { get; init; }
    public FlightQualityEvidence? FlightQuality { get; init; }
}

public sealed record ExecutionSampleReference(string SourceFile, int SourceRecord, double AlongFraction);

public sealed record SurveyExecutionResult(
    string AlgorithmVersion,
    IReadOnlyList<PhysicalFlight> PhysicalFlights,
    IReadOnlyList<MissionExecution> Missions,
    IReadOnlyList<FlightLeg> Legs,
    IReadOnlyList<SurveyLinePass> Passes,
    IReadOnlyList<string> Warnings);

public sealed record SurveyCompletionResult(
    string SurveyTaskStableId,
    double PlannedLengthM,
    IReadOnlyList<CoverageInterval> CoveredIntervals,
    double CoverageRatio,
    double LargestUncoveredGapM,
    string Status);

public static class SurveyCoverage
{
    public static IReadOnlyList<CoverageInterval> Union(IEnumerable<CoverageInterval> intervals)
    {
        var ordered = intervals.Where(x => double.IsFinite(x.Start) && double.IsFinite(x.End))
            .Select(x => new CoverageInterval(
                Math.Clamp(x.Start, 0d, 1d), Math.Clamp(x.End, 0d, 1d)))
            .Where(x => x.End > x.Start)
            .OrderBy(x => x.Start).ThenBy(x => x.End).ToList();
        var merged = new List<CoverageInterval>();
        foreach (CoverageInterval interval in ordered)
        {
            if (merged.Count > 0 && interval.Start <= merged[^1].End + 1e-9)
                merged[^1] = merged[^1] with { End = Math.Max(merged[^1].End, interval.End) };
            else
                merged.Add(interval);
        }
        return merged;
    }

    public static SurveyCompletionResult Calculate(
        string taskId, double plannedLengthM, IEnumerable<CoverageInterval> intervals)
    {
        if (!double.IsFinite(plannedLengthM) || plannedLengthM <= 0)
            throw new ArgumentOutOfRangeException(nameof(plannedLengthM));
        var union = Union(intervals);
        double ratio = union.Sum(x => x.End - x.Start);
        double cursor = 0;
        double gap = 0;
        foreach (CoverageInterval interval in union)
        {
            gap = Math.Max(gap, (interval.Start - cursor) * plannedLengthM);
            cursor = interval.End;
        }
        gap = Math.Max(gap, (1d - cursor) * plannedLengthM);
        string status = ratio >= 0.90d
            ? gap <= Math.Min(150d, plannedLengthM * 0.05d) ? "COMPLETE" : "COMPLETE_GAP_WARNING"
            : ratio > 0d ? "PARTIAL" : "UNMATCHED";
        return new SurveyCompletionResult(taskId, plannedLengthM, union, ratio, gap, status);
    }
}
