using System.Drawing.Drawing2D;

namespace MagOps.Desktop;

/// <summary>Read-only map: input samples are points so separate acquisitions are never joined.</summary>
internal sealed class QualityOffsetPreview : Control
{
    internal sealed record Point(double Longitude, double Latitude, string Reason);
    private Point[] _points = [], _design = [];
    internal int VisiblePointCount { get; private set; }
    internal int DrawnPointCount { get; private set; }
    private double _originX, _originY, _cos = 1, _scale = 1, _panX, _panY;
    private System.Drawing.Point? _drag;
    public QualityOffsetPreview() { DoubleBuffered = true; BackColor = Color.White; TabStop = true; AccessibleName = "母测线与设计线空间预览，可拖动和滚轮缩放"; }
    public void SetData(Point[] points, Point[] design)
    { _points = points; _design = design; VisiblePointCount = DrawnPointCount = 0; Fit(); }
    public void Fit()
    {
        var all = _points.Concat(_design).ToArray();
        if (all.Length > 0)
        {
            _originX = (all.Min(p => p.Longitude) + all.Max(p => p.Longitude)) / 2;
            _originY = (all.Min(p => p.Latitude) + all.Max(p => p.Latitude)) / 2;
            _cos = Math.Max(.001, Math.Cos(_originY * Math.PI / 180));
            double width = (all.Max(p => p.Longitude) - all.Min(p => p.Longitude)) * _cos * 111195;
            double height = (all.Max(p => p.Latitude) - all.Min(p => p.Latitude)) * 111195;
            _scale = Math.Min(Math.Max(20, ClientSize.Width - 50) / Math.Max(1, width), Math.Max(20, ClientSize.Height - 50) / Math.Max(1, height));
        }
        _panX = _panY = 0; Invalidate();
    }
    protected override void OnResize(EventArgs e) { base.OnResize(e); Fit(); }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) { Focus(); _drag = e.Location; Capture = true; } }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _drag = null; Capture = false; }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_drag is { } old) { _panX += e.X - old.X; _panY += e.Y - old.Y; _drag = e.Location; Invalidate(); }
    }
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (e.Delta == 0) return;
        if (e is HandledMouseEventArgs wheel) wheel.Handled = true;
        double zoom = e.Delta > 0 ? 1.25 : .8;
        _scale *= zoom; _panX = e.X - ClientSize.Width / 2d - (e.X - ClientSize.Width / 2d - _panX) * zoom;
        _panY = e.Y - ClientSize.Height / 2d - (e.Y - ClientSize.Height / 2d - _panY) * zoom; Invalidate();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        if (_points.Length + _design.Length == 0) { g.DrawString("选择母线或候选查看空间对应", Font, Brushes.DimGray, 12, 12); return; }
        PointF Screen(Point p) => new((float)(ClientSize.Width / 2d + _panX + (p.Longitude - _originX) * _cos * 111195 * _scale),
            (float)(ClientSize.Height / 2d + _panY - (p.Latitude - _originY) * 111195 * _scale));
        using var pen = new Pen(Color.SeaGreen, 2);
        if (_design.Length > 1) g.DrawLines(pen, _design.Select(Screen).ToArray());
        var visible = new List<(PointF Screen, string Reason)>();
        foreach (var point in _points)
        {
            var p = Screen(point);
            if (p.X < -5 || p.Y < -5 || p.X > Width + 5 || p.Y > Height + 5) continue;
            visible.Add((p, point.Reason));
        }
        VisiblePointCount = visible.Count;
        DrawnPointCount = Math.Min(15000, visible.Count);
        for (int i = 0; i < DrawnPointCount; i++)
        {
            int index = DrawnPointCount <= 1 ? 0 : (int)((long)i * (visible.Count - 1) / (DrawnPointCount - 1));
            var (p, reason) = visible[index];
            var brush = reason == "END_OUTSIDE" ? Brushes.DarkOrange : reason is "" or "VALID" ? Brushes.SteelBlue : Brushes.IndianRed;
            g.FillEllipse(brush, p.X - 2, p.Y - 2, 4, 4);
        }
        string note = $"有效坐标{_points.Length}点 / 当前范围{VisiblePointCount}点 / 显示{DrawnPointCount}点（均匀抽样，缩放更新）";
        var size = g.MeasureString(note, Font);
        g.FillRectangle(Brushes.White, 5, 5, Math.Min(Width - 5, size.Width + 4), size.Height + 4);
        g.DrawString(note, Font, Brushes.DimGray, 7, 7);
    }
}
