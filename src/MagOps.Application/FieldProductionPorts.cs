using MagOps.Core.Domain;
using MagOps.Infrastructure.Services;

namespace MagOps.Application;

// Application-owned I/O boundaries. Existing DTOs stay in their current
// namespaces until a separate compatibility migration removes that dependency.
public interface IFieldProductionArtifacts
{
    void ValidateGeneratedArtifactContext(string root, GeneratedArtifactContext context);
    ArtifactRecord RegisterGeneratedFile(string root, string fullPath, string sourceKind);
}

public interface IFieldProductionRuns
{
    ProcessingRunRecord Begin(string root, string runKind, GeneratedArtifactContext context);
    ProcessingRunRecord Complete(string root, string runId, IEnumerable<string> artifactIds);
    ProcessingRunRecord Fail(string root, string runId, Exception error);
}

public interface IFieldProductionOutput
{
    DiurnalCorrectionExportResult Write(FieldProductionRequest request, AuditProductionResult audit);
}

public sealed class FieldProductionArtifactsAdapter(ArtifactService service) : IFieldProductionArtifacts
{
    private readonly ArtifactService _service = service ?? throw new ArgumentNullException(nameof(service));
    public void ValidateGeneratedArtifactContext(string root, GeneratedArtifactContext context) =>
        _service.ValidateGeneratedArtifactContext(root, context);
    public ArtifactRecord RegisterGeneratedFile(string root, string fullPath, string sourceKind) =>
        _service.RegisterGeneratedFile(root, fullPath, sourceKind);
}

public sealed class FieldProductionRunsAdapter(ProcessingRunService service) : IFieldProductionRuns
{
    private readonly ProcessingRunService _service = service ?? throw new ArgumentNullException(nameof(service));
    public ProcessingRunRecord Begin(string root, string runKind, GeneratedArtifactContext context) =>
        _service.Begin(root, runKind, context);
    public ProcessingRunRecord Complete(string root, string runId, IEnumerable<string> artifactIds) =>
        _service.Complete(root, runId, artifactIds);
    public ProcessingRunRecord Fail(string root, string runId, Exception error) => _service.Fail(root, runId, error);
}

public sealed class FieldProductionOutputAdapter(DiurnalCorrectionExportService service) : IFieldProductionOutput
{
    private readonly DiurnalCorrectionExportService _service = service ?? throw new ArgumentNullException(nameof(service));
    public DiurnalCorrectionExportResult Write(FieldProductionRequest request, AuditProductionResult audit) =>
        _service.WriteNativeCorrection(request.OutputDirectory, request.Analysis, request.DiurnalOptions,
            request.IgrfModels, request.PreLevelOptions, audit.EffectiveTasks ?? request.SurveyLines,
            request.SegmentationOptions,
            audit.TrackSegmentation?.Enabled == true ? "TRACK_REFERENCE: " + audit.TrackSegmentation.ReferenceSource : request.SurveyLineSource,
            request.SurveyLineFingerprint, request.LegacyRepeatReplacementRules ?? [], audit,
            request.ArtifactContext, allowLegacyRepeatReviewDraft: request.AuditRequest.AllowReviewDraft);
}
