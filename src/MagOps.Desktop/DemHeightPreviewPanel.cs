using MagOps.Infrastructure.Services;

namespace MagOps.Desktop;

internal sealed class DemHeightPreviewPanel : Panel
{
    private readonly PreviewNavigation _navigation;
    private (DemHeightPoint Point, double X, double Y)[] _points = [];
    private DemHeightPoint? _selected;
    private double _minX, _maxX, _minY, _maxY, _minHeight, _maxHeight;
    private bool _profile;
    private string _key = "";
    private DemHeightPoint[] _input = [];
    private int _validHeightCount;
    internal int VisiblePointCount { get; private set; }
    internal int DrawnPointCount { get; private set; }
    internal string CaptionText { get; private set; } = "";
    internal const int MaximumDrawnPoints = 30000;
    internal DemHeightPreviewPanel()
    {
        Dock = DockStyle.Fill; DoubleBuffered = true; BackColor = Color.White;
        SetStyle(ControlStyles.Selectable, true);
        AccessibleName = "DEM 离地高度预览";
        _navigation = new(this);
    }
    internal void SetProfile(bool value) { _profile = value; SetPoints(_input, _key); }
    internal void SelectPoint(DemHeightPoint? point) { _selected = point; _revealSelection = point is not null; Invalidate(); }
    private bool _revealSelection;
    internal void SetPoints(DemHeightPoint[] points, string key)
    {
        _input = points; _key = key;
        var positioned = points.Where(p => p.Source.Longitude is { } x && p.Source.Latitude is { } y &&
            WorkareaSpatialService.ValidCoordinate(x, y)).ToArray();
        double lon0 = positioned.Length == 0 ? 0 : positioned.Average(p => p.Source.Longitude!.Value);
        double lat0 = positioned.Length == 0 ? 0 : positioned.Average(p => p.Source.Latitude!.Value);
        double factor = 111319.49 * Math.Cos(lat0 * Math.PI / 180);
        _points = _profile
            ? points.Where(p => p.AboveGroundHeightM.HasValue).Select(p => (p, (double)p.InputIndex + 1, p.AboveGroundHeightM!.Value)).ToArray()
            : positioned.Select(p => (p, (p.Source.Longitude!.Value - lon0) * factor, (p.Source.Latitude!.Value - lat0) * 111319.49)).ToArray();
        if (_points.Length > 0)
        { _minX = _points.Min(p => p.X); _maxX = _points.Max(p => p.X); _minY = _points.Min(p => p.Y); _maxY = _points.Max(p => p.Y); }
        var valid = points.Where(p => p.AboveGroundHeightM.HasValue).Select(p => p.AboveGroundHeightM!.Value).ToArray();
        _validHeightCount = valid.Length;
        _minHeight = valid.Length == 0 ? 0 : valid.Min(); _maxHeight = valid.Length == 0 ? 0 : valid.Max();
        _navigation.SetDataKey(new { key, _profile, Count = points.Length, First = points.FirstOrDefault()?.InputIndex, Last = points.LastOrDefault()?.InputIndex });
        Invalidate();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var focus = _navigation.BeginPaint(e.Graphics);
        VisiblePointCount = DrawnPointCount = 0;
        if (_points.Length == 0)
        {
            CaptionText = _profile ? "暂无可计算离地高度；请查看上方缺项与基准条件。" : "暂无有效空间坐标。";
            TextRenderer.DrawText(e.Graphics, CaptionText, Font, ClientRectangle, SystemColors.GrayText); return;
        }
        double sx = Math.Max(1, Width - 64) / Math.Max(1, _maxX - _minX), sy = Math.Max(1, Height - 100) / Math.Max(1, _maxY - _minY);
        if (!_profile) sx = sy = Math.Min(sx, sy);
        PointF Map(double x, double y) => _navigation.Apply(new(
            (float)(Width / 2d + (x - (_minX + _maxX) / 2) * sx),
            (float)(Height / 2d + 20 - (y - (_minY + _maxY) / 2) * sy)));
        var plot = new RectangleF(0, 48, Width, Math.Max(1, Height - 48));
        int selectionIndex = _selected is { } selection ? Array.FindIndex(_points, p => p.Point.InputIndex == selection.InputIndex) : -1;
        if (_revealSelection && selectionIndex >= 0)
        {
            var selectedPosition = Map(_points[selectionIndex].X, _points[selectionIndex].Y);
            if (!plot.Contains(selectedPosition))
                _navigation.Translate((float)(plot.Width / 2 - selectedPosition.X), (float)(plot.Top + plot.Height / 2 - selectedPosition.Y));
        }
        _revealSelection = false;
        // Filter by viewport before sampling: zooming into a short interval reveals its original points.
        var visible = new List<(int Index, PointF Position)>();
        for (int i = 0; i < _points.Length; i++)
        {
            PointF xy = Map(_points[i].X, _points[i].Y);
            if (plot.Contains(xy)) visible.Add((i, xy));
        }
        VisiblePointCount = visible.Count;
        var palette = Enumerable.Range(0, 32).Select(i => new SolidBrush(Color.FromArgb(55 + i * 5, 120 - i * 2, 195 - i * 4))).ToArray();
        using var missing = new SolidBrush(Color.Gray); using var negative = new SolidBrush(Color.Firebrick);
        using var selected = new Pen(Color.Black, 2);
        int draw = Math.Min(visible.Count, MaximumDrawnPoints);
        bool selectedDrawn = false;
        try
        {
            // A scatter profile never invents continuity across a gap or between flights.
            for (int i = 0; i < draw; i++)
            {
                var item = visible[draw == 1 ? 0 : (int)((long)i * (visible.Count - 1) / (draw - 1))];
                var p = _points[item.Index]; PointF xy = item.Position;
                selectedDrawn |= item.Index == selectionIndex;
                var h = p.Point.AboveGroundHeightM;
                int color = h is null ? 0 : Math.Clamp((int)(31 * (h.Value - _minHeight) / Math.Max(1, _maxHeight - _minHeight)), 0, 31);
                e.Graphics.FillEllipse(h is null ? missing : h < 0 ? negative : palette[color], xy.X - 2, xy.Y - 2, 4, 4);
            }
            DrawnPointCount = draw;
            if (selectionIndex >= 0)
            {
                var p = Map(_points[selectionIndex].X, _points[selectionIndex].Y);
                if (plot.Contains(p))
                {
                    e.Graphics.DrawEllipse(selected, p.X - 6, p.Y - 6, 12, 12);
                    if (!selectedDrawn) DrawnPointCount++;
                }
            }
        }
        finally { foreach (var brush in palette) brush.Dispose(); }
        string legend = _validHeightCount == 0 ? "暂无可计算离地高度；灰点仅表示位置，请查看上方缺项条件。"
            : _profile ? $"离地高度散点剖面；横轴＝原始输入序号；高度 {_minHeight:F1}–{_maxHeight:F1} m；红＝负值"
            : $"离地高度空间着色；{_minHeight:F1}–{_maxHeight:F1} m；灰＝缺项，红＝负值";
        string counts = $"筛选输入 {_input.Length:N0}；可定位 {_points.Length:N0}；当前范围 {VisiblePointCount:N0}；绘制 {DrawnPointCount:N0}（均匀抽样，缩放更新；选中点另保留）";
        CaptionText = counts + "\n" + legend;
        TextRenderer.DrawText(e.Graphics, counts, Font, new Rectangle(8, 2, Math.Max(1, Width - 16), 22), Color.DimGray,
            TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        TextRenderer.DrawText(e.Graphics, legend, Font, new Rectangle(8, 24, Math.Max(1, Width - 16), 22), Color.DimGray,
            TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
    }
    protected override void Dispose(bool disposing) { if (disposing) _navigation.Dispose(); base.Dispose(disposing); }
}
