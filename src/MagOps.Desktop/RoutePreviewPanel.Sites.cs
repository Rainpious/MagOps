using MagOps.Core.Coordinates;
using MagOps.Core.Routes;
namespace MagOps.Desktop;

internal sealed partial class RoutePreviewPanel
{
    internal RoutePlanResult? AssessedPlan;
    internal IReadOnlyList<SortieAccessAssessment>? ConfirmedRows;
    internal AccessPlanningData? SiteDraft;
    internal IReadOnlyList<SortieAccessAssessment>? DraftRows;
    internal IReadOnlyList<LaunchSite> Suggestions = [];
    internal HashSet<string> DraftNewIds = [];
    internal string? SelectedSite;
    internal bool DrawingCut => _drawCutLine;
    internal event Action<string?,AccessPoint>? PlaceSite;
    internal event Action? SiteSelectionChanged;
    private Point? _siteDown;
    private Point _sitePointer;
    internal void ClearSiteGesture() { _siteDown = null; Capture = false; SelectedSite = null; }
    private void BeginSiteGesture(Point mouse)
    {
        if (!TryCreateTransform(out var screen,out _)) return;
        _siteDown = _sitePointer = mouse;
        Capture = true;
        SelectedSite = SiteDraft!.Sites.Select(s => (s.Id, pos:screen(AccessPhysical(new(s.Longitude,s.Latitude)))))
            .Select(s => (s.Id, d: Math.Pow(s.pos.X-mouse.X,2)+Math.Pow(s.pos.Y-mouse.Y,2)))
            .Where(s => s.d <= 144).OrderBy(s=>s.d).FirstOrDefault().Id;
        SiteSelectionChanged?.Invoke();
        Invalidate();
    }
    private AccessPoint? SiteAt(Point mouse)
    {
        if (WorldFromScreen(mouse) is not { } w || Plan is null) return null;
        if (Plan.Mode != "projected") return new(w.X,w.Y);
        var ll = CoordinateService.GaussKrugerInverse(w.X,w.Y,Plan.CentralMeridian,"WGS84");
        return new(ll.Longitude,ll.Latitude);
    }
    private void EndSiteGesture(Point mouse)
    {
        if (_siteDown is not { } start) return;
        _siteDown = null; Capture = false; Invalidate();
        bool dragged = Math.Abs(start.X-mouse.X)+Math.Abs(start.Y-mouse.Y) >= 5;
        if ((SelectedSite is null) == dragged) return;
        if (SiteAt(mouse) is { } p && RouteAccessPlanner.Valid(p)) PlaceSite?.Invoke(SelectedSite,p);
        Invalidate();
    }
    private void DrawDragTarget(Graphics g, Func<(double X,double Y),PointF> screen)
    {
        if (!_siteDown.HasValue || SiteDraft is not { } d || SiteAt(_sitePointer) is not { } p || !RouteAccessPlanner.Valid(p)) return;
        var target = d.SnapEnabled ? RouteAccessPlanner.Snap(p,d.Roads,d.SnapToleranceM)?.Point ?? p : p;
        var at = screen(AccessPhysical(target));
        g.DrawEllipse(Pens.Blue,at.X-7,at.Y-7,14,14);
    }
}
