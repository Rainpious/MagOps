using MagOps.Core.Routes;
using P = MagOps.Core.SurveyPlanning.SurveyBoundaryPoint;

namespace MagOps.Desktop;

internal sealed partial class TieLineDesigner
{
    private enum Pick { None, Create, Anchor, Second }
    private readonly NumericUpDown groupCount = Number(1, 500, 1);
    private readonly NumericUpDown pairDistance = Number(.01m, 100000000, 1000);
    private readonly CheckBox perpendicular = new() { Text = "垂直正式测线", AutoSize = true };
    private Pick pick;
    private bool advancedOpen;
    private P? dragOrigin, dragPoint;
    private string? anchorId;

    private void AddTool(Control parent, string text, Action action)
    {
        var button = new Button { Text = text, AutoSize = true };
        button.Click += (_, _) => TryEdit(action);
        parent.Controls.Add(button);
    }

    private void TryEdit(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or InvalidDataException)
        { info.Text = ex.Message; }
    }

    private void BuildPairTools(FlowLayoutPanel bar, FlowLayoutPanel advanced)
    {
        void Field(string title, Control c)
        {
            bar.Controls.Add(new Label { Text = title, AutoSize = true, Margin = new(4, 8, 0, 0) });
            bar.Controls.Add(c);
        }
        KeyPreview = true;
        KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Escape || (pick == Pick.None && dragOrigin == null)) return;
            pick = Pick.None; dragOrigin = dragPoint = null; map.Capture = false; map.Cursor = Cursors.Default;
            e.Handled = e.SuppressKeyPress = true; UpdatePreview();
        };
        groupCount.DecimalPlaces = 0;
        var centered = context.LastCenteredBatch(lines);
        if (centered.Length > 0) groupCount.Value = centered.Select(l => l.Design!.Layout!.BatchId).Distinct().Count();
        pairDistance.Value = (decimal)(lines.LastOrDefault(l => l.Design?.Layout != null)?.Design?.Layout?.IntervalM
            ?? (double)spacing.Value * (standard.SelectedIndex == 1 ? 20 : 10));
        perpendicular.Checked = context.Mains.Length > 0;
        perpendicular.Enabled = perpendicular.Checked;
        perpendicular.CheckedChanged += (_, _) =>
        {
            azimuth.Enabled = !perpendicular.Checked;
            if (perpendicular.Checked) azimuth.Value = (decimal)context.Bearing;
        };
        bar.Controls.Add(perpendicular);
        azimuth.Enabled = !perpendicular.Checked;
        Field("切割方位 °", azimuth); Field("组数（每组两条）", groupCount); Field("组内线距 m", pairDistance);
        centerPair.Click += (_, _) => TryEdit(() => CreateGroups(null));
        bar.Controls.Add(centerPair);
        AddTool(bar, "点图布设", () => Arm(Pick.Create));
        AddTool(bar, "定位选中线", () => Arm(Pick.Anchor));
        AddTool(bar, "第二点定间距", () => Arm(Pick.Second));
        AddTool(bar, "应用组内线距", () => { var (a, _, gap) = Selection(); MovePair(a, a.Design!.Approach.End, Math.CopySign((double)pairDistance.Value,gap)); });
        AddTool(bar, "翻到另一侧", () => { var (a, _, gap) = Selection(); MovePair(a, a.Design!.Approach.End, -gap); });
        AddTool(bar, "高级设置", () => { advancedOpen = advanced.Visible = !advancedOpen; UpdatePreview(); });
        AddTool(advanced, "恢复规范建议线距", () => pairDistance.Value = spacing.Value * (standard.SelectedIndex == 1 ? 20 : 10));
        AddTool(advanced, "用 E/N 定位选中线", () => { var (a, _, gap) = Selection(); MovePair(a, new((double)east.Value,(double)north.Value), gap); });
    }

    private void Arm(Pick mode)
    {
        anchorId = mode == Pick.Create ? null : Selection().Anchor.Id;
        pick = mode; map.Cursor = Cursors.Cross; info.Text = PairStatus();
    }

    private void CreateGroups(P? through)
    {
        var budget = lines.LastOrDefault(l => l.Design?.Version == context.Version)?.Design?.FlightBudget ?? new();
        double bearing = perpendicular.Checked ? context.Bearing : (double)azimuth.Value;
        var replacing = through is not null ? [] : context.LastCenteredBatch(lines);
        if (through is null && replacing.Length == 0 && SelectedLine is { Design.Layout: { CenteredBatchId: null } } legacy)
        {
            var pair = context.PairMembers(lines, legacy);
            if (pair.Length == 2)
            {
                var choice = ReviewCentering("旧版线组未记录布设方式", pair.Select(l => l.Name),
                    "可用当前选中的这对线重排，或保留它们并新增居中线组。", legacy: true);
                if (choice == DialogResult.Cancel) return;
                if (choice == DialogResult.Yes) replacing = pair;
            }
        }
        var retained = lines.Except(replacing).ToList();
        var added = context.CreatePairs((int)groupCount.Value, (double)pairDistance.Value, bearing,
            (double)spacing.Value, (double)left.Value, (double)right.Value, standard.Text, designNote.Text.Trim(), budget,
            retained, through, replacing);
        int at = replacing.Length == 0 ? retained.Count : lines.IndexOf(replacing[0]);
        retained.InsertRange(at, added);
        if (replacing.Length > 0)
        {
            var changes = new List<string>();
            var byId = added.ToDictionary(l => l.Id);
            foreach (var old in replacing)
            {
                var design = old.Design!;
                bool kept = byId.TryGetValue(old.Id, out var next);
                if (kept && design.Approach == next!.Design!.Approach && design.Departure == next.Design.Departure) continue;
                if (kept && !design.Layout!.Edited && design.CutFractions.Length == 0) continue;
                string detail = design.CutFractions.Length == 0 ? "" : " · 切点 " +
                    string.Join("、", design.CutFractions.Select(c => c.ToString("0.##%")));
                changes.Add(old.Name + (kept ? " · 更新线位" : " · 移除") + detail);
            }
            if (changes.Count > 0 && ReviewCentering("核对居中重排", changes,
                "保留的母线按原里程比例迁移切点，减少组数时移除列出的多余母线。") != DialogResult.Yes) return;
        }
        string focus = added.Any(l => l.Id == SelectedLine?.Id) ? SelectedLine!.Id : added[0].Id;
        Apply(retained, focus);
    }

    private DialogResult ReviewCentering(string title, IEnumerable<string> details, string message, bool legacy = false)
    {
        using var dialog = new Form { Text = title, StartPosition = FormStartPosition.CenterParent, ClientSize = new(510, 240) };
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new(8) };
        panel.RowStyles.Add(new(SizeType.AutoSize)); panel.RowStyles.Add(new(SizeType.Percent, 100)); panel.RowStyles.Add(new(SizeType.AutoSize));
        panel.Controls.Add(new Label { Text = message, AutoSize = true }, 0, 0);
        var items = new ListBox { Dock = DockStyle.Fill, HorizontalScrollbar = true };
        items.Items.AddRange(details.ToArray()); panel.Controls.Add(items, 0, 1);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        actions.Controls.Add(new Button { Text = legacy ? "重排选中线组" : "应用重排", DialogResult = DialogResult.Yes, AutoSize = true });
        if (legacy) actions.Controls.Add(new Button { Text = "保留并新增", DialogResult = DialogResult.No, AutoSize = true });
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, AutoSize = true };
        actions.Controls.Add(cancel); dialog.CancelButton = dialog.AcceptButton = cancel;
        panel.Controls.Add(actions, 0, 2); dialog.Controls.Add(panel);
        return dialog.ShowDialog(this);
    }

    private (RouteCheckLine Anchor, RouteCheckLine Other, double Gap) Selection()
    {
        if (list.SelectedIndex < 0 || list.SelectedIndex >= lines.Count) throw new InvalidOperationException("先选择要定位的组内线。");
        var a = lines[list.SelectedIndex];
        var pair = context.PairMembers(lines, a);
        if (pair.Length != 2) throw new InvalidOperationException("请选择同一有效线组中的一条切割线。");
        var other = pair.Single(l => l.Id != a.Id);
        return (a, other, Offset(a, other.Design!.Approach.End));
    }

    private static double Offset(RouteCheckLine line, P p)
    {
        var d = line.Design!;
        var a = d.Approach.End; var b = d.Departure.Start;
        return ((p.X-a.X)*(b.Y-a.Y)-(p.Y-a.Y)*(b.X-a.X))/Math.Sqrt(Math.Pow(b.X-a.X,2)+Math.Pow(b.Y-a.Y,2));
    }

    private void MovePair(RouteCheckLine a, P through, double gap) => Apply(context.PositionPair(lines, a, through, gap), a.Id);

    private void Apply(List<RouteCheckLine> next, string? select)
    {
        TieFlightPlanner.Build(next, context.Version, context.MaxSortieM, context.Bearing);
        pick = Pick.None; anchorId = null; map.Cursor = Cursors.Default;
        lines.Clear(); lines.AddRange(next); Changed = true; RefreshLists();
        list.SelectedIndex = lines.FindIndex(l => l.Id == select);
    }

    private void RemoveSelection()
    {
        if (list.SelectedIndex < 0) return;
        var selected = lines[list.SelectedIndex];
        var pair = context.PairMembers(lines, selected);
        var next = lines.Except(pair.Length == 2 ? pair : [selected]).ToList();
        Apply(next, next.FirstOrDefault()?.Id);
    }

    private string PairStatus()
    {
        if (pick == Pick.Create) return "在地图上点第一组首线的通过位置，其余切割线向右等距排布。Esc 取消。";
        if (pick == Pick.Anchor) return "在图上点选中线应经过的位置，配线随组平移。Esc 取消。";
        if (pick == Pick.Second) return "点配线应经过的位置，保留选中线，按法向距离更新组内线距。Esc 取消。";
        if (lines.Count == 0) return "先选组数和组内线距，点击“居中布设”；要指定首线位置，点击“点图布设”后在地图上点一下。";
        decimal min = spacing.Value * (standard.SelectedIndex == 1 ? 10 : 5);
        decimal max = spacing.Value * (standard.SelectedIndex == 1 ? 30 : 15);
        string label = $"主测线距 {spacing.Value:0.##} m · 一般切割线距 {min:0.##}～{max:0.##} m";
        if (list.SelectedIndex < 0 || list.SelectedIndex >= lines.Count) return label;
        var a = lines[list.SelectedIndex]; var pair = context.PairMembers(lines, a);
        if (pair.Length != 2) return label;
        var other = pair.Single(l => l.Id != a.Id);
        double gap = Math.Abs(Offset(a, other.Design!.Approach.End));
        var neighbors = lines.Except(pair).Where(l => l.Design?.Version == context.Version &&
            Math.Abs(Offset(a,l.Design.Departure.Start)-Offset(a,l.Design.Approach.End)) < .01).ToArray();
        string nearby = neighbors.Length == 0 ? "" : $" · 最近组外线距 {neighbors.Min(l=>pair.Min(m=>Math.Abs(Offset(m,l.Design!.Approach.End)))):0.##} m";
        return $"{pair[0].Name} / {pair[1].Name} · 组内 {gap:0.##} m{nearby} · 拖中心菱形平移　{label}";
    }

    private P MapPoint(Point pixel) => new(xmin + pixel.X / scale, ymax - pixel.Y / scale);
    private static P Center(RouteCheckLine[] pair) => new(
        pair.Average(l=>(l.Design!.Approach.End.X+l.Design.Departure.Start.X)/2),
        pair.Average(l=>(l.Design!.Approach.End.Y+l.Design.Departure.Start.Y)/2));

    private bool PairMouseDown(Point pixel, P p, bool advanced)
    {
        if (pick != Pick.None)
        {
            TryEdit(() =>
            {
                if (pick == Pick.Create) { CreateGroups(p); return; }
                var (a, _, gap) = Selection();
                if (anchorId != a.Id) { Arm(pick); return; }
                if (pick == Pick.Anchor) MovePair(a, p, gap);
                else MovePair(a, a.Design!.Approach.End, Offset(a,p));
            });
            return true;
        }
        if (list.SelectedIndex >= 0)
        {
            var pair = context.PairMembers(lines, lines[list.SelectedIndex]);
            if (pair.Length == 2)
            {
                var c = Center(pair);
                if (Math.Pow((p.X-c.X)*scale,2)+Math.Pow((p.Y-c.Y)*scale,2) <= Math.Pow(10 * map.DeviceDpi / 96d, 2))
                { dragOrigin = dragPoint = p; map.Capture = true; map.Cursor = Cursors.SizeAll; return true; }
            }
        }
        if (advanced && twoPoints.Checked) return false;
        string? labelId = lineLabels.LastOrDefault(label => label.Bounds.Contains(pixel)).LineId;
        int index = labelId == null ? HitCutLine(p) : lines.FindIndex(l => l.Id == labelId);
        if (index < 0) return !advanced;
        list.SelectedIndex = index;
        if (labelId == null && drawFlights.Checked)
        {
            // A mother line can supply several sorties. Resolve the actual flown segment at the click.
            var hitFlight = flights.SelectMany(f => f.Legs.Where(leg => leg.MotherId == lines[index].Id)
                    .Select(leg => (Flight: f, Distance: SegmentDistanceSquared(p, leg.Start, leg.End))))
                .Where(hit => hit.Distance <= Math.Pow(8 * Unit / scale, 2))
                .OrderBy(hit => hit.Distance).ThenByDescending(hit => hit.Flight == CurrentFlight)
                .FirstOrDefault();
            if (hitFlight.Flight != null) flightChoice.SelectedItem = hitFlight.Flight;
        }
        return true;
    }

    private int HitCutLine(P point)
    {
        double best = Math.Pow(8 * Unit / scale, 2);
        int hit = -1;
        for (int index = 0; index < lines.Count; index++)
        {
            if (lines[index].Design is not { } design) continue;
            foreach (var segment in design.Inside.Prepend(design.Approach).Append(design.Departure))
            {
                if (segment.Length == 0) continue;
                double distance2 = SegmentDistanceSquared(point, segment.Start, segment.End);
                if (distance2 > best) continue;
                best = distance2;
                hit = index;
            }
        }
        return hit;
    }

    private static double SegmentDistanceSquared(P point, P a, P b)
    {
        double dx = b.X-a.X, dy = b.Y-a.Y;
        double length2 = dx*dx + dy*dy;
        double t = length2 == 0 ? 0 : Math.Clamp(((point.X-a.X)*dx + (point.Y-a.Y)*dy)/length2, 0, 1);
        double ex = point.X-(a.X+t*dx), ey = point.Y-(a.Y+t*dy);
        return ex*ex + ey*ey;
    }

    private void EndPairDrag(MouseEventArgs e)
    {
        if (dragOrigin is not { } start) return;
        dragOrigin = dragPoint = null; map.Capture = false; map.Cursor = Cursors.Default;
        var end = MapPoint(e.Location);
        TryEdit(() => { var (a, _, gap) = Selection(); var p = a.Design!.Approach.End;
            MovePair(a, new(p.X+end.X-start.X,p.Y+end.Y-start.Y),gap); });
        map.Invalidate();
    }

    private void PaintPairHandles(Graphics g, Func<P,PointF> px, float unit)
    {
        if (list.SelectedIndex < 0 || list.SelectedIndex >= lines.Count) return;
        var pair = context.PairMembers(lines, lines[list.SelectedIndex]);
        if (pair.Length != 2) return;
        var center = Center(pair);
        var c = px(center);
        DrawMapSymbol(g, MapInk.Handle, c, unit);
        foreach (var l in pair)
        {
            var d = l.Design!;
            var start = d.Approach.End; var stop = d.Departure.Start;
            if (dragOrigin is { } origin && dragPoint is { } target)
            {
                double length = Math.Sqrt(Math.Pow(stop.X-start.X,2)+Math.Pow(stop.Y-start.Y,2));
                double shift = Offset(l,target)-Offset(l,origin);
                double x = shift*(stop.Y-start.Y)/length, y = -shift*(stop.X-start.X)/length;
                g.DrawLine(Pens.Blue, px(new(start.X+x,start.Y+y)), px(new(stop.X+x,stop.Y+y)));
            }

        }
    }
}
