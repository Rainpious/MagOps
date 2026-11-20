namespace MagOps.Desktop;

internal static class FeatureIds
{
    public const string ProjectDashboard = "project.dashboard";
    public const string DepartureReadiness = "preparation.departure";
    public const string Coordinates = "design.coordinates";
    public const string RouteDesign = "design.routes";
    public const string FieldOperations = "field.operations";
    public const string SegmentationProcessing = "processing.segmentation";
    public const string ExcelProcessing = "processing.excel";
    public const string OasisProcessing = "processing.oasis";
    public const string AnalysisTools = "tools.analysis";

    public const string ArtifactWorkspace = "project.artifacts";
    public const string DataOrganization = "processing.data-organization";
    public const string ReportCompilation = "results.report";
    public const string QualityChecks = "results.quality-checks";
}

internal static class NavigationGroupIds
{
    public const string Project = "project";
    public const string Preparation = "preparation";
    public const string Design = "design";
    public const string Field = "field";
    public const string Processing = "processing";
    public const string Results = "results";
    public const string Tools = "tools";
}

internal sealed record NavigationGroupDefinition(
    string GroupId,
    string Title,
    int Order);

internal sealed record NavigationFeatureDefinition(
    string FeatureId,
    string GroupId,
    string GroupTitle,
    string Title,
    int Order,
    bool Experimental = false,
    bool Frozen = false);

internal static class NavigationModel
{
    public static IReadOnlyList<NavigationGroupDefinition> Groups { get; } =
    [
        new(NavigationGroupIds.Project, "项目", 10),
        new(NavigationGroupIds.Preparation, "作业准备", 20),
        new(NavigationGroupIds.Design, "设计", 30),
        new(NavigationGroupIds.Field, "外业", 40),
        new(NavigationGroupIds.Processing, "数据处理", 50),
        new(NavigationGroupIds.Results, "成果", 60),
        new(NavigationGroupIds.Tools, "工具", 70)
    ];

    public static IReadOnlyList<NavigationFeatureDefinition> Current { get; } =
    [
        new(FeatureIds.ProjectDashboard, NavigationGroupIds.Project, "项目", "项目看板", 10),
        new(FeatureIds.ArtifactWorkspace, NavigationGroupIds.Project, "项目", "资料与归档", 15),
        new(FeatureIds.DepartureReadiness, NavigationGroupIds.Preparation, "作业准备", "出发整备", 20),
        new(FeatureIds.Coordinates, NavigationGroupIds.Design, "设计", "坐标与空间", 30),
        new(FeatureIds.RouteDesign, NavigationGroupIds.Design, "设计", "绘制航线", 40),
        new(FeatureIds.FieldOperations, NavigationGroupIds.Field, "外业", "外业管理", 50),
        new(FeatureIds.SegmentationProcessing, NavigationGroupIds.Processing, "数据处理", "分线与处理", 55),
        new(FeatureIds.ExcelProcessing, NavigationGroupIds.Processing, "数据处理", "Excel处理", 60, Frozen: true),
        new(FeatureIds.OasisProcessing, NavigationGroupIds.Processing, "数据处理", "Oasis处理", 70, Frozen: true),
        new(FeatureIds.QualityChecks, NavigationGroupIds.Results, "成果", "质量验算", 74),
        new(FeatureIds.ReportCompilation, NavigationGroupIds.Results, "成果", "报告编写", 75),
        new(FeatureIds.AnalysisTools, NavigationGroupIds.Tools, "工具", "分析工具", 80, Experimental: true, Frozen: true)
    ];

    public static NavigationFeatureDefinition Require(string featureId) =>
        Current.FirstOrDefault(x =>
            x.FeatureId.Equals(featureId, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException($"未注册导航 FeatureId：{featureId}");
}

internal sealed record ProjectWorkareaContextSnapshot(
    string ProjectRoot,
    string WorkareaId,
    string WorkareaRelativePath,
    long Revision)
{
    public bool HasProject => !string.IsNullOrWhiteSpace(ProjectRoot);
    public bool HasWorkarea =>
        !string.IsNullOrWhiteSpace(WorkareaId) &&
        !string.IsNullOrWhiteSpace(WorkareaRelativePath);
}

internal sealed class ProjectWorkareaContextChangedEventArgs(
    ProjectWorkareaContextSnapshot previous,
    ProjectWorkareaContextSnapshot current) : EventArgs
{
    public ProjectWorkareaContextSnapshot Previous { get; } = previous;
    public ProjectWorkareaContextSnapshot Current { get; } = current;
}

internal sealed class ProjectWorkareaContext
{
    private ProjectWorkareaContextSnapshot _current = new("", "", "", 0);

    public ProjectWorkareaContextSnapshot Current => _current;

    public event EventHandler<ProjectWorkareaContextChangedEventArgs>? Changed;

    public void SetProject(string projectRoot)
    {
        string normalized = string.IsNullOrWhiteSpace(projectRoot)
            ? ""
            : Path.GetFullPath(projectRoot);
        Set(normalized, "", "");
    }

    public void SetWorkarea(string workareaId, string workareaRelativePath) =>
        Set(_current.ProjectRoot, workareaId, NormalizeWorkarea(workareaRelativePath));

    public void Set(string projectRoot, string workareaId, string workareaRelativePath)
    {
        string root = string.IsNullOrWhiteSpace(projectRoot)
            ? ""
            : Path.GetFullPath(projectRoot);
        string canonicalId = (workareaId ?? "").Trim();
        string workarea = NormalizeWorkarea(workareaRelativePath);

        if (_current.ProjectRoot.Equals(root, StringComparison.OrdinalIgnoreCase) &&
            _current.WorkareaId.Equals(canonicalId, StringComparison.OrdinalIgnoreCase) &&
            _current.WorkareaRelativePath.Equals(workarea, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        ProjectWorkareaContextSnapshot previous = _current;
        _current = new ProjectWorkareaContextSnapshot(
            root,
            canonicalId,
            workarea,
            previous.Revision + 1);

        Changed?.Invoke(
            this,
            new ProjectWorkareaContextChangedEventArgs(previous, _current));
    }

    private static string NormalizeWorkarea(string value) =>
        (value ?? "").Replace('\\', '/').Trim('/').Trim();
}

internal interface IWorkspaceFeature
{
    string FeatureId { get; }
    string Title { get; }
    Control View { get; }

    void Activated();
    void Deactivated();
    void ContextChanged(ProjectWorkareaContextSnapshot context);
}

internal sealed class LegacyTabWorkspaceFeature : IWorkspaceFeature
{
    private readonly Action? _activated;
    private readonly Action? _deactivated;
    private readonly Action<ProjectWorkareaContextSnapshot>? _contextChanged;

    public LegacyTabWorkspaceFeature(
        string featureId,
        string title,
        Control view,
        Action? activated = null,
        Action? deactivated = null,
        Action<ProjectWorkareaContextSnapshot>? contextChanged = null)
    {
        FeatureId = featureId;
        Title = title;
        View = view;
        _activated = activated;
        _deactivated = deactivated;
        _contextChanged = contextChanged;
    }

    public string FeatureId { get; }
    public string Title { get; }
    public Control View { get; }

    public void Activated() => _activated?.Invoke();
    public void Deactivated() => _deactivated?.Invoke();

    public void ContextChanged(ProjectWorkareaContextSnapshot context) =>
        _contextChanged?.Invoke(context);
}

internal sealed class FeatureHost
{
    private readonly TabControl _tabs;
    private readonly Dictionary<string, HostedFeature> _byId =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<TabPage, HostedFeature> _byPage = [];
    private HostedFeature? _active;

    public FeatureHost(TabControl tabs)
    {
        _tabs = tabs;
        _tabs.Selected += OnSelected;
    }

    public string ActiveFeatureId => _active?.Feature.FeatureId ?? "";
    public TabPage? ActivePage => _active?.Page ?? _tabs.SelectedTab;
    public IReadOnlyCollection<string> RegisteredFeatureIds => _byId.Keys.ToArray();

    public event EventHandler? ActiveFeatureChanged;

    public void Register(IWorkspaceFeature feature)
    {
        ArgumentNullException.ThrowIfNull(feature);

        NavigationFeatureDefinition definition =
            NavigationModel.Require(feature.FeatureId);

        if (_byId.ContainsKey(feature.FeatureId))
            throw new InvalidOperationException($"FeatureId 已重复注册：{feature.FeatureId}");

        TabPage page;
        if (feature.View is TabPage existingPage)
        {
            page = existingPage;
        }
        else
        {
            page = new TabPage(definition.Title)
            {
                Padding = Padding.Empty
            };
            feature.View.Dock = DockStyle.Fill;
            page.Controls.Add(feature.View);
        }

        page.Name = feature.FeatureId;
        page.Text = definition.Frozen ? definition.Title + "（正在开发）" : definition.Title;
        if (definition.Frozen)
        {
            // Keep the existing controls owned by the page for disposal, but do not
            // expose their commands or run feature callbacks while frozen.
            foreach (Control control in page.Controls)
            {
                control.Enabled = false;
                control.Visible = false;
            }
            var notice = new Label
            {
                Name = "developmentNotice",
                Text = definition.Title + "正在开发，当前版本暂不开放。",
                AccessibleName = definition.Title + "开发状态",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                Padding = new Padding(24)
            };
            page.Controls.Add(notice);
            notice.BringToFront();
        }

        var hosted = new HostedFeature(feature, page);
        _byId.Add(feature.FeatureId, hosted);
        _byPage.Add(page, hosted);
        _tabs.TabPages.Add(page);

        if (ReferenceEquals(_tabs.SelectedTab, page))
            Activate(hosted);
    }

    public bool Navigate(string featureId)
    {
        if (!_byId.TryGetValue(featureId, out HostedFeature? hosted))
            return false;

        _tabs.SelectedTab = hosted.Page;
        return true;
    }

    public bool IsActive(string featureId) =>
        _active is not null &&
        _active.Feature.FeatureId.Equals(featureId, StringComparison.OrdinalIgnoreCase);

    public void NotifyContextChanged(ProjectWorkareaContextSnapshot context)
    {
        foreach (HostedFeature hosted in _byId.Values)
            if (!NavigationModel.Require(hosted.Feature.FeatureId).Frozen)
                hosted.Feature.ContextChanged(context);
    }

    private void OnSelected(object? sender, TabControlEventArgs e)
    {
        if (e.TabPage is null ||
            !_byPage.TryGetValue(e.TabPage, out HostedFeature? hosted))
        {
            return;
        }

        Activate(hosted);
    }

    private void Activate(HostedFeature hosted)
    {
        if (ReferenceEquals(_active, hosted))
            return;

        if (_active is not null && !NavigationModel.Require(_active.Feature.FeatureId).Frozen)
            _active.Feature.Deactivated();
        _active = hosted;
        if (!NavigationModel.Require(hosted.Feature.FeatureId).Frozen)
            _active.Feature.Activated();
        ActiveFeatureChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed record HostedFeature(
        IWorkspaceFeature Feature,
        TabPage Page);
}

internal sealed record DirtyFeatureRegistration(
    string FeatureId,
    string DisplayName,
    Func<bool> IsDirty,
    Func<string> Describe,
    Func<bool>? TrySave,
    Action? Discard);

internal sealed record DirtyFeatureSnapshot(
    string FeatureId,
    string DisplayName,
    string Reason,
    bool CanSave);

internal sealed class DirtyStateCoordinator
{
    private readonly Dictionary<string, DirtyFeatureRegistration> _registrations =
        new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler? Changed;

    public void Register(DirtyFeatureRegistration registration)
    {
        _registrations[registration.FeatureId] = registration;
        NotifyChanged();
    }

    public IReadOnlyList<DirtyFeatureSnapshot> GetDirtyStates() =>
        _registrations.Values
            .Where(x => x.IsDirty())
            .Select(x => new DirtyFeatureSnapshot(
                x.FeatureId,
                x.DisplayName,
                x.Describe(),
                x.TrySave is not null))
            .OrderBy(x => NavigationOrder(x.FeatureId))
            .ThenBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public bool TrySaveAll(IReadOnlyList<DirtyFeatureSnapshot> dirtyStates)
    {
        foreach (DirtyFeatureSnapshot dirty in dirtyStates)
        {
            if (!_registrations.TryGetValue(
                    dirty.FeatureId,
                    out DirtyFeatureRegistration? registration) ||
                registration.TrySave is null ||
                !registration.TrySave())
            {
                return false;
            }
        }

        NotifyChanged();
        return true;
    }

    public void DiscardAll(IReadOnlyList<DirtyFeatureSnapshot> dirtyStates)
    {
        foreach (DirtyFeatureSnapshot dirty in dirtyStates)
        {
            if (_registrations.TryGetValue(
                    dirty.FeatureId,
                    out DirtyFeatureRegistration? registration))
            {
                registration.Discard?.Invoke();
            }
        }

        NotifyChanged();
    }

    public void NotifyChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private static int NavigationOrder(string featureId)
    {
        try
        {
            return NavigationModel.Require(featureId).Order;
        }
        catch
        {
            return int.MaxValue;
        }
    }
}
