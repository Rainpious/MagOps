using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace MagOps.Core.Routes;

public sealed record AccessPoint(double Longitude, double Latitude);
public sealed record ReferenceRoad(string Id, string Name, string SourcePath, string Sha256, List<List<AccessPoint>> Segments, string Crs = "WGS84", bool UseForRecommendations = false);
public sealed record LaunchSite(string Id, string Name, double Longitude, double Latitude, bool Enabled = true,
    string? RoadId = null, int? SegmentIndex = null, int? EdgeIndex = null, double? EdgeFraction = null, double? SnapDistanceM = null);
public sealed record AccessPlanningData
{
    public string SchemaVersion { get; init; } = "magops-access-planning-1";
    public List<ReferenceRoad> Roads { get; init; } = [];
    public List<LaunchSite> Sites { get; init; } = [];
    public Dictionary<string, string> SortieSiteLocks { get; init; } = [];
    public const int AutomaticSiteCount = -1;
    public int MaxSuggestedSites { get; init; } = AutomaticSiteCount;
    public bool SnapEnabled { get; init; }
    public double SnapToleranceM { get; init; } = 50;
}
public sealed record SortieAccessAssessment(string SortieKey, string SortieName, string? SiteId, string SiteName,
    AccessPoint? Site, AccessPoint? First, AccessPoint? Last, double WorkM, double? OutboundM, double? ReturnM, double? TotalM,
    string Status, bool Locked, string Fingerprint);
public sealed record RoadSnap(AccessPoint Point, string RoadId, int SegmentIndex, int EdgeIndex, double Fraction, double DistanceM);

/// <summary>Access legs remain independent of formal survey geometry and flight direction.</summary>
public static class RouteAccessPlanner
{
    public static bool Valid(AccessPoint p) => double.IsFinite(p.Longitude) && double.IsFinite(p.Latitude) &&
        p.Longitude is >= -180 and <= 180 && p.Latitude is > -89 and < 89;
    public static void Validate(AccessPlanningData d)
    {
        if (d.Roads is null || d.Sites is null || d.SortieSiteLocks is null ||
            d.SchemaVersion != "magops-access-planning-1" || !double.IsFinite(d.SnapToleranceM) || d.SnapToleranceM <= 0 ||
            d.Sites.Any(s => string.IsNullOrWhiteSpace(s.Id) || string.IsNullOrWhiteSpace(s.Name) || !Valid(new(s.Longitude, s.Latitude))) ||
            d.Sites.Select(s => s.Id).Distinct().Count() != d.Sites.Count || d.Roads.Select(r => r.Id).Distinct().Count() != d.Roads.Count ||
            d.Roads.Any(r => string.IsNullOrWhiteSpace(r.Id) || r.Segments is null || r.Segments.Count == 0 || r.Segments.Any(s => s is null || s.Count < 2 || s.Any(p => !Valid(p)))))
            throw new InvalidDataException("道路/起降点坐标、身份、版本或吸附容差无效。");
    }
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static string SortieKey(RouteSortie s) => Hash(JsonSerializer.Serialize(s.Points.Select(p => new { p.X, p.Y, p.Pid })));
    public static AccessPoint Geographic(RouteEndpoint point, RoutePlanResult plan)
    {
        var ll = RouteCoordinateMath.ToLonLat(point, plan.Mode, plan.CentralMeridian);
        var result = new AccessPoint(ll.Lon, ll.Lat);
        if (!Valid(result)) throw new InvalidDataException("航线坐标不能转换为有效经纬度。");
        return result;
    }
    public static double Distance(AccessPoint a, AccessPoint b) => RouteCoordinateMath.Haversine(a.Longitude, a.Latitude, b.Longitude, b.Latitude);
    public static SortieAccessAssessment Assess(RoutePlanResult plan, RouteSortie sortie)
    {
        if (plan.AccessPlanning is { } data) Validate(data);
        return AssessValidated(plan, sortie, Hash(JsonSerializer.Serialize(plan.AccessPlanning)), plan.AccessPlanning);
    }
    private static SortieAccessAssessment AssessValidated(RoutePlanResult plan, RouteSortie sortie, string dataFingerprint, AccessPlanningData? d)
    {
        string key = SortieKey(sortie);
        string fingerprint = Hash(JsonSerializer.Serialize(new { key, plan.Mode, plan.CentralMeridian, plan.Options.MaxSortieKm, plan.Options.TakeoffX, plan.Options.TakeoffY, dataFingerprint }));
        double work = RouteCoordinateMath.RouteLength(sortie.Points, plan.Mode, plan.CentralMeridian);
        if (sortie.Points.Count < 2) return new(key, sortie.Name, null, "", null, null, null, work, null, null, null, "EMPTY", false, fingerprint);
        var a = Geographic(sortie.Points[0], plan); var b = Geographic(sortie.Points[^1], plan);
        var candidates = d?.Sites.Where(s => s.Enabled).ToArray() ?? [];
        bool locked = d is not null && d.SortieSiteLocks.ContainsKey(key);
        if (locked)
        {
            string id = d!.SortieSiteLocks[key];
            candidates = candidates.Where(s => s.Id == id).ToArray();
            if (candidates.Length == 0) return new(key, sortie.Name, id, "锁定点不可用", null, a, b, work, null, null, null, "LOCK_UNAVAILABLE", true, fingerprint);
        }
        if (candidates.Length == 0 && (d is null || d.Sites.Count == 0) && plan.Options.TakeoffX is { } tx && plan.Options.TakeoffY is { } ty)
        {
            var old = Geographic(new RouteEndpoint { X = tx, Y = ty }, plan);
            candidates = [new("legacy", "旧版单起飞点", old.Longitude, old.Latitude)];
        }
        var ranked = candidates.Select(s =>
        {
            var p = new AccessPoint(s.Longitude, s.Latitude);
            return new { Site = s, Point = p, Go = Distance(p, a), Back = Distance(b, p) };
        }).OrderBy(s => s.Go + s.Back).ThenBy(s => s.Site.Id, StringComparer.Ordinal).FirstOrDefault();
        if (ranked is null) return new(key, sortie.Name, null, "未设置起降点", null, a, b, work, null, null, null, "NO_SITE", false, fingerprint);
        double total = work + ranked.Go + ranked.Back;
        return new(key, sortie.Name, ranked.Site.Id, ranked.Site.Name, ranked.Point, a, b, work, ranked.Go, ranked.Back, total,
            total > plan.Options.MaxSortieKm * 1000 ? "OVER_LIMIT" : "OK", locked, fingerprint);
    }
    public static IReadOnlyList<SortieAccessAssessment> Assess(RoutePlanResult plan) => Assess(plan, plan.AccessPlanning);
    public static IReadOnlyList<SortieAccessAssessment> Assess(RoutePlanResult plan, AccessPlanningData? data)
    {
        if (data is not null) Validate(data);
        string fingerprint = Hash(JsonSerializer.Serialize(data));
        return plan.Sorties.Select(s => AssessValidated(plan, s, fingerprint, data)).ToArray();
    }
    public static RoadSnap? Snap(AccessPoint p, IReadOnlyList<ReferenceRoad> roads, double toleranceM)
    {
        if (!Valid(p) || !double.IsFinite(toleranceM) || toleranceM <= 0) throw new ArgumentException("吸附位置或容差无效。");
        RoadSnap? best = null;
        double sx = Math.PI / 180 * RouteCoordinateMath.EarthRadiusM * Math.Cos(p.Latitude * Math.PI / 180);
        double sy = Math.PI / 180 * RouteCoordinateMath.EarthRadiusM;
        double Delta(double lon) => Math.IEEERemainder(lon - p.Longitude, 360);
        foreach (var road in roads)
        for (int si = 0; si < road.Segments.Count; si++)
        for (int ei = 0; ei < road.Segments[si].Count - 1; ei++)
        {
            var a = road.Segments[si][ei]; var b = road.Segments[si][ei + 1];
            double ax = Delta(a.Longitude) * sx, ay = (a.Latitude - p.Latitude) * sy;
            double dx = Delta(b.Longitude) * sx - ax, dy = (b.Latitude - a.Latitude) * sy;
            double length2 = dx * dx + dy * dy;
            double t = length2 > 0 ? Math.Clamp(-(ax * dx + ay * dy) / length2, 0, 1) : 0;
            var foot = new AccessPoint(Math.IEEERemainder(p.Longitude + (ax + dx * t) / sx, 360), p.Latitude + (ay + dy * t) / sy);
            double distance = Distance(p, foot);
            if (distance <= toleranceM && (best is null || distance < best.DistanceM)) best = new(foot, road.Id, si, ei, t, distance);
        }
        return best;
    }
}
