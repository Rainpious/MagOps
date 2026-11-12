using System.Globalization;
using MagOps.Core.Domain;
using MagOps.Infrastructure.Services;

namespace MagOps.Desktop;

internal sealed partial class AutomaticSegmentationReviewDialog
{
    // The grids project session decisions. Filtering these two views never
    // removes decisions or changes source/sample identities.
    private readonly DataGridView _pendingLegGrid = GridFactory.Create();
    private readonly DataGridView _acceptedLegGrid = GridFactory.Create();
    private readonly ComboBox _legSort = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
    private readonly ComboBox _pendingLegFilter = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
    private readonly CheckBox _legSortDescending = new() { Text = "降序", AutoSize = true };
    private readonly Label _pendingLegCount = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label _acceptedLegCount = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    private bool _refreshingLegViews;

    private bool IsAcceptedLegRow(DataGridViewRow row) => row.Tag is ReviewLegRow value &&
        _reviewDecisions.Get(value.Leg.FlightLegId).Accepted;

    private Control BuildLegSelectionPanel()
    {
        _pendingLegGrid.AccessibleName = "待核验航段（左侧）";
        _acceptedLegGrid.AccessibleName = "已纳入航段（右侧）";
        _legSort.AccessibleName = "航段排序字段";
        _pendingLegFilter.AccessibleName = "待核验航段类别筛选";
        _legDetails.AccessibleName = "当前航段时间方向与识别依据";
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
        var settings = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, WrapContents = true };
        bool detailsExpanded = false;
        void FitSelectionLayout()
        {
            // Keep wrapped actions reachable without consuming the two leg lists on short windows.
            int preferred = settings.GetPreferredSize(new Size(Math.Max(1, panel.ClientSize.Width - 24), 0)).Height;
            panel.RowStyles[0].Height = Math.Min(preferred, Math.Max(48, panel.ClientSize.Height / 4));
            panel.RowStyles[2].Height = detailsExpanded ? 180 : Math.Clamp(panel.ClientSize.Height / 6, 32, 70);
        }
        panel.SizeChanged += (_, _) => FitSelectionLayout();
        settings.Controls.Add(new Label { Text = "排序", AutoSize = true, Margin = new Padding(3, 8, 3, 3) });
        _legSort.Items.AddRange(["类别（默认）", "测线号", "飞行时间", "覆盖率", "实飞方向", "长度"]);
        _legSort.SelectedIndex = 0;
        _legSort.SelectedIndexChanged += (_, _) => RefreshLegSelectionViews();
        _legSortDescending.CheckedChanged += (_, _) => RefreshLegSelectionViews();
        settings.Controls.Add(_legSort);
        settings.Controls.Add(_legSortDescending);
        settings.Controls.Add(new Label { Text = "左侧筛选", AutoSize = true, Margin = new Padding(3, 8, 3, 3) });
        _pendingLegFilter.Items.AddRange(["全部", "已匹配", "未匹配", "反向航段", "有质量提示", "往返上下文", "连接段"]);
        _pendingLegFilter.SelectedIndex = 0;
        _pendingLegFilter.SelectedIndexChanged += (_, _) =>
        {
            // Do not leave filtered-out selections eligible for an exclusion action.
            _legGrid.ClearSelection();
            _legGrid.CurrentCell = null;
            RefreshLegSelectionViews();
        };
        settings.Controls.Add(_pendingLegFilter);
        settings.Controls.Add(UiComponents.Button("选中同一往返整段", (_, _) =>
        {
            var selected = _pendingLegGrid.SelectedRows.Cast<DataGridViewRow>().Select(r => r.Tag).OfType<ReviewLegRow>()
                .Where(v => v.Leg.RouteRole == "TRANSIT").Select(v => v.Leg.ParentRouteId).ToHashSet();
            foreach (DataGridViewRow row in _pendingLegGrid.Rows)
                if (row.Tag is ReviewLegRow value && selected.Contains(value.Leg.ParentRouteId)) row.Selected = true;
        }));
        settings.Controls.Add(UiComponents.Button("批量添加作业候选", (_, _) => MoveLegRows(
            _pendingLegGrid.Rows.Cast<DataGridViewRow>().Where(r => r.Tag is ReviewLegRow value &&
                CanBatchAddLeg(value)), true)));
        settings.Controls.Add(UiComponents.Button("核对异常区间", (_, _) => ReviewProductionAnomalies()));
        settings.Controls.Add(UiComponents.Button("显示列…", (sender, _) => ShowColumnSettings((Control)sender!)));
        if (_spatialOnly)
        {
            settings.Controls.Add(UiComponents.Button("归并到母测线…", (_, _) => ShowActualLineRegrouping()));
            settings.Controls.Add(UiComponents.Button("撤销上次归并", (_, _) => UndoActualLineRegrouping()));
        }
        settings.Controls.Add(MenuButton("更多…", ("手动指定测线", () =>
        {
            foreach (var grid in new[] { _pendingLegGrid, _acceptedLegGrid })
                grid.Columns["ManualTask"]!.Visible = true;
            _status.Text = "在所选航段的“指定所属测线”列中选择身份；归属修改不会自动纳入右侧。";
        }), ("展开 / 收起详情", () =>
        {
            detailsExpanded = !detailsExpanded;
            FitSelectionLayout();
        })));
        panel.Controls.Add(settings, 0, 0);

        var lanes = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1 };
        lanes.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        lanes.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        lanes.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        lanes.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        Control Lane(DataGridView grid, Label label)
        {
            ConfigureLegSelectionGrid(grid);
            var lane = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            lane.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            lane.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            lane.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            lane.Controls.Add(label, 0, 0);
            lane.Controls.Add(grid, 0, 1);
            return lane;
        }
        lanes.Controls.Add(Lane(_pendingLegGrid, _pendingLegCount), 0, 0);
        var transfer = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoScroll = true,
            FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(0, 25, 0, 0) };
        transfer.Controls.Add(UiComponents.Button("添加 →", (_, _) => MoveLegRows(_pendingLegGrid.SelectedRows.Cast<DataGridViewRow>(), true)));
        transfer.Controls.Add(UiComponents.Button("← 移回", (_, _) => MoveLegRows(_acceptedLegGrid.SelectedRows.Cast<DataGridViewRow>(), false)));
        transfer.Controls.Add(UiComponents.Button("左侧全选", (_, _) => _pendingLegGrid.SelectAll()));
        transfer.Controls.Add(UiComponents.Button("右侧全选", (_, _) => _acceptedLegGrid.SelectAll()));
        lanes.Controls.Add(transfer, 1, 0);
        lanes.Controls.Add(Lane(_acceptedLegGrid, _acceptedLegCount), 2, 0);
        panel.Controls.Add(lanes, 0, 1);
        panel.Controls.Add(_legDetails, 0, 2);
        return panel;
    }

    private void ReviewProductionAnomalies()
    {
        if (_audit is null || _audit.ProductionAnomalies.Count == 0)
        { _status.Text = "当前未发现符合停留观察窗的异常区间。"; return; }
        using var dialog = new Form { Text = "生产异常区间（身份与原始数据保留）", Width = 1050, Height = 550,
            StartPosition = FormStartPosition.CenterParent, MinimumSize = new Size(750, 400) };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var grid = GridFactory.Create();
        grid.Dock = DockStyle.Fill;
        grid.ReadOnly = false;
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
        grid.AllowUserToAddRows = false;
        grid.Columns.Add("Time", "时间区间"); grid.Columns.Add("Points", "点数");
        grid.Columns.Add("Evidence", "证据");
        grid.Columns.Add(new DataGridViewComboBoxColumn { Name = "Decision", HeaderText = "生产决定",
            DataSource = new[] { "按自动证据", "排除这些点", "恢复这些点" } });
        grid.Columns["Time"]!.Width = 215; grid.Columns["Evidence"]!.Width = 470;
        grid.Columns["Points"]!.Width = 60;
        grid.Columns["Decision"]!.Width = 135;
        grid.Columns["Decision"]!.DisplayIndex = 1;
        grid.Columns["Time"]!.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        grid.Columns["Evidence"]!.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        grid.Columns["Evidence"]!.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        foreach (DataGridViewColumn c in grid.Columns) c.ReadOnly = c.Name != "Decision";
        foreach (var a in _audit.ProductionAnomalies)
        {
            int row = grid.Rows.Add($"{ProjectTime.Display(a.StartUtc, _timeZone):MM-dd HH:mm:ss}\n至 {ProjectTime.Display(a.EndUtc, _timeZone):MM-dd HH:mm:ss}",
                a.PointCount, a.Reason + " 当前：" + (a.ProductionDecision switch
                { "USER_RESTORE" => "人工保留", "USER_EXCLUDE" => "人工排除", "AUTO_EXCLUDE" => "自动扣除", _ => "保留待复核" }),
                a.ProductionDecision == "USER_RESTORE" ? "恢复这些点" : a.ProductionDecision == "USER_EXCLUDE" ? "排除这些点" : "按自动证据");
            grid.Rows[row].Tag = a;
        }
        var controls = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        controls.Controls.Add(new Label { AutoSize = true, Text = $"观察窗 {_audit.ProductionAnomalyOptions.ObservationSeconds:0.#}s／最短 {_audit.ProductionAnomalyOptions.MinimumDurationSeconds:0.#}s／半径 {_audit.ProductionAnomalyOptions.HorizontalRadiusM:0.#}m。恢复仅对已纳入航段生效。", Margin = new Padding(5, 10, 5, 5) });
        controls.Controls.Add(UiComponents.Button("应用并重新分析", (_, _) =>
        {
            grid.EndEdit();
            var decisions = grid.Rows.Cast<DataGridViewRow>().Where(r => Convert.ToString(r.Cells["Decision"].Value) != "按自动证据")
                .Select(r => new ProductionAnomalyDecision(((ProductionAnomalyInterval)r.Tag!).AnomalyId,
                    Convert.ToString(r.Cells["Decision"].Value) == "恢复这些点" ? "RESTORE" : "EXCLUDE", "用户在异常区间核对中明确选择")).ToArray();
            _seedRequest = BuildReviewedRequest() with { AnomalyDecisions = decisions };
            dialog.DialogResult = DialogResult.OK;
        }));
        layout.Controls.Add(grid, 0, 0); layout.Controls.Add(controls, 0, 1); dialog.Controls.Add(layout);
        UiTheme.Apply(dialog);
        UiLayout.FitFormToWorkingArea(dialog);
        if (dialog.ShowDialog(this) == DialogResult.OK) RefreshReviewedExecution();
    }

    private void ConfigureLegSelectionGrid(DataGridView grid)
    {
        ConfigureGrid(grid);
        grid.ReadOnly = false;
        grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Category", HeaderText = "类别", Width = 85, ReadOnly = true });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Line", HeaderText = "测线", Width = 65, ReadOnly = true });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Time", HeaderText = "飞行时间", Width = 145, ReadOnly = true, Visible = false });
        grid.Columns["Time"]!.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Direction", HeaderText = "方向", Width = 110, ReadOnly = true });
        grid.Columns["Direction"]!.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Coverage", HeaderText = "覆盖", Width = 75, ReadOnly = true });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Length", HeaderText = "长度(m)", Width = 80, ReadOnly = true });
        grid.Columns.Add(new DataGridViewComboBoxColumn { Name = "ManualTask", HeaderText = "指定所属测线", Width = 125, FlatStyle = FlatStyle.Flat, Visible = false });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Evidence", HeaderText = "说明", Width = 260, ReadOnly = true, Visible = false });
        RestoreColumnPreferences(grid);
        foreach (DataGridViewColumn column in grid.Columns)
            column.SortMode = column.Name == "ManualTask" ? DataGridViewColumnSortMode.NotSortable : DataGridViewColumnSortMode.Programmatic;
        grid.ColumnHeaderMouseClick += (_, e) =>
        {
            int index = grid.Columns[e.ColumnIndex].Name switch
            { "Category" => 0, "Line" => 1, "Time" => 2, "Coverage" => 3, "Direction" => 4, "Length" => 5, _ => -1 };
            if (index < 0) return;
            if (_legSort.SelectedIndex == index) _legSortDescending.Checked = !_legSortDescending.Checked;
            else { _legSortDescending.Checked = false; _legSort.SelectedIndex = index; }
        };
        grid.SelectionChanged += (_, _) => SelectLegViewRows(grid);
        grid.CellValueChanged += (_, e) =>
        {
            if (_refreshingLegViews || e.RowIndex < 0 || e.ColumnIndex < 0 || grid.Columns[e.ColumnIndex].Name != "ManualTask") return;
            if (grid.Rows[e.RowIndex].Tag is not ReviewLegRow value) return;
            DataGridViewRow? master = FindLegDecisionRow(value.Leg.FlightLegId);
            if (master is not null) master.Cells["ManualTask"].Value = grid.Rows[e.RowIndex].Cells[e.ColumnIndex].Value;
        };
    }

    private DataGridViewRow? FindLegDecisionRow(string id) => _legGrid.Rows.Cast<DataGridViewRow>()
        .FirstOrDefault(r => r.Tag is ReviewLegRow value && value.Leg.FlightLegId.Equals(id, StringComparison.OrdinalIgnoreCase));

    private int LegCategoryOrder(ReviewLegRow value) => value.Pass is null && value.Leg.MotionKind.Length > 0 ? 7 : value.Leg.RouteRole == "TRANSIT" ? 6 : value.Leg.RouteRole == "CONNECTOR" ? 4 : value.Pass is not null
        ? ReverseNeedsReview(value) ? 5 : value.Leg.ReviewReason.Contains("REVIEW") ? 1 : 0
        : value.Leg.Classification == "SURVEY_CANDIDATE" ? 1 : value.Leg.Classification == "UNRESOLVED" ? 2 : 3;

    private bool MatchesLegFilter(ReviewLegRow value) => _pendingLegFilter.SelectedIndex switch
    {
        1 => value.Pass is not null,
        2 => value.Pass is null,
        3 => value.Pass?.ExecutionDirection == "REVERSE" && !value.AxisOnlyDirection,
        4 => value.Pass?.FlightQuality is { Warnings.Count: > 0 },
        5 => value.Leg.RouteRole == "TRANSIT",
        6 => value.Leg.RouteRole == "CONNECTOR",
        _ => true
    };

    private int CompareLegRows(DataGridViewRow left, DataGridViewRow right)
    {
        var a = (ReviewLegRow)left.Tag!; var b = (ReviewLegRow)right.Tag!;
        string LineName(ReviewLegRow value) => _tasks.FirstOrDefault(t =>
            t.SurveyTaskStableId == value.Pass?.SurveyTaskStableId)?.LineName ?? "\uffff";
        double Direction(ReviewLegRow value)
        {
            var first = value.Leg.Samples.FirstOrDefault(); var last = value.Leg.Samples.LastOrDefault();
            return first is null || last is null ? -1 : SurveyDirection.Bearing(
                new(first.Longitude, first.Latitude), new(last.Longitude, last.Latitude)) ?? -1;
        }
        int comparison = _legSort.SelectedIndex switch
        {
            1 => CompareNaturalLineNames(LineName(a), LineName(b)),
            2 => a.Leg.StartUtc.CompareTo(b.Leg.StartUtc),
            3 => (a.Pass?.PlannedCoverage ?? -1).CompareTo(b.Pass?.PlannedCoverage ?? -1),
            4 => Direction(a).CompareTo(Direction(b)),
            5 => LegLength(a).CompareTo(LegLength(b)),
            _ => LegCategoryOrder(a).CompareTo(LegCategoryOrder(b))
        };
        if (comparison != 0) return _legSortDescending.Checked ? -comparison : comparison;
        if (_legSort.SelectedIndex == 0)
        {
            comparison = CompareNaturalLineNames(LineName(a), LineName(b));
            if (comparison != 0) return comparison;
        }
        comparison = a.Leg.StartUtc.CompareTo(b.Leg.StartUtc);
        return comparison != 0 ? comparison : StringComparer.Ordinal.Compare(a.Leg.FlightLegId, b.Leg.FlightLegId);
    }

    private static int CompareNaturalLineNames(string left, string right)
    {
        string[] a = System.Text.RegularExpressions.Regex.Split(left, "(\\d+)");
        string[] b = System.Text.RegularExpressions.Regex.Split(right, "(\\d+)");
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            string x = a[i], y = b[i];
            int comparison;
            if (x.Length > 0 && y.Length > 0 && char.IsDigit(x[0]) && char.IsDigit(y[0]))
            {
                x = x.TrimStart('0'); y = y.TrimStart('0');
                comparison = x.Length.CompareTo(y.Length);
                if (comparison == 0) comparison = StringComparer.Ordinal.Compare(x, y);
            }
            else comparison = StringComparer.OrdinalIgnoreCase.Compare(x, y);
            if (comparison != 0) return comparison;
        }
        return a.Length.CompareTo(b.Length);
    }

    private void RefreshLegSelectionViews()
    {
        if (_refreshingLegViews || _populatingLegGrid || _pendingLegGrid.Columns.Count == 0) return;
        _legGrid.EndEdit();
        var selected = _legGrid.SelectedRows.Cast<DataGridViewRow>().Where(r => r.Tag is ReviewLegRow)
            .Select(r => ((ReviewLegRow)r.Tag!).Leg.FlightLegId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rows = _legGrid.Rows.Cast<DataGridViewRow>().Where(r => r.Tag is ReviewLegRow).ToList();
        var currentLegs = rows.Select(r => ((ReviewLegRow)r.Tag!).Leg).ToHashSet(ReferenceEqualityComparer.Instance);
        foreach (var old in _legLengths.Keys.Where(k => !currentLegs.Contains(k)).ToArray()) _legLengths.Remove(old);
        rows.Sort(CompareLegRows);
        _refreshingLegViews = true;
        try
        {
            _pendingLegGrid.Rows.Clear(); _acceptedLegGrid.Rows.Clear();
            foreach (DataGridViewRow master in rows)
            {
                var value = (ReviewLegRow)master.Tag!;
                bool accepted = IsAcceptedLegRow(master);
                if (!accepted && !MatchesLegFilter(value)) continue;
                DataGridView grid = accepted ? _acceptedLegGrid : _pendingLegGrid;
                DataGridViewRow row = grid.Rows[grid.Rows.Add()];
                row.Tag = value;
                row.Cells["Category"].Value = accepted && value.Pass is null ? "手动纳入·归属待判定" :
                    value.Pass is null && value.Leg.MotionKind == "STATIONARY_VERTICAL" ? "原地升降·需复核" :
                    value.Pass is null && value.Leg.MotionKind == "STATIONARY" ? "停留段·需复核" :
                    value.Leg.RouteRole == "TRANSIT" ? "往返·局部命中待复核" :
                    value.Leg.RouteRole == "CONNECTOR" ? "连接段·需复核" : value.Leg.TransitEvidence.Count > 0 ? "疑似往返·需复核" : value.Pass is not null
                    ? value.Pass.MatchMethod == "ACTUAL_TRACK" ? value.Leg.ReviewReason.Contains("REVIEW") ? "实测段·需复核" :
                      value.AxisOnlyDirection ? "实测作业候选" : value.Pass.ExecutionDirection == "REVERSE" ? "反向实测候选" : "实测作业候选" :
                      value.Pass.ExecutionDirection == "REVERSE" ? ReverseNeedsReview(value) ? "反向段·默认不纳入" : "反向参考线命中" : "正向命中"
                    : value.Leg.LocalHitCandidates.Count > 0 ? "未命中·局部命中候选" : Convert.ToString(master.Cells[0].Value);
                row.Cells["Category"].ToolTipText = Convert.ToString(row.Cells["Category"].Value);
                row.Cells["Line"].Value = value.Pass is null ? value.Leg.FlightLegId : master.Cells[1].Value;
                row.Cells["Line"].ToolTipText = value.Leg.FlightLegId;
                row.Cells["Length"].Value = LegLength(value).ToString("0", CultureInfo.InvariantCulture);
                row.Cells["Time"].Value = master.Cells[3].Value;
                row.Cells["Time"].ToolTipText = master.Cells[3].ToolTipText;
                row.Cells["Direction"].Value = master.Cells["Direction"].Value;
                row.Cells["Direction"].ToolTipText = Convert.ToString(master.Cells["Direction"].Value) + "; " + master.Cells["Direction"].ToolTipText;
                row.Cells["Coverage"].Value = value.Pass?.PlannedCoverage.ToString("P0", CultureInfo.InvariantCulture) ?? "—";
                string evidence = Convert.ToString(master.Cells[8].Value) ?? "";
                row.Cells["Evidence"].Value = accepted ? evidence.Replace("需确认归属", "已确认纳入") : evidence;
                row.Cells["Evidence"].ToolTipText = master.Cells[8].ToolTipText + "；质量检查：" + master.Cells["QC"].Value;
                var combo = (DataGridViewComboBoxCell)row.Cells["ManualTask"];
                combo.Items.AddRange(value.TaskIdByOption.Keys.Cast<object>().ToArray());
                combo.Value = master.Cells["ManualTask"].Value;
                row.DefaultCellStyle.BackColor = accepted ? Color.FromArgb(232, 246, 236) :
                    value.Pass is null ? Color.FromArgb(249, 242, 229) : SystemColors.Window;
            }
            foreach (DataGridView grid in new[] { _pendingLegGrid, _acceptedLegGrid })
            {
                // Setting CurrentCell can collapse a multi-selection. Restore it first,
                // then project every selected identity without changing the master store.
                var current = grid.Rows.Cast<DataGridViewRow>().FirstOrDefault(r =>
                    selected.Contains(((ReviewLegRow)r.Tag!).Leg.FlightLegId));
                grid.CurrentCell = current?.Cells[0];
                grid.ClearSelection();
                foreach (DataGridViewRow row in grid.Rows)
                    row.Selected = selected.Contains(((ReviewLegRow)row.Tag!).Leg.FlightLegId);
                string columnName = _legSort.SelectedIndex switch
                    { 1 => "Line", 2 => "Time", 3 => "Coverage", 4 => "Direction", _ => "Category" };
                grid.Columns[columnName]!.HeaderCell.SortGlyphDirection = _legSortDescending.Checked ? SortOrder.Descending : SortOrder.Ascending;
            }
            _pendingLegCount.Text = $"待选航段：显示 {_pendingLegGrid.Rows.Count} / 共 {rows.Count(r => !IsAcceptedLegRow(r))}";
            _acceptedLegCount.Text = $"已纳入处理：{_acceptedLegGrid.Rows.Count} 段";
        }
        finally { _refreshingLegViews = false; }
        if (_legGrid.CurrentRow is null)
        {
            _map.SetData(_displayAnalysis, _tasks, _audit?.Execution, null, acceptedLegIds: CurrentAcceptedLegIds());
            _timeline.SetExecution(_audit?.Execution, null, _timeZone);
        }
        UpdateSelectedLeg();
    }

    private void SelectLegViewRows(DataGridView grid)
    {
        if (_refreshingLegViews || _populatingLegGrid) return;
        var selected = grid.SelectedRows.Cast<DataGridViewRow>().Select(r => r.Tag).OfType<ReviewLegRow>()
            .Select(r => r.Leg.FlightLegId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var current = grid.CurrentRow?.Selected is true ? grid.CurrentRow.Tag as ReviewLegRow :
            grid.SelectedRows.Cast<DataGridViewRow>().Select(r => r.Tag).OfType<ReviewLegRow>().FirstOrDefault();
        _refreshingLegViews = true;
        try
        {
            (grid == _pendingLegGrid ? _acceptedLegGrid : _pendingLegGrid).ClearSelection();
            DataGridViewRow? masterCurrent = current is null ? null : FindLegDecisionRow(current.Leg.FlightLegId);
            _legGrid.CurrentCell = masterCurrent?.Cells[0];
            _legGrid.ClearSelection();
            foreach (DataGridViewRow row in _legGrid.Rows)
                row.Selected = row.Tag is ReviewLegRow value && selected.Contains(value.Leg.FlightLegId);
        }
        finally { _refreshingLegViews = false; }
        if (current is null)
        {
            _map.SetData(_displayAnalysis, _tasks, _audit?.Execution, null, acceptedLegIds: CurrentAcceptedLegIds());
            _timeline.SetExecution(_audit?.Execution, null, _timeZone);
        }
        UpdateSelectedLeg();
    }

    private IReadOnlySet<string> CurrentAcceptedLegIds() => _reviewDecisions.AcceptedLegIds();

    private ContextMenuStrip? _previewLegMenu;

    private void LocatePreviewLegs(IReadOnlyList<string> ids, Point point)
    {
        if (_previewLegMenu is { IsDisposed: false } priorMenu)
        {
            if (priorMenu.Visible) priorMenu.Close();
            DisposeMenuAfterClose(priorMenu, _map);
            _previewLegMenu = null;
        }
        var rows = ids.Select(FindLegDecisionRow).Where(r => r is not null).Cast<DataGridViewRow>().ToArray();
        if (rows.Length == 1) { LocatePreviewLeg(((ReviewLegRow)rows[0].Tag!).Leg.FlightLegId); return; }
        if (rows.Length == 0) return;
        var menu = new ContextMenuStrip();
        _previewLegMenu = menu;
        menu.Items.Add(new ToolStripMenuItem($"此处有 {rows.Length} 个航段候选") { Enabled = false });
        foreach (var row in rows)
        {
            var value = (ReviewLegRow)row.Tag!;
            string label = $"{row.Cells[1].Value} · {ProjectTime.Display(value.Leg.StartUtc, _timeZone):MM-dd HH:mm:ss}–{ProjectTime.Display(value.Leg.EndUtc, _timeZone):HH:mm:ss} · {row.Cells["Direction"].Value} · {(IsAcceptedLegRow(row) ? "已纳入" : "待选")}";
            menu.Items.Add(label, null, (_, _) => LocatePreviewLeg(value.Leg.FlightLegId));
        }
        menu.Closed += (_, _) => DisposeMenuAfterClose(menu, _map);
        menu.Show(_map, point);
    }

    private void LocatePreviewLeg(string id)
    {
        var master = FindLegDecisionRow(id);
        if (master?.Tag is not ReviewLegRow value) return;
        bool hidden = !IsAcceptedLegRow(master) && !MatchesLegFilter(value);
        if (hidden) _pendingLegFilter.SelectedIndex = 0;
        var grid = IsAcceptedLegRow(master) ? _acceptedLegGrid : _pendingLegGrid;
        var target = grid.Rows.Cast<DataGridViewRow>().FirstOrDefault(r => r.Tag is ReviewLegRow v && v.Leg.FlightLegId == id);
        if (target is null) return;
        _refreshingLegViews = true;
        try
        {
            foreach (var list in new[] { _pendingLegGrid, _acceptedLegGrid, _legGrid }) list.ClearSelection();
            grid.CurrentCell = target.Cells[0]; target.Selected = true;
            _legGrid.CurrentCell = master.Cells[0]; master.Selected = true;
            if (grid.Visible && grid.DisplayRectangle.Height > grid.ColumnHeadersHeight + target.Height)
                grid.FirstDisplayedScrollingRowIndex = target.Index;
        }
        finally { _refreshingLegViews = false; }
        var window = _windowGrid.Rows.Cast<DataGridViewRow>().FirstOrDefault(r => r.Tag is WindowDraft w && w.Id == value.Leg.MissionExecutionId);
        if (window is not null)
        {
            _windowGrid.ClearSelection(); _windowGrid.CurrentCell = window.Cells[0]; window.Selected = true;
            if (_windowGrid.Visible && _windowGrid.DisplayedRowCount(false) > 0)
                _windowGrid.FirstDisplayedScrollingRowIndex = window.Index;
        }
        UpdateSelectedLeg();
        _status.Text = "已定位预览航段；纳入状态保持。" + (hidden ? "左侧筛选已切换为全部。" : "");
    }

    private void MoveLegRows(IEnumerable<DataGridViewRow> sourceRows, bool accept)
    {
        // Capture identities before committing combo edits, which can rebuild the views.
        var values = sourceRows.Select(r => r.Tag).OfType<ReviewLegRow>().ToArray();
        RecordMotherLineInclusion(values.Select(v => v.Leg.FlightLegId), accept);
        _pendingLegGrid.EndEdit(); _acceptedLegGrid.EndEdit(); _legGrid.EndEdit();
        int changed = 0, unmatched = 0;
        _populatingLegGrid = true;
        try
        {
            _reviewDecisions.Move(values.Where(v => FindLegDecisionRow(v.Leg.FlightLegId) is not null)
                .Select(v => (v.Leg.FlightLegId, v.Pass is not null)), accept);
            foreach (ReviewLegRow value in values)
            {
                DataGridViewRow? master = FindLegDecisionRow(value.Leg.FlightLegId);
                if (master is null) continue;
                bool hasIdentity = master.Tag is ReviewLegRow { Pass: not null };
                ProjectLegDecision(master);
                if (accept && !hasIdentity) unmatched++;
                // Quality status is deliberately independent of explicit preprocessing inclusion.
                changed++;
            }
        }
        finally { _populatingLegGrid = false; }
        if (changed > 0) RefreshReviewedExecution();
        else RefreshLegSelectionViews();
        if (_spatialOnly && _audit is not null)
            unmatched = values.Count(v => _audit.Execution.Legs.Any(l => l.FlightLegId == v.Leg.FlightLegId && l.SurveyTaskStableId is null));
        _status.Text = accept
            ? $"已添加 {changed} 段到右侧；质量状态保留。" + (unmatched > 0 ? $"其中 {unmatched} 个未归属航段按整段纳入，导出使用独立航段编号；测线归属仍待判定。" : "")
            : $"已将 {changed} 段移回左侧，不再参与本次处理；原始数据和质量记录保留。";
        if (_spatialOnly) _status.Text += MotherLineRestorationSummary();
        UpdateSelectedLeg();
    }

    private static bool CanBatchAddLeg(ReviewLegRow value) =>
        SurveyCandidatePolicy.CanSuggest(value.Leg, value.Pass, value.AxisOnlyDirection);

    private void AddWindowMatchedLegs()
    {
        var ids = SelectedWindows().Select(w => w.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        MoveLegRows(_legGrid.Rows.Cast<DataGridViewRow>().Where(r => r.Tag is ReviewLegRow value &&
            CanBatchAddLeg(value) &&
            ids.Contains(value.Leg.MissionExecutionId)), true);
    }
}
