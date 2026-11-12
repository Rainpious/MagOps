using MagOps.Application;
using MagOps.Infrastructure.Services;
using MagOps.Core.Domain;

namespace MagOps.Desktop;

internal sealed partial class AutomaticSegmentationReviewDialog
{
    private void ShowActualLineRegrouping()
    {
        ShowMotherLineRestoration();
    }

    private void ApplyActualLineRegrouping(IReadOnlyList<string> legIds, string targetId)
        => ApplyActualLineRegroupingBatch(legIds.Select(id => new ManualFlightLegAssignment(id, targetId, "用户确认归并")).ToArray());

    private void ApplyActualLineRegroupingBatch(IReadOnlyList<ManualFlightLegAssignment> assignments)
    {
        if (!_spatialOnly || _audit is null || assignments.Any(a => !_tasks.Any(t => t.SurveyTaskStableId == a.SurveyTaskStableId && t.GeometryOrigin == "ACTUAL_TRACK")))
            throw new InvalidOperationException("当前归并仅支持中间 CSV 的实测推断母测线。");
        var legIds = assignments.Select(a => a.FlightLegId).ToArray();
        if (legIds.Distinct().Count() != legIds.Length) throw new InvalidDataException("同一航段不能在同一批次指定多个目标。");
        var known = _audit.Execution.Legs.Select(l => l.FlightLegId).ToHashSet();
        if (legIds.Length == 0 || legIds.Any(id => !known.Contains(id))) throw new InvalidDataException("归并航段已变化，请重新选择。");
        var previous = legIds.Distinct().Select(id => _reviewDecisions.Get(id)).ToArray();
        var previousRestoration = _seedRequest.MotherLineRestoration;
        var recorded = MotherLineRestorationWorkflow.RecordManual(previousRestoration ?? new(), assignments, _reviewDecisions, _audit.Execution, _tasks);
        _seedRequest = _seedRequest with { MotherLineRestoration = recorded };
        _seedRequest = BuildReviewedRequest();
        try { RefreshReviewedExecution(); }
        catch
        {
            foreach (var decision in previous) _reviewDecisions.Set(decision);
            _seedRequest = _seedRequest with { MotherLineRestoration = previousRestoration };
            _seedRequest = BuildReviewedRequest();
            throw;
        }
        _status.Text = $"已将 {previous.Length} 个航段归并到目标母测线；纳入选择不变，各段/趟次保留。可撤销，保存后可继续核验。";
    }

    private void UndoActualLineRegrouping()
    {
        UndoLatestMotherLineBatch();
    }


}

internal sealed class ActualLineRegroupingDialog : Form
{
    private sealed record Target(string Id, string Label) { public override string ToString() => Label; }
    private readonly ComboBox _target = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 320 };
    private readonly DataGridView _legs = Grid();
    private readonly DataGridView _pairs = Grid();
    private readonly NumericUpDown _cross = new() { Minimum = .1m, Maximum = 1000, DecimalPlaces = 1, Value = 10, Width = 70 };
    private readonly NumericUpDown _angle = new() { Minimum = .1m, Maximum = 15, DecimalPlaces = 1, Value = 3, Width = 60 };
    private readonly NumericUpDown _gap = new() { Minimum = 0, Maximum = 10000, Value = 200, Width = 70 };
    private readonly NumericUpDown _spacing = new() { Minimum = 0, Maximum = 5000, DecimalPlaces = 1, Width = 70 };
    private readonly Label _summary = new() { AutoSize = true };
    public string? TargetId { get; private set; }
    public IReadOnlyList<string> LegIds { get; private set; } = [];
    public IReadOnlyList<ManualFlightLegAssignment> BatchAssignments { get; private set; } = [];

    public ActualLineRegroupingDialog(AuditProductionResult audit, IReadOnlySet<string> selected)
    {
        Text = "归并到母测线：先核对空间证据，再应用";
        Width = 1580; Height = 900; MinimumSize = new(1100, 700);
        StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false;
        var tasks = audit.EffectiveTasks ?? [];
        var actual = tasks.Where(t => t.GeometryOrigin == "ACTUAL_TRACK").OrderBy(t => t.Order).ToArray();
        var byId = tasks.ToDictionary(t => t.SurveyTaskStableId);
        foreach (var t in actual) _target.Items.Add(new Target(t.SurveyTaskStableId, $"{t.LineName} · 顺序{t.Order} · {t.LengthM:0}m"));
        if (_target.Items.Count > 0) _target.SelectedIndex = 0;
        double? estimate = audit.InferredAxisDegrees is { } axis ? IntermediateSegmentationEvidence.Estimate(actual, axis).SpacingM : null;
        if (estimate is > 0) _spacing.Value = (decimal)Math.Clamp(estimate.Value, 0, 5000);
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, Padding = new(10) };
        root.RowStyles.Add(new(SizeType.AutoSize)); root.RowStyles.Add(new(SizeType.AutoSize));
        root.RowStyles.Add(new(SizeType.Percent, 44)); root.RowStyles.Add(new(SizeType.AutoSize));
        root.RowStyles.Add(new(SizeType.Percent, 56)); root.RowStyles.Add(new(SizeType.AutoSize)); root.RowStyles.Add(new(SizeType.AutoSize));
        root.Controls.Add(new Label { AutoSize = true, MaximumSize = new(1120, 0), Text =
            "下方候选可全选或批量勾选，核对右侧地图后点击“确认勾选候选并继续”，一次完成归并。每对保留测线一的线号；共享测线的候选需先消除冲突。上方仍可手动指定例外。原始记录、断口、各趟次及纳入范围保留。" }, 0, 0);
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        actions.Controls.Add(new Label { Text = "目标母测线", AutoSize = true, Margin = new(3, 8, 3, 0) }); actions.Controls.Add(_target);
        root.Controls.Add(actions, 0, 1);
        _legs.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Choose", HeaderText = "归并", Width = 60 });
        foreach (var (name, label, width) in new[] { ("Line", "当前线号", 100), ("Window", "任务窗口", 220), ("Points", "记录数", 80),
            ("Time", "源时间范围", 220), ("Role", "用途 / 证据", 330) })
            _legs.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = label, Width = width, ReadOnly = true });
        foreach (var leg in audit.Execution.Legs)
        {
            string line = leg.SurveyTaskStableId is { } id && byId.TryGetValue(id, out var t) ? t.LineName : "未归属";
            int index = _legs.Rows.Add(selected.Contains(leg.FlightLegId), line, leg.MissionExecutionId, leg.Samples.Count,
                $"{leg.StartUtc:MM-dd HH:mm:ss}–{leg.EndUtc:HH:mm:ss}", leg.RouteRole + " / " + leg.ReviewReason);
            _legs.Rows[index].Tag = leg;
        }
        root.Controls.Add(_legs, 0, 2);
        var parameters = new FlowLayoutPanel { AutoSize = false, Height = 100, Dock = DockStyle.Fill, WrapContents = true };
        root.RowStyles[3] = new RowStyle(SizeType.Absolute, 100);
        void Add(string name, Control control) { parameters.Controls.Add(new Label { Text = name, AutoSize = true, Margin = new(3, 8, 3, 0) }); parameters.Controls.Add(control); }
        Add("横向阈值m", _cross); Add("方向差°", _angle); Add("沿线缺口上限m", _gap); Add("参考线距m（0为未知）", _spacing);
        var refresh = new Button { Text = "计算空间候选", AutoSize = true };
        parameters.Controls.Add(refresh);
        root.Controls.Add(parameters, 0, 3);
        _pairs.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Choose", HeaderText = "确认", Width = 55 });
        foreach (var (name, label, width) in new[] { ("First", "测线一（保留）", 120), ("Second", "测线二（并入）", 120), ("Angle", "轴向差°", 90),
            ("Cross", "双向横偏P95/m", 140), ("Gap", "沿线缺口/m", 110), ("Overlap", "沿线重叠", 100), ("Relation", "关系提示", 380) })
            _pairs.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = label, Width = width, ReadOnly = true });
        root.Controls.Add(_pairs, 0, 4); root.Controls.Add(_summary, 0, 5);
        bool updatingPairs = false;
        Action? updatePreview = null;
        void Recalculate()
        {
            var pairs = ActualLineRegroupingService.Suggest(tasks, audit.Execution, (double)_cross.Value, (double)_angle.Value,
                (double)_gap.Value, _spacing.Value > 0 ? (double)_spacing.Value : null);
            updatingPairs = true;
            _pairs.Rows.Clear();
            foreach (var pair in pairs)
            {
                int row = _pairs.Rows.Add(false, pair.FirstName, pair.SecondName, pair.AxisDifferenceDegrees.ToString("0.##"),
                    pair.CrossDistanceM.ToString("0.##"), pair.AlongGapM.ToString("0.#"), pair.OverlapRatio.ToString("P0"), pair.Relation);
                _pairs.Rows[row].Tag = pair;
            }
            updatingPairs = false;
            updatePreview?.Invoke();
            _summary.Text = $"{pairs.Count} 对候选；有效横向阈值 {Math.Min((double)_cross.Value, _spacing.Value > 0 ? (double)_spacing.Value * .2 : double.MaxValue):0.##}m。参数修改后请重新计算，原勾选会清除。";
        }
        refresh.Click += (_, _) => Recalculate();
        var bottom = new FlowLayoutPanel { AutoSize = false, Height = 80, Dock = DockStyle.Fill, WrapContents = true };
        root.RowStyles[6] = new RowStyle(SizeType.Absolute, 80);
        var use = new Button { Text = "将所选候选载入上方预览", AutoSize = true };
        use.Click += (_, _) =>
        {
            if (_pairs.CurrentRow?.Tag is not ActualLinePair pair) return;
            _target.SelectedItem = _target.Items.Cast<Target>().First(t => t.Id == pair.FirstId);
            foreach (DataGridViewRow row in _legs.Rows)
                row.Cells["Choose"].Value = row.Tag is MagOps.Core.Domain.FlightLeg leg && leg.SurveyTaskStableId == pair.SecondId;
            _summary.Text = $"已预览 {pair.SecondName} → {pair.FirstName}；请核对上方航段和目标，再点击应用归并。";
        };
        actions.Controls.Add(use);
        var apply = new Button { Text = "应用归并", AutoSize = true };
        apply.Click += (_, _) =>
        {
            _legs.EndEdit();
            var chosen = _legs.Rows.Cast<DataGridViewRow>().Where(r => r.Cells["Choose"].Value is true)
                .Select(r => ((MagOps.Core.Domain.FlightLeg)r.Tag!).FlightLegId).ToArray();
            if (_target.SelectedItem is not Target target || chosen.Length == 0)
            { _summary.Text = "请勾选航段并选择目标母测线。"; return; }
            TargetId = target.Id; LegIds = chosen; DialogResult = DialogResult.OK; Close();
        };
        actions.Controls.Add(apply); bottom.Controls.Add(new Button { Text = "取消", AutoSize = true, DialogResult = DialogResult.Cancel });
        root.Controls.Add(bottom, 0, 6);
        var map = new TrackMapPanel { Dock = DockStyle.Fill };
        var after = new CheckBox { Text = "预览归并后（不改变纳入范围）", AutoSize = true, Checked = true };
        var overview = new CheckBox { Text = "显示全区背景", AutoSize = true };
        var detail = new Label { AutoSize = true, MaximumSize = new(530, 0) };
        var visual = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new(5) };
        visual.RowStyles.Add(new(SizeType.AutoSize)); visual.RowStyles.Add(new(SizeType.AutoSize)); visual.RowStyles.Add(new(SizeType.Percent, 100));
        var display = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        display.Controls.Add(after); display.Controls.Add(overview);
        visual.Controls.Add(display, 0, 0); visual.Controls.Add(detail, 0, 1); visual.Controls.Add(map, 0, 2);
        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, Size = new(1500, 780), SplitterDistance = 920 };
        split.Panel1.Controls.Add(root); split.Panel2.Controls.Add(visual); Controls.Add(split);
        split.Panel1.Resize += (_, _) =>
        {
            var heading = root.GetControlFromPosition(0, 0)!;
            heading.MaximumSize = new(Math.Max(250, split.Panel1.ClientSize.Width - 24), 0);
            _summary.MaximumSize = heading.MaximumSize;
            actions.MaximumSize = heading.MaximumSize;
            parameters.MaximumSize = new(heading.MaximumSize.Width, 100);
            bottom.MaximumSize = new(heading.MaximumSize.Width, 80);
        };
        var source = new FieldMagFolderAnalysis { UavSamples = audit.Execution.Legs.SelectMany(l => l.Samples).Select(p =>
            new UavMagCanonicalSample(p.TimestampUtc, p.Longitude, p.Latitude, p.AltitudeM, null, null, null, "", null, "", p.SourceFile, p.SourceRecord, [])).ToArray() };
        ActualLinePair[] Chosen() => _pairs.Rows.Cast<DataGridViewRow>().Where(r => r.Cells["Choose"].Value is true).Select(r => (ActualLinePair)r.Tag!).ToArray();
        void Preview()
        {
            if (updatingPairs) return;
            var chosen = Chosen();
            var focus = _pairs.CurrentRow?.Tag as ActualLinePair;
            var shown = focus is null ? chosen : new[] { focus };
            var ids = shown.SelectMany(p => new[] { p.FirstId, p.SecondId }).ToHashSet();
            var assignments = shown.SelectMany(p => audit.Execution.Legs.Where(l => l.SurveyTaskStableId == p.SecondId)
                .Select(l => new ManualFlightLegAssignment(l.FlightLegId, p.FirstId, "preview"))).ToArray();
            var execution = after.Checked ? audit.Execution with { Legs = audit.Execution.Legs.Select(l => assignments.FirstOrDefault(a => a.FlightLegId == l.FlightLegId) is { } a
                ? l with { SurveyTaskStableId = a.SurveyTaskStableId } : l).ToArray() } : audit.Execution;
            var geometry = after.Checked ? ActualLineRegroupingService.ExtendTargets(tasks, audit.Execution, assignments) : tasks.ToArray();
            var selectedLegs = audit.Execution.Legs.Where(l => l.SurveyTaskStableId is { } id && ids.Contains(id)).Select(l => execution.Legs.Single(e => e.FlightLegId == l.FlightLegId)).ToArray();
            var selectedIds = selectedLegs.Select(l => l.FlightLegId).ToHashSet();
            execution = execution with { Passes = execution.Passes.Where(p => !selectedIds.Contains(p.FlightLegId)).Concat(selectedLegs.Select(l =>
                l.SurveyTaskStableId is { } id ? WholeLegMatcherService.CreateManualAssignmentPass(l, geometry.Single(t => t.SurveyTaskStableId == id)) : null).OfType<SurveyLinePass>()).ToArray() };
            var visibleTasks = !overview.Checked && ids.Count > 0 ? geometry.Where(t => ids.Contains(t.SurveyTaskStableId)).ToArray() : geometry;
            var selectedKeys = selectedLegs.SelectMany(l => l.Samples).Select(s => (s.SourceFile, s.SourceRecord)).ToHashSet();
            var visibleSource = !overview.Checked && selectedIds.Count > 0 ? new FieldMagFolderAnalysis { UavSamples = source.UavSamples.Where(p =>
                selectedKeys.Contains((p.SourceFile, p.SourceRecord))).ToArray() } : source;
            if (!overview.Checked && selectedIds.Count > 0) execution = execution with { Legs = selectedLegs, Passes = execution.Passes.Where(p => selectedIds.Contains(p.FlightLegId)).ToArray() };
            map.SetData(visibleSource, visibleTasks, execution, selectedLegs.FirstOrDefault(), after.Checked ? focus?.FirstId : null, selectedLegs, audit.ManualIncludedFlightLegIds.ToHashSet());
            detail.Text = $"已勾选 {chosen.Length} 对。" + (focus is null ? "选择候选查看空间轨迹。" : $"当前：{focus.SecondName} → {focus.FirstName}；横偏P95 {focus.CrossDistanceM:0.##}m；缺口 {focus.AlongGapM:0.#}m；重叠 {focus.OverlapRatio:P0}。红色描边为当前两条测线，原观测断口保留。");
        }
        updatePreview = Preview;
        _pairs.SelectionChanged += (_, _) => Preview();
        _pairs.CurrentCellDirtyStateChanged += (_, _) => { if (_pairs.IsCurrentCellDirty) _pairs.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        _pairs.CellValueChanged += (_, _) => Preview(); after.CheckedChanged += (_, _) => Preview(); overview.CheckedChanged += (_, _) => Preview();
        foreach (var (label, check) in new[] { ("全选候选", true), ("清空勾选", false) })
        {
            var choose = new Button { Text = label, AutoSize = true };
            choose.Click += (_, _) => { updatingPairs = true; foreach (DataGridViewRow r in _pairs.Rows) r.Cells["Choose"].Value = check; updatingPairs = false; Preview(); };
            bottom.Controls.Add(choose);
        }
        var batch = new Button { Text = "确认勾选候选并继续", AutoSize = true };
        batch.Click += (_, _) =>
        {
            _pairs.EndEdit(); var chosen = Chosen();
            if (chosen.Length == 0) { _summary.Text = "请勾选下方候选；也可点击全选候选后统一确认。"; return; }
            var duplicates = chosen.SelectMany(p => new[] { p.FirstId, p.SecondId }).GroupBy(id => id).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
            if (duplicates.Count > 0) { _summary.Text = "勾选组合共享测线：" + string.Join("、", actual.Where(t => duplicates.Contains(t.SurveyTaskStableId)).Select(t => t.LineName)) + "。请取消冲突勾选，避免链式覆盖；本次尚未应用。"; return; }
            BatchAssignments = chosen.SelectMany(p => audit.Execution.Legs.Where(l => l.SurveyTaskStableId == p.SecondId)
                .Select(l => new ManualFlightLegAssignment(l.FlightLegId, p.FirstId, "用户批量确认空间候选"))).ToArray();
            if (BatchAssignments.Count == 0) { _summary.Text = "候选航段已变化，请重新计算。"; return; }
            DialogResult = DialogResult.OK; Close();
        };
        bottom.Controls.Add(batch); bottom.Controls.SetChildIndex(batch, 0);
        Recalculate(); Preview();
    }

    private static DataGridView Grid() => new() { Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        AutoGenerateColumns = false, RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        BackgroundColor = Color.White, ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
        ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { WrapMode = DataGridViewTriState.True } };
}
