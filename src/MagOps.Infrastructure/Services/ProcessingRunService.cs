using MagOps.Core.Domain;
using MagOps.Infrastructure.Storage;

namespace MagOps.Infrastructure.Services;

/// <summary>
/// Audit identity for one formal managed production execution. A run does not
/// replace domain identities; it groups the exact artifacts produced together.
/// </summary>
public sealed class ProcessingRunService
{
    private readonly JsonStore _store;
    private readonly WorkspaceService _workspace;
    private readonly ArtifactService _artifacts;

    public ProcessingRunService(
        JsonStore store,
        WorkspaceService workspace,
        ArtifactService artifacts)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _artifacts = artifacts ?? throw new ArgumentNullException(nameof(artifacts));
    }

    public List<ProcessingRunRecord> List(string root) =>
        _store.Read(ProjectPaths.Json(root, "processing_runs.json"),
            new List<ProcessingRunRecord>());

    public ProcessingRunRecord Begin(
        string root,
        string runKind,
        GeneratedArtifactContext context)
    {
        if (string.IsNullOrWhiteSpace(runKind))
            throw new ArgumentException("Processing run kind cannot be empty.", nameof(runKind));
        root = Path.GetFullPath(root);
        _artifacts.ValidateGeneratedArtifactContext(root, context);
        if (!context.ContextStatus.Equals("WORKAREA", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Formal processing run must bind canonical ProjectId + WorkareaId.");

        using var writeLock = WorkspaceFileLock.AcquireForRoot(root);
        var rows = List(root);
        var run = new ProcessingRunRecord
        {
            ProcessingRunId = "run_" + Guid.NewGuid().ToString("N"),
            ProjectId = context.ProjectId,
            WorkareaId = context.WorkareaId,
            RunKind = runKind.Trim(),
            Status = "STARTED",
            StartedAt = DateTimeOffset.UtcNow.ToString("O")
        };
        rows.Add(run);
        _store.Write(ProjectPaths.Json(root, "processing_runs.json"), rows);
        _store.AppendJsonLine(ProjectPaths.DataDirectory(root),
            "processing_run_started",
            new { processing_run_id = run.ProcessingRunId, run_kind = run.RunKind,
                project_id = run.ProjectId, workarea_id = run.WorkareaId });
        return run;
    }

    public ProcessingRunRecord Complete(
        string root,
        string processingRunId,
        IEnumerable<string> artifactIds)
    {
        root = Path.GetFullPath(root);
        using var writeLock = WorkspaceFileLock.AcquireForRoot(root);
        var runs = List(root);
        ProcessingRunRecord run = RequireStarted(runs, processingRunId);
        var ids = artifactIds.Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        if (ids.Count == 0)
            throw new InvalidOperationException("Completed processing run must contain at least one Artifact.");

        var artifacts = _artifacts.List(root);
        var byId = artifacts.Where(x => !string.IsNullOrWhiteSpace(x.ArtifactId))
            .ToDictionary(x => x.ArtifactId, StringComparer.OrdinalIgnoreCase);
        var previousBacklinks = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (string id in ids)
        {
            if (!byId.TryGetValue(id, out ArtifactRecord? artifact))
                throw new InvalidOperationException($"Processing run references unknown ArtifactId: {id}");
            if (!string.IsNullOrWhiteSpace(artifact.ProcessingRunId) &&
                !artifact.ProcessingRunId.Equals(run.ProcessingRunId, StringComparison.OrdinalIgnoreCase))
            {
                ProcessingRunRecord? previousRun = runs.SingleOrDefault(x =>
                    x.ProcessingRunId.Equals(
                        artifact.ProcessingRunId, StringComparison.OrdinalIgnoreCase));
                if (previousRun is null ||
                    !previousRun.Status.Equals("COMPLETED", StringComparison.OrdinalIgnoreCase) ||
                    !previousRun.ArtifactIds.Contains(id, StringComparer.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"Artifact backlink references a run that does not own it: {id} -> {artifact.ProcessingRunId}");
            }
            previousBacklinks[id] = artifact.ProcessingRunId;
            // Artifact backlink is a denormalized pointer to the latest successful
            // production run. Historical membership remains authoritative in each
            // completed ProcessingRun.ArtifactIds list.
            artifact.ProcessingRunId = run.ProcessingRunId;
        }

        run.ArtifactIds = ids;
        run.Status = "COMPLETED";
        run.CompletedAt = DateTimeOffset.UtcNow.ToString("O");
        _store.Write(ProjectPaths.Json(root, "artifacts.json"), artifacts);
        try
        {
            _store.Write(ProjectPaths.Json(root, "processing_runs.json"), runs);
        }
        catch (Exception commitError)
        {
            try
            {
                foreach (string id in ids)
                    byId[id].ProcessingRunId = previousBacklinks[id];
                _store.Write(ProjectPaths.Json(root, "artifacts.json"), artifacts);
            }
            catch (Exception rollbackError)
            {
                throw new InvalidOperationException(
                    "ProcessingRun commit failed and Artifact backlink rollback was incomplete; manual review required.",
                    new AggregateException(commitError, rollbackError));
            }
            throw new InvalidOperationException(
                "ProcessingRun commit failed; Artifact backlinks were rolled back.",
                commitError);
        }
        _store.AppendJsonLine(ProjectPaths.DataDirectory(root),
            "processing_run_completed",
            new { processing_run_id = run.ProcessingRunId, artifact_ids = ids });
        return run;
    }

    public ProcessingRunRecord Fail(
        string root,
        string processingRunId,
        Exception error,
        string failureCode = "PRODUCTION_FAILED")
    {
        ArgumentNullException.ThrowIfNull(error);
        root = Path.GetFullPath(root);
        using var writeLock = WorkspaceFileLock.AcquireForRoot(root);
        var runs = List(root);
        ProcessingRunRecord run = runs.Single(x =>
            x.ProcessingRunId.Equals(processingRunId, StringComparison.OrdinalIgnoreCase));
        if (run.Status.Equals("COMPLETED", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Completed processing run cannot be changed to FAILED.");
        run.Status = "FAILED";
        run.CompletedAt = DateTimeOffset.UtcNow.ToString("O");
        run.FailureCode = failureCode;
        run.FailureMessage = error.Message;
        _store.Write(ProjectPaths.Json(root, "processing_runs.json"), runs);
        _store.AppendJsonLine(ProjectPaths.DataDirectory(root),
            "processing_run_failed",
            new { processing_run_id = run.ProcessingRunId, failure_code = failureCode,
                message = error.Message });
        return run;
    }

    private static ProcessingRunRecord RequireStarted(
        IReadOnlyList<ProcessingRunRecord> rows,
        string id)
    {
        ProcessingRunRecord run = rows.Single(x =>
            x.ProcessingRunId.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (!run.Status.Equals("STARTED", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Processing run is not STARTED: {id} ({run.Status})");
        return run;
    }
}
