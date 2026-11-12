using MagOps.Core.Domain;
using MagOps.Infrastructure.Services;

namespace MagOps.Desktop;

public sealed partial class MainForm
{
    private void BuildArtifactTab()
    {
        RegisterFeatureControl(
            FeatureIds.ArtifactWorkspace,
            _artifactWorkspace,
            activated: QueueActivation,
            contextChanged: _ => QueueActivation());

        void QueueActivation() => QueueFeatureActivation(FeatureIds.ArtifactWorkspace, () =>
            RunUi("刷新资料台账", RefreshArtifacts));
    }

    private async Task ChooseAndImportRawMaterialsAsync()
    {
        EnsureInitialized();
        using var dialog = new OpenFileDialog
        {
            Title = "选择要复制归档的原始资料",
            Filter = "所有文件 (*.*)|*.*",
            Multiselect = true,
            CheckFileExists = true
        };
        FileInteractions.For(this, "raw-intake").Prepare(dialog);
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        await ImportRawMaterialPathsAsync(dialog.FileNames);
    }

    private async Task ImportRawMaterialPathsAsync(IEnumerable<string> sourcePaths)
    {
        EnsureInitialized();
        string workarea = SelectWorkareaRelativePath("添加原始资料");
        string projectRoot = Root;
        long revision = _projectWorkareaContext.Current.Revision;
        bool wasEnabled = Enabled;
        Enabled = false;
        SetStatus("正在归档原始资料…");
        _artifactWorkspace.SetRawImportHealth("正在归档原始资料，请在进度窗口查看或取消。", healthy: true);
        _artifactWorkspace.ShowImportResult($"正在处理…\r\n项目：{projectRoot}\r\n工区：{workarea}", activate: false);
        try { await ImportRawMaterialPathsCoreAsync(sourcePaths, projectRoot, workarea, revision); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            if (!IsDisposed && !Disposing && _projectWorkareaContext.Current.Revision == revision &&
                Root.Equals(projectRoot, StringComparison.OrdinalIgnoreCase))
            {
                SetStatus($"原始资料导入未完成：{ex.Message}");
                _artifactWorkspace.SetRawImportHealth("原始资料导入未完成，请查看本次归档详情后重试。", healthy: false);
                _artifactWorkspace.ShowImportResult("本次原始资料归档未完成\r\n" +
                    ex + "\r\n\r\n请检查来源路径和目标项目后重试；源文件不会被移动或删除。", activate: true);
            }
            throw;
        }
        finally { if (!IsDisposed) Enabled = wasEnabled; }
    }

    private async Task ImportRawMaterialPathsCoreAsync(
        IEnumerable<string> sourcePaths, string projectRoot, string workarea, long revision)
    {
        var existingHashes = _artifactCache
            .Where(x =>
                !string.IsNullOrWhiteSpace(x.Sha256) &&
                (x.Role.Equals("raw", StringComparison.OrdinalIgnoreCase) ||
                 x.SourceKind.Equals("raw_import", StringComparison.OrdinalIgnoreCase)) &&
                x.Path.StartsWith(workarea.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase))
            .GroupBy(x => x.Sha256!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key.ToLowerInvariant(),
                g => g.First().Path,
                StringComparer.OrdinalIgnoreCase);

        using var progressForm = new RawImportProgressForm();
        var progress = new Progress<RawImportProgress>(progressForm.Report);

        RawImportBatchResult result;
        try
        {
            progressForm.Show(this);
            result = await _rawImport.ImportAsync(
                projectRoot,
                workarea,
                sourcePaths,
                existingHashes,
                progress,
                progressForm.CancellationToken);
        }
        finally
        {
            if (!progressForm.IsDisposed)
                progressForm.CloseAfterOperation();
        }

        _workspace.SyncWorkareas(projectRoot);
        IReadOnlyList<ArtifactRecord> changedRows =
            _artifacts.RegisterRawImportBatch(projectRoot, result);
        if (IsDisposed || Disposing || _projectWorkareaContext.Current.Revision != revision ||
            !Root.Equals(projectRoot, StringComparison.OrdinalIgnoreCase)) return;
        RefreshWorkareaContextSelectors();
        ApplyArtifactChanges(changedRows);
        _lastRawImportDirectory = result.DestinationDirectory;
        if (result.Status != "CANCELLED")
            FileInteractions.For(this, "raw-intake").InputSucceeded(result.Items
                .Where(x => x.Status is RawImportStatuses.Archived or RawImportStatuses.SkippedDuplicate)
                .Select(x => x.SourcePath).ToArray());

        if (result.Status == "COMPLETED")
            FileInteractions.For(this, "raw-intake", "output").OutputSucceeded(result.DestinationDirectory, directory: true);
        RefreshProject();

        string resultText = result.Status switch
        {
            "CANCELLED" => "原始资料导入已取消；已完成的文件保留并已登记。",
            "PARTIAL" => "原始资料导入部分完成；请检查失败项目。",
            "FAILED" => "原始资料导入未成功完成。",
            _ => "原始资料归档完成。"
        };

        SetStatus($"{resultText} 已归档 {result.ArchivedCount}，重复跳过 {result.SkippedDuplicateCount}，失败 {result.FailedCount}。");
        _artifactWorkspace.SetRawImportHealth(
            $"{resultText} 已归档 {result.ArchivedCount}，重复跳过 {result.SkippedDuplicateCount}，失败 {result.FailedCount}。可点击“打开最近归档”查看。",
            healthy: result.Status == "COMPLETED");
        _artifactWorkspace.ShowImportResult(
            $"{resultText}\r\n\r\n" +
            $"项目：{projectRoot}\r\n工区：{workarea}\r\n" +
            $"已归档：{result.ArchivedCount}\r\n" +
            $"重复跳过：{result.SkippedDuplicateCount}\r\n" +
            $"失败：{result.FailedCount}\r\n" +
            $"归档目录：{result.DestinationDirectory}\r\n" +
            $"导入清单：{result.ManifestPath}\r\n" +
            $"脱敏清单：{Path.Combine(result.DestinationDirectory, "导入清单_脱敏.json")}\r\n\r\n" +
            "源文件未移动或删除；已归档文件通过 SHA-256 校验。" +
            string.Concat(result.Items.Where(x => x.Status == RawImportStatuses.Failed)
                .Select(x => $"\r\n失败：{x.SourcePath}\r\n{x.Note}")), activate: true);
    }

    private void OpenLastRawImportFolder()
    {
        if (string.IsNullOrWhiteSpace(_lastRawImportDirectory) ||
            !Directory.Exists(_lastRawImportDirectory))
        {
            throw new DirectoryNotFoundException("当前会话还没有可打开的原始资料归档批次。");
        }

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = _lastRawImportDirectory,
            UseShellExecute = true
        });
    }

    private void RefreshRawImportHealth()
    {
        if (string.IsNullOrWhiteSpace(Root) || !Directory.Exists(Root))
            return;

        IReadOnlyList<string> partials = _rawImport.FindPartialFiles(Root, 201);
        _artifactWorkspace.SetRawImportHealth(
            partials.Count == 0
                ? "原始资料导入：未发现未完成批次。"
                : $"原始资料导入：发现 {partials.Count}{(partials.Count >= 201 ? "+" : "")} 个未完成临时文件，请检查。",
            healthy: partials.Count == 0);
    }

    private void InspectAndCleanupPartialImports()
    {
        EnsureInitialized();
        IReadOnlyList<string> partials = _rawImport.FindPartialFiles(Root, 500);
        if (partials.Count == 0)
        {
            MessageBox.Show(
                this,
                "未发现 .magops-partial 未完成导入文件。",
                "原始资料导入",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            RefreshRawImportHealth();
            return;
        }

        string preview = string.Join(
            "\r\n",
            partials.Take(12).Select(x => Path.GetRelativePath(Root, x)));
        if (partials.Count > 12)
            preview += $"\r\n…另有 {partials.Count - 12} 个";

        DialogResult answer = MessageBox.Show(
            this,
            $"发现 {partials.Count} 个未完成导入临时文件：\r\n\r\n{preview}\r\n\r\n" +
            "是否删除这些 .magops-partial 文件？已完整归档的正式文件不会删除。",
            "清理未完成导入",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);
        if (answer != DialogResult.Yes)
            return;

        int removed = _rawImport.CleanupPartialFiles(partials);
        RefreshRawImportHealth();
        MessageBox.Show(
            this,
            $"已清理 {removed} 个未完成临时文件。",
            "清理完成",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private void ResetArtifactWorkspaceForProjectChange()
    {
        _artifactBrowseScope = "";
        _artifactPreparedRoot = "";
        _artifactCache.Clear();
        _lastRawImportDirectory = "";
        _artifactWorkspace.ResetForProjectChange();
    }

    private void RefreshArtifacts()
    {
        if (!MagOps.Infrastructure.Storage.ProjectPaths.HasProject(Root))
        {
            ResetArtifactWorkspaceForProjectChange();
            return;
        }

        ArtifactGridState state = _artifactWorkspace.CaptureGridState();

        if (!_artifactPreparedRoot.Equals(Root, StringComparison.OrdinalIgnoreCase))
        {
            ArtifactModelMigrationResult migration = _artifacts.EnsureV03Metadata(Root);
            _artifactPreparedRoot = Root;
            if (migration.Changed > 0)
            {
                SetStatus(
                    $"资料模型已升级：{migration.Changed} 条；" +
                    $"工区归属 {migration.Assigned}，项目级 {migration.ProjectLevel}，待确认 {migration.ReviewRequired}。");
            }
        }

        IndependentArtifactTransaction.Recover(Root, _store);
        _artifacts.RefreshFreshnessStatus(Root);
        _artifactCache = _artifacts.List(Root)
            .OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        RefreshArtifactFilterOptions();
        RefreshArtifactScopeList();
        ApplyArtifactFilters(state);
        RefreshRawImportHealth();
        RefreshWorkareaContextSelectors();
        _artifactWorkspace.NormalizeLayout();
    }

    private void RefreshArtifactFilterOptions()
    {
        string current =
            _artifactWorkspace.GetExtensionFilterText();
        string[] extensions = _artifactCache
            .Select(x => Path.GetExtension(x.Path))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        _artifactWorkspace.SetExtensionOptions(
            extensions,
            current);
    }

    private void RefreshArtifactScopeList()
    {
        if (string.IsNullOrWhiteSpace(Root) ||
            !MagOps.Infrastructure.Storage.ProjectPaths.HasProject(Root))
        {
            return;
        }

        string currentKey = _artifactBrowseScope;
        string search =
            _artifactWorkspace.GetWorkareaSearchText();

        List<WorkareaRecord> workareas = _workspace.GetWorkareas(Root);
        var byId = workareas
            .Where(x => !string.IsNullOrWhiteSpace(x.WorkareaId))
            .ToDictionary(x => x.WorkareaId, StringComparer.OrdinalIgnoreCase);

        int projectCount = _artifactCache.Count(x =>
            string.Equals(x.WorkareaAssignmentStatus, "PROJECT", StringComparison.OrdinalIgnoreCase));
        int reviewCount = _artifactCache.Count(x =>
            string.Equals(x.WorkareaAssignmentStatus, "REVIEW_REQUIRED", StringComparison.OrdinalIgnoreCase));

        var items = new List<ArtifactScopeItem>
        {
            new("", "全部资料", _artifactCache.Count)
        };
        if (projectCount > 0)
            items.Add(new("__PROJECT__", "项目级", projectCount));
        if (reviewCount > 0)
            items.Add(new("__REVIEW__", "待确认归属", reviewCount));

        foreach (var group in _artifactCache
                     .Where(x =>
                         string.Equals(x.WorkareaAssignmentStatus, "ASSIGNED", StringComparison.OrdinalIgnoreCase) &&
                         !string.IsNullOrWhiteSpace(x.WorkareaRef))
                     .GroupBy(x => x.WorkareaRef, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(g =>
                         byId.TryGetValue(g.Key, out WorkareaRecord? w) ? w.DisplayName : g.Key,
                         StringComparer.OrdinalIgnoreCase))
        {
            if (!byId.TryGetValue(group.Key, out WorkareaRecord? workarea))
                continue;

            if (search.Length > 0 &&
                !workarea.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase) &&
                !workarea.WorkareaId.Contains(search, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            items.Add(new ArtifactScopeItem(
                workarea.WorkareaId,
                workarea.DisplayName,
                group.Count(),
                workarea.WorkareaId,
                workarea.Path));
        }

        _artifactWorkspace.SetScopeItems(
            items,
            currentKey);
        _artifactBrowseScope =
            _artifactWorkspace.GetSelectedScopeKey();
    }

    private void ApplyArtifactFilters()
    {
        ApplyArtifactFilters(_artifactWorkspace.CaptureGridState());
    }

    private void ApplyArtifactFilters(ArtifactGridState state)
    {
        IEnumerable<ArtifactRecord> rows = _artifactCache;

        if (_artifactBrowseScope.Equals("__PROJECT__", StringComparison.OrdinalIgnoreCase))
        {
            rows = rows.Where(x =>
                string.Equals(x.WorkareaAssignmentStatus, "PROJECT", StringComparison.OrdinalIgnoreCase));
        }
        else if (_artifactBrowseScope.Equals("__REVIEW__", StringComparison.OrdinalIgnoreCase))
        {
            rows = rows.Where(x =>
                string.Equals(x.WorkareaAssignmentStatus, "REVIEW_REQUIRED", StringComparison.OrdinalIgnoreCase));
        }
        else if (!string.IsNullOrWhiteSpace(_artifactBrowseScope))
        {
            rows = rows.Where(x =>
                string.Equals(x.WorkareaAssignmentStatus, "ASSIGNED", StringComparison.OrdinalIgnoreCase) &&
                x.WorkareaRef.Equals(_artifactBrowseScope, StringComparison.OrdinalIgnoreCase));
        }

        string textFilter = _artifactWorkspace.GetTextFilter();
        if (textFilter.Length > 0)
        {
            rows = rows.Where(x =>
                x.Name.Contains(textFilter, StringComparison.OrdinalIgnoreCase) ||
                x.Path.Contains(textFilter, StringComparison.OrdinalIgnoreCase) ||
                x.Notes.Contains(textFilter, StringComparison.OrdinalIgnoreCase));
        }

        string role = _artifactWorkspace.GetRoleFilterValue();
        if (!string.IsNullOrWhiteSpace(role))
        {
            rows = rows.Where(x =>
                x.Role.Equals(role, StringComparison.OrdinalIgnoreCase));
        }

        HashSet<string> extensions = ParseArtifactExtensionFilter(_artifactWorkspace.GetExtensionFilterText());
        if (extensions.Count > 0)
        {
            rows = rows.Where(x =>
                extensions.Contains(Path.GetExtension(x.Path)));
        }

        if (_artifactWorkspace.GetMissingOnly())
            rows = rows.Where(x => x.ChangeStatus == "MISSING");

        List<ArtifactRecord> desired = rows
            .OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _artifactWorkspace.ShowRows(
            desired,
            state);
        RefreshArtifactDetail();

        string scope =
            _artifactWorkspace.GetSelectedScopeLabel();
        string roleDisplay =
            _artifactWorkspace.GetRoleFilterDisplay();
        string extensionDisplay =
            _artifactWorkspace.GetExtensionFilterText();
        SetStatus(
            $"资料台账：显示 {desired.Count} / {_artifactCache.Count} 条｜范围 {scope}｜" +
            $"资料类型 {(roleDisplay.Length > 0 ? roleDisplay : "全部")}｜" +
            $"扩展名 {(extensionDisplay.Length > 0 ? extensionDisplay : "全部")}");
    }

    private void ApplyArtifactChanges(IEnumerable<ArtifactRecord> changedRows)
    {
        ArtifactGridState state = _artifactWorkspace.CaptureGridState();
        foreach (ArtifactRecord row in changedRows)
            UpsertArtifactCache(row);

        RefreshArtifactFilterOptions();
        RefreshArtifactScopeList();
        ApplyArtifactFilters(state);
    }

    private void UpsertArtifactCache(ArtifactRecord row)
    {
        int index = _artifactCache.FindIndex(x =>
            x.ArtifactId.Equals(row.ArtifactId, StringComparison.OrdinalIgnoreCase) ||
            x.Path.Equals(row.Path, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
            _artifactCache[index] = row;
        else
            _artifactCache.Add(row);
    }

    private void SetBrowseWorkareaAsCurrent()
    {
        if (_artifactWorkspace.GetSelectedScopeItem() is not ArtifactScopeItem item ||
            !item.IsWorkarea)
        {
            throw new InvalidOperationException("请先选择一个具体工区。");
        }

        WorkareaRecord row = _workspace.GetWorkareas(Root)
            .FirstOrDefault(x =>
                x.WorkareaId.Equals(item.WorkareaId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("该工区已不在当前工区登记中。");

        if (!SetCurrentWorkareaRelativePath(row.Path))
            return;

        SetStatus($"当前工作工区已设为：{row.DisplayName}");
    }

    private void RefreshArtifactDetail()
    {
        if (!_artifactWorkspace.TryGetCurrentArtifact(
                out ArtifactRecord? row) ||
            row is null)
        {
            _artifactWorkspace.ClearDetail();
            return;
        }

        _artifactWorkspace.SetDetail(
            row.Name,
            BuildArtifactDetailText(row));
    }

    private string BuildArtifactDetailText(ArtifactRecord row)
    {
        string workarea = ResolveArtifactWorkareaDisplay(row);
        string selected = row.Selected switch
        {
            true => "是",
            false => "否",
            null => "未指定"
        };

        string derived = row.DerivedFrom.Count == 0
            ? "—"
            : string.Join(Environment.NewLine + "  ", row.DerivedFrom);
        string previousPaths = row.PreviousPaths.Count == 0
            ? "—"
            : string.Join(Environment.NewLine + "  ", row.PreviousPaths);

        return
            $"资料 ID：{row.ArtifactId}\r\n" +
            $"文件名：{row.Name}\r\n" +
            $"相对路径：{row.Path}\r\n" +
            $"工区归属：{workarea}\r\n" +
            $"资料类型：{UiText.Display(row.Role)}\r\n" +
            $"处理阶段：{UiText.Display(row.ProcessingStage)}\r\n" +
            $"版本：{row.VersionLabel}\r\n" +
            $"质量状态：{UiText.Display(row.QualityStatus)}\r\n" +
            $"空间基准状态：{UiText.Display(row.SpatialReferenceStatus)}\r\n" +
            $"来源数据状态：{UiText.Display(row.SourceFreshnessStatus)}\r\n" +
            $"来源核验说明：{row.FreshnessReason}\r\n" +
            $"选用：{selected}\r\n" +
            $"变化状态：{UiText.Display(row.ChangeStatus)}\r\n" +
            $"哈希状态：{UiText.Display(row.HashStatus)}\r\n" +
            $"SHA-256：{row.Sha256 ?? "—"}\r\n" +
            $"大小：{row.SizeBytes:N0} B\r\n" +
            $"修改时间：{FormatArtifactTime(row.ModifiedAt)}\r\n" +
            $"来源类型：{row.SourceKind}\r\n" +
            $"来源引用：{(string.IsNullOrWhiteSpace(row.SourceRef) ? "—" : row.SourceRef)}\r\n" +
            $"派生自：{derived}\r\n" +
            $"历史路径：{previousPaths}\r\n" +
            $"备注：{(string.IsNullOrWhiteSpace(row.Notes) ? "—" : row.Notes)}";
    }

    private void ShowArtifactDetailDialog(ArtifactRecord row)
    {
        using var form = new Form
        {
            Text = $"资料详情 - {row.Name}",
            StartPosition = FormStartPosition.CenterParent,
            AutoScaleMode = AutoScaleMode.Dpi,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            MinimumSize = new Size(720, 520),
            Size = new Size(860, 650)
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(12)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var detail = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = SystemColors.Window,
            DetectUrls = false,
            Text = BuildArtifactDetailText(row)
        };
        layout.Controls.Add(detail, 0, 0);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = true,
            AutoScroll = false,
            Margin = new Padding(0, 8, 0, 0)
        };
        var close = new Button
        {
            Text = "关闭",
            AutoSize = true,
            MinimumSize = new Size(88, 34),
            DialogResult = DialogResult.OK
        };
        actions.Controls.Add(close);
        layout.Controls.Add(actions, 0, 1);

        form.Controls.Add(layout);
        form.AcceptButton = close;
        form.CancelButton = close;
        form.Shown += (_, _) => UiLayout.Normalize(form);
        form.DpiChanged += (_, _) =>
            form.BeginInvoke((Action)(() => UiLayout.Normalize(form)));
        form.ShowDialog(this);
    }

    private string ResolveArtifactWorkareaDisplay(ArtifactRecord row)
    {
        if (string.Equals(row.WorkareaAssignmentStatus, "PROJECT", StringComparison.OrdinalIgnoreCase))
            return "项目级";
        if (string.Equals(row.WorkareaAssignmentStatus, "REVIEW_REQUIRED", StringComparison.OrdinalIgnoreCase))
            return "待确认";

        WorkareaRecord? workarea = _workspace.GetWorkareas(Root)
            .FirstOrDefault(x =>
                x.WorkareaId.Equals(row.WorkareaRef, StringComparison.OrdinalIgnoreCase));
        return workarea is null
            ? $"未知工区（{row.WorkareaRef}）"
            : workarea.DisplayName;
    }

    private static string FormatArtifactTime(string value) =>
        DateTimeOffset.TryParse(value, out DateTimeOffset parsed)
            ? parsed.LocalDateTime.ToString("yyyy-MM-dd HH:mm")
            : value;

    private static HashSet<string> ParseArtifactExtensionFilter(string text)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text) ||
            text.Trim().Equals("全部扩展名", StringComparison.OrdinalIgnoreCase) ||
            text.Trim() is "*" or "*.*")
        {
            return result;
        }

        foreach (string raw in text.Split(
                     [',', '，', ';', '；', ' ', '\t'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string token = raw.Trim();
            if (token is "*" or "*.*")
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (token.StartsWith("*.", StringComparison.Ordinal))
                token = token[1..];
            else if (token.StartsWith('*'))
                token = token.TrimStart('*');

            if (token.Length == 0)
                continue;
            if (!token.StartsWith('.'))
                token = "." + token;

            result.Add(token);
        }

        return result;
    }

    private void EditArtifactProperty(ArtifactRecord row, string property)
    {
        PromptField field = property switch
        {
            nameof(ArtifactRecord.Role) => new PromptField(
                "value",
                "资料类型",
                row.Role,
                ["raw", "registry", "coordinate", "route", "route_result", "field", "processing",
                 "derived", "figure", "report", "external_software", "project_management",
                 "workarea", "other", "unclassified"],
                Required: true),

            nameof(ArtifactRecord.ProcessingStage) => new PromptField(
                "value",
                "处理阶段",
                row.ProcessingStage,
                ["raw", "registry", "coordinate", "route", "field", "processing", "reporting",
                 "gis", "project", "management", "derived", "unclassified"],
                Required: true),

            nameof(ArtifactRecord.VersionLabel) => new PromptField(
                "value",
                "版本",
                row.VersionLabel),

            nameof(ArtifactRecord.QualityStatus) => new PromptField(
                "value",
                "质量状态",
                row.QualityStatus,
                ["UNREVIEWED", "PASSED", "REVIEW_REQUIRED", "BLOCKED", "DEFERRED"],
                Required: true),

            nameof(ArtifactRecord.Notes) => new PromptField(
                "value",
                "备注",
                row.Notes,
                Multiline: true),

            _ => throw new InvalidOperationException("该属性不是可编辑资料字段。")
        };

        var values = PromptDialog.Show(
            this,
            $"编辑{field.Label} - {row.Name}",
            [field],
            width: 600);
        if (values is null)
            return;

        string value = values["value"];
        ArtifactRecord updated = property switch
        {
            nameof(ArtifactRecord.Role) =>
                _artifacts.Update(Root, row.Path, role: value),
            nameof(ArtifactRecord.ProcessingStage) =>
                _artifacts.Update(Root, row.Path, processingStage: value),
            nameof(ArtifactRecord.VersionLabel) =>
                _artifacts.Update(Root, row.Path, versionLabel: value),
            nameof(ArtifactRecord.QualityStatus) =>
                _artifacts.Update(Root, row.Path, qualityStatus: value),
            nameof(ArtifactRecord.Notes) =>
                _artifacts.Update(Root, row.Path, notes: value),
            _ => row
        };

        ApplyArtifactChanges([updated]);
    }

    private void ToggleArtifactSelected(ArtifactRecord row)
    {
        bool next = row.Selected != true;
        ArtifactRecord updated = _artifacts.Update(
            Root,
            row.Path,
            selected: next);
        ApplyArtifactChanges([updated]);
        SetStatus(next
            ? $"已选用：{row.Name}"
            : $"已取消选用：{row.Name}");
    }

    private ArtifactRecord CurrentArtifact() =>
        _artifactWorkspace.GetCurrentArtifact();

    private void RevealCurrentArtifact()
    {
        RevealArtifactInExplorer(CurrentArtifact());
    }

    private void OpenLatestGeneratedArtifact()
    {
        string path = _lastGeneratedBusinessFile;
        if (string.IsNullOrWhiteSpace(path) || !(_lastGeneratedIsDirectory ? Directory.Exists(path) : File.Exists(path)))
        {
            ClearLatestGeneratedBusinessFile();
            SetStatus("最近成果文件已不存在；请在资料与归档中刷新或重新生成成果。");
            return;
        }

        string? directory = _lastGeneratedIsDirectory ? path : Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            ClearLatestGeneratedBusinessFile();
            SetStatus("最近成果所在目录已不存在；请检查当前项目/工区。");
            return;
        }

        if (_lastGeneratedIsDirectory)
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            return;
        }
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = _lastGeneratedIsDirectory ? $"\"{path}\"" : $"/select,\"{path}\"",
            UseShellExecute = true
        });
    }

    private void RevealArtifactInExplorer(ArtifactRecord row)
    {
        EnsureInitialized();

        string full = Path.Combine(
            Root,
            row.Path.Replace('/', Path.DirectorySeparatorChar));
        string? directory = Path.GetDirectoryName(full);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            throw new DirectoryNotFoundException($"资料所在目录不存在：{directory}");

        if (File.Exists(full))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{full}\"",
                UseShellExecute = true
            });
        }
        else
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = directory,
                UseShellExecute = true
            });
            SetStatus($"资料文件当前缺失，已打开记录所在目录：{row.Name}");
        }
    }

    private void EditArtifact()
    {
        EnsureInitialized();
        ArtifactRecord row = CurrentArtifact();

        var values = PromptDialog.Show(
            this,
            $"编辑资料 - {row.Name}",
            [
                new("role", "资料类型", row.Role,
                    ["raw", "registry", "coordinate", "route", "route_result", "field", "processing",
                     "derived", "figure", "report", "external_software", "project_management",
                     "workarea", "other", "unclassified"]),
                new("stage", "处理阶段", row.ProcessingStage,
                    ["raw", "registry", "coordinate", "route", "field", "processing", "reporting",
                     "gis", "project", "management", "derived", "unclassified"]),
                new("version", "版本", row.VersionLabel),
                new("quality", "质量状态", row.QualityStatus,
                    ["UNREVIEWED", "PASSED", "REVIEW_REQUIRED", "BLOCKED", "DEFERRED"]),
                new("selected", "是否选用", row.Selected.HasValue ? row.Selected.Value.ToString() : "",
                    ["", "True", "False"]),
                new("derived", "派生自（每行一项）",
                    string.Join(Environment.NewLine, row.DerivedFrom),
                    Multiline: true),
                new("notes", "备注", row.Notes, Multiline: true)
            ],
            height: 680);
        if (values is null)
            return;

        bool? selected = values["selected"] switch
        {
            "True" => true,
            "False" => false,
            _ => null
        };

        ArtifactRecord updated = _artifacts.Update(
            Root,
            row.Path,
            values["role"],
            selected,
            values["quality"],
            values["stage"],
            values["notes"],
            SplitValues(values["derived"]),
            values["version"]);

        ApplyArtifactChanges([updated]);
    }

    private void AssignArtifactWorkarea()
    {
        ArtifactRecord row = CurrentArtifact();
        List<WorkareaRecord> workareas = _workspace.GetWorkareas(Root);

        var labels = new List<string> { "项目级" };
        var map = new Dictionary<string, WorkareaRecord?>(StringComparer.OrdinalIgnoreCase)
        {
            ["项目级"] = null
        };
        foreach (WorkareaRecord workarea in workareas
                     .OrderBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            string label = $"{workarea.DisplayName}  [{workarea.WorkareaId}]";
            labels.Add(label);
            map[label] = workarea;
        }

        string current = "项目级";
        if (string.Equals(row.WorkareaAssignmentStatus, "ASSIGNED", StringComparison.OrdinalIgnoreCase))
        {
            WorkareaRecord? existing = workareas.FirstOrDefault(x =>
                x.WorkareaId.Equals(row.WorkareaRef, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
                current = $"{existing.DisplayName}  [{existing.WorkareaId}]";
        }

        var values = PromptDialog.Show(
            this,
            $"设置工区归属 - {row.Name}",
            [
                new PromptField(
                    "workarea",
                    "归属",
                    current,
                    labels,
                    Required: true)
            ],
            note: "这里只修改资料归属元数据，不移动文件，也不会改变当前工作工区。");
        if (values is null)
            return;

        string selected = values["workarea"];
        ArtifactRecord updated;
        if (!map.TryGetValue(selected, out WorkareaRecord? target) || target is null)
        {
            updated = _artifacts.Update(
                Root,
                row.Path,
                workareaRef: "",
                workareaAssignmentStatus: "PROJECT");
        }
        else
        {
            updated = _artifacts.Update(
                Root,
                row.Path,
                workareaRef: target.WorkareaId,
                workareaAssignmentStatus: "ASSIGNED");
        }

        ApplyArtifactChanges([updated]);
    }

    private void OpenArtifactFolder()
    {
        RevealCurrentArtifact();
    }
}
