using MagOps.Core.Routes;
using MagOps.Core.SurveyPlanning;

namespace MagOps.Desktop;

internal enum RouteSourceChoice
{
    None = 0,
    Ply = 1,
    Kml = 2,
    Csv = 3
}

internal sealed record RouteWorkspaceActions(
    Action<string?> ChoosePly,
    Action RefreshKmlDirectory,
    Action<string> SelectKmlFromDirectory,
    Action<string?> ChooseKml,
    Action LoadProjectKml,
    Action<string> BindBoundary,
    Action<string?> ChooseCsv,
    Action<string?> RunConcaveRepair,
    Action RefreshSubdivisionPreview,
    Action SplitKml,
    Action ApplyCornerStart,
    Action ApplyAdvancedSettings,
    Action<PartitionRouteOverviewEdit> PartitionOverviewSettingsChanged,
    Action<string> OpenPartitionFromOverview,
    Action GenerateFromSelectedSource,
    Action SplitOverlongExecutionSegment,
    Action<string, PointF, PointF> CutLineDrawn,
    Action MoveLastTwoLines,
    Action MoveFirstTwoLines,
    Action EditCurrentSortie,
    Action ReverseCurrentSortie,
    Action Regenerate,
    Action ChooseOutput,
    Action<string?> ChooseTerrain,
    Action DownloadTerrain,
    Action ManageTerrainLibrary,
    Action MatchTerrainTiles,
    Action PackageTerrainTiles,
    Action ExportAllAndRegister,
    Action<bool, bool, bool> ExportSubset,
    Action<bool, bool, bool> ExportCurrentSubset,
    Action SaveProject,
    Action<string?> LoadProject,
    Action SortieSelectionChanged,
    Func<RouteSortie, string> ResolveSortieState,
    Action<RouteEndpoint> EndpointClicked,
    Action<double, double> MapPointClicked,
    Action ManageAccessPlanning,
    Action LockSortieSite,
    Action ExportAccessAssessment,
    Action<IReadOnlyList<SurveySubdivisionCutLine>, int> SubdivisionCutsChanged,
    Func<string, string> FormatPathForDisplay);

internal sealed partial class RouteWorkspaceControl : UserControl
{
    internal event Action? AlignBoundaryRequested;
    internal event Action? EstimateRequested;
    internal event Action? CheckLinesRequested;
    internal void SetLineAzimuth(double value) => _routeLineAzimuth.Text = value.ToString("0.########", System.Globalization.CultureInfo.InvariantCulture);
    private readonly RouteWorkspaceActions _actions;
    private readonly ToolTip _pathToolTip = new() { AutoPopDelay = 20000 };

    private readonly TextBox _routeCsv = new();
    private readonly TextBox _routePly = new();
    private readonly TextBox _routeKml = new();
    private readonly RadioButton _routeSourcePly = new();
    private readonly RadioButton _routeSourceKml = new();
    private readonly RadioButton _routeSourceCsv = new();
    private readonly NumericUpDown _routeKmlSplitCount = new();
    private readonly CheckBox _routeKmlOverlapEnabled = new();
    private readonly NumericUpDown _routeKmlOverlapLength = new();
    private readonly ComboBox _routeKmlSplitMode = new();
    private readonly ComboBox _routeKmlDirectorySelector = new();
    private readonly TextBox _routeLineSpacing = new();
    private readonly TextBox _routeLineAzimuth = new();
    private readonly TextBox _routeInitialLine = new();
    private readonly TextBox _routeLineIncrement = new();
    private readonly ComboBox _routeCoordMode = new();
    private readonly ComboBox _routeHeaderlessOrder = new();
    private readonly TextBox _routeCentralMeridian = new();
    private readonly TextBox _routeMaxSortieKm = new();
    private readonly ComboBox _routePartitionStartSide = new();
    private readonly ComboBox _routeStartDirection = new();
    private readonly ComboBox _routeStartPoint = new();
    private readonly TextBox _routeAgl = new();
    private readonly TextBox _routeSpeed = new();
    private readonly TextBox _routeTerrainDem = new();
    private readonly CheckBox _routeKmlUseTerrain = new();
    private readonly TextBox _routeOutput = new();
    private readonly TextBox _routeInfo = new();
    private readonly Label _accessSummary = new() { AutoSize = true, MaximumSize = new Size(650, 0), ForeColor = Color.DarkRed };
    internal void SetAccessSummary(string text) => _accessSummary.Text = text;
    private readonly Label _routeAdvancedSummary = new();
    private readonly DataGridView _routeSorties = GridFactory.Create();
    private readonly DataGridView _routePoints = GridFactory.Create();
    private readonly RoutePreviewPanel _routePreview = new();
    private readonly KmlSubdivisionPreviewPanel _routeSubdivisionPreview = new();
    private readonly PartitionRouteOverviewPanel _routePartitionOverview = new();
    private readonly TabControl _routePreviewTabs = new();
    private TabPage? _subdivisionPreviewTab;

    private readonly List<string> _routeKmlDirectoryPaths = [];
    private bool _routeKmlDirectorySelectorSyncing;
    private bool _suppressSortieSelectionChanged;
    private int _takeoffAnchorModeIndex;
    private int _flightDirectionIndex;
    private string _takeoffAnchorRef = "";
    private TableLayoutPanel? _routeSetupLayout;
    private SplitContainer? _routeMainSplit;
    private Panel? _routeContinuousHost;
    private Control? _routeEditSection;

    internal RouteWorkspaceControl(
        RouteWorkspaceActions actions)
    {
        _actions = actions;

        Dock = DockStyle.Fill;
        Margin = Padding.Empty;
        Padding = Padding.Empty;
        BackColor = UiTheme.Workspace;
        AutoScroll = true;

        Build();
        Disposed += (_, _) => _pathToolTip.Dispose();
        foreach (var pathBox in new[] { _routeCsv, _routePly, _routeKml, _routeOutput, _routeTerrainDem })
            pathBox.TextChanged += (_, _) => _pathToolTip.SetToolTip(pathBox, GetPathValue(pathBox));
        foreach (var pathBox in new[] { _routeCsv, _routePly, _routeKml })
            pathBox.MaximumSize = new Size(320, 0);
        foreach (var (control, label) in new (Control, string)[]
        {
            (_routeCsv, "坐标 CSV 来源"), (_routePly, "PLY 边界来源"), (_routeKml, "KML 航线来源"),
            (_routeKmlSplitCount, "工区切分数量"), (_routeKmlOverlapLength, "分区测线重叠总长度（米）"),
            (_routeKmlSplitMode, "KML 工区切分模式"), (_routeKmlDirectorySelector, "KML 来源目录"),
            (_routeLineSpacing, "测线间距（米）"), (_routeLineAzimuth, "测线方位角（度）"),
            (_routeInitialLine, "起始测线号"), (_routeLineIncrement, "测线号递增值"),
            (_routeCoordMode, "航线输入坐标模式"), (_routeHeaderlessOrder, "无表头坐标列顺序"),
            (_routeCentralMeridian, "航线中央经线（度）"), (_routeMaxSortieKm, "单架次最长航程（千米）"),
            (_routePartitionStartSide, "分区起始侧"), (_routeStartDirection, "起始飞行方向"),
            (_routeStartPoint, "起始点"), (_routeAgl, "设计离地高度（米）"), (_routeSpeed, "设计飞行速度"),
            (_routeTerrainDem, "地形 DEM 文件路径"), (_routeOutput, "航线输出目录"),
            (_routeInfo, "航线生成说明与结果"), (_routeSorties, "航线架次列表"), (_routePoints, "航线点列表")
        }) control.AccessibleName = label;
        Resize += (_, _) =>
            ApplySetupWidthProfile();
    }

    private void Build()
    {
        // route-tab: survey-drawing-and-v25-connection
        // v0.75 UI-only restructure: preserve all route business behavior while
        // presenting the existing workflow as Input -> Parameters -> Edit/Preview -> Output.
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = UiTheme.PagePadding,
            BackColor = UiTheme.Workspace
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        static Label TopLabel(string text) => new()
        {
            Text = text,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            ForeColor = UiTheme.TextSecondary,
            Margin = new Padding(4, 7, 6, 2)
        };

        static Button FixedButton(
            string text,
            int width,
            EventHandler click,
            UiButtonRole role = UiButtonRole.Neutral) =>
            UiComponents.Button(text, click, role, width, 34);

        root.Controls.Add(UiComponents.PageHeader(
            "绘制航线",
            "PLY、KML 或已有端点统一进入航线流程；KML 分区后进入多子区总览，再按需进入单区精细编辑。"), 0, 0);

        var continuous = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(2) };
        _routeContinuousHost = continuous;
        var workflow = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, RowCount = 3 };
        workflow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        workflow.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        workflow.RowStyles.Add(new RowStyle(SizeType.Absolute, 470));
        workflow.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        continuous.Controls.Add(workflow);
        continuous.SizeChanged += (_, _) => NormalizeLayout();
        root.Controls.Add(continuous, 0, 1);

        // ----- Step 1 + Step 2: input and route parameters -----
        var setup = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0, 0, 0, 6),
            Padding = Padding.Empty,
            BackColor = UiTheme.Workspace
        };
        setup.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        setup.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        setup.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _routeSetupLayout = setup;

        var inputGroup = new GroupBox
        {
            Text = "1  输入",
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(8, 16, 8, 6),
            Margin = new Padding(0, 0, 4, 0)
        };

        var inputLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 4,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        inputLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        inputLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        inputLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        inputLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // Three route sources share one downstream "Generate / Update" action.
        // PLY and KML create survey-line endpoints first; existing CSV enters
        // the unchanged v25 pairing route directly.
        inputLayout.RowCount = 4;
        inputLayout.RowStyles.Clear();
        for (int i = 0; i < inputLayout.RowCount; i++)
            inputLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _routeSourcePly.Text = "PLY 边界";
        _routeSourcePly.AutoSize = true;
        _routeSourcePly.Margin = new Padding(4, 8, 8, 2);
        var plySourceRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = true,
            Margin = Padding.Empty
        };
        plySourceRow.Controls.Add(_routeSourcePly);
        _routePly.Width = 320;
        _routePly.Margin = new Padding(0, 3, 6, 3);
        _routePly.PlaceholderText = "进入本页时优先读取项目 PLY；平面米制 X<TAB>Y";
        plySourceRow.Controls.Add(_routePly);
        var chooseRoutePly = FixedButton(
            "选择",
            78,
            (_, _) => _actions.ChoosePly(null),
            UiButtonRole.Subtle);
        FileDropSupport.EnableSingle(
            _routePly,
            path => _actions.ChoosePly(path),
            path => FileDropSupport.HasExtension(path, ".ply"));
        FileDropSupport.EnableSingle(
            chooseRoutePly,
            path => _actions.ChoosePly(path),
            path => FileDropSupport.HasExtension(path, ".ply"));
        plySourceRow.Controls.Add(chooseRoutePly);
        plySourceRow.Controls.Add(FixedButton("设为工区边界", 116, (_, _) => _actions.BindBoundary(GetPlyPath()), UiButtonRole.Subtle));
        inputLayout.Controls.Add(plySourceRow, 0, 0);

        _routeSourceKml.Text = "KML 边界";
        _routeSourceKml.AutoSize = true;
        _routeSourceKml.Margin = new Padding(4, 8, 8, 2);
        var kmlSourceRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = true,
            Margin = new Padding(0, 2, 0, 0)
        };
        kmlSourceRow.Controls.Add(_routeSourceKml);
        _routeKml.Width = 320;
        _routeKml.Margin = new Padding(0, 3, 6, 3);
        _routeKml.PlaceholderText = "进入本页时读取项目范围 KML；分区后自动进入全部子区总览";
        kmlSourceRow.Controls.Add(_routeKml);

        _routeKmlDirectorySelector.DropDownStyle = ComboBoxStyle.DropDownList;
        _routeKmlDirectorySelector.Width = 112;
        _routeKmlDirectorySelector.Margin = new Padding(0, 3, 6, 3);
        _routeKmlDirectorySelector.DropDown += (_, _) =>
            _actions.RefreshKmlDirectory();
        _routeKmlDirectorySelector.SelectedIndexChanged += (_, _) =>
            NotifyKmlDirectorySelectionChanged();
        kmlSourceRow.Controls.Add(_routeKmlDirectorySelector);

        var chooseRouteKml = FixedButton(
            "选择",
            78,
            (_, _) => _actions.ChooseKml(null),
            UiButtonRole.Subtle);
        FileDropSupport.EnableSingle(
            _routeKml,
            path => _actions.ChooseKml(path),
            path => FileDropSupport.HasExtension(path, ".kml"));
        FileDropSupport.EnableSingle(
            chooseRouteKml,
            path => _actions.ChooseKml(path),
            path => FileDropSupport.HasExtension(path, ".kml"));
        kmlSourceRow.Controls.Add(chooseRouteKml);
        kmlSourceRow.Controls.Add(FixedButton(
            "工区边界",
            96,
            (_, _) => _actions.LoadProjectKml(),
            UiButtonRole.Subtle));
        kmlSourceRow.Controls.Add(FixedButton("设为工区边界", 116, (_, _) => _actions.BindBoundary(GetKmlPath()), UiButtonRole.Subtle));
        inputLayout.Controls.Add(kmlSourceRow, 0, 1);

        _routeSourceCsv.Text = "端点 CSV";
        _routeSourceCsv.AutoSize = true;
        _routeSourceCsv.Margin = new Padding(4, 8, 8, 2);
        var sourceRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = true,
            Margin = new Padding(0, 2, 0, 0)
        };
        sourceRow.Controls.Add(_routeSourceCsv);
        _routeCsv.Width = 320;
        _routeCsv.Margin = new Padding(0, 3, 6, 3);
        _routeCsv.PlaceholderText = "已有航线端点 CSV / TXT / TSV；高级/兼容入口";
        sourceRow.Controls.Add(_routeCsv);
        var chooseRouteCsv = FixedButton(
            "选择",
            78,
            (_, _) => _actions.ChooseCsv(null),
            UiButtonRole.Subtle);
        FileDropSupport.EnableSingle(
            _routeCsv,
            path => _actions.ChooseCsv(path),
            path => FileDropSupport.HasExtension(path, ".csv", ".txt", ".tsv"));
        FileDropSupport.EnableSingle(
            chooseRouteCsv,
            path => _actions.ChooseCsv(path),
            path => FileDropSupport.HasExtension(path, ".csv", ".txt", ".tsv"));
        sourceRow.Controls.Add(chooseRouteCsv);
        var concaveRepair = FixedButton(
            "连接 / 分割…",
            126,
            (_, _) => _actions.RunConcaveRepair(null),
            UiButtonRole.Subtle);
        FileDropSupport.EnableSingle(
            concaveRepair,
            path => _actions.RunConcaveRepair(path),
            path => FileDropSupport.HasExtension(path, ".csv", ".txt", ".tsv"));
        sourceRow.Controls.Add(concaveRepair);
        inputLayout.Controls.Add(sourceRow, 0, 2);

        var splitKmlRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = true,
            Margin = new Padding(4, 5, 0, 0),
            Padding = Padding.Empty
        };
        splitKmlRow.Controls.Add(TopLabel("KML 分区"));
        _routeKmlSplitMode.DropDownStyle = ComboBoxStyle.DropDownList;
        _routeKmlSplitMode.Items.Clear();
        _routeKmlSplitMode.Items.AddRange([
            "沿测线方向等分",
            "按 X/Easting（经度向）等分",
            "按 Y/Northing（纬度向）等分",
            "手绘切分线",
            "品字三分（上1下2）"
        ]);
        _routeKmlSplitMode.SelectedIndex = 0;
        _routeKmlSplitMode.Width = 180;
        _routeKmlSplitMode.SelectedIndexChanged += (_, _) =>
        {
            _routeKmlSplitCount.Enabled =
                _routeKmlSplitMode.SelectedIndex is not 3 and not 4;
            _actions.RefreshSubdivisionPreview();
        };
        splitKmlRow.Controls.Add(_routeKmlSplitMode);
        splitKmlRow.Controls.Add(TopLabel("目标子区"));
        _routeKmlSplitCount.Minimum = 2;
        _routeKmlSplitCount.Maximum = 50;
        _routeKmlSplitCount.Value = 2;
        _routeKmlSplitCount.Width = 68;
        _routeKmlSplitCount.ValueChanged += (_, _) =>
        {
            if (_routeKmlSplitMode.SelectedIndex is not 3 and not 4)
                _actions.RefreshSubdivisionPreview();
        };
        splitKmlRow.Controls.Add(_routeKmlSplitCount);
        _routeKmlOverlapEnabled.Text = "分区测线重叠";
        _routeKmlOverlapEnabled.AutoSize = true;
        _routeKmlOverlapLength.Minimum = 1;
        _routeKmlOverlapLength.Maximum = 10000;
        _routeKmlOverlapLength.DecimalPlaces = 1;
        _routeKmlOverlapLength.Value = 10;
        _routeKmlOverlapLength.Width = 88;
        _routeKmlOverlapLength.Enabled = false;
        _routeKmlOverlapEnabled.CheckedChanged += (_, _) =>
        {
            _routeKmlOverlapLength.Enabled = _routeKmlOverlapEnabled.Checked;
            _routeSubdivisionPreview.SetOverlapLength(GetKmlOverlapLengthM());
        };
        _routeKmlOverlapLength.ValueChanged += (_, _) =>
            _routeSubdivisionPreview.SetOverlapLength(GetKmlOverlapLengthM());
        splitKmlRow.Controls.Add(_routeKmlOverlapEnabled);
        splitKmlRow.Controls.Add(_routeKmlOverlapLength);
        splitKmlRow.Controls.Add(TopLabel("米（两侧合计，各延伸一半）"));
        splitKmlRow.Controls.Add(FixedButton(
            "预览切分",
            108,
            (_, _) => _actions.RefreshSubdivisionPreview(),
            UiButtonRole.Subtle));
        splitKmlRow.Controls.Add(FixedButton(
            "执行分区",
            108,
            (_, _) => _actions.SplitKml(),
            UiButtonRole.Subtle));
        splitKmlRow.Controls.Add(UiComponents.HelpLabel(
            "自动切分线可在预览中拖动微调；手绘模式左键拖线、右键删除。"));
        splitKmlRow.SizeChanged += (_, _) =>
        {
            int height = splitKmlRow.GetPreferredSize(new Size(Math.Max(1, splitKmlRow.ClientSize.Width), 0)).Height;
            if (splitKmlRow.MinimumSize.Height != height)
                splitKmlRow.MinimumSize = new Size(0, height);
        };

        var kmlSubdivisionGroup = new GroupBox
        {
            Text = "KML 分区（可选）",
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(6, 14, 6, 4),
            Margin = new Padding(0, 3, 0, 0)
        };
        var kmlSubdivisionLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        kmlSubdivisionLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        kmlSubdivisionLayout.Controls.Add(splitKmlRow, 0, 0);
        kmlSubdivisionGroup.Controls.Add(kmlSubdivisionLayout);
        inputLayout.Controls.Add(kmlSubdivisionGroup, 0, 3);

        _routeSourcePly.CheckedChanged += (_, _) =>
        {
            if (!_routeSourcePly.Checked) return;
            _routeSourceKml.Checked = false;
            _routeSourceCsv.Checked = false;
        };
        _routeSourceKml.CheckedChanged += (_, _) =>
        {
            if (!_routeSourceKml.Checked) return;
            _routeSourcePly.Checked = false;
            _routeSourceCsv.Checked = false;
        };
        _routeSourceCsv.CheckedChanged += (_, _) =>
        {
            if (!_routeSourceCsv.Checked) return;
            _routeSourcePly.Checked = false;
            _routeSourceKml.Checked = false;
        };

        inputGroup.Controls.Add(inputLayout);
        setup.Controls.Add(inputGroup, 0, 0);

        var parameterGroup = new GroupBox
        {
            Text = "2  航线参数",
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(8, 16, 8, 6),
            Margin = new Padding(4, 0, 0, 0)
        };

        var parameterLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 3,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        for (int i = 0; i < 3; i++)
            parameterLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // Common production parameters stay visible. Use four equal cells
        // rather than one long horizontal FlowLayout row so the card scales
        // cleanly from low-resolution screens to large monitors.
        var surveyParamGrid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 4,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        for (int i = 0; i < 4; i++)
            surveyParamGrid.ColumnStyles.Add(
                new ColumnStyle(SizeType.Percent, 25));
        surveyParamGrid.RowStyles.Add(
            new RowStyle(SizeType.AutoSize));

        _routeLineSpacing.Text = "100";
        _routeLineAzimuth.Text = "0";
        _routeMaxSortieKm.Text = "8";
        _routeAgl.Text = "100";

        surveyParamGrid.Controls.Add(
            BuildCompactParameterCell(
                "线距 m",
                _routeLineSpacing),
            0,
            0);
        surveyParamGrid.Controls.Add(
            BuildCompactParameterCell(
                "坐标北方位角 °",
                _routeLineAzimuth),
            1,
            0);
        surveyParamGrid.Controls.Add(
            BuildCompactParameterCell(
                "最大单架次 km",
                _routeMaxSortieKm),
            2,
            0);
        surveyParamGrid.Controls.Add(
            BuildCompactParameterCell(
                "AGL m",
                _routeAgl),
            3,
            0);
        parameterLayout.Controls.Add(
            surveyParamGrid,
            0,
            0);

        var concaveWorkflowRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0, 5, 0, 0),
            Padding = new Padding(4, 4, 4, 4),
            BackColor = UiTheme.SurfaceMuted
        };
        concaveWorkflowRow.ColumnStyles.Add(
            new ColumnStyle(SizeType.AutoSize));
        concaveWorkflowRow.ColumnStyles.Add(
            new ColumnStyle(SizeType.Percent, 100));

        concaveWorkflowRow.Controls.Add(
            new Label
            {
                Text = "凹形断线",
                AutoSize = true,
                Font = new Font(
                    Font,
                    FontStyle.Bold),
                ForeColor = UiTheme.TextPrimary,
                Margin = new Padding(2, 3, 10, 0)
            },
            0,
            0);

        var concaveWorkflowHelp =
            UiComponents.HelpLabel(
                "生成时自动检测；发现同一测线被边界切成多段时，进入“连接 / 分割”窗口逐线确认。KML 默认采用安全分割，可改为连接。");
        concaveWorkflowHelp.AutoEllipsis = true;
        concaveWorkflowHelp.AutoSize = false;
        concaveWorkflowHelp.Dock = DockStyle.Fill;
        concaveWorkflowHelp.Margin =
            new Padding(0, 3, 0, 0);
        concaveWorkflowRow.Controls.Add(
            concaveWorkflowHelp,
            1,
            0);

        parameterLayout.Controls.Add(
            concaveWorkflowRow,
            0,
            1);

        // Advanced controls retain the existing values and business semantics;
        // they are simply removed from the primary production surface.
        _routeInitialLine.Text = "1";
        _routeInitialLine.Width = 72;
        _routeLineIncrement.Text = "1";
        _routeLineIncrement.Width = 72;

        _routeCoordMode.DropDownStyle = ComboBoxStyle.DropDownList;
        _routeCoordMode.Items.AddRange(["自动判断", "平面坐标（高斯-克吕格）", "经纬度（WGS84）"]);
        _routeCoordMode.SelectedIndex = 0;

        _routeHeaderlessOrder.DropDownStyle = ComboBoxStyle.DropDownList;
        _routeHeaderlessOrder.Items.AddRange(["Point,Y,X", "Point,X,Y", "Y,X", "X,Y"]);
        _routeHeaderlessOrder.SelectedIndex = 0;

        _routeCentralMeridian.Text = "";

        _routePartitionStartSide.DropDownStyle = ComboBoxStyle.DropDownList;
        _routePartitionStartSide.Items.AddRange([
            "自动（共同切分边界）",
            "北侧",
            "南侧",
            "西侧",
            "东侧"
        ]);
        _routePartitionStartSide.SelectedIndex = 0;

        _routeStartDirection.DropDownStyle = ComboBoxStyle.DropDownList;
        _routeStartDirection.Items.AddRange(["西南", "西北", "东南", "东北"]);
        _routeStartDirection.SelectedIndex = 0;
        _routeStartDirection.SelectedIndexChanged += (_, _) =>
        {
            UpdateAdvancedSummary();
            _actions.ApplyCornerStart();
        };

        _routeStartPoint.DropDownStyle = ComboBoxStyle.DropDown;
        _routeStartPoint.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
        _routeStartPoint.AutoCompleteSource = AutoCompleteSource.ListItems;
        _routeStartPoint.TextChanged += (_, _) =>
            UpdateAdvancedSummary();

        // Keep the action row compact: a short explanation expands in the
        // middle, while both buttons remain fully visible on the right.
        var actionRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 4,
            Margin = new Padding(0, 5, 0, 0),
            Padding = Padding.Empty
        };
        actionRow.ColumnStyles.Add(
            new ColumnStyle(SizeType.Percent, 100));
        actionRow.ColumnStyles.Add(
            new ColumnStyle(SizeType.AutoSize));
        actionRow.ColumnStyles.Add(
            new ColumnStyle(SizeType.AutoSize));

        actionRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        actionRow.Controls.Add(FixedButton("切割线设计", 108, (_, _) => CheckLinesRequested?.Invoke()), 3, 0);

        _routeAdvancedSummary.AutoSize = false;
        _routeAdvancedSummary.AutoEllipsis = true;
        _routeAdvancedSummary.Dock = DockStyle.Fill;
        _routeAdvancedSummary.TextAlign =
            ContentAlignment.MiddleLeft;
        _routeAdvancedSummary.ForeColor =
            UiTheme.TextSecondary;
        _routeAdvancedSummary.Margin =
            new Padding(4, 7, 8, 0);
        actionRow.Controls.Add(
            _routeAdvancedSummary,
            0,
            0);

        actionRow.Controls.Add(
            FixedButton(
                "高级参数…",
                112,
                (_, _) => EditAdvancedSettings(),
                UiButtonRole.Subtle),
            1,
            0);
        actionRow.Controls.Add(
            FixedButton(
                "生成 / 更新航线",
                154,
                (_, _) =>
                    _actions.GenerateFromSelectedSource(),
                UiButtonRole.Primary),
            2,
            0);

        parameterLayout.Controls.Add(
            actionRow,
            0,
            2);

        UpdateAdvancedSummary();
        parameterGroup.Controls.Add(parameterLayout);
        setup.Controls.Add(parameterGroup, 1, 0);
        workflow.Controls.Add(setup, 0, 0);

        // ----- Step 3: edit and preview. Preview is the default view. -----
        var editSection = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = UiTheme.Workspace
        };
        editSection.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        editSection.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        editSection.Controls.Add(UiComponents.SectionLabel("3", "编辑与预览"), 0, 0);
        editSection.Controls.RemoveAt(0);
        var editHeading = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        editHeading.Controls.Add(UiComponents.SectionLabel("3", "编辑与预览"));
        editSection.Controls.Add(editHeading, 0, 0);

        _routeMainSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterWidth = 6,
            Margin = Padding.Empty,
            BackColor = UiTheme.Border
        };

        var leftLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Margin = Padding.Empty,
            Padding = new Padding(0, 0, 6, 0),
            BackColor = UiTheme.Surface
        };
        leftLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        leftLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        leftLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 104));
        leftLayout.Controls.Add(new Label
        {
            Text = "架次与长度",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font(Font, FontStyle.Bold),
            ForeColor = UiTheme.TextPrimary,
            Padding = new Padding(8, 0, 0, 0)
        }, 0, 0);
        leftLayout.Controls.Add(_routeSorties, 0, 1);

        var edit = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = false, WrapContents = true,
            AutoScroll = true, Padding = new Padding(3), BackColor = UiTheme.SurfaceMuted };
        Button CommandMenu(string title, params (string Text, Action Run)[] commands)
        {
            var menu = new ContextMenuStrip();
            foreach (var command in commands) menu.Items.Add(command.Text, null, (_, _) => command.Run());
            var button = UiComponents.Button(title, (_, _) => { }, UiButtonRole.Subtle);
            button.ContextMenuStrip = menu;
            button.Click += (_, _) => menu.Show(button, new Point(0, button.Height));
            button.Disposed += (_, _) => menu.Dispose();
            return button;
        }
        edit.Controls.Add(CommandMenu("架次编辑 ▾",
            ("末2条 → 下一架次", () => _actions.MoveLastTwoLines()),
            ("下一架次前2条 → 当前", () => _actions.MoveFirstTwoLines()),
            ("编辑当前架次航点", () => _actions.EditCurrentSortie()),
            ("更换当前架次测线方向", () => _actions.ReverseCurrentSortie()),
            ("切分超长测线为执行段…", () => _actions.SplitOverlongExecutionSegment())));
        edit.Controls.Add(UiComponents.Button("重新生成", (_, _) => _actions.Regenerate(), UiButtonRole.Subtle));
        leftLayout.Controls.Add(edit, 0, 2);
        _routeMainSplit.Panel1.Controls.Add(leftLayout);

        _routePreviewTabs.Dock = DockStyle.Fill;
        _routePreviewTabs.Margin = Padding.Empty;
        _routePreviewTabs.TabPages.Clear();
        var rightTabs = _routePreviewTabs;
        var previewTab = new TabPage("航线预览") { Padding = Padding.Empty };
        _routePreview.Dock = DockStyle.Fill;
        _routePreview.Margin = Padding.Empty;
        _routePreview.Cursor = Cursors.Cross;
        previewTab.Controls.Add(_routePreview);
        if (LaunchSitePlanning.Enabled) previewTab.Controls.Add(BuildSiteEditorBar());

        var partitionOverviewTab = new TabPage("分区总览") { Padding = Padding.Empty };
        _routePartitionOverview.Dock = DockStyle.Fill;
        _routePartitionOverview.Margin = Padding.Empty;
        _routePartitionOverview.SettingsChanged += edit =>
            _actions.PartitionOverviewSettingsChanged(edit);
        _routePartitionOverview.OpenPartitionRequested += code =>
            _actions.OpenPartitionFromOverview(code);
        partitionOverviewTab.Controls.Add(_routePartitionOverview);

        var subdivisionPreviewTab = new TabPage("KML 分区预览") { Padding = Padding.Empty };
        _subdivisionPreviewTab = subdivisionPreviewTab;
        _routeSubdivisionPreview.Dock = DockStyle.Fill;
        _routeSubdivisionPreview.Margin = Padding.Empty;
        _routeSubdivisionPreview.CutsChanged += cuts =>
            _actions.SubdivisionCutsChanged(
                cuts,
                _routeSubdivisionPreview.PreviewSubareaCount);
        subdivisionPreviewTab.Controls.Add(_routeSubdivisionPreview);

        var pointsTab = new TabPage("当前架次航点") { Padding = Padding.Empty };
        pointsTab.Controls.Add(_routePoints);
        rightTabs.TabPages.Add(previewTab);
        rightTabs.TabPages.Add(partitionOverviewTab);
        rightTabs.TabPages.Add(pointsTab);
        rightTabs.SelectedTab = previewTab;
        _routeMainSplit.Panel2.Controls.Add(rightTabs);
        editSection.Controls.Add(_routeMainSplit, 0, 1);
        workflow.Controls.Add(editSection, 0, 1);
        _routeEditSection = editSection;

        // ----- Step 4: output. KMZ remains intentionally frozen. -----
        var outputGroup = new GroupBox
        {
            Text = "4  输出与保存",
            Dock = DockStyle.Fill,
            AutoSize = true,
            Padding = new Padding(8, 16, 8, 6),
            Margin = new Padding(0, 6, 0, 0)
        };
        var outputLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 4,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        outputLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        outputLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        outputLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        outputLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 100));

        var outputRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = true,
            Margin = Padding.Empty
        };
        _routeOutput.Dock = DockStyle.None;
        _routeOutput.Width = 320;
        _routeOutput.Margin = new Padding(0, 3, 6, 3);
        _routeOutput.PlaceholderText = "有项目+工区时固定归档到 04_航线设计与任务文件/05_设计航线；无项目上下文时可临时指定；KML/CSV 分别进入 01_KML / 02_CSV";
        outputRow.Controls.Add(TopLabel("输出目录"));
        outputRow.Controls.Add(_routeOutput);
        outputRow.Controls.Add(FixedButton("选择", 78, (_, _) => _actions.ChooseOutput(), UiButtonRole.Subtle));
        outputLayout.Controls.Add(outputRow, 0, 0);

        var terrainRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = true,
            Margin = Padding.Empty
        };
        terrainRow.Controls.Add(TopLabel("工区地形 GeoTIFF"));
        _routeKmlUseTerrain.Text = "KML 使用地形高程";
        _routeKmlUseTerrain.Checked = true;
        _routeKmlUseTerrain.AutoSize = true;
        _routeKmlUseTerrain.Margin = new Padding(0, 8, 10, 3);
        terrainRow.Controls.Add(_routeKmlUseTerrain);
        _routeTerrainDem.Dock = DockStyle.None;
        _routeTerrainDem.Width = 260;
        _routeTerrainDem.Margin = new Padding(0, 3, 6, 3);
        _routeTerrainDem.PlaceholderText =
            "公共地形库瓦片集合，项目保存工区引用；可留空并取消“使用地形高程”导出无高程 KML";
        terrainRow.Controls.Add(_routeTerrainDem);
        var chooseRouteTerrain = FixedButton("导入 / 选择", 98,
            (_, _) => _actions.ChooseTerrain(null), UiButtonRole.Subtle);
        FileDropSupport.EnableSingle(
            _routeTerrainDem,
            path => _actions.ChooseTerrain(path),
            path => FileDropSupport.HasExtension(path, ".tif", ".tiff") || path.EndsWith(".terrain.json",StringComparison.OrdinalIgnoreCase));
        FileDropSupport.EnableSingle(
            chooseRouteTerrain,
            path => _actions.ChooseTerrain(path),
            path => FileDropSupport.HasExtension(path, ".tif", ".tiff") || path.EndsWith(".terrain.json",StringComparison.OrdinalIgnoreCase));
        terrainRow.Controls.Add(chooseRouteTerrain);
        terrainRow.Controls.Add(CommandMenu("地形库 ▾",
            ("地形下载设置", () => _actions.DownloadTerrain()),
            ("公共库设置", () => _actions.ManageTerrainLibrary()),
            ("按当前范围匹配瓦片", () => _actions.MatchTerrainTiles()),
            ("打包当前工区瓦片", () => _actions.PackageTerrainTiles())));
        terrainRow.Controls.Add(UiComponents.HelpLabel(
            "勾选：约 10 m 密化 + DEM + AGL；取消：导出 lon,lat 无高程 KML；KMZ 冻结"));
        outputLayout.Controls.Add(terrainRow, 0, 1);

        var actionFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoScroll = false,
            WrapContents = true,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(0, 2, 0, 2),
            Margin = Padding.Empty
        };

        actionFlow.Controls.Add(FixedButton("统一导出 KML + CSV（有项目时登记）", 300,
            (_, _) => _actions.ExportAllAndRegister(), UiButtonRole.Primary));
        actionFlow.Controls.Add(CommandMenu("更多导出 ▾",
            ("全部 KML", () => _actions.ExportSubset(true, false, false)),
            ("当前 KML", () => _actions.ExportCurrentSubset(true, false, false)),
            ("全部 CSV", () => _actions.ExportSubset(false, true, false)),
            ("当前 CSV", () => _actions.ExportCurrentSubset(false, true, false))));
        actionFlow.Controls.Add(FixedButton("保存编辑工程 JSON", 160, (_, _) => _actions.SaveProject()));
        var loadRouteProject = FixedButton("载入编辑工程 JSON", 160,
            (_, _) => _actions.LoadProject(null));
        FileDropSupport.EnableSingle(
            loadRouteProject,
            path => _actions.LoadProject(path),
            path => FileDropSupport.HasExtension(path, ".json"));
        actionFlow.Controls.Add(loadRouteProject);
        actionFlow.Controls.Add(FixedButton("沿边定向", 110, (_, _) => AlignBoundaryRequested?.Invoke()));
        actionFlow.Controls.Add(FixedButton("设计工作量", 120, (_, _) => EstimateRequested?.Invoke()));
        actionFlow.Controls.Add(FixedButton("检查线管理", 120, (_, _) => CheckLinesRequested?.Invoke()));
        actionFlow.Controls.Add(FixedButton("添加踏勘轨迹", 140, (_, _) => _actions.ManageAccessPlanning()));

        var kmzFrozen = new Button
        {
            Text = "KMZ 导出（冻结）",
            AutoSize = true,
            MinimumSize = new Size(142, 36),
            Margin = new Padding(4, 2, 4, 2),
            Enabled = false
        };
        UiComponents.SetButtonRole(kmzFrozen, UiButtonRole.Subtle);
        actionFlow.Controls.Add(kmzFrozen);
        outputLayout.Controls.Add(actionFlow, 0, 2);

        _routeInfo.Multiline = true;
        _routeInfo.ReadOnly = true;
        _routeInfo.ScrollBars = ScrollBars.Vertical;
        _routeInfo.WordWrap = true;
        _routeInfo.Dock = DockStyle.Fill;
        _routeInfo.Margin = new Padding(0, 2, 0, 0);
        outputLayout.Controls.Add(_routeInfo, 0, 3);
        outputGroup.Controls.Add(outputLayout);
        workflow.Controls.Add(outputGroup, 0, 2);

        _routeSorties.SelectionChanged += (_, _) =>
        {
            if (!_suppressSortieSelectionChanged)
                _actions.SortieSelectionChanged();
        };
        _routePreview.SortieStateResolver =
            _actions.ResolveSortieState;
        _routePreview.EndpointClicked += endpoint =>
        {
            _routeStartPoint.Text =
                endpoint.DisplayLabel;
            _actions.EndpointClicked(
                endpoint);
        };
        if (LaunchSitePlanning.Enabled) _routePreview.MapPointClicked += (x, y) => _actions.MapPointClicked(x, y);
        _routePreview.CutLineDrawn += (start, end) =>
            _actions.CutLineDrawn(_routePreview.Tag as string ?? "", start, end);

        Controls.Add(root);
    }

    internal void BeginCutLineDrawing(string motherLine)
    {
        _routePreview.Tag = motherLine;
        _routePreview.BeginCutLineDrawing();
    }

    internal (double X, double Y)? WorldPoint(PointF point) =>
        _routePreview.WorldPoint(Point.Round(point));

    internal (double MinX, double MaxX, double MinY, double MaxY)? WorldBounds() =>
        _routePreview.WorldBounds();

    private void NotifyKmlDirectorySelectionChanged()
    {
        if (_routeKmlDirectorySelectorSyncing)
            return;

        int index =
            _routeKmlDirectorySelector.SelectedIndex;
        if (index < 0 ||
            index >= _routeKmlDirectoryPaths.Count)
        {
            return;
        }

        _actions.SelectKmlFromDirectory(
            _routeKmlDirectoryPaths[index]);
    }

    internal void SetKmlDirectoryPaths(
        IReadOnlyList<string> paths,
        string currentPath)
    {
        _routeKmlDirectorySelectorSyncing =
            true;
        try
        {
            _routeKmlDirectoryPaths.Clear();
            _routeKmlDirectorySelector.Items.Clear();

            foreach (string path in paths)
            {
                string full =
                    Path.GetFullPath(path);
                _routeKmlDirectoryPaths.Add(
                    full);
                _routeKmlDirectorySelector.Items.Add(
                    Path.GetFileName(full));
            }

            int selected =
                _routeKmlDirectoryPaths.FindIndex(
                    path =>
                        path.Equals(
                            currentPath,
                            StringComparison.OrdinalIgnoreCase));
            _routeKmlDirectorySelector.SelectedIndex =
                selected;
        }
        finally
        {
            _routeKmlDirectorySelectorSyncing =
                false;
        }
    }

    internal string GetCsvPath() =>
        GetPathValue(_routeCsv);

    internal void SetCsvPath(
        string value) =>
        SetPathValue(
            _routeCsv,
            value);

    internal string GetPlyPath() =>
        GetPathValue(_routePly);

    internal void SetPlyPath(
        string value) =>
        SetPathValue(
            _routePly,
            value);

    internal string GetKmlPath() =>
        GetPathValue(_routeKml);

    internal void SetKmlPath(
        string value) =>
        SetPathValue(
            _routeKml,
            value);

    internal RouteSourceChoice GetSourceChoice()
    {
        if (_routeSourcePly.Checked)
            return RouteSourceChoice.Ply;
        if (_routeSourceKml.Checked)
            return RouteSourceChoice.Kml;
        if (_routeSourceCsv.Checked)
            return RouteSourceChoice.Csv;
        return RouteSourceChoice.None;
    }

    internal void SetSourceChoice(
        RouteSourceChoice choice)
    {
        _routeSourcePly.Checked =
            choice == RouteSourceChoice.Ply;
        _routeSourceKml.Checked =
            choice == RouteSourceChoice.Kml;
        _routeSourceCsv.Checked =
            choice == RouteSourceChoice.Csv;
    }

    internal int GetKmlSplitModeIndex() =>
        _routeKmlSplitMode.SelectedIndex;

    internal double GetKmlOverlapLengthM() =>
        _routeKmlOverlapEnabled.Checked ? Decimal.ToDouble(_routeKmlOverlapLength.Value) : 0;

    internal string GetKmlSubdivisionOutputDirectory(string baseDirectory) =>
        _routeKmlOverlapEnabled.Checked
            ? Path.TrimEndingDirectorySeparator(baseDirectory) + "_O"
            : baseDirectory;

    internal int GetKmlSplitCount() =>
        Decimal.ToInt32(
            _routeKmlSplitCount.Value);

    internal void SetKmlSplitCount(
        int value)
    {
        int min =
            Decimal.ToInt32(
                _routeKmlSplitCount.Minimum);
        int max =
            Decimal.ToInt32(
                _routeKmlSplitCount.Maximum);
        _routeKmlSplitCount.Value =
            Math.Clamp(
                value,
                min,
                max);
    }

    internal string GetLineSpacingText() =>
        _routeLineSpacing.Text;

    internal string GetLineAzimuthText() =>
        _routeLineAzimuth.Text;

    internal string GetInitialLineText() =>
        _routeInitialLine.Text;

    internal string GetLineIncrementText() =>
        _routeLineIncrement.Text;

    internal int GetCoordModeIndex() =>
        _routeCoordMode.SelectedIndex;

    internal string GetCoordModeText() =>
        _routeCoordMode.SelectedItem?.ToString()
        ?? "自动判断";

    internal void SetCoordModeIndex(
        int index)
    {
        _routeCoordMode.SelectedIndex =
            Math.Clamp(
                index,
                0,
                Math.Max(
                    0,
                    _routeCoordMode.Items.Count - 1));
        UpdateAdvancedSummary();
    }

    internal void SelectCoordModeText(
        string value)
    {
        SelectCombo(
            _routeCoordMode,
            value);
        UpdateAdvancedSummary();
    }

    internal string GetHeaderlessOrderText() =>
        _routeHeaderlessOrder.SelectedItem?.ToString()
        ?? "Point,Y,X";

    internal void SelectHeaderlessOrderText(
        string value)
    {
        SelectCombo(
            _routeHeaderlessOrder,
            value);
        UpdateAdvancedSummary();
    }

    internal string GetCentralMeridianText() =>
        _routeCentralMeridian.Text;

    internal void SetCentralMeridianText(
        string value)
    {
        _routeCentralMeridian.Text =
            value;
        UpdateAdvancedSummary();
    }

    internal string GetMaxSortieKmText() =>
        _routeMaxSortieKm.Text;

    internal void SetMaxSortieKmText(
        string value)
    {
        _routeMaxSortieKm.Text = value;
        AcceptFlightLimit();
    }

    internal int GetPartitionStartSideIndex() =>
        _routePartitionStartSide.SelectedIndex;

    internal string GetPartitionStartSideText() =>
        _routePartitionStartSide.SelectedItem?.ToString()
        ?? "自动（共同切分边界）";

    internal void SelectPartitionStartSideText(
        string value)
    {
        string normalized = value?.Trim() ?? "";
        normalized = normalized.ToLowerInvariant() switch
        {
            "" or "自动" or "automatic" => "自动（共同切分边界）",
            "north" => "北侧",
            "south" => "南侧",
            "west" => "西侧",
            "east" => "东侧",
            _ => normalized
        };
        SelectCombo(
            _routePartitionStartSide,
            normalized);
        UpdateAdvancedSummary();
    }

    internal int GetStartDirectionIndex() =>
        _routeStartDirection.SelectedIndex;

    internal string GetStartDirectionText() =>
        _routeStartDirection.SelectedItem?.ToString()
            ?? "西南";

    internal RouteTakeoffAnchorMode GetTakeoffAnchorMode() =>
        (RouteTakeoffAnchorMode)Math.Clamp(_takeoffAnchorModeIndex, 0, 3);
    internal RouteFlightDirection GetFlightDirection() =>
        (RouteFlightDirection)Math.Clamp(_flightDirectionIndex, 0, 4);
    internal string GetTakeoffAnchorRef() => _takeoffAnchorRef;
    internal void SetTakeoffAnchorSettings(RouteTakeoffAnchorMode mode, RouteFlightDirection direction, string reference)
    {
        _takeoffAnchorModeIndex = (int)mode;
        _flightDirectionIndex = (int)direction;
        _takeoffAnchorRef = reference ?? "";
    }

    internal void SelectStartDirectionText(
        string value)
    {
        SelectCombo(
            _routeStartDirection,
            value);
        UpdateAdvancedSummary();
    }

    internal string GetStartPointText() =>
        _routeStartPoint.Text.Trim();

    internal void SetStartPointText(
        string value)
    {
        _routeStartPoint.Text =
            value;
        UpdateAdvancedSummary();
    }

    internal void SetStartPointChoices(
        IReadOnlyList<string> values)
    {
        _routeStartPoint.Items.Clear();
        foreach (string value in values)
            _routeStartPoint.Items.Add(value);
    }

    internal string GetAglText() =>
        _routeAgl.Text;

    internal void SetAglText(
        string value)
    {
        _routeAgl.Text =
            value;
    }

    internal string GetSpeedText() =>
        _routeSpeed.Text;

    internal void SetSpeedText(
        string value)
    {
        _routeSpeed.Text =
            value;
    }

    internal string GetTerrainDemPath() =>
        GetPathValue(
            _routeTerrainDem);

    internal void SetTerrainDemPath(
        string value) =>
        SetPathValue(
            _routeTerrainDem,
            value);

    internal bool GetUseTerrain() =>
        _routeKmlUseTerrain.Checked;

    internal void SetUseTerrain(
        bool value)
    {
        _routeKmlUseTerrain.Checked =
            value;
    }

    internal string GetOutputPath() =>
        GetPathValue(
            _routeOutput);

    internal void SetOutputPath(
        string value) =>
        SetPathValue(
            _routeOutput,
            value);

    internal void SetInfoText(
        string value)
    {
        _routeInfo.Text =
            value;
    }

    internal int GetSelectedSortieIndex() =>
        _routeSorties.CurrentRow?.Index
        ?? 0;

    internal void ShowSortieRows(
        IReadOnlyList<RouteSortieViewRow> rows,
        int selected)
    {
        _suppressSortieSelectionChanged =
            true;
        try
        {
            _routeSorties.DataSource =
                null;
            _routeSorties.DataSource =
                rows.ToList();
            _routeSorties.CellFormatting -= SortieCellFormatting;
            _routeSorties.CellFormatting += SortieCellFormatting;

            GridHeaders.Apply(
                _routeSorties,
                new Dictionary<string, string>
                {
                    [nameof(RouteSortieViewRow.Index)] =
                        "#",
                    [nameof(RouteSortieViewRow.Name)] =
                        "架次",
                    [nameof(RouteSortieViewRow.LineCount)] =
                        "测线数",
                    [nameof(RouteSortieViewRow.PointCount)] =
                        "航点数",
                    [nameof(RouteSortieViewRow.LengthKm)] =
                        "作业 km",
                    [nameof(RouteSortieViewRow.SiteName)] = "起降点",
                    [nameof(RouteSortieViewRow.OutboundKm)] = "去程 km",
                    [nameof(RouteSortieViewRow.ReturnKm)] = "返程 km",
                    [nameof(RouteSortieViewRow.TotalKm)] = "完整航程 km",
                    [nameof(RouteSortieViewRow.Status)] =
                        "长度状态",
                    [nameof(RouteSortieViewRow.EditState)] =
                        "修改状态"
                });

            if (_routeSorties.Columns[
                    nameof(
                        RouteSortieViewRow.LengthKm)] is
                DataGridViewColumn lengthColumn)
            {
                lengthColumn.DefaultCellStyle.Format =
                    "F2";
            }
            foreach (var column in new[] { nameof(RouteSortieViewRow.OutboundKm), nameof(RouteSortieViewRow.ReturnKm), nameof(RouteSortieViewRow.TotalKm) })
                if (_routeSorties.Columns[column] is { } c) c.DefaultCellStyle.Format = "F3";

            if (!LaunchSitePlanning.Enabled)
                foreach (var name in new[] { nameof(RouteSortieViewRow.SiteName), nameof(RouteSortieViewRow.OutboundKm), nameof(RouteSortieViewRow.ReturnKm), nameof(RouteSortieViewRow.TotalKm) })
                    if (_routeSorties.Columns[name] is { } column) column.Visible = false;
            if (_routeSorties.Rows.Count > 0)
            {
                selected =
                    Math.Clamp(
                        selected,
                        0,
                        _routeSorties.Rows.Count - 1);
                _routeSorties.CurrentCell =
                    _routeSorties.Rows[selected]
                        .Cells[nameof(RouteSortieViewRow.Name)];
            }
        }
        finally
        {
            _suppressSortieSelectionChanged =
                false;
        }
    }

    private static void SortieCellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (sender is not DataGridView grid || e.RowIndex < 0 || e.RowIndex >= grid.Rows.Count) return;
        if (grid.Rows[e.RowIndex].Cells[nameof(RouteSortieViewRow.Status)].Value?.ToString() == "OVER_LIMIT")
        {
            e.CellStyle.ForeColor = Color.FromArgb(190, 35, 35);
            e.CellStyle.SelectionForeColor = Color.FromArgb(190, 35, 35);
            e.CellStyle.Font = new Font(grid.Font, FontStyle.Bold);
        }
    }

    internal void ShowPointRows(
        IReadOnlyList<RoutePointViewRow> rows,
        RoutePlanResult plan,
        int selectedSortieIndex,
        string selectedSortieState)
    {
        _routePoints.DataSource =
            null;
        _routePoints.DataSource =
            rows.ToList();

        GridHeaders.Apply(
            _routePoints,
            new Dictionary<string, string>
            {
                [nameof(RoutePointViewRow.Sequence)] =
                    "#",
                [nameof(RoutePointViewRow.PointId)] =
                    "Point ID",
                [nameof(RoutePointViewRow.Line)] =
                    "Line",
                [nameof(RoutePointViewRow.Side)] =
                    "Side",
                [nameof(RoutePointViewRow.X)] =
                    "X / Lon",
                [nameof(RoutePointViewRow.Y)] =
                    "Y / Lat",
                [nameof(RoutePointViewRow.SourceLabel)] =
                    "原始点名"
            });

        _routePreview.Plan =
            plan;
        _routePreview.SelectedSortieIndex =
            selectedSortieIndex;
        _routePreview.SelectedSortieState =
            selectedSortieState;
        _routePreview.Invalidate();
    }

    internal void ClearRoutePreview()
    {
        _routePreview.Plan =
            null;
        _routePreview.Invalidate();
    }

    internal void ShowRoutePreviewTab()
    {
        ShowRouteEditSection();
        if (_routePreviewTabs.TabPages.Count > 0)
            _routePreviewTabs.SelectedIndex =
                0;
    }

    internal void InvalidateRoutePreview()
    {
        _routePreview.Invalidate();
    }

    internal void ClearSubdivisionPreview()
    {
        _routeSubdivisionPreview.ClearPreview();
        RemoveSubdivisionPreviewTab();
    }

    internal void SetSurveyTrackReferences(IEnumerable<ReferenceRoad> roads, double centralMeridian, string datum)
        => _routeSubdivisionPreview.SetSurveyTracks(roads, centralMeridian, datum);

    internal SurveyLinePlanResult? GetSubdivisionMotherPlan() =>
        _routeSubdivisionPreview.MotherPlan;

    internal SurveySubdivisionAxis GetSubdivisionAxis() =>
        _routeSubdivisionPreview.Axis;

    internal IReadOnlyList<SurveySubdivisionCutLine> GetSubdivisionCuts() =>
        _routeSubdivisionPreview.GetCuts();

    internal int GetSubdivisionPreviewSubareaCount() =>
        _routeSubdivisionPreview.PreviewSubareaCount;

    internal void SetSubdivisionPreview(
        SurveyLinePlanResult motherPlan,
        IReadOnlyList<SurveySubdivisionCutLine> cuts,
        SurveySubdivisionAxis axis,
        bool manualDrawingEnabled)
    {
        _routeSubdivisionPreview.SetOverlapLength(GetKmlOverlapLengthM());
        _routeSubdivisionPreview.SetPreview(
            motherPlan,
            cuts,
            axis,
            manualDrawingEnabled);
    }

    internal void SetSubdivisionOperationPreview(
        SurveyLinePlanResult motherPlan,
        SurveySubdivisionOperationPlan operationPlan)
    {
        _routeSubdivisionPreview.SetOverlapLength(GetKmlOverlapLengthM());
        _routeSubdivisionPreview.SetOperationPreview(motherPlan, operationPlan);
    }

    internal bool IsPartitionOverviewActive => _routePreviewTabs.SelectedIndex == 1;
    internal string OverviewSelection => _routePartitionOverview.OverviewSelection;

    internal void ShowPartitionOverviewTab()
    {
        ShowRouteEditSection();
        if (_routePreviewTabs.TabPages.Count > 1)
            _routePreviewTabs.SelectedIndex =
                1;
    }

    internal void SetPartitionOverviewEntries(
        IReadOnlyList<PartitionRouteOverviewEntry> entries,
        string selectedPartitionCode = "") =>
        _routePartitionOverview.SetEntries(
            entries,
            selectedPartitionCode);

    internal void UpdatePartitionOverviewEntry(
        PartitionRouteOverviewEntry entry) =>
        _routePartitionOverview.UpdateEntry(entry);

    internal void ClearPartitionOverview() =>
        _routePartitionOverview.ClearEntries();

    internal void ShowSubdivisionPreviewTab()
    {
        ShowRouteEditSection();
        if (_subdivisionPreviewTab is null)
            return;
        if (!_routePreviewTabs.TabPages.Contains(_subdivisionPreviewTab))
            _routePreviewTabs.TabPages.Insert(
                Math.Min(2, _routePreviewTabs.TabPages.Count),
                _subdivisionPreviewTab);
        _routePreviewTabs.SelectedTab = _subdivisionPreviewTab;
    }

    internal void RemoveSubdivisionPreviewTab()
    {
        if (_subdivisionPreviewTab is not null &&
            _routePreviewTabs.TabPages.Contains(_subdivisionPreviewTab))
        {
            _routePreviewTabs.TabPages.Remove(_subdivisionPreviewTab);
        }
        ShowRoutePreviewTab();
    }

    internal void ResetForContextChange()
    {
        _dismissTerrain?.Invoke();
        _routeCentralMeridian.Clear();
        ClearPathValue(_routePly);
        ClearPathValue(_routeKml);
        ClearPathValue(_routeCsv);
        SetSourceChoice(
            RouteSourceChoice.None);

        _routeKmlDirectorySelectorSyncing =
            true;
        try
        {
            _routeKmlDirectoryPaths.Clear();
            _routeKmlDirectorySelector.Items.Clear();
            _routeKmlDirectorySelector.SelectedIndex =
                -1;
        }
        finally
        {
            _routeKmlDirectorySelectorSyncing =
                false;
        }

        ClearSubdivisionPreview();
        _routePartitionOverview.ClearEntries();
        _routeSorties.DataSource =
            null;
        _routePoints.DataSource =
            null;
        _routePreview.Plan =
            null;
        _routePreview.SelectedSortieIndex =
            -1;
        _routePreview.SelectedSortieState =
            "";
        _routePreview.Invalidate();
    }

    private static Control BuildCompactParameterCell(
        string label,
        TextBox input)
    {
        var cell = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(2, 0, 4, 0),
            Padding = Padding.Empty
        };
        cell.RowStyles.Add(
            new RowStyle(SizeType.AutoSize));
        cell.RowStyles.Add(
            new RowStyle(SizeType.AutoSize));

        cell.Controls.Add(
            new Label
            {
                Text = label,
                AutoSize = true,
                ForeColor = UiTheme.TextSecondary,
                Margin = new Padding(2, 0, 2, 2)
            },
            0,
            0);

        input.Dock = DockStyle.Fill;
        input.Margin = new Padding(0, 0, 0, 0);
        cell.Controls.Add(
            input,
            0,
            1);

        return cell;
    }

    private string GetPathValue(
        TextBox box)
    {
        string visible =
            box.Text.Trim();
        if (box.Tag is string full &&
            visible.Equals(
                _actions.FormatPathForDisplay(full),
                StringComparison.Ordinal))
        {
            return full;
        }

        return visible;
    }

    private void SetPathValue(
        TextBox box,
        string value)
    {
        string full =
            value?.Trim() ?? "";
        box.Tag =
            string.IsNullOrWhiteSpace(full)
                ? null
                : full;
        box.Text =
            string.IsNullOrWhiteSpace(full)
                ? ""
                : _actions.FormatPathForDisplay(full);

        if (box.TextLength > 0)
        {
            box.SelectionStart =
                box.TextLength;
            box.SelectionLength =
                0;
            box.ScrollToCaret();
        }
    }

    private static void ClearPathValue(
        TextBox box)
    {
        box.Tag =
            null;
        box.Clear();
    }

    private void EditAdvancedSettings()
    {
        var current =
            new RouteAdvancedSettingsState(
                InitialLine:
                    _routeInitialLine.Text,
                LineIncrement:
                    _routeLineIncrement.Text,
                CoordModeIndex:
                    _routeCoordMode.SelectedIndex,
                HeaderlessOrder:
                    _routeHeaderlessOrder.SelectedItem?.ToString()
                    ?? "Point,Y,X",
                CentralMeridian:
                    _routeCentralMeridian.Text,
                PartitionStartSideIndex:
                    _routePartitionStartSide.SelectedIndex,
                StartDirectionIndex:
                    _routeStartDirection.SelectedIndex,
                TakeoffAnchorModeIndex: _takeoffAnchorModeIndex,
                FlightDirectionIndex: _flightDirectionIndex,
                TakeoffAnchorRef: _takeoffAnchorRef,
                StartPoint:
                    _routeStartPoint.Text,
                StartPointChoices:
                    _routeStartPoint.Items
                        .Cast<object>()
                        .Select(item =>
                            item?.ToString() ?? "")
                        .Where(item =>
                            !string.IsNullOrWhiteSpace(item))
                        .ToArray());

        using var dialog =
            new RouteAdvancedSettingsDialog(
                current);
        if (dialog.ShowDialog(
                FindForm()) !=
            DialogResult.OK)
        {
            return;
        }

        RouteAdvancedSettingsState updated =
            dialog.GetSettings();

        _routeInitialLine.Text =
            updated.InitialLine;
        _routeLineIncrement.Text =
            updated.LineIncrement;
        SetCoordModeIndex(
            updated.CoordModeIndex);
        SelectHeaderlessOrderText(
            updated.HeaderlessOrder);
        SetCentralMeridianText(
            updated.CentralMeridian);

        int partitionStartSide =
            Math.Clamp(
                updated.PartitionStartSideIndex,
                0,
                Math.Max(
                    0,
                    _routePartitionStartSide.Items.Count - 1));
        _routePartitionStartSide.SelectedIndex =
            partitionStartSide;

        int startDirection =
            Math.Clamp(
                updated.StartDirectionIndex,
                0,
                Math.Max(
                    0,
                    _routeStartDirection.Items.Count - 1));
        _routeStartDirection.SelectedIndex =
            startDirection;
        _routeStartPoint.Text =
            updated.StartPoint;
        _takeoffAnchorModeIndex = Math.Clamp(updated.TakeoffAnchorModeIndex, 0, 3);
        _flightDirectionIndex = Math.Clamp(updated.FlightDirectionIndex, 0, 4);
        _takeoffAnchorRef = updated.TakeoffAnchorRef;

        UpdateAdvancedSummary();
        _actions.ApplyAdvancedSettings();
    }

    private void UpdateAdvancedSummary()
    {
        if (_routeAdvancedSummary.IsDisposed)
            return;

        _routeAdvancedSummary.Text =
            $"分区起始侧：{GetPartitionStartSideText()} · 起始方向：{GetStartDirectionText()} · 编号/坐标/中央经线";
    }

    private void ShowRouteEditSection()
    {
        if (_routeEditSection is not null) _routeContinuousHost?.ScrollControlIntoView(_routeEditSection);
    }

    private void ApplySetupWidthProfile()
    {
        TableLayoutPanel? setup =
            _routeSetupLayout;
        if (setup is null ||
            setup.IsDisposed ||
            setup.ColumnStyles.Count < 2)
        {
            return;
        }

        float dpiScale =
            Math.Max(
                1f,
                DeviceDpi / 96f);
        double logicalWidth =
            ClientSize.Width /
            dpiScale;

        bool stacked = logicalWidth < 1120;
        Control[] groups = setup.Controls.Cast<Control>().ToArray();
        setup.SuspendLayout();
        setup.ColumnCount = stacked ? 1 : 2;
        setup.RowCount = stacked ? 2 : 1;
        for (int i = 0; i < groups.Length; i++)
            setup.SetCellPosition(groups[i], new TableLayoutPanelCellPosition(stacked ? 0 : i, stacked ? i : 0));
        setup.RowStyles.Clear();
        setup.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        if (stacked) setup.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        setup.ResumeLayout(true);

        float inputPercent;
        if (logicalWidth < 1120)
        {
            inputPercent = 60f;
        }
        else if (logicalWidth < 1500)
        {
            inputPercent = 55f;
        }
        else
        {
            inputPercent = 50f;
        }

        setup.ColumnStyles[0].SizeType =
            SizeType.Percent;
        setup.ColumnStyles[0].Width =
            stacked ? 100 : inputPercent;
        setup.ColumnStyles[1].SizeType =
            SizeType.Percent;
        setup.ColumnStyles[1].Width =
            stacked ? 0 : 100f - inputPercent;
    }

    internal void NormalizeLayout()
    {
        ApplySetupWidthProfile();

        // Let the map use larger screens instead of keeping a fixed-height
        // editing strip. The continuous workflow remains scrollable on laptops.
        if (_routeContinuousHost?.Controls.OfType<TableLayoutPanel>().FirstOrDefault() is { } workflow)
        {
            float scale = Math.Max(1f, DeviceDpi / 96f);
            float height = Math.Max(440f * scale, _routeContinuousHost.ClientSize.Height * 0.60f);
            if (Math.Abs(workflow.RowStyles[1].Height - height) > 1f)
                workflow.RowStyles[1].Height = height;
        }

        SplitContainer? splitter =
            _routeMainSplit;
        if (splitter is null ||
            splitter.IsDisposed)
        {
            return;
        }

        int available =
            splitter.ClientSize.Width -
            splitter.SplitterWidth;
        if (available <= 0)
            return;

        int min1 =
            Math.Min(
                180,
                Math.Max(
                    100,
                    available / 5));
        int min2 =
            Math.Min(
                300,
                Math.Max(
                    140,
                    available / 4));
        if (available <= min1 + min2)
            return;

        int desired =
            (int)Math.Round(
                available * 0.30);
        int max =
            available - min2;
        desired =
            Math.Clamp(
                desired,
                min1,
                max);

        int current =
            splitter.SplitterDistance;
        if (current < min1 ||
            current > max ||
            current < available * 0.16 ||
            current > available * 0.42)
        {
            splitter.SplitterDistance =
                desired;
        }
    }

    private static void SelectCombo(
        ComboBox box,
        string value)
    {
        int index =
            box.Items.IndexOf(
                value);
        if (index >= 0)
        {
            box.SelectedIndex =
                index;
        }
    }
}
