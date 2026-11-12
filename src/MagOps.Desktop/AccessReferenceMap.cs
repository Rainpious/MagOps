using System.Drawing.Drawing2D;
using MagOps.Core.Routes;
namespace MagOps.Desktop;

/// <summary>Independent reference layer. Left click creates; drag-near-site moves on release; middle drag pans.</summary>
internal sealed class AccessReferenceMap : Panel
{
    internal bool ReferenceOnly;
    private readonly PreviewNavigation _navigation;
    private AccessPlanningData _data = new();
    private AccessPoint[] _context = [];
    private Func<AccessPoint, PointF>? _screen;
    private Func<PointF, AccessPoint>? _world;
    private string? _dragSite;
    private Point _down;
    internal event Action<AccessPoint, string?>? PointChosen;
    internal AccessReferenceMap()
    {
        Dock = DockStyle.Fill; BackColor = Color.White; BorderStyle = BorderStyle.FixedSingle; DoubleBuffered = true;
        _navigation = new PreviewNavigation(this);
    }
    internal void SetData(AccessPlanningData data, IEnumerable<AccessPoint>? context = null)
    {
        _data = ReferenceOnly ? data with { Sites = [], SortieSiteLocks = [] } : data; _context = context?.ToArray() ?? [];
        _navigation.SetDataKey(new { Roads = data.Roads.Select(r => r.Sha256).ToArray(), Context = _context });
        Invalidate();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); using var cue = _navigation.BeginPaint(e.Graphics);
        var points = _data.Roads.SelectMany(r => r.Segments).SelectMany(s => s)
            .Concat(_data.Sites.Select(s => new AccessPoint(s.Longitude, s.Latitude))).Concat(_context).ToArray();
        if (points.Length == 0)
        {
            // Empty map remains usable by coordinate entry; no arbitrary world location is implied.
            _screen = null; _world = null;
            TextRenderer.DrawText(e.Graphics, ReferenceOnly ? "添加踏勘轨迹后可查看参考路线；中键平移，滚轮缩放。" : "可先手工输入一个起降点；也可导入道路或使用当前工区坐标定位。\n有参考范围后，可点击自由选点；拖动已有点移动；中键平移，滚轮缩放。", Font,
                new Rectangle(18, 18, Math.Max(20, Width - 36), 90), UiTheme.TextSecondary); return;
        }
        double ox = points.Average(p => p.Longitude), oy = points.Average(p => p.Latitude);
        double cos = Math.Max(.02, Math.Cos(oy * Math.PI / 180));
        double minx = points.Min(p => (p.Longitude - ox) * cos), maxx = points.Max(p => (p.Longitude - ox) * cos);
        double miny = points.Min(p => p.Latitude - oy), maxy = points.Max(p => p.Latitude - oy);
        double spanx = Math.Max(maxx - minx, .004), spany = Math.Max(maxy - miny, .004);
        double scale = Math.Min(Math.Max(1, Width - 60) / spanx, Math.Max(1, Height - 60) / spany);
        double cx = (minx + maxx) / 2, cy = (miny + maxy) / 2;
        _screen = p => _navigation.Apply(new((float)(Width / 2d + ((p.Longitude - ox) * cos - cx) * scale), (float)(Height / 2d - (p.Latitude - oy - cy) * scale)));
        _world = q =>
        {
            var p = _navigation.Unapply(q);
            return new(ox + ((p.X - Width / 2d) / scale + cx) / cos, oy - (p.Y - Height / 2d) / scale + cy);
        };
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var roadPen = new Pen(Color.SlateGray, 2);
        foreach (var r in _data.Roads) foreach (var segment in r.Segments)
            e.Graphics.DrawLines(roadPen, segment.Select(_screen).ToArray());
        using var contextPen = new Pen(Color.FromArgb(100, 36, 140, 180), 1) { DashStyle = DashStyle.Dash };
        if (_context.Length > 1) e.Graphics.DrawLines(contextPen, _context.Select(_screen).ToArray());
        foreach (var site in _data.Sites)
        {
            var p = _screen(new(site.Longitude, site.Latitude));
            using var brush = new SolidBrush(site.Enabled ? Color.OrangeRed : Color.Gray);
            e.Graphics.FillEllipse(brush, p.X - 5, p.Y - 5, 10, 10);
            TextRenderer.DrawText(e.Graphics, site.Name, Font, new Point((int)p.X + 7, (int)p.Y), site.Enabled ? Color.DarkRed : Color.Gray);
        }
        TextRenderer.DrawText(e.Graphics, ReferenceOnly ? "灰：踏勘轨迹　虚线：工区参考" : "灰：参考道路　红：起降点　虚线：工区参考；道路不代表可通行", Font, new Point(8, 8), UiTheme.TextSecondary);
    }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (ReferenceOnly || e.Button != MouseButtons.Left || _screen is null) return;
        _down = e.Location; _dragSite = _data.Sites.OrderBy(s =>
        {
            var p = _screen(new(s.Longitude, s.Latitude)); return Math.Pow(p.X - e.X, 2) + Math.Pow(p.Y - e.Y, 2);
        }).FirstOrDefault(s =>
        {
            var p = _screen(new(s.Longitude, s.Latitude)); return Math.Pow(p.X - e.X, 2) + Math.Pow(p.Y - e.Y, 2) <= 144;
        })?.Id;
    }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (ReferenceOnly || e.Button != MouseButtons.Left || _world is null) return;
        if (_dragSite is null && Math.Abs(e.X - _down.X) + Math.Abs(e.Y - _down.Y) > 8) return;
        var target = _world(e.Location);
        PointChosen?.Invoke(target, _dragSite); _dragSite = null;
    }
}
