using System.Diagnostics;
using System.Drawing.Drawing2D;
using MagOps.Core.Routes;
using MagOps.Infrastructure.Services;
using P = MagOps.Core.SurveyPlanning.SurveyBoundaryPoint;

namespace MagOps.Desktop;

internal sealed partial class TieLineDesigner : Form
{
    private readonly TieLineWorkspace context;
    private readonly List<RouteCheckLine> lines;
    private readonly Canvas map = new() { Dock = DockStyle.Fill, BackColor = Color.White };
    private readonly ListBox list = new() { Dock = DockStyle.Fill, HorizontalScrollbar = true };
    private readonly ListBox net = new() { Dock = DockStyle.Fill, HorizontalScrollbar = true };
    private readonly Label lineCaption = new() { AutoSize = true };
    private readonly Label coverageLabel = new() { AutoSize = true };
    private readonly Label info = new() { AutoSize = true };
    private readonly bool initiallySaved;
    private readonly Button centerPair = new() { Text = "居中布设", AutoSize = true };
    private readonly Button saveDesign = new() { AutoSize = true };
    private readonly Button exportFlight = new() { Text = "按架次导出 KML", AutoSize = true };
    private readonly Button openExport = new() { Text = "打开上次导出目录", AutoSize = true, Visible = false };
    private string? exportDirectory;
    private readonly TextBox name = new() { Width = 87 };
    private readonly NumericUpDown spacing = Number(0.01m, 100000, 100), left = Number(0, 1000000, 400), right = Number(0, 1000000, 400);
    private readonly NumericUpDown azimuth = Number(0, 360, 0), east = Number(-100000000, 100000000, 0), north = Number(-100000000, 100000000, 0);
    private readonly CheckBox twoPoints = new() { Text = "两点定向", AutoSize = true };
    private readonly ComboBox standard = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 185 };
    private readonly TextBox designNote = new() { Width = 180 };
    private MainSurveySegment[] displayedMains = [];
    private readonly CheckBox drawFlights = new() { Text = "显示飞向、连接和切点", Checked = true, AutoSize = true };
    private List<TieSortie> flights = [];
    private P? first;
    private RouteCheckLine? preview;
    private string error = "";
    private bool loading;
    private double xmin, ymax, scale;
    internal IReadOnlyList<RouteCheckLine> Lines => lines;
    internal bool Changed { get; private set; }
    private sealed class Canvas : Panel { public Canvas() { DoubleBuffered = true; ResizeRedraw = true; } }
    private static NumericUpDown Number(decimal min, decimal max, decimal value) => new() { Minimum = min, Maximum = max, Value = value, DecimalPlaces = 2, Width = 100 };

    public TieLineDesigner(TieLineWorkspace context, List<RouteCheckLine> draft, double step, bool saved, Func<IReadOnlyList<RouteCheckLine>, string, string> export, Func<IReadOnlyList<TieSortie>, string> exportKml)
    {
        initiallySaved = saved;
        this.context = context; lines = draft; Text = $"切割线设计 · {Path.GetFileName(context.BoundaryPath)} · {context.Datum} / 中央经线 {context.Meridian}° · 待生成子区 {context.PendingPartitions}"; Width = 1230; Height = 780; MinimumSize = new(870, 560);
        StartPosition = FormStartPosition.CenterParent;
        var bounds = context.Region.Rings().SelectMany(r => r).ToArray();
        east.Value = (decimal)bounds.Average(p => p.X); north.Value = (decimal)bounds.Average(p => p.Y);
        azimuth.Value = (decimal)context.Bearing; spacing.Value = (decimal)Math.Clamp(step, .01, 100000); left.Value = right.Value = Math.Min(left.Maximum, 4 * spacing.Value);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5 };
        layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        var inputs = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        inputs.SizeChanged += (_, _) => { foreach (var child in inputs.Controls.OfType<FlowLayoutPanel>()) child.MaximumSize = new(Math.Max(200, inputs.ClientSize.Width - 12), 0); };
        var advanced = new FlowLayoutPanel { AutoSize = true, Visible = false };
        void Input(string text, Control control) { advanced.Controls.Add(new Label { Text = text, AutoSize = true, Margin = new(4, 8, 0, 0) }); advanced.Controls.Add(control); }
        Input("线号", name); Input("主线距 m", spacing); Input("起端外延 m", left); Input("末端外延 m", right);
        Input("通过点 E", east); Input("N", north); advanced.Controls.Add(twoPoints); advanced.Controls.Add(drawFlights);
        drawFlights.CheckedChanged += (_, _) => UpdateFlightDisplay();
        standard.Items.AddRange([TieLineDesign.IndustryStandard, TieLineDesign.RegionalStandard]);
        standard.SelectedItem = draft.LastOrDefault(l => l.Design != null)?.Design?.Standard ?? TieLineDesign.IndustryStandard;
        Input("布设依据", standard); Input("设计说明", designNote);
        inputs.Controls.Add(Step("1", "布设线组"));
        BuildPairTools(inputs, advanced);
        inputs.Controls.Add(advanced);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        var output = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        static Label Step(string number, string title)
        {
            var label = UiComponents.SectionLabel(number, title);
            label.Margin = new Padding(6, 6, 9, 3);
            return label;
        }
        void Bind(FlowLayoutPanel row, Button button, Action action)
        {
            button.Click += (_, _) => { try { action(); } catch (Exception ex) { MessageBox.Show(this, ex.Message, Text); } };
            row.Controls.Add(button);
        }
        void Act(string title, Action action) => Bind(actions, new Button { Text = title, AutoSize = true }, action);
        actions.Controls.Add(Step("2", "核对设计"));
        Act("架次与续航…", FlightDetails); Act("母线切点…", EditMotherCuts);
        AddTool(advanced, "添加单线", () => Put(false)); AddTool(advanced, "修改单线", () => Put(true));
        Act("查看本线交点", () =>
        {
            if (list.SelectedIndex < 0 || lines[list.SelectedIndex].Design is not { } d)
                throw new InvalidOperationException("请在清单选择切割线。");
            using var detail = new Form { Text = lines[list.SelectedIndex].Name + " · 主测线交点", Width = 580, Height = 410, StartPosition = FormStartPosition.CenterParent };
            var table = new DataGridView { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill };
            table.Columns.Add("main", "主测线"); table.Columns.Add("east", "东坐标 m"); table.Columns.Add("north", "北坐标 m");
            foreach (var hit in d.Crossings) table.Rows.Add(hit.Label, hit.Point.X.ToString("F2"), hit.Point.Y.ToString("F2"));
            detail.Controls.Add(table); detail.ShowDialog(this);
        });
        AddTool(advanced, "登记选中原线作控制线", () =>
        {
            if (net.SelectedIndex < 0) throw new InvalidDataException("请在右下列表选择承担控制作用的原测线。");
            var control = context.RegisterControl(displayedMains[net.SelectedIndex].LineId, (double)spacing.Value);
            if (control.Design!.Crossings.Length == 0 || lines.Any(l => l.Name == control.Name))
                throw new InvalidDataException("原线须横穿其他测线，且线号须唯一。");
            if (lines.Any(l => l.Design?.SameAlignment(control.Design) == true))
                throw new InvalidDataException("该位置已经布设切割线。");
            lines.Add(control with { Design = control.Design with { Standard = standard.SelectedItem!.ToString()!, DesignNote = designNote.Text.Trim() } });
            Changed = true; RefreshLists();
        });
        Act("移除所选线组或单线", RemoveSelection);
        output.Controls.Add(Step("3", "保存与导出"));
        void ShowExport(string directory)
        {
            exportDirectory = directory;
            openExport.Visible = true;
            openExport.AccessibleDescription = directory;
        }
        Bind(output, exportFlight, () => ShowExport(exportKml(flights)));
        Bind(advanced, new Button { Text = "导出设计参考资料", AutoSize = true }, () =>
            ShowExport(export(lines, Changed || !saved ? "草稿" : "已保存设计")));
        Bind(output, openExport, () => Process.Start(new ProcessStartInfo(exportDirectory!) { UseShellExecute = true }));
        saveDesign.Text = context.Mains.Length == 0 ? "保存工区草稿" : "确认到工程";
        Bind(output, saveDesign, () => { RouteCheckLines.Validate(lines); TieFlightPlanner.Build(lines, context.Version, context.MaxSortieM, context.Bearing); DialogResult = DialogResult.OK; });
        Bind(output, new Button { Text = "取消", AutoSize = true }, () => DialogResult = DialogResult.Cancel);
        output.Controls.Add(UiComponents.HelpLabel("每架次一个作业 KML，含执行段和线间连接。"
            + (context.Mains.Length == 0 ? "草稿可在本工区重开。" : "确认到工程后，请在主界面保存工程。")));
        var split = new SplitContainer { Dock = DockStyle.Fill, Width = 1150, SplitterDistance = 772 }; split.Panel1.Controls.Add(map);
        var side = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7 };
        foreach (var pct in new[] { 0, 21, 0, 0, 29, 0, 50 }) side.RowStyles.Add(pct == 0 ? new(SizeType.AutoSize) : new(SizeType.Percent, pct));
        BuildFlightDisplay(side);
        side.Controls.Add(lineCaption, 0, 3); side.Controls.Add(list, 0, 4);
        side.Controls.Add(coverageLabel, 0, 5); side.Controls.Add(net, 0, 6); split.Panel2.Controls.Add(side);
        layout.Controls.Add(inputs, 0, 0); layout.Controls.Add(split, 0, 1); layout.Controls.Add(info, 0, 2); layout.Controls.Add(actions, 0, 3); layout.Controls.Add(output, 0, 4); Controls.Add(layout);
        foreach (var c in new[] { left, right, azimuth, east, north, spacing }) c.ValueChanged += (_, _) => UpdatePreview();
        standard.SelectedIndexChanged += (_, _) => UpdatePreview();
        twoPoints.CheckedChanged += (_, _) => { if (twoPoints.Checked) perpendicular.Checked = false; first = null; UpdatePreview(); };
        InstallMapLegend();
        map.Paint += PaintMap; map.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left || scale <= 0) return;
            var p = new P(xmin + e.X / scale, ymax - e.Y / scale);
            if (PairMouseDown(e.Location, p, advanced.Visible)) return;
            loading = true;
            if (twoPoints.Checked && first is { } a)
            {
                if (Math.Abs(p.X - a.X) + Math.Abs(p.Y - a.Y) > 0.01)
                    azimuth.Value = (decimal)((Math.Atan2(p.X - a.X, p.Y - a.Y) * 180 / Math.PI + 360) % 360);
                first = null;
            }
            else { east.Value = (decimal)p.X; north.Value = (decimal)p.Y; if (twoPoints.Checked) first = p; }
            loading = false; UpdatePreview();
        };
        list.SelectedIndexChanged += (_, _) =>
        {
            if (list.SelectedIndex >= 0 && list.SelectedIndex < lines.Count && lines[list.SelectedIndex] is { } l && l.Design is { } d)
            {
                loading = true; name.Text = l.Name; standard.SelectedItem = d.Standard; designNote.Text = d.DesignNote; var a = d.Approach.End; var b = d.Departure.Start;
                east.Value = (decimal)((a.X + b.X) / 2); north.Value = (decimal)((a.Y + b.Y) / 2);
                if (!perpendicular.Checked) azimuth.Value = (decimal)((Math.Atan2(b.X - a.X, b.Y - a.Y) * 180 / Math.PI + 360) % 360);
                left.Value = (decimal)Math.Round(d.Approach.Length, 2); right.Value = (decimal)Math.Round(d.Departure.Length, 2); spacing.Value = (decimal)d.Spacing; loading = false; if (d.Layout is { } pair) pairDistance.Value = (decimal)Math.Clamp(pair.IntervalM,.01,100000000); UpdatePreview();
            }
            if (SelectedLine is { } selected &&
                (flightChoice.SelectedItem as TieSortie)?.Legs.Any(leg => leg.MotherId == selected.Id) != true)
                flightChoice.SelectedIndex = flights.FindIndex(f => f.Legs.Any(leg => leg.MotherId == selected.Id));
            UpdateFlightDisplay();
        };
        map.MouseMove += (_, e) => { if (dragOrigin is not null) { dragPoint = MapPoint(e.Location); map.Invalidate(); } };
        map.MouseUp += (_, e) => EndPairDrag(e);
        net.SelectedIndexChanged += (_, _) => map.Invalidate();
        RefreshLists();
        if (lines.Count > 0 && list.SelectedIndex < 0) list.SelectedIndex = 0;
    }

    private void UpdatePreview()
    {
        if (loading) return;
        try { preview = context.Draw(name.Text, new((double)east.Value, (double)north.Value), (double)azimuth.Value, (double)spacing.Value, (double)left.Value, (double)right.Value); error = ""; }
        catch (InvalidDataException ex) { preview = null; error = ex.Message; }
        var d = preview?.Design;
        info.Text = (first != null ? "再点一点确定方向。 " : "点击地图选择通过点。 ") + (d == null ? error :
            $"预览：区内 {d.InsideM:F2} m，外延 {d.ExtensionM:F2} m，总长 {d.TotalM:F2} m；交会 {d.Crossings.DistinctBy(h => h.LineId).Count()} 条主测线；" +
            (d.ExtensionsRecommended ? "两端外延符合 3～5 倍线距" : $"起端{(d.ApproachRecommended ? "正常" : "偏离推荐范围")}、末端{(d.DepartureRecommended ? "正常" : "偏离推荐范围")}") +
            (d.InterruptedM > .01 ? $"；中断 {d.InterruptedM:F2} m" : "") +
             $"；一般切割线距 {spacing.Value * (standard.SelectedIndex == 1 ? 10 : 5):0.##}～{spacing.Value * (standard.SelectedIndex == 1 ? 30 : 15):0.##} m；相对默认垂线偏角 {Math.Abs(Math.IEEERemainder((double)azimuth.Value - context.Bearing, 180)):0.##}°");
        if (!advancedOpen || pick != Pick.None) info.Text = PairStatus();
        map.Invalidate();
    }

    private void Put(bool replace)
    {
        int index = list.SelectedIndex; if (replace && index < 0) throw new InvalidOperationException("先选择要替换的线。");
        if (replace && context.PairMembers(lines, lines[index]).Length == 2) throw new InvalidOperationException("成组切割线请使用整组定位。");
        if (replace && lines[index].Design?.ExistingControlLineId != null)
            throw new InvalidOperationException("该线采用已有原线几何。可移除登记，再选择原线重新登记。");
        string title = name.Text.Trim();
        if (title.Length == 0 || lines.Where((_, i) => !replace || i != index).Any(l => l.Name == title)) throw new InvalidDataException("请输入唯一线号。");
        if (preview?.Design is not { } design || (context.Mains.Length > 0 && design.Crossings.Length == 0) || first != null) throw new InvalidDataException("请选定贯穿工区并穿过主测线的位置。");
        if (lines.Where((_, i) => !replace || i != index).Any(l => l.Design?.SameAlignment(design) == true))
            throw new InvalidDataException("该位置已经布设切割线。");
        var previous = replace ? lines[index].Design : null;
        var line = preview with { Id = replace ? lines[index].Id : "check-" + Guid.NewGuid().ToString("N"), Name = title, Design = design with { Layout = previous?.Layout is { } layout ? layout with { Edited = true } : null, FlightBudget = previous?.FlightBudget ?? new(), CutFractions = previous?.CutFractions ?? [], Standard = standard.SelectedItem!.ToString()!, DesignNote = designNote.Text.Trim() } };
        if (replace) lines[index] = line; else lines.Add(line);
        Changed = true; RefreshLists();
    }

    private void RefreshLists()
    {
        bool hasLines = lines.Count > 0;
        UiComponents.SetButtonRole(centerPair, hasLines ? UiButtonRole.Neutral : UiButtonRole.Primary);
        UiComponents.SetButtonRole(saveDesign, Changed || (hasLines && !initiallySaved) ? UiButtonRole.Primary : UiButtonRole.Neutral);
        list.Items.Clear(); net.Items.Clear();
        foreach (var l in lines) list.Items.Add(l.Design is { } d ? $"{l.Name}  {d.InsideM:F1} / {d.ExtensionM:F1} / {d.TotalM:F1}  · {(d.Version != context.Version ? "来源待核" : d.ExtensionsRecommended ? "外延正常" : "外延待核")}" : l.Name + "（旧版待核）");
        var counts = mainCoverage = TieLineDesign.Coverage(context.Mains, lines, context.Version);
        coverageLabel.Text = counts.Count == 0 ? "交会待载入正式测线" : $"达到两次交会 {counts.Values.Count(n => n >= 2)}/{counts.Count} 条";
        try
        {
            flights = TieFlightPlanner.Build(lines, context.Version, context.MaxSortieM, context.Bearing);
            coverageLabel.Text += $" · {flights.Count} 架次";
        }
        catch (InvalidDataException ex) { flights = []; coverageLabel.Text += " · " + ex.Message; }
        exportFlight.Enabled = flights.Count > 0;
        UiComponents.SetButtonRole(exportFlight, exportFlight.Enabled ? UiButtonRole.Primary : UiButtonRole.Neutral);
        displayedMains = context.Mains.DistinctBy(m => m.LineId).Where(m => counts.ContainsKey(m.LineId))
            .OrderBy(m => counts[m.LineId] >= 2).ThenBy(m => m.Label.Length).ThenBy(m => m.Label, StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var m in displayedMains)
        {
            var intersect = lines.Where(l => l.Design?.Version == context.Version && l.Design.Crossings.Any(h => h.LineId == m.LineId));
            net.Items.Add($"{m.Label} · {counts[m.LineId]} 条{(counts[m.LineId] < 2 ? "（不足）" : "")}  {string.Join("、", intersect.Select(l => l.Name))}");
        }
        RefreshFlightChoices();
        int next = 1; while (lines.Any(l => l.Name == "Q" + next)) next++; name.Text = "Q" + next;
        UpdatePreview();
    }

}
