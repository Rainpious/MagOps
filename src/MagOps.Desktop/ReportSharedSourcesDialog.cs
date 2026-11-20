using MagOps.Reporting;

namespace MagOps.Desktop;

internal sealed class ReportSharedSourcesDialog : Form
{
    private readonly DataGridView _table = new()
    {
        Dock = DockStyle.Fill, AutoGenerateColumns = false, AllowUserToAddRows = false,
        AllowUserToDeleteRows = false, RowHeadersVisible = false, AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells
    };
    private readonly Dictionary<string, ReportSharedItem> _previous;
    private readonly HashSet<string> _ambiguous;
    public ReportSharedItem[] Selection { get; private set; } = [];

    public ReportSharedSourcesDialog(List<ReportSharedItem> live, ReportSharedItem[] saved)
    {
        Text = "引用共用资料";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(1120, 670);
        _previous = saved.ToDictionary(s => s.Key);
        _ambiguous = live.GroupBy(s => s.Key).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        _table.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Use", HeaderText = "采用", Width = 48 });
        void Column(string name, string title, int width) => _table.Columns.Add(new DataGridViewTextBoxColumn
            { Name = name, HeaderText = title, Width = width, ReadOnly = true, DefaultCellStyle = new() { WrapMode = DataGridViewTriState.True } });
        Column("Item", "批次 / 记录", 145);
        Column("State", "核对事项", 145);
        Column("Previous", "已采用", 185);
        Column("Current", "来源现值", 255);
        var choice = new DataGridViewComboBoxColumn { Name = "Decision", HeaderText = "取值", Width = 105 };
        choice.Items.AddRange("采用来源", "保留原值", "请选择");
        _table.Columns.Add(choice);
        Column("Origin", "来源", 155);
        var keys = live.Select(s => s.Key).ToHashSet();
        foreach (var item in live.Concat(saved.Where(s => !keys.Contains(s.Key))))
        {
            _previous.TryGetValue(item.Key, out var old);
            bool missing = !keys.Contains(item.Key);
            bool changed = old != null && ((old.ReviewedValue ?? old.Value) != item.Value || old.Label != item.Label || old.RetainedAfterLoss != missing);
            string state = missing ? "来源失效，可确认保留原值" :
                _ambiguous.Contains(item.Key) ? "记录身份重复，需核对台账" : changed ? "来源已更新" : item.Conflict;
            string decision = changed ? "请选择" : old?.ReviewedValue != null || old?.RetainedAfterLoss == true ? "保留原值" : "采用来源";
            int ix = _table.Rows.Add(old != null, item.Label, state, old?.Value ?? "", missing ? "" : item.Value, decision, item.Source);
            _table.Rows[ix].Tag = item;
        }
        var ok = new Button { Text = "应用到报告", AutoSize = true };
        ok.Click += (_, _) => Apply(keys);
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel };
        CancelButton = cancel;
        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        footer.Controls.Add(cancel); footer.Controls.Add(ok);
        Controls.Add(_table); Controls.Add(footer);
        Controls.Add(new Label { Dock = DockStyle.Top, Height = 35, TextAlign = ContentAlignment.MiddleLeft,
            Text = "勾选本报告采用的批次和相关资料。设备、基站和测试随所属批次引用。修改取值后点击“应用到报告”。" });
        _table.CellValueChanged += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex != 0) return;
            var row = _table.Rows[e.RowIndex];
            var parent = (ReportSharedItem)row.Tag!;
            bool use = Equals(row.Cells["Use"].Value, true);
            foreach (DataGridViewRow child in _table.Rows)
                if (child.Tag is ReportSharedItem item && item.Parent == parent.Key &&
                    (!use || item.Conflict.Length == 0 && !_ambiguous.Contains(item.Key)))
                    child.Cells["Use"].Value = use;
        };
        _table.CurrentCellDirtyStateChanged += (_, _) => { if (_table.IsCurrentCellDirty) _table.CommitEdit(DataGridViewDataErrorContexts.Commit); };
    }

    private void Apply(HashSet<string> liveKeys)
    {
        _table.EndEdit();
        var selected = new List<ReportSharedItem>();
        foreach (DataGridViewRow row in _table.Rows)
        {
            if (!Equals(row.Cells["Use"].Value, true)) continue;
            var item = (ReportSharedItem)row.Tag!;
            string decision = Convert.ToString(row.Cells["Decision"].Value) ?? "";
            if ((!liveKeys.Contains(item.Key) && decision != "保留原值") || _ambiguous.Contains(item.Key) || decision == "请选择")
            { Alert("请处理“" + item.Label + "”的来源或取值。"); return; }
            if (decision == "保留原值")
            {
                if (!_previous.TryGetValue(item.Key, out var old)) { Alert("这条记录还没有已采用的值。"); return; }
                item = item with { ReviewedValue = item.Value, Value = old.Value, RetainedAfterLoss = !liveKeys.Contains(item.Key) };
            }
            selected.Add(item);
        }
        var keys = selected.Select(i => i.Key).ToHashSet();
        var orphan = selected.FirstOrDefault(i => i.Parent.Length > 0 && !keys.Contains(i.Parent));
        if (orphan != null) { Alert("请同时勾选所属批次或设备：" + orphan.Label); return; }
        var duplicate = selected.Where(i => i.Exclusive.Length > 0).GroupBy(i => i.Exclusive).FirstOrDefault(g => g.Count() > 1);
        if (duplicate != null) { Alert("同一批次、同一设备角色请选择一台：" + duplicate.First().Label); return; }
        Selection = selected.ToArray(); DialogResult = DialogResult.OK;
    }

    private void Alert(string message) => MessageBox.Show(this, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
}
