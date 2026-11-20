using MagOps.Core.Analysis;
using MagOps.Core.Domain;
using MagOps.Core.Routes;
using MagOps.Infrastructure.Services;
using MagOps.Infrastructure.Storage;

namespace MagOps.Application;

public sealed record RoutePlanningRequest(
    string SourceCsv,
    RoutePlanOptions Options,
    ConcaveSplitRouteContext? ConcaveSplitContext = null,
    string PartitionCode = "",
    string PartitionManifestPath = "");

public sealed record RouteExportRequest(
    string ProjectRoot,
    string OutputDirectory,
    string WorkareaName,
    bool ExportKml,
    bool ExportCsv,
    GeneratedArtifactContext ArtifactContext,
    string? KmlTerrainGeoTiffPath = null,
    double KmlAglM = 0d,
    bool KmlIncludeElevation = false,
    string ArtifactSourceKind = "route_export");

public sealed record RouteWorkflowExportResult(
    RouteExportBundle Bundle,
    IReadOnlyList<MagOps.Core.Domain.ArtifactRecord> RegisteredArtifacts,
    MagOps.Core.Domain.ProcessingRunRecord ProcessingRun,
    RouteManagedExportRollbackSnapshot RollbackSnapshot);

public sealed record RouteManagedExportRollbackSnapshot(
    string BundleDirectory,
    bool HadActiveBundle,
    byte[]? ArtifactsJson,
    byte[]? RouteResultsJson);

public sealed record RouteWorkflowFinalizeResult(
    string ManifestPath,
    MagOps.Core.Domain.RouteResultRecord ImportedResult,
    MagOps.Core.Domain.ArtifactRecord ManifestArtifact,
    MagOps.Core.Domain.ProcessingRunRecord ProcessingRun);

/// <summary>
/// Application orchestration for route generation, execution-segment editing,
/// project persistence and ordinary CSV/KML export. UI confirmation, file pickers,
/// preview state and presentation remain in Desktop.
/// </summary>
public sealed class RoutePlanningWorkflow
{
    private readonly RoutePlanner _planner;
    private readonly ConcaveSplitRoutePlanService _concave;
    private readonly RouteExecutionSegmentPlanner _segments;
    private readonly RouteProjectService _projects;
    private readonly RouteExportService _exporter;
    private readonly ArtifactService _artifacts;
    private readonly RouteManifestWriter _manifestWriter;
    private readonly RouteResultService _routeResults;
    private readonly ProcessingRunService _processingRuns;

    public RoutePlanningWorkflow(
        RoutePlanner planner,
        ConcaveSplitRoutePlanService concave,
        RouteExecutionSegmentPlanner segments,
        RouteProjectService projects,
        RouteExportService exporter,
        ArtifactService artifacts,
        RouteManifestWriter manifestWriter,
        RouteResultService routeResults,
        ProcessingRunService processingRuns)
    {
        _planner = planner ?? throw new ArgumentNullException(nameof(planner));
        _concave = concave ?? throw new ArgumentNullException(nameof(concave));
        _segments = segments ?? throw new ArgumentNullException(nameof(segments));
        _projects = projects ?? throw new ArgumentNullException(nameof(projects));
        _exporter = exporter ?? throw new ArgumentNullException(nameof(exporter));
        _artifacts = artifacts ?? throw new ArgumentNullException(nameof(artifacts));
        _manifestWriter = manifestWriter ?? throw new ArgumentNullException(nameof(manifestWriter));
        _routeResults = routeResults ?? throw new ArgumentNullException(nameof(routeResults));
        _processingRuns = processingRuns ?? throw new ArgumentNullException(nameof(processingRuns));
    }

    public RoutePlanResult Generate(RoutePlanningRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!File.Exists(request.SourceCsv))
            throw new FileNotFoundException("航线端点 CSV 不存在。", request.SourceCsv);
        RoutePlanResult plan = _planner.BuildFromCsv(request.SourceCsv, request.Options);
        if (request.ConcaveSplitContext is not null)
            plan = _concave.Apply(plan, request.ConcaveSplitContext).Plan;
        plan.PartitionCode = request.PartitionCode?.Trim() ?? "";
        plan.PartitionManifestPath = request.PartitionManifestPath?.Trim() ?? "";
        for (int i = 0; i < plan.Sorties.Count; i++)
            plan.Sorties[i].Name = plan.PartitionCode.Length > 0
                ? $"{plan.PartitionCode}-A{i + 1:D2}" : $"A{i + 1:D2}";
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (request.ConcaveSplitContext is { } split)
        {
            foreach (var group in split.SplitGroups)
                foreach (string output in group.OutputLines) aliases.Add(output, group.SourceLine);
            foreach (var pair in split.SplitPairs)
            {
                aliases.TryAdd(pair.FirstOutputLine, pair.SourceLine);
                aliases.TryAdd(pair.SecondOutputLine, pair.SourceLine);
            }
        }
        PartitionMotherLineIdentityService.BindGeneratedPlan(plan, aliases);
        return plan;
    }

    public RouteExecutionSegmentPlan ApplyCutLine(
        RoutePlanResult plan, IEnumerable<string> targets,
        double x1, double y1, double x2, double y2)
    {
        var previous = plan.Sorties.ToArray();
        var result = _segments.ApplyCutLineToLines(plan, targets, x1, y1, x2, y2);
        RouteIdentityService.EnsureReferences(result.Plan.Sorties, previous);
        return result;
    }

    public void SaveProject(
        string path, RoutePlanResult plan, string outputDirectory,
        string headerlessOrder, string coordModeText,
        string startDirectionText, string partitionStartSideText,
        RouteSubdivisionWorkspace? subdivisionWorkspace = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        _projects.Save(path, plan, outputDirectory, headerlessOrder,
            coordModeText, startDirectionText, partitionStartSideText, subdivisionWorkspace);
    }

    public RouteProjectFile LoadProject(string path) => _projects.Load(path);

    public RouteWorkflowExportResult Export(RoutePlanResult plan, RouteExportRequest request)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(request);
        string root = Path.GetFullPath(request.ProjectRoot);
        _artifacts.ValidateGeneratedArtifactContext(root, request.ArtifactContext);
        RouteExportDirectoryLayout transactionLayout =
            _exporter.GetSeparatedOutputLayout(
                Path.GetFullPath(request.OutputDirectory), request.WorkareaName);
        RouteManagedExportRollbackSnapshot rollbackSnapshot =
            CaptureRollbackSnapshot(root, transactionLayout.BaseDirectory);
        ProcessingRunRecord run = _processingRuns.Begin(
            root, "route_export", request.ArtifactContext);
        try
        {
        string output = Path.GetFullPath(request.OutputDirectory);
        string prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                        + Path.DirectorySeparatorChar;
        if (!output.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("受管航线导出目录必须位于当前项目内。");
        if (request.ExportKml && request.KmlIncludeElevation &&
            (string.IsNullOrWhiteSpace(request.KmlTerrainGeoTiffPath) ||
             !File.Exists(request.KmlTerrainGeoTiffPath)))
            throw new FileNotFoundException("KML 高程导出需要有效 DEM。", request.KmlTerrainGeoTiffPath);

        GeneratedArtifactLineage lineage = BuildRouteLineage(plan);
        IReadOnlyList<string> plannedOutputPaths = _exporter.GetSeparatedOutputPaths(
            plan, output, request.WorkareaName,
            request.ExportKml, request.ExportCsv, exportKmz: false);
        var stableArtifactIds = plannedOutputPaths.ToDictionary(
            path => Path.GetFullPath(path),
            path => _artifacts.ResolveStableArtifactIdForGeneratedPath(root, path),
            StringComparer.OrdinalIgnoreCase);
        RouteExportDirectoryLayout finalLayout = transactionLayout;

        string sourceFingerprint = File.Exists(plan.SourcePath)
            ? GeneratedArtifactIdentity.Hash(plan.SourcePath)
            : "";
        IReadOnlyList<string> sourceFiles = string.IsNullOrWhiteSpace(plan.SourcePath)
            ? [] : [Path.GetFullPath(plan.SourcePath)];

        // KMZ remains explicitly frozen outside P1. Sidecars are written inside
        // the same atomic stage so data+identity commit as one formal bundle.
        RouteExportBundle bundle = _exporter.ExportAllSeparated(
            plan, output, request.WorkareaName,
            request.ExportKml, request.ExportCsv, exportKmz: false,
            request.KmlTerrainGeoTiffPath, request.KmlAglM, request.KmlIncludeElevation,
            stageFiles =>
            {
                foreach (string full in stageFiles)
                {
                    string parent = Path.GetDirectoryName(full)
                        ?? throw new InvalidDataException("航线 staging 成果缺少父目录。");
                    string stageRoot = finalLayout.KmlDirectory == finalLayout.BaseDirectory
                        ? parent : Path.GetDirectoryName(parent)
                        ?? throw new InvalidDataException("航线 staging 目录结构无效。");
                    string relative = Path.GetRelativePath(stageRoot, full);
                    string finalPath = Path.GetFullPath(
                        Path.Combine(finalLayout.BaseDirectory, relative));
                    if (!stableArtifactIds.TryGetValue(finalPath, out string? stableArtifactId))
                        throw new InvalidDataException($"无法解析航线成果稳定 Artifact ID：{finalPath}");
                    GeneratedArtifactIdentity.Write(
                        full,
                        role: "route",
                        stage: "route_export",
                        sourceFingerprint,
                        sourceFiles,
                        artifactId: stableArtifactId,
                        qualityStatus: plan.HasOverLimitSorties ? "REVIEW_REQUIRED" : "UNREVIEWED",
                        context: request.ArtifactContext,
                        lineage: lineage);
                    if (GeneratedArtifactIdentity.Inspect(full).Status != "VERIFIED")
                        throw new InvalidDataException($"航线 staging 身份 sidecar 验证失败：{relative}");
                }
            });

        var rows = new List<MagOps.Core.Domain.ArtifactRecord>();
        foreach (string file in bundle.OutputPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string full = Path.GetFullPath(file);
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
                throw new InvalidOperationException($"航线成果未在项目内完整提交：{full}");
            if (GeneratedArtifactIdentity.Inspect(full).Status != "VERIFIED")
                throw new InvalidDataException($"航线成果身份 sidecar 未随原子 bundle 完整提交：{full}");
            rows.Add(_artifacts.RegisterGeneratedFile(root, full, request.ArtifactSourceKind));
        }
        return new RouteWorkflowExportResult(bundle, rows, run, rollbackSnapshot);
        }
        catch (Exception ex)
        {
            _processingRuns.Fail(root, run.ProcessingRunId, ex);
            throw;
        }
    }
    public RouteWorkflowFinalizeResult FinalizeManagedExport(
        RoutePlanResult plan,
        RouteWorkflowExportResult export,
        string projectRoot,
        string manifestPath,
        GeneratedArtifactContext context)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(export);
        string root = Path.GetFullPath(projectRoot);
        _artifacts.ValidateGeneratedArtifactContext(root, context);
        try
        {
            string fullManifest = Path.GetFullPath(manifestPath);
            string? stableManifestArtifactId =
                _artifacts.ResolveStableArtifactIdForGeneratedPath(root, fullManifest);
            string manifest = _manifestWriter.Write(
                fullManifest,
                plan,
                export.Bundle.OutputPaths,
                context.ProjectId,
                context.WorkareaId,
                writeArtifactIdentity: true,
                artifactId: stableManifestArtifactId);
            RouteResultRecord imported = _routeResults.Import(
                root, manifest, confirmGate: false);
            ArtifactRecord artifact = _artifacts.RegisterGeneratedFile(
                root, manifest, "route_manifest");
            ProcessingRunRecord completed = _processingRuns.Complete(
                root,
                export.ProcessingRun.ProcessingRunId,
                export.RegisteredArtifacts.Select(x => x.ArtifactId)
                    .Append(artifact.ArtifactId));
            return new RouteWorkflowFinalizeResult(
                manifest, imported, artifact, completed);
        }
        catch (Exception ex)
        {
            Exception? compensationError = null;
            try
            {
                CompensateFailedManagedExport(root, export.RollbackSnapshot);
            }
            catch (Exception rollbackEx)
            {
                compensationError = rollbackEx;
            }

            try
            {
                ProcessingRunRecord? current = _processingRuns.List(root)
                    .SingleOrDefault(x => x.ProcessingRunId.Equals(
                        export.ProcessingRun.ProcessingRunId,
                        StringComparison.OrdinalIgnoreCase));
                if (current is not null &&
                    current.Status.Equals("STARTED", StringComparison.OrdinalIgnoreCase))
                    _processingRuns.Fail(root, current.ProcessingRunId, ex);
            }
            catch (Exception failStateError)
            {
                compensationError = compensationError is null
                    ? failStateError
                    : new AggregateException(compensationError, failStateError);
            }

            if (compensationError is not null)
                throw new InvalidOperationException(
                    "Managed Route finalize failed and rollback was incomplete; manual review required.",
                    new AggregateException(ex, compensationError));
            throw;
        }
    }

    private static RouteManagedExportRollbackSnapshot CaptureRollbackSnapshot(
        string root, string bundleDirectory)
    {
        string artifacts = ProjectPaths.Json(root, "artifacts.json");
        string routeResults = ProjectPaths.Json(root, "route_results.json");
        return new RouteManagedExportRollbackSnapshot(
            Path.GetFullPath(bundleDirectory),
            Directory.Exists(Path.GetFullPath(bundleDirectory)),
            File.Exists(artifacts) ? File.ReadAllBytes(artifacts) : null,
            File.Exists(routeResults) ? File.ReadAllBytes(routeResults) : null);
    }

    private static void CompensateFailedManagedExport(
        string root, RouteManagedExportRollbackSnapshot snapshot)
    {
        using var writeLock = WorkspaceFileLock.AcquireForRoot(root);

        if (snapshot.HadActiveBundle)
            AtomicDirectoryBundle.RestorePrevious(snapshot.BundleDirectory);
        else if (Directory.Exists(snapshot.BundleDirectory))
            Directory.Delete(snapshot.BundleDirectory, true);

        RestoreManagementFile(
            ProjectPaths.Json(root, "artifacts.json"), snapshot.ArtifactsJson);
        RestoreManagementFile(
            ProjectPaths.Json(root, "route_results.json"), snapshot.RouteResultsJson);
    }

    private static void RestoreManagementFile(string path, byte[]? bytes)
    {
        if (bytes is null)
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".rollback-" + Guid.NewGuid().ToString("N");
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, true);
    }

    private static GeneratedArtifactLineage BuildRouteLineage(RoutePlanResult plan) =>
        new(
            [],
            [],
            plan.Sorties.SelectMany(sortie => sortie.TaskReferences)
                .Select(reference => reference.SurveyTaskStableId)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray(),
            plan.Sorties.SelectMany(sortie => sortie.TaskReferences)
                .Select(reference => reference.SurveyLineStableId)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray(),
            plan.ExecutionSegments.Select(segment => segment.SegmentStableId)
                .Concat(plan.Sorties.SelectMany(sortie => sortie.TaskReferences)
                    .Select(reference => reference.ExecutionSegmentStableId))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray(),
            plan.PartitionCode?.Trim() ?? "",
            plan.PartitionManifestPath?.Trim() ?? "");

}
