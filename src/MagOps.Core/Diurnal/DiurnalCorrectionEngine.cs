namespace MagOps.Core.Diurnal;

/// <summary>
/// Independent MagOps implementation of the confirmed GEMLink 5.4.8.1
/// numerical behavior. It does not call or copy GEMLink code.
/// </summary>
public sealed class DiurnalCorrectionEngine
{
    public const string AlgorithmVersion =
        "magops-diurnal-gemlink-compatible-2";

    public DiurnalCorrectionResult Correct(
        IReadOnlyList<DiurnalRoverSample> rover,
        IReadOnlyList<DiurnalBaseSample> baseSamples,
        DiurnalCorrectionOptions? options = null)
    {
        options ??= new DiurnalCorrectionOptions();
        ValidateOptions(options);

        List<DiurnalRoverSample> orderedRover =
            (rover ?? Array.Empty<DiurnalRoverSample>())
            .OrderBy(x => x.TimestampUtc)
            .ThenBy(x => x.SourceFile, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.SourceRow)
            .ToList();

        List<DiurnalBaseSample> orderedBase =
            (baseSamples ?? Array.Empty<DiurnalBaseSample>())
            .OrderBy(x => x.TimestampUtc)
            .ThenBy(x => x.SourceFile, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.SourceRow)
            .ToList();

        var corrected = new List<DiurnalCorrectedSample>(
            orderedRover.Count);
        var days = new List<DiurnalDaySummary>();

        foreach (IGrouping<DateTime, DiurnalRoverSample> dayGroup in
                 orderedRover.GroupBy(
                     x => DiurnalDatePolicy.UtcDay(x.TimestampUtc)))
        {
            DateTime utcDate = dayGroup.Key;
            List<DiurnalRoverSample> roverDay =
                dayGroup.OrderBy(x => x.TimestampUtc).ToList();
            List<DiurnalBaseSample> baseDay =
                orderedBase
                    .Where(x =>
                        DiurnalDatePolicy.UtcDay(x.TimestampUtc) ==
                        utcDate)
                    .OrderBy(x => x.TimestampUtc)
                    .ToList();

            if (baseDay.Count == 0)
            {
                List<DiurnalCorrectedSample> rows =
                    roverDay.Select(x =>
                        Uncorrected(
                            x,
                            options,
                            "NO_VALID_BASE_FOR_UTC_DATE"))
                    .ToList();
                corrected.AddRange(rows);
                days.Add(BuildDaySummary(
                    utcDate,
                    roverDay.Count,
                    0,
                    rows,
                    0,
                    false,
                    "NO_BASE",
                    ["NO_VALID_BASE_FOR_UTC_DATE"]));
                continue;
            }

            List<IGrouping<long, DiurnalBaseSample>> baseGroups =
                baseDay
                    .GroupBy(x =>
                        TimeKey(
                            x.TimestampUtc,
                            options))
                    .OrderBy(x => x.Key)
                    .ToList();

            int duplicateBaseKeys =
                baseGroups.Count(x => x.Count() > 1);

            // GEMLink behavior for duplicate 0.1-second Base keys is not
            // confirmed by the current golden evidence. Do not guess.
            // Conservatively reject correction for this UTC day.
            if (duplicateBaseKeys > 0)
            {
                List<DiurnalCorrectedSample> rows =
                    roverDay.Select(x =>
                        Uncorrected(
                            x,
                            options,
                            "DUPLICATE_BASE_TIME"))
                    .ToList();
                corrected.AddRange(rows);
                days.Add(BuildDaySummary(
                    utcDate,
                    roverDay.Count,
                    baseDay.Count,
                    rows,
                    duplicateBaseKeys,
                    false,
                    "DUPLICATE_BASE_TIME_REJECTED",
                    ["DUPLICATE_BASE_TIME"]));
                continue;
            }

            List<BasePoint> basePoints =
                baseGroups
                    .Select(x =>
                        new BasePoint(
                            x.Key,
                            x.Single()))
                    .ToList();

            long minRoverKey =
                roverDay.Min(x =>
                    TimeKey(
                        x.TimestampUtc,
                        options));
            long maxRoverKey =
                roverDay.Max(x =>
                    TimeKey(
                        x.TimestampUtc,
                        options));
            long minBaseKey = basePoints[0].Key;
            long maxBaseKey = basePoints[^1].Key;

            bool hasOverlap =
                !(maxBaseKey < minRoverKey ||
                  maxRoverKey < minBaseKey);

            if (!hasOverlap)
            {
                List<DiurnalCorrectedSample> rows =
                    roverDay.Select(x =>
                        Uncorrected(
                            x,
                            options,
                            "NO_OVERLAP"))
                    .ToList();
                corrected.AddRange(rows);
                days.Add(BuildDaySummary(
                    utcDate,
                    roverDay.Count,
                    baseDay.Count,
                    rows,
                    0,
                    false,
                    "NO_OVERLAP",
                    ["NO_OVERLAP"]));
                continue;
            }

            var rowsForDay =
                new List<DiurnalCorrectedSample>(
                    roverDay.Count);

            foreach (DiurnalRoverSample row in roverDay)
            {
                long key =
                    TimeKey(
                        row.TimestampUtc,
                        options);
                int index =
                    LowerBound(
                        basePoints,
                        key);

                if (index < basePoints.Count &&
                    basePoints[index].Key == key)
                {
                    BasePoint exact =
                        basePoints[index];
                    double exactCorrectedNt =
                        row.RawMagNt -
                        exact.Sample.BaseMagNt +
                        options.DatumNt;

                    rowsForDay.Add(
                        new DiurnalCorrectedSample(
                            row.TimestampUtc,
                            row.RawMagNt,
                            exact.Sample.BaseMagNt,
                            options.DatumNt,
                            exactCorrectedNt,
                            DiurnalCorrectionMode.Exact,
                            exact.Sample.TimestampUtc,
                            exact.Sample.TimestampUtc,
                            0d,
                            ["EXACT"],
                            row.SourceFile,
                            row.SourceRow,
                            exact.Sample.SourceFile,
                            exact.Sample.SourceRow,
                            exact.Sample.SourceFile,
                            exact.Sample.SourceRow));
                    continue;
                }

                int previousIndex = index - 1;
                int nextIndex = index;
                if (previousIndex < 0)
                {
                    rowsForDay.Add(
                        Uncorrected(
                            row,
                            options,
                            "NO_BASE_BEFORE"));
                    continue;
                }

                if (nextIndex >= basePoints.Count)
                {
                    rowsForDay.Add(
                        Uncorrected(
                            row,
                            options,
                            "NO_BASE_AFTER"));
                    continue;
                }

                BasePoint previous =
                    basePoints[previousIndex];
                BasePoint next =
                    basePoints[nextIndex];

                long denominator =
                    next.Key - previous.Key;
                if (denominator <= 0)
                {
                    rowsForDay.Add(
                        Uncorrected(
                            row,
                            options,
                            "DUPLICATE_BASE_TIME"));
                    continue;
                }

                double baseGapSeconds =
                    denominator /
                    TimeKeyUnitsPerSecond(options);

                if (options.MaxInterpolationGapSeconds.HasValue &&
                    baseGapSeconds >
                    options.MaxInterpolationGapSeconds.Value &&
                    options.RejectInterpolationAcrossLargeGap)
                {
                    rowsForDay.Add(
                        UncorrectedBetween(
                            row,
                            options,
                            previous.Sample,
                            next.Sample,
                            baseGapSeconds,
                            "BASE_GAP_TOO_LARGE"));
                    continue;
                }

                double ratio =
                    (key - previous.Key) /
                    (double)denominator;
                double baseUsed =
                    previous.Sample.BaseMagNt +
                    ratio *
                    (next.Sample.BaseMagNt -
                     previous.Sample.BaseMagNt);

                double interpolatedCorrectedNt =
                    row.RawMagNt -
                    baseUsed +
                    options.DatumNt;

                var flags =
                    new List<string>
                    {
                        "INTERPOLATED"
                    };
                if (options.MaxInterpolationGapSeconds.HasValue &&
                    baseGapSeconds >
                    options.MaxInterpolationGapSeconds.Value)
                {
                    flags.Add("BASE_GAP_LARGE");
                }

                rowsForDay.Add(
                    new DiurnalCorrectedSample(
                        row.TimestampUtc,
                        row.RawMagNt,
                        baseUsed,
                        options.DatumNt,
                        interpolatedCorrectedNt,
                        DiurnalCorrectionMode.Interpolated,
                        previous.Sample.TimestampUtc,
                        next.Sample.TimestampUtc,
                        baseGapSeconds,
                        flags,
                        row.SourceFile,
                        row.SourceRow,
                        previous.Sample.SourceFile,
                        previous.Sample.SourceRow,
                        next.Sample.SourceFile,
                        next.Sample.SourceRow));
            }

            corrected.AddRange(rowsForDay);

            string status =
                rowsForDay.All(x =>
                    x.Mode !=
                    DiurnalCorrectionMode.Uncorrected)
                    ? "CORRECTED"
                    : rowsForDay.Any(x =>
                        x.Mode !=
                        DiurnalCorrectionMode.Uncorrected)
                        ? "PARTIAL"
                        : "UNCORRECTED";

            IReadOnlyList<string> dayFlags =
                rowsForDay
                    .SelectMany(x =>
                        x.QualityFlags)
                    .Where(x =>
                        !x.Equals(
                            "EXACT",
                            StringComparison.OrdinalIgnoreCase) &&
                        !x.Equals(
                            "INTERPOLATED",
                            StringComparison.OrdinalIgnoreCase))
                    .Distinct(
                        StringComparer.OrdinalIgnoreCase)
                    .ToList();

            days.Add(BuildDaySummary(
                utcDate,
                roverDay.Count,
                baseDay.Count,
                rowsForDay,
                0,
                true,
                status,
                dayFlags));
        }

        return new DiurnalCorrectionResult(
            corrected
                .OrderBy(x => x.TimestampUtc)
                .ThenBy(
                    x => x.RoverSourceFile,
                    StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.RoverSourceRow)
                .ToList(),
            days
                .OrderBy(x => x.UtcDate)
                .ToList(),
            options);
    }

    private static void ValidateOptions(
        DiurnalCorrectionOptions options)
    {
        if (!double.IsFinite(options.DatumNt))
            throw new ArgumentOutOfRangeException(
                nameof(options.DatumNt));

        if (options.MaxInterpolationGapSeconds.HasValue &&
            (!double.IsFinite(
                 options.MaxInterpolationGapSeconds.Value) ||
             options.MaxInterpolationGapSeconds.Value <= 0d))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options.MaxInterpolationGapSeconds));
        }
    }

    private static long TimeKey(
        DateTimeOffset timestamp,
        DiurnalCorrectionOptions options)
    {
        DateTimeOffset utc =
            timestamp.ToUniversalTime();

        if (options.GemLinkTenthsCompatibility)
        {
            return GemLinkTimeCompatibility
                .ToTenthsOfSecondSinceUtcMidnight(
                    utc);
        }

        return utc.TimeOfDay.Ticks;
    }

    private static double TimeKeyUnitsPerSecond(
        DiurnalCorrectionOptions options) =>
        options.GemLinkTenthsCompatibility
            ? 10d
            : TimeSpan.TicksPerSecond;

    private static int LowerBound(
        IReadOnlyList<BasePoint> points,
        long key)
    {
        int low = 0;
        int high = points.Count;
        while (low < high)
        {
            int mid =
                low + (high - low) / 2;
            if (points[mid].Key < key)
                low = mid + 1;
            else
                high = mid;
        }
        return low;
    }


    private static DiurnalCorrectedSample UncorrectedBetween(
        DiurnalRoverSample row,
        DiurnalCorrectionOptions options,
        DiurnalBaseSample previous,
        DiurnalBaseSample next,
        double gapSeconds,
        params string[] flags) =>
        new(
            row.TimestampUtc,
            row.RawMagNt,
            null,
            options.DatumNt,
            null,
            DiurnalCorrectionMode.Uncorrected,
            previous.TimestampUtc,
            next.TimestampUtc,
            gapSeconds,
            flags,
            row.SourceFile,
            row.SourceRow,
            previous.SourceFile,
            previous.SourceRow,
            next.SourceFile,
            next.SourceRow);

    private static DiurnalCorrectedSample Uncorrected(
        DiurnalRoverSample row,
        DiurnalCorrectionOptions options,
        params string[] flags) =>
        new(
            row.TimestampUtc,
            row.RawMagNt,
            null,
            options.DatumNt,
            null,
            DiurnalCorrectionMode.Uncorrected,
            null,
            null,
            null,
            flags,
            row.SourceFile,
            row.SourceRow,
            null,
            null,
            null,
            null);

    private static DiurnalDaySummary BuildDaySummary(
        DateTime utcDate,
        int roverCount,
        int baseCount,
        IReadOnlyList<DiurnalCorrectedSample> rows,
        int duplicateBaseKeys,
        bool hasOverlap,
        string status,
        IReadOnlyList<string> flags) =>
        new(
            utcDate,
            roverCount,
            baseCount,
            rows.Count(x =>
                x.Mode ==
                DiurnalCorrectionMode.Exact),
            rows.Count(x =>
                x.Mode ==
                DiurnalCorrectionMode.Interpolated),
            rows.Count(x =>
                x.Mode ==
                DiurnalCorrectionMode.Uncorrected),
            duplicateBaseKeys,
            hasOverlap,
            status,
            flags);

    private sealed record BasePoint(
        long Key,
        DiurnalBaseSample Sample);
}
