using MagOps.Core.Diurnal;

namespace MagOps.Infrastructure.Services;

public enum FieldMagCoverageSegmentKind
{
    UavCovered,
    UavUncovered,
    BaseValidCoverage,
    BaseRejected
}

public sealed record FieldMagCoverageSegment(
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    FieldMagCoverageSegmentKind Kind,
    string Reason);

public sealed record FieldMagDailyCoverageSummary(
    DateTime BeijingDate,
    int UavEligibleRecords,
    int CoveredUavRecords,
    int UncoveredUavRecords,
    double CoveragePercent,
    int BaseRecords,
    int AcceptedBaseRecords,
    int RejectedBaseRecords,
    DateTimeOffset? UavFirstUtc,
    DateTimeOffset? UavLastUtc,
    DateTimeOffset? FirstUncoveredUtc,
    DateTimeOffset? LastUncoveredUtc,
    double LargestUncoveredSpanSeconds,
    int UncoveredSpanCount,
    string Status,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<FieldMagCoverageSegment> Segments)
{
    public bool HasUav => UavEligibleRecords > 0;
    public bool IsComplete => HasUav && UncoveredUavRecords == 0;
}

/// <summary>
/// Daily production-readiness check between UAV magnetic samples and the
/// GSM-19T Base samples that survive the production Base Validity Gate.
/// The coverage rule intentionally mirrors the native diurnal production
/// contract: same UTC date, GEMLink 0.1-second key, no duplicate Base key,
/// and no interpolation across gaps greater than 999 seconds.
/// </summary>
public static class FieldMagCoverageService
{
    public const double MaxInterpolationGapSeconds = 999d;
    private const double UavContinuityGapSeconds = 5d;
    private const double RejectedBaseContinuityGapSeconds = 60d;

    public static IReadOnlyList<FieldMagDailyCoverageSummary> Analyze(
        FieldMagFolderAnalysis analysis) =>
        Analyze(analysis.UavSamples, analysis.BaseSamples, TimeZoneInfo.FindSystemTimeZoneById(analysis.ProjectTimeZoneId));

    public static IReadOnlyList<FieldMagDailyCoverageSummary> Analyze(
        IReadOnlyList<UavMagCanonicalSample> uavSamples,
        IReadOnlyList<Gsm19TCanonicalSample> baseSamples, TimeZoneInfo? timeZone = null)
    {
        timeZone ??= ProjectTime.Beijing;
        List<UavMagCanonicalSample> eligibleUav =
            (uavSamples ?? Array.Empty<UavMagCanonicalSample>())
                .Where(IsEligibleRover)
                .OrderBy(x => x.TimestampUtc)
                .ThenBy(x => x.SourceFile, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.SourceRecord)
                .ToList();

        List<Gsm19TCanonicalSample> allBase =
            (baseSamples ?? Array.Empty<Gsm19TCanonicalSample>())
                .OrderBy(x => x.TimestampUtc)
                .ThenBy(x => x.SourceFile, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.SourceRecord)
                .ToList();

        List<Gsm19TCanonicalSample> acceptedBase =
            DiurnalBaseValidityGate.Accepted(allBase).ToList();

        Dictionary<DateTime, BaseDayCoverage> baseDays =
            acceptedBase
                .GroupBy(x => x.TimestampUtc.UtcDateTime.Date)
                .ToDictionary(
                    group => group.Key,
                    group => BuildBaseDayCoverage(group.Key, group.ToList()));

        DateTime[] beijingDates = eligibleUav
            .Select(x => ProjectTime.Date(x.TimestampUtc, timeZone))
            .Distinct()
            .OrderBy(x => x)
            .ToArray();

        var result = new List<FieldMagDailyCoverageSummary>(beijingDates.Length);
        foreach (DateTime beijingDate in beijingDates)
        {
            DateTimeOffset dayStartUtc = ProjectTime.DayStartUtc(beijingDate, timeZone);
            DateTimeOffset dayEndUtc = ProjectTime.DayStartUtc(beijingDate.AddDays(1), timeZone);

            List<UavMagCanonicalSample> dayUav = eligibleUav
                .Where(x => x.TimestampUtc >= dayStartUtc && x.TimestampUtc < dayEndUtc)
                .ToList();
            List<Gsm19TCanonicalSample> dayBase = allBase
                .Where(x => x.TimestampUtc >= dayStartUtc && x.TimestampUtc < dayEndUtc)
                .ToList();

            var classified = new List<ClassifiedUav>(dayUav.Count);
            foreach (UavMagCanonicalSample row in dayUav)
            {
                string reason = ClassifyCoverage(row.TimestampUtc, baseDays);
                classified.Add(new ClassifiedUav(
                    row,
                    reason == "COVERED",
                    reason));
            }

            int covered = classified.Count(x => x.Covered);
            int uncovered = classified.Count - covered;
            double percent = classified.Count == 0
                ? 0d
                : covered * 100d / classified.Count;

            List<FieldMagCoverageSegment> uavSegments =
                BuildUavSegments(classified);
            List<FieldMagCoverageSegment> baseCoverageSegments =
                BuildBaseCoverageSegments(
                    baseDays,
                    dayStartUtc,
                    dayEndUtc);
            List<FieldMagCoverageSegment> rejectedBaseSegments =
                BuildRejectedBaseSegments(
                    dayBase.Where(x => !DiurnalBaseValidityGate.IsEligible(x)).ToList());

            List<FieldMagCoverageSegment> uncoveredSegments = uavSegments
                .Where(x => x.Kind == FieldMagCoverageSegmentKind.UavUncovered)
                .ToList();

            IReadOnlyList<string> reasons = classified
                .Where(x => !x.Covered)
                .Select(x => x.Reason)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();

            string status = classified.Count == 0
                ? "NO_UAV"
                : uncovered == 0
                    ? "COMPLETE"
                    : covered == 0
                        ? "NO_COVERAGE"
                        : "PARTIAL";

            result.Add(new FieldMagDailyCoverageSummary(
                beijingDate.Date,
                classified.Count,
                covered,
                uncovered,
                percent,
                dayBase.Count,
                dayBase.Count(DiurnalBaseValidityGate.IsEligible),
                dayBase.Count(x => !DiurnalBaseValidityGate.IsEligible(x)),
                classified.Count == 0 ? null : classified[0].Sample.TimestampUtc,
                classified.Count == 0 ? null : classified[^1].Sample.TimestampUtc,
                uncoveredSegments.Count == 0 ? null : uncoveredSegments.Min(x => x.StartUtc),
                uncoveredSegments.Count == 0 ? null : uncoveredSegments.Max(x => x.EndUtc),
                uncoveredSegments.Count == 0
                    ? 0d
                    : uncoveredSegments.Max(x => Math.Max(0d, (x.EndUtc - x.StartUtc).TotalSeconds)),
                uncoveredSegments.Count,
                status,
                reasons,
                uavSegments
                    .Concat(baseCoverageSegments)
                    .Concat(rejectedBaseSegments)
                    .OrderBy(x => x.StartUtc)
                    .ThenBy(x => x.Kind)
                    .ToList()));
        }

        return result;
    }

    public static DateTimeOffset BeijingDayStartUtc(DateTime beijingDate) =>
        new DateTimeOffset(
                DateTime.SpecifyKind(beijingDate.Date, DateTimeKind.Unspecified),
                FieldMagDataService.BeijingUtcOffset)
            .ToUniversalTime();

    private static bool IsEligibleRover(UavMagCanonicalSample row) =>
        row.MagneticFieldNt.HasValue &&
        !row.SensorStatus.Equals("F", StringComparison.OrdinalIgnoreCase);

    private static BaseDayCoverage BuildBaseDayCoverage(
        DateTime utcDate,
        IReadOnlyList<Gsm19TCanonicalSample> rows)
    {
        List<IGrouping<int, Gsm19TCanonicalSample>> groups = rows
            .GroupBy(x => GemLinkTimeCompatibility.ToTenthsOfSecondSinceUtcMidnight(x.TimestampUtc))
            .OrderBy(x => x.Key)
            .ToList();

        if (groups.Any(x => x.Count() > 1))
        {
            return new BaseDayCoverage(
                utcDate,
                true,
                [],
                []);
        }

        List<BasePoint> points = groups
            .Select(group => new BasePoint(group.Key, group.Single().TimestampUtc))
            .ToList();
        if (points.Count == 0)
            return new BaseDayCoverage(utcDate, false, [], []);

        var intervals = new List<BaseKeyInterval>();
        int startKey = points[0].Key;
        int endKey = points[0].Key;
        DateTimeOffset startUtc = points[0].TimestampUtc;
        DateTimeOffset endUtc = points[0].TimestampUtc;

        for (int i = 1; i < points.Count; i++)
        {
            BasePoint point = points[i];
            BasePoint previous = points[i - 1];
            double gapSeconds = (point.Key - previous.Key) / 10d;
            if (gapSeconds <= MaxInterpolationGapSeconds)
            {
                endKey = point.Key;
                endUtc = point.TimestampUtc;
                continue;
            }

            intervals.Add(new BaseKeyInterval(startKey, endKey, startUtc, endUtc));
            startKey = point.Key;
            endKey = point.Key;
            startUtc = point.TimestampUtc;
            endUtc = point.TimestampUtc;
        }

        intervals.Add(new BaseKeyInterval(startKey, endKey, startUtc, endUtc));
        return new BaseDayCoverage(utcDate, false, points, intervals);
    }

    private static string ClassifyCoverage(
        DateTimeOffset timestampUtc,
        IReadOnlyDictionary<DateTime, BaseDayCoverage> baseDays)
    {
        DateTime utcDate = timestampUtc.UtcDateTime.Date;
        if (!baseDays.TryGetValue(utcDate, out BaseDayCoverage? day) ||
            day.Points.Count == 0)
        {
            return "NO_VALID_BASE_FOR_UTC_DATE";
        }

        if (day.HasDuplicateKey)
            return "DUPLICATE_BASE_TIME";

        int key = GemLinkTimeCompatibility
            .ToTenthsOfSecondSinceUtcMidnight(timestampUtc);
        int index = LowerBound(day.Points, key);
        if (index < day.Points.Count && day.Points[index].Key == key)
            return "COVERED";
        if (index <= 0)
            return "NO_BASE_BEFORE";
        if (index >= day.Points.Count)
            return "NO_BASE_AFTER";

        BasePoint previous = day.Points[index - 1];
        BasePoint next = day.Points[index];
        double gapSeconds = (next.Key - previous.Key) / 10d;
        return gapSeconds <= MaxInterpolationGapSeconds
            ? "COVERED"
            : "BASE_GAP_TOO_LARGE";
    }

    private static int LowerBound(
        IReadOnlyList<BasePoint> points,
        int key)
    {
        int low = 0;
        int high = points.Count;
        while (low < high)
        {
            int mid = low + (high - low) / 2;
            if (points[mid].Key < key)
                low = mid + 1;
            else
                high = mid;
        }
        return low;
    }

    private static List<FieldMagCoverageSegment> BuildUavSegments(
        IReadOnlyList<ClassifiedUav> rows)
    {
        var result = new List<FieldMagCoverageSegment>();
        if (rows.Count == 0)
            return result;

        ClassifiedUav first = rows[0];
        DateTimeOffset start = first.Sample.TimestampUtc;
        DateTimeOffset end = start;
        bool covered = first.Covered;
        string reason = first.Reason;
        string source = first.Sample.SourceFile;

        for (int i = 1; i < rows.Count; i++)
        {
            ClassifiedUav row = rows[i];
            double gap = (row.Sample.TimestampUtc - end).TotalSeconds;
            bool sameSegment =
                row.Covered == covered &&
                row.Reason.Equals(reason, StringComparison.OrdinalIgnoreCase) &&
                row.Sample.SourceFile.Equals(source, StringComparison.OrdinalIgnoreCase) &&
                gap >= 0d &&
                gap <= UavContinuityGapSeconds;

            if (sameSegment)
            {
                end = row.Sample.TimestampUtc;
                continue;
            }

            result.Add(new FieldMagCoverageSegment(
                start,
                end,
                covered
                    ? FieldMagCoverageSegmentKind.UavCovered
                    : FieldMagCoverageSegmentKind.UavUncovered,
                reason));
            start = row.Sample.TimestampUtc;
            end = start;
            covered = row.Covered;
            reason = row.Reason;
            source = row.Sample.SourceFile;
        }

        result.Add(new FieldMagCoverageSegment(
            start,
            end,
            covered
                ? FieldMagCoverageSegmentKind.UavCovered
                : FieldMagCoverageSegmentKind.UavUncovered,
            reason));
        return result;
    }

    private static List<FieldMagCoverageSegment> BuildBaseCoverageSegments(
        IReadOnlyDictionary<DateTime, BaseDayCoverage> baseDays,
        DateTimeOffset dayStartUtc,
        DateTimeOffset dayEndUtc)
    {
        var result = new List<FieldMagCoverageSegment>();
        DateTime utcStartDate = dayStartUtc.UtcDateTime.Date;
        DateTime utcEndDate = dayEndUtc.AddTicks(-1).UtcDateTime.Date;
        for (DateTime utcDate = utcStartDate;
             utcDate <= utcEndDate;
             utcDate = utcDate.AddDays(1))
        {
            if (!baseDays.TryGetValue(utcDate, out BaseDayCoverage? day) ||
                day.HasDuplicateKey)
            {
                continue;
            }

            foreach (BaseKeyInterval interval in day.Intervals)
            {
                DateTimeOffset start = interval.StartUtc < dayStartUtc
                    ? dayStartUtc
                    : interval.StartUtc;
                DateTimeOffset end = interval.EndUtc >= dayEndUtc
                    ? dayEndUtc.AddTicks(-1)
                    : interval.EndUtc;
                if (end < dayStartUtc || start >= dayEndUtc || end < start)
                    continue;

                result.Add(new FieldMagCoverageSegment(
                    start,
                    end,
                    FieldMagCoverageSegmentKind.BaseValidCoverage,
                    "PRODUCTION_BASE_COVERAGE"));
            }
        }
        return result;
    }

    private static List<FieldMagCoverageSegment> BuildRejectedBaseSegments(
        IReadOnlyList<Gsm19TCanonicalSample> rows)
    {
        var result = new List<FieldMagCoverageSegment>();
        if (rows.Count == 0)
            return result;

        foreach (IGrouping<string, Gsm19TCanonicalSample> sourceGroup in
                 rows.GroupBy(x => x.SourceFile, StringComparer.OrdinalIgnoreCase))
        {
            List<Gsm19TCanonicalSample> ordered = sourceGroup
                .OrderBy(x => x.TimestampUtc)
                .ThenBy(x => x.SourceRecord)
                .ToList();
            if (ordered.Count == 0)
                continue;

            DateTimeOffset start = ordered[0].TimestampUtc;
            DateTimeOffset end = start;
            var reasons = new HashSet<string>(
                ordered[0].QualityFlags,
                StringComparer.OrdinalIgnoreCase);

            for (int i = 1; i < ordered.Count; i++)
            {
                Gsm19TCanonicalSample row = ordered[i];
                double gap = (row.TimestampUtc - end).TotalSeconds;
                if (gap >= 0d && gap <= RejectedBaseContinuityGapSeconds)
                {
                    end = row.TimestampUtc;
                    reasons.UnionWith(row.QualityFlags);
                    continue;
                }

                result.Add(new FieldMagCoverageSegment(
                    start,
                    end,
                    FieldMagCoverageSegmentKind.BaseRejected,
                    CoverageReason(reasons)));
                start = row.TimestampUtc;
                end = start;
                reasons = new HashSet<string>(row.QualityFlags, StringComparer.OrdinalIgnoreCase);
            }

            result.Add(new FieldMagCoverageSegment(
                start,
                end,
                FieldMagCoverageSegmentKind.BaseRejected,
                CoverageReason(reasons)));
        }

        return result;
    }

    private static string CoverageReason(IEnumerable<string> flags)
    {
        string[] preferred =
        [
            "BASE_SESSION_INVALID",
            "BASE_DAY_OUTLIER",
            "BASE_VALUE_OUT_OF_RANGE",
            "SQ_OUT_OF_RANGE",
            "LOW_SQ",
            "TIMESTAMP_CONFLICT"
        ];
        HashSet<string> set = flags.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return preferred.FirstOrDefault(set.Contains) ?? "BASE_REJECTED";
    }

    private sealed record ClassifiedUav(
        UavMagCanonicalSample Sample,
        bool Covered,
        string Reason);

    private sealed record BasePoint(int Key, DateTimeOffset TimestampUtc);

    private sealed record BaseKeyInterval(
        int StartKey,
        int EndKey,
        DateTimeOffset StartUtc,
        DateTimeOffset EndUtc);

    private sealed record BaseDayCoverage(
        DateTime UtcDate,
        bool HasDuplicateKey,
        IReadOnlyList<BasePoint> Points,
        IReadOnlyList<BaseKeyInterval> Intervals);
}
