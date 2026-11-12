using System.Globalization;
using System.Text;
using MagOps.Core.Coordinates;
using MagOps.Core.Diurnal;
using MagOps.Core.Geomagnetics;

namespace MagOps.Infrastructure.Services;

/// <summary>
/// Controls how T0 is assigned when creating Oasis-compatible pre-levelling data.
/// AutoGlobal is the new-project default. AutoByUtcDate remains available for
/// reproducing legacy results; ManualFixed uses one user-supplied T0.
/// </summary>
public enum MagneticT0Mode
{
    AutoByUtcDate,
    AutoGlobal,
    ManualFixed
}

public sealed record MagneticPreLevelProcessingOptions(
    MagneticT0Mode T0Mode = MagneticT0Mode.AutoGlobal,
    double ManualT0Nt = 0d,
    double? ProjectionCentralMeridianDeg = null,
    string ProjectionDatum = "CGCS2000",
    double ProjectionFalseEastingM = 500000d,
    double ProjectionFalseNorthingM = 0d,
    string ProjectionCentralMeridianSource = "");

public sealed record MagneticPreLevelDaySummary(
    DateOnly UtcDate,
    int ProcessingRecords,
    int IgrfRecords,
    double? T0Nt,
    double? DTgzMeanNt,
    double? DTgzMinNt,
    double? DTgzMaxNt);

public sealed record MagneticPreLevelProcessingSummary(
    int ProcessingRecords,
    int IgrfRecords,
    int IgrfSkippedRecords,
    string T0Mode,
    double? FixedT0Nt,
    double? T0MinNt,
    double? T0MaxNt,
    double? DTgzMeanNt,
    double? DTgzMinNt,
    double? DTgzMaxNt,
    IReadOnlyList<MagneticPreLevelDaySummary> Days,
    string? IgrfCoefficientSha256,
    double? ProjectionCentralMeridianDeg,
    string ProjectionCentralMeridianSource,
    double? ProcessingDataSuggestedCentralMeridianDeg,
    bool ProjectionCentralMeridianConsistent,
    string ProjectionDatum,
    double ProjectionFalseEastingM,
    double ProjectionFalseNorthingM);

public static class MagneticPreLevelProcessingService
{
    public static readonly IReadOnlyList<string> ProcessingColumns = Array.AsReadOnly(
        "LineName,LineOrder,LinePass,DateUtc,TimeUtc,LineTaskId,Longitude,Latitude,X,Y,AltitudeM,LaserAltitudeM,RawMagNt,BaseUsedNt,rigai,Tof,Inc,Dec,T0,dTgz".Split(','));
    public const string Formula = "dTgz = rigai + T0 - Tof";
    public const string RigaiFormula = "rigai = Rover - BaseUsed";

    public static MagneticPreLevelProcessingSummary WriteProcessingCsv(
        string path,
        IReadOnlyList<UavMagCanonicalSample> allRover,
        IReadOnlyDictionary<string, DiurnalCorrectedSample> correction,
        IgrfModelSet igrfModels,
        MagneticPreLevelProcessingOptions? options = null,
        MagneticSurveyLineSegmentationResult? segmentation = null,
        MagneticRepeatReplacementResult? repeatReplacement = null,
        Action<UavMagCanonicalSample, MagneticSegmentAssignment?>? recordExported = null,
        Action<int, IReadOnlyList<string>>? recordProcessingRow = null)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Output path is required.", nameof(path));
        ArgumentNullException.ThrowIfNull(allRover);
        ArgumentNullException.ThrowIfNull(correction);
        ArgumentNullException.ThrowIfNull(igrfModels);

        options ??= new MagneticPreLevelProcessingOptions();
        if (repeatReplacement is not null &&
            repeatReplacement.ActiveRuleCount > 0 &&
            (segmentation is null || !segmentation.Options.Enabled))
        {
            throw new InvalidOperationException(
                "Repeat-survey replacement requires enabled survey-line segmentation.");
        }
        if (!double.IsFinite(options.ManualT0Nt))
            throw new ArgumentOutOfRangeException(nameof(options), "Manual T0 must be finite.");
        if (options.ProjectionCentralMeridianDeg.HasValue &&
            (!double.IsFinite(options.ProjectionCentralMeridianDeg.Value) ||
             options.ProjectionCentralMeridianDeg.Value is < -180d or > 180d))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Projection central meridian must be within -180 to 180 degrees.");
        }
        if (!double.IsFinite(options.ProjectionFalseEastingM) ||
            !double.IsFinite(options.ProjectionFalseNorthingM))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Projection false easting/northing must be finite.");
        }

        double? processingDataSuggestedCentralMeridian =
            SuggestProjectionCentralMeridian(
                allRover,
                correction,
                segmentation,
                repeatReplacement);

        // The final Rover records that actually survive production filtering are
        // authoritative for this run's GK3 projection. Project/route/manual values
        // remain provenance and fallback only; a stale workspace value must not
        // silently shift today's XY or block otherwise valid Rover data.
        double? projectionCentralMeridian =
            processingDataSuggestedCentralMeridian ??
            options.ProjectionCentralMeridianDeg;
        bool projectionCentralMeridianConsistent =
            !options.ProjectionCentralMeridianDeg.HasValue ||
            !processingDataSuggestedCentralMeridian.HasValue ||
            Math.Abs(
                options.ProjectionCentralMeridianDeg.Value -
                processingDataSuggestedCentralMeridian.Value) <= 1e-9;

        string projectionCentralMeridianSource =
            processingDataSuggestedCentralMeridian.HasValue
                ? projectionCentralMeridianConsistent
                    ? "AUTO_GK3_FROM_FINAL_PROCESSING_DATA"
                    : "AUTO_GK3_FROM_FINAL_PROCESSING_DATA; REFERENCE_MISMATCH=" +
                      (string.IsNullOrWhiteSpace(options.ProjectionCentralMeridianSource)
                          ? "PROJECT_OR_EXPLICIT"
                          : options.ProjectionCentralMeridianSource.Trim()) +
                      ":" +
                      options.ProjectionCentralMeridianDeg!.Value.ToString(
                          "0.########",
                          CultureInfo.InvariantCulture) +
                      "deg"
                : options.ProjectionCentralMeridianDeg.HasValue
                    ? string.IsNullOrWhiteSpace(options.ProjectionCentralMeridianSource)
                        ? "PROJECT_OR_EXPLICIT_FALLBACK"
                        : options.ProjectionCentralMeridianSource.Trim() + "; FALLBACK_NO_DATA_SUGGESTION"
                    : "UNAVAILABLE";

        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        string tempPath = fullPath + ".prelevel." + Guid.NewGuid().ToString("N") + ".tmp";
        string stagedCsv = fullPath + ".staging." + Guid.NewGuid().ToString("N") + ".tmp";
        int processingRecords = 0;
        int igrfRecords = 0;
        int igrfSkippedRecords = 0;
        double rawDtSum = 0d;
        var firstPassDays = new Dictionary<DateOnly, FirstPassDayStats>();
        var nextOutputPass = (segmentation?.Assignments ?? []).Where(a => a.LinePass.HasValue)
            .GroupBy(a => a.LineTaskId).ToDictionary(g => g.Key, g => g.Max(a => a.LinePass!.Value));
        var lastOutput = new Dictionary<(string Task, int Pass), (UavMagCanonicalSample Sample, int OutputPass)>();

        try
        {
            using (var tempWriter = new StreamWriter(
                       tempPath,
                       false,
                       new UTF8Encoding(false)))
            {
                foreach (UavMagCanonicalSample row in
                         allRover
                             .OrderBy(x => x.TimestampUtc)
                             .ThenBy(
                                 x => x.SourceFile,
                                 StringComparer.OrdinalIgnoreCase)
                             .ThenBy(x => x.SourceRecord))
                {
                    if (!row.Longitude.HasValue ||
                        !row.Latitude.HasValue ||
                        !correction.TryGetValue(
                            SampleKey(row.SourceFile, row.SourceRecord),
                            out DiurnalCorrectedSample? corrected) ||
                        !corrected.DiurnalDifferenceNt.HasValue)
                    {
                        continue;
                    }

                    MagneticSegmentAssignment? segment = null;
                    if (segmentation is not null && segmentation.Options.Enabled)
                    {
                        if (!segmentation.TryGet(
                                row.SourceFile,
                                row.SourceRecord,
                                out MagneticSegmentAssignment assignment) ||
                            !assignment.IncludeInProcessing)
                        {
                            continue;
                        }

                        if (repeatReplacement is not null &&
                            !repeatReplacement.ShouldInclude(assignment))
                        {
                            continue;
                        }

                        segment = assignment;
                    }

                    if (segment is { LinePass: { } originalPass } current)
                    {
                        var key = (current.LineTaskId, originalPass);
                        int outputPass = originalPass;
                        if (lastOutput.TryGetValue(key, out var last))
                        {
                            bool continuous = last.Sample.SourceFile.Equals(row.SourceFile, StringComparison.OrdinalIgnoreCase) &&
                                row.SourceRecord == last.Sample.SourceRecord + 1 &&
                                (row.TimestampUtc - last.Sample.TimestampUtc).TotalSeconds <= 8;
                            outputPass = continuous ? last.OutputPass : ++nextOutputPass[current.LineTaskId];
                        }
                        segment = current with { LinePass = outputPass };
                        lastOutput[key] = (row, outputPass);
                    }
                    processingRecords++;
                    recordExported?.Invoke(row, segment);
                    DateOnly dateUtc = DateOnly.FromDateTime(
                        row.TimestampUtc.UtcDateTime.Date);
                    FirstPassDayStats dayStats = GetOrCreateFirstPassDay(
                        firstPassDays,
                        dateUtc);
                    dayStats.ProcessingRecords++;

                    double rigai = corrected.DiurnalDifferenceNt.Value;
                    GeomagneticFieldResult? field = null;

                    if (row.AltitudeM.HasValue)
                    {
                        try
                        {
                            field = IgrfSphericalHarmonicEngine.Evaluate(
                                igrfModels,
                                row.Latitude.Value,
                                row.Longitude.Value,
                                row.AltitudeM.Value,
                                dateUtc);
                        }
                        catch (ArgumentOutOfRangeException)
                        {
                            igrfSkippedRecords++;
                        }
                        catch (InvalidOperationException)
                        {
                            igrfSkippedRecords++;
                        }
                    }
                    else
                    {
                        igrfSkippedRecords++;
                    }

                    string rawDtText = "";
                    if (field is not null)
                    {
                        double rawDt = rigai - field.TotalNt;
                        rawDtSum += rawDt;
                        igrfRecords++;
                        dayStats.IgrfRecords++;
                        dayStats.RawDtSum += rawDt;
                        rawDtText = Number(rawDt, "0.######");
                    }

                    if (!projectionCentralMeridian.HasValue)
                        throw new InvalidOperationException(
                            "Cannot create processing X/Y coordinates without a projection central meridian.");

                    var projected = CoordinateService.GaussKrugerForward(
                        row.Longitude.Value,
                        row.Latitude.Value,
                        projectionCentralMeridian.Value,
                        options.ProjectionDatum,
                        options.ProjectionFalseEastingM,
                        options.ProjectionFalseNorthingM);

                    // Internal TSV is intentionally numeric/text-safe and is deleted
                    // immediately after the final CSV is written. It prevents a second
                    // full spherical-harmonic pass when T0 is auto-estimated.
                    // X/Y semantics are fixed for the processing table: X=Easting,
                    // Y=Northing, in metres, so Oasis Microlevelling can consume them
                    // without an axis-order guess.
                    tempWriter.WriteLine(string.Join(
                        "\t",
                        UtcDate(row.TimestampUtc),
                        UtcTime(row.TimestampUtc),
                        TsvText(segment?.LineTaskId ?? ""),
                        TsvText(segment?.LineName ?? ""),
                        segment?.LineOrder?.ToString(CultureInfo.InvariantCulture) ?? "",
                        segment?.LinePass?.ToString(CultureInfo.InvariantCulture) ?? "",
                        Number(row.Longitude, "0.########"),
                        Number(row.Latitude, "0.########"),
                        Number(projected.Easting, "0.###"),
                        Number(projected.Northing, "0.###"),
                        Number(row.AltitudeM, "0.###"),
                        Number(row.LaserAltitudeM, "0.###"),
                        Number(row.MagneticFieldNt, "0.###"),
                        Number(corrected.BaseUsedNt, "0.###"),
                        Number(rigai, "0.###"),
                        field is null ? "" : Number(field.TotalNt, "0.###"),
                        field is null ? "" : Number(field.InclinationDeg, "0.######"),
                        field is null ? "" : Number(field.DeclinationDeg, "0.######"),
                        rawDtText));
                }
            }

            string t0Mode = options.T0Mode switch
            {
                MagneticT0Mode.AutoByUtcDate => "AUTO_BY_UTC_DATE",
                MagneticT0Mode.AutoGlobal => "AUTO_GLOBAL_MEAN_CENTERED",
                MagneticT0Mode.ManualFixed => "MANUAL_FIXED",
                _ => throw new InvalidOperationException("Unsupported T0 mode.")
            };

            double? fixedT0Nt = options.T0Mode switch
            {
                MagneticT0Mode.AutoGlobal when igrfRecords > 0 =>
                    -(rawDtSum / igrfRecords),
                MagneticT0Mode.ManualFixed => options.ManualT0Nt,
                _ => null
            };

            var t0ByDate = new Dictionary<DateOnly, double>();
            foreach (KeyValuePair<DateOnly, FirstPassDayStats> pair in firstPassDays)
            {
                DateOnly date = pair.Key;
                FirstPassDayStats stats = pair.Value;
                switch (options.T0Mode)
                {
                    case MagneticT0Mode.AutoByUtcDate:
                        if (stats.IgrfRecords > 0)
                            t0ByDate[date] = -(stats.RawDtSum / stats.IgrfRecords);
                        break;
                    case MagneticT0Mode.AutoGlobal:
                        if (fixedT0Nt.HasValue && stats.IgrfRecords > 0)
                            t0ByDate[date] = fixedT0Nt.Value;
                        break;
                    case MagneticT0Mode.ManualFixed:
                        t0ByDate[date] = options.ManualT0Nt;
                        break;
                    default:
                        throw new InvalidOperationException("Unsupported T0 mode.");
                }
            }

            double? dtgzMin = null;
            double? dtgzMax = null;
            double dtgzSum = 0d;
            int dtgzCount = 0;
            var outputDays = firstPassDays.ToDictionary(
                x => x.Key,
                x => new OutputDayStats());

            using (var writer = new StreamWriter(
                       stagedCsv,
                       false,
                       new UTF8Encoding(true)))
            using (var reader = new StreamReader(
                       tempPath,
                       new UTF8Encoding(false)))
            {
                writer.WriteLine(string.Join(",", ProcessingColumns));

                string? line;
                int outputRowIndex = 0;
                while ((line = reader.ReadLine()) is not null)
                {
                    string[] parts = line.Split('\t');
                    if (parts.Length != 19)
                        throw new InvalidDataException("Internal pre-levelling row shape is invalid.");

                    if (!DateOnly.TryParseExact(
                            parts[0],
                            "yyyy-MM-dd",
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.None,
                            out DateOnly dateUtc))
                    {
                        throw new InvalidDataException("Internal pre-levelling UTC date is invalid.");
                    }

                    bool hasT0 = t0ByDate.TryGetValue(dateUtc, out double rowT0Nt);
                    string dtgzText = "";
                    if (hasT0 &&
                        !string.IsNullOrWhiteSpace(parts[18]) &&
                        double.TryParse(
                            parts[18],
                            NumberStyles.Float,
                            CultureInfo.InvariantCulture,
                            out double rawDt))
                    {
                        double dtgz = rawDt + rowT0Nt;
                        dtgzText = Number(dtgz, "0.###");
                        dtgzSum += dtgz;
                        dtgzCount++;
                        dtgzMin = !dtgzMin.HasValue
                            ? dtgz
                            : Math.Min(dtgzMin.Value, dtgz);
                        dtgzMax = !dtgzMax.HasValue
                            ? dtgz
                            : Math.Max(dtgzMax.Value, dtgz);

                        OutputDayStats outputDay = outputDays[dateUtc];
                        outputDay.DTgzCount++;
                        outputDay.DTgzSum += dtgz;
                        outputDay.DTgzMin = !outputDay.DTgzMin.HasValue
                            ? dtgz
                            : Math.Min(outputDay.DTgzMin.Value, dtgz);
                        outputDay.DTgzMax = !outputDay.DTgzMax.HasValue
                            ? dtgz
                            : Math.Max(outputDay.DTgzMax.Value, dtgz);
                    }

                    var outputColumns =
                        new List<string>(20);
                    outputColumns.AddRange(new[] { parts[3], parts[4], parts[5], parts[0], parts[1], parts[2] }
                        .Concat(parts.Skip(6).Take(12)));
                    outputColumns.Add(
                        hasT0 ? Number(rowT0Nt, "0.###") : "");
                    outputColumns.Add(dtgzText);
                    writer.WriteLine(
                        string.Join(",", outputColumns.Select(Csv)));
                    recordProcessingRow?.Invoke(outputRowIndex++, outputColumns.AsReadOnly());
                }
            }

            IReadOnlyList<MagneticPreLevelDaySummary> days =
                firstPassDays
                    .OrderBy(x => x.Key)
                    .Select(x =>
                    {
                        OutputDayStats output = outputDays[x.Key];
                        return new MagneticPreLevelDaySummary(
                            x.Key,
                            x.Value.ProcessingRecords,
                            x.Value.IgrfRecords,
                            t0ByDate.TryGetValue(x.Key, out double dayT0)
                                ? dayT0
                                : null,
                            output.DTgzCount > 0
                                ? output.DTgzSum / output.DTgzCount
                                : null,
                            output.DTgzMin,
                            output.DTgzMax);
                    })
                    .ToArray();

            double[] availableT0 = days
                .Where(x => x.T0Nt.HasValue)
                .Select(x => x.T0Nt!.Value)
                .ToArray();

            var summary = new MagneticPreLevelProcessingSummary(
                processingRecords,
                igrfRecords,
                igrfSkippedRecords,
                t0Mode,
                fixedT0Nt,
                availableT0.Length > 0 ? availableT0.Min() : null,
                availableT0.Length > 0 ? availableT0.Max() : null,
                dtgzCount > 0 ? dtgzSum / dtgzCount : null,
                dtgzMin,
                dtgzMax,
                days,
                igrfModels.SourceSha256,
                projectionCentralMeridian,
                projectionCentralMeridianSource,
                processingDataSuggestedCentralMeridian,
                projectionCentralMeridianConsistent,
                options.ProjectionDatum,
                options.ProjectionFalseEastingM,
                options.ProjectionFalseNorthingM);
            // A failed calculation leaves the last complete CSV untouched.
            File.Move(stagedCsv, fullPath, true);
            return summary;
        }
        finally
        {
            try
            {
                File.Delete(tempPath);
                if (File.Exists(stagedCsv)) File.Delete(stagedCsv);
            }
            catch
            {
                // Best-effort cleanup only; output generation result should not be
                // hidden by a temporary-file deletion failure.
            }
        }
    }

    public static void WriteT0DaySummaryCsv(
        string path,
        MagneticPreLevelProcessingSummary summary)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Output path is required.", nameof(path));
        ArgumentNullException.ThrowIfNull(summary);

        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        using var writer = new StreamWriter(
            fullPath,
            false,
            new UTF8Encoding(true));
        writer.WriteLine(
            "DateUtc,T0Mode,T0,IgrfRecords,ProcessingRecords,dTgzMean,dTgzMin,dTgzMax");

        foreach (MagneticPreLevelDaySummary day in summary.Days)
        {
            writer.WriteLine(string.Join(",",
                day.UtcDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                summary.T0Mode,
                Number(day.T0Nt, "0.###"),
                day.IgrfRecords.ToString(CultureInfo.InvariantCulture),
                day.ProcessingRecords.ToString(CultureInfo.InvariantCulture),
                Number(day.DTgzMeanNt, "0.###"),
                Number(day.DTgzMinNt, "0.###"),
                Number(day.DTgzMaxNt, "0.###")));
        }
    }

    private static FirstPassDayStats GetOrCreateFirstPassDay(
        IDictionary<DateOnly, FirstPassDayStats> days,
        DateOnly date)
    {
        if (!days.TryGetValue(date, out FirstPassDayStats? stats))
        {
            stats = new FirstPassDayStats();
            days[date] = stats;
        }

        return stats;
    }

    private static double? SuggestProjectionCentralMeridian(
        IReadOnlyList<UavMagCanonicalSample> allRover,
        IReadOnlyDictionary<string, DiurnalCorrectedSample> correction,
        MagneticSurveyLineSegmentationResult? segmentation,
        MagneticRepeatReplacementResult? repeatReplacement)
    {
        double[] longitudes = allRover
            .Where(x =>
                x.Longitude.HasValue &&
                x.Latitude.HasValue &&
                correction.TryGetValue(
                    SampleKey(x.SourceFile, x.SourceRecord),
                    out DiurnalCorrectedSample? corrected) &&
                corrected.DiurnalDifferenceNt.HasValue &&
                (segmentation is null ||
                 !segmentation.Options.Enabled ||
                 (segmentation.TryGet(
                      x.SourceFile,
                      x.SourceRecord,
                      out MagneticSegmentAssignment segment) &&
                  segment.IncludeInProcessing &&
                  (repeatReplacement is null ||
                   repeatReplacement.ShouldInclude(segment)))))
            .Select(x => x.Longitude!.Value)
            .OrderBy(x => x)
            .ToArray();

        if (longitudes.Length == 0)
            return null;

        double medianLongitude = longitudes.Length % 2 == 1
            ? longitudes[longitudes.Length / 2]
            : (longitudes[(longitudes.Length / 2) - 1] +
               longitudes[longitudes.Length / 2]) / 2d;

        return CoordinateService.SuggestGk3CentralMeridian(medianLongitude);
    }

    private static string SampleKey(string sourceFile, int sourceRow) =>
        $"{sourceFile}\u001f{sourceRow.ToString(CultureInfo.InvariantCulture)}";

    private static string UtcDate(DateTimeOffset value) =>
        value.ToUniversalTime().ToString(
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture);

    private static string UtcTime(DateTimeOffset value) =>
        value.ToUniversalTime().ToString(
            "HH:mm:ss.fff",
            CultureInfo.InvariantCulture);

    private static string Number(double? value, string format) =>
        value.HasValue
            ? Number(value.Value, format)
            : "";

    private static string Number(double value, string format) =>
        value.ToString(format, CultureInfo.InvariantCulture);


    private static string TsvText(string value) =>
        (value ?? "")
            .Replace('\t', ' ')
            .Replace('\r', ' ')
            .Replace('\n', ' ');

    private static string Csv(string value)
    {
        string text = value ?? "";
        return text.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? "\"" + text.Replace("\"", "\"\"") + "\""
            : text;
    }

    private sealed class FirstPassDayStats
    {
        public int ProcessingRecords { get; set; }
        public int IgrfRecords { get; set; }
        public double RawDtSum { get; set; }
    }

    private sealed class OutputDayStats
    {
        public int DTgzCount { get; set; }
        public double DTgzSum { get; set; }
        public double? DTgzMin { get; set; }
        public double? DTgzMax { get; set; }
    }
}
