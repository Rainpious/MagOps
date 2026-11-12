using System.ComponentModel;
using System.Drawing.Drawing2D;
using MagOps.Core.Coordinates;

namespace MagOps.Desktop;

internal sealed class CoordinatePreviewPanel : Panel
{
    private IReadOnlyList<CoordinateConversionResult> _points =
        Array.Empty<CoordinateConversionResult>();
    private int _selectedIndex = -1;
    private int _measurementA = -1;
    private int _measurementB = -1;
    private readonly PreviewNavigation _navigation;

    public event Action<int>? PointSelected;

    public CoordinatePreviewPanel()
    {
        _navigation = new PreviewNavigation(this);
        Dock = DockStyle.Fill;
        BackColor = SystemColors.Window;
        BorderStyle = BorderStyle.FixedSingle;
        DoubleBuffered = true;
        TabStop = true;
        SetStyle(
            ControlStyles.Selectable |
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw,
            true);
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            int next = value >= 0 && value < _points.Count
                ? value
                : -1;
            if (_selectedIndex == next)
                return;

            _selectedIndex = next;
            Invalidate();
        }
    }

    public void SetPoints(IReadOnlyList<CoordinateConversionResult>? points)
    {
        _points = points ?? Array.Empty<CoordinateConversionResult>();
        _selectedIndex = -1;
        _measurementA = _measurementB = -1;
        _navigation.SetDataKey(_points.Count == 0 ? null : _points.Select(p => new { p.Longitude, p.Latitude, p.CentralMeridian, p.Datum }).ToArray());
        Invalidate();
    }

    internal void SetMeasurementEndpoints(int a, int b)
    {
        _measurementA = a >= 0 && a < _points.Count ? a : -1;
        _measurementB = b >= 0 && b < _points.Count ? b : -1;
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left || _points.Count == 0)
            return;

        PointF[] screenPoints = BuildScreenPoints();
        if (screenPoints.Length == 0)
            return;

        int nearest = -1;
        double nearestSquared = double.PositiveInfinity;
        for (int i = 0; i < screenPoints.Length; i++)
        {
            double dx = screenPoints[i].X - e.X;
            double dy = screenPoints[i].Y - e.Y;
            double distanceSquared = dx * dx + dy * dy;
            if (distanceSquared < nearestSquared)
            {
                nearestSquared = distanceSquared;
                nearest = i;
            }
        }

        if (nearest < 0)
            return;

        Focus();
        SelectedIndex = nearest;
        PointSelected?.Invoke(nearest);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var focusCue = _navigation.BeginPaint(e.Graphics);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

        if (_points.Count == 0)
        {
            DrawCenteredMessage(
                e.Graphics,
                "完成坐标转换后可在此预览区域轮廓和点序。");
            return;
        }

        PointF[] screenPoints = BuildScreenPoints();
        if (screenPoints.Length == 0)
        {
            DrawCenteredMessage(
                e.Graphics,
                "当前坐标无法生成预览。");
            return;
        }

        if (screenPoints.Length >= 3)
        {
            using var rangeBrush = new SolidBrush(
                Color.FromArgb(36, SystemColors.Highlight));
            e.Graphics.FillPolygon(rangeBrush, screenPoints);
        }

        using var routePen = new Pen(SystemColors.ControlDarkDark, 1.6f);
        if (screenPoints.Length >= 2)
            e.Graphics.DrawLines(routePen, screenPoints);

        if (screenPoints.Length >= 3)
        {
            using var closePen = new Pen(SystemColors.ControlDark, 1f)
            {
                DashStyle = DashStyle.Dash
            };
            e.Graphics.DrawLine(
                closePen,
                screenPoints[^1],
                screenPoints[0]);
        }

        for (int i = 0; i < screenPoints.Length; i++)
        {
            PointF point = screenPoints[i];
            bool selected = i == _selectedIndex;
            float radius = selected ? 6.5f : 4.5f;
            RectangleF marker = new(
                point.X - radius,
                point.Y - radius,
                radius * 2,
                radius * 2);

            if (selected)
            {
                e.Graphics.FillEllipse(SystemBrushes.Highlight, marker);
                using var selectedPen = new Pen(SystemColors.Highlight, 2f);
                e.Graphics.DrawEllipse(selectedPen, marker);
            }
            else
            {
                e.Graphics.FillEllipse(SystemBrushes.Window, marker);
                e.Graphics.DrawEllipse(SystemPens.ControlDarkDark, marker);
            }

            string label = (i + 1).ToString();
            Size labelSize = TextRenderer.MeasureText(
                label,
                Font,
                Size.Empty,
                TextFormatFlags.NoPadding);
            TextRenderer.DrawText(
                e.Graphics,
                label,
                Font,
                new Point(
                    (int)Math.Round(point.X + radius + 2),
                    (int)Math.Round(point.Y - labelSize.Height / 2d)),
                selected
                    ? SystemColors.Highlight
                    : SystemColors.ControlText,
                TextFormatFlags.NoPadding);
        }

        if (_measurementA >= 0 && _measurementB >= 0)
        {
            using var measurementPen = new Pen(SystemColors.Highlight, 2.5f) { DashStyle = DashStyle.Dash };
            e.Graphics.DrawLine(measurementPen, screenPoints[_measurementA], screenPoints[_measurementB]);
        }
        foreach (var (index, label) in new[] { (_measurementA, "A"), (_measurementB, "B") })
        {
            if (index < 0) continue;
            var point = screenPoints[index];
            using var ring = new Pen(SystemColors.Highlight, 2f);
            e.Graphics.DrawEllipse(ring, point.X - 9, point.Y - 9, 18, 18);
            TextRenderer.DrawText(e.Graphics, label, Font, new Point((int)point.X - 18,
                (int)point.Y - (label == "A" ? 28 : -12)), SystemColors.Highlight,
                SystemColors.Window, TextFormatFlags.NoPadding);
        }

        DrawAxisHint(e.Graphics);
        DrawExtentSummary(e.Graphics);

        if (_selectedIndex >= 0 &&
            _selectedIndex < _points.Count &&
            _measurementA < 0 && _measurementB < 0)
        {
            DrawSelectedPointCallout(
                e.Graphics,
                screenPoints[_selectedIndex],
                _points[_selectedIndex],
                _selectedIndex + 1);
        }
    }

    private PointF[] BuildScreenPoints()
    {
        if (_points.Count == 0)
            return Array.Empty<PointF>();

        const int horizontalMargin = 44;
        const int verticalMargin = 42;
        Rectangle plot = Rectangle.Inflate(
            ClientRectangle,
            -horizontalMargin,
            -verticalMargin);
        if (plot.Width < 30 || plot.Height < 30)
            return Array.Empty<PointF>();

        double meanLatitude = _points.Average(x => x.Latitude);
        double lonScale = Math.Max(
            0.15,
            Math.Cos(meanLatitude * Math.PI / 180d));

        double[] xs = _points
            .Select(x => x.Longitude * lonScale)
            .ToArray();
        double[] ys = _points
            .Select(x => x.Latitude)
            .ToArray();

        double minX = xs.Min();
        double maxX = xs.Max();
        double minY = ys.Min();
        double maxY = ys.Max();

        double spanX = Math.Max(maxX - minX, 1e-12);
        double spanY = Math.Max(maxY - minY, 1e-12);

        double scale = Math.Min(
            plot.Width / spanX,
            plot.Height / spanY);

        double usedWidth = spanX * scale;
        double usedHeight = spanY * scale;
        double originX = plot.Left + (plot.Width - usedWidth) / 2d;
        double originY = plot.Top + (plot.Height - usedHeight) / 2d;

        var result = new PointF[_points.Count];
        for (int i = 0; i < _points.Count; i++)
        {
            double px = originX + (xs[i] - minX) * scale;
            double py = originY + usedHeight - (ys[i] - minY) * scale;
            result[i] = _navigation.Apply(new PointF(
                (float)px,
                (float)py));
        }

        return result;
    }


    private void DrawAxisHint(Graphics graphics)
    {
        var area = new Rectangle(
            8,
            6,
            Math.Max(0, ClientSize.Width - 16),
            22);
        TextRenderer.DrawText(
            graphics,
            "N ↑    E →",
            Font,
            area,
            SystemColors.GrayText,
            TextFormatFlags.Right |
            TextFormatFlags.VerticalCenter |
            TextFormatFlags.NoPrefix);
    }

    private void DrawExtentSummary(Graphics graphics)
    {
        double minLon = _points.Min(x => x.Longitude);
        double maxLon = _points.Max(x => x.Longitude);
        double minLat = _points.Min(x => x.Latitude);
        double maxLat = _points.Max(x => x.Latitude);

        double areaM2 = CoordinateGeometryMath.PolygonAreaSquareMeters(_points);
        string areaText = _points.Count >= 3
            ? areaM2 >= 1_000_000d
                ? $"Area {areaM2 / 1_000_000d:0.###} km² ({areaM2 / 10_000d:0.##} ha)"
                : areaM2 >= 10_000d
                    ? $"Area {areaM2 / 10_000d:0.##} ha ({areaM2:0} m²)"
                    : $"Area {areaM2:0.##} m²"
            : "Area --";

        string summary =
            $"Lon {minLon:0.########}–{maxLon:0.########}   " +
            $"Lat {minLat:0.########}–{maxLat:0.########}   " +
            $"Points {_points.Count}   {areaText}";

        Rectangle area = new(
            8,
            Math.Max(0, ClientSize.Height - 28),
            Math.Max(0, ClientSize.Width - 16),
            22);

        TextRenderer.DrawText(
            graphics,
            summary,
            Font,
            area,
            SystemColors.GrayText,
            TextFormatFlags.Left |
            TextFormatFlags.VerticalCenter |
            TextFormatFlags.EndEllipsis |
            TextFormatFlags.NoPrefix);
    }

    private void DrawSelectedPointCallout(
        Graphics graphics,
        PointF point,
        CoordinateConversionResult selected,
        int sequence)
    {
        string title =
            $"点 #{sequence}";
        string body =
            $"经纬度：{selected.LongitudeDecimal}, {selected.LatitudeDecimal}\n" +
            $"DMS：{selected.LongitudeDms}  /  {selected.LatitudeDms}\n" +
            $"X / Y：{selected.X:0.###}, {selected.Y:0.###} m\n" +
            $"E / N：{selected.Easting:0.###}, {selected.Northing:0.###} m\n" +
            $"{selected.Datum} · 中央经线 {selected.CentralMeridian:0.########}°E · {selected.AxisOrderLabel}";

        int availableWidth =
            Math.Max(
                220,
                ClientSize.Width - 20);
        int width =
            Math.Min(
                560,
                Math.Max(
                    360,
                    availableWidth / 2));
        width =
            Math.Min(
                width,
                availableWidth);

        using var titleFont = new Font(
            Font,
            FontStyle.Bold);
        Size bodySize = TextRenderer.MeasureText(
            graphics,
            body,
            Font,
            new Size(
                Math.Max(
                    160,
                    width - 24),
                int.MaxValue),
            TextFormatFlags.NoPadding |
            TextFormatFlags.WordBreak |
            TextFormatFlags.NoPrefix);

        int titleHeight =
            Math.Max(
                22,
                TextRenderer.MeasureText(
                    title,
                    titleFont,
                    Size.Empty,
                    TextFormatFlags.NoPadding).Height + 6);
        int height =
            Math.Min(
                Math.Max(
                    112,
                    titleHeight +
                    bodySize.Height +
                    18),
                Math.Max(
                    112,
                    ClientSize.Height - 52));

        bool placeRight =
            point.X + 18 + width <=
            ClientSize.Width - 8;
        int x = placeRight
            ? (int)Math.Round(
                point.X + 16)
            : (int)Math.Round(
                point.X - width - 16);

        bool placeAbove =
            point.Y - height - 14 >= 8;
        int y = placeAbove
            ? (int)Math.Round(
                point.Y - height - 12)
            : (int)Math.Round(
                point.Y + 14);

        x = Math.Clamp(
            x,
            8,
            Math.Max(
                8,
                ClientSize.Width -
                width -
                8));
        y = Math.Clamp(
            y,
            8,
            Math.Max(
                8,
                ClientSize.Height -
                height -
                34));

        var box =
            new Rectangle(
                x,
                y,
                width,
                height);

        // A short leader makes it clear which point the larger information
        // card describes, while keeping the point itself unobscured.
        Point leaderTarget = new(
            placeRight
                ? box.Left
                : box.Right,
            Math.Clamp(
                (int)Math.Round(point.Y),
                box.Top + 8,
                box.Bottom - 8));
        using (var leaderPen =
            new Pen(
                SystemColors.Highlight,
                1.3f))
        {
            graphics.DrawLine(
                leaderPen,
                point,
                leaderTarget);
        }

        using var infoBrush =
            new SolidBrush(UiTheme.Surface);
        using var infoPen =
            new Pen(
                SystemColors.Highlight,
                1.4f);
        using var headerBrush =
            new SolidBrush(UiTheme.SurfaceMuted);

        graphics.FillRectangle(
            infoBrush,
            box);
        graphics.FillRectangle(
            headerBrush,
            new Rectangle(
                box.Left,
                box.Top,
                box.Width,
                titleHeight));
        graphics.DrawRectangle(
            infoPen,
            box);

        TextRenderer.DrawText(
            graphics,
            title,
            titleFont,
            new Rectangle(
                box.Left + 10,
                box.Top + 4,
                box.Width - 20,
                titleHeight - 6),
            SystemColors.Highlight,
            UiTheme.SurfaceMuted,
            TextFormatFlags.Left |
            TextFormatFlags.VerticalCenter |
            TextFormatFlags.NoPadding |
            TextFormatFlags.NoPrefix);

        TextRenderer.DrawText(
            graphics,
            body,
            Font,
            new Rectangle(
                box.Left + 10,
                box.Top + titleHeight + 7,
                box.Width - 20,
                box.Height -
                titleHeight -
                13),
            UiTheme.TextPrimary,
            UiTheme.Surface,
            TextFormatFlags.Left |
            TextFormatFlags.Top |
            TextFormatFlags.WordBreak |
            TextFormatFlags.NoPadding |
            TextFormatFlags.NoPrefix);
    }

    private void DrawCenteredMessage(
        Graphics graphics,
        string text)
    {
        TextRenderer.DrawText(
            graphics,
            text,
            Font,
            ClientRectangle,
            SystemColors.GrayText,
            TextFormatFlags.HorizontalCenter |
            TextFormatFlags.VerticalCenter |
            TextFormatFlags.WordBreak |
            TextFormatFlags.NoPrefix);
    }
}
