using System.Text.Json;
using MagOps.Core.Domain;
using MagOps.Core.Routes;
using MagOps.Core.SurveyPlanning;
using MagOps.Infrastructure.Services;
using MagOps.Infrastructure.Storage;

namespace MagOps.Desktop;

internal sealed record ProjectChoice(string Root, string Label)
{
    public override string ToString() => Label;
}

internal sealed class RecentProjectEntry
{
    public string Root { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string LastOpenedAt { get; set; } = "";
}

public sealed partial class MainForm
{
    private void TryRestoreLastProjectAndRoute()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(Root))
            {
                RecentProjectEntry? recent = LoadRecentProjects().FirstOrDefault();
                if (recent is not null)
                    OpenProjectRoot(recent.Root, remember: false);
            }

            if (string.IsNullOrWhiteSpace(Root) || !IsProjectInitialized())
                return;

            EnsureRememberedWorkareaContextLoaded();
            UserContextState state = _userContext.Load(Root);
            string routePath = state.LastRouteProjectPath;
            if (string.IsNullOrWhiteSpace(routePath))
                return;
            if (!File.Exists(routePath))
            {
                SetStatus($"上次航线工程不存在，未自动恢复：{routePath}");
                return;
            }

            LoadRouteProject(routePath, restoringCachedProject: true);
            if (_routePartitionContexts.Count == 0 && File.Exists(state.LastPartitionManifestPath))
                RestorePartitionOverviewState(
                    state.LastPartitionManifestPath,
                    state.LastPartitionCode);
        }
        catch (Exception ex)
        {
            string log = LogService.LogException(Root, ex, "自动恢复项目与航线工程");
            SetStatus($"项目或航线工程自动恢复失败：{ex.Message}；{DescribeErrorLog(log)}");
        }
        finally
        {
            // Root is assigned before project refresh. A later read/write failure
            // must not leave the selector showing the pre-restore placeholder.
            RefreshProjectSelector();
        }
    }

    private void RestorePartitionOverviewState(string manifestPath, string activeCode)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        var contexts = new List<PartitionRouteRuntimeContext>();
        foreach (string kmlPath in Directory.EnumerateFiles(directory, "*.kml", SearchOption.AllDirectories))
        {
            if (!_kmlSurveySubdivision.TryResolveGeneratedChild(kmlPath, out KmlSurveySubdivisionResolvedChild? child) ||
                child is null || !child.ManifestPath.Equals(Path.GetFullPath(manifestPath), StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                var polygon = _kmlSurveySubdivision.ReadPrimaryPolygon(child.KmlPath);
                var context = new PartitionRouteRuntimeContext
                {
                    PartitionCode = child.PartitionCode,
                    Index = child.Index,
                    Child = child,
                    Boundary = _kmlSurveySubdivision.ReadProjectedBoundary(child.KmlPath, child.CentralMeridian, child.Datum),
                    StartSide = RoutePartitionStartSide.Automatic,
                    StartDirection = (RouteStartDirection)Math.Clamp(_routeWorkspace.GetStartDirectionIndex(), 0, 3),
                    TakeoffAnchorMode = _routeWorkspace.GetTakeoffAnchorMode(),
                    FlightDirection = _routeWorkspace.GetFlightDirection(),
                    TakeoffAnchorRef = _routeWorkspace.GetTakeoffAnchorRef(),
                    MaxSortieKm = ParseRoutePositive(_routeWorkspace.GetMaxSortieKmText(), "最大单架次长度"),
                    AglM = ParseRouteNumber(_routeWorkspace.GetAglText(), "离地高程 AGL"),
                    RouteSourcePath = child.EndpointCsvPath
                };
                RebuildPartitionRouteContext(context);
                contexts.Add(context);
            }
            catch { }
        }
        if (contexts.Count == 0) return;
        _routePartitionContexts.Clear();
        foreach (var context in contexts.OrderBy(x => x.Index))
            _routePartitionContexts[context.PartitionCode] = context;
        _routeWorkspace.SetPartitionOverviewEntries(
            contexts.OrderBy(x => x.Index).Select(x => x.ToOverviewEntry()).ToArray(), activeCode);
        _routeWorkspace.ShowPartitionOverviewTab();
        if (!string.IsNullOrWhiteSpace(activeCode) && _routePartitionContexts.ContainsKey(activeCode))
            OpenPartitionFromOverview(activeCode);
    }

    private string? _recentProjectHistoryPathOverride = null;
    private string RecentProjectHistoryPath => _recentProjectHistoryPathOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MagOps",
        "recent-projects.json");

    private List<RecentProjectEntry> LoadRecentProjects()
    {
        try
        {
            string path = RecentProjectHistoryPath;
            if (!File.Exists(path))
                return [];

            string json = File.ReadAllText(path);
            return (JsonSerializer.Deserialize<List<RecentProjectEntry>>(json) ?? [])
                .Where(x => !string.IsNullOrWhiteSpace(x.Root) && Directory.Exists(x.Root))
                .GroupBy(x => Path.GetFullPath(x.Root), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(x => x.LastOpenedAt, StringComparer.Ordinal).First())
                .OrderByDescending(x => x.LastOpenedAt, StringComparer.Ordinal)
                .Take(16)
                .ToList();
        }
        catch
        {
            // A broken per-user recent-project file must never block the business project.
            return [];
        }
    }

    private void SaveRecentProjects(IEnumerable<RecentProjectEntry> entries)
    {
        string path = RecentProjectHistoryPath;
        string? parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(parent))
            Directory.CreateDirectory(parent);

        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(entries.Take(16).ToList(), options),
            new System.Text.UTF8Encoding(false));
    }

    private void RememberProject(string root, string displayName)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return;

        string full = Path.GetFullPath(root);
        string label = string.IsNullOrWhiteSpace(displayName)
            ? new DirectoryInfo(full).Name
            : displayName.Trim();

        List<RecentProjectEntry> rows = LoadRecentProjects();
        rows.RemoveAll(x => Path.GetFullPath(x.Root).Equals(full, StringComparison.OrdinalIgnoreCase));
        rows.Insert(0, new RecentProjectEntry
        {
            Root = full,
            DisplayName = label,
            LastOpenedAt = DateTimeOffset.Now.ToString("O")
        });
        SaveRecentProjects(rows);
    }

    private string ProjectDisplayName(string root)
    {
        try
        {
            if (ProjectPaths.HasProject(root))
                return _workspace.GetWorkspaceReadOnly(root).DisplayName;
        }
        catch
        {
            // Fall back to the directory name for a damaged/uninitialized entry.
        }

        return new DirectoryInfo(root).Name;
    }

    private void RefreshProjectSelector()
    {
        _syncingProjectSelector = true;
        try
        {
            string current = string.IsNullOrWhiteSpace(Root) ? "" : Path.GetFullPath(Root);
            List<RecentProjectEntry> rows = LoadRecentProjects();

            if (current.Length > 0 && Directory.Exists(current) &&
                rows.All(x => !Path.GetFullPath(x.Root).Equals(current, StringComparison.OrdinalIgnoreCase)))
            {
                rows.Insert(0, new RecentProjectEntry
                {
                    Root = current,
                    DisplayName = ProjectDisplayName(current),
                    LastOpenedAt = DateTimeOffset.Now.ToString("O")
                });
            }

            _projectSelector.Items.Clear();
            _projectSelector.Items.Add(new ProjectChoice("", "请选择项目"));
            foreach (RecentProjectEntry row in rows)
            {
                _projectSelector.Items.Add(new ProjectChoice(
                    Path.GetFullPath(row.Root),
                    string.IsNullOrWhiteSpace(row.DisplayName)
                        ? new DirectoryInfo(row.Root).Name
                        : row.DisplayName));
            }

            int selected = 0;
            if (current.Length > 0)
            {
                for (int i = 1; i < _projectSelector.Items.Count; i++)
                {
                    if (_projectSelector.Items[i] is ProjectChoice choice &&
                        Path.GetFullPath(choice.Root).Equals(current, StringComparison.OrdinalIgnoreCase))
                    {
                        selected = i;
                        break;
                    }
                }
            }

            _projectSelector.SelectedIndex = Math.Clamp(selected, 0, Math.Max(0, _projectSelector.Items.Count - 1));
            if (_projectSelector.SelectedItem is ProjectChoice selectedChoice &&
                !string.IsNullOrWhiteSpace(selectedChoice.Root))
            {
                _toolTip.SetToolTip(_projectSelector, selectedChoice.Root);
            }
            else
            {
                _toolTip.SetToolTip(_projectSelector, "从最近项目中选择，或使用“新建项目 / 选择其他项目…”添加项目。");
            }
        }
        finally
        {
            _syncingProjectSelector = false;
        }
    }

    private bool OpenProjectRoot(string root, bool remember)
    {
        string full = Path.GetFullPath(root);
        if (!Directory.Exists(full))
            throw new DirectoryNotFoundException($"项目目录不存在：{full}");

        bool projectChanged =
            string.IsNullOrWhiteSpace(Root) ||
            !Path.GetFullPath(Root).Equals(
                full,
                StringComparison.OrdinalIgnoreCase);
        if (projectChanged &&
            !ConfirmContextSwitch(
                $"切换到项目“{new DirectoryInfo(full).Name}”"))
        {
            return false;
        }

        _rootBox.Text = full;
        SetProjectContext(full);
        ResetWorkareaContext();

        if (ProjectPaths.HasProject(full))
        {
            WorkspaceRecord workspace = _workspace.RelinkProjectRoot(full);
            workspace = _workspace.InvalidateLegacyCoordinateDerivedCentralMeridian(full);
            if (remember)
                RememberProject(full, workspace.DisplayName);
            RefreshAll();
            _toolTip.SetToolTip(_projectSelector, full);
            return true;
        }

        if (remember)
            RememberProject(full, new DirectoryInfo(full).Name);
        RefreshProjectSelector();
        RefreshWorkareaContextSelectors();
        RefreshGlobalSpatialReference(new WorkspaceRecord());
        _toolTip.SetToolTip(_projectSelector, full);
        SetStatus("已选择未初始化目录；可点击“初始化当前目录”，或使用“新建项目”创建标准项目。");
        return true;
    }

    private void ChooseExistingProject()
    {
        var fileHistory = FileInteractions.For(this, "open-project", projectLevel: false, global: true);
        using var dialog = new FolderBrowserDialog
        {
            Description = "选择已有航磁项目根目录",
            ShowNewFolderButton = false
        };
        fileHistory.Prepare(dialog);
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        if (OpenProjectRoot(dialog.SelectedPath, remember: true)) fileHistory.DirectorySucceeded(dialog.SelectedPath);
    }

    private void CreateNewProject()
    {
        var fileHistory = FileInteractions.For(this, "new-project-parent", projectLevel: false, global: true);
        using var parentDialog = new FolderBrowserDialog
        {
            Description = "选择母文件夹（将在其中创建新的项目子文件夹）",
            ShowNewFolderButton = true
        };
        fileHistory.Prepare(parentDialog);
        if (parentDialog.ShowDialog(this) != DialogResult.OK)
            return;

        var values = PromptDialog.Show(
            this,
            "新建项目",
            [
                new PromptField("folder", "项目子文件夹名称", "新项目", Required: true)
            ],
            note: $"已选母文件夹：{parentDialog.SelectedPath}\r\n" +
                "填写的名称用于创建其下的项目子文件夹，并同时作为项目显示名。\r\n" +
                "名称中的空格及特殊字符会规范为连字符；文件夹名与显示名保持一致。\r\n" +
                "其它业务目录仍在首次写文件时按需创建。");
        if (values is null)
            return;

        string folder = WorkspaceService.Slug(values["folder"].Trim());
        string alias = folder;
        if (folder.Length == 0 || alias.Length == 0)
            throw new InvalidOperationException("项目文件夹名称和显示名称不能为空。");

        string root = Path.Combine(parentDialog.SelectedPath, folder);
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            throw new IOException($"目标项目目录已存在且非空：{root}");

        if (!ConfirmContextSwitch(
                $"新建并切换到项目“{alias}”"))
        {
            return;
        }

        Directory.CreateDirectory(root);
        _rootBox.Text = Path.GetFullPath(root);
        SetProjectContext(Root);
        ResetWorkareaContext();
        WorkspaceRecord workspace = _workspace.Initialize(Root, alias);
        RememberProject(Root, workspace.DisplayName);
        RefreshProjectSelector();
        RefreshAll();
        SetStatus($"已新建项目：{workspace.DisplayName}");
        fileHistory.DirectorySucceeded(parentDialog.SelectedPath);
    }

    private void RenameCurrentProjectDisplayName()
    {
        EnsureInitialized();
        var workspace = _workspace.GetWorkspace(Root);
        var values = PromptDialog.Show(this, "修改项目显示名称",
            [new PromptField("name", "显示名称", workspace.DisplayName, Required: true)],
            note: $"项目目录：{Path.GetFullPath(Root)}\r\n仅更新显示名称；目录、稳定身份和已有成果路径保持不变。");
        if (values is not null) ApplyCurrentProjectDisplayName(values["name"]);
    }

    private void ApplyCurrentProjectDisplayName(string name)
    {
        var workspace = _workspace.RenameProjectDisplayName(Root, name);
        RememberProject(Root, workspace.DisplayName);
        RefreshProject();
        RefreshProjectSelector();
        SetStatus($"项目显示名称已更新：{workspace.DisplayName}；目录未移动。");
    }

    private void RenameCurrentWorkareaDisplayName()
    {
        EnsureInitialized();
        var row = _workspace.GetWorkareas(Root).SingleOrDefault(w =>
            w.Path.Equals(CurrentWorkareaRelativePath, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("请先选择当前工区。");
        var values = PromptDialog.Show(this, "修改工区显示名称",
            [new PromptField("name", "显示名称", row.DisplayName, Required: true)],
            note: $"工区目录：{Path.GetFullPath(Path.Combine(Root, row.Path))}\r\n仅更新显示名称；目录、工区身份和成果归属保持不变。");
        if (values is not null) ApplyCurrentWorkareaDisplayName(row.WorkareaId, values["name"]);
    }

    private void ApplyCurrentWorkareaDisplayName(string workareaId, string name)
    {
        var row = _workspace.RenameWorkareaDisplayName(Root, workareaId, name);
        SetWorkareaContext(row);
        RefreshProject();
        RefreshWorkareaContextSelectors();
        SetStatus($"工区显示名称已更新：{row.DisplayName}；目录与成果身份保持不变。");
    }

    private void CreateNewWorkarea()
    {
        EnsureInitialized();
        var values = PromptDialog.Show(
            this,
            "新建工区",
            [
                new PromptField("name", "工区名称", "", Required: true)
            ],
            note: "这里只登记工区并切换当前工区；10_工区/<工区名> 目录在首次实际写文件时才创建。");
        if (values is null)
            return;

        if (!ConfirmContextSwitch(
                $"新建并切换到工区“{values["name"].Trim()}”"))
        {
            return;
        }

        WorkareaRecord row = _workspace.RegisterWorkarea(Root, values["name"]);
        SetCurrentWorkareaRelativePath(row.Path);
        RefreshProject();
        RefreshArtifacts();
        RefreshWorkareaContextSelectors();
        SetStatus($"已登记并切换到工区：{row.DisplayName}；业务目录尚未创建。");
    }

    private void OpenExistingWorkareaFolder()
    {
        var fileHistory = FileInteractions.For(this, "open-workarea", projectLevel: false, global: true);
        string initial = "";
        if (IsProjectInitialized())
        {
            EnsureRememberedWorkareaContextLoaded();
            if (!string.IsNullOrWhiteSpace(CurrentWorkareaRelativePath))
            {
                string? currentFolder = ResolveRegisteredWorkareaAbsolutePath(
                    Root,
                    CurrentWorkareaRelativePath);
                if (!string.IsNullOrWhiteSpace(currentFolder) && Directory.Exists(currentFolder))
                    initial = currentFolder;
            }

            if (initial.Length == 0)
            {
                string candidate = _folderLayout.ResolveProjectFolder(Root, "project.workareas");
                if (Directory.Exists(candidate))
                    initial = candidate;
                else
                    initial = Root;
            }
        }

        using var dialog = new FolderBrowserDialog
        {
            Description =
                "选择已有工区根目录。程序会自动向上查找 MagOps 项目根目录；" +
                "项目或上级文件夹改名/移动后，可直接从这里重新打开。",
            ShowNewFolderButton = false
        };
        if (!string.IsNullOrWhiteSpace(initial))
            dialog.SelectedPath = initial;

        fileHistory.Prepare(dialog, business: initial ?? "");

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        string selectedFolder = Path.GetFullPath(dialog.SelectedPath);
        string? detectedProjectRoot = WorkspaceService.FindInitializedProjectRoot(selectedFolder);
        if (string.IsNullOrWhiteSpace(detectedProjectRoot))
        {
            MessageBox.Show(
                this,
                "没有在所选目录或其上级目录找到已初始化的 MagOps 项目。\r\n\r\n" +
                "请确认所选工区属于一个包含 00_项目管理/magops/project.json（或旧 workspace.json）的项目。",
                "无法识别项目",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        bool projectChanged =
            string.IsNullOrWhiteSpace(Root) ||
            !Path.GetFullPath(Root).Equals(
                Path.GetFullPath(detectedProjectRoot),
                StringComparison.OrdinalIgnoreCase);
        if (projectChanged && !OpenProjectRoot(detectedProjectRoot, remember: true))
            return;

        EnsureInitialized();
        List<WorkareaRecord> rows = _workspace.GetWorkareas(Root);
        selectedFolder = NormalizeSelectedWorkareaFolder(Root, selectedFolder, rows);

        string standardWorkareasRoot = Path.GetFullPath(
            _folderLayout.ResolveProjectFolder(Root, "project.workareas"));
        string legacyWorkareasRoot = Path.GetFullPath(Path.Combine(Root, "10_子工区"));
        if (selectedFolder.Equals(standardWorkareasRoot, StringComparison.OrdinalIgnoreCase) ||
            selectedFolder.Equals(legacyWorkareasRoot, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(
                this,
                "请选择具体工区目录，不要选择“10_工区 / 10_子工区”这一层容器目录。",
                "请选择工区根目录",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        string selectedRelative = Path.GetRelativePath(Root, selectedFolder)
            .Replace('\\', '/')
            .Trim('/');

        WorkareaRecord? exact = rows.FirstOrDefault(x =>
            NormalizeWorkareaRelativePath(x.Path).Equals(
                selectedRelative,
                StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            if (!SetCurrentWorkareaRelativePath(exact.Path))
                return;

            RefreshProject();
            ApplyArtifactFilters();
            fileHistory.DirectorySucceeded(selectedFolder);
            SetStatus($"已打开工区：{exact.DisplayName}");
            return;
        }

        string selectedLeaf = new DirectoryInfo(selectedFolder).Name;
        string selectedId = WorkspaceService.Slug(selectedLeaf);
        WorkareaRecord? identityConflict = rows.FirstOrDefault(x =>
            x.WorkareaId.Equals(selectedId, StringComparison.OrdinalIgnoreCase) ||
            x.SubareaId.Equals(selectedId, StringComparison.OrdinalIgnoreCase) ||
            x.DisplayName.Equals(selectedLeaf, StringComparison.OrdinalIgnoreCase));
        if (identityConflict is not null)
        {
            string? conflictFolder = ResolveRegisteredWorkareaAbsolutePath(
                Root,
                identityConflict.Path);
            if (!string.IsNullOrWhiteSpace(conflictFolder) && Directory.Exists(conflictFolder))
            {
                MessageBox.Show(
                    this,
                    $"项目中已有同名/同编号工区“{identityConflict.DisplayName}”，且原目录仍存在：\r\n" +
                    $"{identityConflict.Path}\r\n\r\n" +
                    "为避免产生两个相同工区身份，请选择原目录，或先明确调整工区名称。",
                    "工区身份冲突",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }
        }

        List<WorkareaRecord> unavailableRows = rows
            .Where(x =>
            {
                string? folder = ResolveRegisteredWorkareaAbsolutePath(Root, x.Path);
                return string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder);
            })
            .OrderBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        string? relinkWorkareaId = null;
        if (unavailableRows.Count > 0)
        {
            const string registerNew = "作为新的现有工区登记";
            var labels = new List<string>();
            if (identityConflict is null)
                labels.Add(registerNew);
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (WorkareaRecord row in unavailableRows)
            {
                string label = $"重新关联：{row.DisplayName}  [{row.Path}]  <{row.WorkareaId}>";
                labels.Add(label);
                map[label] = row.WorkareaId;
            }

            WorkareaRecord? likely = unavailableRows.FirstOrDefault(x =>
                    x.DisplayName.Equals(selectedLeaf, StringComparison.OrdinalIgnoreCase))
                ?? (unavailableRows.Count == 1 ? unavailableRows[0] : null);
            string defaultChoice = labels.Count > 0 ? labels[0] : "";
            if (likely is not null)
            {
                defaultChoice = labels.First(x =>
                    map.TryGetValue(x, out string? id) &&
                    id.Equals(likely.WorkareaId, StringComparison.OrdinalIgnoreCase));
            }

            Dictionary<string, string>? values = PromptDialog.Show(
                this,
                "打开工区文件夹",
                [
                    new PromptField(
                        "mode",
                        "所选目录尚未登记，请选择处理方式",
                        defaultChoice,
                        labels,
                        Required: true)
                ],
                note:
                    "“重新关联”会保留原工区 ID、状态和门禁，只更新工区相对路径；" +
                    "适合工区目录或其中层级被改名后的恢复。\r\n" +
                    "“作为新的现有工区登记”会创建新的工区身份，不会改动原记录。");
            if (values is null)
                return;

            string mode = values["mode"];
            if (map.TryGetValue(mode, out string? id))
                relinkWorkareaId = id;
        }
        else
        {
            DialogResult decision = MessageBox.Show(
                this,
                $"所选目录尚未登记为工区：\r\n{selectedRelative}\r\n\r\n" +
                "是否把这个已有目录登记为当前项目的新工区？",
                "登记已有工区",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);
            if (decision != DialogResult.Yes)
                return;
        }

        bool workareaWillChange = !CurrentWorkareaRelativePath.Equals(
            selectedRelative,
            StringComparison.OrdinalIgnoreCase);
        if (workareaWillChange &&
            !ConfirmContextSwitch($"切换到工区“{selectedRelative}”"))
        {
            return;
        }

        WorkareaRecord attached = _workspace.AttachExistingWorkareaFolder(
            Root,
            selectedFolder,
            relinkWorkareaId);
        if (!SetCurrentWorkareaRelativePath(
                attached.Path,
                confirmContextSwitch: false))
        {
            return;
        }

        RefreshProject();
        ApplyArtifactFilters();
        SetStatus(
            string.IsNullOrWhiteSpace(relinkWorkareaId)
                ? $"已登记并打开现有工区：{attached.DisplayName}"
                : $"已重新关联并打开工区：{attached.DisplayName} -> {attached.Path}");
        fileHistory.DirectorySucceeded(selectedFolder);
    }

    private string NormalizeSelectedWorkareaFolder(
        string projectRoot,
        string selectedFolder,
        IReadOnlyList<WorkareaRecord> rows)
    {
        string selected = Path.GetFullPath(selectedFolder);

        // If the user picked a subfolder inside an already registered workarea,
        // treat the registered workarea root as the target instead of accidentally
        // registering a production-stage folder as a new workarea.
        string? registeredContainer = rows
            .Select(x => ResolveRegisteredWorkareaAbsolutePath(projectRoot, x.Path))
            .Where(x => !string.IsNullOrWhiteSpace(x) &&
                        IsSameOrDescendantPath(x!, selected))
            .OrderByDescending(x => x!.Length)
            .FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(registeredContainer))
            return registeredContainer;

        // For the standard/legacy workarea containers, any nested selection belongs
        // to the first child directory. This also makes it forgiving if a user clicks
        // 04_航线设计与任务文件 (or deeper) instead of the workarea root itself.
        foreach (string container in new[]
                 {
                     _folderLayout.ResolveProjectFolder(projectRoot, "project.workareas"),
                     Path.Combine(projectRoot, "10_子工区")
                 })
        {
            string containerFull = Path.GetFullPath(container);
            if (!IsSameOrDescendantPath(containerFull, selected) ||
                selected.Equals(containerFull, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string relative = Path.GetRelativePath(containerFull, selected);
            string[] parts = relative.Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0)
                return Path.GetFullPath(Path.Combine(containerFull, parts[0]));
        }

        return selected;
    }

    private static string? ResolveRegisteredWorkareaAbsolutePath(
        string projectRoot,
        string relativePath)
    {
        try
        {
            string relative = NormalizeWorkareaRelativePath(relativePath);
            if (relative.Length == 0 || Path.IsPathRooted(relative))
                return null;

            string root = Path.GetFullPath(projectRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string full = Path.GetFullPath(
                Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            return IsSameOrDescendantPath(root, full) &&
                   !full.Equals(root, StringComparison.OrdinalIgnoreCase)
                ? full
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsSameOrDescendantPath(string parentPath, string candidatePath)
    {
        string parent = Path.GetFullPath(parentPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string candidate = Path.GetFullPath(candidatePath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return candidate.Equals(parent, StringComparison.OrdinalIgnoreCase) ||
               candidate.StartsWith(
                   parent + Path.DirectorySeparatorChar,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeWorkareaRelativePath(string value) =>
        (value ?? "").Replace('\\', '/').Trim('/').Trim();
}
