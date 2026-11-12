using System.Text.Json;
using System.Text.Json.Nodes;
using MagOps.Infrastructure.Services;

namespace MagOps.Desktop;

/// <summary>Explicit calibration choices; reports never silently rewrite recognition thresholds.</summary>
internal sealed class TrackRecognitionThresholdDialog : Form
{
    private readonly Dictionary<(bool Track, string Property), NumericUpDown> _values = [];
    private readonly ComboBox _profile = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
    private readonly ComboBox _speed = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
    private readonly NumericUpDown _plannedSpeed = new() { Minimum = 0, Maximum = 100, DecimalPlaces = 1, Width = 75 };
    private bool _updating;
    public TrackSegmentationInput? Result { get; private set; }
    public TrackRecognitionThresholdDialog(TrackSegmentationInput input)
    {
        Text = "识别门槛：根据飞行评估调整"; Size = new Size(710, 780); MinimumSize = new Size(600, 500);
        StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false;
        var track = JsonSerializer.SerializeToNode(input.RecognitionOptions ?? new())!.AsObject();
        var match = JsonSerializer.SerializeToNode(input.MatchOptions ?? new())!.AsObject();
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1, Padding = new Padding(12) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(650, 0),
            Text = input.Enabled ? "先查看飞行评估，再调整并比较。更改后重新分析，旧归属与纳入决定失效。轴容差控制身份方向；直飞角度控制转弯切分。" : "设计测线模式：轨迹推断参数已禁用；可调整下方设计匹配通道、搜索和质量辅助门槛。更改后重新分析，旧纳入决定失效。" }, 0, 0);
        var profileRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        profileRow.Controls.Add(new Label { Text = "参数组", AutoSize = true, Padding = new Padding(0, 7, 4, 0) });
        _profile.Items.AddRange(TrackSegmentationParameterProfiles.Names.ToArray());
        IReadOnlyDictionary<string, TrackSegmentationInput> savedProfiles;
        try { savedProfiles = SegmentationParameterStore.LoadLibrary(); }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException)
        { savedProfiles = new Dictionary<string, TrackSegmentationInput>(); MessageBox.Show(this, "参数组库未加载：" + ex.Message, "参数组", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        _profile.Items.AddRange(savedProfiles.Keys.ToArray());
        _profile.SelectedItem = _profile.Items.Contains(input.ParameterProfile) ? input.ParameterProfile : "自定义";
        profileRow.Controls.Add(_profile);
        _speed.Items.AddRange(["保留速度设置", "8 m/s", "12 m/s", "自定义速度"]); _speed.SelectedIndex = 0;
        _plannedSpeed.Value = (decimal)(input.PlannedSpeedMps ?? 0);
        profileRow.Controls.Add(_speed); profileRow.Controls.Add(new Label { Text = "计划m/s（0=未指定）", AutoSize = true, Padding = new Padding(0, 7, 0, 0) }); profileRow.Controls.Add(_plannedSpeed);
        profileRow.Controls.Add(new Label { Text = "切换后仍需点击应用并重新分析；实际数值会保存到评估快照。", AutoSize = true, Padding = new Padding(8, 7, 0, 0) });
        root.Controls.Add(profileRow, 0, 1);
        var rows = new TableLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, ColumnCount = 2, Padding = new Padding(0, 10, 15, 10) };
        rows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 75)); rows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        root.Controls.Add(rows, 0, 2);
        void Add(bool isTrack, string property, string label, decimal minimum, decimal maximum, int decimals = 2)
        {
            int row = rows.RowCount++; rows.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var field = new NumericUpDown { Minimum = minimum, Maximum = maximum, DecimalPlaces = decimals,
                Value = (decimal)(isTrack ? track : match)[property]!.GetValue<double>(), Enabled = !isTrack || input.Enabled, Dock = DockStyle.Fill, Margin = new Padding(4, 5, 4, 5) };
            rows.Controls.Add(new Label { Text = label, AutoSize = true, Padding = new Padding(0, 8, 0, 0) }, 0, row);
            rows.Controls.Add(field, 1, row); _values[(isTrack, property)] = field;
            field.ValueChanged += (_, _) => { if (!_updating) _profile.SelectedItem = "自定义"; };
        }
        void Group(string title)
        {
            int row = rows.RowCount++; rows.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var label = new Label { Text = title, AutoSize = true, Padding = new Padding(0, 14, 0, 2) };
            rows.Controls.Add(label, 0, row); rows.SetColumnSpan(label, 2);
        }
        Group("整段几何补识别：宽范围召回与邻线竞争");
        var geometryRecovery = new CheckBox { AutoSize = true, Checked = (input.MatchOptions ?? new()).EnableGeometryRecovery,
            Text = "启用整段几何补识别（唯一对应直接获得身份，质量仍需核验）" };
        int geometryRow = rows.RowCount++; rows.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        rows.Controls.Add(geometryRecovery, 0, geometryRow); rows.SetColumnSpan(geometryRecovery, 2);
        rows.SizeChanged += (_, _) => geometryRecovery.MaximumSize = new Size(Math.Max(120, rows.ClientSize.Width - 24), 0);
        geometryRecovery.CheckedChanged += (_, _) => { if (!_updating) _profile.SelectedItem = "自定义"; };
        Add(false, nameof(WholeLegMatchOptions.GeometryRecoverySearchM), "补识别最大横距（米）", .1m, 1000);
        Add(false, nameof(WholeLegMatchOptions.GeometryRecoveryMinimumSpanM), "补识别最短跨度（米）", 1, 10000);
        Add(false, nameof(WholeLegMatchOptions.GeometryRecoveryMaximumAxisDegrees), "补识别最大轴差（度）", .1m, 89.9m);
        Add(false, nameof(WholeLegMatchOptions.GeometryRecoveryCompetitionGapM), "首选与邻线最小距离优势（米）", .1m, 1000);
        Group("方向识别与切分");
        Add(true, nameof(TrackRecognitionOptions.AxisToleranceDegrees), "测线轴方向容差（度）", .1m, 89.9m);
        Add(true, nameof(TrackRecognitionOptions.StraightHeadingToleranceDegrees), "直飞方向 / 转角切分门槛（度）", .1m, 89.9m);
        Add(true, nameof(TrackRecognitionOptions.MinimumSpanM), "最短作业候选跨度（米）", 1, 10000);
        Add(true, nameof(TrackRecognitionOptions.MinimumDurationSeconds), "最短作业候选时间（秒）", .1m, 300);
        Add(true, nameof(TrackRecognitionOptions.MinimumAlignedRatio), "方向一致的最低比例（0–1）", .01m, 1);
        Add(true, nameof(TrackRecognitionOptions.RepeatAxisToleranceDegrees), "重复航段轴差（度）", .1m, 89.9m);
        Add(true, nameof(TrackRecognitionOptions.RepeatCrossTrackToleranceM), "重复身份横距（米；放宽可能合并相邻线）", .1m, 100);
        Add(true, nameof(TrackRecognitionOptions.ExpectedLineSpacingM), "预期测线间距（米；0=不使用，仅作辅助）", 0, 5000, 1);
        Add(true, nameof(TrackRecognitionOptions.TurnConfirmationSeconds), "转向持续确认（秒）", .1m, 30);
        Add(true, nameof(TrackRecognitionOptions.DirectionWindowSeconds), "方向测量窗口（秒）", .1m, 8);
        Add(false, nameof(WholeLegMatchOptions.CoreM), "质量核心偏航通道（米）", .1m, 100);
        Add(false, nameof(WholeLegMatchOptions.CorridorM), "偏航容错通道（米）", .1m, 100);
        Add(false, nameof(WholeLegMatchOptions.EndpointSearchM), "参考端点搜索半径（米）", .1m, 100);
        Add(false, nameof(WholeLegMatchOptions.CandidateSearchM), "参考主体候选搜索（米）", .1m, 200);
        Group("飞行速度与升降辅助：质量范围不改变切分尺度");
        Add(false, nameof(WholeLegMatchOptions.MinimumFlightSpeedMps), "最低可信飞行速度（米/秒；质量辅助）", .1m, 100);
        Add(false, nameof(WholeLegMatchOptions.MaximumFlightSpeedMps), "最高可信飞行速度（米/秒；质量辅助）", .1m, 100);
        Add(false, nameof(WholeLegMatchOptions.VerticalSpeedReviewMps), "垂直升降提示速度（米/秒）", .1m, 100);
        Add(false, nameof(WholeLegMatchOptions.VerticalMotionHorizontalSpeedMps), "升降状态的水平速度上限（米/秒；影响切分）", .1m, 100);
        Add(false, nameof(WholeLegMatchOptions.TerminalSearchSpeedMps), "末端搜索速度尺度（米/秒；影响搜索半径）", .1m, 100);
        Add(false, nameof(WholeLegMatchOptions.DepartureAuxiliaryMinimumSpeedMps), "离开测线的辅助速度下限（米/秒；需其他证据）", .1m, 100);
        Add(false, nameof(WholeLegMatchOptions.DepartureAuxiliaryMaximumSpeedMps), "离开测线的辅助速度上限（米/秒；需其他证据）", .1m, 100);
        Group("端点停留与升降：组合证据，不单凭磁值切分");
        Add(false, nameof(WholeLegMatchOptions.TerminalStationaryRadiusM), "端点停留水平半径（米）", .1m, 20);
        Add(false, nameof(WholeLegMatchOptions.TerminalStationarySeconds), "端点停留持续时间（秒）", .1m, 300);
        Add(false, nameof(WholeLegMatchOptions.TerminalVerticalChangeM), "停留期间连续高度变化（米）", .1m, 100);
        TrackSegmentationInput Capture(bool validate = true)
        {
            match[nameof(WholeLegMatchOptions.EnableGeometryRecovery)] = geometryRecovery.Checked;
            foreach (var (key, field) in _values) (key.Track ? track : match)[key.Property] = (double)field.Value;
            var current = input with { RecognitionOptions = track.Deserialize<TrackRecognitionOptions>()!, MatchOptions = match.Deserialize<WholeLegMatchOptions>()!,
                PlannedSpeedMps = _plannedSpeed.Value == 0 ? null : (double)_plannedSpeed.Value,
                ParameterProfile = _profile.SelectedItem is string profile ? profile : "自定义", ParameterProfileVersion = 1 };
            if (validate) SegmentationParameterStore.Validate(current); return current;
        }
        void SetValues(TrackSegmentationInput applied, bool includeSpeed = false)
        {
            _updating = true;
            try
            {
                geometryRecovery.Checked = applied.MatchOptions.EnableGeometryRecovery;
                foreach (var ((trackOption, property), field) in _values)
                { object source = trackOption ? applied.RecognitionOptions : applied.MatchOptions;
                    field.Value = Math.Clamp(Convert.ToDecimal(source.GetType().GetProperty(property)!.GetValue(source)), field.Minimum, field.Maximum); }
                if (includeSpeed) _plannedSpeed.Value = (decimal)(applied.PlannedSpeedMps ?? 0);
            }
            finally { _updating = false; }
        }
        _profile.SelectedIndexChanged += (_, _) =>
        {
            if (_profile.SelectedItem is not string name || name == "自定义") return;
            var current = Capture(false);
            if (savedProfiles.TryGetValue(name, out var saved))
                SetValues(current with { RecognitionOptions = saved.RecognitionOptions, MatchOptions = saved.MatchOptions, PlannedSpeedMps = saved.PlannedSpeedMps }, true);
            else SetValues(TrackSegmentationParameterProfiles.Apply(name, current));
        };
        _speed.SelectedIndexChanged += (_, _) =>
        {
            if (_speed.SelectedIndex is 1 or 2) SetValues(TrackSegmentationParameterProfiles.ApplySpeed(_speed.SelectedIndex == 1 ? 8 : 12, Capture(false)), true);
        };
        _plannedSpeed.ValueChanged += (_, _) => { if (!_updating) { _speed.SelectedIndex = 3; _profile.SelectedItem = "自定义"; } };
        var nameBox = new TextBox { Width = 140, PlaceholderText = "新参数组名称" };
        profileRow.Controls.Add(nameBox);
        profileRow.Controls.Add(UiComponents.Button("另存参数组", (_, _) =>
        {
            try { SegmentationParameterStore.SaveLibrary(nameBox.Text, Capture()); savedProfiles = SegmentationParameterStore.LoadLibrary();
                string name = nameBox.Text.Trim(); _profile.Items.Add(name); _profile.SelectedItem = name; }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
            { MessageBox.Show(this, ex.Message, "参数组未保存", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }));
        var apply = UiComponents.Button("应用门槛", (_, _) =>
        {
            foreach (var (key, field) in _values) (key.Track ? track : match)[key.Property] = (double)field.Value;
            try
            {
                var recognition = track.Deserialize<TrackRecognitionOptions>()!; recognition.Validate();
                var matching = match.Deserialize<WholeLegMatchOptions>()!;
                if (matching.MinimumFlightSpeedMps >= matching.MaximumFlightSpeedMps || matching.CoreM > matching.CorridorM || matching.CandidateSearchM < matching.CorridorM)
                    throw new InvalidDataException("最低速度应小于最高速度；核心通道不应超过容错通道；候选搜索应覆盖容错通道。");
                Result = Capture(); DialogResult = DialogResult.OK; Close();
            }
            catch (InvalidDataException ex) { MessageBox.Show(this, ex.Message, "门槛不一致", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        });
        root.Controls.Add(apply, 0, 3); Controls.Add(root); UiTheme.Apply(this); UiLayout.FitFormToWorkingArea(this);
    }
}
