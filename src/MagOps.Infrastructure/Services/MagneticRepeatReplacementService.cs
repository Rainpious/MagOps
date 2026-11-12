using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using MagOps.Core.Diurnal;
using MagOps.Infrastructure.Storage;

namespace MagOps.Infrastructure.Services;

public static class MagneticRepeatReplacementModes
{
    public const string WholeLine = "WHOLE_LINE";
    public const string Segment = "SEGMENT";
}

public sealed class MagneticRepeatReplacementRule
{
    [JsonPropertyName("rule_id")]
    public string RuleId { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("workarea_path")]
    public string WorkareaPath { get; set; } = "";

    [JsonPropertyName("line_task_id")]
    public string LineTaskId { get; set; } = "";

    [JsonPropertyName("line_name")]
    public string LineName { get; set; } = "";

    [JsonPropertyName("range_mode")]
    public string RangeMode { get; set; } = MagneticRepeatReplacementModes.Segment;

    [JsonPropertyName("start_along_m")]
    public double? StartAlongM { get; set; }

    [JsonPropertyName("end_along_m")]
    public double? EndAlongM { get; set; }

    [JsonPropertyName("original_source_key")]
    public string OriginalSourceKey { get; set; } = "";

    [JsonPropertyName("original_line_pass")]
    public int OriginalLinePass { get; set; }

    [JsonPropertyName("original_first_utc")]
    public DateTimeOffset OriginalFirstUtc { get; set; }

    [JsonPropertyName("original_last_utc")]
    public DateTimeOffset OriginalLastUtc { get; set; }

    [JsonPropertyName("replacement_source_key")]
    public string ReplacementSourceKey { get; set; } = "";

    [JsonPropertyName("replacement_line_pass")]
    public int ReplacementLinePass { get; set; }

    [JsonPropertyName("replacement_first_utc")]
    public DateTimeOffset ReplacementFirstUtc { get; set; }

    [JsonPropertyName("replacement_last_utc")]
    public DateTimeOffset ReplacementLastUtc { get; set; }

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = "";

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("created_at_utc")]
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("source_fingerprint")]
    public string SourceFingerprint { get; set; } = "";

    [JsonPropertyName("survey_line_source")]
    public string SurveyLineSource { get; set; } = "";

    [JsonPropertyName("survey_line_fingerprint")]
    public string SurveyLineFingerprint { get; set; } = "";
}

public sealed record MagneticSurveyPassCandidate(
    string LineTaskId,
    string LineName,
    int? LineOrder,
    int LinePass,
    string SourceFile,
    string SourceKey,
    DateTimeOffset FirstTimeUtc,
    DateTimeOffset LastTimeUtc,
    double MinAlongM,
    double MaxAlongM,
    int SegmentedRecords,
    int ProductionEligibleRecords,
    double? MaxCrossTrackM)
{
    public string DisplayLabel =>
        $"{LineName} / Pass {LinePass} / {FirstTimeUtc.ToLocalTime():MM-dd HH:mm:ss}–{LastTimeUtc.ToLocalTime():HH:mm:ss} / {Path.GetFileName(SourceKey)}";

    public override string ToString() => DisplayLabel;
}

public sealed record MagneticRepeatReplacementRuleSummary(
    string RuleId,
    string LineTaskId,
    string LineName,
    double StartAlongM,
    double EndAlongM,
    int OriginalLinePass,
    int ReplacementLinePass,
    int OriginalRecordsInRange,
    int ReplacementRecordsInRange,
    double? ReplacementMinAlongM,
    double? ReplacementMaxAlongM,
    double? ReplacementMaxGapM,
    double AllowedEndpointGapM,
    double AllowedInternalGapM,
    double? ReplacementMaxCrossTrackM,
    bool CoverageValid,
    string Status,
    string Reason);

public readonly record struct MagneticRepeatReplacementDecision(
    string SourceFile,
    int SourceRecord,
    bool IncludeInProcessing,
    string Action,
    string RuleIds);

public sealed class MagneticRepeatReplacementResult
{
    private readonly Dictionary<(string SourceFile, int SourceRecord), MagneticRepeatReplacementDecision> _decisions;

    internal MagneticRepeatReplacementResult(
        IReadOnlyList<MagneticRepeatReplacementRuleSummary> summaries,
        IReadOnlyList<MagneticRepeatReplacementDecision> decisions)
    {
        Summaries = summaries;
        Decisions = decisions;
        _decisions = decisions.ToDictionary(
            x => (x.SourceFile, x.SourceRecord),
            x => x);
    }

    public IReadOnlyList<MagneticRepeatReplacementRuleSummary> Summaries { get; }
    public IReadOnlyList<MagneticRepeatReplacementDecision> Decisions { get; }
    public static MagneticRepeatReplacementResult Empty() => new([], []);
    public int ActiveRuleCount => Summaries.Count;
    public int ExcludedOriginalRecords =>
        Decisions.Count(x => x.Action.Equals("ORIGINAL_REPLACED", StringComparison.Ordinal));
    public int SelectedReplacementRecords =>
        Decisions.Count(x => x.Action.Equals("REPLACEMENT_SELECTED", StringComparison.Ordinal));
    public int SuppressedReplacementRecords =>
        Decisions.Count(x => x.Action.Equals("REPLACEMENT_NOT_SELECTED", StringComparison.Ordinal));

    public bool ShouldInclude(MagneticSegmentAssignment assignment)
    {
        if (_decisions.TryGetValue((assignment.SourceFile, assignment.SourceRecord), out MagneticRepeatReplacementDecision decision))
            return decision.IncludeInProcessing;
        return assignment.IncludeInProcessing;
    }

    public bool TryGetDecision(
        string sourceFile,
        int sourceRecord,
        out MagneticRepeatReplacementDecision decision) =>
        _decisions.TryGetValue((sourceFile, sourceRecord), out decision);
}

/// <summary>
/// Applies explicit production-data selection for repeated survey measurements.
/// Raw/canonical data are never deleted. Within a selected chainage interval the
/// original pass is excluded and the chosen later pass is retained. The chosen
/// replacement pass is suppressed outside all of its selected intervals so a
/// repeat flight cannot silently duplicate the original measurement in Oasis.
/// </summary>
public sealed class MagneticRepeatReplacementService
{
    public const string AlgorithmVersion = "magnetic-repeat-replacement-1";
    public const string RulesFileName = "magnetic_repeat_replacements.json";

    private readonly JsonStore _store;

    public MagneticRepeatReplacementService(JsonStore store) => _store = store;

    public List<MagneticRepeatReplacementRule> GetRules(
        string projectRoot,
        string workareaPath)
    {
        string normalized = NormalizeWorkareaPath(workareaPath);
        return _store
            .Read(ProjectPaths.Json(projectRoot, RulesFileName), new List<MagneticRepeatReplacementRule>())
            .Select(CloneAndNormalize)
            .Where(x => NormalizeWorkareaPath(x.WorkareaPath)
                .Equals(normalized, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.LineName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.StartAlongM ?? double.MinValue)
            .ThenBy(x => x.CreatedAtUtc)
            .ToList();
    }

    public void ReplaceWorkareaRules(
        string projectRoot,
        string workareaPath,
        IReadOnlyList<MagneticRepeatReplacementRule> rules)
    {
        string normalized = NormalizeWorkareaPath(workareaPath);
        if (string.IsNullOrWhiteSpace(normalized))
            throw new InvalidOperationException("重测替换规则必须归属于明确工区。 ");

        List<MagneticRepeatReplacementRule> all = _store.Read(
            ProjectPaths.Json(projectRoot, RulesFileName),
            new List<MagneticRepeatReplacementRule>());
        all.RemoveAll(x => NormalizeWorkareaPath(x.WorkareaPath)
            .Equals(normalized, StringComparison.OrdinalIgnoreCase));

        foreach (MagneticRepeatReplacementRule rule in rules)
        {
            rule.WorkareaPath = normalized;
            NormalizeRule(rule);
            all.Add(rule);
        }

        _store.Write(ProjectPaths.Json(projectRoot, RulesFileName), all);
        _store.AppendJsonLine(
            ProjectPaths.DataDirectory(projectRoot),
            "magnetic_repeat_replacement_rules_saved",
            new
            {
                workarea_path = normalized,
                rule_count = rules.Count,
                enabled_count = rules.Count(x => x.Enabled)
            });
    }

    public static MagneticRepeatReplacementRule CreateRule(
        string workareaPath,
        MagneticSurveyPassCandidate original,
        MagneticSurveyPassCandidate replacement,
        string rangeMode,
        double? startAlongM,
        double? endAlongM,
        string reason,
        string sourceFingerprint,
        string surveyLineSource,
        string surveyLineFingerprint)
    {
        if (!original.LineTaskId.Equals(replacement.LineTaskId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("原测与重测必须属于同一条正式测线。 ");

        var rule = new MagneticRepeatReplacementRule
        {
            RuleId = Guid.NewGuid().ToString("N"),
            WorkareaPath = NormalizeWorkareaPath(workareaPath),
            LineTaskId = original.LineTaskId,
            LineName = string.IsNullOrWhiteSpace(original.LineName) ? replacement.LineName : original.LineName,
            RangeMode = NormalizeRangeMode(rangeMode),
            StartAlongM = startAlongM,
            EndAlongM = endAlongM,
            OriginalSourceKey = original.SourceKey,
            OriginalLinePass = original.LinePass,
            OriginalFirstUtc = original.FirstTimeUtc.ToUniversalTime(),
            OriginalLastUtc = original.LastTimeUtc.ToUniversalTime(),
            ReplacementSourceKey = replacement.SourceKey,
            ReplacementLinePass = replacement.LinePass,
            ReplacementFirstUtc = replacement.FirstTimeUtc.ToUniversalTime(),
            ReplacementLastUtc = replacement.LastTimeUtc.ToUniversalTime(),
            Reason = (reason ?? "").Trim(),
            Enabled = true,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            SourceFingerprint = sourceFingerprint ?? "",
            SurveyLineSource = surveyLineSource ?? "",
            SurveyLineFingerprint = surveyLineFingerprint ?? ""
        };
        NormalizeRule(rule);
        return rule;
    }

    public static IReadOnlyList<MagneticSurveyPassCandidate> BuildPassCatalog(
        string sourceDirectory,
        MagneticSurveyLineSegmentationResult segmentation,
        DiurnalCorrectionResult correction)
    {
        ArgumentNullException.ThrowIfNull(segmentation);
        ArgumentNullException.ThrowIfNull(correction);

        Dictionary<string, DiurnalCorrectedSample> corrected = correction.Samples.ToDictionary(
            x => SampleKey(x.RoverSourceFile, x.RoverSourceRow),
            StringComparer.OrdinalIgnoreCase);

        return segmentation.Assignments
            .Where(x =>
                x.IncludeInProcessing &&
                x.LinePass.HasValue &&
                x.AlongM.HasValue &&
                !string.IsNullOrWhiteSpace(x.LineTaskId))
            .GroupBy(x => new
            {
                x.LineTaskId,
                x.LineName,
                x.LineOrder,
                LinePass = x.LinePass!.Value,
                x.SourceFile,
                SourceKey = SourceKey(sourceDirectory, x.SourceFile)
            })
            .Select(g =>
            {
                MagneticSegmentAssignment[] rows = g.OrderBy(x => x.TimestampUtc).ToArray();
                double[] along = rows.Select(x => x.AlongM!.Value).ToArray();
                int eligible = rows.Count(x =>
                    corrected.TryGetValue(SampleKey(x.SourceFile, x.SourceRecord), out DiurnalCorrectedSample? row) &&
                    row.DiurnalDifferenceNt.HasValue);
                double[] crossTrack = rows
                    .Where(x => x.CrossTrackM.HasValue)
                    .Select(x => Math.Abs(x.CrossTrackM!.Value))
                    .ToArray();
                double? maxCross = crossTrack.Length == 0 ? null : crossTrack.Max();

                return new MagneticSurveyPassCandidate(
                    g.Key.LineTaskId,
                    g.Key.LineName,
                    g.Key.LineOrder,
                    g.Key.LinePass,
                    g.Key.SourceFile,
                    g.Key.SourceKey,
                    rows[0].TimestampUtc,
                    rows[^1].TimestampUtc,
                    along.Min(),
                    along.Max(),
                    rows.Length,
                    eligible,
                    maxCross);
            })
            .OrderBy(x => x.LineOrder ?? int.MaxValue)
            .ThenBy(x => x.LineName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.FirstTimeUtc)
            .ThenBy(x => x.LinePass)
            .ToArray();
    }

    public static MagneticRepeatReplacementRuleSummary PreviewRule(
        string sourceDirectory,
        MagneticSurveyLineSegmentationResult segmentation,
        DiurnalCorrectionResult correction,
        MagneticRepeatReplacementRule rule,
        string currentSourceFingerprint = "",
        string currentSurveyLineFingerprint = "")
    {
        MagneticRepeatReplacementResult result = Evaluate(
            sourceDirectory,
            segmentation,
            correction,
            [rule],
            currentSourceFingerprint,
            currentSurveyLineFingerprint,
            throwOnInvalid: false);
        return result.Summaries.Single();
    }

    public static MagneticRepeatReplacementResult Evaluate(
        string sourceDirectory,
        MagneticSurveyLineSegmentationResult segmentation,
        DiurnalCorrectionResult correction,
        IReadOnlyList<MagneticRepeatReplacementRule>? rules,
        string currentSourceFingerprint = "",
        string currentSurveyLineFingerprint = "",
        bool throwOnInvalid = true)
    {
        ArgumentNullException.ThrowIfNull(segmentation);
        ArgumentNullException.ThrowIfNull(correction);
        rules ??= [];

        List<MagneticRepeatReplacementRule> active = rules
            .Where(x => x.Enabled)
            .Select(CloneAndNormalize)
            .ToList();
        if (active.Count == 0)
            return new MagneticRepeatReplacementResult([], []);

        if (!segmentation.Options.Enabled)
            throw new InvalidOperationException("已配置重测段替换，但正式测线分线未启用。重测替换依赖 LineTaskId / LinePass / AlongM，不能在未分线数据上执行。 ");

        IReadOnlyList<MagneticSurveyPassCandidate> catalog = BuildPassCatalog(
            sourceDirectory,
            segmentation,
            correction);
        Dictionary<string, DiurnalCorrectedSample> corrected = correction.Samples.ToDictionary(
            x => SampleKey(x.RoverSourceFile, x.RoverSourceRow),
            StringComparer.OrdinalIgnoreCase);

        var resolved = new List<ResolvedRule>();
        var summaries = new List<MagneticRepeatReplacementRuleSummary>();
        foreach (MagneticRepeatReplacementRule rule in active)
        {
            if (!string.IsNullOrWhiteSpace(currentSourceFingerprint) &&
                !string.IsNullOrWhiteSpace(rule.SourceFingerprint) &&
                !rule.SourceFingerprint.Equals(currentSourceFingerprint, StringComparison.OrdinalIgnoreCase))
            {
                summaries.Add(InvalidSummary(rule, "磁测输入数据已变化；请重新打开该规则并确认原测/重测来源与区段"));
                continue;
            }

            if (!string.IsNullOrWhiteSpace(currentSurveyLineFingerprint) &&
                !string.IsNullOrWhiteSpace(rule.SurveyLineFingerprint) &&
                !rule.SurveyLineFingerprint.Equals(currentSurveyLineFingerprint, StringComparison.OrdinalIgnoreCase))
            {
                summaries.Add(InvalidSummary(rule, "设计测线几何已变化；旧 AlongM 规则必须重新确认"));
                continue;
            }

            MagneticSurveyPassCandidate? original = ResolvePass(catalog, rule, replacement: false);
            MagneticSurveyPassCandidate? replacement = ResolvePass(catalog, rule, replacement: true);
            if (original is null || replacement is null)
            {
                string missing = original is null && replacement is null
                    ? "原测和重测 pass 均无法在当前数据中唯一恢复"
                    : original is null
                        ? "原测 pass 无法在当前数据中唯一恢复"
                        : "重测 pass 无法在当前数据中唯一恢复";
                summaries.Add(InvalidSummary(rule, missing));
                continue;
            }

            ResolvedRule evaluated = EvaluateResolvedRule(
                sourceDirectory,
                segmentation,
                corrected,
                rule,
                original,
                replacement);
            resolved.Add(evaluated);
            summaries.Add(evaluated.Summary);
        }

        string? conflict = FindConflict(resolved);
        if (!string.IsNullOrWhiteSpace(conflict))
        {
            if (throwOnInvalid)
                throw new InvalidOperationException("重测替换规则冲突：" + conflict);
            summaries = summaries
                .Select(x => x with
                {
                    CoverageValid = false,
                    Status = x.Status + "；规则冲突：" + conflict
                })
                .ToList();
        }

        MagneticRepeatReplacementRuleSummary[] invalid = summaries
            .Where(x => !x.CoverageValid)
            .ToArray();
        if (throwOnInvalid && invalid.Length > 0)
        {
            string detail = string.Join(
                Environment.NewLine,
                invalid.Select(x => $"{x.LineName} / {x.RuleId[..Math.Min(8, x.RuleId.Length)]}: {x.Status}"));
            throw new InvalidOperationException(
                "存在未通过覆盖完整性检查的重测替换规则，已阻止生成正式 Oasis CSV：" +
                Environment.NewLine + detail);
        }

        if (!string.IsNullOrWhiteSpace(conflict))
            return new MagneticRepeatReplacementResult(summaries, []);

        ResolvedRule[] valid = resolved.Where(x => x.Summary.CoverageValid).ToArray();
        var decisions = new Dictionary<(string SourceFile, int SourceRecord), MagneticRepeatReplacementDecision>();

        // A chosen replacement pass is opt-in: suppress it everywhere first, then
        // re-enable only the chainage intervals explicitly selected by rules.
        foreach (IGrouping<PassIdentity, ResolvedRule> group in valid.GroupBy(x => x.ReplacementIdentity))
        {
            ResolvedRule[] groupRules = group.ToArray();
            HashSet<string> ruleIds = groupRules.Select(x => x.Rule.RuleId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (MagneticSegmentAssignment assignment in segmentation.Assignments.Where(x => MatchesPass(sourceDirectory, x, group.Key)))
            {
                if (!assignment.IncludeInProcessing)
                    continue;

                bool selected = assignment.AlongM.HasValue && groupRules.Any(x =>
                    InRange(assignment.AlongM.Value, x.StartAlongM, x.EndAlongM));
                string action = selected ? "REPLACEMENT_SELECTED" : "REPLACEMENT_NOT_SELECTED";
                decisions[(assignment.SourceFile, assignment.SourceRecord)] = new MagneticRepeatReplacementDecision(
                    assignment.SourceFile,
                    assignment.SourceRecord,
                    selected,
                    action,
                    string.Join(";", ruleIds.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)));
            }
        }

        foreach (IGrouping<PassIdentity, ResolvedRule> group in valid.GroupBy(x => x.OriginalIdentity))
        {
            ResolvedRule[] groupRules = group.ToArray();
            foreach (MagneticSegmentAssignment assignment in segmentation.Assignments.Where(x => MatchesPass(sourceDirectory, x, group.Key)))
            {
                if (!assignment.IncludeInProcessing || !assignment.AlongM.HasValue)
                    continue;
                ResolvedRule[] matching = groupRules
                    .Where(x => InRange(assignment.AlongM.Value, x.StartAlongM, x.EndAlongM))
                    .ToArray();
                if (matching.Length == 0)
                    continue;

                decisions[(assignment.SourceFile, assignment.SourceRecord)] = new MagneticRepeatReplacementDecision(
                    assignment.SourceFile,
                    assignment.SourceRecord,
                    false,
                    "ORIGINAL_REPLACED",
                    string.Join(";", matching.Select(x => x.Rule.RuleId).Distinct(StringComparer.OrdinalIgnoreCase)));
            }
        }

        return new MagneticRepeatReplacementResult(
            summaries,
            decisions.Values
                .OrderBy(x => x.SourceFile, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.SourceRecord)
                .ToArray());
    }

    internal static void WriteAuditGzip(
        string path,
        MagneticRepeatReplacementResult result,
        MagneticSurveyLineSegmentationResult segmentation,
        MagDataSourceCatalog sourceCatalog)
    {
        var assignmentBySource = segmentation.Assignments
            .ToDictionary(
                x => (x.SourceFile, x.SourceRecord),
                x => x);

        MagDataCompactExport.WriteGzipCsv(
            path,
            writer =>
            {
                writer.WriteLine("DateUtc,TimeUtc,SourceId,SourceRecord,LineTaskId,LineName,LinePass,AlongM,Action,IncludeInProcessing,RuleIds");
                foreach (MagneticRepeatReplacementDecision decision in result.Decisions)
                {
                    if (!assignmentBySource.TryGetValue(
                            (decision.SourceFile, decision.SourceRecord),
                            out MagneticSegmentAssignment row))
                    {
                        continue;
                    }
                    writer.WriteLine(string.Join(",",
                        Csv(row.TimestampUtc.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                        Csv(row.TimestampUtc.ToUniversalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)),
                        sourceCatalog.GetSourceId(row.SourceFile).ToString(CultureInfo.InvariantCulture),
                        row.SourceRecord.ToString(CultureInfo.InvariantCulture),
                        Csv(row.LineTaskId),
                        Csv(row.LineName),
                        row.LinePass?.ToString(CultureInfo.InvariantCulture) ?? "",
                        Number(row.AlongM),
                        decision.Action,
                        decision.IncludeInProcessing ? "YES" : "NO",
                        Csv(decision.RuleIds)));
                }
            });
    }

    public static void WriteSummaryCsv(
        string path,
        MagneticRepeatReplacementResult result)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        writer.WriteLine("RuleId,LineTaskId,LineName,StartAlongM,EndAlongM,OriginalLinePass,ReplacementLinePass,OriginalRecordsInRange,ReplacementRecordsInRange,ReplacementMinAlongM,ReplacementMaxAlongM,ReplacementMaxGapM,AllowedEndpointGapM,AllowedInternalGapM,ReplacementMaxCrossTrackM,CoverageValid,Status,Reason");
        foreach (MagneticRepeatReplacementRuleSummary row in result.Summaries)
        {
            writer.WriteLine(string.Join(",",
                Csv(row.RuleId),
                Csv(row.LineTaskId),
                Csv(row.LineName),
                Number(row.StartAlongM),
                Number(row.EndAlongM),
                row.OriginalLinePass.ToString(CultureInfo.InvariantCulture),
                row.ReplacementLinePass.ToString(CultureInfo.InvariantCulture),
                row.OriginalRecordsInRange.ToString(CultureInfo.InvariantCulture),
                row.ReplacementRecordsInRange.ToString(CultureInfo.InvariantCulture),
                Number(row.ReplacementMinAlongM),
                Number(row.ReplacementMaxAlongM),
                Number(row.ReplacementMaxGapM),
                Number(row.AllowedEndpointGapM),
                Number(row.AllowedInternalGapM),
                Number(row.ReplacementMaxCrossTrackM),
                row.CoverageValid ? "YES" : "NO",
                Csv(row.Status),
                Csv(row.Reason)));
        }
    }

    private static ResolvedRule EvaluateResolvedRule(
        string sourceDirectory,
        MagneticSurveyLineSegmentationResult segmentation,
        IReadOnlyDictionary<string, DiurnalCorrectedSample> corrected,
        MagneticRepeatReplacementRule rule,
        MagneticSurveyPassCandidate original,
        MagneticSurveyPassCandidate replacement)
    {
        double start;
        double end;
        if (rule.RangeMode.Equals(MagneticRepeatReplacementModes.WholeLine, StringComparison.OrdinalIgnoreCase))
        {
            start = Math.Min(original.MinAlongM, original.MaxAlongM);
            end = Math.Max(original.MinAlongM, original.MaxAlongM);
        }
        else
        {
            start = rule.StartAlongM ?? double.NaN;
            end = rule.EndAlongM ?? double.NaN;
        }

        string status = "OK";
        bool valid = true;
        if (!double.IsFinite(start) || !double.IsFinite(end) || end <= start)
        {
            valid = false;
            status = "替换区段 AlongM 起止无效";
        }
        else if (replacement.FirstTimeUtc <= original.LastTimeUtc)
        {
            valid = false;
            status = "重测 pass 不是原测完成后的后续测量";
        }
        else if (start < original.MinAlongM - 5d || end > original.MaxAlongM + 5d)
        {
            valid = false;
            status = "替换区段超出原测 pass 的有效沿线覆盖范围";
        }

        MagneticSegmentAssignment[] originalRows = PassRows(
            sourceDirectory,
            segmentation,
            original)
            .Where(x => x.AlongM.HasValue && InRange(x.AlongM.Value, start, end) && IsProductionEligible(corrected, x))
            .ToArray();
        MagneticSegmentAssignment[] replacementRows = PassRows(
            sourceDirectory,
            segmentation,
            replacement)
            .Where(x => x.AlongM.HasValue && InRange(x.AlongM.Value, start, end) && IsProductionEligible(corrected, x))
            .ToArray();

        double originalMedianGap = MedianPositiveGap(originalRows.Select(x => x.AlongM!.Value));
        // Replacement coverage is intentionally strict.  A selected interval must
        // remain spatially continuous after the original points are suppressed;
        // tolerances only absorb normal sample spacing / endpoint quantisation.
        double allowedEndpoint = Math.Min(10d, Math.Max(2d, double.IsFinite(originalMedianGap) ? originalMedianGap * 2d : 2d));
        double allowedInternal = Math.Min(25d, Math.Max(10d, double.IsFinite(originalMedianGap) ? originalMedianGap * 5d : 10d));
        double? replacementMin = replacementRows.Length > 0 ? replacementRows.Min(x => x.AlongM!.Value) : null;
        double? replacementMax = replacementRows.Length > 0 ? replacementRows.Max(x => x.AlongM!.Value) : null;
        double replacementMaxGapValue = MaxPositiveGap(replacementRows.Select(x => x.AlongM!.Value));
        double? replacementMaxGap = double.IsFinite(replacementMaxGapValue) ? replacementMaxGapValue : null;
        double[] replacementCrossTrack = replacementRows
            .Where(x => x.CrossTrackM.HasValue)
            .Select(x => Math.Abs(x.CrossTrackM!.Value))
            .ToArray();
        double? replacementMaxCross = replacementCrossTrack.Length == 0
            ? null
            : replacementCrossTrack.Max();

        if (valid && originalRows.Length == 0)
        {
            valid = false;
            status = "原测区段没有可进入正式处理的有效日变记录";
        }
        else if (valid && replacementRows.Length == 0)
        {
            valid = false;
            status = "重测区段没有可进入正式处理的有效日变记录";
        }
        else if (valid && (!replacementMin.HasValue || replacementMin.Value > start + allowedEndpoint))
        {
            valid = false;
            status = $"重测起点未覆盖目标区段（允许端点缺口 {allowedEndpoint:0.#} m）";
        }
        else if (valid && (!replacementMax.HasValue || replacementMax.Value < end - allowedEndpoint))
        {
            valid = false;
            status = $"重测终点未覆盖目标区段（允许端点缺口 {allowedEndpoint:0.#} m）";
        }
        else if (valid && replacementMaxGap.HasValue && replacementMaxGap.Value > allowedInternal)
        {
            valid = false;
            status = $"重测区段存在过大内部覆盖缺口 {replacementMaxGap.Value:0.#} m（允许 {allowedInternal:0.#} m）";
        }

        var summary = new MagneticRepeatReplacementRuleSummary(
            rule.RuleId,
            rule.LineTaskId,
            rule.LineName,
            start,
            end,
            original.LinePass,
            replacement.LinePass,
            originalRows.Length,
            replacementRows.Length,
            replacementMin,
            replacementMax,
            replacementMaxGap,
            allowedEndpoint,
            allowedInternal,
            replacementMaxCross,
            valid,
            status,
            rule.Reason);

        return new ResolvedRule(
            rule,
            original,
            replacement,
            new PassIdentity(original.LineTaskId, original.SourceKey, original.LinePass),
            new PassIdentity(replacement.LineTaskId, replacement.SourceKey, replacement.LinePass),
            start,
            end,
            summary);
    }

    private static MagneticRepeatReplacementRuleSummary InvalidSummary(
        MagneticRepeatReplacementRule rule,
        string status) =>
        new(
            rule.RuleId,
            rule.LineTaskId,
            rule.LineName,
            rule.StartAlongM ?? double.NaN,
            rule.EndAlongM ?? double.NaN,
            rule.OriginalLinePass,
            rule.ReplacementLinePass,
            0,
            0,
            null,
            null,
            null,
            2d,
            10d,
            null,
            false,
            status,
            rule.Reason);

    private static IEnumerable<MagneticSegmentAssignment> PassRows(
        string sourceDirectory,
        MagneticSurveyLineSegmentationResult segmentation,
        MagneticSurveyPassCandidate pass) =>
        segmentation.Assignments.Where(x =>
            x.IncludeInProcessing &&
            x.LinePass == pass.LinePass &&
            x.LineTaskId.Equals(pass.LineTaskId, StringComparison.OrdinalIgnoreCase) &&
            SourceKey(sourceDirectory, x.SourceFile).Equals(pass.SourceKey, StringComparison.OrdinalIgnoreCase));

    private static bool IsProductionEligible(
        IReadOnlyDictionary<string, DiurnalCorrectedSample> corrected,
        MagneticSegmentAssignment assignment) =>
        corrected.TryGetValue(SampleKey(assignment.SourceFile, assignment.SourceRecord), out DiurnalCorrectedSample? row) &&
        row.DiurnalDifferenceNt.HasValue;

    private static MagneticSurveyPassCandidate? ResolvePass(
        IReadOnlyList<MagneticSurveyPassCandidate> catalog,
        MagneticRepeatReplacementRule rule,
        bool replacement)
    {
        string sourceKey = replacement ? rule.ReplacementSourceKey : rule.OriginalSourceKey;
        int linePass = replacement ? rule.ReplacementLinePass : rule.OriginalLinePass;
        DateTimeOffset first = replacement ? rule.ReplacementFirstUtc : rule.OriginalFirstUtc;
        DateTimeOffset last = replacement ? rule.ReplacementLastUtc : rule.OriginalLastUtc;

        MagneticSurveyPassCandidate[] candidates = catalog
            .Where(x =>
                x.LineTaskId.Equals(rule.LineTaskId, StringComparison.OrdinalIgnoreCase) &&
                x.SourceKey.Equals(sourceKey, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (candidates.Length == 0)
            return null;

        MagneticSurveyPassCandidate? exact = candidates.FirstOrDefault(x =>
            x.LinePass == linePass &&
            TimeRangesOverlap(x.FirstTimeUtc, x.LastTimeUtc, first, last, TimeSpan.FromSeconds(2)));
        if (exact is not null)
            return exact;

        MagneticSurveyPassCandidate[] temporal = candidates
            .Where(x => TimeRangesOverlap(
                x.FirstTimeUtc,
                x.LastTimeUtc,
                first,
                last,
                TimeSpan.FromSeconds(2)))
            .ToArray();
        return temporal.Length == 1 ? temporal[0] : null;
    }

    private static string? FindConflict(IReadOnlyList<ResolvedRule> rules)
    {
        ResolvedRule[] valid = rules.Where(x => x.Summary.CoverageValid).ToArray();
        HashSet<PassIdentity> originals = valid.Select(x => x.OriginalIdentity).ToHashSet();
        HashSet<PassIdentity> replacements = valid.Select(x => x.ReplacementIdentity).ToHashSet();
        PassIdentity crossover = originals.FirstOrDefault(replacements.Contains);
        if (crossover != default)
            return "同一个 pass 同时被用作原测和重测来源，链式替换语义不明确。";

        for (int i = 0; i < valid.Length; i++)
        {
            for (int j = i + 1; j < valid.Length; j++)
            {
                ResolvedRule a = valid[i];
                ResolvedRule b = valid[j];
                if (!a.Rule.LineTaskId.Equals(b.Rule.LineTaskId, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!RangesOverlap(a.StartAlongM, a.EndAlongM, b.StartAlongM, b.EndAlongM))
                    continue;
                bool samePair = a.OriginalIdentity.Equals(b.OriginalIdentity) &&
                                a.ReplacementIdentity.Equals(b.ReplacementIdentity);
                if (!samePair)
                    return $"测线 {a.Rule.LineName} 存在相互重叠但来源 pass 不同的替换区段。";
            }
        }
        return null;
    }

    private static bool MatchesPass(
        string sourceDirectory,
        MagneticSegmentAssignment assignment,
        PassIdentity identity) =>
        assignment.LinePass == identity.LinePass &&
        assignment.LineTaskId.Equals(identity.LineTaskId, StringComparison.OrdinalIgnoreCase) &&
        SourceKey(sourceDirectory, assignment.SourceFile).Equals(identity.SourceKey, StringComparison.OrdinalIgnoreCase);

    private static bool InRange(double value, double start, double end) =>
        value >= start - 1e-6 && value <= end + 1e-6;

    private static bool RangesOverlap(double a0, double a1, double b0, double b1) =>
        Math.Max(a0, b0) <= Math.Min(a1, b1) + 1e-6;

    private static bool TimeRangesOverlap(
        DateTimeOffset a0,
        DateTimeOffset a1,
        DateTimeOffset b0,
        DateTimeOffset b1,
        TimeSpan tolerance) =>
        a0 <= b1 + tolerance && b0 <= a1 + tolerance;

    private static double MedianPositiveGap(IEnumerable<double> values)
    {
        double[] sorted = values
            .Where(double.IsFinite)
            .Distinct()
            .OrderBy(x => x)
            .ToArray();
        if (sorted.Length < 2)
            return double.NaN;
        double[] gaps = sorted
            .Zip(sorted.Skip(1), (a, b) => b - a)
            .Where(x => x > 1e-6)
            .OrderBy(x => x)
            .ToArray();
        if (gaps.Length == 0)
            return double.NaN;
        return gaps.Length % 2 == 1
            ? gaps[gaps.Length / 2]
            : (gaps[gaps.Length / 2 - 1] + gaps[gaps.Length / 2]) / 2d;
    }

    private static double MaxPositiveGap(IEnumerable<double> values)
    {
        double[] sorted = values
            .Where(double.IsFinite)
            .Distinct()
            .OrderBy(x => x)
            .ToArray();
        if (sorted.Length < 2)
            return double.NaN;
        double max = 0d;
        for (int i = 1; i < sorted.Length; i++)
            max = Math.Max(max, sorted[i] - sorted[i - 1]);
        return max;
    }

    private static string SourceKey(string sourceDirectory, string sourceFile)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(sourceDirectory))
            {
                string root = Path.GetFullPath(sourceDirectory);
                string full = Path.GetFullPath(sourceFile);
                string relative = Path.GetRelativePath(root, full).Replace('\\', '/');
                if (!relative.StartsWith("../", StringComparison.Ordinal) && relative != "..")
                    return relative;
            }
        }
        catch
        {
            // Fall through to a stable display key.
        }
        return Path.GetFileName(sourceFile);
    }

    private static string NormalizeWorkareaPath(string value) =>
        (value ?? "").Replace('\\', '/').Trim('/').Trim();

    private static string NormalizeRangeMode(string value) =>
        string.Equals(value, MagneticRepeatReplacementModes.WholeLine, StringComparison.OrdinalIgnoreCase)
            ? MagneticRepeatReplacementModes.WholeLine
            : MagneticRepeatReplacementModes.Segment;

    private static void NormalizeRule(MagneticRepeatReplacementRule rule)
    {
        rule.RuleId = string.IsNullOrWhiteSpace(rule.RuleId) ? Guid.NewGuid().ToString("N") : rule.RuleId.Trim();
        rule.WorkareaPath = NormalizeWorkareaPath(rule.WorkareaPath);
        rule.LineTaskId = (rule.LineTaskId ?? "").Trim();
        rule.LineName = (rule.LineName ?? "").Trim();
        rule.RangeMode = NormalizeRangeMode(rule.RangeMode);
        rule.OriginalSourceKey = (rule.OriginalSourceKey ?? "").Replace('\\', '/').Trim();
        rule.ReplacementSourceKey = (rule.ReplacementSourceKey ?? "").Replace('\\', '/').Trim();
        rule.Reason = (rule.Reason ?? "").Trim();
        rule.SourceFingerprint ??= "";
        rule.SurveyLineSource ??= "";
        rule.SurveyLineFingerprint ??= "";
        rule.OriginalFirstUtc = rule.OriginalFirstUtc.ToUniversalTime();
        rule.OriginalLastUtc = rule.OriginalLastUtc.ToUniversalTime();
        rule.ReplacementFirstUtc = rule.ReplacementFirstUtc.ToUniversalTime();
        rule.ReplacementLastUtc = rule.ReplacementLastUtc.ToUniversalTime();
        rule.CreatedAtUtc = rule.CreatedAtUtc == default ? DateTimeOffset.UtcNow : rule.CreatedAtUtc.ToUniversalTime();
    }

    private static MagneticRepeatReplacementRule CloneAndNormalize(MagneticRepeatReplacementRule rule)
    {
        var copy = new MagneticRepeatReplacementRule
        {
            RuleId = rule.RuleId,
            WorkareaPath = rule.WorkareaPath,
            LineTaskId = rule.LineTaskId,
            LineName = rule.LineName,
            RangeMode = rule.RangeMode,
            StartAlongM = rule.StartAlongM,
            EndAlongM = rule.EndAlongM,
            OriginalSourceKey = rule.OriginalSourceKey,
            OriginalLinePass = rule.OriginalLinePass,
            OriginalFirstUtc = rule.OriginalFirstUtc,
            OriginalLastUtc = rule.OriginalLastUtc,
            ReplacementSourceKey = rule.ReplacementSourceKey,
            ReplacementLinePass = rule.ReplacementLinePass,
            ReplacementFirstUtc = rule.ReplacementFirstUtc,
            ReplacementLastUtc = rule.ReplacementLastUtc,
            Reason = rule.Reason,
            Enabled = rule.Enabled,
            CreatedAtUtc = rule.CreatedAtUtc,
            SourceFingerprint = rule.SourceFingerprint,
            SurveyLineSource = rule.SurveyLineSource,
            SurveyLineFingerprint = rule.SurveyLineFingerprint
        };
        NormalizeRule(copy);
        return copy;
    }

    private static string SampleKey(string sourceFile, int sourceRow) =>
        $"{sourceFile}\u001f{sourceRow.ToString(CultureInfo.InvariantCulture)}";

    private static string Number(double? value) =>
        value.HasValue && double.IsFinite(value.Value)
            ? value.Value.ToString("0.###", CultureInfo.InvariantCulture)
            : "";

    private static string Number(double value) =>
        double.IsFinite(value)
            ? value.ToString("0.###", CultureInfo.InvariantCulture)
            : "";

    private static string Csv(string value)
    {
        string text = value ?? "";
        return text.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? "\"" + text.Replace("\"", "\"\"") + "\""
            : text;
    }

    private readonly record struct PassIdentity(
        string LineTaskId,
        string SourceKey,
        int LinePass);

    private sealed record ResolvedRule(
        MagneticRepeatReplacementRule Rule,
        MagneticSurveyPassCandidate Original,
        MagneticSurveyPassCandidate Replacement,
        PassIdentity OriginalIdentity,
        PassIdentity ReplacementIdentity,
        double StartAlongM,
        double EndAlongM,
        MagneticRepeatReplacementRuleSummary Summary);
}
