using MagOps.Core.Routes;

namespace MagOps.Desktop;

internal sealed class RouteEditorDialog : Form
{
    private readonly RouteSortie _sortie;
    private readonly IReadOnlyList<RouteEndpoint> _allEndpoints;
    private readonly DataGridView _grid = GridFactory.Create();

    internal bool WasEdited { get; private set; }
    internal bool ReverseParityChanged { get; private set; }

    public RouteEditorDialog(
        IWin32Window owner,
        RouteSortie sortie,
        IReadOnlyList<RouteEndpoint> allEndpoints)
    {
        _sortie = sortie;
        _allEndpoints = allEndpoints;

        Text = "编辑 " + sortie.Name;
        Width = 920;
        Height = 650;
        MinimumSize = new Size(700, 450);
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            WrapContents = true,
            AutoScroll = false,
            Padding = new Padding(6)
        };
        buttons.Controls.Add(MakeButton("上移", (_, _) => MovePoint(-1)));
        buttons.Controls.Add(MakeButton("下移", (_, _) => MovePoint(1)));
        buttons.Controls.Add(MakeButton("删除", (_, _) => Delete()));
        buttons.Controls.Add(MakeButton("添加端点", (_, _) => AddEndpoint()));
        buttons.Controls.Add(MakeButton("更换测线方向", (_, _) => Reverse()));
        buttons.Controls.Add(MakeButton("关闭", (_, _) => Close()));

        Controls.Add(_grid);
        Controls.Add(buttons);
        Shown += (_, _) =>
        {
            UiLayout.SizeDialogToOwner(this, owner, 0.72, 0.72, new Size(780, 520), new Size(1250, 850));
            UiLayout.Normalize(this);
        };
        DpiChanged += (_, _) => BeginInvoke((Action)(() => UiLayout.Normalize(this)));
        RefreshGrid();
    }

    private void RefreshGrid(int select = -1)
    {
        _grid.DataSource = null;
        _grid.DataSource = _sortie.Points.Select((p, i) => new RoutePointViewRow
        {
            Sequence = i + 1,
            PointId = p.Pid,
            Line = p.Line,
            Side = p.Endpoint,
            X = p.X,
            Y = p.Y,
            SourceLabel = p.Label
        }).ToList();

        GridHeaders.Apply(_grid, new Dictionary<string, string>
        {
            [nameof(RoutePointViewRow.Sequence)] = "#",
            [nameof(RoutePointViewRow.PointId)] = "Point ID",
            [nameof(RoutePointViewRow.Line)] = "Line",
            [nameof(RoutePointViewRow.Side)] = "Side",
            [nameof(RoutePointViewRow.X)] = "X / Lon",
            [nameof(RoutePointViewRow.Y)] = "Y / Lat",
            [nameof(RoutePointViewRow.SourceLabel)] = "原始点名"
        });

        if (_grid.Rows.Count > 0)
        {
            int row = select >= 0 ? Math.Min(select, _grid.Rows.Count - 1) : 0;
            _grid.CurrentCell = _grid.Rows[row].Cells[0];
        }
    }

    private int CurrentIndex => _grid.CurrentRow?.Index ?? -1;

    private void MovePoint(int delta)
    {
        int i = CurrentIndex;
        int j = i + delta;
        if (i < 0 || j < 0 || j >= _sortie.Points.Count) return;
        (_sortie.Points[i], _sortie.Points[j]) = (_sortie.Points[j], _sortie.Points[i]);
        MarkEdited();
        RefreshGrid(j);
    }

    private void Delete()
    {
        int i = CurrentIndex;
        if (i < 0) return;
        if (_sortie.Points.Count <= 2)
        {
            MessageBox.Show(this, "航线至少需要 2 个点。", "不能删除",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _sortie.Points.RemoveAt(i);
        MarkEdited();
        RefreshGrid(Math.Min(i, _sortie.Points.Count - 1));
    }

    private void AddEndpoint()
    {
        using var dialog = new Form
        {
            Text = "添加空间端点",
            Width = 720,
            Height = 190,
            MinimumSize = new Size(620, 170),
            StartPosition = FormStartPosition.CenterParent,
            AutoScaleMode = AutoScaleMode.Dpi,
            MinimizeBox = false,
            MaximizeBox = false
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(14)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var combo = new ComboBox
        {
            Dock = DockStyle.Top,
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        foreach (RouteEndpoint p in _allEndpoints)
            combo.Items.Add(p.DisplayLabel);
        if (combo.Items.Count > 0)
            combo.SelectedIndex = 0;
        layout.Controls.Add(combo, 0, 0);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = true,
            AutoScroll = false
        };
        var cancel = new Button
        {
            Text = "取消",
            AutoSize = true,
            DialogResult = DialogResult.Cancel
        };
        var ok = new Button
        {
            Text = "插入到当前点之后",
            AutoSize = true
        };
        ok.Click += (_, _) =>
        {
            if (combo.SelectedIndex < 0)
                return;
            RouteEndpoint p = _allEndpoints[combo.SelectedIndex];
            int pos = CurrentIndex < 0 ? _sortie.Points.Count : CurrentIndex + 1;
            _sortie.Points.Insert(pos, p);
            MarkEdited();
            dialog.DialogResult = DialogResult.OK;
            dialog.Close();
        };
        actions.Controls.Add(cancel);
        actions.Controls.Add(ok);
        layout.Controls.Add(actions, 0, 1);

        dialog.Controls.Add(layout);
        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;
        dialog.Shown += (_, _) => UiLayout.Normalize(dialog);
        dialog.DpiChanged += (_, _) => dialog.BeginInvoke((Action)(() => UiLayout.Normalize(dialog)));

        if (dialog.ShowDialog(this) == DialogResult.OK)
            RefreshGrid();
    }

    private void Reverse()
    {
        for (int i = 0; i + 1 < _sortie.Points.Count; i += 2)
            (_sortie.Points[i], _sortie.Points[i + 1]) =
                (_sortie.Points[i + 1], _sortie.Points[i]);
        _sortie.SourceLines = [];
        ReverseParityChanged = !ReverseParityChanged;
        RefreshGrid();
    }

    private void MarkEdited()
    {
        _sortie.SourceLines = [];
        WasEdited = true;
    }

    private static Button MakeButton(string text, EventHandler click)
    {
        var button = new Button { Text = text, Width = 105, Height = 30 };
        button.Click += click;
        return button;
    }
}

internal sealed class RoutePointViewRow
{
    public int Sequence { get; set; }
    public string PointId { get; set; } = "";
    public string Line { get; set; } = "";
    public int Side { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public string SourceLabel { get; set; } = "";
}
