using MagOps.Infrastructure.Services;

namespace MagOps.Desktop;

/// <summary>Explicit lineage choice. No nearest-line or name-based automatic binding.</summary>
internal sealed class MotherLineCsvBindingDialog : Form
{
    private readonly DataGridView _grid = new() { Dock = DockStyle.Fill, AllowUserToAddRows = false,
        AllowUserToDeleteRows = false, RowHeadersVisible = false, AutoGenerateColumns = false };
    private readonly MotherLineCsvInput _input;
    private readonly Dictionary<string, (string Partition, string Mother)> _targets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MotherLineCsvInputRow[]> _rows;
    internal MotherLineCsvBindingDialog(MotherLineCsvInput input, PartitionMotherLineIdentityCatalog catalog)
    {
        _input = input;
        _rows = input.Rows.GroupBy(r => r.GroupKey).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        Text = "核对已有分线CSV与母线对应"; StartPosition = FormStartPosition.CenterParent;
        Size = new Size(1000, 620); MinimumSize = new Size(760, 460);
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = UiTheme.PagePadding };
        root.ColumnStyles.Add(new(SizeType.Percent, 100));
        root.RowStyles.Add(new(SizeType.AutoSize)); root.RowStyles.Add(new(SizeType.Percent, 100)); root.RowStyles.Add(new(SizeType.AutoSize));
        var description = new Label { AutoSize = true, Dock = DockStyle.Fill, Text =
            $"文件：{input.Path}\r\n{input.Rows.Count:N0}条记录，{input.Groups.Count:N0}组。逐组选择分区/母线；留空组进入未归属列表，不导出。\r\n" +
            $"当前设计：{catalog.DesignId}；{catalog.Datum} / 中央经线{catalog.CentralMeridian}°。\r\n" +
            "仅接受完整20列处理CSV。原始血缘、原项目/设计及审核资格未验证；路径草稿标记不能证明审核状态。请核对实际轨迹和母线；逐Pass勾选质量重复，单独输出。" };
        root.Controls.Add(description, 0, 0);
        root.SizeChanged += (_, _) => description.MaximumSize = new Size(Math.Max(1, root.ClientSize.Width - root.Padding.Horizontal - 6), 0);
        foreach (var (name, title, width) in new[] { ("Line", "原测线名", 150), ("Task", "原任务ID", 190),
            ("Order", "原顺序", 75), ("Count", "记录数", 80), ("LinePass", "LinePass", 85) })
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = title, Width = width, ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable });
        var target = new DataGridViewComboBoxColumn { Name = "Target", HeaderText = "明确对应的分区 / 母线", Width = 260,
            SortMode = DataGridViewColumnSortMode.NotSortable, FlatStyle = FlatStyle.Flat };
        target.Items.Add("待绑定");
        foreach (var partition in catalog.Partitions)
        foreach (var line in partition.Lines)
        {
            string label = $"{partition.PartitionCode} / {line.LineName} [{line.MotherStableId}]";
            target.Items.Add(label); _targets.Add(label, (partition.PartitionCode, line.MotherStableId));
        }
        _grid.Columns.Add(target);
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Repeat", HeaderText = "质量重复线", Width = 95 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Distance", HeaderText = "线段距 中位/P95(m)", Width = 230,
            ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable });
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        foreach (DataGridViewColumn column in _grid.Columns)
        {
            column.FillWeight = column.Width;
            column.MinimumWidth = column.Name switch { "Target" => 185, "Distance" => 175, "LinePass" => 85, "Task" => 90, "Line" => 85, _ => 55 };
        }
        _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        _grid.AccessibleName = "已有分线CSV显式母线对应";
        foreach (var group in input.Groups)
            _grid.Rows.Add(group.LineName, group.LineTaskId, group.LineOrder, group.Records, group.LinePass, "待绑定", false);
        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        { if (_grid.IsCurrentCellDirty) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        _grid.CellValueChanged += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex != _grid.Columns["Target"]!.Index) return;
            var row = _grid.Rows[e.RowIndex];
            if (!_targets.TryGetValue(Convert.ToString(row.Cells["Target"].Value) ?? "", out var selected))
            { row.Cells["Distance"].Value = "未绑定"; return; }
            var mother = catalog.Mothers.Single(m => m.StableId == selected.Mother);
            var points = _rows[input.Groups[e.RowIndex].Key];
            int count = Math.Min(101, points.Length);
            var distances = Enumerable.Range(0, count).Select(i =>
            {
                var point = points[count == 1 ? 0 : (int)((long)i * (points.Length - 1) / (count - 1))];
                var xy = MagOps.Core.Coordinates.CoordinateService.GaussKrugerForward(
                    point.Longitude, point.Latitude, catalog.CentralMeridian, catalog.Datum);
                return mother.Segments.Min(s =>
                {
                    double dx = s.End.X - s.Start.X, dy = s.End.Y - s.Start.Y;
                    double t = Math.Clamp(((xy.Easting - s.Start.X) * dx + (xy.Northing - s.Start.Y) * dy) / (dx * dx + dy * dy), 0, 1);
                    return Math.Sqrt(Math.Pow(xy.Easting - s.Start.X - t * dx, 2) + Math.Pow(xy.Northing - s.Start.Y - t * dy, 2));
                });
            }).Order().ToArray();
            row.Cells["Distance"].Value = $"{distances[count / 2]:0.0} / {distances[(int)Math.Ceiling(.95 * count) - 1]:0.0}";
            row.Cells["Distance"].ToolTipText = $"按记录顺序均匀抽取{count}点，到有限设计线段的水平距离；只供错绑核对，不判合格，也不自动更改对应。";
        };
        root.Controls.Add(_grid, 0, 1);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        var accept = new Button { Text = "按此对应载入候选", AutoSize = true };
        accept.Click += (_, _) =>
        {
            if (ReadBindings().Count == 0) return;
            DialogResult = DialogResult.OK; Close();
        };
        var cancel = new Button { Text = "取消", AutoSize = true, DialogResult = DialogResult.Cancel };
        buttons.Controls.AddRange([accept, cancel]); root.Controls.Add(buttons, 0, 2);
        AcceptButton = accept; CancelButton = cancel; Controls.Add(root);
    }

    internal IReadOnlyList<MotherLineCsvBinding> ReadBindings()
    {
        _grid.EndEdit();
        var result = new List<MotherLineCsvBinding>();
        for (int i = 0; i < _input.Groups.Count; i++)
            if (_targets.TryGetValue(Convert.ToString(_grid.Rows[i].Cells["Target"].Value) ?? "", out var target))
                result.Add(new(_input.Groups[i].Key, target.Partition, target.Mother,
                    Convert.ToBoolean(_grid.Rows[i].Cells["Repeat"].Value)));
        return result;
    }
}
