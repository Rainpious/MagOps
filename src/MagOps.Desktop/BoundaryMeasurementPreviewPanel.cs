using System.Drawing.Drawing2D;
using MagOps.Infrastructure.Services;

namespace MagOps.Desktop;

/// <summary>Read-only projected polygon preview, including holes and area-weighted centroid.</summary>
internal sealed class BoundaryMeasurementPreviewPanel : Panel
{
    private readonly PreviewNavigation _navigation;
    private BoundaryMeasurement? _measurement;
    private bool _showCentroid = true;
    private double _minE, _maxE, _minN, _maxN;

    internal BoundaryMeasurementPreviewPanel()
    {
        Dock = DockStyle.Fill;
        BackColor = SystemColors.Window;
        BorderStyle = BorderStyle.FixedSingle;
        DoubleBuffered = true;
        SetStyle(ControlStyles.Selectable | ControlStyles.ResizeRedraw, true);
        AccessibleName = "完整边界与面积重心预览";
        _navigation = new PreviewNavigation(this, allowPrimaryDrag: true);
    }

    internal void SetMeasurement(BoundaryMeasurement? measurement, object? identity)
    {
        _measurement = measurement;
        if (measurement is { Vertices.Count: > 0 })
        {
            _minE = measurement.Vertices.Min(v => v.Easting); _maxE = measurement.Vertices.Max(v => v.Easting);
            _minN = measurement.Vertices.Min(v => v.Northing); _maxN = measurement.Vertices.Max(v => v.Northing);
        }
        _navigation.SetDataKey(identity);
        Invalidate();
    }

    internal void ShowCentroid(bool show) { _showCentroid = show; Invalidate(); }

    internal void LocateCentroid()
    {
        if (_measurement is { } measurement)
            _navigation.CenterOn(ProjectToBase(measurement.CentroidEasting, measurement.CentroidNorthing));
        Focus();
    }

    private PointF ProjectToBase(double e, double n)
    {
        if (_measurement is not { Vertices.Count: > 0 } measurement) return PointF.Empty;
        double scale = Math.Min(Math.Max(1, ClientSize.Width - 80) / Math.Max(1e-9, _maxE - _minE),
            Math.Max(1, ClientSize.Height - 80) / Math.Max(1e-9, _maxN - _minN));
        return new PointF((float)(ClientSize.Width / 2d + (e - (_minE + _maxE) / 2d) * scale),
            (float)(ClientSize.Height / 2d - (n - (_minN + _maxN) / 2d) * scale));
    }

    private PointF ToScreen(double e, double n) => _navigation.Apply(ProjectToBase(e, n));

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var focusCue = _navigation.BeginPaint(e.Graphics);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        if (_measurement is not { } measurement)
        {
            TextRenderer.DrawText(e.Graphics, "导入完整工区边界 KML 后显示外环、孔洞与面积重心。", Font,
                ClientRectangle, SystemColors.GrayText, TextFormatFlags.HorizontalCenter |
                TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            return;
        }
        using var fill = new SolidBrush(Color.FromArgb(35, SystemColors.Highlight));
        using var outerPen = new Pen(SystemColors.ControlText, 1.6f);
        using var holePen = new Pen(SystemColors.ControlText, 1.4f) { DashStyle = DashStyle.Dash };
        foreach (var polygon in measurement.Vertices.GroupBy(v => v.Polygon))
        {
            using var path = new GraphicsPath(FillMode.Alternate);
            foreach (var ring in polygon.GroupBy(v => v.Ring))
            {
                var points = ring.OrderBy(v => v.Index).Select(v => ToScreen(v.Easting, v.Northing)).ToArray();
                if (points.Length < 3) continue;
                path.AddPolygon(points);
            }
            e.Graphics.FillPath(fill, path);
            foreach (var ring in polygon.GroupBy(v => v.Ring))
            {
                var points = ring.OrderBy(v => v.Index).Select(v => ToScreen(v.Easting, v.Northing)).ToArray();
                if (points.Length >= 3) e.Graphics.DrawPolygon(ring.Key == 0 ? outerPen : holePen, points);
            }
        }
        if (_showCentroid)
        {
            var center = ToScreen(measurement.CentroidEasting, measurement.CentroidNorthing);
            using var marker = new Pen(SystemColors.Highlight, 2.5f);
            e.Graphics.DrawEllipse(marker, center.X - 7, center.Y - 7, 14, 14);
            e.Graphics.DrawLine(marker, center.X - 12, center.Y, center.X + 12, center.Y);
            e.Graphics.DrawLine(marker, center.X, center.Y - 12, center.X, center.Y + 12);
            TextRenderer.DrawText(e.Graphics, "面积重心", Font, new Point((int)center.X + 14, (int)center.Y),
                SystemColors.Highlight, SystemColors.Window);
        }
        TextRenderer.DrawText(e.Graphics, "N ↑  E →  · 实线外环 / 虚线孔洞", Font,
            new Rectangle(8, 6, Math.Max(0, Width - 16), 24), SystemColors.GrayText,
            TextFormatFlags.Right | TextFormatFlags.EndEllipsis);
    }
}
