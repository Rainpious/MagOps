using MagOps.Core.Routes;

namespace MagOps.Desktop;

internal sealed partial class TieLineDesigner
{

    private void EditMotherCuts()
    {
        int index = list.SelectedIndex;
        if (index < 0 || lines[index].Design is not { } design || design.Version != context.Version)
            throw new InvalidOperationException("请先选择当前设计版本的切割母线。");
        var a = design.Approach.Start; var b = design.Departure.End;
        double dx = b.X - a.X, dy = b.Y - a.Y, length = Math.Sqrt(dx * dx + dy * dy);
        double along = (((double)east.Value - a.X) * dx + ((double)north.Value - a.Y) * dy) / length;
        if (length < 2) throw new InvalidOperationException("母线长度不足以切成两个有效执行段。");
        using var dialog = new Form { Text = lines[index].Name + " · 母线切点", ClientSize = new(540, 325), StartPosition = FormStartPosition.CenterParent };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new(8) };
        layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.AutoSize));
        var input = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        input.Controls.Add(new Label { Text = "自起端外延端点量起 m", AutoSize = true });
        var distance = Number(0, (decimal)length, (decimal)Math.Clamp(along, 0, length));
        var add = new Button { Text = "添加切点", AutoSize = true }; input.Controls.Add(distance); input.Controls.Add(add);
        var listCuts = new ListBox { Dock = DockStyle.Fill };
        var fractions = new SortedSet<double>(design.CutFractions);
        void RefreshCuts() { listCuts.Items.Clear(); foreach (var f in fractions) listCuts.Items.Add($"{f * length:F2} m"); }
        add.Click += (_, _) =>
        {
            double f = (double)distance.Value / length;
            if (fractions.Append(0).Append(1).Any(c => Math.Abs(c - f) * length < RouteExecutionSegmentPlanner.MinimumSegmentLengthM))
            { MessageBox.Show(dialog, "切点距端点及相邻切点至少 1 m。"); return; }
            fractions.Add(f); RefreshCuts();
        };
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        var remove = new Button { Text = "移除选中切点", AutoSize = true };
        remove.Click += (_, _) => { if (listCuts.SelectedIndex >= 0) { fractions.Remove(fractions.ElementAt(listCuts.SelectedIndex)); RefreshCuts(); } };
        var ok = new Button { Text = "采用切点", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, AutoSize = true };
        actions.Controls.AddRange([remove, ok, cancel]); layout.Controls.Add(input, 0, 0); layout.Controls.Add(listCuts, 0, 1); layout.Controls.Add(actions, 0, 2);
        dialog.Controls.Add(layout); dialog.CancelButton = cancel; RefreshCuts();
        if (dialog.ShowDialog(this) != DialogResult.OK || fractions.SequenceEqual(design.CutFractions)) return;
        var changed = lines[index] with { Design = design with { CutFractions = fractions.ToArray() } };
        TieFlightPlanner.Build(lines.Select((l, i) => i == index ? changed : l), context.Version, context.MaxSortieM, context.Bearing);
        lines[index] = changed; Changed = true; RefreshLists(); list.SelectedIndex = index;
    }

    private void FlightDetails()
    {
        var active = lines.Where(l => l.Design?.Version == context.Version).ToList();
        if (active.Count == 0) throw new InvalidOperationException("请先绘制切割线。");
        using var dialog = new Form
        {
            Text = "架次与续航",
            ClientSize = new(870, 510),
            MinimumSize = new(740, 400),
            StartPosition = FormStartPosition.CenterParent
        };
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new(8) };
        panel.RowStyles.Add(new(SizeType.AutoSize)); panel.RowStyles.Add(new(SizeType.AutoSize));
        panel.RowStyles.Add(new(SizeType.Percent, 100)); panel.RowStyles.Add(new(SizeType.AutoSize));
        var inputs = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        panel.Controls.Add(inputs, 0, 0);
        void Input(string label, Control field)
        {
            inputs.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new(3, 8, 0, 2) });
            inputs.Controls.Add(field);
        }
        var prior = active.LastOrDefault()?.Design;
        var budget = prior?.FlightBudget ?? new();
        var pair = list.SelectedIndex >= 0 ? context.PairMembers(lines, lines[list.SelectedIndex]) : [];
        var lead = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90 };
        var direction = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
        bool pairingEdited = false;
        if (pair.Length == 2)
        {
            var stored = pair[0].Design!.Layout!;
            foreach (var l in pair) lead.Items.Add(l.Name);
            lead.SelectedIndex = stored.FirstMotherId == pair[1].Id ? 1 : 0;
            void SetDirections()
            {
                var line = pair[lead.SelectedIndex];
                var a = line.Design!.Approach.Start; var b = line.Design.Departure.End;
                bool ns = Math.Abs(b.Y-a.Y) >= Math.Abs(b.X-a.X);
                bool increasing = ns ? b.Y >= a.Y : b.X >= a.X;
                string forward = ns ? (increasing ? "北" : "南") : (increasing ? "东" : "西");
                string reverse = ns ? (increasing ? "南" : "北") : (increasing ? "西" : "东");
                direction.Items.Clear(); direction.Items.AddRange(["自动", forward + "端直连", reverse + "端直连"]);
            }
            SetDirections();
            direction.SelectedIndex = stored.FirstLegReverse switch { true => 2, false => 1, null => 0 };
            Input("本组先飞", lead); Input("连接端", direction);
            lead.SelectedIndexChanged += (_, _) => { SetDirections(); direction.SelectedIndex = 0; };
        }
        var speed = Number(.01m, 1000, (decimal)budget.SpeedMps);
        var minutes = Number(0, 10000, (decimal)budget.AvailableMinutes);
        var access = Number(0, 10000000, (decimal)budget.AccessAllowanceM);
        Input("估算航速 m/s", speed); Input("可用航时 min（0 仅按航程）", minutes); Input("额外往返预留 m", access);
        var summary = new Label { AutoSize = true, Dock = DockStyle.Fill, Padding = new(0, 4, 0, 4) };
        var table = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.AllCells
        };
        foreach (string col in new[] { "架次", "母线 / 执行段", "飞行方向", "配对状态", "建议起降点", "接入 m", "线间连接 m", "直返回程 m", "含预留航程 m", "上限 m", "估算 min" }) table.Columns.Add(col, col);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        var calculate = new Button { Text = "计算预览", AutoSize = true };
        var accept = new Button { Text = "应用编排", AutoSize = true };
        var cancel = new Button { Text = "关闭", AutoSize = true, DialogResult = DialogResult.Cancel };
        actions.Controls.AddRange([calculate, accept, cancel]); panel.Controls.Add(summary, 0, 1);
        panel.Controls.Add(table, 0, 2); panel.Controls.Add(actions, 0, 3); dialog.Controls.Add(panel); dialog.CancelButton = cancel;
        List<RouteCheckLine>? candidate = null;
        bool budgetEdited = false;
        void InvalidateResult()
        {
            candidate = null; accept.Enabled = false; table.Rows.Clear();
            summary.Text = $"工程单架次上限 {context.MaxSortieM / 1000:0.###} km。修改参数后点击“计算预览”。";
        }
        void Calculate()
        {
            InvalidateResult();
            try
            {
                var requestedBudget = new TieFlightBudget((double)speed.Value, (double)minutes.Value, (double)access.Value);
                var next = lines.ToList();
                if (budgetEdited)
                {
                    next = next.Select(l => l.Design?.Version == context.Version ? l with { Design = l.Design with { FlightBudget = requestedBudget } } : l).ToList();
                }
                if (pairingEdited)
                {
                    foreach (var l in pair)
                    {
                        int ix = next.FindIndex(item => item.Id == l.Id);
                        var old = next[ix]; var design = old.Design!;
                        next[ix] = old with { Design = design with { Layout = design.Layout! with
                        { FirstMotherId = pair[lead.SelectedIndex].Id, FirstLegReverse = direction.SelectedIndex switch { 1 => false, 2 => true, _ => null } } } };
                    }
                }
                var flights = TieFlightPlanner.Build(next, context.Version, context.MaxSortieM, context.Bearing);
                var coverage = TieLineDesign.Coverage(context.Mains, next, context.Version);
                foreach (var flight in flights)
                {
                    table.Rows.Add(flight.Name, string.Join("、", flight.Legs.Select(l => l.Pieces == 1 ? l.MotherName : $"{l.MotherName}.S{l.Sequence}/{l.Pieces}")),
                        string.Join(" / ", flight.Legs.Select(l => l.FromFraction < l.ToFraction ? "正向" : "反向")),
                        flight.Pairing, flight.LaunchBasis, flight.OutboundM.ToString("F1"), flight.ConnectionM.ToString("F1"), flight.ReturnM.ToString("F1"), flight.TotalM.ToString("F1"), flight.LimitM.ToString("F1"), flight.Minutes.ToString("F1"));
                }
                summary.Text = $"工程上限 {context.MaxSortieM / 1000:0.###} km · {flights.Count} 架次 · 主测线交会不足 {coverage.Values.Count(v => v < 2)}/{coverage.Count} 条" +
                    " · 含起降接入和返航";
                candidate = next; accept.Enabled = budgetEdited || pairingEdited;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidDataException or InvalidOperationException)
            { summary.Text = ex.Message; }
        }
        foreach (var field in new[] { speed, minutes, access }) field.ValueChanged += (_, _) => { budgetEdited = true; InvalidateResult(); };
        direction.SelectedIndexChanged += (_, _) => { pairingEdited = true; InvalidateResult(); };
        calculate.Click += (_, _) => Calculate(); accept.Click += (_, _) => dialog.DialogResult = DialogResult.OK;
        Calculate();
        if (dialog.ShowDialog(this) != DialogResult.OK || candidate == null) return;
        int selected = list.SelectedIndex;
        lines.Clear(); lines.AddRange(candidate); Changed = true; RefreshLists();
        if (selected >= 0 && selected < lines.Count) list.SelectedIndex = selected;
    }
}
