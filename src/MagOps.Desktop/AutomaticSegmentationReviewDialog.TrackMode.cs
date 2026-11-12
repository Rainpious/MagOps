using MagOps.Infrastructure.Services;

namespace MagOps.Desktop;

internal sealed partial class AutomaticSegmentationReviewDialog
{
    private bool ReverseNeedsReview(ReviewLegRow row) => row.Pass?.ExecutionDirection == "REVERSE" && !row.AxisOnlyDirection &&
        (!TrackMode || _tasks.FirstOrDefault(t => t.SurveyTaskStableId == row.Pass.SurveyTaskStableId)?.GeometryOrigin is not ("ACTUAL_TRACK" or "IMPORTED_REFERENCE"));

    private void RefreshEffectiveTaskList()
    {
        _loadingTaskChecks = true;
        try
        {
            _taskList.Rows.Clear();
            foreach (var task in _tasks)
            {
                var coverage = _audit?.RecognizedCoverage.FirstOrDefault(c => c.TaskId == task.SurveyTaskStableId);
                string suffix = coverage is null ? "" : $"  累计{coverage.Ratio:P0} / {coverage.PassCount}段 / 最大缺口{coverage.LargestGapM:0.#}m";
                int row = _taskList.Rows.Add(false, task.LineName + (task.SortieName == "实测推断" ? "（实测推断）" : "") + suffix);
                _taskList.Rows[row].Tag = new TaskChoice(task);
            }
        }
        finally { _loadingTaskChecks = false; }
    }

    private void RefreshTrackModeLabels()
    {
        void Visit(Control parent)
        {
            foreach (Control control in parent.Controls)
            {
                if (control is Button button)
                    button.Text = button.Text switch
                    {
                        "添加所选窗口的正向命中" when TrackMode => "添加所选窗口作业候选",
                        "批量添加正向命中" when TrackMode => "批量添加作业候选",
                        "添加所选窗口作业候选" when !TrackMode => "添加所选窗口的正向命中",
                        "批量添加作业候选" when !TrackMode => "批量添加正向命中",
                        _ => button.Text
                    };
                if (control is CheckBox check)
                    check.Text = check.Text switch { "显示设计线" when TrackMode => "显示参考/拟合线", "显示参考/拟合线" when !TrackMode => "显示设计线", _ => check.Text };
                Visit(control);
            }
        }
        Visit(this);
    }

    private void EditRecognitionMode()
    {
        using var dialog = new TrackSegmentationOptionsDialog(_displayAnalysis, _seedRequest.TrackSegmentation ?? new(), _referenceCentralMeridian, _spatialOnly);
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Result is null) return;
        if (TrackSegmentationReferenceService.Fingerprint(dialog.Result) == TrackSegmentationReferenceService.Fingerprint(_seedRequest.TrackSegmentation)) return;
        ClearReviewDecisions();
        _seedRequest = _seedRequest with { TrackSegmentation = dialog.Result,
            TrackSegmentationFingerprint = TrackSegmentationReferenceService.Fingerprint(dialog.Result) };
        SaveParameterDefaults();
        _tasks = _designTasks;
        foreach (var window in _windows) { window.TaskIds.Clear(); window.TaskScopeSource = "AUTO"; }
        RefreshEffectiveTaskList();
        AnalyzeExecution();
    }
    private void SaveParameterDefaults()
    {
        if (ParameterDefaultsPath is null || _seedRequest.TrackSegmentation is null) return;
        try { SegmentationParameterStore.SaveDefaults(ParameterDefaultsPath, _seedRequest.TrackSegmentation); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        { MessageBox.Show(this, "当前设置已应用，但工区默认参数未保存：" + ex.Message, "分线参数", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }
    private void EditWindowSpeed()
    {
        var selected = SelectedWindows(); if (selected.Length == 0) return;
        using var dialog = new Form { Text = "所选窗口计划速度", Size = new Size(430, 180), StartPosition = FormStartPosition.CenterParent, ShowInTaskbar = false };
        var value = new NumericUpDown { Minimum = 0, Maximum = 100, DecimalPlaces = 1, Value = 0, Width = 90 };
        var root = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12) };
        root.Controls.Add(new Label { AutoSize = true, Text = "8/12m/s使用对应质量档；0恢复项目默认。\n其他速度只记录计划值，保留项目质量上下限。" });
        root.Controls.Add(value);
        root.Controls.Add(UiComponents.Button("应用并重新分析", (_, _) => { dialog.DialogResult = DialogResult.OK; dialog.Close(); }));
        dialog.Controls.Add(root); UiTheme.Apply(dialog);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var input = _seedRequest.TrackSegmentation ?? new();
        var overrides = new Dictionary<string, double>(input.WindowPlannedSpeeds);
        foreach (var window in selected)
            if (value.Value == 0) overrides.Remove(window.Id); else overrides[window.Id] = (double)value.Value;
        input = input with { WindowPlannedSpeeds = overrides };
        ClearReviewDecisions();
        _seedRequest = _seedRequest with { TrackSegmentation = input, TrackSegmentationFingerprint = TrackSegmentationReferenceService.Fingerprint(input) };
        AnalyzeExecution();
    }
    private void EditTakeoffPositions()
    {
        using var dialog = new Form { Text = "已知起降点（经度,纬度）", Size = new Size(500, 340), StartPosition = FormStartPosition.CenterParent, ShowInTaskbar = false };
        var input = _seedRequest.TrackSegmentation ?? new();
        var box = new TextBox { Multiline = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical,
            Text = string.Join(Environment.NewLine, input.KnownTakeoffPositions.Select(p =>
                p.Longitude.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "," + p.Latitude.ToString("R", System.Globalization.CultureInfo.InvariantCulture))) };
        var apply = UiComponents.Button("保存并重新分析", (_, _) =>
        {
            try
            {
                var coordinates = box.Lines.Where(l => !string.IsNullOrWhiteSpace(l)).Select(l =>
                { var pair = l.Split([',', '，', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                    if (pair.Length != 2) throw new InvalidDataException("每行填写经度,纬度。");
                    double lon = double.Parse(pair[0], System.Globalization.CultureInfo.InvariantCulture), lat = double.Parse(pair[1], System.Globalization.CultureInfo.InvariantCulture);
                    if (!WorkareaSpatialService.ValidCoordinate(lon, lat)) throw new InvalidDataException("经纬度无效。");
                    return new GeoCoordinate(lon, lat); }).Distinct().ToArray();
                input = input with { KnownTakeoffPositions = coordinates }; dialog.DialogResult = DialogResult.OK; dialog.Close();
            }
            catch (Exception ex) when (ex is FormatException or InvalidDataException or OverflowException)
            { MessageBox.Show(dialog, ex.Message, "起降点未保存", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        });
        apply.Dock = DockStyle.Bottom; dialog.Controls.Add(box); dialog.Controls.Add(apply); UiTheme.Apply(dialog);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        ClearReviewDecisions(); _seedRequest = _seedRequest with { TrackSegmentation = input, TrackSegmentationFingerprint = TrackSegmentationReferenceService.Fingerprint(input) };
        SaveParameterDefaults(); AnalyzeExecution();
    }
}

internal sealed class TrackSegmentationOptionsDialog : Form
{
    private readonly FieldMagFolderAnalysis _analysis;
    private TrackSegmentationInput _input;
    private readonly ComboBox _mode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 290 };
    private readonly CheckBox _manualAxis = new() { Text = "指定测线轴方向（双向有效）", AutoSize = true };
    private readonly NumericUpDown _axis = new() { Minimum = 0, Maximum = 179.99m, DecimalPlaces = 2, Width = 80 };
    private readonly NumericUpDown _axisTolerance = new() { Minimum = .1m, Maximum = 89.9m, DecimalPlaces = 1, Width = 70 };
    private readonly NumericUpDown _meridian = new() { Minimum = -180, Maximum = 180, DecimalPlaces = 2, Width = 80 };
    private readonly CheckBox _hasMeridian = new() { Text = "投影 CSV 中央经线", AutoSize = true };
    private readonly DataGridView _grid = GridFactory.Create();
    private readonly TrackMapPanel _map = new() { Dock = DockStyle.Fill };
    private readonly Label _source = new() { AutoSize = true };
    private readonly bool _requireEvidence;
    private readonly NumericUpDown _spacing = new() { Minimum = 0, Maximum = 5000, DecimalPlaces = 1, Width = 90 };
    private readonly Label _basisStatus = new() { AutoSize = true, ForeColor = Color.DarkRed };
    private readonly TextBox _sourceFiles = new()
    {
        Dock = DockStyle.Fill, Multiline = true, ReadOnly = true,
        ScrollBars = ScrollBars.Both, WordWrap = false
    };
    private IReadOnlyList<PlannedSurveyLineTask> _previewTasks = [];
    public TrackSegmentationInput? Result { get; private set; }

    public TrackSegmentationOptionsDialog(FieldMagFolderAnalysis analysis, TrackSegmentationInput input, double? centralMeridian)
        : this(analysis, input, centralMeridian, false) { }

    public TrackSegmentationOptionsDialog(FieldMagFolderAnalysis analysis, TrackSegmentationInput input, double? centralMeridian, bool requireEvidence)
    {
        _requireEvidence = requireEvidence;
        _analysis = analysis; _input = input;
        Text = "分线模式与参考航线"; StartPosition = FormStartPosition.CenterParent;
        Size = new Size(1180, 760); MinimumSize = new Size(920, 620); ShowInTaskbar = false;
        _mode.Items.AddRange(["设计测线匹配（现有模式）", "实测轨迹（KML / CSV 可选）", "设计匹配 + 实测轨迹补漏"]);
        _mode.SelectedIndex = input.Enabled ? input.UseDesignTasks ? 2 : 1 : 0;
        if (requireEvidence) { _mode.SelectedIndex = 1; _mode.Enabled = false; _mode.Items[1] = "实测轨迹（KML / 航线参数至少一项）"; }
        _manualAxis.Checked = input.AxisDegrees.HasValue; _axis.Value = (decimal)(input.AxisDegrees ?? 0);
        _axis.Enabled = _manualAxis.Checked; _manualAxis.CheckedChanged += (_, _) => _axis.Enabled = _manualAxis.Checked;
        _axisTolerance.Value = (decimal)(_input.RecognitionOptions?.AxisToleranceDegrees ?? 12);
        _spacing.Value = (decimal)input.RecognitionOptions.ExpectedLineSpacingM;
        _hasMeridian.Checked = centralMeridian.HasValue; _meridian.Value = (decimal)(centralMeridian ?? 0);
        _meridian.Enabled = _hasMeridian.Checked; _hasMeridian.CheckedChanged += (_, _) => _meridian.Enabled = _hasMeridian.Checked;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 6, ColumnCount = 1, Padding = new Padding(10) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 80));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var options = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        options.Controls.AddRange([_mode, _manualAxis, _axis, new Label { Text = "轴容差±°", AutoSize = true, Padding = new Padding(0, 8, 0, 0) },
            _axisTolerance, new Label { Text = "北=0°，东=90°", AutoSize = true, Padding = new Padding(0, 8, 0, 0) }]);
        options.Controls.Add(UiComponents.Button("识别门槛…", (_, _) =>
        {
            _input = _input with { Enabled = _mode.SelectedIndex != 0, UseDesignTasks = _mode.SelectedIndex == 2,
                ParameterProfile = (_input.RecognitionOptions ?? new()).AxisToleranceDegrees == (double)_axisTolerance.Value ? _input.ParameterProfile : "自定义",
                RecognitionOptions = (_input.RecognitionOptions ?? new()) with { AxisToleranceDegrees = (double)_axisTolerance.Value } };
            using var thresholds = new TrackRecognitionThresholdDialog(_input);
            if (thresholds.ShowDialog(this) == DialogResult.OK && thresholds.Result is { } result)
            { _input = result; _axisTolerance.Value = (decimal)result.RecognitionOptions.AxisToleranceDegrees; }
        }));
        root.Controls.Add(options, 0, 0);
        options.Controls.Add(new Label { Text = "线距m（0=待定）", AutoSize = true, Padding = new Padding(0, 8, 0, 0) });
        options.Controls.Add(_spacing);
        SurveySpacingEstimate? estimate = null;
        var adopt = UiComponents.Button("采用候选线距", (_, _) =>
        { if (estimate?.SpacingM is { } value) _spacing.Value = (decimal)value; });
        adopt.Enabled = false;
        options.Controls.Add(UiComponents.Button("测算线距", (_, _) =>
        {
            if (!_manualAxis.Checked) { _basisStatus.Text = "请先指定测线轴方向，再测算该方向的线距。"; return; }
            var parameters = CurrentParameters();
            var audit = BusyOperationDialog.Run(this, "测算线距", "正在寻找方向一致、沿线范围重叠的测线组…", () =>
                new AuditProductionService().Analyze(_analysis, [], new AuditProductionRequest(_analysis.Fingerprint, [], [], [], true)
                    { TrackSegmentation = parameters }));
            estimate = IntermediateSegmentationEvidence.Estimate(audit.EffectiveTasks ?? [], (double)_axis.Value);
            adopt.Enabled = estimate.SpacingM.HasValue;
            _basisStatus.Text = $"线距候选：{estimate.SpacingM?.ToString("0.0") ?? "未形成"} m；" +
                $"一致线对 {estimate.SupportingPairs}/{estimate.PairCount}；中位离差 {estimate.MedianDeviationM:0.0} m。{estimate.Message}";
        }));
        options.Controls.Add(adopt);
        options.Controls.Add(_basisStatus);
        _basisStatus.Text = requireEvidence ? "必须提供有效测线 KML 或明确轴方向；两项同时填写时须方向一致。" : "线距测算值需要人工确认后采用。";
        _axis.ValueChanged += (_, _) => { estimate = null; adopt.Enabled = false; PreviewAxis(); };
        _manualAxis.CheckedChanged += (_, _) => { estimate = null; adopt.Enabled = false; PreviewAxis(); };
        void UpdateModeControls()
        {
            bool track = _mode.SelectedIndex != 0;
            _manualAxis.Enabled = track; _axis.Enabled = track && _manualAxis.Checked; _axisTolerance.Enabled = track;
        }
        _mode.SelectedIndexChanged += (_, _) => UpdateModeControls();
        _manualAxis.CheckedChanged += (_, _) => UpdateModeControls();
        UpdateModeControls();
        var imports = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        imports.Controls.Add(UiComponents.Button("导入参考 KML / 测线 CSV", (_, _) => Import()));
        imports.Controls.Add(UiComponents.Button("清空参考", (_, _) => { _input = _input with { ReferenceGeometry = [], ReferenceSource = "", ReferenceFileHash = "", ReferenceAuthority = "ASSISTED" }; Populate(); }));
        imports.Controls.AddRange([_hasMeridian, _meridian]); root.Controls.Add(imports, 0, 1);
        _source.Dock = DockStyle.Fill;
        root.SizeChanged += (_, _) => _source.MaximumSize = new Size(
            Math.Max(1, root.ClientSize.Width - root.Padding.Horizontal - _source.Margin.Horizontal), 0);
        root.Controls.Add(_source, 0, 2);
        _sourceFiles.AccessibleName = "参考文件路径（可滚动和复制）";
        root.Controls.Add(_sourceFiles, 0, 3);
        var split = new SplitContainer { Dock = DockStyle.Fill, Size = new Size(1100, 450), SplitterDistance = 650 };
        split.SizeChanged += (_, _) => { if (split.Width > 530) split.SplitterDistance = Math.Clamp((int)(split.Width * .60), 300, split.Width - 224); };
        _grid.Dock = DockStyle.Fill; _grid.ReadOnly = false; _grid.AllowUserToAddRows = false; _grid.AllowUserToDeleteRows = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect; _grid.MultiSelect = true;
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Name", HeaderText = "参考片段", ReadOnly = true, Width = 160 });
        _grid.Columns.Add(new DataGridViewComboBoxColumn { Name = "Kind", HeaderText = "类别", DataSource = new[] { "测线", "联络测线", "连接线", "待复核" }, Width = 105 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Length", HeaderText = "长度 m", ReadOnly = true, Width = 85 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Heading", HeaderText = "方向 °", ReadOnly = true, Width = 80 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Evidence", HeaderText = "依据", ReadOnly = true, Width = 280 });
        _grid.DataError += (_, e) => e.ThrowException = false;
        _grid.SelectionChanged += (_, _) =>
        {
            string? id = _grid.SelectedRows.Cast<DataGridViewRow>().Select(r => r.Tag).OfType<SegmentationReferenceGeometry>().FirstOrDefault()?.Id;
            _map.SetData(_analysis, _previewTasks, null, null, id);
        };
        split.Panel1.Controls.Add(_grid); split.Panel2.Controls.Add(_map); root.Controls.Add(split, 0, 4);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        foreach (string label in new[] { "测线", "联络测线", "连接线", "待复核" })
            actions.Controls.Add(UiComponents.Button("选中设为" + label, (_, _) =>
            { foreach (DataGridViewRow row in _grid.SelectedRows) row.Cells["Kind"].Value = label; }));
        actions.Controls.Add(UiComponents.Button("应用并重新分析", (_, _) =>
        {
            _grid.EndEdit(); ReadSnapshot();
            var candidate = CurrentParameters() with { ReferenceGeometry = TrackSegmentationReferenceService.Consolidate(_input.ReferenceGeometry) };
            if (_requireEvidence && IntermediateSegmentationEvidence.Problem(candidate) is { } error)
            { Result = null; _basisStatus.Text = error; return; }
            Result = candidate;
            DialogResult = DialogResult.OK; Close();
        }));
        root.Controls.Add(actions, 0, 5); Controls.Add(root);
        Populate(); UiTheme.Apply(this); UiLayout.FitFormToWorkingArea(this);
    }
    private TrackSegmentationInput CurrentParameters() => _input with
    {
        Enabled = _mode.SelectedIndex != 0, UseDesignTasks = _mode.SelectedIndex == 2,
        AxisDegrees = _manualAxis.Checked ? (double)_axis.Value : null,
        ParameterProfile = _input.RecognitionOptions.AxisToleranceDegrees == (double)_axisTolerance.Value &&
            _input.RecognitionOptions.ExpectedLineSpacingM == (double)_spacing.Value ? _input.ParameterProfile : "自定义",
        RecognitionOptions = _input.RecognitionOptions with { AxisToleranceDegrees = (double)_axisTolerance.Value,
            ExpectedLineSpacingM = (double)_spacing.Value }
    };
    private void PreviewAxis()
    {
        var preview = _previewTasks.ToList();
        var points = _analysis.UavSamples.Where(p => p.Longitude.HasValue && p.Latitude.HasValue).ToArray();
        if (_manualAxis.Checked && points.Length > 0)
        {
            double lon = (points.Min(p => p.Longitude!.Value) + points.Max(p => p.Longitude!.Value)) / 2;
            double lat = (points.Min(p => p.Latitude!.Value) + points.Max(p => p.Latitude!.Value)) / 2;
            double radians = (double)_axis.Value * Math.PI / 180;
            double half = Math.Max(100, Math.Max((points.Max(p => p.Longitude!.Value) - points.Min(p => p.Longitude!.Value)) * 111320 * Math.Cos(lat * Math.PI / 180),
                (points.Max(p => p.Latitude!.Value) - points.Min(p => p.Latitude!.Value)) * 111320) * .35);
            double dx = half * Math.Sin(radians) / (111320 * Math.Cos(lat * Math.PI / 180)), dy = half * Math.Cos(radians) / 111320;
            preview.Add(new PlannedSurveyLineTask(0, 0, "axis-preview", "方向预览", $"指定轴{_axis.Value}°（双向）",
                new(lon - dx, lat - dy), new(lon + dx, lat + dy), half * 2) { SurveyTaskStableId = "axis-preview", GeometryOrigin = "IMPORTED_REFERENCE" });
        }
        _map.SetData(_analysis, preview, null, null);
    }
    private static string Label(string kind) => kind switch { "SURVEY" => "测线", "TIE" => "联络测线", "CONNECTOR" => "连接线", _ => "待复核" };
    private static string Kind(string label) => label switch { "测线" => "SURVEY", "联络测线" => "TIE", "连接线" => "CONNECTOR", _ => "REVIEW" };
    private void ReadSnapshot() => _input = _input with { ReferenceGeometry = _grid.Rows.Cast<DataGridViewRow>()
        .Select(r => ((SegmentationReferenceGeometry)r.Tag!) with { Kind = Kind(Convert.ToString(r.Cells["Kind"].Value) ?? "") }).ToArray() };
    private void Populate()
    {
        _previewTasks = _input.ReferenceGeometry.Select((g, i) => new PlannedSurveyLineTask(i + 1, 0, g.Id, "参考", g.Name, g.Start, g.End,
            TrackSegmentationReferenceService.Distance(g.Start, g.End)) { SurveyTaskStableId = g.Id, GeometryOrigin = "IMPORTED_REFERENCE" }).ToArray();
        _grid.Rows.Clear();
        foreach (var g in _input.ReferenceGeometry)
        {
            int row = _grid.Rows.Add(g.Name, Label(g.Kind), TrackSegmentationReferenceService.Distance(g.Start, g.End).ToString("0"),
                SurveyDirection.Bearing(g.Start, g.End)?.ToString("0.#") ?? "待判断", g.Evidence);
            _grid.Rows[row].Tag = g;
        }
        _source.Text = $"参考片段 {_input.ReferenceGeometry.Count} 个。KML 含连接线，自动类别可用 Ctrl / Shift 多选修订。待复核片段不作为测线约束。\n" +
            (TrackSegmentationReferenceService.IsAuthoritative(_input) ? "实际KML提供测线身份；未命中轨迹保留核验，不自动新建测线。" : "无KML时可按实际轨迹推断测线。");
        _sourceFiles.Text = string.IsNullOrWhiteSpace(_input.ReferenceSource) ? "未导入参考文件。" :
            string.Join(Environment.NewLine, _input.ReferenceSource.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        PreviewAxis();
    }
    private void Import()
    {
        using var dialog = new OpenFileDialog { Filter = "参考航线|*.kml;*.csv;*.tsv;*.txt", Multiselect = true, CheckFileExists = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            _grid.EndEdit(); ReadSnapshot();
            var imported = dialog.FileNames.Select(p => TrackSegmentationReferenceService.Import(p, _hasMeridian.Checked ? (double)_meridian.Value : null)).ToArray();
            var geometry = TrackSegmentationReferenceService.Consolidate(
                _input.ReferenceGeometry.Concat(imported.SelectMany(i => i.ReferenceGeometry)));
            bool hasAuthoritativeKml = string.Equals(_input.ReferenceAuthority, "KML_AUTHORITATIVE", StringComparison.OrdinalIgnoreCase) ||
                imported.Any(i => string.Equals(i.ReferenceAuthority, "KML_AUTHORITATIVE", StringComparison.OrdinalIgnoreCase));
            _input = _input with { ReferenceGeometry = geometry, ReferenceSource = string.Join("; ", new[] { _input.ReferenceSource }.Concat(dialog.FileNames).Where(s => s.Length > 0)),
                ReferenceFileHash = string.Join(";", new[] { _input.ReferenceFileHash }.Concat(imported.Select(i => i.ReferenceFileHash)).Where(s => s.Length > 0)),
                ReferenceAuthority = hasAuthoritativeKml ? "KML_AUTHORITATIVE" : _input.ReferenceAuthority };
            if (_mode.SelectedIndex == 0) _mode.SelectedIndex = 1;
            Populate();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or InvalidDataException or System.Xml.XmlException)
        { MessageBox.Show(this, ex.Message, "参考航线导入失败", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }
}
