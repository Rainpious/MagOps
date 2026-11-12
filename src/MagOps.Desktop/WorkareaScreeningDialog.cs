using MagOps.Core.Domain;
using MagOps.Infrastructure.Services;

namespace MagOps.Desktop;

internal sealed class WorkareaScreeningDialog : Form
{
    private readonly FieldMagFolderAnalysis _analysis;
    private WorkareaScreeningInput _input;
    private readonly NumericUpDown _coarse = new() { DecimalPlaces = 1, Minimum = 0.1m, Maximum = 1000m, Width = 90 };
    private readonly NumericUpDown _buffer = new() { DecimalPlaces = 2, Minimum = 0, Maximum = 100m, Width = 90 };
    private readonly DataGridView _grid = GridFactory.Create();
    private readonly Label _summary = new() { AutoSize = true, Dock = DockStyle.Top };
    private readonly TimeZoneInfo _timeZone;
    private readonly HashSet<WorkareaExcludedSample> _excludedSamples;
    private WorkareaScreeningResult _screening;
    public WorkareaScreeningInput? ReviewedInput { get; private set; }

    public WorkareaScreeningDialog(FieldMagFolderAnalysis analysis, WorkareaScreeningInput input)
    {
        _analysis = analysis; _input = input;
        _timeZone = analysis.ProjectTimeZoneId == IntermediateCsvSegmentationService.TimeBasis
            ? IntermediateCsvSegmentationService.SourceClock
            : TimeZoneInfo.FindSystemTimeZoneById(analysis.ProjectTimeZoneId);
        _screening = new WorkareaSpatialService().Screen(analysis, input);
        bool current = input.SourceFingerprint == analysis.Fingerprint && input.ContextFingerprint == _screening.ContextFingerprint;
        if (!current) _input = input with { Decisions = [], ExcludedSamples = [] };
        _excludedSamples = _input.ExcludedSamples.ToHashSet();
        Text = "当前工区数据筛选 · 高级参数";
        Size = new Size(1100, 680); MinimumSize = new Size(850, 550);
        StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(12) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.SizeChanged += (_, _) => _summary.MaximumSize = new Size(Math.Max(200, root.ClientSize.Width - root.Padding.Horizontal - 12), 0);
        var parameters = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        _coarse.Value = (decimal)Math.Clamp(input.Options.CoarseDistanceM / 1000, 0.1, 1000);
        _buffer.Value = (decimal)Math.Clamp(input.Options.BufferM / 1000, 0, 100);
        parameters.Controls.Add(new Label { Text = "远距离粗筛（km）", AutoSize = true, Margin = new Padding(3, 8, 3, 3) });
        parameters.Controls.Add(_coarse);
        parameters.Controls.Add(new Label { Text = "矩形四边外扩（km）", AutoSize = true, Margin = new Padding(12, 8, 3, 3) });
        parameters.Controls.Add(_buffer);
        parameters.Controls.Add(UiComponents.Button("应用参数并重算", (_, _) => Recalculate()));
        root.Controls.Add(parameters, 0, 0); root.Controls.Add(_summary, 0, 1);
        _grid.Dock = DockStyle.Fill; _grid.AutoGenerateColumns = false; _grid.AllowUserToAddRows = false;
        _grid.ReadOnly = false;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        _grid.AllowUserToDeleteRows = false; _grid.RowHeadersVisible = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect; _grid.MultiSelect = true;
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Exclude", HeaderText = "排除", Width = 65, ReadOnly = false });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "File", HeaderText = "来源文件", Width = 190, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Time", HeaderText = "作业时间（项目时区）", Width = 330, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Count", HeaderText = "点数", Width = 70, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Status", HeaderText = "空间判断", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, ReadOnly = true });
        root.Controls.Add(_grid, 0, 2);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        actions.Controls.Add(UiComponents.Button("排除选中轨迹", (_, _) => SetSelected(true, false)));
        actions.Controls.Add(UiComponents.Button("恢复选中轨迹", (_, _) => SetSelected(false, false)));
        actions.Controls.Add(UiComponents.Button("排除同文件轨迹", (_, _) => SetSelected(true, true)));
        actions.Controls.Add(UiComponents.Button("恢复全部 / 包括单独排除的窗口和航段", (_, _) =>
        {
            _excludedSamples.Clear(); foreach (DataGridViewRow row in _grid.Rows) row.Cells["Exclude"].Value = false;
        }));
        var apply = UiComponents.Button("保存并继续", (_, _) =>
        {
            if (_input.Options != ReadOptions()) { Recalculate(); return; }
            _grid.EndEdit(); ReviewedInput = BuildInput(); DialogResult = DialogResult.OK; Close();
        }, UiButtonRole.Primary);
        actions.Controls.Add(apply);
        var cancel = UiComponents.Button("取消", (_, _) => Close()); actions.Controls.Add(cancel);
        CancelButton = cancel; root.Controls.Add(actions, 0, 3); Controls.Add(root);
        Populate(); UiTheme.Apply(this); UiLayout.FitFormToWorkingArea(this);
    }

    private WorkareaScreeningOptions ReadOptions() => new((double)_coarse.Value * 1000, (double)_buffer.Value * 1000);
    private WorkareaScreeningInput BuildInput() => _input with
    {
        SourceFingerprint = _analysis.Fingerprint, ContextFingerprint = _screening.ContextFingerprint,
        Decisions = _grid.Rows.Cast<DataGridViewRow>().Where(r => r.Tag is WorkareaScreeningGroup)
            .Select(r => new WorkareaScreeningDecision(((WorkareaScreeningGroup)r.Tag!).GroupId,
                r.Cells["Exclude"].Value is true ? "EXCLUDE" : "RESTORE", "用户核对当前工区数据选用")).ToArray(),
        ExcludedSamples = _excludedSamples.ToArray()
    };
    private void Recalculate()
    {
        _input = _input with { Options = ReadOptions(), Decisions = [], ExcludedSamples = [] };
        _excludedSamples.Clear(); _screening = new WorkareaSpatialService().Screen(_analysis, _input); Populate();
    }
    private void Populate()
    {
        _grid.Rows.Clear();
        foreach (var group in _screening.Groups)
        {
            var start = ProjectTime.Display(group.StartUtc, _timeZone);
            var end = ProjectTime.Display(group.EndUtc, _timeZone);
            string time = start.Date == end.Date ? $"{start:yyyy-MM-dd HH:mm:ss} → {end:HH:mm:ss}" :
                $"{start:yyyy-MM-dd HH:mm:ss} → {end:yyyy-MM-dd HH:mm:ss}";
            bool hasManualDecision = _input.Decisions.Any(d => d.GroupId == group.GroupId);
            string decisionLabel = hasManualDecision
                ? (_input.Decisions.Last(d => d.GroupId == group.GroupId).Decision == "EXCLUDE"
                    ? " · 人工排除"
                    : " · 人工恢复")
                : "";
            string autoLabel = group.AutomaticallyExcluded ? " · 自动排除，可恢复" : "";
            int row = _grid.Rows.Add(group.Excluded, Path.GetFileName(group.SourceFile),
                time, group.PointCount, group.Status + autoLabel + decisionLabel);
            _grid.Rows[row].Tag = group;
            _grid.Rows[row].Cells["Time"].ToolTipText = $"{start:yyyy-MM-dd HH:mm:sszzz} → {end:yyyy-MM-dd HH:mm:sszzz}";
            _grid.Rows[row].Cells["File"].ToolTipText = group.SourceFile;
        }
        _summary.Text = $"{ProjectTime.Label(_timeZone)}；范围：{_input.Range?.Source ?? "未设置，保留供人工检查"}。已排除 {_screening.Groups.Count(g => g.Excluded)} 段；" +
            $"单独排除的窗口/航段样点 {_excludedSamples.Count} 个。原始文件保留，日变站不参与空间筛选。";
    }
    private void SetSelected(bool exclude, bool wholeFile)
    {
        var selected = _grid.SelectedRows.Cast<DataGridViewRow>().Select(r => r.Tag).OfType<WorkareaScreeningGroup>().ToArray();
        foreach (DataGridViewRow row in _grid.Rows)
            if (row.Tag is WorkareaScreeningGroup group && selected.Any(s => wholeFile ?
                s.SourceFile.Equals(group.SourceFile, StringComparison.OrdinalIgnoreCase) : s.GroupId == group.GroupId))
            {
                row.Cells["Exclude"].Value = exclude;
                if (!exclude) foreach (var sample in group.Samples) _excludedSamples.Remove(new(sample.SourceFile, sample.SourceRecord));
            }
    }
}
