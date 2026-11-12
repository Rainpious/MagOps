namespace MagOps.Core.Domain;

/// <summary>Point-level production exclusion evidence. Identity and source rows remain unchanged.</summary>
public sealed record ProductionAnomalyInterval(
    string AnomalyId,
    string FlightLegId,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    string Kind,
    string Reason,
    string SourceFile,
    int StartSourceRecord,
    int EndSourceRecord,
    int PointCount,
    double HorizontalRadiusM,
    double? AltitudeRangeM,
    IReadOnlyList<ExecutionSampleReference> Samples)
{
    public DateTimeOffset ConfirmedUtc { get; init; }
    public string EvidenceStatus { get; init; } = "REVIEW_REQUIRED";
    public string ProductionDecision { get; init; } = "KEEP_PENDING_REVIEW";
    public int ExcludedSelectedPoints { get; init; }
    public bool Contains(string sourceFile, int sourceRecord) =>
        Samples.Any(x => x.SourceFile.Equals(sourceFile, StringComparison.OrdinalIgnoreCase) &&
                         x.SourceRecord == sourceRecord);
}

public sealed record ProductionAnomalyDecision(string AnomalyId, string Decision, string Reason);
