using MagOps.Core.Domain;
using MagOps.Infrastructure.Services;
using MagOps.Infrastructure.Storage;

namespace MagOps.Application;

public sealed record WorkspaceContextResult(
    WorkspaceRecord Workspace,
    IReadOnlyList<WorkareaRecord> Workareas,
    string DataDirectory);

public sealed class WorkspaceQueries
{
    private readonly WorkspaceService _workspace;
    private readonly ArtifactService _artifacts;

    public WorkspaceQueries(JsonStore store)
    {
        _workspace = new WorkspaceService(store);
        _artifacts = new ArtifactService(store, _workspace);
    }

    public ApplicationResult<WorkspaceContextResult> GetContext(ApplicationRequest request)
    {
        string operationId = $"op-{Guid.NewGuid():N}";
        try
        {
            string root = Path.GetFullPath(request.ProjectRoot);
            WorkspaceRecord workspace = _workspace.GetWorkspaceReadOnly(root);
            List<WorkareaRecord> workareas = _workspace.GetWorkareas(root);
            WorkareaResolutionResult selected = ResolveRequestedWorkarea(workareas, request.WorkareaId, useFirstWhenEmpty: true);
            if (selected.Error is not null)
                return ApplicationResult<WorkspaceContextResult>.Failure(operationId, selected.Error);

            var context = new ApplicationContext(
                root,
                workspace.ProjectId,
                selected.Workarea?.WorkareaId ?? "",
                workspace.UpdatedAt);
            return ApplicationResult<WorkspaceContextResult>.Success(
                operationId,
                context,
                new WorkspaceContextResult(workspace, workareas, ProjectPaths.DataDirectory(root)),
                selected.Warnings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return ApplicationResult<WorkspaceContextResult>.Failure(
                operationId,
                new ApplicationError(ApplicationErrorCodes.EnvironmentError, ex.Message));
        }
    }

    public ApplicationResult<IReadOnlyList<ArtifactRecord>> ListArtifacts(ApplicationRequest request)
    {
        string operationId = $"op-{Guid.NewGuid():N}";
        try
        {
            string root = Path.GetFullPath(request.ProjectRoot);
            WorkspaceRecord workspace = _workspace.GetWorkspaceReadOnly(root);
            List<WorkareaRecord> workareas = _workspace.GetWorkareas(root);
            WorkareaResolutionResult selected = ResolveRequestedWorkarea(workareas, request.WorkareaId, useFirstWhenEmpty: true);
            if (selected.Error is not null)
                return ApplicationResult<IReadOnlyList<ArtifactRecord>>.Failure(operationId, selected.Error);

            var context = new ApplicationContext(root, workspace.ProjectId, selected.Workarea?.WorkareaId ?? "", workspace.UpdatedAt);
            return ApplicationResult<IReadOnlyList<ArtifactRecord>>.Success(
                operationId,
                context,
                _artifacts.List(root),
                selected.Warnings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return ApplicationResult<IReadOnlyList<ArtifactRecord>>.Failure(
                operationId,
                new ApplicationError(ApplicationErrorCodes.EnvironmentError, ex.Message));
        }
    }

    public ApplicationResult<IReadOnlyList<WorkareaRecord>> ListWorkareas(ApplicationRequest request)
    {
        string operationId = $"op-{Guid.NewGuid():N}";
        try
        {
            string root = Path.GetFullPath(request.ProjectRoot);
            WorkspaceRecord workspace = _workspace.GetWorkspaceReadOnly(root);
            List<WorkareaRecord> workareas = _workspace.GetWorkareas(root);
            WorkareaResolutionResult selected = ResolveRequestedWorkarea(workareas, request.WorkareaId, useFirstWhenEmpty: false);
            if (selected.Error is not null)
                return ApplicationResult<IReadOnlyList<WorkareaRecord>>.Failure(operationId, selected.Error);

            var context = new ApplicationContext(root, workspace.ProjectId, selected.Workarea?.WorkareaId ?? "", workspace.UpdatedAt);
            return ApplicationResult<IReadOnlyList<WorkareaRecord>>.Success(operationId, context, workareas, selected.Warnings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return ApplicationResult<IReadOnlyList<WorkareaRecord>>.Failure(
                operationId,
                new ApplicationError(ApplicationErrorCodes.EnvironmentError, ex.Message));
        }
    }

    private static WorkareaResolutionResult ResolveRequestedWorkarea(
        IReadOnlyList<WorkareaRecord> workareas,
        string? selector,
        bool useFirstWhenEmpty)
    {
        if (string.IsNullOrWhiteSpace(selector))
            return new(useFirstWhenEmpty ? workareas.FirstOrDefault() : null, [], null);

        WorkareaIdentityResolution resolution = WorkareaIdentityResolver.Resolve(workareas, selector);
        if (resolution.MatchKind == WorkareaIdentityMatchKind.Ambiguous)
        {
            string candidates = resolution.CandidateWorkareaIds.Count == 0
                ? "多个工区"
                : string.Join(", ", resolution.CandidateWorkareaIds);
            return new(null, [], new ApplicationError(
                ApplicationErrorCodes.AmbiguousIdentity,
                $"工区选择“{resolution.Selector}”存在歧义（{candidates}）。请使用稳定 WorkareaId。"));
        }
        if (!resolution.Found)
        {
            return new(null, [], new ApplicationError(
                ApplicationErrorCodes.ValidationError,
                $"未找到工区“{resolution.Selector}”。请使用有效 WorkareaId。"));
        }

        IReadOnlyList<ApplicationWarning> warnings = resolution.UsedCompatibilityAlias
            ? [new ApplicationWarning(
                "LEGACY_WORKAREA_ALIAS",
                $"工区选择“{resolution.Selector}”已兼容解析为稳定 WorkareaId“{resolution.Workarea!.WorkareaId}”；后续请持久化/传递 WorkareaId。")]
            : [];
        return new(resolution.Workarea, warnings, null);
    }

    private sealed record WorkareaResolutionResult(
        WorkareaRecord? Workarea,
        IReadOnlyList<ApplicationWarning> Warnings,
        ApplicationError? Error);
}
