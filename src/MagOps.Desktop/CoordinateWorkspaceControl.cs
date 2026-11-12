using System.Globalization;
using MagOps.Core.Coordinates;
using MagOps.Infrastructure.Services;

namespace MagOps.Desktop;

internal enum CoordinateKmlTemplateChoice
{
    BuiltInSky = 0,
    BuiltInRedYellow = 1,
    Uploaded = 2,
    Custom = 3
}

internal sealed record CoordinateWorkspaceActions(
    Action ReloadProjectResult,
    Action ConvertCoordinates,
    Action UseWorkareaRange,
    Action<string?> SelectKmlTemplate,
    Action EditKmlFormat,
    Action<string> CopyCoordinateColumns,
    Action ExportCoordinates,
    Action ExportKmlRange,
    Action ExportOasisPly,
    Action RefreshCoordinateRecords,
    Action RegisterCoordinates,
    Action<string?> ImportCoordinateCsv,
    Action RegisterCrsDefinition,
    Action<string?> AuditSpatialFile,
    Action<string?> ConvertSpatialFile,
    Action CalculateIgrfField,
    Action<CoordinateKmlTemplateChoice> KmlChoiceChanged);

internal sealed class CoordinateWorkspaceControl : UserControl
{
    internal SurveyTrackControl AccessPlanning { get; }
    private TabControl? _sections;
    private TabPage? _accessPage;
    internal void ShowAccessPlanning() { if (_sections is not null) _sections.SelectedTab = _accessPage; }
    private readonly CoordinateWorkspaceActions _actions;

    private readonly ComboBox _coordMode = new();
    private readonly ComboBox _coordDatum = new();
    private readonly ComboBox _coordProjection = new();
    private readonly NumericUpDown _coordUtmZone = new() { Minimum = 1, Maximum = 60, Value = 48, Width = 72 };
    private readonly ComboBox _coordHemisphere = new();
    private readonly Label _coordProjectionNotice = new() { AutoSize = true, MaximumSize = new Size(480, 0) };
    private readonly TextBox _coordInput = new();
    private readonly TextBox _coordCentralMeridian = new();
    private readonly TextBox _coordFalseEasting = new();
    private readonly TextBox _coordFalseNorthing = new();
    private readonly ComboBox _coordAxis = new();
    private readonly NumericUpDown _coordDecimalPlaces = new();
    private readonly CheckBox _coordIncludeZoneNumber = new();
    private readonly CheckBox _coordAutoMeridian = new();
    private readonly TextBox _coordDmsSeparators = new();
    private readonly Label _coordSeparatorSummary = new();
    private readonly Label _coordProjectSourceSummary = new();
    private readonly Label _coordWarning = new();
    private readonly Label _coordParameterSummary = new();
    private readonly TextBox _coordParameterDetails = new() { ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Both,
        WordWrap = true, Dock = DockStyle.Fill };
    private readonly DataGridView _coordResults = GridFactory.Create();
    private readonly TabControl _coordDisplayTabs = new();
    private readonly CoordinatePreviewPanel _coordPreview = new();
    private readonly TextBox _coordMeasurement = new() { ReadOnly = true, Multiline = true,
        ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Height = 108 };
    private readonly Button _measureA = new();
    private readonly Button _measureB = new();
    private CoordinateConversionResult[] _measurementPoints = [];
    private int _measurementA = -1;
    private int _measurementB = -1;
    private string? _measurementInvalidReason;
    private readonly BoundaryMeasurementPreviewPanel _boundaryPreview = new();
    private readonly TextBox _boundaryDetails = new() { ReadOnly = true, Multiline = true,
        ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
    private readonly Button _locateBoundaryCentroid = new() { Text = "定位面积重心", AutoSize = true, Enabled = false };
    private readonly DataGridView _coordinateRegistryGrid = GridFactory.Create();

    private readonly RadioButton _coordKmlBuiltInSky = new()
    {
        Text = "天青边 / 天青填充",
        AutoSize = true,
        Checked = true
    };

    private readonly RadioButton _coordKmlBuiltInRedYellow = new()
    {
        Text = "红色边 / 黄色填充",
        AutoSize = true
    };

    private readonly RadioButton _coordKmlUploaded = new()
    {
        Text = "上传 KML 模板",
        AutoSize = true
    };

    private readonly RadioButton _coordKmlCustom = new()
    {
        Text = "自定义 KML 格式",
        AutoSize = true
    };

    private readonly Label _coordKmlTemplatePathLabel = new();

    private bool _syncingCoordinatePreviewSelection;
    private bool _suppressKmlChoiceNotification;
    private int _previewPointCount;

    internal CoordinateWorkspaceControl(CoordinateWorkspaceActions actions) : this(actions, null, null) { }

    internal CoordinateWorkspaceControl(
        CoordinateWorkspaceActions actions, Action<MagOps.Core.Routes.AccessPlanningData>? saveAccess, Func<ProjectionParameters?>? accessProjection)
    {
        _actions = actions;
        AccessPlanning = new(saveAccess ?? (_ => { }), accessProjection ?? (() => null));

        Dock = DockStyle.Fill;
        Margin = Padding.Empty;
        Padding = Padding.Empty;
        BackColor = UiTheme.Workspace;

        Build();
        foreach (Control input in new Control[] { _coordInput, _coordCentralMeridian, _coordFalseEasting,
            _coordFalseNorthing, _coordDmsSeparators })
            input.TextChanged += (_, _) => InvalidateMeasurement();
        foreach (var input in new[] { _coordMode, _coordDatum, _coordAxis, _coordProjection, _coordHemisphere })
            input.SelectedIndexChanged += (_, _) => InvalidateMeasurement();
        _coordUtmZone.ValueChanged += (_, _) => InvalidateMeasurement();
        _coordAutoMeridian.CheckedChanged += (_, _) => InvalidateMeasurement();
        _coordIncludeZoneNumber.CheckedChanged += (_, _) => InvalidateMeasurement();
        _coordDecimalPlaces.ValueChanged += (_, _) => InvalidateMeasurement();
        InvalidateMeasurement();
        foreach (var (control, label) in new (Control, string)[]
        {
            (_coordMode, "坐标转换模式"), (_coordDatum, "坐标椭球基准"), (_coordInput, "待转换坐标"),
            (_coordCentralMeridian, "中央经线（度）"), (_coordFalseEasting, "假东距（米）"),
            (_coordFalseNorthing, "假北距（米）"), (_coordAxis, "坐标轴顺序"), (_coordDecimalPlaces, "输出小数位数"),
            (_coordDmsSeparators, "度分秒分隔符"), (_coordResults, "坐标转换结果"),
            (_coordParameterDetails, "本次转换完整参数"), (_coordinateRegistryGrid, "已登记坐标成果")
        }) control.AccessibleName = label;
    }

    private void Build()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = UiTheme.PagePadding,
            BackColor = UiTheme.Workspace
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        root.Controls.Add(UiComponents.PageHeader(
            "坐标与空间",
            "进入页面时优先读取当前工区已有坐标成果；输入、投影参数、结果预览和导出按工作流分区。"), 0, 0);

        var sections = new TabControl { Dock = DockStyle.Fill, Multiline = false };
        _sections = sections;
        var converterTab = new TabPage("坐标转换") { Padding = new Padding(6), AutoScroll = true };
        var recordsTab = new TabPage("坐标记录 / 空间审计") { Padding = new Padding(6), AutoScroll = true };
        sections.TabPages.Add(converterTab);
        sections.TabPages.Add(recordsTab);
        sections.TabPages.Add(BuildBoundaryMeasurementTab());
        _accessPage = new TabPage("踏勘轨迹") { Padding = new Padding(6) };
        _accessPage.Controls.Add(AccessPlanning); sections.TabPages.Add(_accessPage);
        root.Controls.Add(sections, 0, 1);

        // ---------- 坐标转换：按 Python 包的左右工作台结构 ----------
        var converterBody = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        converterBody.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        converterBody.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));

        var left = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 20,
            AutoScroll = true,
            Padding = new Padding(6),
            Margin = new Padding(0, 0, 4, 0)
        };
        left.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 118));
        left.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        int row = 0;
        left.Controls.Add(new Label
        {
            Text = "1  输入与项目成果",
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Margin = new Padding(3, 0, 3, 7)
        }, 0, row);
        left.SetColumnSpan(left.GetControlFromPosition(0, row)!, 2);
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        row++;

        var projectSourceRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            Margin = new Padding(3, 0, 3, 6),
            Padding = Padding.Empty
        };
        projectSourceRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        projectSourceRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _coordProjectSourceSummary.Text = "项目成果：进入本页时自动检查当前工区。";
        _coordProjectSourceSummary.AutoSize = true;
        _coordProjectSourceSummary.ForeColor = UiTheme.TextSecondary;
        _coordProjectSourceSummary.MaximumSize = new Size(420, 0);
        _coordProjectSourceSummary.Margin = new Padding(0, 6, 8, 0);
        projectSourceRow.Controls.Add(_coordProjectSourceSummary, 0, 0);
        projectSourceRow.Controls.Add(UiComponents.Button(
            "重新读取项目成果",
            (_, _) => _actions.ReloadProjectResult(),
            UiButtonRole.Subtle,
            minimumHeight: 32), 1, 0);
        left.Controls.Add(projectSourceRow, 0, row);
        left.SetColumnSpan(projectSourceRow, 2);
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        row++;

        AddCoordinateWorkbenchField(left, row++, "输入格式", _coordMode);
        _coordMode.DropDownStyle = ComboBoxStyle.DropDownList;
        _coordMode.Items.AddRange(["度分秒", "小数格式（十进制度）", "XY"]);
        _coordMode.SelectedIndex = 0;
        _coordMode.SelectedIndexChanged += (_, _) => UpdateCoordinateModeUi();

        left.Controls.Add(new Label
        {
            Text = "整体坐标",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(3, 7, 3, 3)
        }, 0, row);
        _coordInput.Multiline = true;
        _coordInput.ScrollBars = ScrollBars.Both;
        _coordInput.WordWrap = false;
        _coordInput.Dock = DockStyle.Fill;
        _coordInput.PlaceholderText = "支持 DMS、十进制度、XY；可粘贴“点号 / 经度 / 纬度”表格，也兼容无表头“点号→经度→纬度”每三行一组。";
        left.Controls.Add(_coordInput, 1, row);
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 94));
        row++;

        var inputHint = new Label
        {
            Text = "度分秒兼容完整两字段、三列表格、无表头每三行一组、六列 D/M/S、自定义分隔符和半球标记；检测到 °/分秒符号时会自动切换为度分秒。",
            AutoSize = true,
            MaximumSize = new Size(520, 0),
            Margin = new Padding(3, 2, 3, 6)
        };
        left.Controls.Add(inputHint, 0, row);
        left.SetColumnSpan(inputHint, 2);
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        row++;

        left.Controls.Add(new Label
        {
            Text = "DMS 分隔符",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(3, 7, 3, 3)
        }, 0, row);
        var separatorActions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = true,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        separatorActions.Controls.Add(UiComponents.Button(
            "分隔符管理…",
            (_, _) => EditCoordinateDmsSeparators(),
            UiButtonRole.Subtle,
            minimumHeight: 32));
        _coordSeparatorSummary.AutoSize = true;
        _coordSeparatorSummary.ForeColor = UiTheme.TextSecondary;
        _coordSeparatorSummary.Margin = new Padding(8, 8, 3, 3);
        separatorActions.Controls.Add(_coordSeparatorSummary);
        left.Controls.Add(separatorActions, 1, row);
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        row++;
        UpdateCoordinateSeparatorSummary();

        var separator = new Panel
        {
            Dock = DockStyle.Fill,
            Height = 1,
            BorderStyle = BorderStyle.FixedSingle,
            Margin = new Padding(3, 6, 3, 8)
        };
        left.Controls.Add(separator, 0, row);
        left.SetColumnSpan(separator, 2);
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 16));
        row++;

        var projectionTitle = new Label
        {
            Text = "2  投影参数",
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Margin = new Padding(3, 0, 3, 7)
        };
        left.Controls.Add(projectionTitle, 0, row);
        left.SetColumnSpan(projectionTitle, 2);
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        row++;

        _coordProjection.AccessibleName = "投影方式";
        _coordProjection.DropDownStyle = ComboBoxStyle.DropDownList;
        _coordProjection.Items.AddRange(["高斯-克吕格三度带", "WGS84 / UTM", "Kertau (RSO) / RSO Malaya (m)"]);
        _coordProjection.SelectedIndex = 0;
        AddCoordinateWorkbenchField(left, row++, "投影方式", _coordProjection);
        var utmOptions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, WrapContents = true };
        _coordUtmZone.AccessibleName = "UTM带号（1～60）";
        _coordHemisphere.AccessibleName = "UTM南北半球";
        _coordHemisphere.DropDownStyle = ComboBoxStyle.DropDownList;
        _coordHemisphere.Items.AddRange(["北半球 N", "南半球 S"]);
        _coordHemisphere.SelectedIndex = 0;
        _coordHemisphere.Width = 108;
        utmOptions.Controls.Add(_coordUtmZone);
        utmOptions.Controls.Add(_coordHemisphere);
        AddCoordinateWorkbenchField(left, row++, "UTM带号 / 半球", utmOptions);
        AddCoordinateWorkbenchField(left, row++, "经纬度与精度", _coordProjectionNotice);

        AddCoordinateWorkbenchField(left, row++, "椭球 / 基准", _coordDatum);
        _coordDatum.DropDownStyle = ComboBoxStyle.DropDownList;
        _coordDatum.Items.AddRange(["CGCS2000", "WGS84"]);
        _coordDatum.SelectedIndex = 0;

        AddCoordinateWorkbenchField(left, row++, "XY 轴顺序", _coordAxis);
        _coordAxis.DropDownStyle = ComboBoxStyle.DropDownList;
        _coordAxis.Items.AddRange(["GIS：X=Easting / Y=Northing", "测绘：X=Northing / Y=Easting"]);
        _coordAxis.SelectedIndex = 0;

        AddCoordinateWorkbenchField(left, row++, "中央子午线（°）", _coordCentralMeridian);
        _coordCentralMeridian.PlaceholderText = "自动或手工，如 117";

        _coordAutoMeridian.Text = "经纬度输入时自动建议三度带中央子午线";
        _coordAutoMeridian.Checked = true;
        _coordAutoMeridian.AutoSize = true;
        _coordAutoMeridian.CheckedChanged += (_, _) => UpdateCoordinateModeUi();
        left.Controls.Add(new Label { Text = "", AutoSize = true }, 0, row);
        left.Controls.Add(_coordAutoMeridian, 1, row);
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        row++;

        AddCoordinateWorkbenchField(left, row++, "假东（m）", _coordFalseEasting);
        _coordFalseEasting.Text = "500000";
        AddCoordinateWorkbenchField(left, row++, "假北（m）", _coordFalseNorthing);
        _coordFalseNorthing.Text = "0";
        _coordProjection.SelectedIndexChanged += (_, _) => UpdateProjectionUi();
        _coordUtmZone.ValueChanged += (_, _) => UpdateProjectionUi();
        _coordHemisphere.SelectedIndexChanged += (_, _) => UpdateProjectionUi();
        UpdateProjectionUi();

        var outputSeparator = new Panel
        {
            Dock = DockStyle.Fill,
            Height = 1,
            BorderStyle = BorderStyle.FixedSingle,
            Margin = new Padding(3, 6, 3, 8)
        };
        left.Controls.Add(outputSeparator, 0, row);
        left.SetColumnSpan(outputSeparator, 2);
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 16));
        row++;

        var outputTitle = new Label
        {
            Text = "3  输出设置",
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Margin = new Padding(3, 0, 3, 7)
        };
        left.Controls.Add(outputTitle, 0, row);
        left.SetColumnSpan(outputTitle, 2);
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        row++;

        _coordDecimalPlaces.Minimum = 0;
        _coordDecimalPlaces.Maximum = 12;
        _coordDecimalPlaces.Value = 8;
        _coordDecimalPlaces.Width = 82;
        AddCoordinateWorkbenchField(left, row++, "小数坐标位数", _coordDecimalPlaces);

        _coordIncludeZoneNumber.Text = "添加带号（仅东向坐标）";
        _coordIncludeZoneNumber.AutoSize = true;
        _coordIncludeZoneNumber.Checked = false;
        _coordIncludeZoneNumber.Margin = new Padding(0, 4, 0, 4);
        left.Controls.Add(new Label
        {
            Text = "带号",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(3, 7, 3, 3)
        }, 0, row);
        left.Controls.Add(_coordIncludeZoneNumber, 1, row);
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        row++;

        var convertActions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = true,
            AutoScroll = false,
            Margin = new Padding(0, 6, 0, 0)
        };
        convertActions.Controls.Add(Button("转换坐标", (_, _) => _actions.ConvertCoordinates(), UiButtonRole.Primary));
        convertActions.Controls.Add(Button("设为当前工区范围", (_, _) => _actions.UseWorkareaRange(), UiButtonRole.Subtle));
        left.Controls.Add(convertActions, 0, row);
        left.SetColumnSpan(convertActions, 2);
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        row++;

        var kmlGroup = new GroupBox
        {
            Text = "范围 KML 样式",
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(8),
            Margin = new Padding(3, 10, 3, 3)
        };
        var kmlLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 4,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        kmlLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        kmlLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        kmlLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        kmlLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var kmlTemplateChoices = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            WrapContents = true,
            Margin = Padding.Empty
        };
        kmlTemplateChoices.Controls.Add(_coordKmlBuiltInSky);
        kmlTemplateChoices.Controls.Add(_coordKmlBuiltInRedYellow);
        kmlTemplateChoices.Controls.Add(_coordKmlUploaded);
        kmlTemplateChoices.Controls.Add(_coordKmlCustom);
        kmlLayout.Controls.Add(kmlTemplateChoices, 0, 0);

        var kmlTemplateActions = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            WrapContents = true,
            AutoScroll = false,
            Margin = new Padding(0, 4, 0, 0)
        };
        var selectKmlTemplate = Button(
            "选择模板…",
            (_, _) => _actions.SelectKmlTemplate(null));
        FileDropSupport.EnableSingle(
            selectKmlTemplate,
            path => _actions.SelectKmlTemplate(path),
            path => FileDropSupport.HasExtension(path, ".kml"));
        FileDropSupport.EnableSingle(
            _coordKmlUploaded,
            path => _actions.SelectKmlTemplate(path),
            path => FileDropSupport.HasExtension(path, ".kml"));
        FileDropSupport.EnableSingle(
            _coordKmlTemplatePathLabel,
            path => _actions.SelectKmlTemplate(path),
            path => FileDropSupport.HasExtension(path, ".kml"));
        kmlTemplateActions.Controls.Add(selectKmlTemplate);
        kmlTemplateActions.Controls.Add(Button(
            "编辑自定义格式…",
            (_, _) => _actions.EditKmlFormat()));
        kmlLayout.Controls.Add(kmlTemplateActions, 0, 1);

        _coordKmlTemplatePathLabel.Text = "内置模板：天青边 / 天青填充（DJI 极简兼容）";
        _coordKmlTemplatePathLabel.AutoSize = true;
        _coordKmlTemplatePathLabel.MaximumSize = new Size(520, 0);
        _coordKmlTemplatePathLabel.ForeColor = SystemColors.GrayText;
        _coordKmlTemplatePathLabel.Margin = new Padding(3, 4, 3, 5);
        kmlLayout.Controls.Add(_coordKmlTemplatePathLabel, 0, 2);

        kmlLayout.Controls.Add(UiComponents.HelpLabel(
            "这里只配置范围 KML 的显示样式；实际导出统一在右侧底部“导出”区完成。"), 0, 3);
        kmlGroup.Controls.Add(kmlLayout);

        _coordKmlBuiltInSky.CheckedChanged += (_, _) =>
            NotifyKmlChoiceChanged(
                CoordinateKmlTemplateChoice.BuiltInSky,
                _coordKmlBuiltInSky.Checked);
        _coordKmlBuiltInRedYellow.CheckedChanged += (_, _) =>
            NotifyKmlChoiceChanged(
                CoordinateKmlTemplateChoice.BuiltInRedYellow,
                _coordKmlBuiltInRedYellow.Checked);
        _coordKmlUploaded.CheckedChanged += (_, _) =>
            NotifyKmlChoiceChanged(
                CoordinateKmlTemplateChoice.Uploaded,
                _coordKmlUploaded.Checked);
        _coordKmlCustom.CheckedChanged += (_, _) =>
            NotifyKmlChoiceChanged(
                CoordinateKmlTemplateChoice.Custom,
                _coordKmlCustom.Checked);

        left.Controls.Add(kmlGroup, 0, row);
        left.SetColumnSpan(kmlGroup, 2);
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        converterBody.Controls.Add(left, 0, 0);

        var right = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(6),
            Margin = new Padding(4, 0, 0, 0)
        };
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        // Reserve two compact action rows; AutoSize flow panels can consume the
        // entire remaining height and collapse the results to one pixel.
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 64 * DeviceDpi / 96f));

        _coordDisplayTabs.Dock = DockStyle.Fill;
        _coordDisplayTabs.Multiline = false;

        var resultTab = new TabPage("自动转换结果")
        {
            Padding = new Padding(6)
        };
        var resultLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        resultLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        resultLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 100 * DeviceDpi / 96f));
        resultLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _coordWarning.Text = "输入或粘贴坐标后点击“转换”生成结果表。";
        _coordWarning.AutoSize = true;
        _coordWarning.MaximumSize = new Size(760, 0);
        _coordWarning.Margin = new Padding(0, 2, 0, 6);
        resultLayout.Controls.Add(_coordWarning, 0, 0);

        var parameterBox = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0, 0, 0, 2),
            Padding = Padding.Empty
        };
        parameterBox.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        parameterBox.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _coordParameterSummary.Text = "等待坐标输入。";
        _coordParameterSummary.Dock = DockStyle.Fill;
        _coordParameterSummary.AutoSize = false;
        _coordParameterSummary.MaximumSize = new Size(760, 0);
        _coordParameterSummary.TextAlign = ContentAlignment.MiddleLeft;
        _coordParameterSummary.AutoEllipsis = true;
        parameterBox.Controls.Add(_coordParameterSummary, 0, 0);
        var parameterPage = new TabPage("完整参数") { Padding = new Padding(6) };
        parameterPage.Controls.Add(_coordParameterDetails);
        var parameterDetailsButton = UiComponents.Button("查看完整参数", (_, _) => _coordDisplayTabs.SelectedTab = parameterPage,
            UiButtonRole.Subtle, minimumHeight: 28);
        parameterBox.Controls.Add(parameterDetailsButton, 1, 0);
        _coordParameterDetails.Text = "等待坐标输入。";
        _coordParameterSummary.TextChanged += (_, _) => _coordParameterDetails.Text = _coordParameterSummary.Text;
        resultLayout.Controls.Add(parameterBox, 0, 1);
        resultLayout.Controls.Add(_coordResults, 0, 2);
        resultTab.Controls.Add(resultLayout);

        var previewTab = new TabPage("区域预览")
        {
            Padding = new Padding(6)
        };
        var previewLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        previewLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        previewLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        previewLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var previewInstructions = new Label
        {
            Text = "单击选点；设为 A/B 测量。方向键平移，+/− 缩放，Home 全图。末首虚线仅作点序预览。",
            AutoSize = true,
            MaximumSize = new Size(760, 0),
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 0, 0, 4)
        };
        previewLayout.Controls.Add(previewInstructions, 0, 0);
        var previewNavigation = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = true,
            AutoScroll = false,
            Margin = new Padding(0, 0, 0, 6),
            Padding = Padding.Empty
        };
        previewNavigation.Controls.Add(UiComponents.Button(
            "← 上一点",
            (_, _) => StepCoordinatePreview(-1),
            UiButtonRole.Subtle,
            minimumHeight: 30));
        previewNavigation.Controls.Add(UiComponents.Button(
            "下一点 →",
            (_, _) => StepCoordinatePreview(1),
            UiButtonRole.Subtle,
            minimumHeight: 30));
        previewLayout.SizeChanged += (_, _) =>
        {
            int width = Math.Max(80, previewLayout.ClientSize.Width - 12);
            previewInstructions.MaximumSize = new Size(width, 0);
        };
        previewLayout.Controls.Add(previewNavigation, 0, 1);
        previewLayout.Controls.Add(_coordPreview, 0, 2);
        // Keep measurement on the map page so endpoint selection and its result stay visible together.
        previewLayout.RowCount = 4;
        previewLayout.RowStyles.Insert(2, new RowStyle(SizeType.Absolute, 108));
        previewLayout.SetRow(_coordPreview, 3);
        _measureA.Text = "选中点设为 A";
        _measureB.Text = "选中点设为 B";
        foreach (var button in new[] { _measureA, _measureB })
        {
            button.AutoSize = true;
            button.MinimumSize = new Size(0, 30);
            previewNavigation.Controls.Add(button);
        }
        _measureA.Click += (_, _) => SetMeasurementEndpoint(true);
        _measureB.Click += (_, _) => SetMeasurementEndpoint(false);
        previewNavigation.Controls.Add(UiComponents.Button("清除测量", (_, _) => ClearMeasurement(),
            UiButtonRole.Subtle, minimumHeight: 30));
        _coordMeasurement.AccessibleName = "两点跨度测量（投影米）";
        previewLayout.Controls.Add(_coordMeasurement, 0, 2);
        previewTab.Controls.Add(previewLayout);

        _coordDisplayTabs.TabPages.Add(resultTab);
        _coordDisplayTabs.TabPages.Add(previewTab);
        _coordDisplayTabs.TabPages.Add(parameterPage);
        right.Controls.Add(_coordDisplayTabs, 0, 0);

        _coordResults.CellClick += (_, e) =>
        {
            if (e.RowIndex < 0 ||
                e.ColumnIndex < 0 ||
                e.ColumnIndex >= _coordResults.Columns.Count)
            {
                return;
            }

            string property = _coordResults.Columns[e.ColumnIndex].DataPropertyName;
            if (property.Equals(
                    nameof(CoordinateConversionGridRow.Index),
                    StringComparison.OrdinalIgnoreCase) &&
                TryGetCoordinatePreviewIndex(
                    _coordResults.Rows[e.RowIndex],
                    out int previewIndex))
            {
                SelectCoordinatePreviewPoint(
                    previewIndex,
                    switchToPreview: true);
            }
        };
        _coordResults.SelectionChanged += (_, _) =>
        {
            if (_syncingCoordinatePreviewSelection)
                return;

            if (_coordResults.CurrentRow is not null &&
                TryGetCoordinatePreviewIndex(
                    _coordResults.CurrentRow,
                    out int previewIndex))
            {
                _coordPreview.SelectedIndex = previewIndex;
            }
        };
        _coordPreview.PointSelected += index =>
            SelectCoordinatePreviewPoint(
                index,
                switchToPreview: false);

        // Small-screen hotfix: copy/export groups stack vertically so an AutoSize
        // export strip cannot force the whole coordinate page wider than the screen.
        var resultActions = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0, 6, 0, 0),
            Padding = Padding.Empty
        };
        resultActions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        resultActions.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        resultActions.RowStyles.Add(new RowStyle(SizeType.Percent, 50));

        var copyActions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            WrapContents = true,
            AutoScroll = false,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        copyActions.Controls.Add(Button("IGRF 主场…", (_, _) => _actions.CalculateIgrfField(), UiButtonRole.Subtle));
        var copyMenu = new ContextMenuStrip();
        copyMenu.Items.Add("复制度分秒", null, (_, _) => _actions.CopyCoordinateColumns("dms"));
        copyMenu.Items.Add("复制十进制度", null, (_, _) => _actions.CopyCoordinateColumns("decimal"));
        copyMenu.Items.Add("复制 XY", null, (_, _) => _actions.CopyCoordinateColumns("xy"));
        var copyButton = Button("复制坐标 ▾", (_, _) => { }, UiButtonRole.Subtle);
        copyButton.ContextMenuStrip = copyMenu;
        copyButton.Click += (_, _) => copyMenu.Show(copyButton, new Point(0, copyButton.Height));
        copyButton.Disposed += (_, _) => copyMenu.Dispose();
        copyActions.Controls.Add(copyButton);
        resultActions.Controls.Add(copyActions, 0, 0);

        var exportActions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            WrapContents = true,
            AutoScroll = false,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = new Padding(0, 4, 0, 0),
            Padding = Padding.Empty
        };
        exportActions.Controls.Add(Button("导出范围 KML", (_, _) => _actions.ExportKmlRange(), UiButtonRole.Primary));
        var exportMenu = new ContextMenuStrip();
        exportMenu.Items.Add("导出坐标 CSV", null, (_, _) => _actions.ExportCoordinates());
        exportMenu.Items.Add("导出 Oasis PLY", null, (_, _) => _actions.ExportOasisPly());
        var exportButton = Button("其他导出 ▾", (_, _) => { });
        exportButton.ContextMenuStrip = exportMenu;
        exportButton.Click += (_, _) => exportMenu.Show(exportButton, new Point(0, exportButton.Height));
        exportButton.Disposed += (_, _) => exportMenu.Dispose();
        exportActions.Controls.Add(exportButton);
        resultActions.Controls.Add(exportActions, 0, 1);
        right.Controls.Add(resultActions, 0, 1);

        converterBody.Controls.Add(right, 1, 0);
        converterTab.Controls.Add(converterBody);

        // ---------- 坐标记录 / 空间审计 ----------
        var recordsLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        recordsLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        recordsLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        recordsLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        recordsLayout.Controls.Add(new Label
        {
            Text = "坐标记录用于保存项目中已登记坐标的点号、经纬度/XY、高程类型、源坐标系和来源资料；" +
                   "空间审计用于快速检查 CSV、KML、GPX 的明显结构问题，不替代正式 GIS/测绘成果验收。",
            AutoSize = true,
            MaximumSize = new Size(1180, 0),
            Margin = new Padding(0, 0, 0, 6)
        }, 0, 0);

        recordsLayout.Controls.Add(_coordinateRegistryGrid, 0, 1);

        var recordActions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = true,
            AutoScroll = false,
            Margin = new Padding(0, 6, 0, 0)
        };
        recordActions.Controls.Add(Button("刷新坐标记录", (_, _) => _actions.RefreshCoordinateRecords()));
        recordActions.Controls.Add(Button("登记当前转换结果", (_, _) => _actions.RegisterCoordinates()));
        var importCoordinateCsv = Button("导入坐标 CSV", (_, _) => _actions.ImportCoordinateCsv(null));
        FileDropSupport.EnableSingle(
            importCoordinateCsv,
            path => _actions.ImportCoordinateCsv(path),
            path => FileDropSupport.HasExtension(path, ".csv"));
        recordActions.Controls.Add(importCoordinateCsv);
        recordActions.Controls.Add(Button("登记 CRS", (_, _) => _actions.RegisterCrsDefinition()));

        var auditSpatialFile = Button("审计空间文件", (_, _) => _actions.AuditSpatialFile(null));
        FileDropSupport.EnableSingle(
            auditSpatialFile,
            path => _actions.AuditSpatialFile(path),
            path => FileDropSupport.HasExtension(path, ".kml", ".gpx"));
        recordActions.Controls.Add(auditSpatialFile);

        var convertSpatialFile = Button("KML/GPX 互转", (_, _) => _actions.ConvertSpatialFile(null));
        FileDropSupport.EnableSingle(
            convertSpatialFile,
            path => _actions.ConvertSpatialFile(path),
            path => FileDropSupport.HasExtension(path, ".kml", ".gpx"));
        recordActions.Controls.Add(convertSpatialFile);
        recordsLayout.Controls.Add(recordActions, 0, 2);

        recordsTab.Controls.Add(recordsLayout);
        Controls.Add(root);
        UpdateCoordinateModeUi();
    }

    private static Button Button(
        string text,
        EventHandler handler) =>
        UiComponents.Button(
            text,
            handler);

    private static Button Button(
        string text,
        EventHandler handler,
        UiButtonRole role) =>
        UiComponents.Button(
            text,
            handler,
            role);

    private static void AddCoordinateWorkbenchField(
        TableLayoutPanel panel,
        int row,
        string labelText,
        Control control)
    {
        panel.Controls.Add(
            new Label
            {
                Text = labelText,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(3, 7, 3, 3)
            },
            0,
            row);
        control.Dock = DockStyle.Top;
        panel.Controls.Add(
            control,
            1,
            row);
        panel.RowStyles.Add(
            new RowStyle(
                SizeType.AutoSize));
    }

    private void UpdateCoordinateModeUi()
    {
        bool gk = GetProjectionKind() == CoordinateProjectionKind.GaussKruger;
        bool xy =
            _coordMode.SelectedIndex == 2;
        _coordAutoMeridian.Enabled =
            gk && !xy;
        _coordCentralMeridian.Enabled =
            gk && (xy || !_coordAutoMeridian.Checked);
        _coordDmsSeparators.Enabled =
            _coordMode.SelectedIndex == 0;

        if (xy)
            _coordAutoMeridian.Checked = false;
    }

    private void UpdateProjectionUi()
    {
        bool gk = GetProjectionKind() == CoordinateProjectionKind.GaussKruger;
        bool utm = GetProjectionKind() == CoordinateProjectionKind.Utm;
        _coordUtmZone.Enabled = _coordHemisphere.Enabled = utm;
        _coordDatum.Enabled = _coordFalseEasting.Enabled = _coordFalseNorthing.Enabled = gk;
        _coordIncludeZoneNumber.Enabled = gk;
        if (!gk)
        {
            if (!_coordDatum.Items.Contains("Kertau (RSO)")) _coordDatum.Items.Add("Kertau (RSO)");
            var p = CoordinateProjectionService.Normalize(new ProjectionParameters(Kind: GetProjectionKind(), UtmZone: (int)_coordUtmZone.Value, SouthernHemisphere: _coordHemisphere.SelectedIndex == 1));
            SelectDatum(p.Datum);
            _coordCentralMeridian.Text = p.CentralMeridian!.Value.ToString("0.########", CultureInfo.InvariantCulture);
            _coordFalseEasting.Text = p.FalseEasting.ToString(CultureInfo.InvariantCulture);
            _coordFalseNorthing.Text = p.FalseNorthing.ToString(CultureInfo.InvariantCulture);
            _coordAutoMeridian.Checked = _coordIncludeZoneNumber.Checked = false;
        }
        else
        {
            if (GetDatumText() == "Kertau (RSO)") SelectDatum("CGCS2000");
            _coordDatum.Items.Remove("Kertau (RSO)");
            _coordFalseEasting.Text = "500000"; _coordFalseNorthing.Text = "0";
            _coordCentralMeridian.Clear();
            _coordAutoMeridian.Checked = _coordMode.SelectedIndex != 2;
        }
        _coordProjectionNotice.Text = gk ? "沿用原高斯三度带及项目中央经线。" : utm
            ? "经纬度输入/输出为WGS84；带号1～60须明确选择，不自动跨带。"
            : CoordinateProjectionService.RsoWarning + " 经纬度输入/输出为WGS84。";
        UpdateCoordinateModeUi();
    }

    internal CoordinateProjectionKind GetProjectionKind() => (CoordinateProjectionKind)Math.Max(0, _coordProjection.SelectedIndex);
    internal ProjectionParameters ReadProjectionParameters(double? central) => CoordinateProjectionService.Normalize(new(
        GetDatumText(), central, double.Parse(GetFalseEastingText(), CultureInfo.InvariantCulture),
        double.Parse(GetFalseNorthingText(), CultureInfo.InvariantCulture), GetAxisIndex() == 1 ? XyAxisOrder.NorthingEasting : XyAxisOrder.EastingNorthing,
        GetProjectionKind(), (int)_coordUtmZone.Value, _coordHemisphere.SelectedIndex == 1));
    internal void SetProjection(CoordinateProjectionKind kind, int zone = 48, bool south = false)
    {
        _coordUtmZone.Value = Math.Clamp(zone, 1, 60);
        _coordHemisphere.SelectedIndex = south ? 1 : 0;
        _coordProjection.SelectedIndex = (int)kind;
        UpdateProjectionUi();
    }

    private void UpdateCoordinateSeparatorSummary()
    {
        string raw =
            (_coordDmsSeparators.Text ?? "")
                .Replace(
                    "\\t",
                    "\t",
                    StringComparison.Ordinal);

        if (raw.Length == 0)
        {
            _coordSeparatorSummary.Text =
                "使用系统内置分隔规则";
            return;
        }

        string display =
            string.Join(
                "、",
                raw
                    .Distinct()
                    .Select(
                        CoordinateSeparatorDialog.DisplaySeparator));
        _coordSeparatorSummary.Text =
            $"自定义：{display}";
    }

    private void EditCoordinateDmsSeparators()
    {
        using var dialog =
            new CoordinateSeparatorDialog(
                _coordDmsSeparators.Text);
        if (dialog.ShowDialog(this) !=
            DialogResult.OK)
        {
            throw new OperationCanceledException();
        }

        _coordDmsSeparators.Text =
            dialog.Separators;
        UpdateCoordinateSeparatorSummary();
    }

    private void StepCoordinatePreview(
        int delta)
    {
        int count =
            _previewPointCount;
        if (count == 0)
            return;

        int current =
            _coordPreview.SelectedIndex;
        if (current < 0 ||
            current >= count)
        {
            current =
                delta >= 0
                    ? -1
                    : 0;
        }

        int next =
            (current + delta) % count;
        if (next < 0)
            next += count;

        SelectCoordinatePreviewPoint(
            next,
            switchToPreview: false);
        _coordPreview.Focus();
    }

    private bool TryGetCoordinatePreviewIndex(
        DataGridViewRow row,
        out int index)
    {
        index = -1;
        DataGridViewColumn? sequenceColumn =
            _coordResults.Columns
                .Cast<DataGridViewColumn>()
                .FirstOrDefault(x =>
                    x.DataPropertyName.Equals(
                        nameof(CoordinateConversionGridRow.Index),
                        StringComparison.OrdinalIgnoreCase));
        if (sequenceColumn is null)
            return false;

        string? text =
            Convert.ToString(
                row.Cells[
                    sequenceColumn.Index].Value,
                CultureInfo.InvariantCulture);
        if (!int.TryParse(
                text,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int sequence) ||
            sequence <= 0)
        {
            return false;
        }

        index =
            sequence - 1;
        return true;
    }

    private void SelectCoordinatePreviewPoint(
        int index,
        bool switchToPreview)
    {
        if (index < 0 ||
            index >= _previewPointCount)
        {
            return;
        }

        _syncingCoordinatePreviewSelection =
            true;
        try
        {
            _coordPreview.SelectedIndex =
                index;

            DataGridViewRow? row =
                _coordResults.Rows
                    .Cast<DataGridViewRow>()
                    .FirstOrDefault(x =>
                        TryGetCoordinatePreviewIndex(
                            x,
                            out int rowIndex) &&
                        rowIndex == index);

            if (row is not null)
            {
                DataGridViewColumn? sequenceColumn =
                    _coordResults.Columns
                        .Cast<DataGridViewColumn>()
                        .FirstOrDefault(x =>
                            x.DataPropertyName.Equals(
                                nameof(CoordinateConversionGridRow.Index),
                                StringComparison.OrdinalIgnoreCase));

                if (sequenceColumn is not null &&
                    _coordResults.CurrentCell !=
                        row.Cells[
                            sequenceColumn.Index])
                {
                    _coordResults.CurrentCell =
                        row.Cells[
                            sequenceColumn.Index];
                }

                if (!row.Selected)
                {
                    _coordResults.ClearSelection();
                    row.Selected =
                        true;
                }

                if (row.Index >= 0 &&
                    row.Index <
                        _coordResults.RowCount)
                {
                    try
                    {
                        _coordResults.FirstDisplayedScrollingRowIndex =
                            row.Index;
                    }
                    catch
                    {
                        // Layout can temporarily reject scrolling.
                    }
                }
            }
        }
        finally
        {
            _syncingCoordinatePreviewSelection =
                false;
        }

        if (switchToPreview &&
            _coordDisplayTabs.TabPages.Count > 1)
        {
            _coordDisplayTabs.SelectedIndex =
                1;
        }
    }

    private void NotifyKmlChoiceChanged(
        CoordinateKmlTemplateChoice choice,
        bool isChecked)
    {
        if (!isChecked ||
            _suppressKmlChoiceNotification)
        {
            return;
        }

        if (choice is
            CoordinateKmlTemplateChoice.Uploaded or
            CoordinateKmlTemplateChoice.Custom)
        {
            BeginInvoke(
                new Action(() =>
                    _actions.KmlChoiceChanged(
                        choice)));
            return;
        }

        _actions.KmlChoiceChanged(
            choice);
    }

    internal int GetModeIndex() =>
        _coordMode.SelectedIndex;

    internal void SetModeIndex(
        int index)
    {
        _coordMode.SelectedIndex =
            Math.Clamp(
                index,
                0,
                Math.Max(
                    0,
                    _coordMode.Items.Count - 1));
        UpdateCoordinateModeUi();
    }

    internal string GetDatumText() =>
        _coordDatum.SelectedItem?.ToString()
        ?? "CGCS2000";

    internal void SelectDatum(
        string datum)
    {
        int index =
            _coordDatum.Items.IndexOf(
                datum);
        if (index >= 0)
            _coordDatum.SelectedIndex =
                index;
    }

    internal string GetInputText() =>
        _coordInput.Text;

    internal void SetInputText(
        string value)
    {
        _coordInput.Text =
            value;
    }

    internal string GetCentralMeridianText() =>
        _coordCentralMeridian.Text;

    internal void SetCentralMeridianText(
        string value)
    {
        _coordCentralMeridian.Text =
            value;
    }

    internal string GetFalseEastingText() =>
        _coordFalseEasting.Text;

    internal void SetFalseEastingText(
        string value)
    {
        _coordFalseEasting.Text =
            value;
    }

    internal string GetFalseNorthingText() =>
        _coordFalseNorthing.Text;

    internal void SetFalseNorthingText(
        string value)
    {
        _coordFalseNorthing.Text =
            value;
    }

    internal int GetAxisIndex() =>
        _coordAxis.SelectedIndex;

    internal string GetAxisText() =>
        _coordAxis.SelectedItem?.ToString()
        ?? "";

    internal void SetAxisIndex(
        int index)
    {
        _coordAxis.SelectedIndex =
            index == 1
                ? 1
                : 0;
    }

    internal int GetDecimalPlaces() =>
        Decimal.ToInt32(
            _coordDecimalPlaces.Value);

    internal void SetDecimalPlaces(
        int value)
    {
        int minimum =
            Decimal.ToInt32(
                _coordDecimalPlaces.Minimum);
        int maximum =
            Decimal.ToInt32(
                _coordDecimalPlaces.Maximum);
        _coordDecimalPlaces.Value =
            Math.Clamp(
                value,
                minimum,
                maximum);
    }

    internal bool GetIncludeZoneNumber() =>
        _coordIncludeZoneNumber.Checked;

    internal void SetIncludeZoneNumber(
        bool value)
    {
        _coordIncludeZoneNumber.Checked =
            value;
    }

    internal bool GetAutoMeridian() =>
        _coordAutoMeridian.Checked;

    internal void SetAutoMeridian(
        bool value)
    {
        _coordAutoMeridian.Checked =
            value;
        UpdateCoordinateModeUi();
    }

    internal string GetDmsSeparators() =>
        _coordDmsSeparators.Text;

    internal void SetProjectSourceSummary(
        string text)
    {
        _coordProjectSourceSummary.Text =
            text;
    }

    internal void SetWarning(
        string text)
    {
        _coordWarning.Text =
            text;
    }

    internal void SetParameterSummary(
        string text, string? compact = null)
    {
        _coordParameterSummary.Text =
            compact ?? text;
        _coordParameterDetails.Text = text;
    }

    internal void ResetForContextChange()
    {
        InvalidateMeasurement();
        SetProjection(CoordinateProjectionKind.GaussKruger);
        SelectDatum("CGCS2000");
        SetAxisIndex(0);
        SetFalseEastingText("500000");
        SetFalseNorthingText("0");
        SetDecimalPlaces(8);
        SetIncludeZoneNumber(false);
        _coordCentralMeridian.Clear();
        _coordAutoMeridian.Checked = true;
        _coordInput.Clear();
        _coordResults.DataSource =
            null;
        _previewPointCount =
            0;
        _coordPreview.SetPoints(
            null);
        _coordWarning.Text =
            "输入坐标，或进入本页读取当前工区已有成果。";
        _coordParameterSummary.Text =
            "等待坐标输入。";
        _coordProjectSourceSummary.Text =
            "项目成果：等待读取当前工区。";
    }

    internal void ShowConversionResults(
        IReadOnlyList<CoordinateConversionGridRow> rows,
        IReadOnlyList<CoordinateConversionResult> previewPoints)
    {
        _previewPointCount =
            previewPoints.Count;
        _coordPreview.SetPoints(
            previewPoints);
        _measurementPoints = previewPoints.ToArray();
        _measurementInvalidReason = null;
        if (_measurementPoints.Any(p => !double.IsFinite(p.Easting) || !double.IsFinite(p.Northing) ||
            !double.IsFinite(p.X) || !double.IsFinite(p.Y)) ||
            _measurementPoints.Any(p => p.Datum != _measurementPoints[0].Datum ||
                p.CentralMeridian != _measurementPoints[0].CentralMeridian ||
                p.ProjectionKind != _measurementPoints[0].ProjectionKind ||
                p.AxisOrderLabel != _measurementPoints[0].AxisOrderLabel))
        {
            _measurementInvalidReason = "测量不可用：点集包含非有限坐标或混合椭球、中央经线、轴序；请统一投影后重新转换。";
            _measurementPoints = [];
        }
        ClearMeasurement();

        _coordResults.DataSource =
            null;
        _coordResults.DataSource =
            rows.ToList();

        GridHeaders.Apply(
            _coordResults,
            new Dictionary<string, string>
            {
                [nameof(CoordinateConversionGridRow.Index)] =
                    "序号",
                [nameof(CoordinateConversionGridRow.PointId)] =
                    "输入点号",
                [nameof(CoordinateConversionGridRow.Longitude)] =
                    "经度（小数）",
                [nameof(CoordinateConversionGridRow.Latitude)] =
                    "纬度（小数）",
                [nameof(CoordinateConversionGridRow.LongitudeDms)] =
                    "经度（度分秒）",
                [nameof(CoordinateConversionGridRow.LatitudeDms)] =
                    "纬度（度分秒）",
                [nameof(CoordinateConversionGridRow.X)] =
                    "X",
                [nameof(CoordinateConversionGridRow.Y)] =
                    "Y"
            });
    }

    private void InvalidateMeasurement()
    {
        if (_measurementPoints.Length > 0)
            SetParameterSummary("输入或参数已改变，面积、重心及转换结果需重新计算。");
        _measurementPoints = [];
        _measurementInvalidReason = null;
        ClearMeasurement();
        ClearBoundaryMeasurement();
    }

    private TabPage BuildBoundaryMeasurementTab()
    {
        var tab = new TabPage("工区形态") { Padding = new Padding(6) };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 150));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        var importBoundary = UiComponents.Button("导入完整边界 KML（可拖入）", (_, _) =>
        {
            using var dialog = new OpenFileDialog { Title = "选择完整工区边界（含全部区域及孔洞）", Filter = "KML 边界|*.kml" };
            FileInteractions.For(this, "coordinate-boundary").Prepare(dialog);
            if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;
            LoadBoundaryMeasurement(dialog.FileName);
        }, UiButtonRole.Primary);
        importBoundary.AccessibleDescription = "可拖入一个KML边界文件，与点击选择使用同一测量流程。";
        FileDropSupport.EnableSingle(importBoundary, LoadBoundaryMeasurement,
            path => FileDropSupport.HasExtension(path, ".kml"),
            onError: message => _boundaryDetails.AppendText("\r\n未导入：" + message));
        toolbar.Controls.Add(importBoundary);
        toolbar.Controls.Add(_locateBoundaryCentroid);
        _locateBoundaryCentroid.Click += (_, _) => _boundaryPreview.LocateCentroid();
        var show = new CheckBox { Text = "显示面积重心", Checked = true, AutoSize = true, Margin = new Padding(8) };
        show.CheckedChanged += (_, _) => _boundaryPreview.ShowCentroid(show.Checked);
        _locateBoundaryCentroid.Click += (_, _) => show.Checked = true;
        toolbar.Controls.Add(show);
        toolbar.Controls.Add(UiComponents.Button("清除边界测量", (_, _) => ClearBoundaryMeasurement(), UiButtonRole.Subtle));
        layout.Controls.Add(toolbar, 0, 0);
        _boundaryDetails.AccessibleName = "完整边界测量来源与投影结果";
        layout.Controls.Add(_boundaryDetails, 0, 1);
        layout.Controls.Add(_boundaryPreview, 0, 2);
        tab.Controls.Add(layout);
        return tab;
    }

    private void ClearBoundaryMeasurement()
    {
        _boundaryPreview.SetMeasurement(null, null);
        _locateBoundaryCentroid.Enabled = false;
        _boundaryDetails.Text = "导入你确认包含完整工区的 Polygon KML，使用坐标转换页当前椭球、明确的中央经线和假东/北参数计算投影面积重心。多区域按面积加权，孔洞扣除；凹面、孔洞或多区域的重心可能位于有效范围外。\r\n仅供形态测量，不设为当前工区、不修改报告面积或生产数据；边界完整性仍由用户确认。输入、参数或工区变化后需重新导入。";
    }

    internal void LoadBoundaryMeasurement(string path)
    {
        try
        {
            double ReadNumber(TextBox input, string label)
            {
                if (!double.TryParse(input.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value))
                    throw new InvalidDataException($"请在坐标转换页填写明确、有效的{label}后重新导入边界。");
                return value;
            }
            var projection = ReadProjectionParameters(ReadNumber(_coordCentralMeridian, "中央经线"));
            var measurement = SpatialBoundaryMeasurementService.MeasureKml(path, projection, requireCompleteRings: true);
            _boundaryPreview.SetMeasurement(measurement, new { Source = Path.GetFullPath(path), projection, measurement.Vertices });
            _locateBoundaryCentroid.Enabled = true;
            FileInteractions.For(this, "coordinate-boundary").InputSucceeded(path);
            _boundaryDetails.Text = FormattableString.Invariant($"来源：{Path.GetFullPath(path)}\r\n{projection.Datum}；中央经线 {projection.CentralMeridian:0.######}°；假东/北 {projection.FalseEasting:0.###} / {projection.FalseNorthing:0.###} m；E=东 / N=北\r\n区域 {measurement.Vertices.Select(v => v.Polygon).Distinct().Count()} 个；孔洞 {measurement.Vertices.Where(v => v.Ring > 0).Select(v => (v.Polygon, v.Ring)).Distinct().Count()} 个；净投影面积 {measurement.AreaSquareMetres:0.###} m²\r\n面积重心 E={measurement.CentroidEasting:0.###} m；N={measurement.CentroidNorthing:0.###} m\r\n外环加权、孔洞扣除；重心可能位于有效范围外。按本次导入快照测量，外部文件更新后请重新导入；不写工区、报告或生产数据。滚轮缩放、左/中键拖动、Home 全图。");
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or FormatException or System.Xml.XmlException)
        {
            _boundaryDetails.AppendText("\r\n本次边界测量未采用：" + ex.Message + "；已保留此前测量。");
        }
    }

    private void ClearMeasurement()
    {
        _measurementA = _measurementB = -1;
        _coordPreview.SetMeasurementEndpoints(-1, -1);
        _measureA.Enabled = _measureB.Enabled = _measurementPoints.Length > 0;
        _coordMeasurement.Text = _measurementPoints.Length == 0
            ? _measurementInvalidReason ?? "两点测量：请先转换当前输入；输入、参数或工区变化后需重新转换。"
            : "两点测量：单击预览或通过上一点 / 下一点选点，再分别设为 A、B。跨度使用本次转换的投影坐标（米）；仅测量，不修改工区。";
    }

    private void SetMeasurementEndpoint(bool start)
    {
        int index = _coordPreview.SelectedIndex;
        if (index < 0 || index >= _measurementPoints.Length)
        {
            _coordMeasurement.Text = "请先在预览中选中一个点，再设为 A 或 B。";
            return;
        }
        if (start) _measurementA = index;
        else _measurementB = index;
        _coordPreview.SetMeasurementEndpoints(_measurementA, _measurementB);
        string PointLabel(int i) => i < 0 ? "未选择" : $"序号 {i + 1}（点号 {_measurementPoints[i].PointId ?? "未命名"}）";
        string text = $"A：{PointLabel(_measurementA)} → B：{PointLabel(_measurementB)}";
        if (_measurementA >= 0 && _measurementB >= 0)
        {
            var a = _measurementPoints[_measurementA];
            var b = _measurementPoints[_measurementB];
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double de = b.Easting - a.Easting, dn = b.Northing - a.Northing;
            text += FormattableString.Invariant($"\r\n{a.Datum}；中央经线 {a.CentralMeridian:0.######}°；{a.AxisOrderLabel}\r\nΔX = {dx:0.###} m；ΔY = {dy:0.###} m；|ΔX| = {Math.Abs(dx):0.###} m；|ΔY| = {Math.Abs(dy):0.###} m\r\nΔE = {de:0.###} m；ΔN = {dn:0.###} m；平面距离 = {Math.Sqrt(de * de + dn * dn):0.###} m");
            text += "\r\n按转换快照的未舍入投影坐标计算，不含带号前缀；仅测量，不修改工区。";
        }
        _coordMeasurement.Text = text;
    }

    internal void ShowRegistryRows(
        IReadOnlyList<CoordinateRegistryGridRow> rows)
    {
        _coordinateRegistryGrid.DataSource =
            null;
        _coordinateRegistryGrid.DataSource =
            rows.ToList();

        GridHeaders.Apply(
            _coordinateRegistryGrid,
            new Dictionary<string, string>
            {
                [nameof(CoordinateRegistryGridRow.CoordinateId)] =
                    "坐标 ID",
                [nameof(CoordinateRegistryGridRow.Longitude)] =
                    "经度",
                [nameof(CoordinateRegistryGridRow.Latitude)] =
                    "纬度",
                [nameof(CoordinateRegistryGridRow.Z)] =
                    "高程/高度",
                [nameof(CoordinateRegistryGridRow.HeightType)] =
                    "高度类型",
                [nameof(CoordinateRegistryGridRow.Crs)] =
                    "源坐标系",
                [nameof(CoordinateRegistryGridRow.Source)] =
                    "来源"
            });
    }

    internal CoordinateKmlTemplateChoice GetKmlChoice()
    {
        if (_coordKmlBuiltInSky.Checked)
            return CoordinateKmlTemplateChoice.BuiltInSky;
        if (_coordKmlBuiltInRedYellow.Checked)
            return CoordinateKmlTemplateChoice.BuiltInRedYellow;
        if (_coordKmlUploaded.Checked)
            return CoordinateKmlTemplateChoice.Uploaded;
        return CoordinateKmlTemplateChoice.Custom;
    }

    internal void SetKmlChoice(
        CoordinateKmlTemplateChoice choice)
    {
        _suppressKmlChoiceNotification =
            true;
        try
        {
            _coordKmlBuiltInSky.Checked =
                choice ==
                CoordinateKmlTemplateChoice.BuiltInSky;
            _coordKmlBuiltInRedYellow.Checked =
                choice ==
                CoordinateKmlTemplateChoice.BuiltInRedYellow;
            _coordKmlUploaded.Checked =
                choice ==
                CoordinateKmlTemplateChoice.Uploaded;
            _coordKmlCustom.Checked =
                choice ==
                CoordinateKmlTemplateChoice.Custom;
        }
        finally
        {
            _suppressKmlChoiceNotification =
                false;
        }
    }

    internal void SetKmlTemplateHint(
        string text)
    {
        _coordKmlTemplatePathLabel.Text =
            text;
    }
}
