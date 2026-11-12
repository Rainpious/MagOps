using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using MagOps.Core.Coordinates;
using MagOps.Core.Routes;
using MagOps.Infrastructure.Storage;
namespace MagOps.Infrastructure.Services;

public sealed class AccessPlanningService
{
    private readonly JsonStore _store = new();
    private static string Key(string workarea) => workarea.Replace('\\', '/').Trim('/').ToUpperInvariant();
    public AccessPlanningData Load(string root, string workarea)
    {
        var all = _store.Read(ProjectPaths.Json(root, "access_planning.json"), new Dictionary<string, AccessPlanningData>());
        var data = all.GetValueOrDefault(Key(workarea)) ?? new();
        RouteAccessPlanner.Validate(data); return data;
    }
    public void Save(string root, string workarea, AccessPlanningData data)
    {
        if (string.IsNullOrWhiteSpace(workarea)) throw new InvalidDataException("请选择工区后保存道路和起降点。");
        RouteAccessPlanner.Validate(data);
        using var guard = WorkspaceFileLock.AcquireForRoot(root);
        var path = ProjectPaths.Json(root, "access_planning.json");
        var all = _store.Read(path, new Dictionary<string, AccessPlanningData>());
        all[Key(workarea)] = data; _store.Write(path, all);
    }
    public static ReferenceRoad Import(string path, ProjectionParameters? csvProjection = null, bool includePolygonBoundaries = false)
    {
        string full = Path.GetFullPath(path), ext = Path.GetExtension(full).ToLowerInvariant();
        List<List<AccessPoint>> segments = [];
        double N(string text) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n)
            ? n : throw new InvalidDataException("轨迹坐标包含无效数字。");
        byte[] sourceBytes = File.ReadAllBytes(full);
        string crs = csvProjection is null ? "WGS84" : JsonSerializer.Serialize(CoordinateProjectionService.Normalize(csvProjection));
        if (ext == ".ovobj")
        {
            segments.Add(OviSurveyTrack.Decode(sourceBytes));
            crs = "未标注";
        }
        else if (ext is ".kml" or ".ovkml" or ".gpx")
        {
            using var input = new MemoryStream(sourceBytes);
            var doc = XDocument.Load(input);
            if (ext == ".ovkml")
            {
                string[] labels = doc.Descendants().Where(e => e.Name.LocalName == "OvCoordType")
                    .Select(e => e.Value.Trim().ToUpperInvariant()).Distinct().ToArray();
                if (labels.Any(c => c is not ("CGCS2000" or "WGS84")) || labels.Length > 1)
                    throw new InvalidDataException("踏勘轨迹需统一的 WGS84 或 CGCS2000 经纬度；请核对导出坐标类型。");
                crs = labels.FirstOrDefault() ?? "未标注";
            }
            if (ext is ".kml" or ".ovkml")
            {
                foreach (var line in doc.Descendants().Where(e => e.Name.LocalName == "LineString"))
                {
                    var coords = line.Elements().SingleOrDefault(e => e.Name.LocalName == "coordinates") ?? throw new InvalidDataException("KML线缺少坐标。");
                    segments.Add(coords.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(token =>
                    {
                        var c = token.Split(','); if (c.Length < 2) throw new InvalidDataException("KML轨迹坐标不完整。");
                        return new AccessPoint(N(c[0]), N(c[1]));
                    }).ToList());
                }
                foreach (var track in doc.Descendants().Where(e => e.Name.LocalName == "Track"))
                    segments.Add(track.Elements().Where(e => e.Name.LocalName == "coord").Select(e =>
                    {
                        var c = e.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                        if (c.Length < 2) throw new InvalidDataException("KML gx:Track坐标不完整。");
                        return new AccessPoint(N(c[0]), N(c[1]));
                    }).ToList());
                if (includePolygonBoundaries)
                    foreach (var ring in doc.Descendants().Where(e => e.Name.LocalName == "LinearRing" &&
                        e.Ancestors().Any(a => a.Name.LocalName == "Polygon")))
                    {
                        var coords = ring.Elements().SingleOrDefault(e => e.Name.LocalName == "coordinates")
                            ?? throw new InvalidDataException("KML边界环缺少坐标。");
                        var points = coords.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(token =>
                        {
                            var c = token.Split(',');
                            if (c.Length < 2) throw new InvalidDataException("KML边界坐标不完整。");
                            return new AccessPoint(N(c[0]), N(c[1]));
                        }).ToList();
                        if (points.Count < 3) throw new InvalidDataException("KML边界环需至少三个点。");
                        if (points[0] != points[^1]) points.Add(points[0]);
                        segments.Add(points);
                    }
            }
            else
            {
                foreach (var segment in doc.Descendants().Where(e => e.Name.LocalName is "trkseg" or "rte"))
                    segments.Add(segment.Elements().Where(e => e.Name.LocalName is "trkpt" or "rtept")
                        .Select(e => new AccessPoint(N((string?)e.Attribute("lon") ?? ""), N((string?)e.Attribute("lat") ?? ""))).ToList());
            }
        }
        else if (ext is ".csv" or ".tsv" or ".txt")
        {
            List<AccessPoint> current = [];
            bool firstData = true;
            void End() { if (current.Count > 0) { segments.Add(current); current = []; } }
            using var input = new StreamReader(new MemoryStream(sourceBytes));
            while (input.ReadLine() is { } raw)
            {
                string line = raw.Trim();
                if (line.Length == 0) { End(); continue; }
                if (line.StartsWith('#')) continue;
                var c = line.Split(line.Contains('\t') ? '\t' : ',', StringSplitOptions.TrimEntries);
                if (firstData && c.Length >= 2 &&
                    (c[0].Equals("longitude", StringComparison.OrdinalIgnoreCase) || c[0].Equals("lon", StringComparison.OrdinalIgnoreCase) ||
                     c[0] is "经度" or "x" or "X" or "easting" or "Easting")) { firstData = false; continue; }
                firstData = false;
                if (c.Length < 2) throw new InvalidDataException("CSV需两列经纬度或明确投影XY；空行表示断段。");
                double x = N(c[0]), y = N(c[1]);
                if (csvProjection is null) current.Add(new(x, y));
                else
                {
                    var p = csvProjection;
                    var ll = p.AxisOrder == XyAxisOrder.NorthingEasting ? CoordinateProjectionService.Inverse(y, x, p) : CoordinateProjectionService.Inverse(x, y, p);
                    current.Add(new(ll.Longitude, ll.Latitude));
                }
            }
            End();
        }
        else throw new NotSupportedException("道路参考支持OVKML、OVOBJ轨迹、KML、GPX及两列CSV/TSV/TXT。");
        if (segments.Count == 0 || segments.Any(s => s.Count < 2 || s.Any(p => !RouteAccessPlanner.Valid(p))))
            throw new InvalidDataException("道路轨迹需至少一条包含两个有效点的线；单点段、无效点或非WGS84坐标拒绝。未导入部分结果。");
        string hash = Convert.ToHexString(SHA256.HashData(sourceBytes));
        return new(Guid.NewGuid().ToString("N"), Path.GetFileName(full), full, hash, segments,
            crs);
    }
    public static void ExportAssessment(string path, RoutePlanResult plan)
    {
        var rows = RouteAccessPlanner.Assess(plan);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string Q(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
        string F(double? value) => value?.ToString("0.########", CultureInfo.InvariantCulture) ?? "";
        string full = Path.GetFullPath(path);
        string staged = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var w = new StreamWriter(staged, false, new UTF8Encoding(true)))
            {
                w.WriteLine("Sortie,SiteId,SiteName,SiteLon,SiteLat,Work_m,Outbound_m,Return_m,Total_m,Limit_m,Status,Locked,Fingerprint");
                foreach (var r in rows)
                    w.WriteLine(string.Join(",", Q(r.SortieName), Q(r.SiteId ?? ""), Q(r.SiteName), F(r.Site?.Longitude), F(r.Site?.Latitude),
                        F(r.WorkM), F(r.OutboundM), F(r.ReturnM), F(r.TotalM), F(plan.Options.MaxSortieKm * 1000), Q(r.Status), r.Locked, Q(r.Fingerprint)));
            }
            File.Move(staged, full, true);
        }
        finally { if (File.Exists(staged)) File.Delete(staged); }
    }
}
