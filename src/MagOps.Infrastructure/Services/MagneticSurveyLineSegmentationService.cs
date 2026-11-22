using System.Globalization;
using System.Text;

namespace MagOps.Infrastructure.Services;

public enum MagneticSegmentClass
{
    SurveyLine,
    TransitToFirstSurveyLine,
    ConnectorBetweenSurveyLines,
    ReturnAfterLastSurveyLine,
    UnmatchedNoSurveyLine
}

public sealed record MagneticSurveyLineSegmentationOptions(
    bool Enabled = true,
    double HeadingToleranceDegrees = 25d,
    double MatchToleranceCapM = 12d,
    double MatchToleranceSpacingRatio = 0.35d,
    double MatchToleranceFloorM = 3d,
    double EndpointMarginM = 8d,
    double MaxHeadingTimeGapSeconds = 8d,
    double SessionBreakSeconds = 300d,
    double SmallGapBridgeSeconds = 1.5d,
    int MinimumSurveyRunPoints = 5,
    double MinimumSurveyRunAlongSpanM = 15d,
    double MinimumSurveyRunDurationSeconds = 2d,
    double MinimumAlongMonotonicity = 0.82d,
    double MinimumLineCoverageRatio = 0.20d,
    double MaximumRequiredCoverageM = 60d);

public readonly record struct MagneticSegmentAssignment(
    string SourceFile,
    int SourceRecord,
    DateTimeOffset TimestampUtc,
    double Longitude,
    double Latitude,
    MagneticSegmentClass SegmentClass,
    bool IncludeInProcessing,
    int SessionIndex,
    string LineTaskId,
    string SurveyTaskStableId,
    string LineName,
    int? LineOrder,
    int? LinePass,
    double? AlongM,
    double? CrossTrackM,
    double? MatchToleranceM,
    double? HeadingDifferenceDeg)
{
    // Trace the design relationship without replacing the recognized child task.
    // MotherLineTaskId is a legacy name key and is not a cross-project merge key.
    public string PartitionCode { get; init; } = "";
    public string MotherLineTaskId { get; init; } = "";
    public string MotherLineStableId { get; init; } = "";
    public string SurveyLineStableId { get; init; } = "";
    public string ExecutionSegmentId { get; init; } = "";
    public string ExecutionSegmentStableId { get; init; } = "";
    public bool IsQualityRepeat { get; init; }
}

public sealed record MagneticLineSegmentationLineSummary(
    int LineOrder,
    string TaskId,
    string SurveyTaskStableId,
    string LineName,
    int RetainedRecords,
    int Passes,
    DateTimeOffset? FirstTimeUtc,
    DateTimeOffset? LastTimeUtc);

public sealed record MagneticSurveyLineSegmentationSummary(
    bool Enabled,
    bool RouteAvailable,
    int PlannedLineCount,
    int CoordinateRecords,
    int Sessions,
    int SurveyLineRecords,
    int TransitToFirstRecords,
    int ConnectorRecords,
    int ReturnRecords,
    int UnmatchedRecords,
    int MatchedLineCount,
    IReadOnlyList<MagneticLineSegmentationLineSummary> Lines)
{
    public int ExcludedRecords =>
        TransitToFirstRecords + ConnectorRecords + ReturnRecords + UnmatchedRecords;
}

public sealed class MagneticSurveyLineSegmentationResult
{
    private readonly Dictionary<(string SourceFile, int SourceRecord), int> _retainedAssignmentIndices;

    internal MagneticSurveyLineSegmentationResult(
        IReadOnlyList<MagneticSegmentAssignment> assignments,
        MagneticSurveyLineSegmentationSummary summary,
        MagneticSurveyLineSegmentationOptions options)
    {
        Assignments = assignments;
        Summary = summary;
        Options = options;
        _retainedAssignmentIndices = new Dictionary<(string SourceFile, int SourceRecord), int>();
        for (int i = 0; i < assignments.Count; i++)
        {
            MagneticSegmentAssignment assignment = assignments[i];
            if (assignment.IncludeInProcessing)
                _retainedAssignmentIndices[(assignment.SourceFile, assignment.SourceRecord)] = i;
        }
    }

    public IReadOnlyList<MagneticSegmentAssignment> Assignments { get; }
    public MagneticSurveyLineSegmentationSummary Summary { get; }
    public MagneticSurveyLineSegmentationOptions Options { get; }

    public bool TryGet(
        string sourceFile,
        int sourceRecord,
        out MagneticSegmentAssignment assignment)
    {
        if (_retainedAssignmentIndices.TryGetValue((sourceFile, sourceRecord), out int index))
        {
            assignment = Assignments[index];
            return true;
        }

        assignment = default;
        return false;
    }
}

/// <summary>Audit-result DTO/CSV adapter; no point-wise line matching algorithm remains.</summary>
public static class MagneticSurveyLineSegmentationService
{
    public const string AlgorithmVersion = WholeLegMatcherService.AlgorithmVersion;
    public const string ImplementationVersion = "audit-point-selection-adapter-3-lineage";

    public static MagneticSurveyLineSegmentationResult FromAudit(
        IReadOnlyList<UavMagCanonicalSample> samples, IReadOnlyList<PlannedSurveyLineTask> tasks,
        AuditProductionResult audit)
    {
        var selected = audit.SelectedSamples.Select(p => (p.SourceFile, p.SourceRecord)).ToHashSet();
        var matches = audit.Execution.Passes.SelectMany((pass, index) => pass.Samples.Select(p =>
            (Sample: p, Pass: pass, Ordinal: index + 1)))
            .GroupBy(x => (x.Sample.SourceFile, x.Sample.SourceRecord))
            .ToDictionary(g => g.Key, g => g.Single());
        var byTask = tasks.Where(t => !string.IsNullOrWhiteSpace(t.SurveyTaskStableId))
            .GroupBy(t => t.SurveyTaskStableId).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single());
        // Explicitly selected legs without a survey identity still need separate
        // Oasis groups. These export labels do not create tasks, passes or coverage.
        var passLegIds = audit.Execution.Passes.Select(p => p.FlightLegId).ToHashSet();
        var includedLegIds = audit.ManualIncludedFlightLegIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var actualRepeats = audit.ActualRepeatLegIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        int orderOffset = tasks.Select(t => t.Order).DefaultIfEmpty(0).Max();
        var usedNames = tasks.Select(t => t.LineName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unassigned = new Dictionary<(string SourceFile, int SourceRecord), (string LegId, string Name, int Order, int Pass)>();
        foreach (var (leg, index) in audit.Execution.Legs.Select((leg, index) => (leg, index)))
        {
            if (passLegIds.Contains(leg.FlightLegId) || !includedLegIds.Contains(leg.FlightLegId)) continue;
            int ordinal = audit.Execution.Passes.Count + index + 1;
            string name = "u" + ordinal.ToString(CultureInfo.InvariantCulture);
            while (!usedNames.Add(name)) name = "u" + name;
            foreach (var sample in leg.Samples)
                if (selected.Contains((sample.SourceFile, sample.SourceRecord)))
                    unassigned.Add((sample.SourceFile, sample.SourceRecord),
                        (leg.FlightLegId, name, orderOffset + index + 1, ordinal));
        }
        var rows = new List<MagneticSegmentAssignment>();
        foreach (var sample in samples.Where(p => p.Longitude.HasValue && p.Latitude.HasValue))
        {
            matches.TryGetValue((sample.SourceFile, sample.SourceRecord), out var match);
            PlannedSurveyLineTask? task = match.Pass is null ? null : byTask.GetValueOrDefault(match.Pass.SurveyTaskStableId);
            // A review draft preserves every input row in the audit export, but only
            // explicit production selection is eligible for pre-level processing.
            bool included = selected.Contains((sample.SourceFile, sample.SourceRecord));
            bool hasUnassignedLeg = unassigned.TryGetValue((sample.SourceFile, sample.SourceRecord), out var exportLeg);
            bool manualUnassigned = included && task is null && hasUnassignedLeg;
            rows.Add(new(sample.SourceFile, sample.SourceRecord, sample.TimestampUtc,
                sample.Longitude!.Value, sample.Latitude!.Value,
                task is null ? MagneticSegmentClass.UnmatchedNoSurveyLine : MagneticSegmentClass.SurveyLine,
                included, 0,
                task?.SurveyTaskStableId ?? (manualUnassigned ? "unassigned:" + exportLeg.LegId : ""),
                task?.SurveyTaskStableId ?? "",
                task?.LineName ?? (manualUnassigned ? exportLeg.Name : ""), task?.Order ?? (manualUnassigned ? exportLeg.Order : null),
                task is null ? manualUnassigned ? exportLeg.Pass : null : match.Ordinal, task is null ? null : match.Sample.AlongFraction * task.LengthM,
                match.Pass?.MedianCrossTrackM, null, match.Pass?.HeadingErrorDeg)
            {
                PartitionCode = task?.PartitionCode ?? "",
                MotherLineTaskId = task?.MotherLineTaskId ?? "",
                MotherLineStableId = task?.MotherLineStableId ?? "",
                SurveyLineStableId = task?.SurveyLineStableId ?? "",
                ExecutionSegmentId = task?.ExecutionSegmentId ?? "",
                ExecutionSegmentStableId = task?.ExecutionSegmentStableId ?? "",
                IsQualityRepeat = task?.IsQualityRepeat == true ||
                    match.Pass is { } matched && actualRepeats.Contains(matched.FlightLegId) ||
                    manualUnassigned && actualRepeats.Contains(exportLeg.LegId)
            });
        }
        rows = SplitProductionGaps(rows);
        var lines = rows.Where(r => r.LineOrder.HasValue).GroupBy(r => r.LineTaskId).Select(g =>
            new MagneticLineSegmentationLineSummary(g.First().LineOrder!.Value, g.Key,
                g.First().SurveyTaskStableId, g.First().LineName,
                g.Count(r => r.IncludeInProcessing), g.Select(r => r.LinePass).Distinct().Count(),
                g.Min(r => r.TimestampUtc), g.Max(r => r.TimestampUtc))).ToArray();
        return new(rows, new(true, tasks.Count > 0, tasks.Count, rows.Count, audit.Execution.Missions.Count,
            rows.Count(r => r.SegmentClass == MagneticSegmentClass.SurveyLine), 0, 0, 0,
            rows.Count(r => r.SegmentClass == MagneticSegmentClass.UnmatchedNoSurveyLine),
            lines.Count(l => !string.IsNullOrWhiteSpace(l.SurveyTaskStableId)), lines),
            new MagneticSurveyLineSegmentationOptions(Enabled: true));
    }

    /// <summary>Give each retained continuous run its own LinePass so Oasis cannot
    /// connect points on opposite sides of a confirmed production gap.</summary>
    private static List<MagneticSegmentAssignment> SplitProductionGaps(List<MagneticSegmentAssignment> rows)
    {
        var replacements = new Dictionary<(string Source, int Record), int>();
        foreach (var taskGroup in rows.Where(r => r.IncludeInProcessing && r.LinePass.HasValue)
                     .GroupBy(r => r.LineTaskId, StringComparer.OrdinalIgnoreCase))
        {
            int nextPass = taskGroup.Max(r => r.LinePass!.Value) + 1;
            foreach (var passGroup in taskGroup.GroupBy(r => r.LinePass!.Value).OrderBy(g => g.Key))
            {
                var run = new List<MagneticSegmentAssignment>();
                int runPass = passGroup.Key;
                void Commit()
                {
                    if (run.Count == 0) return;
                    foreach (var item in run) replacements[(item.SourceFile, item.SourceRecord)] = runPass;
                    run.Clear();
                }
                MagneticSegmentAssignment? previous = null;
                foreach (var row in passGroup.OrderBy(r => r.TimestampUtc).ThenBy(r => r.SourceFile).ThenBy(r => r.SourceRecord))
                {
                    bool continuous = previous is { } p &&
                        p.SourceFile.Equals(row.SourceFile, StringComparison.OrdinalIgnoreCase) &&
                        row.SourceRecord == p.SourceRecord + 1 &&
                        (row.TimestampUtc - p.TimestampUtc).TotalSeconds <= 8;
                    if (previous is not null && !continuous) { Commit(); runPass = nextPass++; }
                    run.Add(row); previous = row;
                }
                Commit();
            }
        }
        return rows.Select(row => replacements.TryGetValue((row.SourceFile, row.SourceRecord), out int pass)
            ? row with { LinePass = pass } : row).ToList();
    }

    internal static void WriteAuditGzip(
        string path,
        MagneticSurveyLineSegmentationResult result,
        MagDataSourceCatalog sourceCatalog)
    {
        MagDataCompactExport.WriteGzipCsv(
            path,
            writer =>
            {
                writer.WriteLine(
                    "DateUtc,TimeUtc,SourceId,SourceRecord,SegmentClass,IncludeInProcessing,SessionIndex,LineTaskId,SurveyTaskStableId,LineName,LineOrder,LinePass,Longitude,Latitude,AlongM,CrossTrackM,MatchToleranceM,HeadingDifferenceDeg,Evidence,PartitionCode,MotherLineTaskId,MotherLineStableId,SurveyLineStableId,ExecutionSegmentId,ExecutionSegmentStableId,IsQualityRepeat");
                foreach (MagneticSegmentAssignment row in result.Assignments
                             .OrderBy(x => x.TimestampUtc)
                             .ThenBy(x => x.SourceFile, StringComparer.OrdinalIgnoreCase)
                             .ThenBy(x => x.SourceRecord))
                {
                    writer.WriteLine(string.Join(",",
                        Csv(row.TimestampUtc.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                        Csv(row.TimestampUtc.ToUniversalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)),
                        sourceCatalog.GetSourceId(row.SourceFile).ToString(CultureInfo.InvariantCulture),
                        row.SourceRecord.ToString(CultureInfo.InvariantCulture),
                        SegmentClassCode(row.SegmentClass),
                        row.IncludeInProcessing ? "YES" : "NO",
                        row.SessionIndex.ToString(CultureInfo.InvariantCulture),
                        Csv(row.LineTaskId),
                        Csv(row.SurveyTaskStableId),
                        Csv(row.LineName),
                        row.LineOrder?.ToString(CultureInfo.InvariantCulture) ?? "",
                        row.LinePass?.ToString(CultureInfo.InvariantCulture) ?? "",
                        Number(row.Longitude, "0.########"),
                        Number(row.Latitude, "0.########"),
                        Number(row.AlongM, "0.###"),
                        Number(row.CrossTrackM, "0.###"),
                        Number(row.MatchToleranceM, "0.###"),
                        Number(row.HeadingDifferenceDeg, "0.###"),
                        row.IncludeInProcessing && row.LineTaskId.StartsWith("unassigned:", StringComparison.Ordinal)
                            ? "USER_INCLUDED_UNASSIGNED_LEG" : EvidenceCode(row.SegmentClass),
                        Csv(row.PartitionCode), Csv(row.MotherLineTaskId), Csv(row.MotherLineStableId),
                        Csv(row.SurveyLineStableId), Csv(row.ExecutionSegmentId), Csv(row.ExecutionSegmentStableId),
                        row.IsQualityRepeat ? "YES" : "NO"));
                }
            });
    }

    public static void WriteSummaryCsv(
        string path,
        MagneticSurveyLineSegmentationResult result)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        writer.WriteLine("RowType,Key,Name,Records,Percent,Passes,FirstUtc,LastUtc");

        MagneticSurveyLineSegmentationSummary summary = result.Summary;
        WriteClassSummary(writer, "SURVEY_LINE", "正式测线", summary.SurveyLineRecords, summary.CoordinateRecords);
        WriteClassSummary(writer, "TRANSIT_TO_FIRST_LINE", "起飞点至首条测线", summary.TransitToFirstRecords, summary.CoordinateRecords);
        WriteClassSummary(writer, "CONNECTOR_BETWEEN_LINES", "测线间连接/转弯", summary.ConnectorRecords, summary.CoordinateRecords);
        WriteClassSummary(writer, "RETURN_AFTER_LAST_LINE", "末条测线后返航", summary.ReturnRecords, summary.CoordinateRecords);
        WriteClassSummary(writer, "UNMATCHED_NO_SURVEY_LINE", "未匹配正式测线", summary.UnmatchedRecords, summary.CoordinateRecords);

        foreach (MagneticLineSegmentationLineSummary line in summary.Lines)
        {
            writer.WriteLine(string.Join(",",
                "LINE",
                Csv(line.TaskId),
                Csv(line.LineName),
                line.RetainedRecords.ToString(CultureInfo.InvariantCulture),
                summary.SurveyLineRecords > 0
                    ? (line.RetainedRecords * 100d / summary.SurveyLineRecords).ToString("0.###", CultureInfo.InvariantCulture)
                    : "0",
                line.Passes.ToString(CultureInfo.InvariantCulture),
                line.FirstTimeUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? "",
                line.LastTimeUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? ""));
        }
    }

    private static string SegmentClassCode(MagneticSegmentClass segmentClass) =>
        segmentClass switch
        {
            MagneticSegmentClass.SurveyLine => "SURVEY_LINE",
            MagneticSegmentClass.TransitToFirstSurveyLine => "TRANSIT_TO_FIRST_LINE",
            MagneticSegmentClass.ConnectorBetweenSurveyLines => "CONNECTOR_BETWEEN_LINES",
            MagneticSegmentClass.ReturnAfterLastSurveyLine => "RETURN_AFTER_LAST_LINE",
            _ => "UNMATCHED_NO_SURVEY_LINE"
        };

    private static string EvidenceCode(MagneticSegmentClass segmentClass) =>
        segmentClass switch
        {
            MagneticSegmentClass.SurveyLine => "TIME_SPACE_CONFIRMED_PLANNED_LINE_LEG",
            MagneticSegmentClass.TransitToFirstSurveyLine => "BEFORE_FIRST_RETAINED_SURVEY_RUN",
            MagneticSegmentClass.ConnectorBetweenSurveyLines => "BETWEEN_RETAINED_SURVEY_RUNS",
            MagneticSegmentClass.ReturnAfterLastSurveyLine => "AFTER_LAST_RETAINED_SURVEY_RUN",
            _ => "NO_TIME_SPACE_CONFIRMED_SURVEY_LEG"
        };

    private static void WriteClassSummary(
        TextWriter writer,
        string key,
        string name,
        int count,
        int total)
    {
        writer.WriteLine(string.Join(",",
            "CLASS",
            key,
            Csv(name),
            count.ToString(CultureInfo.InvariantCulture),
            total > 0
                ? (count * 100d / total).ToString("0.###", CultureInfo.InvariantCulture)
                : "0",
            "",
            "",
            ""));
    }

    private static string Number(double? value, string format) =>
        value.HasValue
            ? value.Value.ToString(format, CultureInfo.InvariantCulture)
            : "";

    private static string Csv(string value)
    {
        string text = value ?? "";
        return text.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? "\"" + text.Replace("\"", "\"\"") + "\""
            : text;
    }

}
