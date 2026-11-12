using System.ComponentModel;
using System.Drawing.Drawing2D;
using MagOps.Core.Routes;

namespace MagOps.Desktop;

internal sealed partial class RoutePreviewPanel : Panel
{
    private float UiScale => Math.Max(1f, DeviceDpi / 96f);
    private float PlotLeft => 38f * UiScale;
    private float PlotRight => 24f * UiScale;
    private float PlotTop => 40f * UiScale;
    private float PlotBottom => 52f * UiScale;
    private readonly PreviewNavigation _navigation;
    private RoutePlanResult? _navigationPlan;

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public RoutePlanResult? Plan { get; set; }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int SelectedSortieIndex { get; set; } = -1;

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string SelectedSortieState { get; set; } = "";

    // Runtime-only callback from MainForm. Keeping this internal avoids adding
    // another public WinForms-designer property while still letting the
    // preview distinguish edited/reversed sorties even when they are not
    // currently selected.
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal Func<RouteSortie, string>? SortieStateResolver { get; set; }

    public event Action<RouteEndpoint>? EndpointClicked;
    public event Action<double, double>? MapPointClicked;

    public RoutePreviewPanel()
    {
        SetStyle(ControlStyles.Selectable, true);
        DoubleBuffered = true;
        BackColor = UiTheme.Surface;
        BorderStyle = BorderStyle.FixedSingle;
        _navigation = new PreviewNavigation(this);
        Resize += (_, _) => Invalidate();
    }
    public event Action<PointF, PointF>? CutLineDrawn;
    private bool _drawCutLine;
    private Point _cutLineStart;
    private Point _cutLineCurrent;

    internal void BeginCutLineDrawing()
    {
        _drawCutLine = true;
        Cursor = Cursors.Cross;
        Invalidate();
    }

    internal (double X, double Y)? WorldPoint(Point point) => WorldFromScreen(point);

    internal (double MinX, double MaxX, double MinY, double MaxY)? WorldBounds()
    {
        if (Plan is null || Plan.Inference.Endpoints.Count == 0) return null;
        return (Plan.Inference.Endpoints.Min(x => x.X), Plan.Inference.Endpoints.Max(x => x.X),
            Plan.Inference.Endpoints.Min(x => x.Y), Plan.Inference.Endpoints.Max(x => x.Y));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var focusCue = _navigation.BeginPaint(e.Graphics);
        if (!ReferenceEquals(_navigationPlan, Plan))
        {
            _navigationPlan = Plan;
            _navigation.SetDataKey(Plan is null ? null : new { Plan.Mode, Plan.CentralMeridian, Points = Plan.Inference.Endpoints.Select(p => new { p.X, p.Y }).ToArray() });
        }
        if (Plan is null || Plan.Inference.Endpoints.Count == 0)
        {
            DrawEmptyState(e.Graphics);
            return;
        }

        if (!TryCreateTransform(out Func<(double X, double Y), PointF> screen, out double scale))
            return;

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

        using var basePen = new Pen(UiTheme.BorderStrong, 1f);
        foreach (string line in Plan.Inference.OrderedLines)
        {
            RouteEndpoint[] pair = Plan.Inference.Grouped[line];
            if (pair.Length < 2) continue;
            PointF a = screen(ToPhysical(pair[0]));
            PointF b = screen(ToPhysical(pair[1]));
            e.Graphics.DrawLine(basePen, a, b);
        }

        foreach (RouteExecutionSegment segment in Plan.ExecutionSegments
                     .Where(x => x.CutLineStartX != x.CutLineEndX || x.CutLineStartY != x.CutLineEndY)
                     .DistinctBy(x => x.CutOperationId))
        {
            PointF cutA = screen(ToPhysical(segment.CutLineStartX, segment.CutLineStartY, null));
            PointF cutB = screen(ToPhysical(segment.CutLineEndX, segment.CutLineEndY, null));
            using var cutPen = new Pen(Color.FromArgb(210, 40, 40), 2f) { DashStyle = DashStyle.Dash };
            e.Graphics.DrawLine(cutPen, cutA, cutB);
            using var cutBrush = new SolidBrush(Color.FromArgb(210, 40, 40));
            e.Graphics.FillEllipse(cutBrush, cutA.X - 3, cutA.Y - 3, 6, 6);
            e.Graphics.FillEllipse(cutBrush, cutB.X - 3, cutB.Y - 3, 6, 6);
        }

        var accessRows = LaunchSitePlanning.Enabled ? (DraftRows ?? (ReferenceEquals(Plan, AssessedPlan) && ConfirmedRows?.Count == Plan.Sorties.Count ? ConfirmedRows : RouteAccessPlanner.Assess(Plan))) : Array.Empty<SortieAccessAssessment>();
        for (int si = 0; si < Plan.Sorties.Count; si++)
        {
            PointF[] pts = Plan.Sorties[si].Points
                .Select(p => screen(ToPhysical(p)))
                .ToArray();
            if (pts.Length < 2) continue;

            bool selected = si == SelectedSortieIndex;
            bool overLimit = RouteCoordinateMath.RouteLength(Plan.Sorties[si].Points, Plan.Mode, Plan.CentralMeridian)
                > Plan.Options.MaxSortieKm * 1000d || (LaunchSitePlanning.Enabled && accessRows[si].Status == "OVER_LIMIT");
            string sortieState = SortieStateResolver?.Invoke(Plan.Sorties[si])
                ?? (selected ? SelectedSortieState : "自动生成");
            using var pen = new Pen(
                selected ? UiTheme.Accent : Color.FromArgb(44, 125, 205),
                selected ? 3f : 1.5f)
            {
                LineJoin = LineJoin.Round
            };

            bool edited = sortieState.Contains("已编辑", StringComparison.Ordinal);
            bool reversed = sortieState.Contains("已反转", StringComparison.Ordinal);
            bool boundaryAdjusted = sortieState.Contains("边界调整", StringComparison.Ordinal);
            if (edited && reversed)
                pen.DashStyle = DashStyle.DashDot;
            else if (edited)
                pen.DashStyle = DashStyle.Dash;
            else if (reversed)
                pen.DashStyle = DashStyle.Dot;
            else if (boundaryAdjusted)
                pen.DashStyle = DashStyle.DashDotDot;

            e.Graphics.DrawLines(pen, pts);
            if (overLimit)
            {
                using var overLimitPen = new Pen(Color.FromArgb(210, 45, 45), 3f)
                {
                    LineJoin = LineJoin.Round
                };
                for (int pointIndex = 0; pointIndex + 1 < Plan.Sorties[si].Points.Count; pointIndex += 2)
                {
                    PointF lineStart = screen(ToPhysical(Plan.Sorties[si].Points[pointIndex]));
                    PointF lineEnd = screen(ToPhysical(Plan.Sorties[si].Points[pointIndex + 1]));
                    e.Graphics.DrawLine(overLimitPen, lineStart, lineEnd);
                }
            }
        }

        if ((SiteDraft ?? Plan.AccessPlanning) is { } access)
        {
            using var roadPen = new Pen(Color.FromArgb(140, Color.SlateGray), 1.5f);
            foreach (var road in access.Roads) foreach (var segment in road.Segments)
                e.Graphics.DrawLines(roadPen, segment.Select(p => screen(AccessPhysical(p))).ToArray());
            foreach (var site in LaunchSitePlanning.Enabled ? access.Sites : [])
            {
                var p = screen(AccessPhysical(new(site.Longitude, site.Latitude)));
                using var brush = new SolidBrush(site.Enabled ? Color.OrangeRed : Color.Gray);
                if (DraftNewIds.Contains(site.Id)) e.Graphics.DrawEllipse(Pens.DarkCyan, p.X-6,p.Y-6,12,12);
                else e.Graphics.FillEllipse(brush, p.X - 4, p.Y - 4, 8, 8);
                if (SiteDraft != null && site.Id == SelectedSite) e.Graphics.DrawEllipse(Pens.Blue,p.X-9,p.Y-9,18,18);
                TextRenderer.DrawText(e.Graphics, site.Name, Font, new Point((int)p.X + 5, (int)p.Y), Color.DarkRed);
            }
        }
        if (LaunchSitePlanning.Enabled && SiteDraft is null)
            foreach (var site in Suggestions)
            {
                var p = screen(AccessPhysical(new(site.Longitude,site.Latitude)));
                e.Graphics.DrawEllipse(Pens.DarkCyan, p.X-6,p.Y-6,12,12);
                TextRenderer.DrawText(e.Graphics,site.Name,Font,new Point((int)p.X+7,(int)p.Y),Color.DarkCyan);
            }
        DrawDragTarget(e.Graphics,screen);
        foreach (var a in accessRows)
        {
            if (a.Site is null || a.First is null || a.Last is null) continue;
            using var ferry = new Pen(Color.FromArgb(180, Color.OrangeRed), 1.5f) { DashStyle = DashStyle.Dash };
            var site = screen(AccessPhysical(a.Site));
            e.Graphics.DrawLine(ferry, site, screen(AccessPhysical(a.First)));
            e.Graphics.DrawLine(ferry, screen(AccessPhysical(a.Last)), site);
        }
        DrawSortieStartsAndDirections(e.Graphics, screen);

        PointF start = screen(ToPhysical(Plan.StartPoint));
        const float r = 5f;
        using var startBrush = new SolidBrush(UiTheme.Accent);
        e.Graphics.FillEllipse(startBrush, start.X - r, start.Y - r, r * 2, r * 2);
        if (LaunchSitePlanning.Enabled && Plan.Options.TakeoffX.HasValue && Plan.Options.TakeoffY.HasValue)
        {
            PointF takeoff = screen(ToPhysical(Plan.Options.TakeoffX.Value, Plan.Options.TakeoffY.Value, null));
            using var takeoffPen = new Pen(Color.FromArgb(206, 70, 70), 2f);
            e.Graphics.DrawEllipse(takeoffPen, takeoff.X - 7, takeoff.Y - 7, 14, 14);
            e.Graphics.DrawLine(takeoffPen, takeoff.X - 10, takeoff.Y, takeoff.X + 10, takeoff.Y);
            e.Graphics.DrawLine(takeoffPen, takeoff.X, takeoff.Y - 10, takeoff.X, takeoff.Y + 10);
        }

        DrawLegend(e.Graphics);
        if (_drawCutLine && _cutLineStart != Point.Empty)
        {
            using var cutPen = new Pen(Color.FromArgb(210, 40, 40), 2.5f) { DashStyle = DashStyle.Dash };
            e.Graphics.DrawLine(cutPen, _cutLineStart, _cutLineCurrent);
        }

        double metersPerWorldUnit = string.Equals(
            Plan.Mode,
            "projected",
            StringComparison.OrdinalIgnoreCase)
                ? 1d
                : Math.PI * 6378137d / 180d;
        PreviewScaleBar.Draw(
            e.Graphics,
            this,
            metersPerWorldUnit / scale);
    }

    private void DrawEmptyState(Graphics graphics)
    {
        TextRenderer.DrawText(
            graphics,
            "生成航线后在此显示架次、起始点和首段飞行方向。",
            Font,
            new Rectangle(16, 16, Math.Max(0, Width - 32), 48),
            UiTheme.TextSecondary,
            TextFormatFlags.WordBreak | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    private void DrawLegend(Graphics graphics)
    {
        using var legendFont = new Font(
            Font.FontFamily,
            Math.Max(7f, Font.Size - 0.5f),
            FontStyle.Bold,
            GraphicsUnit.Point);
        string state = string.IsNullOrWhiteSpace(SelectedSortieState)
            ? "自动生成"
            : SelectedSortieState;
        string partitionStart = Plan is not null &&
            !string.IsNullOrWhiteSpace(Plan.PartitionCode)
                ? $"   {Plan.PartitionCode} 起始侧：{DisplayPartitionStartSide(Plan.Options.PartitionStartSide)}"
                : "";
        string legend =
            $"● 总起点   △ 架次起始   箭头=首段飞行方向   虚线=编辑   点线=测线方向已更换   当前架次：{state}{partitionStart}";
        float uiScale = UiScale;
        TextRenderer.DrawText(
            graphics,
            legend,
            legendFont,
            new Rectangle(
                (int)(10f * uiScale),
                (int)(7f * uiScale),
                Math.Max(0, Width - (int)(20f * uiScale)),
                (int)(24f * uiScale)),
            UiTheme.TextPrimary,
            UiTheme.Surface,
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
    }

    private void DrawSortieStartsAndDirections(
        Graphics graphics,
        Func<(double X, double Y), PointF> screen)
    {
        if (Plan is null) return;

        using var labelFont = new Font(
            Font.FontFamily,
            Math.Max(7f, Font.Size - 1.2f),
            FontStyle.Bold,
            GraphicsUnit.Point);

        for (int sortieIndex = 0; sortieIndex < Plan.Sorties.Count; sortieIndex++)
        {
            RouteSortie sortie = Plan.Sorties[sortieIndex];
            if (sortie.Points.Count < 2)
                continue;

            RouteEndpoint from = sortie.Points[0];
            RouteEndpoint? to = sortie.Points
                .Skip(1)
                .FirstOrDefault(point =>
                {
                    (double X, double Y) a0 = ToPhysical(from);
                    (double X, double Y) b0 = ToPhysical(point);
                    return Math.Abs(a0.X - b0.X) > 1e-12 ||
                           Math.Abs(a0.Y - b0.Y) > 1e-12;
                });
            if (to is null)
                continue;

            bool selected = sortieIndex == SelectedSortieIndex;
            using var markerBrush = new SolidBrush(
                selected ? UiTheme.Accent : Color.FromArgb(94, 108, 124));

            PointF a = screen(ToPhysical(from));
            PointF b = screen(ToPhysical(to));
            float dx = b.X - a.X;
            float dy = b.Y - a.Y;
            float pixelLength = MathF.Sqrt(dx * dx + dy * dy);
            if (pixelLength < 1f)
                continue;

            float ux = dx / pixelLength;
            float uy = dy / pixelLength;
            float nx = -uy;
            float ny = ux;
            const float marker = 4.6f;
            PointF[] startTriangle =
            [
                new(a.X + ux * marker, a.Y + uy * marker),
                new(a.X - ux * marker + nx * marker * 0.75f,
                    a.Y - uy * marker + ny * marker * 0.75f),
                new(a.X - ux * marker - nx * marker * 0.75f,
                    a.Y - uy * marker - ny * marker * 0.75f)
            ];
            graphics.FillPolygon(markerBrush, startTriangle);

            float arrowDistance = Math.Min(25f, Math.Max(11f, pixelLength * 0.28f));
            PointF tip = new(a.X + ux * arrowDistance, a.Y + uy * arrowDistance);
            const float arrowBack = 6.5f;
            const float arrowHalf = 3.2f;
            PointF[] arrow =
            [
                tip,
                new(tip.X - ux * arrowBack + nx * arrowHalf,
                    tip.Y - uy * arrowBack + ny * arrowHalf),
                new(tip.X - ux * arrowBack - nx * arrowHalf,
                    tip.Y - uy * arrowBack - ny * arrowHalf)
            ];
            graphics.FillPolygon(markerBrush, arrow);

            // Do not label every sortie. Dense projects can contain dozens of
            // sorties and the labels obscure the very geometry operators need
            // to inspect. Only the selected sortie receives a text label; all
            // other sorties keep the start marker + direction arrow.
            if (!selected)
                continue;

            (double X, double Y) p0 = ToPhysical(from);
            (double X, double Y) p1 = ToPhysical(to);
            double azimuth = Math.Atan2(p1.X - p0.X, p1.Y - p0.Y) * 180.0 / Math.PI;
            if (azimuth < 0) azimuth += 360.0;

            string firstLine = string.IsNullOrWhiteSpace(from.Line)
                ? "未标线"
                : from.Line;
            string label = $"架次{sortieIndex + 1:D2} | 首线 {firstLine} | 首段 {azimuth:0.#}°";
            Size textSize = TextRenderer.MeasureText(
                graphics,
                label,
                labelFont,
                Size.Empty,
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            int labelX = (int)Math.Clamp(
                a.X + 8f,
                PlotLeft,
                Math.Max(PlotLeft, Width - PlotRight - textSize.Width - 8f));
            int preferredY = a.Y <= PlotTop + 26f
                ? (int)(a.Y + 8f)
                : (int)(a.Y - textSize.Height - 8f);
            int labelY = (int)Math.Clamp(
                preferredY,
                PlotTop,
                Math.Max(PlotTop, Height - PlotBottom - textSize.Height));
            var box = new Rectangle(
                labelX - 4,
                labelY - 2,
                textSize.Width + 8,
                textSize.Height + 4);
            using var boxBrush = new SolidBrush(Color.FromArgb(238, UiTheme.Surface));
            using var boxPen = new Pen(UiTheme.Border);
            graphics.FillRectangle(boxBrush, box);
            graphics.DrawRectangle(boxPen, box);
            TextRenderer.DrawText(
                graphics,
                label,
                labelFont,
                new Point(labelX, labelY),
                UiTheme.TextPrimary,
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        }
    }

    private static string DisplayPartitionStartSide(
        RoutePartitionStartSide side) => side switch
    {
        RoutePartitionStartSide.North => "北侧",
        RoutePartitionStartSide.South => "南侧",
        RoutePartitionStartSide.West => "西侧",
        RoutePartitionStartSide.East => "东侧",
        _ => "自动（共同切分边界）"
    };

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (SiteDraft is not null) return;
        if (e.Button != MouseButtons.Left || Plan is null || Plan.Inference.Endpoints.Count == 0)
            return;

        if (!TryCreateTransform(out Func<(double X, double Y), PointF> screen, out _))
            return;

        RouteEndpoint? best = null;
        double bestPixels = double.PositiveInfinity;
        foreach (RouteEndpoint endpoint in Plan.Inference.Endpoints)
        {
            PointF s = screen(ToPhysical(endpoint));
            double dx = s.X - e.X;
            double dy = s.Y - e.Y;
            double d = Math.Sqrt(dx * dx + dy * dy);
            if (d < bestPixels)
            {
                bestPixels = d;
                best = endpoint;
            }
        }

        if (best is not null && bestPixels <= 14.0)
            EndpointClicked?.Invoke(best);
        else
        {
            var world = WorldFromScreen(e.Location);
            if (LaunchSitePlanning.Enabled && world.HasValue) MapPointClicked?.Invoke(world.Value.X, world.Value.Y);
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (SiteDraft is not null && e.Button == MouseButtons.Left) { BeginSiteGesture(e.Location); return; }
        if (_drawCutLine && e.Button == MouseButtons.Left)
        {
            _cutLineStart = e.Location;
            _cutLineCurrent = e.Location;
            Invalidate();
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_siteDown.HasValue && e.Button == MouseButtons.Left) { _sitePointer = e.Location; Invalidate(); return; }
        if (_drawCutLine && _cutLineStart != Point.Empty)
        {
            _cutLineCurrent = e.Location;
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (SiteDraft is not null && e.Button == MouseButtons.Left) { EndSiteGesture(e.Location); return; }
        if (!_drawCutLine || e.Button != MouseButtons.Left || _cutLineStart == Point.Empty)
            return;
        _cutLineCurrent = e.Location;
        PointF start = _cutLineStart;
        PointF end = _cutLineCurrent;
        _drawCutLine = false;
        _cutLineStart = Point.Empty;
        Cursor = Cursors.Default;
        Invalidate();
        CutLineDrawn?.Invoke(start, end);
    }

    private bool TryCreateTransform(
        out Func<(double X, double Y), PointF> screen,
        out double scale)
    {
        screen = static _ => PointF.Empty;
        scale = 0d;
        if (Plan is null || Plan.Inference.Endpoints.Count == 0)
            return false;

        var all = AllPhysicalPoints();
        if (all.Count == 0)
            return false;

        double minX = all.Min(p => p.X);
        double maxX = all.Max(p => p.X);
        double minY = all.Min(p => p.Y);
        double maxY = all.Max(p => p.Y);
        double spanX = Math.Max(maxX - minX, 1e-9);
        double spanY = Math.Max(maxY - minY, 1e-9);
        double plotWidth = Math.Max(1d, Width - PlotLeft - PlotRight);
        double plotHeight = Math.Max(1d, Height - PlotTop - PlotBottom);
        double sx = plotWidth / spanX;
        double sy = plotHeight / spanY;
        scale = Math.Max(1e-12, Math.Min(sx, sy));
        double resolvedScale = scale;

        screen = point => _navigation.Apply(new PointF(
            (float)(PlotLeft + (point.X - minX) * resolvedScale),
            (float)(Height - PlotBottom - (point.Y - minY) * resolvedScale)));
        return true;
    }

    private (double X, double Y) ToPhysical(RouteEndpoint p)
        => ToPhysical(p.X, p.Y, null);

    private (double X, double Y) AccessPhysical(AccessPoint p)
    {
        if (Plan!.Mode == "projected")
        {
            var xy = MagOps.Core.Coordinates.CoordinateService.GaussKrugerForward(p.Longitude, p.Latitude, Plan.CentralMeridian, "WGS84");
            return (xy.Easting, xy.Northing);
        }
        return ToPhysical(p.Longitude, p.Latitude, false);
    }
    private List<(double X, double Y)> AllPhysicalPoints()
    {
        var all = Plan!.Inference.Endpoints.Select(ToPhysical).ToList();
        if (LaunchSitePlanning.Enabled && Plan.Options.TakeoffX.HasValue && Plan.Options.TakeoffY.HasValue) all.Add(ToPhysical(Plan.Options.TakeoffX.Value, Plan.Options.TakeoffY.Value, null));
        if (Plan.AccessPlanning is { } a)
        {
            if (LaunchSitePlanning.Enabled) all.AddRange(a.Sites.Select(s => AccessPhysical(new(s.Longitude, s.Latitude))));
            all.AddRange(a.Roads.SelectMany(r => r.Segments).SelectMany(s => s).Select(AccessPhysical));
        }
        return all;
    }

    private (double X, double Y) ToPhysical(double x, double y, bool? projectedOverride)
    {
        var plan = Plan ?? throw new InvalidOperationException("路线预览尚未加载路线方案。");
        bool projected = projectedOverride ?? plan.Mode == "projected";
        if (projected)
        {
            var en = RouteCoordinateMath.ResolveProjectedEN(x, y);
            return (en.E, en.N);
        }

        double lat0 = Plan.Inference.Endpoints.Average(x => x.Y) * Math.PI / 180.0;
        return (x * Math.Cos(lat0), y);
    }

    private (double X, double Y)? WorldFromScreen(Point point)
    {
        if (!TryCreateTransform(out _, out double scale) || Plan is null) return null;
        var all = AllPhysicalPoints();
        double minX = all.Min(p => p.X), minY = all.Min(p => p.Y);
        PointF local = _navigation.Unapply(point);
        double x = minX + (local.X - PlotLeft) / scale;
        double y = minY + (Height - PlotBottom - local.Y) / scale;
        if (Plan.Mode.Equals("projected", StringComparison.OrdinalIgnoreCase)) return (x, y);
        double lat0 = Plan.Inference.Endpoints.Average(p => p.Y) * Math.PI / 180.0;
        return (x / Math.Cos(lat0), y);
    }
}
