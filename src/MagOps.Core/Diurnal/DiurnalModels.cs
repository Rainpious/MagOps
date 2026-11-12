namespace MagOps.Core.Diurnal;

public enum DiurnalCorrectionMode
{
    Exact,
    Interpolated,
    Uncorrected
}

public sealed record DiurnalRoverSample(
    DateTimeOffset TimestampUtc,
    double RawMagNt,
    string SourceFile,
    int SourceRow);

public sealed record DiurnalBaseSample(
    DateTimeOffset TimestampUtc,
    double BaseMagNt,
    string SourceFile,
    int SourceRow);

/// <summary>
/// DatumNt remains available for GEMLink/reference-total compatibility, but the
/// primary MagOps production semantic is DiurnalDifferenceNt = Rover - BaseUsed.
/// Datum=0 is the default and matches the historical rigai workflow.
/// </summary>
public sealed record DiurnalCorrectionOptions(
    double DatumNt = 0d,
    bool GemLinkTenthsCompatibility = true,
    double? MaxInterpolationGapSeconds = 999d,
    bool RejectInterpolationAcrossLargeGap = false);

public sealed record DiurnalCorrectedSample(
    DateTimeOffset TimestampUtc,
    double RawMagNt,
    double? BaseUsedNt,
    double DatumNt,
    double? CorrectedMagNt,
    DiurnalCorrectionMode Mode,
    DateTimeOffset? BasePreviousUtc,
    DateTimeOffset? BaseNextUtc,
    double? BaseGapSeconds,
    IReadOnlyList<string> QualityFlags,
    string RoverSourceFile,
    int RoverSourceRow,
    string? BasePreviousSourceFile,
    int? BasePreviousSourceRow,
    string? BaseNextSourceFile,
    int? BaseNextSourceRow)
{
    /// <summary>
    /// Primary production output, equivalent to the historical Oasis/GEMLink
    /// rigai channel: Rover - BaseUsed. It is independent of Datum.
    /// </summary>
    public double? DiurnalDifferenceNt =>
        BaseUsedNt.HasValue
            ? RawMagNt - BaseUsedNt.Value
            : null;

    /// <summary>
    /// Optional reference-total style value: rigai + Datum. CorrectedMagNt is
    /// retained internally for source/API compatibility, but exports use the
    /// explicit DatumAdjustedNt name to avoid confusing Datum with T0.
    /// </summary>
    public double? DatumAdjustedNt => CorrectedMagNt;
}

public sealed record DiurnalDaySummary(
    DateTime UtcDate,
    int RoverCount,
    int BaseCount,
    int ExactCount,
    int InterpolatedCount,
    int UncorrectedCount,
    int DuplicateBaseTimeKeyCount,
    bool HasTimeOverlap,
    string Status,
    IReadOnlyList<string> QualityFlags);

public sealed record DiurnalCorrectionResult(
    IReadOnlyList<DiurnalCorrectedSample> Samples,
    IReadOnlyList<DiurnalDaySummary> Days,
    DiurnalCorrectionOptions Options)
{
    public int ExactCount =>
        Samples.Count(x => x.Mode == DiurnalCorrectionMode.Exact);

    public int InterpolatedCount =>
        Samples.Count(x => x.Mode == DiurnalCorrectionMode.Interpolated);

    public int UncorrectedCount =>
        Samples.Count(x => x.Mode == DiurnalCorrectionMode.Uncorrected);

    public int CorrectedCount =>
        ExactCount + InterpolatedCount;
}
