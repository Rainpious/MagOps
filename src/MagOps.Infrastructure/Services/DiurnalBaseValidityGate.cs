using System.Globalization;

namespace MagOps.Infrastructure.Services;

/// <summary>
/// Production safety gate applied to GSM-19T canonical samples before they are
/// handed to the native diurnal interpolator. Canonical/raw records are never
/// deleted; blocked samples remain in the integrated audit dataset with QC flags.
/// </summary>
public static class DiurnalBaseValidityGate
{
    public const double MinPhysicalFieldNt = 10000d;
    public const double MaxPhysicalFieldNt = 100000d;
    public const double MinSq = 80d;
    public const double MaxSq = 100d;
    public const double DayOutlierFloorNt = 500d;
    public const double DayOutlierMadMultiplier = 20d;
    public const double SessionMedianDeviationNt = 500d;
    public const double SessionInvalidFraction = 0.50d;

    private static readonly HashSet<string> BlockingFlags =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "BASE_VALUE_OUT_OF_RANGE",
            "SQ_OUT_OF_RANGE",
            "LOW_SQ",
            "BASE_DAY_OUTLIER",
            "BASE_SESSION_INVALID",
            "TIMESTAMP_CONFLICT"
        };

    public static bool IsEligible(Gsm19TCanonicalSample sample) =>
        double.IsFinite(sample.MagneticFieldNt) &&
        !sample.QualityFlags.Any(BlockingFlags.Contains);

    public static IReadOnlyList<Gsm19TCanonicalSample> Accepted(
        IEnumerable<Gsm19TCanonicalSample> samples) =>
        samples
            .Where(IsEligible)
            .OrderBy(x => x.TimestampUtc)
            .ThenBy(x => x.SourceFile, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.SourceRecord)
            .ToList();

    public static DiurnalBaseValiditySummary Evaluate(
        IEnumerable<Gsm19TCanonicalSample> samples)
    {
        List<Gsm19TCanonicalSample> all = samples.ToList();
        List<Gsm19TCanonicalSample> accepted = all.Where(IsEligible).ToList();
        List<Gsm19TCanonicalSample> rejected = all.Where(x => !IsEligible(x)).ToList();

        Dictionary<string, int> rejectionCounts =
            BlockingFlags
                .Select(flag => new
                {
                    Flag = flag,
                    Count = rejected.Count(x =>
                        x.QualityFlags.Contains(
                            flag,
                            StringComparer.OrdinalIgnoreCase))
                })
                .Where(x => x.Count > 0)
                .OrderByDescending(x => x.Count)
                .ThenBy(x => x.Flag, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    x => x.Flag,
                    x => x.Count,
                    StringComparer.OrdinalIgnoreCase);

        int rejectedSessions =
            all.Where(x =>
                    x.QualityFlags.Contains(
                        "BASE_SESSION_INVALID",
                        StringComparer.OrdinalIgnoreCase))
                .Select(x =>
                    string.Join(
                        "\u001f",
                        x.SourceFile,
                        x.TimestampUtc.UtcDateTime.Date.ToString(
                            "yyyy-MM-dd",
                            CultureInfo.InvariantCulture)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();

        double? acceptedMedianField =
            accepted.Count == 0
                ? null
                : Median(
                    accepted
                        .Select(x => x.MagneticFieldNt)
                        .OrderBy(x => x)
                        .ToList());

        return new DiurnalBaseValiditySummary(
            all.Count,
            accepted.Count,
            rejected.Count,
            rejectedSessions,
            acceptedMedianField,
            rejectionCounts)
        {
            RejectedSessionIdentityCount = rejected.Where(x => x.QualityFlags.Contains("BASE_SESSION_INVALID"))
                .Select(x => !string.IsNullOrWhiteSpace(x.SessionStableId) ? x.SessionStableId :
                    $"legacy:{x.SourceFile}:{x.TimestampUtc.UtcDateTime:yyyy-MM-dd}")
                .Distinct(StringComparer.OrdinalIgnoreCase).Count()
        };
    }

    private static double Median(IReadOnlyList<double> sorted)
    {
        if (sorted.Count == 0)
            return 0d;

        int middle = sorted.Count / 2;
        return sorted.Count % 2 == 0
            ? (sorted[middle - 1] + sorted[middle]) / 2d
            : sorted[middle];
    }
}

public sealed record DiurnalBaseValiditySummary(
    int TotalRecords,
    int AcceptedRecords,
    int RejectedRecords,
    int RejectedSessions,
    double? AcceptedMedianFieldNt,
    IReadOnlyDictionary<string, int> RejectionCounts)
{
    /// <summary>Actual session identities, distinct from the legacy file/day RejectedSessions reporting contract.</summary>
    public int RejectedSessionIdentityCount { get; init; }
}
