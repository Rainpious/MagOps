using MagOps.Infrastructure.Services;

namespace MagOps.Desktop;

internal sealed class FlightQualitySettingsDialog : Form
{
    private readonly NumericUpDown _spacing = Number(0, 100000, 2);
    private readonly NumericUpDown _height = Number(0, 100000, 2);
    private readonly NumericUpDown _tolerance = Number(0, 100, 1);
    private readonly CheckBox _agl = new() { Text = "激光高度已确认为有效离地垂高", AutoSize = true };
    private readonly CheckBox _static = new() { Text = "选定专门 GPS 静态试验（不自动使用低速飞行段）", AutoSize = true };
    private readonly ComboBox _source = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly DateTimePicker _start = TimePicker();
    private readonly DateTimePicker _end = TimePicker();
    private readonly string[] _files;

    internal FlightQualityAssessmentOptions? SelectedOptions { get; private set; }

    public FlightQualitySettingsDialog(FlightQualityAssessmentOptions options, IReadOnlyList<UavMagCanonicalSample> raw)
    {
        Text = "飞行评估设置";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(700, 620);
        Size = new Size(780, 660);
        AutoScaleMode = AutoScaleMode.Dpi;
        ShowInTaskbar = false;
        _files = raw.Select(s => s.SourceFile).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray();
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 2, RowCount = 10, AutoScroll = true };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 215));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
        for (int i = 1; i < 8; i++) root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        root.RowStyles[7].Height = 84;
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
        var intro = new Label { Dock = DockStyle.Fill, Text = "这些设置仅用于评估报告，不改动分线参数。测线距和设计飞高填 0 表示未知；不套用示例项目数值。静态试验从原始分析点读取，可包含被飞行筛选排除的静置点。" };
        root.Controls.Add(intro, 0, 0); root.SetColumnSpan(intro, 2);
        Add("本工区测线距（m）", _spacing, 1);
        Add("设计离地飞高（m）", _height, 2);
        Add("设计偏差提示比例（%）", _tolerance, 3);
        Add("离地高度依据", _agl, 4);
        Add("GPS 静态定位试验", _static, 5);
        Add("静态试验源文件", _source, 6);
        var timeRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
        timeRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 55));
        timeRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        timeRow.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        timeRow.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        _start.Dock = _end.Dock = DockStyle.Fill;
        timeRow.Controls.Add(new Label { Text = "开始", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        timeRow.Controls.Add(_start, 1, 0);
        timeRow.Controls.Add(new Label { Text = "结束", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 1);
        timeRow.Controls.Add(_end, 1, 1);
        Add("试验起止时间（UTC）", timeRow, 7);
        var note = new Label { Dock = DockStyle.Fill, Text = "示例偏航判据：平均 < 测线距/5，最大 < 测线距/3。\r\n设计飞高偏差提示与测高仪器精度是不同指标；这里的 10% 是可调整的设计偏差初值，不宣称验证测高仪器精度。\r\nGPS 静态结果是重复性标准差；没有已知基准时不能称为绝对定位准确度。零值和负值激光高度不计入有效离地高度。" };
        root.Controls.Add(note, 0, 8); root.SetColumnSpan(note, 2);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var apply = UiComponents.Button("应用并保存评估", (_, _) => Apply());
        var cancel = UiComponents.Button("取消", (_, _) => { DialogResult = DialogResult.Cancel; Close(); });
        actions.Controls.Add(apply); actions.Controls.Add(cancel);
        root.Controls.Add(actions, 0, 9); root.SetColumnSpan(actions, 2);
        Controls.Add(root); AcceptButton = apply; CancelButton = cancel;
        _spacing.Value = Clamp(options.LineSpacingM ?? 0, _spacing);
        _height.Value = Clamp(options.DesignedFlightHeightM ?? 0, _height);
        _tolerance.Value = Clamp(options.HeightToleranceRatio * 100, _tolerance);
        _agl.Checked = options.LaserAltitudeIsAboveGround;
        foreach (string file in _files) _source.Items.Add(file);
        if (_files.Length > 0) _source.SelectedIndex = Math.Max(0, Array.FindIndex(_files, f => options.StaticTestSourceFiles?.Contains(f, StringComparer.OrdinalIgnoreCase) == true));
        _start.Value = options.StaticTestStartUtc?.UtcDateTime ?? raw.FirstOrDefault()?.TimestampUtc.UtcDateTime ?? DateTime.UtcNow;
        _end.Value = options.StaticTestEndUtc?.UtcDateTime ?? _start.Value.AddMinutes(2);
        _static.Checked = options.StaticTestSourceFiles?.Count > 0 || options.StaticTestStartUtc is not null;
        _static.CheckedChanged += (_, _) => UpdateStaticEnabled();
        UpdateStaticEnabled();
        UiTheme.Apply(this); UiLayout.FitFormToWorkingArea(this);

        void Add(string text, Control control, int row)
        {
            root.Controls.Add(new Label { Text = text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row);
            root.Controls.Add(control, 1, row);
        }
    }

    private void Apply()
    {
        if (_static.Checked && (_source.SelectedIndex < 0 || _end.Value <= _start.Value))
        { MessageBox.Show(this, "请选择静态试验源文件，并填写正确的 UTC 起止时间。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        SelectedOptions = new(_spacing.Value == 0 ? null : (double)_spacing.Value,
            _height.Value == 0 ? null : (double)_height.Value, (double)_tolerance.Value / 100,
            _static.Checked ? [_files[_source.SelectedIndex]] : null,
            _static.Checked ? new DateTimeOffset(DateTime.SpecifyKind(_start.Value, DateTimeKind.Utc)) : null,
            _static.Checked ? new DateTimeOffset(DateTime.SpecifyKind(_end.Value, DateTimeKind.Utc)) : null,
            _agl.Checked);
        SelectedOptions.Validate(); DialogResult = DialogResult.OK; Close();
    }
    private void UpdateStaticEnabled() => _source.Enabled = _start.Enabled = _end.Enabled = _static.Checked;
    private static NumericUpDown Number(decimal min, decimal max, int digits) => new() { Minimum = min, Maximum = max, DecimalPlaces = digits, Width = 150 };
    private static decimal Clamp(double value, NumericUpDown control) => Math.Clamp((decimal)value, control.Minimum, control.Maximum);
    private static DateTimePicker TimePicker() => new() { Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd HH:mm:ss", Width = 210 };
}
