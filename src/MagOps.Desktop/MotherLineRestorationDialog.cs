using MagOps.Core.Domain;
using MagOps.Infrastructure.Services;

namespace MagOps.Desktop;

internal sealed partial class MotherLineRestorationDialog : Form
{
    private readonly DataGridView _groups = new() { Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, BackgroundColor = Color.White,
        ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize };
    private readonly TextBox _details = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
    private readonly TableLayoutPanel _root;
    private readonly SplitContainer _mapSplit;
    private readonly ComboBox _filter = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal int FilterIndex { get => _filter.SelectedIndex; set => _filter.SelectedIndex = value; }
    internal event Action? CommandRequested;
    internal event Action? ReturnRequested;
    private sealed record Target(string Id, string Name) { public override string ToString() => Name; }
    public string Command { get; private set; } = "";
    public string? TargetOverride { get; private set; }
    public IReadOnlyList<string> GroupIds { get; private set; } = [];
    public IReadOnlyList<string> LegIds { get; private set; } = [];

    public MotherLineRestorationDialog(MotherLineRestorationState state, AuditProductionResult audit, FieldMagFolderAnalysis analysis)
    {
        Text = "母测线整理 / 恢复结果 / 批量核对"; Width = 1380; Height = 900; MinimumSize = new(1050, 740);
        _groups.AccessibleName = "母测线归并组合";
        _details.AccessibleName = "当前归并组合依据与操作提示";
        _filter.AccessibleName = "母测线候选分类";
        StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false;
        var tasks = audit.EffectiveTasks ?? []; var byId = tasks.ToDictionary(t => t.SurveyTaskStableId);
        double? spacing = audit.TrackSegmentation?.RecognitionOptions.ExpectedLineSpacingM;
        if (spacing is not > 0 && audit.InferredAxisDegrees is { } axis)
        {
            var occupied = audit.Execution.Legs.Select(l => l.SurveyTaskStableId).OfType<string>().ToHashSet();
            spacing = IntermediateSegmentationEvidence.Estimate(tasks.Where(t => occupied.Contains(t.SurveyTaskStableId)).ToArray(), axis).SpacingM;
        }
        double crossLimit = spacing is > 0 ? Math.Min(state.Options.CrossM, spacing.Value * .2) : state.Options.CrossM;
        string Name(string id) => byId.TryGetValue(id, out var t) ? t.LineName : id;
        var root = _root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 6, ColumnCount = 1, Padding = new(10) };
        root.RowStyles.Add(new(SizeType.AutoSize)); root.RowStyles.Add(new(SizeType.AutoSize)); root.RowStyles.Add(new(SizeType.Percent, 45));
        root.RowStyles.Add(new(SizeType.Absolute, 62)); root.RowStyles.Add(new(SizeType.Percent, 55)); root.RowStyles.Add(new(SizeType.Absolute, 180));
        root.Controls.Add(new Label { AutoSize = true, MaximumSize = new(800, 0), Text =
            "默认分组：断开测线归并；连接处碎段单独保留，普通批量不吸收。选中连接碎段后，可单段明确指定目标。端部延伸和重复覆盖单独核对；原始点不删除。青色为保留母线，归并后重排r线号及LineOrder。" }, 0, 0);
        var filters = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        var filter = _filter;
        filter.Items.AddRange(["断开测线归并", "所有分组", "端部延伸", "连接处碎段", "重复覆盖", "竞争 / 冲突", "剩余未归属", "已应用", "已排除 / 撤销", "特殊复核全部", "已处理全部"]); filter.SelectedIndex = 0;
        filters.Controls.Add(filter);
        var overview = new CheckBox { Text = "全区背景", AutoSize = true };
        var preview = new CheckBox { Text = "预览归并后", AutoSize = true, Checked = true };
        var corridor = new CheckBox { Text = "公共轴走廊", AutoSize = true, Checked = true };
        filters.Controls.Add(overview); filters.Controls.Add(preview); filters.Controls.Add(corridor);
        root.Controls.Add(filters, 0, 1);
        _groups.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Choose", HeaderText = "选择", Width = 50 });
        foreach (var (id, label, width) in new[] { ("State", "状态", 120), ("Kind", "空间特征分组", 180), ("Lines", "原线号 → 当前目标", 240), ("Segments", "成员/短段", 90),
            ("Included", "应用时补入点数", 120), ("Cross", "横偏P95/m", 110), ("Reason", "证据 / 未归属原因", 580) })
            _groups.Columns.Add(new DataGridViewTextBoxColumn { Name = id, HeaderText = label, Width = width, ReadOnly = true });
        root.Controls.Add(_groups, 0, 2); root.Controls.Add(_details, 0, 3);
        var map = new TrackMapPanel { Dock = DockStyle.Fill };
        var emptyPreviewAnalysis = new FieldMagFolderAnalysis { Fingerprint = analysis.Fingerprint + "|mother:empty", UavSamples = analysis.UavSamples };
        var nearby = new DataGridView { Name = "NearbySegments", AccessibleName = "附近已归属航段（选择仅高亮）", Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
            RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill };
        nearby.Columns.Add(new DataGridViewTextBoxColumn { Name = "NearbyLine", HeaderText = "附近线号", FillWeight = 24 });
        nearby.Columns.Add(new DataGridViewTextBoxColumn { Name = "NearbyLeg", HeaderText = "航段稳定ID", FillWeight = 48 });
        nearby.Columns.Add(new DataGridViewTextBoxColumn { Name = "NearbyDistance", HeaderText = "端点距/m", FillWeight = 18 });
        nearby.Columns.Add(new DataGridViewTextBoxColumn { Name = "NearbyTask", HeaderText = "归属ID", FillWeight = 48 });
        var nearbyPanel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Padding = new(4) };
        nearbyPanel.RowStyles.Add(new(SizeType.AutoSize)); nearbyPanel.RowStyles.Add(new(SizeType.Percent, 100));
        nearbyPanel.Controls.Add(new Label { Text = "附近已归属航段（按选中段端点距离排序；选择一行仅查看，点击下方按钮才设为目标）", AutoSize = true }, 0, 0);
        nearbyPanel.Controls.Add(nearby, 0, 1);
        var mapSplit = _mapSplit = new SplitContainer { Width = 1200, Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterDistance = 760, FixedPanel = FixedPanel.Panel2 };
        mapSplit.Panel1.Controls.Add(map); mapSplit.Panel2.Controls.Add(nearbyPanel); root.Controls.Add(mapSplit, 0, 4);
        var target = new ComboBox { AccessibleName = "手动归并目标母测线", DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
        foreach (var t in tasks.Where(t => t.GeometryOrigin == "ACTUAL_TRACK").OrderBy(t => t.Order)) target.Items.Add(new Target(t.SurveyTaskStableId, $"{t.LineName} · 顺序{t.Order}"));
        if (target.Items.Count > 0) target.SelectedIndex = 0;
        var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new(2) };
        for (int i = 0; i < 4; i++) actions.RowStyles.Add(new(SizeType.Percent, 25));
        FlowLayoutPanel ActionRow() => new() { Dock = DockStyle.Fill, WrapContents = true, AutoScroll = true, Margin = new(0, 1, 0, 1) };
        var selectionActions = ActionRow(); var decisionActions = ActionRow(); var specialActions = ActionRow(); var targetActions = ActionRow();
        actions.Controls.Add(selectionActions, 0, 0); actions.Controls.Add(decisionActions, 0, 1); actions.Controls.Add(specialActions, 0, 2);
        actions.Controls.Add(targetActions, 0, 3);
        foreach (var (row, label) in new[] { (selectionActions, "选择组合"), (decisionActions, "批量处理"), (specialActions, "特殊复核"), (targetActions, "手动归属") })
            row.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new(3, 8, 8, 0) });
        root.Controls.Add(actions, 0, 5); Controls.Add(root);
        var all = state.Groups.ToList();
        var represented = all.Where(g => g.Status is "HIGH" or "REVIEW" or "CONFLICT" or "SUSPECT" or "EXCLUDED" or "REJECTED").SelectMany(g => g.LegIds).ToHashSet();
        foreach (var e in state.UnassignedEvidence.Where(e => !represented.Contains(e.LegId)))
            all.Add(new("diagnostic:" + e.LegId, e.CandidateIds.Count == 1 ? e.CandidateIds[0] : "", "未归属", 0, [e.LegId], [e.LegId], ["未归属"], "UNASSIGNED", e.Reason,
                e.CrossM ?? 0, e.AxisDegrees ?? 0, 0) { AllowShortInclusion = false,
                SuspectKind = e.Reason.Contains("连接") || e.Reason.Contains("往返") ? "连接处碎段·用途保护" : e.CandidateIds.Count > 1 ? "竞争归属或用途冲突" : "" });
        string Friendly(string status) => status switch { "EXCLUDED" => "已排除·可恢复纳入", "SUSPECT" => "疑似·保留", "HIGH" => "高置信", "REVIEW" => "待整体核对", "CONFLICT" => "冲突·保留", "UNASSIGNED" => "未归属",
            "APPLIED_AUTO" => "已自动恢复", "APPLIED_MANUAL" => "已人工应用", "UNDONE" => "已撤销", "REJECTED" => "已忽略", _ => status };
        _refreshSelectionSummary = null;
        var checkedGroups = new HashSet<string>(StringComparer.Ordinal);
        var currentByFilter = new Dictionary<int, string>();
        int displayedFilter = filter.SelectedIndex;
        bool populatingGroups = false;
        Action? refreshPreview = null;
        void RememberChecks()
        {
            _groups.EndEdit();
            foreach (DataGridViewRow row in _groups.Rows)
                if (row.Tag is MotherLineRestoreGroup group)
                {
                    if (row.Cells["Choose"].Value is true) checkedGroups.Add(group.Id);
                    else checkedGroups.Remove(group.Id);
                }
        }
        void SelectionSummary()
        {
            var visible = _groups.Rows.Cast<DataGridViewRow>().Select(r => ((MotherLineRestoreGroup)r.Tag!).Id).ToHashSet();
            int hidden = checkedGroups.Count(id => !visible.Contains(id));
            var chosen = all.Where(g => checkedGroups.Contains(g.Id)).ToArray();
            int points = chosen.Where(g => g.AllowShortInclusion && g.Status is "HIGH" or "REVIEW")
                .SelectMany(g => g.ShortLegIds).Distinct()
                .Sum(id => audit.ManualIncludedFlightLegIds.Contains(id) ? 0 : audit.Execution.Legs.FirstOrDefault(l => l.FlightLegId == id)?.Samples.Count ?? 0);
            if (_root.GetControlFromPosition(0, 0) is Label summary)
                summary.Text = $"当前 {visible.Count} 组；已勾选 {checkedGroups.Count} 组（其他分类 {hidden} 组）；预计补入 {points:N0} 点。青色：目标母线；橙红：待改段。连接碎段仅限单组明确指定目标。";
        }
        _refreshSelectionSummary = SelectionSummary;
        void Populate()
        {
            if (_groups.CurrentRow?.Tag is MotherLineRestoreGroup previous) currentByFilter[displayedFilter] = previous.Id;
            displayedFilter = filter.SelectedIndex;
            RememberChecks();
            populatingGroups = true;
            _groups.Rows.Clear();
            var unassigned = state.UnassignedEvidence.Select(e => e.LegId).ToHashSet();
            foreach (var g in all.OrderBy(g => g.Status is "HIGH" or "REVIEW" ? 0 : g.Status is "CONFLICT" or "UNASSIGNED" ? 1 : 2).ThenBy(g => g.TargetOrder))
            {
                bool gap = g.SuspectKind.StartsWith("断开测线") || g.SuspectKind == "" && g.Status is "HIGH" or "REVIEW";
                bool show = filter.SelectedIndex switch { 0 => gap && g.Status is "HIGH" or "REVIEW", 1 => true,
                    2 => g.SuspectKind.StartsWith("测线端部延伸"), 3 => g.SuspectKind.StartsWith("连接处碎段"), 4 => g.SuspectKind.StartsWith("整线重叠"),
                    5 => g.SuspectKind.Contains("竞争") || g.Status == "CONFLICT", 6 => g.Status == "UNASSIGNED" || g.LegIds.Any(unassigned.Contains),
                    7 => g.Status is "APPLIED_AUTO" or "APPLIED_MANUAL", 8 => g.Status is "UNDONE" or "REJECTED" or "EXCLUDED",
                    9 => g.SuspectKind.StartsWith("测线端部延伸") || g.SuspectKind.StartsWith("整线重叠") || g.SuspectKind.Contains("竞争") || g.Status == "CONFLICT",
                    10 => g.Status is "APPLIED_AUTO" or "APPLIED_MANUAL" or "UNDONE" or "REJECTED" or "EXCLUDED", _ => true };
                if (!show) continue;
                int row = _groups.Rows.Add(checkedGroups.Contains(g.Id), Friendly(g.Status), g.SuspectKind, string.Join("+", g.OriginalNames) + " → " + (byId.ContainsKey(g.TargetId) ? Name(g.TargetId) : g.TargetName),
                    $"{g.LegIds.Count}/{g.ShortLegIds.Count}", g.Status is "HIGH" or "REVIEW" && g.AllowShortInclusion
                        ? audit.Execution.Legs.Where(l => g.ShortLegIds.Contains(l.FlightLegId) && !audit.ManualIncludedFlightLegIds.Contains(l.FlightLegId)).Sum(l => l.Samples.Count) : g.AddedRecords, g.CrossM.ToString("0.##"), g.Reason);
                _groups.Rows[row].Tag = g;
                if (g.SuspectKind.Length > 0) _groups.Rows[row].DefaultCellStyle.BackColor = g.SuspectKind.StartsWith("连接处碎段") ? Color.PeachPuff : g.SuspectKind.StartsWith("断开测线") ? Color.Honeydew : g.SuspectKind.StartsWith("测线端部延伸") ? Color.LightCyan : g.SuspectKind.StartsWith("整线重叠") ? Color.Lavender : Color.LightPink;
                _groups.Rows[row].Cells["Reason"].ToolTipText = g.Reason;
            }
            if (currentByFilter.TryGetValue(displayedFilter, out var previousId)) RestoreCurrentGroup(previousId);
            populatingGroups = false;
            SelectionSummary();
            UpdateCategoryNavigation();
            refreshPreview?.Invoke();
        }
        MotherLineRestoreGroup[] Chosen() { RememberChecks(); return all.Where(g => checkedGroups.Contains(g.Id)).ToArray(); }
        void Add(FlowLayoutPanel row, string label, Action run, bool primary = false) { var b = new Button { Text = label, AutoSize = true, Height = 30, Margin = new(3), UseVisualStyleBackColor = !primary }; if (primary) b.BackColor = Color.LightCyan; b.Click += (_, _) => run(); row.Controls.Add(b); }
        Add(selectionActions, "选择可归并断开组", () => { foreach (DataGridViewRow r in _groups.Rows) r.Cells["Choose"].Value = r.Tag is MotherLineRestoreGroup g && g.Status is "HIGH" or "REVIEW" && (g.SuspectKind.StartsWith("断开测线") || g.SuspectKind == ""); });
        Add(selectionActions, "选择当前同类", () =>
        {
            if (_groups.CurrentRow?.Tag is not MotherLineRestoreGroup current || current.SuspectKind.Length == 0) return;
            foreach (DataGridViewRow row in _groups.Rows) row.Cells["Choose"].Value = row.Tag is MotherLineRestoreGroup candidate && candidate.SuspectKind == current.SuspectKind;
        });
        Add(selectionActions, "选择全部可见", () => { foreach (DataGridViewRow r in _groups.Rows) r.Cells["Choose"].Value = true; });
        Add(selectionActions, "清除选择", () => { checkedGroups.Clear(); foreach (DataGridViewRow r in _groups.Rows) r.Cells["Choose"].Value = false; SelectionSummary(); });
        void Execute(string command)
        {
            _groups.EndEdit(); var selected = Chosen();
            if (selected.Length == 0) { _details.Text = "请勾选要处理的完整组合。"; return; }
            var visibleIds = _groups.Rows.Cast<DataGridViewRow>().Select(r => ((MotherLineRestoreGroup)r.Tag!).Id).ToHashSet();
            if (selected.Any(g => !visibleIds.Contains(g.Id)))
            {
                _details.Text = $"已勾选 {selected.Length} 组，其中 {selected.Count(g => !visibleIds.Contains(g.Id))} 组位于其他分类。请先清除选择，再在当前分类勾选；跨分类选择不会执行。";
                return;
            }
            GroupIds = selected.Select(g => g.Id).ToArray();
            LegIds = selected.SelectMany(g => command is "EXCLUDE" or "INCLUDE" ? (g.ShortLegIds.Count > 0 ? g.ShortLegIds : g.LegIds.Where(id => !g.OriginalTaskByLeg.TryGetValue(id, out var original) || original != g.TargetId)) : g.LegIds).Distinct().ToArray();
            if (command == "ASSIGN")
            {
                if (selected.Length != 1) { _details.Text = "明确指定目标只作用于当前一个组合；不同组合请用批量确认保留各自建议目标。"; return; }
                if (target.SelectedItem is not Target t) return;
                if (LegIds.Any(id => state.ProtectedLegIds.Contains(id))) { _details.Text = "包含人工保护，先使用解除归属保护；人工排除仍保持。"; return; }
                TargetOverride = t.Id;
            }
            if (command == "APPLY" && selected.Any(g => g.SuspectKind.StartsWith("连接处碎段"))) { _details.Text = "连接处碎段不会进入断开测线批量归并；若该处真实断开，请单独选中一个组合并明确指定目标。"; return; }
            if (command == "APPLY" && selected.Any(g => !(g.Status is "HIGH" or "REVIEW") ||
                !(g.SuspectKind.StartsWith("断开测线") || g.SuspectKind.Length == 0)))
            { _details.Text = "普通批量归并仅处理待确认的断开测线组；端部延伸、重复覆盖、冲突及已处理项请使用各自复核操作。"; return; }
            if (command == "APPLY" && !selected.Any(g => g.Status is "HIGH" or "REVIEW")) { _details.Text = "当前勾选没有可执行组，冲突保持原状。"; return; }
            Command = command;
            if (CommandRequested is not null) { CommandRequested(); return; }
            DialogResult = DialogResult.OK; Close();
        }
        Add(decisionActions, "确认断开测线归并并继续", () => Execute("APPLY"), primary: true); Add(decisionActions, "忽略组合", () => Execute("REJECT"));
        Add(decisionActions, "撤销勾选恢复", () => Execute("UNDO")); Add(decisionActions, "重新开放", () => Execute("REOPEN"));
        Add(specialActions, "解除归属保护", () => Execute("RELEASE")); Add(specialActions, "重新评价纳入", () => Execute("RELEASE_INCLUDE"));
        Add(specialActions, "排除勾选疑似段（不删原始点）", () => Execute("EXCLUDE"));
        Add(specialActions, "恢复段纳入", () => Execute("INCLUDE"));
        Add(targetActions, "将附近选中项设为目标", () =>
        {
            if (nearby.CurrentRow?.Tag is not ValueTuple<FlightLeg, PlannedSurveyLineTask, double> item) return;
            target.SelectedIndex = Enumerable.Range(0, target.Items.Count).FirstOrDefault(i => target.Items[i] is Target t && t.Id == item.Item2.SurveyTaskStableId, -1);
        });
        targetActions.Controls.Add(target); Add(targetActions, "明确指定目标（仅归属）", () => Execute("ASSIGN"));
        Add(decisionActions, "关闭", () =>
        {
            if (ReturnRequested is not null) { ReturnRequested(); return; }
            DialogResult = DialogResult.Cancel; Close();
        });
        IReadOnlyList<(FlightLeg Leg, PlannedSurveyLineTask Task, double Distance)> Nearby(MotherLineRestoreGroup g)
        {
            var selected = audit.Execution.Legs.Where(l => g.LegIds.Contains(l.FlightLegId) && l.Samples.Count > 0).ToArray();
            if (selected.Length == 0) return [];
            var anchors = audit.Execution.Legs.Where(l => !g.LegIds.Contains(l.FlightLegId) && l.SurveyTaskStableId is { } id &&
                byId.ContainsKey(id) && l.Samples.Count >= 2 && l.RouteRole is not ("CONNECTOR" or "TRANSIT") &&
                l.MotionKind.Length == 0 && l.TransitEvidence.Count == 0).Select(l => (Leg: l, Task: byId[l.SurveyTaskStableId!])).ToArray();
            return anchors.Select(a =>
            {
                double distance = selected.SelectMany(s => new[] { s.Samples[0], s.Samples[^1] })
                    .Min(endpoint => a.Leg.Samples.Min(p => TrackSegmentationReferenceService.Distance(
                        new(endpoint.Longitude, endpoint.Latitude), new(p.Longitude, p.Latitude))));
                return (a.Leg, a.Task, Distance: distance);
            }).OrderBy(x => x.Distance).ThenBy(x => x.Task.Order).ThenBy(x => x.Leg.FlightLegId, StringComparer.Ordinal).Take(12).ToArray();
        }
        FieldMagFolderAnalysis? previewAnalysis = null;
        IReadOnlyList<PlannedSurveyLineTask> previewTasks = tasks;
        string? geometryKey = null;
        string? nearbySelection = null;
        string? previewGroup = null;
        bool updatingNearby = false;
        void UpdatePreview(bool refreshNearby = true)
        {
            if (_groups.CurrentRow?.Tag is not MotherLineRestoreGroup g)
            {
                nearby.Rows.Clear();
                _details.Text = "当前分类没有候选组合，可切换分类查看已处理或剩余未归属内容。";
                map.SetData(emptyPreviewAnalysis, tasks, audit.Execution, null, acceptedLegIds: audit.ManualIncludedFlightLegIds.ToHashSet());
                return;
            }
            updatingNearby = true;
            if (refreshNearby) nearby.Rows.Clear();
            if (previewGroup != g.Id) nearbySelection = null;
            previewGroup = g.Id;
            var ids = g.LegIds.Concat(g.AnchorLegIds).ToHashSet();
            var near = Nearby(g);
            if (refreshNearby) foreach (var item in near)
            {
                int row = nearby.Rows.Add(item.Task.LineName, item.Leg.FlightLegId, item.Distance.ToString("0.##"), item.Task.SurveyTaskStableId);
                nearby.Rows[row].Tag = item;
            }
            if (refreshNearby)
            {
                // A nearby row is observation only; never replace the suggested target.
                target.SelectedIndex = Enumerable.Range(0, target.Items.Count).FirstOrDefault(i => target.Items[i] is Target t && t.Id == g.TargetId, -1);
            }
            string? activeTarget = target.SelectedItem is Target choice ? choice.Id : byId.ContainsKey(g.TargetId) ? g.TargetId : null;
            bool proposed = preview.Checked && activeTarget is not null && g.Status is "HIGH" or "REVIEW";
            string? Original(FlightLeg l) => g.OriginalTaskByLeg.TryGetValue(l.FlightLegId, out var original) ? original : l.SurveyTaskStableId;
            var sourceIds = g.LegIds.Where(id => audit.Execution.Legs.FirstOrDefault(l => l.FlightLegId == id) is { } leg && Original(leg) != activeTarget).ToHashSet();
            // Keep real identities for labels and hover. Proposed target is a separate overlay.
            var execution = audit.Execution;
            var relatedTasks = near.Select(x => x.Task.SurveyTaskStableId).Concat(g.OriginalTaskByLeg.Values.OfType<string>()).Append(g.TargetId).ToHashSet();
            string outsideTarget = activeTarget is not null && !relatedTasks.Contains(activeTarget) ? activeTarget : "";
            if (activeTarget is not null) relatedTasks.Add(activeTarget);
            var visibleIds = ids.Concat(audit.Execution.Legs.Where(l => l.SurveyTaskStableId is { } id && relatedTasks.Contains(id)).Select(l => l.FlightLegId)).ToHashSet();
            // Fixed geometry per group: clicking a neighbour or toggling the proposal
            // changes overlays, never the geographic frame or navigation transform.
            string nextGeometryKey = g.Id + ":" + overview.Checked + ":" + outsideTarget;
            bool preserveGeometry = geometryKey == nextGeometryKey;
            if (!preserveGeometry)
            {
                geometryKey = nextGeometryKey;
                var keys = audit.Execution.Legs.Where(l => visibleIds.Contains(l.FlightLegId)).SelectMany(l => l.Samples).Select(p => (p.SourceFile, p.SourceRecord)).ToHashSet();
                previewAnalysis = new FieldMagFolderAnalysis { Fingerprint = analysis.Fingerprint + "|mother:" + nextGeometryKey,
                    UavSamples = overview.Checked ? analysis.UavSamples : analysis.UavSamples.Where(p => keys.Contains((p.SourceFile, p.SourceRecord))).ToArray() };
                previewTasks = overview.Checked ? tasks : tasks.Where(t => relatedTasks.Contains(t.SurveyTaskStableId)).ToArray();
            }
            var accepted = audit.ManualIncludedFlightLegIds.ToHashSet();
            if (proposed && g.AllowShortInclusion) accepted.UnionWith(g.ShortLegIds);
            var roles = execution.Legs.Where(l => visibleIds.Contains(l.FlightLegId)).ToDictionary(l => l.FlightLegId,
                l => sourceIds.Contains(l.FlightLegId) ? (g.SuspectKind.StartsWith("连接处碎段") ? "连接碎段" : g.SuspectKind.StartsWith("断开测线") ? "断开段" : g.SuspectKind.StartsWith("测线端部延伸") ? "端部延伸" : g.SuspectKind.StartsWith("整线重叠") ? "疑似重复" : g.SuspectKind.Length > 0 ? "疑似冲突" : "源段") : l.SurveyTaskStableId == activeTarget ? "目标母线" : "其他候选");
            var mapSelected = execution.Legs.Where(l => roles.ContainsKey(l.FlightLegId)).ToArray();
            map.SetData(previewAnalysis!, previewTasks, execution, null, activeTarget, mapSelected, accepted,
                preserveView: preserveGeometry, highlightRoles: roles);
            map.InspectLeg(nearbySelection);
            double shownLimit = g.Id.StartsWith("spatial:", StringComparison.Ordinal) && spacing is > 0 ? Math.Min(state.Options.CrossM * 2, spacing.Value * .4) : crossLimit;
            map.SetCorridor(preview.Checked && corridor.Checked && activeTarget is not null && byId.TryGetValue(activeTarget, out var t) ? t : null, shownLimit);
            _details.Text = $"橙红实线：待改段（{sourceIds.Count}段）；青色实线：目标母线{(activeTarget is null ? "待确定" : Name(activeTarget))}；灰色虚线：附近参考；绿色：断开测线，橙色：连接处碎段，紫色：重复覆盖，红色：归属冲突。悬停查看线号与航段ID，点击仅查看，不改变归属。{Friendly(g.Status)}；横偏P95 {g.CrossM:0.##}m，轴差{(g.Id.StartsWith("spatial:") ? "未计算" : g.AngleDegrees.ToString("0.##") + "°")}。{g.Reason}。建议目标：{g.TargetName}；手动目标：{(activeTarget is null ? "未设置" : Name(activeTarget))}；疑似类型：{g.SuspectKind}；转弯起始来源记录：{string.Join(",", g.TurnBoundaryRecords.Select(p => p.Key + ":" + p.Value))}；实际待改段：{string.Join(",", sourceIds)}";
            updatingNearby = false;
        }
        refreshPreview = () => UpdatePreview();
        filter.SelectedIndexChanged += (_, _) => Populate();
        _groups.SelectionChanged += (_, _) => { if (!populatingGroups) UpdatePreview(); };
        _groups.CurrentCellDirtyStateChanged += (_, _) => { if (_groups.IsCurrentCellDirty) _groups.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        _groups.CellValueChanged += (_, e) =>
        {
            if (populatingGroups || e.RowIndex < 0 || e.ColumnIndex != 0) return;
            RememberChecks(); SelectionSummary();
        };
        preview.CheckedChanged += (_, _) => UpdatePreview(); overview.CheckedChanged += (_, _) => UpdatePreview(); corridor.CheckedChanged += (_, _) => UpdatePreview();
        nearby.CurrentCellChanged += (_, _) =>
        {
            if (updatingNearby || nearby.CurrentRow?.Tag is not ValueTuple<FlightLeg, PlannedSurveyLineTask, double> item) return;
            nearbySelection = item.Item1.FlightLegId;
            UpdatePreview(false);
            _details.Text = $"附近查看：{item.Item2.LineName} · {item.Item1.FlightLegId}；当前归并目标保持不变。";
        };
        target.SelectedIndexChanged += (_, _) => { if (!updatingNearby) UpdatePreview(false); };
        map.LegsClicked += (ids, _) =>
        {
            _details.Text = "点击查看：" + string.Join("；", ids.Select(id =>
            {
                var leg = audit.Execution.Legs.FirstOrDefault(l => l.FlightLegId == id);
                return $"{(leg?.SurveyTaskStableId is { } taskId ? Name(taskId) : "未归属")} · {id}";
            })) + "。源段保持不变；在附近航段表中选择目标，再明确指定目标或批量确认。";
        };
        root.SizeChanged += (_, _) =>
        {
            foreach (var label in root.Controls.OfType<Label>()) label.MaximumSize = new(Math.Max(300, root.ClientSize.Width - 30), 0);
        };
        Populate();
    }

    internal void ConfigureEmbeddedLayout()
    {
        // A shared review footer already occupies the host's lower area. Use the
        // workspace width for the list and map instead of stacking both vertically.
        _root.SuspendLayout();
        ConfigureCategoryNavigation();
        _root.ColumnStyles.Clear();
        _root.ColumnStyles.Add(new(SizeType.Percent, 100));
        _root.Padding = new(6);
        _root.Controls.Remove(_groups);
        _root.Controls.Remove(_details);
        _root.Controls.Remove(_mapSplit);
        var evidence = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        evidence.ColumnStyles.Add(new(SizeType.Percent, 100));
        evidence.RowStyles.Add(new(SizeType.Percent, 100));
        evidence.RowStyles.Add(new(SizeType.Absolute, 44));
        evidence.Controls.Add(_mapSplit, 0, 0);
        evidence.Controls.Add(_details, 0, 1);
        var split = new SplitContainer { Dock = DockStyle.Fill, Width = 1000, SplitterDistance = 380 };
        split.Panel1.Controls.Add(_groups);
        split.Panel2.Controls.Add(evidence);
        _root.Controls.Add(split, 0, 2);
        _root.SetRowSpan(split, 3);
        _root.RowStyles[2] = new(SizeType.Percent, 100);
        _root.RowStyles[3] = new(SizeType.Absolute, 0);
        _root.RowStyles[4] = new(SizeType.Absolute, 0);
        var actions = (TableLayoutPanel)_root.GetControlFromPosition(0, 5)!;
        var rows = Enumerable.Range(0, 4).Select(i => actions.GetControlFromPosition(0, i)!).ToArray();
        var actionTabs = new TabControl { Dock = DockStyle.Fill };
        foreach (var (title, first) in new[] { ("批量归并", 0), ("特殊复核与手动归属", 2) })
        {
            var page = new TabPage(title);
            var commands = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
            commands.ColumnStyles.Add(new(SizeType.Percent, 100));
            commands.RowStyles.Add(new(SizeType.Percent, 50));
            commands.RowStyles.Add(new(SizeType.Percent, 50));
            commands.Controls.Add(rows[first], 0, 0);
            commands.Controls.Add(rows[first + 1], 0, 1);
            page.Controls.Add(commands);
            actionTabs.TabPages.Add(page);
        }
        _root.Controls.Remove(actions);
        actions.Dispose();
        _root.Controls.Add(actionTabs, 0, 5);
        _root.RowStyles[5] = new(SizeType.Absolute, 146);
        if (_root.GetControlFromPosition(0, 0) is Label summary)
            summary.Text = "按组核对并批量确认断开测线；连接碎段仅限单组明确指定目标。青色为目标母线，橙红为待改段；原始点保留。";
        _mapSplit.Panel1.Controls.OfType<TrackMapPanel>().Single().ShowInlineLegend = false;
        _mapSplit.FixedPanel = FixedPanel.None;
        _root.RowStyles[0] = new(SizeType.Absolute, 26);
        if (_root.GetControlFromPosition(0, 0) is Label compactSummary)
        {
            compactSummary.AutoSize = false;
            compactSummary.AutoEllipsis = true;
            compactSummary.Dock = DockStyle.Fill;
            var tip = new ToolTip();
            tip.SetToolTip(compactSummary, compactSummary.Text);
            compactSummary.TextChanged += (_, _) => tip.SetToolTip(compactSummary, compactSummary.Text);
            compactSummary.Disposed += (_, _) => tip.Dispose();
        }
        _mapSplit.SizeChanged += (_, _) =>
        {
            if (_mapSplit.ClientSize.Width > 200)
                _mapSplit.SplitterDistance = (int)(_mapSplit.ClientSize.Width * .62);
        };
        _root.ResumeLayout(true);
        _refreshSelectionSummary?.Invoke();
    }
}

internal sealed class MotherLineRestoreOptionsDialog : Form
{
    public MotherLineRestoreOptions? Result { get; private set; }
    public MotherLineRestoreOptionsDialog(MotherLineRestoreOptions value)
    {
        Text = "自动恢复母测线设置"; Width = 620; Height = 370; MinimumSize = new(560, 350);
        StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false;
        var root = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new(12) };
        NumericUpDown Input(string label, double current, decimal min, decimal max)
        {
            var row = new FlowLayoutPanel { AutoSize = true };
            row.Controls.Add(new Label { Text = label, Width = 300, Margin = new(3, 8, 3, 0) });
            var n = new NumericUpDown { Minimum = min, Maximum = max, DecimalPlaces = 1, Value = (decimal)current, Width = 100 };
            row.Controls.Add(n); root.Controls.Add(row); return n;
        }
        var cross = Input("最大横偏m（同时不超过参考线距20%）", value.CrossM, .1m, 100);
        var axis = Input("最大轴向差°", value.AxisDegrees, .1m, 15);
        var span = Input("中间短段合计跨度上限m", value.MaximumShortSpanM, 1, 1000);
        var time = Input("相邻观测时间间隔上限s", value.MaximumBoundarySeconds, .1m, 8);
        var gap = Input("待衔接范围上限m", value.MaximumAlongGapM, 0, 10000);
        root.Controls.Add(new Label { AutoSize = true, MaximumSize = new(550, 0), Text = "参数用于后续候选检查；已应用的组合保留，可在恢复结果中撤销。缺失观测不会生成插值点。" });
        var actions = new FlowLayoutPanel { AutoSize = true };
        var apply = new Button { Text = "应用并重新检查", AutoSize = true };
        apply.Click += (_, _) => { Result = new((double)cross.Value, (double)axis.Value, (double)span.Value, (double)time.Value, (double)gap.Value); Result.Validate(); DialogResult = DialogResult.OK; Close(); };
        actions.Controls.Add(apply); actions.Controls.Add(new Button { Text = "取消", AutoSize = true, DialogResult = DialogResult.Cancel });
        root.Controls.Add(actions); Controls.Add(root);
    }
}
