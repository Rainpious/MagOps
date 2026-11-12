using MagOps.Core.Domain;

namespace MagOps.Desktop;

public sealed partial class MainForm
{
    private void InitializeUiArchitecture()
    {
        _projectWorkareaContext.Changed += (_, e) =>
        {
            PreviewNavigation.WorkspaceKey = e.Current.ProjectRoot + "|" + e.Current.WorkareaRelativePath;
            if (!string.Equals(e.Previous.ProjectRoot, e.Current.ProjectRoot, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(e.Previous.WorkareaRelativePath, e.Current.WorkareaRelativePath, StringComparison.OrdinalIgnoreCase))
            {
                _routeDesignBasis = null;
                _routeWorkspace.SetTerrainDemPath("");
                _routeTerrainLoadedProjectRoot = "";
            }
            _featureHost.NotifyContextChanged(e.Current);
        };

        _dirtyStateCoordinator.Changed +=
            (_, _) => UpdateDirtyStateIndicator();

        _dirtyStateCoordinator.Register(
            new DirtyFeatureRegistration(
                FeatureIds.RouteDesign,
                "航线设计",
                () => RouteWorkspaceHasUnsavedChanges,
                DescribeRouteWorkspaceDirtyState,
                TrySaveRouteWorkspaceForContextSwitch,
                () =>
                {
                    foreach (var context in _routePartitionContexts.Values) context.Dirty = false;
                    SetRouteWorkspaceDirty(false);
                }));

        _featureHost.ActiveFeatureChanged +=
            (_, _) => UpdateDirtyStateIndicator();
    }

    private void RegisterFeaturePage(
        string featureId,
        TabPage page,
        Action? activated = null,
        Action? deactivated = null,
        Action<ProjectWorkareaContextSnapshot>? contextChanged = null)
    {
        NavigationFeatureDefinition definition =
            NavigationModel.Require(featureId);

        _featureHost.Register(
            new LegacyTabWorkspaceFeature(
                featureId,
                definition.Title,
                page,
                activated,
                deactivated,
                contextChanged));
    }

    private void RegisterFeatureControl(
        string featureId,
        Control control,
        Action? activated = null,
        Action? deactivated = null,
        Action<ProjectWorkareaContextSnapshot>? contextChanged = null)
    {
        NavigationFeatureDefinition definition =
            NavigationModel.Require(featureId);

        _featureHost.Register(
            new LegacyTabWorkspaceFeature(
                featureId,
                definition.Title,
                control,
                activated,
                deactivated,
                contextChanged));
    }

    private bool NavigateFeature(string featureId) =>
        ResolveSiteDraft("切换页面") && _featureHost.Navigate(featureId);

    private bool IsFeatureActive(string featureId) =>
        _featureHost.IsActive(featureId);

    private readonly Dictionary<string, long> _queuedFeatureActivations = new(StringComparer.OrdinalIgnoreCase);
    private long _featureActivationSequence;

    private void QueueFeatureActivation(string featureId, Action action)
    {
        if (NavigationModel.Require(featureId).Frozen) return;
        if (IsDisposed || Disposing || !IsHandleCreated || !IsFeatureActive(featureId)) return;
        long revision = _projectWorkareaContext.Current.Revision;
        long sequence = ++_featureActivationSequence;
        _queuedFeatureActivations[featureId] = sequence;
        BeginInvoke((Action)(() =>
        {
            if (!_queuedFeatureActivations.TryGetValue(featureId, out long latest) || latest != sequence) return;
            _queuedFeatureActivations.Remove(featureId);
            if (IsDisposed || Disposing || !IsFeatureActive(featureId) ||
                _projectWorkareaContext.Current.Revision != revision) return;
            action();
        }));
    }

    private void SetProjectContext(string projectRoot)
    {
        string before = _projectWorkareaContext.Current.ProjectRoot;
        _projectWorkareaContext.SetProject(projectRoot);
        if (!_projectWorkareaContext.Current.ProjectRoot.Equals(before, StringComparison.OrdinalIgnoreCase))
        {
            ResetSiteEditor();
            _routeWorkspace?.SetSurveyTrackReferences([], 0, "WGS84");
            RestoreLatestFileResult();
        }
    }

    private void SetWorkareaContext(WorkareaRecord? workarea)
    {
        ProjectWorkareaContextSnapshot before = _projectWorkareaContext.Current;
        _projectWorkareaContext.SetWorkarea(
            workarea?.WorkareaId ?? "",
            workarea?.Path ?? "");
        ProjectWorkareaContextSnapshot after = _projectWorkareaContext.Current;
        PreviewNavigation.WorkspaceKey = after.ProjectRoot + "|" + after.WorkareaRelativePath;
        if (!before.WorkareaId.Equals(after.WorkareaId, StringComparison.OrdinalIgnoreCase) ||
            !before.WorkareaRelativePath.Equals(after.WorkareaRelativePath, StringComparison.OrdinalIgnoreCase))
        {
            ResetSiteEditor();
            _routeWorkspace?.SetSurveyTrackReferences([], 0, "WGS84");
            RestoreLatestFileResult();
        }
    }

    private string CurrentWorkareaRelativePath =>
        _projectWorkareaContext.Current.WorkareaRelativePath;

    private string CurrentWorkareaId =>
        _projectWorkareaContext.Current.WorkareaId;

    private bool ConfirmContextSwitch(string targetDescription)
    {
        if (!ResolveSiteDraft(targetDescription)) return false;
        IReadOnlyList<DirtyFeatureSnapshot> dirtyStates =
            _dirtyStateCoordinator.GetDirtyStates();
        if (dirtyStates.Count == 0)
            return true;

        DirtyStateDecision decision = DirtyStateDialog.Ask(
            this,
            dirtyStates,
            targetDescription);

        switch (decision)
        {
            case DirtyStateDecision.SaveAndContinue:
                try
                {
                    if (_dirtyStateCoordinator.TrySaveAll(dirtyStates))
                        return true;

                    SetStatus("切换已取消：仍有内容未完成保存。");
                    return false;
                }
                catch (OperationCanceledException)
                {
                    SetStatus("切换已取消：保存操作被取消。");
                    return false;
                }
                catch (Exception ex)
                {
                    SetStatus($"切换已取消：保存失败：{ex.Message}");
                    MessageBox.Show(
                        this,
                        $"保存未完成，当前上下文保持不变。\r\n\r\n{ex.Message}",
                        "无法切换",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                    return false;
                }

            case DirtyStateDecision.DiscardAndContinue:
                _dirtyStateCoordinator.DiscardAll(dirtyStates);
                return true;

            default:
                SetStatus("已取消上下文切换。");
                return false;
        }
    }

    private void UpdateDirtyStateIndicator()
    {
        IReadOnlyList<DirtyFeatureSnapshot> dirtyStates =
            _dirtyStateCoordinator.GetDirtyStates();

        if (dirtyStates.Count == 0)
        {
            _dirtyStateStatus.Text = "";
            _dirtyStateStatus.Visible = false;
            return;
        }

        _dirtyStateStatus.Text =
            "● 未保存：" +
            string.Join("、", dirtyStates.Select(x => x.DisplayName));
        _dirtyStateStatus.Visible = true;
    }

    private string DescribeRouteWorkspaceDirtyState()
    {
        var reasons = new List<string>();

        if (_routeEditedSorties.Count > 0)
            reasons.Add($"{_routeEditedSorties.Count} 个架次已编辑");

        if (_routeReversedSorties.Count > 0)
            reasons.Add($"{_routeReversedSorties.Count} 个架次已更换测线方向");

        if (_routeBoundaryAdjustedSorties.Count > 0)
            reasons.Add($"{_routeBoundaryAdjustedSorties.Count} 个架次边界已调整");

        if (reasons.Count == 0)
        {
            reasons.Add(
                _routePlan is null
                    ? "航线工作台存在未保存修改"
                    : "当前生成航线尚未保存编辑工程");
        }

        return string.Join("；", reasons);
    }

    private bool TrySaveRouteWorkspaceForContextSwitch()
    {
        try
        {
            SaveRouteProject();
            return !RouteWorkspaceHasUnsavedChanges;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private void SetRouteWorkspaceDirty(bool dirty)
    {
        if (_routeWorkspaceDirty == dirty)
            return;

        _routeWorkspaceDirty = dirty;
        _dirtyStateCoordinator.NotifyChanged();
    }
}
