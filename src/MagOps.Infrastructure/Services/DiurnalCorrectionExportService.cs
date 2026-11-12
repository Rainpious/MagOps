using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using MagOps.Core.Diurnal;
using MagOps.Core.Geomagnetics;
using MagOps.Infrastructure.Storage;

namespace MagOps.Infrastructure.Services;

public sealed record DiurnalCorrectionExportResult(
    IReadOnlyList<string> Files,
    string CorrectedCsvPath,
    string ProcessingCsvPath,
    string T0SummaryCsvPath,
    string LineSegmentationAuditPath,
    string LineSegmentationSummaryPath,
    string RepeatReplacementAuditPath,
    string RepeatReplacementSummaryPath,
    string ManifestPath,
    DiurnalCorrectionResult Correction,
    int TotalCanonicalRoverRecords,
    int EligibleRoverRecords,
    int ExcludedRoverRecords,
    int ProcessingRecordCount,
    DiurnalBaseValiditySummary BaseValidity,
    MagneticPreLevelProcessingSummary PreLevel,
    MagneticSurveyLineSegmentationResult LineSegmentation,
    MagneticRepeatReplacementResult RepeatReplacement)
{
    public AuditProductionResult AuditProduction { get; init; } = null!;
    public string ProcessingOutputWarning { get; init; } = "";
    /// <summary>Exact emitted rows with final correction-gap LinePass values; candidate adapters must use these.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<(UavMagCanonicalSample Sample, MagneticSegmentAssignment? Assignment)> FinalExportedRows { get; init; } = [];
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<FinalProcessingCsvRow> FinalProcessingRows { get; init; } = [];
}

public sealed record FinalProcessingCsvRow(string SourceFile, int SourceRecord, IReadOnlyList<string> Values);

public sealed class DiurnalCorrectionExportService
{
    public string WriteDiurnalOnly(string outputDirectory, FieldMagFolderAnalysis analysis,
        DiurnalCorrectionOptions options, GeneratedArtifactContext context, string? projectRoot = null)
        => WriteDiurnalOnlyWithQuality(outputDirectory, analysis, options, context, projectRoot).Path;

    public (string Path, DiurnalCorrectionResult Correction) WriteDiurnalOnlyWithQuality(string outputDirectory, FieldMagFolderAnalysis analysis,
        DiurnalCorrectionOptions options, GeneratedArtifactContext context, string? projectRoot = null)
    {
        if (DiurnalBaseValidityGate.Evaluate(analysis.BaseSamples).AcceptedRecords == 0)
            throw new InvalidOperationException("PRODUCTION_GATE_BLOCKED: 没有有效 Base 样本，不能生成日变。");
        const string filename = "MagOps_日变改正_完整__MagOps_diurnal_only.csv.gz";
        string target = Path.Combine(Path.GetFullPath(outputDirectory), "完整日变");
        DiurnalCorrectionResult? completed = null;
        AtomicDirectoryBundle.Execute(target, stage =>
        {
            var correction = Correct(analysis, options);
            completed = correction;
            var catalog = MagDataCompactExport.BuildSourceCatalog(analysis);
            string path = Path.Combine(stage, filename);
            var previous = GeneratedArtifactIdentity.Inspect(path);
            if (File.Exists(path) && previous.Status != "VERIFIED")
                throw new InvalidDataException("已有日变成果身份无法验证，保留原文件。");
            WriteCorrectedCsvGzip(path, analysis.UavSamples, correction.Samples.ToDictionary(
                x => SampleKey(x.RoverSourceFile, x.RoverSourceRow), StringComparer.OrdinalIgnoreCase), options, catalog);
            string sourcePath = Path.Combine(stage, "日变来源与参数.json");
            var previousSource = GeneratedArtifactIdentity.Inspect(sourcePath);
            if (File.Exists(sourcePath) && previousSource.Status != "VERIFIED")
                throw new InvalidDataException("已有日变来源文件身份无法验证，保留原文件。");
            string? sourceId = previousSource.Metadata?.ArtifactId;
            if (projectRoot is not null)
            {
                var store = new JsonStore();
                var registry = new ArtifactService(store, new WorkspaceService(store)).List(projectRoot);
                string relative = Path.GetRelativePath(projectRoot, Path.Combine(target, "日变来源与参数.json")).Replace('\\', '/');
                if (relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative))
                    throw new InvalidDataException("日变来源输出不在项目内。");
                var registered = registry.SingleOrDefault(r => r.Path.Equals(relative, StringComparison.OrdinalIgnoreCase));
                if (registered is not null && registered.ArtifactId != sourceId)
                {
                    // Recover only the known diurnal-only manifest regeneration bug.
                    // The registry remains authoritative; no registry or generic identity gate is rewritten.
                    var meta = previousSource.Metadata;
                    if (meta is null || meta.Role != "manifest" || meta.ProcessingStage != "diurnal_only" ||
                        meta.ProjectId != context.ProjectId || meta.WorkareaId != context.WorkareaId ||
                        registered.MetadataOrigin != "processing_diurnal" || registered.ProcessingStage != "diurnal_only" ||
                        string.IsNullOrWhiteSpace(registered.ArtifactId) ||
                        registry.Any(r => r.ArtifactId == registered.ArtifactId && !r.Path.Equals(relative, StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidDataException("日变来源身份冲突不符合已知修复条件，保留原成果。");
                    sourceId = registered.ArtifactId;
                }
            }
            File.WriteAllText(sourcePath, JsonSerializer.Serialize(new
            { analysis.Fingerprint, Options = options, Sources = catalog.Entries,
                BaseValidity = DiurnalBaseValidityGate.Evaluate(analysis.BaseSamples),
                Algorithm = DiurnalCorrectionEngine.AlgorithmVersion }, new JsonSerializerOptions { WriteIndented = true }));
            var sources = catalog.Entries.Select(x => x.SourceFile).ToArray();
            GeneratedArtifactIdentity.Write(path, "diurnal_complete", "diurnal_only", analysis.Fingerprint,
                sources, previous.Metadata?.ArtifactId, context: context);
            GeneratedArtifactIdentity.Write(sourcePath, "manifest", "diurnal_only", analysis.Fingerprint, sources, sourceId, context: context);
            return path;
        }, (_, path) =>
        {
            if (GeneratedArtifactIdentity.Inspect(path).Status != "VERIFIED")
                throw new InvalidDataException("日变成果身份校验失败。");
            if (GeneratedArtifactIdentity.Inspect(Path.Combine(Path.GetDirectoryName(path)!, "日变来源与参数.json")).Status != "VERIFIED")
                throw new InvalidDataException("日变来源身份校验失败。");
        }, preserveUnrelatedFiles: true);
        return (Path.Combine(target, filename), completed!);
    }

    private readonly DiurnalCorrectionEngine _engine =
        new();

    public DiurnalCorrectionResult Correct(
        FieldMagFolderAnalysis analysis,
        DiurnalCorrectionOptions options)
    {
        List<DiurnalRoverSample> rover =
            analysis.UavSamples
                .Where(IsEligibleRover)
                .Select(x =>
                    new DiurnalRoverSample(
                        x.TimestampUtc,
                        x.MagneticFieldNt!.Value,
                        x.SourceFile,
                        x.SourceRecord))
                .ToList();

        List<DiurnalBaseSample> baseSamples =
            DiurnalBaseValidityGate.Accepted(analysis.BaseSamples)
                .Select(x =>
                    new DiurnalBaseSample(
                        x.TimestampUtc,
                        x.MagneticFieldNt,
                        x.SourceFile,
                        x.SourceRecord))
                .ToList();

        return _engine.Correct(
            rover,
            baseSamples,
            options);
    }

    public DiurnalCorrectionExportResult WriteNativeCorrection(
        string outputDirectory,
        FieldMagFolderAnalysis analysis,
        DiurnalCorrectionOptions options,
        IgrfModelSet igrfModels,
        MagneticPreLevelProcessingOptions? preLevelOptions = null,
        IReadOnlyList<PlannedSurveyLineTask>? surveyLines = null,
        MagneticSurveyLineSegmentationOptions? segmentationOptions = null,
        string surveyLineSource = "",
        string surveyLineFingerprint = "",
        IReadOnlyList<MagneticRepeatReplacementRule>? repeatReplacementRules = null,
        AuditProductionRequest? auditRequest = null,
        GeneratedArtifactContext? artifactContext = null)
    {
        AuditProductionResult audit =
            new AuditProductionService().Analyze(analysis, surveyLines ?? [], auditRequest, surveyLineFingerprint);
        return WriteNativeCorrection(
            outputDirectory, analysis, options, igrfModels, preLevelOptions, surveyLines,
            segmentationOptions, surveyLineSource, surveyLineFingerprint,
            repeatReplacementRules, audit, artifactContext,
            allowLegacyRepeatReviewDraft: auditRequest?.AllowReviewDraft == true);
    }

    public DiurnalCorrectionExportResult WriteNativeCorrection(
        string outputDirectory,
        FieldMagFolderAnalysis analysis,
        DiurnalCorrectionOptions options,
        IgrfModelSet igrfModels,
        MagneticPreLevelProcessingOptions? preLevelOptions,
        IReadOnlyList<PlannedSurveyLineTask>? surveyLines,
        MagneticSurveyLineSegmentationOptions? segmentationOptions,
        string surveyLineSource,
        string surveyLineFingerprint,
        IReadOnlyList<MagneticRepeatReplacementRule>? repeatReplacementRules,
        AuditProductionResult audit,
        GeneratedArtifactContext? artifactContext,
        bool allowLegacyRepeatReviewDraft = false)
    {
        ArgumentNullException.ThrowIfNull(audit);
        if (repeatReplacementRules?.Any(r => r.Enabled) == true)
        {
            if (!allowLegacyRepeatReviewDraft)
                throw new InvalidOperationException("PRODUCTION_GATE_BLOCKED: 旧 LinePass 重测规则不能自动迁移为审计区间选择。");
            audit = audit with { Status = "REVIEW_DRAFT", IsReviewDraft = true,
                BlockedReasons = audit.BlockedReasons.Concat(["LEGACY_REPEAT_RULES_REVIEW: 旧重测规则未执行，需映射到审计 Pass/区间。"]).ToArray() };
        }
        string target = Path.GetFullPath(outputDirectory) + (audit.IsReviewDraft ? ".review-draft" : "");
        string stagePath = "";
        var result = AtomicDirectoryBundle.Execute(target, stage =>
        {
            stagePath = stage;
            var previousIds = new Dictionary<string, string>();
            foreach (var pair in DiurnalArtifactBundle.Outputs)
            {
                string stageName = pair.Value.Stage + (audit.IsReviewDraft ? "_REVIEW_DRAFT" : "");
                string tagged = Path.Combine(stage, GeneratedArtifactIdentity.TaggedName(pair.Key, stageName));
                var identity = GeneratedArtifactIdentity.Inspect(tagged);
                if (File.Exists(tagged) && identity.Status != "VERIFIED")
                    throw new InvalidDataException($"已有成果身份无法验证，暂停替换并保留旧成果：{tagged}");
                if (identity.Status == "VERIFIED") previousIds[pair.Key] = identity.Metadata!.ArtifactId;
                File.Delete(tagged);
                File.Delete(tagged + GeneratedArtifactIdentity.Suffix);
            }
            var built = BuildNativeCorrection(stage, analysis, options, igrfModels, preLevelOptions,
                surveyLines, segmentationOptions, surveyLineSource, surveyLineFingerprint, repeatReplacementRules, audit);
            return DiurnalArtifactBundle.Label(built, analysis, previousIds, artifactContext);
        }, (_, built) =>
        {
            foreach (string path in built.Files)
            {
                if (!File.Exists(path)) throw new InvalidDataException($"Export file missing: {path}");
                if (!path.EndsWith(GeneratedArtifactIdentity.Suffix, StringComparison.Ordinal) &&
                    GeneratedArtifactIdentity.Inspect(path).Status != "VERIFIED")
                    throw new InvalidDataException($"Export identity invalid: {path}");
            }
        }, preserveUnrelatedFiles: true);
        return DiurnalArtifactBundle.Rebase(result, stagePath, target);
    }

    private DiurnalCorrectionExportResult BuildNativeCorrection(
        string outputDirectory,
        FieldMagFolderAnalysis analysis,
        DiurnalCorrectionOptions options,
        IgrfModelSet igrfModels,
        MagneticPreLevelProcessingOptions? preLevelOptions,
        IReadOnlyList<PlannedSurveyLineTask>? surveyLines,
        MagneticSurveyLineSegmentationOptions? segmentationOptions,
        string surveyLineSource,
        string surveyLineFingerprint,
        IReadOnlyList<MagneticRepeatReplacementRule>? repeatReplacementRules,
        AuditProductionResult audit)
    {
        string directory =
            Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(directory);

        DiurnalBaseValiditySummary baseValidity =
            DiurnalBaseValidityGate.Evaluate(
                analysis.BaseSamples);

        DiurnalCorrectionResult correction =
            Correct(
                analysis,
                options);

        MagDataSourceCatalog sourceCatalog =
            MagDataCompactExport.BuildSourceCatalog(
                analysis);

        IReadOnlyList<PlannedSurveyLineTask> effectiveSurveyLines = audit.EffectiveTasks ?? surveyLines ?? [];
        MagneticSurveyLineSegmentationResult lineSegmentation =
            MagneticSurveyLineSegmentationService.FromAudit(analysis.UavSamples, effectiveSurveyLines, audit);
        // Legacy LinePass replacement is not a production selector anymore. It is
        // retained only for historical review; audit selections are already
        // materialized in lineSegmentation and must be the sole authority here.
        var repeatReplacement = MagneticRepeatReplacementResult.Empty();

        // Only retire previous managed audit/compatibility files after all
        // production-selection rules have passed validation. A bad replacement
        // rule must not damage the last valid exported audit trail.
        File.Delete(
            Path.Combine(
                directory,
                "MagOps_日变改正_完整.csv"));
        File.Delete(
            Path.Combine(
                directory,
                "磁测分线_记录分类.csv.gz"));
        File.Delete(
            Path.Combine(
                directory,
                "磁测分线_摘要.csv"));
        File.Delete(
            Path.Combine(
                directory,
                "重测替换_记录选择.csv.gz"));
        File.Delete(
            Path.Combine(
                directory,
                "重测替换_摘要.csv"));

        Dictionary<string, DiurnalCorrectedSample> bySource =
            correction.Samples.ToDictionary(
                x => SampleKey(
                    x.RoverSourceFile,
                    x.RoverSourceRow),
                StringComparer.OrdinalIgnoreCase);

        var files =
            new List<string>();

        string correctedCsv =
            Path.Combine(
                directory,
                "MagOps_日变改正_完整.csv.gz");
        WriteCorrectedCsvGzip(
            correctedCsv,
            analysis.UavSamples,
            bySource,
            options,
            sourceCatalog);
        files.Add(correctedCsv);

        string processingCsv =
            Path.Combine(
                directory,
                "MagOps_日变磁测_处理用.csv");
        var exportedRows = new List<(UavMagCanonicalSample Sample, MagneticSegmentAssignment? Assignment)>();
        var processingRows = new List<FinalProcessingCsvRow>();
        MagneticPreLevelProcessingSummary preLevel =
            MagneticPreLevelProcessingService.WriteProcessingCsv(
                processingCsv,
                analysis.UavSamples,
                bySource,
                igrfModels,
                preLevelOptions,
                lineSegmentation,
                repeatReplacement,
                (sample, assignment) => exportedRows.Add((sample, assignment)),
                (index, values) => processingRows.Add(new(exportedRows[index].Sample.SourceFile,
                    exportedRows[index].Sample.SourceRecord, values)));
        int processingRecordCount =
            preLevel.ProcessingRecords;
        var exportedKeys = exportedRows.Select(r => (r.Sample.SourceFile, r.Sample.SourceRecord)).ToHashSet();
        var taskById = effectiveSurveyLines.Where(t => !string.IsNullOrWhiteSpace(t.SurveyTaskStableId))
            .GroupBy(t => t.SurveyTaskStableId).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single());
        var legById = audit.Execution.Legs.ToDictionary(l => l.FlightLegId);
        var csvPasses = audit.Execution.Passes.Where(p => taskById.ContainsKey(p.SurveyTaskStableId)).Select(p => p with
        {
            Coverage = ObservedTrackCoverageService.Calculate(legById[p.FlightLegId].Samples
                .Where(s => exportedKeys.Contains((s.SourceFile, s.SourceRecord))).ToArray(),
                taskById[p.SurveyTaskStableId], audit.TrackSegmentation?.MatchOptions ?? new())
        }).Where(p => p.Coverage.Count > 0).ToArray();
        audit = audit with { ProductionCsvCoverage = TaskTrackCoverageService.Summarize(effectiveSurveyLines, csvPasses, "FINAL_CSV") };
        string processingOutputWarning = "";
        if (processingRecordCount > 0)
            files.Add(processingCsv);
        else
        {
            // Keep the audit bundle, but never publish a header-only file as an Oasis result.
            File.Delete(processingCsv);
            processingCsv = "";
            processingOutputWarning = audit.SelectedSamples.Count == 0
                ? $"没有记录进入生产选择：候选测线 {audit.Execution.Passes.Count} 段，归属已确认 {audit.Execution.Passes.Count(p => p.ReviewConfirmed)} 段，质量检查通过 {audit.Execution.Passes.Count(p => p.QualityStatus == "PASS")} 段。请将核验合格的航段勾选“确认”并将“质量检查”设为“通过”；需要保留待检查航段时，请逐段手动勾选“纳入草稿”。"
                : "已选择航段，但没有同时具备有效日变改正值和经纬度的记录。请检查所选日期的日变数据及其时间覆盖、磁测值和坐标。";
        }

        string t0SummaryCsv =
            Path.Combine(
                directory,
                "调平_T0_UTC日期摘要.csv");
        if (processingRecordCount > 0)
        {
            MagneticPreLevelProcessingService.WriteT0DaySummaryCsv(t0SummaryCsv, preLevel);
            files.Add(t0SummaryCsv);
        }
        else
            t0SummaryCsv = "";

        string lineSegmentationAudit = "";
        string lineSegmentationSummary = "";
        if (lineSegmentation.Options.Enabled)
        {
            lineSegmentationAudit = Path.Combine(
                directory,
                "磁测分线_记录分类.csv.gz");
            MagneticSurveyLineSegmentationService.WriteAuditGzip(
                lineSegmentationAudit,
                lineSegmentation,
                sourceCatalog);
            files.Add(lineSegmentationAudit);

            lineSegmentationSummary = Path.Combine(
                directory,
                "磁测分线_摘要.csv");
            MagneticSurveyLineSegmentationService.WriteSummaryCsv(
                lineSegmentationSummary,
                lineSegmentation);
            files.Add(lineSegmentationSummary);
        }

        string repeatReplacementAudit = "";
        string repeatReplacementSummary = "";
        if (repeatReplacement.ActiveRuleCount > 0)
        {
            repeatReplacementAudit = Path.Combine(
                directory,
                "重测替换_记录选择.csv.gz");
            MagneticRepeatReplacementService.WriteAuditGzip(
                repeatReplacementAudit,
                repeatReplacement,
                lineSegmentation,
                sourceCatalog);
            files.Add(repeatReplacementAudit);

            repeatReplacementSummary = Path.Combine(
                directory,
                "重测替换_摘要.csv");
            MagneticRepeatReplacementService.WriteSummaryCsv(
                repeatReplacementSummary,
                repeatReplacement);
            files.Add(repeatReplacementSummary);
        }

        string summaryCsv =
            Path.Combine(
                directory,
                "日变改正_UTC日期摘要.csv");
        WriteDaySummaryCsv(
            summaryCsv,
            correction.Days);
        files.Add(summaryCsv);

        int eligible =
            analysis.UavSamples.Count(
                IsEligibleRover);
        int excluded =
            analysis.UavSamples.Count -
            eligible;

        string qc =
            Path.Combine(
                directory,
                "日变改正_QC.md");
        WriteQualityMarkdown(
            qc,
            analysis,
            correction,
            eligible,
            excluded,
            baseValidity,
            preLevel,
            lineSegmentation,
            repeatReplacement);
        File.AppendAllText(qc,
            $"\n## 处理用 CSV 导出状态\n\n候选测线 {audit.Execution.Passes.Count} 段；归属已确认 {audit.Execution.Passes.Count(p => p.ReviewConfirmed)} 段；质量检查通过 {audit.Execution.Passes.Count(p => p.QualityStatus == "PASS")} 段；生产选择 {audit.SelectedSamples.Count} 点；处理用 CSV {processingRecordCount} 行。\n\n" +
            (processingRecordCount == 0
                ? "未生成可用于 Oasis 的处理用 CSV 或 T0 摘要。" + processingOutputWarning
                : "处理用 CSV：" + Path.GetFileName(processingCsv)) + "\n", new UTF8Encoding(false));
        files.Add(qc);

        string anomalyPath = Path.Combine(directory, "生产异常区间.json");
        File.WriteAllText(anomalyPath, JsonSerializer.Serialize(new
        {
            Version = ProductionAnomalyDetector.Version,
            audit.ProductionSnapshotFingerprint,
            audit.ProductionAnomalyOptions,
            Intervals = audit.ProductionAnomalies
        }, new JsonSerializerOptions { WriteIndented = true }));
        files.Add(anomalyPath);
        string tracePath = Path.Combine(directory, "生产CSV_逐点追溯.csv.gz");
        ProductionCsvTraceService.Write(tracePath, analysis.UavSamples, audit, exportedRows, sourceCatalog, lineSegmentation);
        files.Add(tracePath);
        string executionPath = Path.Combine(directory, "审计执行与生产选择.json");
        File.WriteAllText(executionPath, JsonSerializer.Serialize(audit, new JsonSerializerOptions { WriteIndented = true }));
        files.Add(executionPath);
        string visualizationPath = Path.Combine(directory, "审计轨迹审查.html");
        AuditTrackVisualizationService.Write(visualizationPath, analysis, effectiveSurveyLines, audit);
        files.Add(visualizationPath);
        string manualPath = Path.Combine(directory, "待复核_手动编辑.csv");
        if (audit.IsReviewDraft)
        {
            using var writer = new StreamWriter(manualPath, false, new UTF8Encoding(true));
            writer.WriteLine("SourceFile,SourceRecord,DateUtc,TimeUtc,Longitude,Latitude,RawMagNt,ReviewStatus,UserLineTaskId,UserPassId,UserInclude,UserNote");
            foreach (var row in analysis.UavSamples.OrderBy(r => r.TimestampUtc))
                writer.WriteLine(string.Join(",", "\"" + row.SourceFile.Replace("\"", "\"\"") + "\"",
                    row.SourceRecord, row.TimestampUtc.ToString("yyyy-MM-dd"), row.TimestampUtc.ToString("HH:mm:ss.fff"),
                    row.Longitude?.ToString("R", CultureInfo.InvariantCulture) ?? "",
                    row.Latitude?.ToString("R", CultureInfo.InvariantCulture) ?? "",
                    row.MagneticFieldNt?.ToString("R", CultureInfo.InvariantCulture) ?? "", "REVIEW_REQUIRED", "", "", "", ""));
            files.Add(manualPath);
        }

        string manifest =
            Path.Combine(
                directory,
                "日变改正_manifest.json");
        File.WriteAllText(
            manifest,
            JsonSerializer.Serialize(
                new
                {
                    schema_version =
                        "magops-native-diurnal-10",
                    production_authority = "AUDIT_ONLY",
                    production_snapshot_fingerprint = audit.ProductionSnapshotFingerprint,
                    production_anomaly_version = ProductionAnomalyDetector.Version,
                    production_anomaly_options = audit.ProductionAnomalyOptions,
                    production_anomaly_excluded_points = audit.ProductionAnomalies.Sum(x => x.ExcludedSelectedPoints),
                    production_trace_file = "生产CSV_逐点追溯.csv.gz",
                    production_trace_exported_rows = exportedRows.Count,
                    production_csv_coverage = audit.ProductionCsvCoverage,
                    production_status = audit.Status,
                    blocked_reasons = audit.BlockedReasons,
                    editable_draft = audit.IsReviewDraft,
                    processing_output_status = processingRecordCount > 0 ? "GENERATED" : "NO_PROCESSING_ROWS",
                    processing_output_warning = processingOutputWarning,
                    production_review = new
                    {
                        candidate_passes = audit.Execution.Passes.Count,
                        attribution_confirmed_passes = audit.Execution.Passes.Count(p => p.ReviewConfirmed),
                        quality_passed_passes = audit.Execution.Passes.Count(p => p.QualityStatus == "PASS"),
                        selected_samples = audit.SelectedSamples.Count
                    },
                    algorithm_version =
                        DiurnalCorrectionEngine.AlgorithmVersion,
                    generated_at_utc =
                        DateTimeOffset.UtcNow.ToString(
                            "O",
                            CultureInfo.InvariantCulture),
                    source_directory =
                        analysis.SourceDirectory,
                    source_fingerprint =
                        analysis.Fingerprint,
                    mode =
                        options.RejectInterpolationAcrossLargeGap
                            ? "MagOps Production Safe"
                            : options.GemLinkTenthsCompatibility
                                ? "GEMLink Compatible"
                                : "Native High Precision",
                    primary_diurnal_formula =
                        "rigai / DiurnalDifferenceNt = Rover - BaseUsed",
                    optional_reference_formula =
                        "DatumAdjustedNt = rigai + Datum",
                    prelevel_formula =
                        MagneticPreLevelProcessingService.Formula,
                    interpolation =
                        "linear between nearest Base samples inside the same UTC date",
                    physical_input_partitioning =
                        "none",
                    physical_output_partitioning =
                        "none",
                    storage =
                        new
                        {
                            complete_audit_output =
                                "MagOps_日变改正_完整.csv.gz",
                            processing_output =
                                processingRecordCount > 0 ? "MagOps_日变磁测_处理用.csv" : null,
                            t0_utc_day_summary =
                                processingRecordCount > 0 ? "调平_T0_UTC日期摘要.csv" : null,
                            complete_output_format =
                                "CSV inside GZip",
                            traceability =
                                "SourceId + SourceRecord",
                            source_path_per_row =
                                false,
                            processing_output_policy =
                                "valid rigai rows with Longitude/Latitude; when survey-line segmentation is enabled only stable planned-survey-line runs are retained; processing CSV starts with LineName,LineOrder,LinePass, followed by DateUtc,TimeUtc,LineTaskId and magnetic/coordinate channels; optional repeat-survey replacement then suppresses selected original chainage intervals and retains only the explicitly chosen later pass inside those intervals; output adds projected X/Y (X=Easting, Y=Northing, metres), Oasis-compatible Tof/Inc/Dec/T0/dTgz; default AUTO_GLOBAL_MEAN_CENTERED computes one project-wide T0 after the production-data selection; AUTO_BY_UTC_DATE remains available only for legacy-result reproduction; one physical CSV is preserved and no numeric downsampling occurs inside retained selections",
                            temporal_downsampling =
                                false,
                            numeric_precision_reduction =
                                false
                        },
                    survey_line_segmentation =
                        new
                        {
                            requested = segmentationOptions?.Enabled ?? true,
                            enabled = lineSegmentation.Options.Enabled,
                            algorithm_version = MagneticSurveyLineSegmentationService.AlgorithmVersion,
                            route_source = string.IsNullOrWhiteSpace(surveyLineSource) ? null : surveyLineSource,
                            route_available = lineSegmentation.Summary.RouteAvailable,
                            planned_line_count = lineSegmentation.Summary.PlannedLineCount,
                            coordinate_records = lineSegmentation.Summary.CoordinateRecords,
                            sessions = lineSegmentation.Summary.Sessions,
                            retained_survey_line_records = lineSegmentation.Summary.SurveyLineRecords,
                            excluded_transit_to_first_line = lineSegmentation.Summary.TransitToFirstRecords,
                            excluded_connectors = lineSegmentation.Summary.ConnectorRecords,
                            excluded_return_after_last_line = lineSegmentation.Summary.ReturnRecords,
                            excluded_unmatched = lineSegmentation.Summary.UnmatchedRecords,
                            matched_line_count = lineSegmentation.Summary.MatchedLineCount,
                            heading_tolerance_deg = lineSegmentation.Options.HeadingToleranceDegrees,
                            endpoint_margin_m = lineSegmentation.Options.EndpointMarginM,
                            minimum_survey_run_points = lineSegmentation.Options.MinimumSurveyRunPoints,
                            minimum_survey_run_along_span_m = lineSegmentation.Options.MinimumSurveyRunAlongSpanM,
                            audit_output = lineSegmentation.Options.Enabled ? "磁测分线_记录分类.csv.gz" : null,
                            summary_output = lineSegmentation.Options.Enabled ? "磁测分线_摘要.csv" : null
                        },
                    repeat_survey_replacement =
                        new
                        {
                            enabled = repeatReplacement.ActiveRuleCount > 0,
                            algorithm_version = MagneticRepeatReplacementService.AlgorithmVersion,
                            active_rule_count = repeatReplacement.ActiveRuleCount,
                            excluded_original_records = repeatReplacement.ExcludedOriginalRecords,
                            selected_replacement_records = repeatReplacement.SelectedReplacementRecords,
                            suppressed_replacement_records = repeatReplacement.SuppressedReplacementRecords,
                            selection_basis = "LineTaskId + source pass + AlongM chainage",
                            raw_data_policy = "raw/canonical/full diurnal audit are immutable; replacement only changes the production processing selection",
                            source_fingerprint = analysis.Fingerprint,
                            survey_line_source = surveyLineSource,
                            survey_line_fingerprint = surveyLineFingerprint,
                            audit_output = repeatReplacement.ActiveRuleCount > 0 ? "重测替换_记录选择.csv.gz" : null,
                            summary_output = repeatReplacement.ActiveRuleCount > 0 ? "重测替换_摘要.csv" : null,
                            rules = repeatReplacement.Summaries.Select(x => new
                            {
                                rule_id = x.RuleId,
                                line_task_id = x.LineTaskId,
                                line_name = x.LineName,
                                start_along_m = x.StartAlongM,
                                end_along_m = x.EndAlongM,
                                original_line_pass = x.OriginalLinePass,
                                replacement_line_pass = x.ReplacementLinePass,
                                original_records_in_range = x.OriginalRecordsInRange,
                                replacement_records_in_range = x.ReplacementRecordsInRange,
                                replacement_max_gap_m = x.ReplacementMaxGapM,
                                replacement_max_cross_track_m = x.ReplacementMaxCrossTrackM,
                                coverage_valid = x.CoverageValid,
                                status = x.Status,
                                reason = x.Reason
                            }).ToArray()
                        },
                    source_id_policy =
                        "same deterministic source-path ordering as integrated canonical export",
                    source_files =
                        sourceCatalog.Entries.Select(x =>
                            new
                            {
                                source_id =
                                    x.SourceId,
                                sensor =
                                    x.Sensor,
                                relative_path =
                                    x.RelativePath,
                                original_source =
                                    x.SourceFile,
                                canonical_records =
                                    x.CanonicalRecordCount
                            })
                            .ToArray(),
                    calculation_grouping =
                        "UTC Date",
                    business_day_grouping =
                        FieldMagDataService
                            .BeijingProgressTimeZone,
                    datum_nt =
                        options.DatumNt,
                    gemlink_tenths_compatibility =
                        options.GemLinkTenthsCompatibility,
                    max_interpolation_gap_seconds =
                        options.MaxInterpolationGapSeconds,
                    reject_interpolation_across_large_gap =
                        options.RejectInterpolationAcrossLargeGap,
                    max_gap_policy =
                        options.RejectInterpolationAcrossLargeGap
                            ? "reject correction and flag BASE_GAP_TOO_LARGE"
                            : "calculate and flag BASE_GAP_LARGE",
                    base_validity_gate =
                        new
                        {
                            min_physical_field_nt =
                                DiurnalBaseValidityGate.MinPhysicalFieldNt,
                            max_physical_field_nt =
                                DiurnalBaseValidityGate.MaxPhysicalFieldNt,
                            min_sq =
                                DiurnalBaseValidityGate.MinSq,
                            max_sq =
                                DiurnalBaseValidityGate.MaxSq,
                            day_outlier_floor_nt =
                                DiurnalBaseValidityGate.DayOutlierFloorNt,
                            day_outlier_mad_multiplier =
                                DiurnalBaseValidityGate.DayOutlierMadMultiplier,
                            session_invalid_fraction =
                                DiurnalBaseValidityGate.SessionInvalidFraction,
                            session_median_deviation_nt =
                                DiurnalBaseValidityGate.SessionMedianDeviationNt,
                            total_records =
                                baseValidity.TotalRecords,
                            accepted_records =
                                baseValidity.AcceptedRecords,
                            rejected_records =
                                baseValidity.RejectedRecords,
                            rejected_sessions =
                                baseValidity.RejectedSessions,
                            accepted_base_median_nt =
                                baseValidity.AcceptedMedianFieldNt,
                            rejection_counts =
                                baseValidity.RejectionCounts
                        },
                    datum_policy =
                        "Datum defaults to 0. The primary production channel is rigai = Rover - BaseUsed and never depends on Datum. A non-zero Datum is optional and only creates DatumAdjustedNt; it is not T0 and is not inferred from the Base median.",
                    prelevel =
                        new
                        {
                            t0_mode = preLevel.T0Mode,
                            fixed_t0_nt = preLevel.FixedT0Nt,
                            t0_min_nt = preLevel.T0MinNt,
                            t0_max_nt = preLevel.T0MaxNt,
                            formula = MagneticPreLevelProcessingService.Formula,
                            igrf_records = preLevel.IgrfRecords,
                            igrf_skipped_records = preLevel.IgrfSkippedRecords,
                            dtgz_mean_nt = preLevel.DTgzMeanNt,
                            dtgz_min_nt = preLevel.DTgzMinNt,
                            dtgz_max_nt = preLevel.DTgzMaxNt,
                            utc_day_t0 = preLevel.Days.Select(x => new
                            {
                                utc_date = x.UtcDate.ToString(
                                    "yyyy-MM-dd",
                                    CultureInfo.InvariantCulture),
                                t0_nt = x.T0Nt,
                                x.IgrfRecords,
                                x.ProcessingRecords,
                                dtgz_mean_nt = x.DTgzMeanNt,
                                dtgz_min_nt = x.DTgzMinNt,
                                dtgz_max_nt = x.DTgzMaxNt
                            }).ToArray(),
                            igrf_coefficient_sha256 = preLevel.IgrfCoefficientSha256,
                            height_source = "AltitudeM",
                            igrf_date_source = "DateUtc",
                            xy_projection = new
                            {
                                x = "Easting",
                                y = "Northing",
                                unit = "m",
                                projection = "Gauss-Kruger 3-degree",
                                datum = preLevel.ProjectionDatum,
                                central_meridian_deg = preLevel.ProjectionCentralMeridianDeg,
                                central_meridian_source = preLevel.ProjectionCentralMeridianSource,
                                processing_data_suggested_central_meridian_deg =
                                    preLevel.ProcessingDataSuggestedCentralMeridianDeg,
                                central_meridian_consistent =
                                    preLevel.ProjectionCentralMeridianConsistent,
                                false_easting_m = preLevel.ProjectionFalseEastingM,
                                false_northing_m = preLevel.ProjectionFalseNorthingM
                            }
                        },
                    duplicate_base_time_policy =
                        "reject that UTC date and flag DUPLICATE_BASE_TIME until GEMLink golden behavior is confirmed",
                    rover_records_total =
                        analysis.UavSamples.Count,
                    rover_records_eligible =
                        eligible,
                    rover_records_excluded =
                        excluded,
                    base_records_total =
                        analysis.BaseSamples.Count,
                    base_records_accepted =
                        baseValidity.AcceptedRecords,
                    base_records_rejected =
                        baseValidity.RejectedRecords,
                    exact =
                        correction.ExactCount,
                    interpolated =
                        correction.InterpolatedCount,
                    uncorrected_eligible =
                        correction.UncorrectedCount,
                    processing_records =
                        processingRecordCount,
                    utc_days =
                        correction.Days.Select(x =>
                            new
                            {
                                utc_date =
                                    x.UtcDate.ToString(
                                        "yyyy-MM-dd",
                                        CultureInfo.InvariantCulture),
                                x.RoverCount,
                                x.BaseCount,
                                x.ExactCount,
                                x.InterpolatedCount,
                                x.UncorrectedCount,
                                x.DuplicateBaseTimeKeyCount,
                                x.HasTimeOverlap,
                                x.Status,
                                quality_flags =
                                    x.QualityFlags
                            })
                            .ToArray(),
                    outputs =
                        files.Select(x =>
                            new
                            {
                                file =
                                    Path.GetFileName(x),
                                bytes =
                                    new FileInfo(x).Length
                            })
                            .ToArray()
                },
                new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder =
                        JavaScriptEncoder.Create(
                            UnicodeRanges.All)
                }),
            new UTF8Encoding(true));
        files.Add(manifest);

        return new DiurnalCorrectionExportResult(
            files,
            correctedCsv,
            processingCsv,
            t0SummaryCsv,
            lineSegmentationAudit,
            lineSegmentationSummary,
            repeatReplacementAudit,
            repeatReplacementSummary,
            manifest,
            correction,
            analysis.UavSamples.Count,
            eligible,
            excluded,
            processingRecordCount,
            baseValidity,
            preLevel,
            lineSegmentation,
            repeatReplacement) { AuditProduction = audit, ProcessingOutputWarning = processingOutputWarning,
                FinalExportedRows = exportedRows.AsReadOnly(), FinalProcessingRows = processingRows.AsReadOnly() };
    }

    private static bool IsEligibleRover(
        UavMagCanonicalSample row) =>
        row.MagneticFieldNt.HasValue &&
        !row.SensorStatus.Equals(
            "F",
            StringComparison.OrdinalIgnoreCase);

    private static void WriteCorrectedCsvGzip(
        string path,
        IReadOnlyList<UavMagCanonicalSample> allRover,
        IReadOnlyDictionary<string, DiurnalCorrectedSample> correction,
        DiurnalCorrectionOptions options,
        MagDataSourceCatalog sourceCatalog)
    {
        MagDataCompactExport.WriteGzipCsv(
            path,
            writer =>
            {
                writer.WriteLine(
                    "DateUtc,TimeUtc,Longitude,Latitude,AltitudeM,LaserAltitudeM,RawMagNt,BaseUsedNt,DiurnalDifferenceNt,DatumNt,DatumAdjustedNt,CorrectionMode,BaseGapSeconds,QcFlags,GpsStatus,Satellites,SensorStatus,RoverSourceId,RoverSourceRecord,BasePrevSourceId,BasePrevSourceRecord,BaseNextSourceId,BaseNextSourceRecord");

                foreach (UavMagCanonicalSample row in
                         allRover
                             .OrderBy(x =>
                                 x.TimestampUtc)
                             .ThenBy(
                                 x => x.SourceFile,
                                 StringComparer.OrdinalIgnoreCase)
                             .ThenBy(x =>
                                 x.SourceRecord))
                {
                    correction.TryGetValue(
                        SampleKey(
                            row.SourceFile,
                            row.SourceRecord),
                        out DiurnalCorrectedSample? corrected);

                    var flags =
                        new List<string>(
                            row.QualityFlags);

                    string mode =
                        DiurnalCorrectionMode
                            .Uncorrected
                            .ToString();
                    double? baseUsed = null;
                    double? diurnalDifference = null;
                    double? datumAdjusted = null;
                    double? baseGap = null;
                    string basePrevSourceId = "";
                    string basePrevRow = "";
                    string baseNextSourceId = "";
                    string baseNextRow = "";

                    if (corrected is not null)
                    {
                        mode =
                            corrected.Mode.ToString();
                        baseUsed =
                            corrected.BaseUsedNt;
                        diurnalDifference =
                            corrected.DiurnalDifferenceNt;
                        datumAdjusted =
                            corrected.DatumAdjustedNt;
                        baseGap =
                            corrected.BaseGapSeconds;
                        basePrevSourceId =
                            sourceCatalog.GetSourceIdText(
                                corrected.BasePreviousSourceFile);
                        basePrevRow =
                            corrected.BasePreviousSourceRow?.ToString(
                                CultureInfo.InvariantCulture) ??
                            "";
                        baseNextSourceId =
                            sourceCatalog.GetSourceIdText(
                                corrected.BaseNextSourceFile);
                        baseNextRow =
                            corrected.BaseNextSourceRow?.ToString(
                                CultureInfo.InvariantCulture) ??
                            "";
                        flags.AddRange(
                            corrected.QualityFlags);
                    }
                    else if (!row.MagneticFieldNt.HasValue)
                    {
                        flags.Add(
                            "NO_MAG_VALUE");
                    }
                    else if (row.SensorStatus.Equals(
                                 "F",
                                 StringComparison.OrdinalIgnoreCase))
                    {
                        flags.Add(
                            "ROVER_SENSOR_F");
                    }
                    else
                    {
                        flags.Add(
                            "NOT_CORRECTED");
                    }

                    writer.WriteLine(
                        string.Join(
                            ",",
                            Csv(UtcDate(row.TimestampUtc)),
                            Csv(UtcTime(row.TimestampUtc)),
                            Number(
                                row.Longitude,
                                "0.########"),
                            Number(
                                row.Latitude,
                                "0.########"),
                            Number(
                                row.AltitudeM,
                                "0.###"),
                            Number(
                                row.LaserAltitudeM,
                                "0.###"),
                            Number(
                                row.MagneticFieldNt,
                                "0.###"),
                            Number(
                                baseUsed,
                                "0.###"),
                            Number(
                                diurnalDifference,
                                "0.###"),
                            options.DatumNt.ToString(
                                "0.###",
                                CultureInfo.InvariantCulture),
                            Number(
                                datumAdjusted,
                                "0.###"),
                            Csv(mode),
                            Number(
                                baseGap,
                                "0.###"),
                            Csv(string.Join(
                                ";",
                                flags
                                    .Where(x =>
                                        !string.IsNullOrWhiteSpace(x))
                                    .Distinct(
                                        StringComparer.OrdinalIgnoreCase))),
                            Csv(row.GpsStatus),
                            row.Satellites?.ToString(
                                CultureInfo.InvariantCulture) ??
                            "",
                            Csv(row.SensorStatus),
                            sourceCatalog
                                .GetSourceId(row.SourceFile)
                                .ToString(
                                    CultureInfo.InvariantCulture),
                            row.SourceRecord.ToString(
                                CultureInfo.InvariantCulture),
                            basePrevSourceId,
                            basePrevRow,
                            baseNextSourceId,
                            baseNextRow));
                }
            });
    }

    private static void WriteDaySummaryCsv(
        string path,
        IReadOnlyList<DiurnalDaySummary> days)
    {
        using var writer =
            new StreamWriter(
                path,
                false,
                new UTF8Encoding(true));
        writer.WriteLine(
            "UTC日期,Rover记录,有效Base记录,Exact,Interpolated,Uncorrected,重复Base时间键,时间重叠,状态,QC");

        foreach (DiurnalDaySummary day in days)
        {
            writer.WriteLine(
                string.Join(
                    ",",
                    Csv(day.UtcDate.ToString(
                        "yyyy-MM-dd",
                        CultureInfo.InvariantCulture)),
                    day.RoverCount.ToString(
                        CultureInfo.InvariantCulture),
                    day.BaseCount.ToString(
                        CultureInfo.InvariantCulture),
                    day.ExactCount.ToString(
                        CultureInfo.InvariantCulture),
                    day.InterpolatedCount.ToString(
                        CultureInfo.InvariantCulture),
                    day.UncorrectedCount.ToString(
                        CultureInfo.InvariantCulture),
                    day.DuplicateBaseTimeKeyCount.ToString(
                        CultureInfo.InvariantCulture),
                    day.HasTimeOverlap
                        ? "YES"
                        : "NO",
                    Csv(day.Status),
                    Csv(string.Join(
                        ";",
                        day.QualityFlags))));
        }
    }

    private static void WriteQualityMarkdown(
        string path,
        FieldMagFolderAnalysis analysis,
        DiurnalCorrectionResult correction,
        int eligible,
        int excluded,
        DiurnalBaseValiditySummary baseValidity,
        MagneticPreLevelProcessingSummary preLevel,
        MagneticSurveyLineSegmentationResult lineSegmentation,
        MagneticRepeatReplacementResult repeatReplacement)
    {
        var sb =
            new StringBuilder();
        sb.AppendLine(
                "# MagOps 原生日变改正 QC")
            .AppendLine()
            .AppendLine(
                $"算法版本：`{DiurnalCorrectionEngine.AlgorithmVersion}`")
            .AppendLine(
                $"模式：`{(correction.Options.RejectInterpolationAcrossLargeGap ? "MagOps Production Safe" : correction.Options.GemLinkTenthsCompatibility ? "GEMLink Compatible" : "Native High Precision")}`")
            .AppendLine(
                $"Datum（仅可选参考总场）：`{correction.Options.DatumNt:0.###} nT`")
            .AppendLine(
                $"UAV 整合记录：{analysis.UavSamples.Count}；可参与日变：{eligible}；因状态/缺磁值保留但不参与：{excluded}")
            .AppendLine(
                $"GSM-19T 整合记录：{analysis.BaseSamples.Count}；生产门控可用：{baseValidity.AcceptedRecords}；排除：{baseValidity.RejectedRecords}；无效 session：{baseValidity.RejectedSessions}")
            .AppendLine(
                $"有效 Base 中位数（诊断统计，不自动作为 Datum）：{(baseValidity.AcceptedMedianFieldNt.HasValue ? baseValidity.AcceptedMedianFieldNt.Value.ToString("0.###", CultureInfo.InvariantCulture) + " nT" : "不可用")}")
            .AppendLine(
                $"Exact：{correction.ExactCount}；Interpolated：{correction.InterpolatedCount}；未改正（可参与样本）：{correction.UncorrectedCount}")
            .AppendLine()
            .AppendLine(
                "## Base Validity Gate")
            .AppendLine()
            .AppendLine(
                $"- 物理磁值范围：{DiurnalBaseValidityGate.MinPhysicalFieldNt:0}–{DiurnalBaseValidityGate.MaxPhysicalFieldNt:0} nT。")
            .AppendLine(
                $"- SQ：存在时必须位于 {DiurnalBaseValidityGate.MinSq:0}–{DiurnalBaseValidityGate.MaxSq:0}；低 SQ 不进入生产插值。")
            .AppendLine(
                $"- 同 UTC 日稳健离群阈值：max({DiurnalBaseValidityGate.DayOutlierFloorNt:0} nT, {DiurnalBaseValidityGate.DayOutlierMadMultiplier:0} × MAD)。")
            .AppendLine(
                $"- 来源文件/UTC 日 session 若无有效样本、坏值比例 ≥ {DiurnalBaseValidityGate.SessionInvalidFraction:P0}，或 session 中位数偏离当日主体 > {DiurnalBaseValidityGate.SessionMedianDeviationNt:0} nT，则整段仅留档、不参与生产日变。")
            .AppendLine(
                $"- 排除原因统计：{(baseValidity.RejectionCounts.Count == 0 ? "无" : string.Join("；", baseValidity.RejectionCounts.Select(x => $"{x.Key}={x.Value}")))}")
            .AppendLine()
            .AppendLine(
                "## 时间与分组")
            .AppendLine()
            .AppendLine(
                "- UAV 与 GSM-19T 各保持一个完整整合数据集，不按日期物理拆分；完整表使用 `.csv.gz` 无损压缩。")
            .AppendLine(
                "- 数学计算内部按 **UTC Date** 分组，不允许跨 UTC 日插值。")
            .AppendLine(
                "- UTC+8 只用于外业业务作业日、进度和简报，不参与日变数学分组。")
            .AppendLine(
                "- 完整日变表使用 `SourceId + SourceRecord` 追溯，完整路径只在 manifest 保存一次；处理用 CSV 不携带审计冗余列。")
            .AppendLine(
                "- 没有时间/空间降采样，也没有降低磁值或坐标写出精度。")
            .AppendLine(
                "- GEMLink Compatible 模式将时间键截断到 0.1 秒。")
            .AppendLine()
            .AppendLine(
                "## 测线分线与干扰段剔除")
            .AppendLine()
            .AppendLine(
                lineSegmentation.Options.Enabled
                    ? $"- 已启用 `{MagneticSurveyLineSegmentationService.AlgorithmVersion}`：计划测线 {lineSegmentation.Summary.PlannedLineCount} 条；坐标记录 {lineSegmentation.Summary.CoordinateRecords}；保留正式测线 {lineSegmentation.Summary.SurveyLineRecords}；剔除起飞至首线 {lineSegmentation.Summary.TransitToFirstRecords}、线间连接/转弯 {lineSegmentation.Summary.ConnectorRecords}、末线后返航 {lineSegmentation.Summary.ReturnRecords}、未形成稳定测线段 {lineSegmentation.Summary.UnmatchedRecords}。"
                    : "- 当前未启用测线分线；处理 CSV 保持旧行为，不按航线几何剔除转场/连接段。")
            .AppendLine(
                lineSegmentation.Options.Enabled
                    ? "- 分线只影响轻量调平处理 CSV；原始 UAV、canonical、完整日变审计数据全部保留，不删除、不降采样。`磁测分线_记录分类.csv.gz` 保存逐记录分类，`磁测分线_摘要.csv` 保存类别与测线统计。"
                    : "- 如需剔除起飞转场、线间连接和返航磁数据，应在外业管理中启用“仅保留正式测线”。")
            .AppendLine()
            .AppendLine(
                "## 重测段替换 / 生产数据选择")
            .AppendLine()
            .AppendLine(
                repeatReplacement.ActiveRuleCount > 0
                    ? $"- 已启用 `{MagneticRepeatReplacementService.AlgorithmVersion}`：规则 {repeatReplacement.ActiveRuleCount} 条；原测剔除 {repeatReplacement.ExcludedOriginalRecords} 条；重测选入 {repeatReplacement.SelectedReplacementRecords} 条；所选重测 pass 在替换区段外主动抑制 {repeatReplacement.SuppressedReplacementRecords} 条，避免与原测重复进入 Oasis。"
                    : "- 当前没有启用重测替换规则；正式处理 CSV 直接使用测线分线后的生产数据。")
            .AppendLine(
                "- 原始 UAV、canonical、完整日变审计数据永不因重测替换而删除或改写；规则只决定 `MagOps_日变磁测_处理用.csv` 的生产数据选择。区段以正式测线 `AlongM`（沿线距离）定义，不按 CSV 行号拼接。")
            .AppendLine(
                "- 每条启用规则在导出前必须通过同测线、后续测量、有效日变记录、端点覆盖和内部缺口检查；失败时阻止生成正式 Oasis CSV。")
            .AppendLine()
            .AppendLine(
                "## 数学与处理语义")
            .AppendLine()
            .AppendLine(
                "- 主日变通道：`rigai / DiurnalDifferenceNt = Rover - BaseUsed`。这是后续 Oasis/调平处理使用的日变差值，默认不加 Datum。")
            .AppendLine(
                "- 可选参考总场：`DatumAdjustedNt = rigai + Datum`。Datum 与 T0 分离，不能把 Base 中位数自动当作 T0。")
            .AppendLine(
                $"- IGRF 批量输出：`Tof / Inc / Dec`；使用 `DateUtc + Longitude + Latitude + AltitudeM`，系数 SHA-256 `{preLevel.IgrfCoefficientSha256 ?? "未知"}`。")
            .AppendLine(
                $"- 调平 XY：处理用 CSV 固定 `X=Easting / Y=Northing`（m），`{preLevel.ProjectionDatum}` Gauss-Kruger 3°；中央经线 `{(preLevel.ProjectionCentralMeridianDeg.HasValue ? preLevel.ProjectionCentralMeridianDeg.Value.ToString("0.###", CultureInfo.InvariantCulture) + "°" : "不可用")}`，来源 `{preLevel.ProjectionCentralMeridianSource}`；假东 `{preLevel.ProjectionFalseEastingM:0.###} m`，假北 `{preLevel.ProjectionFalseNorthingM:0.###} m`。")
            .AppendLine(
                $"- 调平输入：`dTgz = rigai + T0 - Tof`；T0 模式 `{preLevel.T0Mode}`；固定 T0 `{(preLevel.FixedT0Nt.HasValue ? preLevel.FixedT0Nt.Value.ToString("0.###", CultureInfo.InvariantCulture) + " nT" : "不适用")}`；逐日/有效 T0 范围 `{(preLevel.T0MinNt.HasValue ? preLevel.T0MinNt.Value.ToString("0.###", CultureInfo.InvariantCulture) : "不可用")}–{(preLevel.T0MaxNt.HasValue ? preLevel.T0MaxNt.Value.ToString("0.###", CultureInfo.InvariantCulture) : "不可用")} nT`；全体 dTgz 均值 `{(preLevel.DTgzMeanNt.HasValue ? preLevel.DTgzMeanNt.Value.ToString("0.###", CultureInfo.InvariantCulture) : "不可用")} nT`。")
            .AppendLine(
                "- 新项目默认 `AUTO_GLOBAL_MEAN_CENTERED`：对当前生产选择后的全部有效记录求一个项目级 T0；`AUTO_BY_UTC_DATE` 仅用于复现旧成果的逐 UTC 日中心化；`MANUAL_FIXED` 使用人工固定 T0。")
            .AppendLine(
                "- `调平_T0_UTC日期摘要.csv` 记录每个 UTC 日的 T0、IGRF/处理记录数及 dTgz 均值/极值，用于检查日间基线。")
            .AppendLine()
            .AppendLine(
                "无同刻 Base 时，在同一 UTC 日内使用前后 Base 做线性插值。")
            .AppendLine();

        if (correction.Options.MaxInterpolationGapSeconds.HasValue)
        {
            sb.AppendLine(
                correction.Options.RejectInterpolationAcrossLargeGap
                    ? $"Base 前后间隔超过 {correction.Options.MaxInterpolationGapSeconds.Value:0.###} 秒时，生产模式拒绝插值并标记 `BASE_GAP_TOO_LARGE`；不再生成伪改正值。"
                    : $"Base 前后间隔超过 {correction.Options.MaxInterpolationGapSeconds.Value:0.###} 秒仍保留兼容计算，但标记 `BASE_GAP_LARGE`。")
              .AppendLine();
        }

        sb.AppendLine(
                "## UTC 日期摘要")
            .AppendLine();

        foreach (DiurnalDaySummary day in
                 correction.Days)
        {
            sb.AppendLine(
                $"- **{day.UtcDate:yyyy-MM-dd}** — Rover {day.RoverCount}；Base {day.BaseCount}；Exact {day.ExactCount}；Interpolated {day.InterpolatedCount}；Uncorrected {day.UncorrectedCount}；状态 `{day.Status}`" +
                (day.QualityFlags.Count == 0
                    ? ""
                    : $"；QC `{string.Join(",", day.QualityFlags)}`"));
        }

        sb.AppendLine()
            .AppendLine(
                "## 尚待 GEMLink 5.4.8.1 黄金测试确认")
            .AppendLine()
            .AppendLine(
                "- 重复 Base 0.1 秒时间键的厂家实际选择规则；当前 MagOps 保守拒绝该 UTC 日，避免猜测。")
            .AppendLine(
                "- Rover 超出 Base 范围时 GEMLink 原文件行/占位符的逐字符输出；MagOps 当前保留完整 Rover 行，并让 DiurnalDifferenceNt / DatumAdjustedNt 保持为空。")
            .AppendLine(
                "- GEMLink 专有文本列布局、TakeFormat 全部分支和 NoValue 文本行为。")
            .AppendLine()
            .AppendLine(
                "这些未知项不影响已经确认的数值公式、同日线性插值、0.1 秒兼容时间键和 MagOps 原生可追溯输出。");

        File.WriteAllText(
            path,
            sb.ToString(),
            new UTF8Encoding(true));
    }

    private static string SampleKey(
        string sourceFile,
        int sourceRow) =>
        $"{sourceFile}\u001f{sourceRow.ToString(CultureInfo.InvariantCulture)}";

    private static string UtcDate(
        DateTimeOffset value) =>
        value.ToUniversalTime()
            .ToString(
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture);

    private static string UtcTime(
        DateTimeOffset value) =>
        value.ToUniversalTime()
            .ToString(
                "HH:mm:ss.fff",
                CultureInfo.InvariantCulture);

    private static string Utc(
        DateTimeOffset value) =>
        value.ToUniversalTime()
            .ToString(
                "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
                CultureInfo.InvariantCulture);

    private static string Utc(
        DateTimeOffset? value) =>
        value.HasValue
            ? Utc(value.Value)
            : "";

    private static string Number(
        double? value,
        string format) =>
        value.HasValue
            ? value.Value.ToString(
                format,
                CultureInfo.InvariantCulture)
            : "";

    private static string Csv(
        string value)
    {
        string text =
            value ?? "";
        return text.IndexOfAny(
                [',', '"', '\r', '\n']) >= 0
            ? "\"" +
              text.Replace(
                  "\"",
                  "\"\"") +
              "\""
            : text;
    }
}
