using MagOps.Core.Domain;
using MagOps.Infrastructure.Services;
using MagOps.Infrastructure.Storage;

namespace MagOps.Application;

public sealed record ProductionRunArtifactSummary(
    string ArtifactId,
    string Kind,
    string ProcessingStage,
    string RelativePath,
    IReadOnlyList<string> SourceFileStableIds,
    IReadOnlyList<string> GsmSessionStableIds,
    IReadOnlyList<string> SurveyTaskStableIds,
    IReadOnlyList<string> SurveyLineStableIds,
    IReadOnlyList<string> ExecutionSegmentStableIds);

public sealed record ProductionRunSummary(
    string ProcessingRunId,
    string RunKind,
    string Status,
    string ProjectId,
    string WorkareaId,
    string StartedAt,
    string CompletedAt,
    string FailureCode,
    string FailureMessage,
    IReadOnlyList<ProductionRunArtifactSummary> Artifacts);

public sealed record ProductionOverview(
    string ProjectId,
    int RunCount,
    int CompletedRunCount,
    int FailedRunCount,
    int StartedRunCount,
    int ArtifactCount,
    IReadOnlyList<ProductionRunSummary> Runs);

/// <summary>
/// Read-only operational projection over persisted production facts.
/// It never re-runs Route, magnetic, Audit Production Selection or QC logic.
/// </summary>
public sealed class OperationalQueries
{
    private readonly WorkspaceService _workspace;
    private readonly ArtifactService _artifacts;
    private readonly ProcessingRunService _runs;

    public OperationalQueries(JsonStore store)
    {
        _workspace = new WorkspaceService(store);
        _artifacts = new ArtifactService(store, _workspace);
        _runs = new ProcessingRunService(store, _workspace, _artifacts);
    }

    public ApplicationResult<ProductionOverview> GetProductionOverview(
        ApplicationRequest request)
    {
        string operationId = $"op-{Guid.NewGuid():N}";
        try
        {
            string root = Path.GetFullPath(request.ProjectRoot);
            WorkspaceRecord workspace = _workspace.GetWorkspaceReadOnly(root);
            List<WorkareaRecord> workareas = _workspace.GetWorkareas(root);
            string workareaId = ResolveWorkareaId(workareas, request.WorkareaId);
            List<ProcessingRunRecord> runs = _runs.List(root);
            if (!string.IsNullOrWhiteSpace(workareaId))
                runs = runs.Where(x => x.WorkareaId.Equals(
                    workareaId, StringComparison.OrdinalIgnoreCase)).ToList();

            List<ArtifactRecord> artifacts = _artifacts.List(root);
            var summaries = runs
                .OrderByDescending(x => x.StartedAt, StringComparer.Ordinal)
                .Select(run => ToSummary(run, artifacts, runs))
                .ToList();
            var context = new ApplicationContext(
                root, workspace.ProjectId, workareaId, workspace.UpdatedAt);
            return ApplicationResult<ProductionOverview>.Success(
                operationId, context,
                new ProductionOverview(
                    workspace.ProjectId,
                    summaries.Count,
                    summaries.Count(x => x.Status.Equals("COMPLETED",
                        StringComparison.OrdinalIgnoreCase)),
                    summaries.Count(x => x.Status.Equals("FAILED",
                        StringComparison.OrdinalIgnoreCase)),
                    summaries.Count(x => x.Status.Equals("STARTED",
                        StringComparison.OrdinalIgnoreCase)),
                    runs.SelectMany(x => x.ArtifactIds)
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    summaries));
        }
        catch (InvalidDataException ex)
        {
            return ApplicationResult<ProductionOverview>.Failure(
                operationId,
                new ApplicationError(ApplicationErrorCodes.ValidationError, ex.Message));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return ApplicationResult<ProductionOverview>.Failure(
                operationId,
                new ApplicationError(ApplicationErrorCodes.EnvironmentError, ex.Message));
        }
    }

    public ApplicationResult<ProductionRunSummary> GetProductionRun(
        ApplicationRequest request,
        string processingRunId)
    {
        string operationId = $"op-{Guid.NewGuid():N}";
        try
        {
            if (string.IsNullOrWhiteSpace(processingRunId))
                throw new InvalidDataException("ProcessingRunId is required.");
            string root = Path.GetFullPath(request.ProjectRoot);
            WorkspaceRecord workspace = _workspace.GetWorkspaceReadOnly(root);
            List<WorkareaRecord> workareas = _workspace.GetWorkareas(root);
            string workareaId = ResolveWorkareaId(workareas, request.WorkareaId);
            List<ProcessingRunRecord> runs = _runs.List(root);
            ProcessingRunRecord[] matches = runs
                .Where(x => x.ProcessingRunId.Equals(
                    processingRunId, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (matches.Length != 1)
                throw new InvalidDataException(
                    $"ProcessingRunId must resolve exactly once: {processingRunId}");
            ProcessingRunRecord run = matches[0];
            if (!string.IsNullOrWhiteSpace(workareaId) &&
                !run.WorkareaId.Equals(workareaId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "ProcessingRun does not belong to requested WorkareaId.");

            var context = new ApplicationContext(
                root, workspace.ProjectId, run.WorkareaId, workspace.UpdatedAt);
            return ApplicationResult<ProductionRunSummary>.Success(
                operationId, context, ToSummary(run, _artifacts.List(root), runs));
        }
        catch (InvalidDataException ex)
        {
            return ApplicationResult<ProductionRunSummary>.Failure(
                operationId,
                new ApplicationError(ApplicationErrorCodes.ValidationError, ex.Message));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return ApplicationResult<ProductionRunSummary>.Failure(
                operationId,
                new ApplicationError(ApplicationErrorCodes.EnvironmentError, ex.Message));
        }
    }

    private static ProductionRunSummary ToSummary(
        ProcessingRunRecord run,
        IReadOnlyList<ArtifactRecord> artifacts,
        IReadOnlyList<ProcessingRunRecord> runs)
    {
        var byId = artifacts.Where(x => !string.IsNullOrWhiteSpace(x.ArtifactId))
            .GroupBy(x => x.ArtifactId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.ToArray(), StringComparer.OrdinalIgnoreCase);
        var rows = new List<ProductionRunArtifactSummary>();
        foreach (string artifactId in run.ArtifactIds)
        {
            if (!byId.TryGetValue(artifactId, out ArtifactRecord[]? matches) ||
                matches.Length != 1)
                throw new InvalidDataException(
                    $"ProcessingRun lineage is broken for ArtifactId: {artifactId}");
            ArtifactRecord artifact = matches[0];
            if (!string.IsNullOrWhiteSpace(artifact.ProcessingRunId))
            {
                ProcessingRunRecord? backlinkRun = runs.SingleOrDefault(x =>
                    x.ProcessingRunId.Equals(
                        artifact.ProcessingRunId, StringComparison.OrdinalIgnoreCase));
                if (backlinkRun is null ||
                    !backlinkRun.Status.Equals("COMPLETED", StringComparison.OrdinalIgnoreCase) ||
                    !backlinkRun.ArtifactIds.Contains(
                        artifactId, StringComparer.OrdinalIgnoreCase))
                    throw new InvalidDataException(
                        $"Artifact/ProcessingRun backlink mismatch: {artifactId}");
            }
            rows.Add(new ProductionRunArtifactSummary(
                artifact.ArtifactId,
                artifact.Role,
                artifact.ProcessingStage,
                artifact.Path,
                artifact.SourceFileStableIds,
                artifact.GsmSessionStableIds,
                artifact.SurveyTaskStableIds,
                artifact.SurveyLineStableIds,
                artifact.ExecutionSegmentStableIds));
        }
        return new ProductionRunSummary(
            run.ProcessingRunId,
            run.RunKind,
            run.Status,
            run.ProjectId,
            run.WorkareaId,
            run.StartedAt,
            run.CompletedAt,
            run.FailureCode,
            run.FailureMessage,
            rows);
    }

    private static string ResolveWorkareaId(
        IReadOnlyList<WorkareaRecord> workareas,
        string? selector)
    {
        if (string.IsNullOrWhiteSpace(selector)) return "";
        WorkareaIdentityResolution resolution =
            WorkareaIdentityResolver.Resolve(workareas, selector);
        if (!resolution.Found || resolution.UsedCompatibilityAlias ||
            resolution.MatchKind == WorkareaIdentityMatchKind.Ambiguous)
            throw new InvalidDataException(
                "Operational queries require canonical WorkareaId.");
        return resolution.Workarea!.WorkareaId;
    }
}
