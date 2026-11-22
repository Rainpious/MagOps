using MagOps.Core;
using MagOps.Core.Domain;
using MagOps.Infrastructure.Services;

namespace MagOps.Desktop;

public sealed partial class MainForm
{
    private void BuildProjectTab()
    {
        var page = new TabPage("项目看板")
        {
            Padding = Padding.Empty,
            BackColor = UiTheme.Workspace
        };

        _projectDashboardSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterWidth = 7,
            // Do not set large Panel*MinSize values before Dock layout has assigned
            // a real client size. WinForms can otherwise throw during construction.
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = UiTheme.Border
        };

        var managementRoot = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 560,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = new Padding(10, 10, 10, 5),
            BackColor = UiTheme.Workspace
        };
        managementRoot.RowStyles.Add(new RowStyle(SizeType.Absolute, 360));
        managementRoot.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var overview = UiComponents.Card(new Padding(14, 12, 14, 12));
        overview.Margin = new Padding(0, 0, 0, 8);
        var overviewLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = UiTheme.Surface
        };
        overviewLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 74));
        overviewLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 98));
        overviewLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        overviewLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = UiTheme.Surface
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 330));

        var identity = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = UiTheme.Surface
        };
        identity.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        identity.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _projectNameValue.Dock = DockStyle.Fill;
        _projectNameValue.AutoSize = false;
        _projectNameValue.AutoEllipsis = true;
        _projectNameValue.TextAlign = ContentAlignment.MiddleLeft;
        _projectNameValue.Font = new Font(
            SystemFonts.MessageBoxFont?.FontFamily ?? FontFamily.GenericSansSerif,
            16F,
            FontStyle.Bold);
        _projectNameValue.ForeColor = UiTheme.TextPrimary;
        _projectNameValue.Margin = Padding.Empty;
        identity.Controls.Add(_projectNameValue, 0, 0);

        _projectRootValue.Dock = DockStyle.Fill;
        _projectRootValue.AutoSize = false;
        _projectRootValue.AutoEllipsis = true;
        _projectRootValue.TextAlign = ContentAlignment.MiddleLeft;
        _projectRootValue.ForeColor = UiTheme.TextSecondary;
        _projectRootValue.Margin = Padding.Empty;
        identity.Controls.Add(_projectRootValue, 0, 1);
        header.Controls.Add(identity, 0, 0);

        var context = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 4,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = UiTheme.Surface
        };
        context.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88));
        context.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 4; i++)
            context.RowStyles.Add(new RowStyle(SizeType.Percent, 25f));

        static Label MetaCaption(string text) => new()
        {
            Text = text,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleRight,
            ForeColor = UiTheme.TextSecondary,
            Margin = new Padding(0, 0, 8, 0)
        };
        static void PrepareMetaValue(Label label)
        {
            label.Dock = DockStyle.Fill;
            label.AutoSize = false;
            label.AutoEllipsis = true;
            label.TextAlign = ContentAlignment.MiddleLeft;
            label.Margin = Padding.Empty;
        }

        PrepareMetaValue(_projectStatusValue);
        PrepareMetaValue(_projectCurrentWorkareaValue);
        PrepareMetaValue(_projectCentralMeridianValue);
        _projectNameValue.Text = "未载入项目";
        _projectStatusValue.Text = "未载入";
        UiComponents.ApplyStatusBadge(_projectStatusValue, "PENDING");
        _projectCurrentWorkareaValue.Text = "未选择";
        _projectCentralMeridianValue.Text = "未记录";
        context.Controls.Add(MetaCaption("状态"), 0, 0);
        context.Controls.Add(_projectStatusValue, 1, 0);
        context.Controls.Add(MetaCaption("当前工区"), 0, 1);
        context.Controls.Add(_projectCurrentWorkareaValue, 1, 1);
        context.Controls.Add(MetaCaption("中央子午线"), 0, 2);
        context.Controls.Add(_projectCentralMeridianValue, 1, 2);
        context.Controls.Add(MetaCaption("项目时区"), 0, 3);
        _projectTimeZoneButton.Click += (_, _) => RunUi("项目时区", EditProjectTimeZone);
        context.Controls.Add(_projectTimeZoneButton, 1, 3);
        header.Controls.Add(context, 1, 0);
        overviewLayout.Controls.Add(header, 0, 0);

        var stats = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 1,
            Margin = new Padding(0, 6, 0, 6),
            Padding = Padding.Empty,
            BackColor = UiTheme.Surface
        };
        for (int i = 0; i < 4; i++)
            stats.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        stats.Controls.Add(UiComponents.StatTile("工区", _projectWorkareaCountValue), 0, 0);
        stats.Controls.Add(UiComponents.StatTile("未完成任务", _projectOpenTaskCountValue), 1, 0);
        stats.Controls.Add(UiComponents.StatTile("资料", _projectArtifactCountValue), 2, 0);
        stats.Controls.Add(UiComponents.StatTile("未解决问题", _projectIssueCountValue), 3, 0);
        _projectWorkareaCountValue.Text = "0";
        _projectOpenTaskCountValue.Text = "0";
        _projectArtifactCountValue.Text = "0";
        _projectIssueCountValue.Text = "0";
        for (int i = 0; i < stats.Controls.Count; i++)
        {
            Control tile = stats.Controls[i];
            tile.Margin = i == 0
                ? new Padding(0, 0, 5, 0)
                : i == stats.Controls.Count - 1
                    ? new Padding(5, 0, 0, 0)
                    : new Padding(5, 0, 5, 0);
        }
        overviewLayout.Controls.Add(stats, 0, 1);

        var gateRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = UiTheme.Surface
        };
        gateRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
        gateRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        gateRow.Controls.Add(new Label
        {
            Text = "作业门禁",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font(Font, FontStyle.Bold),
            ForeColor = UiTheme.TextPrimary,
            Margin = Padding.Empty
        }, 0, 0);

        _projectGatePanel.Dock = DockStyle.Fill;
        _projectGatePanel.WrapContents = true;
        _projectGatePanel.AutoScroll = false;
        _projectGatePanel.FlowDirection = FlowDirection.LeftToRight;
        _projectGatePanel.Margin = Padding.Empty;
        _projectGatePanel.Padding = new Padding(0, 2, 0, 0);
        _projectGatePanel.BackColor = UiTheme.Surface;
        foreach (string gate in AppConstants.GateNames)
        {
            var chip = new Label
            {
                AutoSize = true,
                BorderStyle = BorderStyle.None,
                Padding = new Padding(7, 3, 7, 3),
                Margin = new Padding(0, 1, 5, 1),
                Text = $"{UiText.Display(gate)} · 待确认",
                Cursor = Cursors.Hand,
                Tag = gate
            };
            chip.Click += (_, _) => RunUi(
                "查看门禁详情",
                () => ShowGateDetail((string)chip.Tag!));
            _projectGateLabels[gate] = chip;
            _projectGatePanel.Controls.Add(chip);
        }
        gateRow.Controls.Add(_projectGatePanel, 1, 0);
        overviewLayout.Controls.Add(gateRow, 0, 2);

        var next = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = UiTheme.AccentSoft,
            Padding = new Padding(10, 4, 10, 4),
            Margin = new Padding(0, 5, 0, 0)
        };
        var nextLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = UiTheme.AccentSoft
        };
        nextLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 66));
        nextLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        nextLayout.Controls.Add(new Label
        {
            Text = "下一步",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font(Font, FontStyle.Bold),
            ForeColor = UiTheme.Accent,
            Margin = Padding.Empty
        }, 0, 0);
        _projectNextActionValue.Text = "选择或新建项目后显示当前工作重点。";
        _projectNextActionValue.Dock = DockStyle.Fill;
        _projectNextActionValue.AutoSize = false;
        _projectNextActionValue.AutoEllipsis = true;
        _projectNextActionValue.TextAlign = ContentAlignment.MiddleLeft;
        _projectNextActionValue.ForeColor = UiTheme.TextPrimary;
        _projectNextActionValue.Margin = Padding.Empty;
        nextLayout.Controls.Add(_projectNextActionValue, 1, 0);
        next.Controls.Add(nextLayout);
        overviewLayout.Controls.Add(next, 0, 3);

        // Retain the legacy summary label as a backing/diagnostic value for code
        // compatibility, but the dashboard now presents the same data as cards.
        _projectStatsValue.Visible = false;
        overview.Controls.Add(overviewLayout);
        managementRoot.Controls.Add(overview, 0, 0);

        _projectDataSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterWidth = 6,
            // SplitterDistance is normalized after the control has real bounds.
            Margin = Padding.Empty,
            BackColor = UiTheme.Border
        };

        var workareaGroup = new GroupBox
        {
            Text = "工区",
            Dock = DockStyle.Fill,
            Padding = new Padding(8),
            Margin = new Padding(0, 0, 3, 0)
        };
        var workareaLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        workareaLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        workareaLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var projectButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = true,
            AutoScroll = false,
            Margin = Padding.Empty
        };
        projectButtons.Controls.Add(Button(
            "工区状态",
            (_, _) => RunUi("修改工区状态", ChangeWorkareaStatus),
            UiButtonRole.Primary));
        projectButtons.Controls.Add(Button(
            "同步工区",
            (_, _) => RunUi("同步工区", () =>
            {
                _workspace.SyncWorkareas(Root);
                _artifactPreparedRoot = "";
                RefreshProject();
                RefreshArtifacts();
                RefreshWorkareaContextSelectors();
            }),
            UiButtonRole.Subtle));

        var managementMenu = new ContextMenuStrip();
        managementMenu.Items.Add("项目状态", null, (_, _) =>
            RunUi("修改项目状态", ChangeProjectStatus));
        managementMenu.Items.Add("门禁状态", null, (_, _) =>
            RunUi("修改门禁", ChangeGate));
        managementMenu.Items.Add(new ToolStripSeparator());
        managementMenu.Items.Add("数据校验", null, (_, _) =>
            RunUi("管理数据校验", ValidateManagementData));
        managementMenu.Items.Add("创建快照", null, (_, _) =>
            RunUi("创建管理快照", CreateManagementSnapshot));
        var configurationMenu = new ToolStripMenuItem("程序配置");
        configurationMenu.DropDownItems.Add("更新 IGRF 系数…", null, (_, _) =>
            RunUi("更新 IGRF 系数", UpdateIgrfConfiguration));
        configurationMenu.DropDownItems.Add("恢复内置 IGRF", null, (_, _) =>
            RunUi("恢复内置 IGRF", RestoreEmbeddedIgrfConfiguration));
        configurationMenu.DropDownItems.Add("目录布局规则…", null, (_, _) =>
            RunUi("打开目录布局规则", OpenFolderLayoutConfiguration));
        managementMenu.Items.Add(configurationMenu);
        var managementButton = Button("项目管理 ▾", (_, _) => { }, UiButtonRole.Subtle);
        managementButton.ContextMenuStrip = managementMenu;
        managementButton.Click += (_, _) =>
            managementMenu.Show(managementButton, new Point(0, managementButton.Height));
        projectButtons.Controls.Add(managementButton);
        workareaLayout.Controls.Add(projectButtons, 0, 0);

        GridFactory.SetEmptyText(
            _workareaGrid,
            "尚无已登记工区。可直接点击顶部“新建工区”；登记时不会预创建业务目录。");
        _workareaGrid.DoubleClick += (_, _) => RunUi("修改工区状态", ChangeWorkareaStatus);
        workareaLayout.Controls.Add(_workareaGrid, 0, 1);
        workareaGroup.Controls.Add(workareaLayout);
        _projectDataSplit.Panel1.Controls.Add(workareaGroup);

        var taskGroup = new GroupBox
        {
            Text = "任务",
            Dock = DockStyle.Fill,
            Padding = new Padding(8),
            Margin = new Padding(3, 0, 0, 0)
        };
        var taskLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        taskLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        taskLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var taskButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = true,
            AutoScroll = false,
            Margin = Padding.Empty
        };
        taskButtons.Controls.Add(Button(
            "新增任务",
            (_, _) => RunUi("新增任务", AddTask),
            UiButtonRole.Primary));
        taskButtons.Controls.Add(Button("编辑任务", (_, _) => RunUi("编辑任务", EditTask)));
        taskButtons.Controls.Add(Button(
            "查看任务锁",
            (_, _) => RunUi("查看任务锁", ShowTaskLock),
            UiButtonRole.Subtle));
        taskLayout.Controls.Add(taskButtons, 0, 0);

        GridFactory.SetEmptyText(
            _taskGrid,
            "暂无任务。可点击“新增任务”建立项目工作项。");
        _taskGrid.DoubleClick += (_, _) => RunUi("编辑任务", EditTask);
        taskLayout.Controls.Add(_taskGrid, 0, 1);
        taskGroup.Controls.Add(taskLayout);
        _projectDataSplit.Panel2.Controls.Add(taskGroup);

        managementRoot.Controls.Add(_projectDataSplit, 0, 1);
        _projectDashboardSplit.Panel1.AutoScroll = true;
        _projectDashboardSplit.Panel1.SizeChanged += (_, _) =>
        {
            // Keep the overview and both management grids reachable when the dashboard
            // splitter or a short window leaves less height than their usable content.
            int minimum = (int)Math.Ceiling(managementRoot.RowStyles[0].Height) + LogicalToDeviceUnits(240);
            managementRoot.Height = Math.Max(minimum, _projectDashboardSplit.Panel1.ClientSize.Height);
        };
        _projectDashboardSplit.Panel1.Controls.Add(managementRoot);

        var artifactsGroup = new GroupBox
        {
            Text = "资料与归档",
            Dock = DockStyle.Fill,
            Padding = new Padding(12, 10, 12, 10),
            Margin = new Padding(10, 5, 10, 10)
        };
        var artifactsSummary = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        artifactsSummary.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        artifactsSummary.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        artifactsSummary.Controls.Add(new Label
        {
            Text = "完整资料管理已迁移为独立 Workspace。这里保留项目资料总数；原始资料导入、工区筛选、元数据、SHA 与文件定位请进入“资料与归档”。",
            Dock = DockStyle.Fill,
            AutoSize = false,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = UiTheme.TextSecondary,
            Margin = new Padding(0, 0, 12, 0)
        }, 0, 0);
        artifactsSummary.Controls.Add(
            UiComponents.Button(
                "打开资料与归档",
                (_, _) => NavigateFeature(FeatureIds.ArtifactWorkspace),
                UiButtonRole.Primary,
                150,
                36),
            1,
            0);
        artifactsGroup.Controls.Add(artifactsSummary);
        _projectDashboardSplit.Panel2.Controls.Add(artifactsGroup);

        var overviewPage = new TabPage("概览") { BackColor = UiTheme.Workspace };
        overviewPage.Controls.Add(_projectDashboardSplit);
        _projectWorkspaceTabs.TabPages.Add(overviewPage);
        _projectWorkspaceTabs.TabPages.Add(BuildDirectoryManagementPage());
        _projectWorkspaceTabs.Dock = DockStyle.Fill;
        _projectWorkspaceTabs.SelectedIndexChanged += (_, _) =>
        {
            if (_projectWorkspaceTabs.SelectedIndex == 1) RunUi("读取目录管理", RefreshDirectoryManagement);
        };
        page.Controls.Add(_projectWorkspaceTabs);
        RegisterFeaturePage(FeatureIds.ProjectDashboard, page);
    }

    private void NormalizeProjectDataSplit(int workareaCount, int taskCount)
    {
        if (_projectDataSplit is null || _projectDataSplit.IsDisposed)
            return;

        int available = _projectDataSplit.ClientSize.Width - _projectDataSplit.SplitterWidth;
        if (available < 700)
            return;

        double ratio;
        if (workareaCount == 0 && taskCount <= 3)
            ratio = 0.42;
        else if (workareaCount > taskCount * 2)
            ratio = 0.52;
        else if (taskCount > workareaCount * 2)
            ratio = 0.40;
        else
            ratio = 0.46;

        int min1 = Math.Min(_projectDataSplit.Panel1MinSize, available / 3);
        int min2 = Math.Min(_projectDataSplit.Panel2MinSize, available / 3);
        int desired = Math.Clamp(
            (int)Math.Round(available * ratio),
            Math.Max(260, min1),
            Math.Max(261, available - Math.Max(280, min2)));
        if (desired > 0 && desired < available)
            _projectDataSplit.SplitterDistance = desired;
    }

    private void ResetProjectDisplayForContextChange()
    {
        _projectNameValue.Text = "当前项目尚未读取";
        _projectRootValue.Text = Root;
        _toolTip.SetToolTip(_projectRootValue, Root);
        _projectStatusValue.Text = "待初始化 / 刷新";
        UiComponents.ApplyStatusBadge(_projectStatusValue, "PENDING");
        _projectCentralMeridianValue.Text = "未读取";
        _toolTip.SetToolTip(_projectCentralMeridianValue, "请选择已初始化项目后刷新空间基准。");
        _projectCurrentWorkareaValue.Text = "未选择";
        _toolTip.SetToolTip(_projectCurrentWorkareaValue, "尚未选择当前工区。");
        foreach (Label label in new[] { _projectWorkareaCountValue, _projectOpenTaskCountValue, _projectArtifactCountValue, _projectIssueCountValue })
        {
            label.Text = "—";
            label.ForeColor = UiTheme.TextSecondary;
        }
        _projectStatsValue.Text = "尚无当前项目统计。";
        _projectNextActionValue.Text = "初始化当前目录，或选择已有项目后继续。";
        foreach ((string gate, Label label) in _projectGateLabels)
        {
            label.Text = $"{UiText.Display(gate)} · 未读取";
            UiComponents.ApplyStatusBadge(label, "PENDING");
            _toolTip.SetToolTip(label, "尚未读取当前项目门禁。");
        }
        foreach (DataGridView grid in new[] { _workareaGrid, _taskGrid, _equipmentGrid, _flightGrid, _baseGrid, _dailyGrid, _issueGrid })
            grid.DataSource = null;
        _projectTimeZone = ProjectTime.Beijing;
        _projectTimeZoneButton.Text = "项目时区未读取 · 设置…";
    }

    private void RefreshProject()
    {
        IndependentArtifactTransaction.Recover(Root, _store);
        _artifacts.RefreshFreshnessStatus(Root);
        EnsureRememberedWorkareaContextLoaded();
        var ws =
            _workspace.ReadWorkareaSpatialContext(Root, CurrentWorkareaRelativePath);
        var workareas = _workspace.GetWorkareas(Root);
        var tasks = _workspace.GetTasks(Root);
        var artifacts = _artifacts.List(Root);
        var equipment = _fieldwork.ListEquipment(Root);
        var flights = _fieldwork.ListFlights(Root);
        var issues = _fieldwork.ListIssues(Root);

        _projectNameValue.Text = ws.DisplayName;
        _projectTimeZone = WorkspaceService.ResolveProjectTimeZone(ws);
        _projectTimeZoneButton.Text = ProjectTime.Label(_projectTimeZone) + " · 设置…";
        _projectStatusValue.Text = UiText.Display(ws.Status);
        UiComponents.ApplyStatusBadge(_projectStatusValue, ws.Status);
        _projectRootValue.Text = Root;
        _toolTip.SetToolTip(_projectRootValue, Root);

        _projectCentralMeridianValue.Text = ws.CentralMeridian.HasValue
            ? $"{ws.CentralMeridian.Value:0.########}°E"
            : "未记录";
        RefreshGlobalSpatialReference(ws);
        _toolTip.SetToolTip(
            _projectCentralMeridianValue,
            ws.CentralMeridian.HasValue
                ? $"工区中央子午线：{ws.CentralMeridian.Value:0.########}°E" +
                  (string.IsNullOrWhiteSpace(ws.CentralMeridianSource)
                      ? ""
                      : $"\r\n来源：{ws.CentralMeridianSource}") +
                  (string.IsNullOrWhiteSpace(ws.CentralMeridianConfirmedAt)
                      ? ""
                      : $"\r\n确认时间：{ws.CentralMeridianConfirmedAt}")
                : WorkspaceService.IsLegacyCoordinateDerivedCentralMeridianSource(
                        ws.CentralMeridianSource) ||
                    ws.CentralMeridianSource.StartsWith(
                        "LEGACY_COORDINATE_DERIVED_INVALIDATED",
                        StringComparison.OrdinalIgnoreCase)
                    ? "历史 Coordinate/Oasis PLY 派生中央子午线已失效。请通过坐标首次转换、航线反算确认或顶部设置确认工区空间基准。"
                    : "尚未确认工区中央子午线。首次坐标转换可确认工区值，也可通过航线反算或顶部设置确认；坐标登记和导出不修改工区空间基准。");

        WorkareaRecord? currentWorkarea = workareas.FirstOrDefault(x =>
            x.Path.Equals(CurrentWorkareaRelativePath, StringComparison.OrdinalIgnoreCase));
        _projectCurrentWorkareaValue.Text = currentWorkarea?.DisplayName
            ?? (string.IsNullOrWhiteSpace(CurrentWorkareaRelativePath)
                ? "未选择"
                : Path.GetFileName(CurrentWorkareaRelativePath.TrimEnd('/', '\\')));
        _toolTip.SetToolTip(
            _projectCurrentWorkareaValue,
            string.IsNullOrWhiteSpace(CurrentWorkareaRelativePath)
                ? "尚未选择当前工区。"
                : CurrentWorkareaRelativePath);

        int openTasks = tasks.Count(x => !x.Status.Equals("DONE", StringComparison.OrdinalIgnoreCase));
        int openIssues = issues.Count(x => !x.Status.Equals("RESOLVED", StringComparison.OrdinalIgnoreCase));
        _projectWorkareaCountValue.Text = workareas.Count.ToString();
        _projectOpenTaskCountValue.Text = openTasks.ToString();
        _projectArtifactCountValue.Text = artifacts.Count.ToString();
        _projectIssueCountValue.Text = openIssues.ToString();
        _projectIssueCountValue.ForeColor = openIssues > 0
            ? UiTheme.Warning
            : UiTheme.Success;
        _projectOpenTaskCountValue.ForeColor = openTasks > 0
            ? UiTheme.TextPrimary
            : UiTheme.Success;

        _projectStatsValue.Text =
            $"工区 {workareas.Count}　|　任务 {openTasks}/{tasks.Count} 未完成　|　" +
            $"资料 {artifacts.Count}　|　设备 {equipment.Count}　|　" +
            $"飞行架次 {flights.Count}　|　未解决问题 {openIssues}";

        string? attentionGate = AppConstants.GateNames.FirstOrDefault(gate =>
        {
            string gateStatus = ws.Gates.TryGetValue(gate, out string? value)
                ? value
                : "PENDING";
            return gateStatus.Equals("BLOCKED", StringComparison.OrdinalIgnoreCase) ||
                   gateStatus.Equals("REVIEW_REQUIRED", StringComparison.OrdinalIgnoreCase);
        });
        TaskRecord? nextTask = tasks.FirstOrDefault(x =>
            !x.Status.Equals("DONE", StringComparison.OrdinalIgnoreCase));
        _projectNextActionValue.Text = openIssues > 0
            ? $"优先处理 {openIssues} 个未解决问题。"
            : attentionGate is not null
                ? $"处理“{UiText.Display(attentionGate)}”门禁，再继续后续作业。"
                : nextTask is not null
                    ? $"继续任务：{nextTask.Title}"
                    : workareas.Count == 0
                        ? "创建工区并建立当前作业上下文。"
                        : string.IsNullOrWhiteSpace(CurrentWorkareaRelativePath)
                            ? "选择当前工区，再进入坐标、航线或外业管理流程。"
                            : "当前没有明显阻塞项，可继续当前工区作业。";

        foreach (string gate in AppConstants.GateNames)
        {
            string status = ws.Gates.TryGetValue(gate, out string? value)
                ? value
                : "PENDING";
            if (_projectGateLabels.TryGetValue(gate, out Label? label))
            {
                label.Text = $"{UiText.Display(gate)} · {UiText.Display(status)}";
                UiComponents.ApplyStatusBadge(label, status);
                string tooltip = $"{UiText.Display(gate)}门禁：{UiText.Display(status)}";
                if (ws.GateRecords.TryGetValue(gate, out GateRecord? record))
                {
                    if (!string.IsNullOrWhiteSpace(record.Note))
                        tooltip += $"\r\n说明：{record.Note}";
                    if (!string.IsNullOrWhiteSpace(record.ConfirmedBy))
                        tooltip += $"\r\n确认：{record.ConfirmedBy} {record.ConfirmedAt}";
                    if (record.EvidenceArtifacts.Count > 0)
                        tooltip += $"\r\n证据：{string.Join("；", record.EvidenceArtifacts.Take(3))}";
                }
                _toolTip.SetToolTip(label, tooltip);
            }
        }

        _workareaGrid.DataSource = null;
        _workareaGrid.DataSource = workareas;
        GridHeaders.Apply(_workareaGrid, new Dictionary<string, string>
        {
            [nameof(WorkareaRecord.DisplayName)] = "工区",
            [nameof(WorkareaRecord.Status)] = "状态",
            [nameof(WorkareaRecord.Path)] = "路径",
            [nameof(WorkareaRecord.Notes)] = "备注",
            [nameof(WorkareaRecord.UpdatedAt)] = "更新时间"
        },
        [nameof(WorkareaRecord.EntityType), nameof(WorkareaRecord.WorkareaId), nameof(WorkareaRecord.SubareaId),
         nameof(WorkareaRecord.Gates), nameof(WorkareaRecord.Extra)]);

        _taskGrid.DataSource = null;
        _taskGrid.DataSource = tasks;
        GridHeaders.Apply(_taskGrid, new Dictionary<string, string>
        {
            [nameof(TaskRecord.TaskId)] = "任务编号",
            [nameof(TaskRecord.Title)] = "任务",
            [nameof(TaskRecord.Workarea)] = "工区",
            [nameof(TaskRecord.Owner)] = "负责人",
            [nameof(TaskRecord.Due)] = "截止",
            [nameof(TaskRecord.Priority)] = "优先级",
            [nameof(TaskRecord.Status)] = "状态",
            [nameof(TaskRecord.Notes)] = "备注",
            [nameof(TaskRecord.UpdatedAt)] = "更新时间"
        },
        [nameof(TaskRecord.Subarea), nameof(TaskRecord.CreatedAt), nameof(TaskRecord.Extra)]);

        NormalizeProjectDataSplit(workareas.Count, tasks.Count);
        GridFactory.SetEmptyText(
            _workareaGrid,
            "尚无已登记工区。\r\n首次向工区写入资料时可选择或创建目标工区。");
        GridFactory.SetEmptyText(
            _taskGrid,
            "暂无任务。\r\n可点击“新增任务”建立项目工作项。");
        SetStatus($"项目 {ws.DisplayName} / {UiText.Display(ws.Status)}");
    }

    private void ShowGateDetail(string gate)
    {
        WorkspaceRecord ws = _workspace.GetWorkspace(Root);
        string status = ws.Gates.TryGetValue(gate, out string? value)
            ? value
            : "PENDING";

        var lines = new List<string>
        {
            $"门禁：{UiText.Display(gate)}",
            $"状态：{UiText.Display(status)}"
        };

        if (ws.GateRecords.TryGetValue(gate, out GateRecord? record))
        {
            if (!string.IsNullOrWhiteSpace(record.Note))
                lines.Add($"说明：{record.Note}");
            if (!string.IsNullOrWhiteSpace(record.ConfirmedBy))
                lines.Add($"确认人：{record.ConfirmedBy}");
            if (!string.IsNullOrWhiteSpace(record.ConfirmedAt))
                lines.Add($"确认时间：{record.ConfirmedAt}");
            if (record.EvidenceArtifacts.Count > 0)
            {
                lines.Add("");
                lines.Add("证据：");
                lines.AddRange(record.EvidenceArtifacts.Select(x => "• " + x));
            }
        }

        MessageBox.Show(
            this,
            string.Join(Environment.NewLine, lines),
            $"{UiText.Display(gate)}门禁详情",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private void ValidateManagementData()
    {
        EnsureInitialized();
        var result = _migration.Validate(Root);
        MessageBox.Show(this,
            result.Status == "PASS"
                ? $"管理数据校验通过。数据版本：{result.SchemaVersion}"
                : $"管理数据校验失败：\r\n{string.Join("\r\n", result.Errors)}",
            "管理数据校验",
            MessageBoxButtons.OK,
            result.Status == "PASS" ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }

    private void CreateManagementSnapshot()
    {
        EnsureInitialized();
        var values = PromptDialog.Show(this, "创建管理数据快照",
        [
            new("reason", "快照原因", "manual")
        ]);
        if (values is null) return;
        string? path = _snapshots.Create(Root, values["reason"]);
        MessageBox.Show(this,
            path is null ? "当前没有可快照的管理文件。" : $"管理数据快照已创建：\r\n{path}",
            "管理快照", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void ChangeProjectStatus()
    {
        EnsureInitialized();
        var ws = _workspace.GetWorkspace(Root);
        var values = PromptDialog.Show(this, "修改项目状态",
        [
            new("status", "目标状态", ws.Status, AppConstants.ProjectStatuses),
            new("note", "说明/依据", "", Multiline: true)
        ]);
        if (values is null) return;
        _workspace.SetProjectStatus(Root, values["status"], values["note"]);
        RefreshProject();
    }

    private void ChangeGate()
    {
        EnsureInitialized();
        var values = PromptDialog.Show(this, "修改门禁",
        [
            new("gate", "门禁", AppConstants.GateNames[0], AppConstants.GateNames),
            new("status", "状态", "PENDING", AppConstants.GateStatuses),
            new("confirmed_by", "确认人"),
            new("evidence", "证据路径", "", Multiline: true),
            new("note", "说明/依据", "", Multiline: true)
        ], height: 560);
        if (values is null) return;

        var evidence = SplitValues(values["evidence"]);
        _workspace.SetGate(Root, values["gate"], values["status"], values["note"], evidence, values["confirmed_by"]);
        RefreshProject();
    }

    private void ChangeWorkareaStatus()
    {
        EnsureInitialized();
        if (_workareaGrid.CurrentRow?.DataBoundItem is not WorkareaRecord row)
            throw new InvalidOperationException("请先在工区表中选择一行。");

        var values = PromptDialog.Show(this, $"修改工区状态 - {row.DisplayName}",
        [
            new("status", "目标状态", row.Status, AppConstants.ProjectStatuses),
            new("note", "说明/依据", row.Notes, Multiline: true)
        ]);
        if (values is null) return;

        _workspace.SetWorkareaStatus(Root, row.WorkareaId, values["status"], values["note"]);
        RefreshProject();
    }

    private void AddTask()
    {
        EnsureInitialized();
        var workareas = _workspace.GetWorkareas(Root).Select(x => x.DisplayName).Prepend("").ToArray();
        var values = PromptDialog.Show(this, "新增任务",
        [
            new("title", "任务标题", Required: true),
            new("workarea", "工区", "", workareas),
            new("owner", "负责人"),
            new("due", "截止日期"),
            new("priority", "优先级", "P1", ["P0", "P1", "P2", "P3"]),
            new("notes", "备注", "", Multiline: true)
        ], height: 520);
        if (values is null) return;

        _workspace.AddTask(Root, values["title"], values["workarea"], values["owner"], values["due"], values["priority"], values["notes"]);
        RefreshProject();
    }

    private void EditTask()
    {
        EnsureInitialized();
        if (_taskGrid.CurrentRow?.DataBoundItem is not TaskRecord task)
            throw new InvalidOperationException("请先选择一条任务。");

        var workareas = _workspace.GetWorkareas(Root).Select(x => x.DisplayName).Prepend("").ToArray();
        var values = PromptDialog.Show(this, $"编辑任务 - {task.TaskId}",
        [
            new("title", "任务标题", task.Title, Required: true),
            new("workarea", "工区", task.Workarea, workareas),
            new("owner", "负责人", task.Owner),
            new("due", "截止日期", task.Due),
            new("priority", "优先级", task.Priority, ["P0", "P1", "P2", "P3"]),
            new("status", "状态", task.Status, AppConstants.TaskStatuses),
            new("notes", "备注", task.Notes, Multiline: true)
        ], height: 580);
        if (values is null) return;

        task.Title = values["title"];
        task.Workarea = values["workarea"];
        task.Owner = values["owner"];
        task.Due = values["due"];
        task.Priority = values["priority"];
        task.Status = values["status"];
        task.Notes = values["notes"];
        _workspace.UpdateTask(Root, task);
        RefreshProject();
    }

    private void ShowTaskLock()
    {
        EnsureInitialized();
        var info = _workspace.ReadTaskLock(Root);
        MessageBox.Show(this,
            $"状态：{UiText.Display(info.Status)}\r\n任务编号：{info.TaskId}\r\n对话线：{info.DialogueLine}\r\n对话标识：{info.DialogueId}\r\n允许写入范围：{info.AllowedWriteScope}\r\n交接状态：{UiText.Display(info.HandoffStatus)}\r\n\r\n{info.Path}",
            "当前任务锁", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private static List<string> SplitValues(string value) =>
        (value ?? "").Split([',', '，', ';', '；', '\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}

internal static class GridHeaders
{
    public static void Apply(
        DataGridView grid,
        IReadOnlyDictionary<string, string> names,
        IEnumerable<string>? hidden = null)
    {
        foreach (DataGridViewColumn column in grid.Columns)
        {
            if (names.TryGetValue(column.DataPropertyName, out string? title))
                column.HeaderText = title;
        }

        if (hidden is not null)
        {
            foreach (string property in hidden)
            {
                if (grid.Columns.Contains(property))
                    grid.Columns[property]!.Visible = false;
            }
        }

        grid.CellFormatting -= LocalizeCell;
        grid.CellFormatting += LocalizeCell;
    }

    private static void LocalizeCell(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (sender is not DataGridView grid || e.ColumnIndex < 0 || e.ColumnIndex >= grid.Columns.Count)
            return;

        string property = grid.Columns[e.ColumnIndex].DataPropertyName;

        if (property.EndsWith("At", StringComparison.OrdinalIgnoreCase) ||
            property.Equals("ModifiedAt", StringComparison.OrdinalIgnoreCase))
        {
            if (e.Value is string timeText &&
                DateTimeOffset.TryParse(timeText, out DateTimeOffset parsed))
            {
                e.Value = parsed.LocalDateTime.ToString("yyyy-MM-dd HH:mm");
                e.FormattingApplied = true;
                return;
            }
        }

        if (!UiText.ShouldTranslateGridProperty(property))
            return;

        if (grid.Columns[e.ColumnIndex] is DataGridViewCheckBoxColumn)
            return;

        if (e.Value is bool boolean)
        {
            e.Value = boolean ? "是" : "否";
            e.FormattingApplied = true;
            return;
        }

        if (e.Value is string text && UiText.TryDisplay(text, out string display))
        {
            e.Value = display;
            e.FormattingApplied = true;
        }
    }
}

