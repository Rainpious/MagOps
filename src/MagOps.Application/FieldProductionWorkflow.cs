using MagOps.Core.Diurnal;
using MagOps.Core.Domain;
using MagOps.Core.Geomagnetics;
using MagOps.Infrastructure.Services;

namespace MagOps.Application;

public sealed record FieldProductionRequest(
    string ProjectRoot,
    string OutputDirectory,
    FieldMagFolderAnalysis Analysis,
    DiurnalCorrectionOptions DiurnalOptions,
    IgrfModelSet IgrfModels,
    MagneticPreLevelProcessingOptions PreLevelOptions,
    IReadOnlyList<PlannedSurveyLineTask> SurveyLines,
    MagneticSurveyLineSegmentationOptions SegmentationOptions,
    string SurveyLineSource,
    string SurveyLineFingerprint,
    AuditProductionRequest AuditRequest,
    GeneratedArtifactContext ArtifactContext,
    IReadOnlyList<MagneticRepeatReplacementRule>? LegacyRepeatReplacementRules = null,
    string ArtifactSourceKind = "processing_diurnal")
{
    /// <summary>Optional live review service. Reuse is fingerprint-validated;
    /// production choices and anomaly exclusions are always checked again.</summary>
    public AuditProductionService? ReviewSession { get; init; }
    public ReviewAnalysisSession? CalculationSession { get; init; }
}

public sealed record FieldProductionWorkflowResult(
    DiurnalCorrectionExportResult Export,
    IReadOnlyList<ArtifactRecord> RegisteredArtifacts,
    ProcessingRunRecord ProcessingRun);

/// <summary>
/// Application-layer orchestration for the formal field magnetic production path.
/// AuditProductionService remains the single production-selection authority.
/// UI code supplies user choices and display-only concerns; this workflow owns
/// the deterministic service sequence and artifact registration.
/// </summary>
public sealed class FieldProductionWorkflow
{
    private readonly AuditProductionService _auditProduction;
    private readonly IFieldProductionOutput _diurnalExport;
    private readonly IFieldProductionArtifacts _artifacts;
    private readonly IFieldProductionRuns _processingRuns;

    public FieldProductionWorkflow(
        AuditProductionService auditProduction,
        DiurnalCorrectionExportService diurnalExport,
        ArtifactService artifacts,
        ProcessingRunService processingRuns)
        : this(auditProduction, new FieldProductionOutputAdapter(diurnalExport),
            new FieldProductionArtifactsAdapter(artifacts), new FieldProductionRunsAdapter(processingRuns))
    {
    }

    public FieldProductionWorkflow(
        AuditProductionService auditProduction,
        IFieldProductionOutput diurnalExport,
        IFieldProductionArtifacts artifacts,
        IFieldProductionRuns processingRuns)
    {
        _auditProduction = auditProduction ?? throw new ArgumentNullException(nameof(auditProduction));
        _diurnalExport = diurnalExport ?? throw new ArgumentNullException(nameof(diurnalExport));
        _artifacts = artifacts ?? throw new ArgumentNullException(nameof(artifacts));
        _processingRuns = processingRuns ?? throw new ArgumentNullException(nameof(processingRuns));
    }

    public FieldProductionWorkflowResult Execute(FieldProductionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);
        _artifacts.ValidateGeneratedArtifactContext(
            request.ProjectRoot,
            request.ArtifactContext);
        ProcessingRunRecord run = _processingRuns.Begin(
            request.ProjectRoot, "field_production", request.ArtifactContext);

        try
        {
            DiurnalBaseValiditySummary baseValidity =
                DiurnalBaseValidityGate.Evaluate(request.Analysis.BaseSamples);
            if (baseValidity.AcceptedRecords == 0)
                throw new InvalidOperationException(
                    "PRODUCTION_GATE_BLOCKED: 当前项目没有通过生产 Base 有效性门控的 GSM-19T 样本。");

            AuditProductionResult audit = request.CalculationSession is { } session
                ? session.AnalyzeForProduction(request.Analysis, request.SurveyLines, request.AuditRequest,
                    request.SurveyLineFingerprint)
                : request.ReviewSession is { } review
                ? review.Analyze(request.Analysis, request.SurveyLines, request.AuditRequest,
                    request.SurveyLineFingerprint, reuseRecognition: true)
                : _auditProduction.Analyze(request.Analysis, request.SurveyLines,
                    request.AuditRequest, request.SurveyLineFingerprint);

            DiurnalCorrectionExportResult export =
                _diurnalExport.Write(request, audit);

            IReadOnlyList<ArtifactRecord> registered =
                RegisterGeneratedArtifacts(
                    request.ProjectRoot,
                    export.Files,
                    request.ArtifactSourceKind);
            ProcessingRunRecord completed = _processingRuns.Complete(
                request.ProjectRoot,
                run.ProcessingRunId,
                registered.Select(x => x.ArtifactId));
            return new FieldProductionWorkflowResult(export, registered, completed);
        }
        catch (Exception ex)
        {
            try
            {
                _processingRuns.Fail(request.ProjectRoot, run.ProcessingRunId, ex);
            }
            catch (Exception failureRecordError)
            {
                // Preserve the production cause if storage also rejects its failure
                // record. Never claim this run was marked FAILED successfully.
                throw new AggregateException(
                    $"生产失败，且无法保存失败状态（ProcessingRunId={run.ProcessingRunId}）。请检查成果与处理记录。",
                    ex, failureRecordError);
            }
            throw;
        }
    }

    private IReadOnlyList<ArtifactRecord> RegisterGeneratedArtifacts(
        string projectRoot,
        IEnumerable<string> paths,
        string sourceKind)
    {
        string root = Path.GetFullPath(projectRoot);
        string prefix = root.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var rows = new List<ArtifactRecord>();

        foreach (string raw in paths
                     .Where(x => !string.IsNullOrWhiteSpace(x))
                     .Where(x => !x.EndsWith(GeneratedArtifactIdentity.Suffix, StringComparison.OrdinalIgnoreCase))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string full = Path.GetFullPath(raw);
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"正式生产成果越出项目目录，禁止登记：{full}");
            if (!File.Exists(full))
                throw new FileNotFoundException(
                    "正式生产成果缺失，禁止登记。", full);

            rows.Add(_artifacts.RegisterGeneratedFile(root, full, sourceKind));
        }

        return rows;
    }

    private static void Validate(FieldProductionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ProjectRoot))
            throw new ArgumentException("ProjectRoot 不能为空。", nameof(request));
        if (string.IsNullOrWhiteSpace(request.OutputDirectory))
            throw new ArgumentException("OutputDirectory 不能为空。", nameof(request));

        string root = Path.GetFullPath(request.ProjectRoot);
        string output = Path.GetFullPath(request.OutputDirectory);
        string prefix = root.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!output.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"正式生产输出目录必须位于当前项目内：{output}");
        if (request.Analysis is null)
            throw new ArgumentException("Analysis 不能为空。", nameof(request));
        if (request.IgrfModels is null)
            throw new ArgumentException("IgrfModels 不能为空。", nameof(request));
        if (request.PreLevelOptions is null)
            throw new ArgumentException("PreLevelOptions 不能为空。", nameof(request));
        if (request.AuditRequest is null)
            throw new ArgumentException("AuditRequest 不能为空。", nameof(request));
        if (request.ArtifactContext is null)
            throw new ArgumentException("ArtifactContext 不能为空。", nameof(request));
        if (!request.ArtifactContext.ContextStatus.Equals(
                "WORKAREA", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "正式外业磁测生产必须绑定 canonical ProjectId + WorkareaId。");
        if (request.SurveyLines is null)
            throw new ArgumentException("SurveyLines 不能为空。", nameof(request));
        if (request.SegmentationOptions is null)
            throw new ArgumentException("SegmentationOptions 不能为空。", nameof(request));
    }
}
