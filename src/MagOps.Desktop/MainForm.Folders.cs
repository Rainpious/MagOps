using System.Diagnostics;
using MagOps.Core.Domain;
using MagOps.Infrastructure.Services;

namespace MagOps.Desktop;

public sealed partial class MainForm
{
    private sealed record WorkareaChoice(string Path, string Label)
    {
        public override string ToString() => Label;
    }

    private Control BuildWorkareaContextBar(string purposeHint)
    {
        var bar = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 3,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = new Padding(0, 2, 0, 4)
        };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));

        bar.Controls.Add(new Label
        {
            Text = "当前工区",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(3, 8, 8, 3)
        }, 0, 0);

        var selector = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            MinimumSize = new Size(0, 48),
            WrapContents = false,
            AutoScroll = true,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        RegisterWorkareaSelector(selector);
        bar.Controls.Add(selector, 1, 0);

        bar.Controls.Add(new Label
        {
            Text = purposeHint,
            AutoSize = false,
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(8, 2, 3, 2)
        }, 2, 0);

        return bar;
    }

    private void RegisterWorkareaSelector(FlowLayoutPanel selector)
    {
        if (_workareaContextSelectors.Contains(selector))
            return;

        _workareaContextSelectors.Add(selector);
    }

    private static RadioButton WorkareaRadio(
        string text,
        string path,
        bool isChecked,
        bool enabled = true)
    {
        return new RadioButton
        {
            Appearance = Appearance.Button,
            AutoSize = true,
            Text = text,
            Tag = path,
            Checked = isChecked,
            Enabled = enabled,
            TextAlign = ContentAlignment.MiddleCenter,
            Padding = new Padding(8, 2, 8, 2),
            Margin = new Padding(0, 1, 6, 1),
            MinimumSize = new Size(0, 30)
        };
    }

    private void PopulateWorkareaSelector(
        FlowLayoutPanel selector,
        IReadOnlyList<WorkareaChoice> choices,
        bool initialized)
    {
        selector.SuspendLayout();
        selector.Controls.Clear();

        if (!initialized)
        {
            selector.Controls.Add(WorkareaRadio(
                "项目尚未初始化",
                "",
                isChecked: true,
                enabled: false));
            selector.ResumeLayout(true);
            return;
        }

        foreach (WorkareaChoice choice in choices)
        {
            var radio = WorkareaRadio(
                choice.Label,
                choice.Path,
                choice.Path.Equals(
                    CurrentWorkareaRelativePath,
                    StringComparison.OrdinalIgnoreCase));

            _toolTip.SetToolTip(
                radio,
                string.IsNullOrWhiteSpace(choice.Path)
                    ? "不固定当前工区；纯工具输出会手动选择位置，项目管理型写入仍会要求目标工区。该选择会随当前 Windows 用户记住。"
                    : $"当前工区：{choice.Path}；下次打开该项目会为当前 Windows 用户自动恢复。");

            radio.CheckedChanged += (_, _) =>
            {
                if (!radio.Checked || _syncingWorkareaContexts)
                    return;

                SetCurrentWorkareaRelativePath(
                    radio.Tag?.ToString() ?? "",
                    refreshSelectors: false);
                SynchronizeWorkareaSelectors(selector);
            };
            selector.Controls.Add(radio);
        }

        selector.ResumeLayout(true);
    }

    private void RefreshHeaderWorkareaSelector()
    {
        _syncingHeaderWorkareaSelector = true;
        try
        {
            _headerWorkareaSelector.Items.Clear();

            bool initialized =
                !string.IsNullOrWhiteSpace(Root) &&
                Directory.Exists(Root) &&
                MagOps.Infrastructure.Storage.ProjectPaths.HasProject(Root);

            if (!initialized)
            {
                _headerWorkareaSelector.Enabled = false;
                _headerWorkareaSelector.Items.Add(new WorkareaChoice("", "项目尚未初始化"));
                _headerWorkareaSelector.SelectedIndex = 0;
                _toolTip.SetToolTip(_headerWorkareaSelector, "请先选择并初始化项目。");
                RefreshCurrentWorkareaDirectoryButton();
                return;
            }

            _headerWorkareaSelector.Enabled = true;
            EnsureRememberedWorkareaContextLoaded();
            List<WorkareaRecord> rows = _workspace.GetWorkareas(Root);
            var choices = new List<WorkareaChoice>
            {
                new("", "未选择工区")
            };
            choices.AddRange(rows.Select(x => new WorkareaChoice(x.Path, x.DisplayName)));

            if (!string.IsNullOrWhiteSpace(CurrentWorkareaRelativePath) &&
                choices.All(x => !x.Path.Equals(CurrentWorkareaRelativePath, StringComparison.OrdinalIgnoreCase)))
            {
                string label = Path.GetFileName(CurrentWorkareaRelativePath.TrimEnd('/', '\\'));
                if (string.IsNullOrWhiteSpace(label))
                    label = "待创建工区";
                choices.Add(new WorkareaChoice(CurrentWorkareaRelativePath, $"待创建：{label}"));
            }

            foreach (WorkareaChoice choice in choices)
                _headerWorkareaSelector.Items.Add(choice);

            int selected = 0;
            for (int i = 0; i < _headerWorkareaSelector.Items.Count; i++)
            {
                if (_headerWorkareaSelector.Items[i] is WorkareaChoice choice &&
                    choice.Path.Equals(CurrentWorkareaRelativePath, StringComparison.OrdinalIgnoreCase))
                {
                    selected = i;
                    break;
                }
            }
            _headerWorkareaSelector.SelectedIndex = selected;

            string tooltip = _headerWorkareaSelector.SelectedItem is WorkareaChoice selectedChoice &&
                             !string.IsNullOrWhiteSpace(selectedChoice.Path)
                ? $"当前工区：{selectedChoice.Path}。切换本身不会创建目录。"
                : "当前未选择工区；纯工具输出会手动选择位置，项目管理型写入仍会要求目标工区。";
            _toolTip.SetToolTip(_headerWorkareaSelector, tooltip);
            RefreshCurrentWorkareaDirectoryButton();
        }
        finally
        {
            _syncingHeaderWorkareaSelector = false;
        }
    }

    private void RefreshCurrentWorkareaDirectoryButton()
    {
        if (!string.IsNullOrWhiteSpace(CurrentWorkareaRelativePath) &&
            Directory.Exists(Root) &&
            MagOps.Infrastructure.Storage.ProjectPaths.HasProject(Root))
        {
            bool registered = _workspace.GetWorkareas(Root).Any(workarea =>
                workarea.Path.Equals(CurrentWorkareaRelativePath, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(CurrentWorkareaId) ||
                 workarea.WorkareaId.Equals(CurrentWorkareaId, StringComparison.OrdinalIgnoreCase)));
            if (!registered)
            {
                _openCurrentWorkareaDirectory.Enabled = false;
                _openCurrentWorkareaDirectory.ToolTipText = "当前工区身份未在项目中登记或已变化；请重新选择有效工区。";
                return;
            }
        }

        bool available = CurrentWorkareaDirectoryResolver.TryResolve(
            Root,
            CurrentWorkareaRelativePath,
            out string directory,
            out string reason);
        _openCurrentWorkareaDirectory.Enabled = available;
        _openCurrentWorkareaDirectory.ToolTipText = available ? directory : reason;
    }

    private void OpenCurrentWorkareaDirectory()
    {
        if (!CurrentWorkareaDirectoryResolver.TryResolve(
                Root,
                CurrentWorkareaRelativePath,
                out string directory,
                out string reason))
        {
            RefreshCurrentWorkareaDirectoryButton();
            SetStatus(reason);
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = directory,
            UseShellExecute = true
        });
    }

    private void RefreshWorkareaContextSelectors()
    {
        bool initialized =
            !string.IsNullOrWhiteSpace(Root) &&
            Directory.Exists(Root) &&
            MagOps.Infrastructure.Storage.ProjectPaths.HasProject(Root);

        if (_workareaContextSelectors.Count > 0)
        {
            if (!initialized)
            {
                _syncingWorkareaContexts = true;
                try
                {
                    foreach (FlowLayoutPanel selector in _workareaContextSelectors)
                        PopulateWorkareaSelector(selector, [], initialized: false);
                }
                finally
                {
                    _syncingWorkareaContexts = false;
                }
            }
            else
            {
                EnsureRememberedWorkareaContextLoaded();

                List<WorkareaRecord> rows = _workspace.GetWorkareas(Root);
                var choices = new List<WorkareaChoice>
                {
                    new("", "未选择工区")
                };
                choices.AddRange(rows.Select(x =>
                    new WorkareaChoice(x.Path, x.DisplayName)));

                if (!string.IsNullOrWhiteSpace(CurrentWorkareaRelativePath) &&
                    choices.All(x => !x.Path.Equals(
                        CurrentWorkareaRelativePath,
                        StringComparison.OrdinalIgnoreCase)))
                {
                    string label = Path.GetFileName(
                        CurrentWorkareaRelativePath.TrimEnd('/', '\\'));
                    if (string.IsNullOrWhiteSpace(label))
                        label = "待创建工区";
                    choices.Add(new WorkareaChoice(
                        CurrentWorkareaRelativePath,
                        $"待创建：{label}"));
                }

                _syncingWorkareaContexts = true;
                try
                {
                    foreach (FlowLayoutPanel selector in _workareaContextSelectors)
                        PopulateWorkareaSelector(selector, choices, initialized: true);
                }
                finally
                {
                    _syncingWorkareaContexts = false;
                }
            }
        }

        RefreshHeaderWorkareaSelector();
    }

    private void SynchronizeWorkareaSelectors(FlowLayoutPanel source)
    {
        _syncingWorkareaContexts = true;
        try
        {
            foreach (FlowLayoutPanel selector in _workareaContextSelectors)
            {
                if (ReferenceEquals(selector, source))
                    continue;

                foreach (RadioButton radio in selector.Controls.OfType<RadioButton>())
                {
                    string path = radio.Tag?.ToString() ?? "";
                    radio.Checked = path.Equals(
                        CurrentWorkareaRelativePath,
                        StringComparison.OrdinalIgnoreCase);
                }
            }
        }
        finally
        {
            _syncingWorkareaContexts = false;
        }

        RefreshHeaderWorkareaSelector();
    }

    private void EnsureRememberedWorkareaContextLoaded()
    {
        if (string.IsNullOrWhiteSpace(Root) ||
            !Directory.Exists(Root) ||
            !MagOps.Infrastructure.Storage.ProjectPaths.HasProject(Root))
        {
            return;
        }

        string fullRoot = Path.GetFullPath(Root);
        if (_workareaContextLoadedRoot.Equals(fullRoot, StringComparison.OrdinalIgnoreCase))
            return;

        _workareaContextLoadedRoot = fullRoot;

        UserContextState state = _userContext.Load(Root);
        string remembered = (state.CurrentWorkareaPath ?? "")
            .Replace('\\', '/')
            .Trim('/')
            .Trim();
        if (remembered.Length == 0)
            return;

        List<WorkareaRecord> rows = _workspace.GetWorkareas(Root);
        WorkareaRecord? match = rows.FirstOrDefault(x =>
            x.Path.Equals(remembered, StringComparison.OrdinalIgnoreCase));

        if (match is null)
        {
            string candidate = Path.Combine(
                Root,
                remembered.Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(candidate))
            {
                rows = _workspace.SyncWorkareas(Root);
                match = rows.FirstOrDefault(x =>
                    x.Path.Equals(remembered, StringComparison.OrdinalIgnoreCase));
            }
        }

        if (match is not null)
        {
            SetWorkareaContext(match);
            return;
        }

        // A stale preference must never recreate a deleted/renamed workarea.
        _userContext.SaveCurrentWorkarea(Root, "");
    }

    private bool SetCurrentWorkareaRelativePath(
        string relativePath,
        bool refreshSelectors = true,
        bool confirmContextSwitch = true)
    {
        string normalized = (relativePath ?? "")
            .Replace('\\', '/')
            .Trim('/')
            .Trim();

        WorkareaRecord? canonical = string.IsNullOrWhiteSpace(normalized)
            ? null
            : _workspace.GetWorkareas(Root).FirstOrDefault(x =>
                x.Path.Equals(normalized, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(normalized) && canonical is null)
            throw new InvalidOperationException(
                $"当前工区必须来自 Workspace 权威记录，禁止仅按路径建立上下文：{normalized}");

        bool workareaChanged =
            !CurrentWorkareaRelativePath.Equals(normalized, StringComparison.OrdinalIgnoreCase) ||
            !(canonical?.WorkareaId ?? "").Equals(CurrentWorkareaId, StringComparison.OrdinalIgnoreCase);

        if (workareaChanged &&
            confirmContextSwitch &&
            !ConfirmContextSwitch(
                string.IsNullOrWhiteSpace(normalized)
                    ? "清除当前工区"
                    : $"切换到工区“{normalized}”"))
        {
            if (refreshSelectors)
                RefreshWorkareaContextSelectors();
            return false;
        }

        if (workareaChanged)
        {
            ResetCoordinateWorkbenchForContextChange();
            ResetFieldOperationsContext();

            // Route sources are workarea-specific. Never carry a valid PLY/CSV
            // path from one workarea into another simply because the file still
            // exists on disk.
            _lastGeneratedRoutePlyPath = "";
            ResetSiteEditor();
            _routeWorkspace.ResetForContextChange();
            _routePartitionContexts.Clear();
            _restoredSubdivisionSnapshot = null;
            _routeActivePartition = null;
            _routeSubdivisionPreparation = null;
            _routeSubdivisionPreparedFingerprint = "";
            _routeInference = null;
            _routePlan = null;
            _routeLoadedFingerprint = "";
            _routeEditedSorties.Clear();
            _routeReversedSorties.Clear();
            _routeBoundaryAdjustedSorties.Clear();
            SetRouteWorkspaceDirty(false);

            SetWorkareaContext(canonical);
        }

        if (!string.IsNullOrWhiteSpace(Root) &&
            Directory.Exists(Root) &&
            MagOps.Infrastructure.Storage.ProjectPaths.HasProject(Root))
        {
            _userContext.SaveCurrentWorkarea(Root, normalized);
            _workareaContextLoadedRoot = Path.GetFullPath(Root);
        }

        if (refreshSelectors)
            RefreshWorkareaContextSelectors();

        return true;
    }

    private void ResetWorkareaContext()
    {
        // Project switching must not overwrite the previous project's saved context.
        SetWorkareaContext(null);
        _workareaContextLoadedRoot = "";
        ResetCoordinateWorkbenchForContextChange();
        ResetFieldOperationsContext();
        _lastGeneratedRoutePlyPath = "";
        _routeWorkspace.ResetForContextChange();
        _routePartitionContexts.Clear();
        _restoredSubdivisionSnapshot = null;
        _routeActivePartition = null;
        _routeSubdivisionPreparation = null;
        _routeSubdivisionPreparedFingerprint = "";
        _routeInference = null;
        _routePlan = null;
        _routeLoadedFingerprint = "";
        _routeEditedSorties.Clear();
        _routeReversedSorties.Clear();
        _routeBoundaryAdjustedSorties.Clear();
        SetRouteWorkspaceDirty(false);
        ResetArtifactWorkspaceForProjectChange();
        ResetProjectDisplayForContextChange();
        _coordinateWorkspace.ShowRegistryRows([]);
        RefreshWorkareaContextSelectors();
    }

    private bool TrySetCurrentWorkareaByDisplayName(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            return false;

        WorkareaRecord? row = _workspace.GetWorkareas(Root)
            .FirstOrDefault(x =>
                x.DisplayName.Equals(displayName, StringComparison.OrdinalIgnoreCase) ||
                x.Path.Equals(displayName, StringComparison.OrdinalIgnoreCase));
        if (row is null)
            return false;

        return SetCurrentWorkareaRelativePath(row.Path);
    }

    private string SelectWorkareaRelativePath(string purpose)
    {
        EnsureInitialized();
        EnsureRememberedWorkareaContextLoaded();

        List<WorkareaRecord> rows = _workspace.GetWorkareas(Root);
        if (rows.Count == 0)
            rows = _workspace.SyncWorkareas(Root);

        if (!string.IsNullOrWhiteSpace(CurrentWorkareaRelativePath))
        {
            WorkareaRecord? cached = rows.FirstOrDefault(x =>
                x.Path.Equals(CurrentWorkareaRelativePath, StringComparison.OrdinalIgnoreCase));
            if (cached is not null || CurrentWorkareaRelativePath.StartsWith("10_工区/", StringComparison.OrdinalIgnoreCase))
                return CurrentWorkareaRelativePath;
        }

        if (rows.Count == 1)
        {
            if (!SetCurrentWorkareaRelativePath(rows[0].Path))
                throw new OperationCanceledException();
            return rows[0].Path;
        }

        if (rows.Count > 1)
        {
            var labels = new List<string>();
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (WorkareaRecord row in rows)
            {
                string label = $"{row.DisplayName}  [{row.Path}]";
                labels.Add(label);
                map[label] = row.Path;
            }

            var values = PromptDialog.Show(this, purpose,
            [
                new PromptField(
                    "workarea",
                    "目标工区",
                    labels[0],
                    labels,
                    Required: true)
            ]);
            if (values is null)
                throw new OperationCanceledException();

            string selected = values["workarea"];
            if (!map.TryGetValue(selected, out string? path))
                throw new InvalidOperationException($"无法识别目标工区：{selected}");

            if (!SetCurrentWorkareaRelativePath(path))
                throw new OperationCanceledException();
            return path;
        }

        // No workarea exists yet. Nothing is created here. The first real write
        // creates 10_工区/<name>/<stage> lazily.
        var create = PromptDialog.Show(this, purpose,
        [
            new PromptField(
                "name",
                "当前未发现工区，请输入新工区名称",
                "",
                Required: true)
        ],
        note: "这里只记录目标工区上下文；业务目录在实际写入时才创建。");
        if (create is null)
            throw new OperationCanceledException();

        string name = create["name"].Trim();
        if (name.Length == 0)
            throw new InvalidOperationException("工区名称不能为空。");

        WorkareaRecord created = _workspace.RegisterWorkarea(Root, name);
        if (!SetCurrentWorkareaRelativePath(created.Path))
            throw new OperationCanceledException();
        return created.Path;
    }

    private string EnsureManagedOutputDirectory(string operationKey, string purpose)
    {
        EnsureInitialized();
        FolderOperationEntry operation = _folderLayout.GetOperation(operationKey);

        string workarea = "";
        if (operation.Scope.Equals("workarea", StringComparison.OrdinalIgnoreCase))
            workarea = SelectWorkareaRelativePath(purpose);

        string path = _folderLayout.EnsureOperationDirectory(
            Root,
            operationKey,
            workarea);

        if (!string.IsNullOrWhiteSpace(workarea))
        {
            _workspace.SyncWorkareas(Root);
            RefreshWorkareaContextSelectors();
        }

        return path;
    }

    private string ManagedOutputPreview(string operationKey, string workareaRelativePath = "")
    {
        return _folderLayout.ResolveOperationDirectory(
            Root,
            operationKey,
            workareaRelativePath);
    }

    private bool IsProjectInitialized()
    {
        return !string.IsNullOrWhiteSpace(Root) &&
               Directory.Exists(Root) &&
               MagOps.Infrastructure.Storage.ProjectPaths.HasProject(Root);
    }

    private bool TryGetManagedOutputContext(
        out string projectName,
        out string workareaRelativePath,
        out string workareaName)
    {
        projectName = "";
        workareaRelativePath = "";
        workareaName = "";

        if (!IsProjectInitialized())
            return false;

        EnsureRememberedWorkareaContextLoaded();
        if (string.IsNullOrWhiteSpace(CurrentWorkareaRelativePath))
            return false;

        WorkspaceRecord workspace = _workspace.GetWorkspace(Root);
        projectName = WorkspaceService.Slug(
            string.IsNullOrWhiteSpace(workspace.DisplayName)
                ? "项目"
                : workspace.DisplayName);

        string selectedWorkareaRelativePath = CurrentWorkareaRelativePath;
        workareaRelativePath = selectedWorkareaRelativePath;

        WorkareaRecord? workarea = _workspace
            .GetWorkareas(Root)
            .FirstOrDefault(x => x.Path.Equals(
                selectedWorkareaRelativePath,
                StringComparison.OrdinalIgnoreCase));

        string candidate = workarea?.DisplayName
            ?? Path.GetFileName(
                selectedWorkareaRelativePath.TrimEnd('/', '\\'));
        workareaName = WorkspaceService.Slug(
            string.IsNullOrWhiteSpace(candidate)
                ? "工区"
                : candidate);

        return true;
    }

    private string ResolveToolOutputFilePath(
        string operationKey,
        string purpose,
        string suggestedFileName,
        string filter,
        string defaultExtension)
    {
        string safeName = Path.GetFileName(suggestedFileName.Trim());
        if (string.IsNullOrWhiteSpace(safeName))
            throw new InvalidOperationException("输出文件名不能为空。");

        FolderOperationEntry operation = _folderLayout.GetOperation(operationKey);
        string? managedDirectory = null;

        if (operation.Scope.Equals(
                "project",
                StringComparison.OrdinalIgnoreCase) &&
            IsProjectInitialized())
        {
            managedDirectory = _folderLayout.ResolveOperationDirectory(
                Root,
                operationKey);
        }
        else if (operation.Scope.Equals(
                     "workarea",
                     StringComparison.OrdinalIgnoreCase) &&
                 TryGetManagedOutputContext(
                     out _,
                     out string workarea,
                     out _))
        {
            managedDirectory = _folderLayout.ResolveOperationDirectory(
                Root,
                operationKey,
                workarea);


        }

        var global = new GlobalWorkspaceService();
        var preferences = global.Load();
        if (_saveElsewhere || (preferences.Root.Length > 0 && !preferences.AutomaticOutput)) managedDirectory = null;
        else if (managedDirectory is null && preferences.Root.Length > 0) {
            managedDirectory = global.Standalone(operationKey); Directory.CreateDirectory(managedDirectory);
        }

        if (!string.IsNullOrWhiteSpace(managedDirectory))
        {
            Directory.CreateDirectory(managedDirectory);
            string managedPath =
                Path.Combine(managedDirectory, safeName);
            if (preferences.Root.Length > 0 && preferences.AutomaticOutput)
                managedPath = GlobalWorkspaceService.UniqueFile(managedPath);
            else ConfirmOverwriteFilesIfNeeded([managedPath], purpose);
            return TrackFileOutput(managedPath, operationKey);
        }

        using var dialog = new SaveFileDialog
        {
            Title = purpose + "（临时使用：请选择输出位置）",
            Filter = filter,
            FileName = safeName,
            DefaultExt = defaultExtension.TrimStart('.'),
            AddExtension = true,
            OverwritePrompt = true
        };

        FileInteractions.For(this, operationKey, "output").Prepare(dialog);

        if (dialog.ShowDialog(this) != DialogResult.OK)
            throw new OperationCanceledException();

        return TrackFileOutput(dialog.FileName, operationKey);
    }

    private void ConfirmOverwriteFilesIfNeeded(
        IEnumerable<string> paths,
        string purpose)
    {
        string[] existing = paths
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(Path.GetFullPath)
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (existing.Length == 0)
            return;

        string details = string.Join(
            Environment.NewLine,
            existing
                .Take(8)
                .Select(x =>
                    $"• {Path.GetFileName(x)}"));

        if (existing.Length > 8)
        {
            details +=
                Environment.NewLine +
                $"• …另有 {existing.Length - 8} 个文件";
        }

        DialogResult answer = MessageBox.Show(
            this,
            $"{purpose}将覆盖 {existing.Length} 个已有文件：" +
            Environment.NewLine +
            Environment.NewLine +
            details +
            Environment.NewLine +
            Environment.NewLine +
            "是否继续覆盖？",
            "确认覆盖已有文件",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);

        if (answer != DialogResult.Yes)
            throw new OperationCanceledException();
    }

    private string ResolveToolOutputDirectory(
        string operationKey,
        string purpose)
    {
        var global = new GlobalWorkspaceService(); var preferences = global.Load();
        if (_saveElsewhere || (preferences.Root.Length > 0 && !preferences.AutomaticOutput))
            return ChooseCustomOutputDirectory(operationKey, purpose);
        FolderOperationEntry operation = _folderLayout.GetOperation(operationKey);

        if (operation.Scope.Equals(
                "project",
                StringComparison.OrdinalIgnoreCase) &&
            IsProjectInitialized())
        {
            return _folderLayout.EnsureOperationDirectory(
                Root,
                operationKey);
        }

        if (operation.Scope.Equals(
                "workarea",
                StringComparison.OrdinalIgnoreCase) &&
            TryGetManagedOutputContext(
                out _,
                out string workarea,
                out _))
        {
            string managed = _folderLayout.EnsureOperationDirectory(
                Root,
                operationKey,
                workarea);

            _workspace.SyncWorkareas(Root);
            RefreshWorkareaContextSelectors();
            return managed;
        }

        if (preferences.Root.Length > 0) {
            string output = global.Standalone(operationKey); Directory.CreateDirectory(output); return output;
        }
        return ChooseCustomOutputDirectory(operationKey, purpose);
    }

    private DialogResult PrepareAutomaticSave(SaveFileDialog dialog, string operation)
    {
        var preferences = new GlobalWorkspaceService().Load();
        if (preferences.Root.Length == 0 || !preferences.AutomaticOutput || _saveElsewhere)
            return dialog.ShowDialog(this);
        dialog.FileName = ResolveToolOutputFilePath(operation, dialog.Title, dialog.FileName, dialog.Filter, Path.GetExtension(dialog.FileName));
        return DialogResult.OK;
    }

    private bool _saveElsewhere;
    private string ChooseCustomOutputDirectory(string operationKey, string purpose)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = purpose + "（临时使用：请选择输出文件夹）",
            ShowNewFolderButton = true,
            SelectedPath = GetToolOutputInitialDirectory()
        };

        FileInteractions.For(this, operationKey, "output").Prepare(dialog);
        if (dialog.ShowDialog(this) != DialogResult.OK)
            throw new OperationCanceledException();

        return dialog.SelectedPath;
    }

    private string GetToolOutputInitialDirectory()
    {
        if (Directory.Exists(Root))
            return Root;

        string documents = Environment.GetFolderPath(
            Environment.SpecialFolder.MyDocuments);
        return Directory.Exists(documents)
            ? documents
            : Environment.CurrentDirectory;
    }

    private bool IsPathInsideCurrentProject(string path)
    {
        if (!IsProjectInitialized() ||
            string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string rootFull = Path.GetFullPath(Root)
            .TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
        string full = Path.GetFullPath(path);

        if (full.Equals(
                rootFull,
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string prefix = rootFull + Path.DirectorySeparatorChar;
        return full.StartsWith(
            prefix,
            StringComparison.OrdinalIgnoreCase);
    }

    private void RegisterGeneratedBusinessFile(string path, string sourceKind)
    {
        if (!File.Exists(path)) return;
        if (!IsPathInsideCurrentProject(path)) { RememberLatestGeneratedBusinessFile(path); return; }

        ArtifactRecord row;
        try { row = _artifacts.RegisterGeneratedFile(Root, path, sourceKind); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        { throw new InvalidOperationException($"文件已写出，但登记失败：{path}\r\n{ex.Message}", ex); }
        RememberLatestGeneratedBusinessFile(path);
        if (_artifactWorkspace.HasGridColumns())
            ApplyArtifactChanges([row]);
    }

    private void RegisterGeneratedBusinessFiles(
        IEnumerable<string> paths,
        string sourceKind)
    {
        var changed = new List<ArtifactRecord>();
        var completedPaths = new List<string>();

        foreach (string raw in paths
                     .Where(x => !string.IsNullOrWhiteSpace(x))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string full = Path.GetFullPath(raw);
            if (!File.Exists(full)) continue;
            if (full.EndsWith(GeneratedArtifactIdentity.Suffix, StringComparison.OrdinalIgnoreCase)) continue;
            if (!IsPathInsideCurrentProject(full)) { completedPaths.Add(full); continue; }

            try { changed.Add(_artifacts.RegisterGeneratedFile(Root, full, sourceKind)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            { throw new InvalidOperationException($"文件已写出，但本批登记未完成：{full}\r\n{ex.Message}", ex); }
            completedPaths.Add(full);
        }

        if (completedPaths.Count > 0) RememberLatestGeneratedBusinessFile(completedPaths[^1]);
        if (changed.Count > 0 &&
            _artifactWorkspace.HasGridColumns())
        {
            ApplyArtifactChanges(changed);
        }
    }

    private void RememberLatestGeneratedBusinessFile(string path)
    {
        if (!File.Exists(path)) return;
        if (_pendingFileOutputs.Remove(Path.GetFullPath(path), out var pending)) { pending.OutputSucceeded(path); return; }
        var metadata = GeneratedArtifactIdentity.Inspect(path).Metadata;
        bool projectLevel = metadata?.ContextStatus == "PROJECT";
        var row = IsPathInsideCurrentProject(path) ? _artifacts.List(Root).FirstOrDefault(x =>
            Path.GetFullPath(Path.Combine(Root, x.Path)).Equals(Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)) : null;
        if (metadata is null && row is not null) projectLevel = string.IsNullOrWhiteSpace(row.WorkareaRef);
        FileInteractions.For(this, row?.SourceKind ?? "generated-result", "output", projectLevel)
            .OutputSucceeded(path, artifactId: row?.ArtifactId ?? metadata?.ArtifactId ?? "");
    }

    private void ClearLatestGeneratedBusinessFile()
    {
        _lastGeneratedBusinessFile = "";
        _lastGeneratedIsDirectory = false;
        _openLatestGeneratedArtifact.Enabled = false;
        _toolTip.SetToolTip(_openLatestGeneratedArtifact,
            "生成或保存成果后，可从此处直接定位最近生成的文件。");
    }

    private void OpenFolderLayoutConfiguration()
    {
        _folderLayout.EnsureEditableConfigurationFile();
        string path = _folderLayout.ConfigurationPath;
        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });
    }
}
