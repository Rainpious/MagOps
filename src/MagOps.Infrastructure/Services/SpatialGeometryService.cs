using System.Globalization;
using System.Xml.Linq;

namespace MagOps.Infrastructure.Services;

public sealed record GeoCoordinate(double Longitude, double Latitude, double? Elevation = null);

public sealed record SpatialPolygon(
    string Name,
    IReadOnlyList<GeoCoordinate> OuterRing,
    IReadOnlyList<IReadOnlyList<GeoCoordinate>> InnerRings);

public sealed class SpatialGeometry
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "LineString";
    public List<GeoCoordinate> Coordinates { get; set; } = [];
    public bool Closed { get; set; }
}

public sealed record SingleGeometryAudit(
    string Type,
    int PointCount,
    bool Closed,
    int DuplicatePoints,
    int SelfIntersections,
    double AreaDegree2,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    string Status);

public sealed record SpatialAuditSummary(
    int Count,
    string Status,
    IReadOnlyList<string> Errors,
    IReadOnlyList<SingleGeometryAudit> Geometries);

public sealed record SpatialRoundtripAudit(
    string Status,
    int DifferenceCount,
    IReadOnlyList<string> Differences,
    double Tolerance);

public static class SpatialGeometryService
{
    private static readonly XNamespace Kml = "http://www.opengis.net/kml/2.2";
    private static readonly XNamespace Gx = "http://www.google.com/kml/ext/2.2";
    private static readonly XNamespace Gpx = "http://www.topografix.com/GPX/1/1";

    public static string WriteKml(string path, IEnumerable<SpatialGeometry> geometries)
    {
        var document = new XElement(Kml + "Document");
        foreach (var geometry in geometries)
        {
            var placemark = new XElement(Kml + "Placemark");
            if (!string.IsNullOrWhiteSpace(geometry.Name))
                placemark.Add(new XElement(Kml + "name", geometry.Name));

            if (geometry.Type == "Polygon")
            {
                var coordinates = new XElement(Kml + "coordinates", CoordinateText(geometry.Coordinates, commaSeparated: true));
                placemark.Add(new XElement(Kml + "Polygon",
                    new XElement(Kml + "outerBoundaryIs",
                        new XElement(Kml + "LinearRing", coordinates))));
            }
            else if (geometry.Type == "Point")
            {
                placemark.Add(new XElement(Kml + "Point",
                    new XElement(Kml + "coordinates", CoordinateText(geometry.Coordinates, commaSeparated: true))));
            }
            else if (geometry.Type == "Track")
            {
                var track = new XElement(Gx + "Track");
                foreach (var point in geometry.Coordinates)
                    track.Add(new XElement(Gx + "coord", CoordinateText([point], commaSeparated: false)));
                placemark.Add(track);
            }
            else
            {
                placemark.Add(new XElement(Kml + "LineString",
                    new XElement(Kml + "coordinates", CoordinateText(geometry.Coordinates, commaSeparated: true))));
            }
            document.Add(placemark);
        }

        var doc = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(Kml + "kml",
                new XAttribute(XNamespace.Xmlns + "gx", Gx),
                document));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        doc.Save(path);
        return path;
    }

    public static List<SpatialPolygon> ParseKmlPolygons(string path, bool requireCompleteRings = false)
    {
        var doc = XDocument.Load(path);
        var polygons = new List<SpatialPolygon>();
        foreach (XElement placemark in doc.Descendants()
                     .Where(x => x.Name.LocalName == "Placemark"))
        {
            string name = placemark.Elements()
                .FirstOrDefault(x => x.Name.LocalName == "name")?.Value ?? "";
            foreach (XElement polygon in placemark.Descendants()
                         .Where(x => x.Name.LocalName == "Polygon"))
            {
                XElement? outerBoundary = polygon.Elements()
                    .FirstOrDefault(x => x.Name.LocalName == "outerBoundaryIs");
                List<GeoCoordinate> outer = ParseBoundaryRing(outerBoundary);
                if (requireCompleteRings && outer.Count < 3)
                    throw new InvalidDataException("边界含不足三个有效点的外环，不能忽略该区域。");
                if (outer.Count < 3)
                    continue;
                if (requireCompleteRings)
                {
                    if (polygon.Elements().Count(x => x.Name.LocalName == "outerBoundaryIs") != 1)
                        throw new InvalidDataException("每个区域必须有且仅有一个外环。");
                    foreach (var boundary in polygon.Elements().Where(x => x.Name.LocalName is "outerBoundaryIs" or "innerBoundaryIs"))
                    {
                        var coordinates = boundary.Descendants().Where(x => x.Name.LocalName == "coordinates").ToArray();
                        if (coordinates.Length != 1 || ParseBoundaryRing(boundary).Count < 3 ||
                            coordinates[0].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Any(token => token.Split(',').Length < 2))
                            throw new InvalidDataException("边界环缺失或含无效坐标，不能跳过孔洞或坐标点。");
                        var raw = ParseKmlCoordinateTokens(coordinates[0].Value);
                        bool closed = raw.Count > 1 && Math.Abs(raw[0].Longitude - raw[^1].Longitude) < 1e-12 &&
                            Math.Abs(raw[0].Latitude - raw[^1].Latitude) < 1e-12;
                        if (NormalizeGeoRing(raw).Count != raw.Count - (closed ? 1 : 0))
                            throw new InvalidDataException("边界包含连续重复点，请修复后重新测量。");
                    }
                }
                var holes = polygon.Elements()
                    .Where(x => x.Name.LocalName == "innerBoundaryIs")
                    .Select(ParseBoundaryRing)
                    .Where(x => x.Count >= 3)
                    .Select(x => (IReadOnlyList<GeoCoordinate>)x)
                    .ToArray();
                polygons.Add(new SpatialPolygon(name, outer, holes));
            }
        }
        if (requireCompleteRings && doc.Descendants().Count(x => x.Name.LocalName == "Polygon") != polygons.Count)
            throw new InvalidDataException("部分 Polygon 不在可读取的 Placemark 中，无法确认完整边界。");
        return polygons;
    }

    public static string WriteKmlPolygons(
        string path,
        IEnumerable<SpatialPolygon> polygons)
    {
        var document = new XElement(Kml + "Document");
        foreach (SpatialPolygon polygon in polygons)
        {
            var placemark = new XElement(Kml + "Placemark");
            if (!string.IsNullOrWhiteSpace(polygon.Name))
                placemark.Add(new XElement(Kml + "name", polygon.Name));
            var polygonElement = new XElement(Kml + "Polygon",
                BoundaryElement("outerBoundaryIs", polygon.OuterRing));
            foreach (IReadOnlyList<GeoCoordinate> hole in polygon.InnerRings)
                polygonElement.Add(BoundaryElement("innerBoundaryIs", hole));
            placemark.Add(polygonElement);
            document.Add(placemark);
        }
        var doc = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(Kml + "kml",
                new XAttribute(XNamespace.Xmlns + "gx", Gx),
                document));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        doc.Save(path);
        return path;
    }

    private static XElement BoundaryElement(
        string kind,
        IReadOnlyList<GeoCoordinate> ring) =>
        new(Kml + kind,
            new XElement(Kml + "LinearRing",
                new XElement(Kml + "coordinates",
                    CoordinateText(CloseRing(ring), commaSeparated: true))));

    private static List<GeoCoordinate> ParseBoundaryRing(XElement? boundary)
    {
        if (boundary is null) return [];
        XElement? ring = boundary.Descendants()
            .FirstOrDefault(x => x.Name.LocalName == "LinearRing");
        string text = ring?.Elements()
            .FirstOrDefault(x => x.Name.LocalName == "coordinates")?.Value ?? "";
        return NormalizeGeoRing(ParseKmlCoordinateTokens(text));
    }

    private static List<GeoCoordinate> NormalizeGeoRing(
        IReadOnlyList<GeoCoordinate> source)
    {
        var result = new List<GeoCoordinate>();
        foreach (GeoCoordinate point in source)
        {
            if (result.Count == 0 ||
                Math.Abs(result[^1].Longitude - point.Longitude) > 1e-12 ||
                Math.Abs(result[^1].Latitude - point.Latitude) > 1e-12)
                result.Add(point);
        }
        if (result.Count > 1 &&
            Math.Abs(result[0].Longitude - result[^1].Longitude) <= 1e-12 &&
            Math.Abs(result[0].Latitude - result[^1].Latitude) <= 1e-12)
            result.RemoveAt(result.Count - 1);
        return result;
    }

    private static IEnumerable<GeoCoordinate> CloseRing(
        IReadOnlyList<GeoCoordinate> source)
    {
        foreach (GeoCoordinate point in source) yield return point;
        if (source.Count > 0 &&
            (Math.Abs(source[0].Longitude - source[^1].Longitude) > 1e-12 ||
             Math.Abs(source[0].Latitude - source[^1].Latitude) > 1e-12))
            yield return source[0];
    }

    public static List<SpatialGeometry> ParseKml(string path)
    {
        var doc = XDocument.Load(path);
        var geometries = new List<SpatialGeometry>();

        foreach (var placemark in doc.Descendants().Where(x => x.Name.LocalName == "Placemark"))
        {
            string name = placemark.Elements().FirstOrDefault(x => x.Name.LocalName == "name")?.Value ?? "";
            foreach (var node in placemark.Descendants())
            {
                string kind = node.Name.LocalName;
                if (kind is "Point" or "LineString" or "LinearRing")
                {
                    string coordinateText = node.Elements().FirstOrDefault(x => x.Name.LocalName == "coordinates")?.Value ?? "";
                    var values = ParseKmlCoordinateTokens(coordinateText);
                    if (values.Count == 0) continue;
                    geometries.Add(new SpatialGeometry
                    {
                        Name = name,
                        Type = kind == "Point" ? "Point" : kind == "LinearRing" ? "Polygon" : "LineString",
                        Coordinates = values,
                        Closed = values.Count > 0 && values[0] == values[^1]
                    });
                }
                else if (kind == "Track")
                {
                    var values = new List<GeoCoordinate>();
                    foreach (var child in node.Elements().Where(x => x.Name.LocalName == "coord"))
                    {
                        string[] parts = child.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length < 2) continue;
                        double lon = ParseDouble(parts[0], "KML Track longitude");
                        double lat = ParseDouble(parts[1], "KML Track latitude");
                        ValidateLonLat(lon, lat);
                        double? z = parts.Length > 2 ? ParseDouble(parts[2], "KML Track elevation") : null;
                        values.Add(new GeoCoordinate(lon, lat, z));
                    }
                    if (values.Count > 0)
                    {
                        geometries.Add(new SpatialGeometry
                        {
                            Name = name,
                            Type = "Track",
                            Coordinates = values,
                            Closed = false
                        });
                    }
                }
            }
        }
        return geometries;
    }

    public static string WriteGpx(string path, IEnumerable<SpatialGeometry> geometries)
    {
        var root = new XElement(Gpx + "gpx",
            new XAttribute("version", "1.1"),
            new XAttribute("creator", "magops C#"));

        foreach (var geometry in geometries)
        {
            if (geometry.Type == "Point")
            {
                if (geometry.Coordinates.Count == 0)
                    throw new InvalidDataException("Point 几何没有坐标");
                var p = geometry.Coordinates[0];
                ValidateLonLat(p.Longitude, p.Latitude);
                var wpt = new XElement(Gpx + "wpt",
                    new XAttribute("lon", Format(p.Longitude)),
                    new XAttribute("lat", Format(p.Latitude)));
                if (!string.IsNullOrWhiteSpace(geometry.Name))
                    wpt.Add(new XElement(Gpx + "name", geometry.Name));
                if (p.Elevation.HasValue)
                    wpt.Add(new XElement(Gpx + "ele", Format(p.Elevation.Value)));
                root.Add(wpt);
            }
            else
            {
                var trk = new XElement(Gpx + "trk");
                if (!string.IsNullOrWhiteSpace(geometry.Name))
                    trk.Add(new XElement(Gpx + "name", geometry.Name));
                var seg = new XElement(Gpx + "trkseg");
                foreach (var p in geometry.Coordinates)
                {
                    ValidateLonLat(p.Longitude, p.Latitude);
                    var trkpt = new XElement(Gpx + "trkpt",
                        new XAttribute("lon", Format(p.Longitude)),
                        new XAttribute("lat", Format(p.Latitude)));
                    if (p.Elevation.HasValue)
                        trkpt.Add(new XElement(Gpx + "ele", Format(p.Elevation.Value)));
                    seg.Add(trkpt);
                }
                trk.Add(seg);
                root.Add(trk);
            }
        }

        var doc = new XDocument(new XDeclaration("1.0", "utf-8", null), root);
        return AtomicFileWriter.Write(path, stream => doc.Save(stream));
    }

    public static List<SpatialGeometry> ParseGpx(string path)
    {
        var doc = XDocument.Load(path);
        var result = new List<SpatialGeometry>();

        foreach (var node in doc.Descendants().Where(x => x.Name.LocalName == "wpt"))
        {
            if (!TryLonLat(node, out double lon, out double lat)) continue;
            double? ele = TryElevation(node);
            string name = node.Elements().FirstOrDefault(x => x.Name.LocalName == "name")?.Value ?? "";
            result.Add(new SpatialGeometry
            {
                Name = name,
                Type = "Point",
                Coordinates = [new GeoCoordinate(lon, lat, ele)],
                Closed = false
            });
        }

        foreach (var route in doc.Descendants().Where(x => x.Name.LocalName == "rte"))
        {
            var values = route.Elements().Where(x => x.Name.LocalName == "rtept")
                .Where(x => TryLonLat(x, out _, out _))
                .Select(x =>
                {
                    TryLonLat(x, out double lon, out double lat);
                    return new GeoCoordinate(lon, lat, TryElevation(x));
                })
                .ToList();
            if (values.Count > 0)
            {
                result.Add(new SpatialGeometry
                {
                    Name = route.Elements().FirstOrDefault(x => x.Name.LocalName == "name")?.Value ?? "",
                    Type = "LineString",
                    Coordinates = values,
                    Closed = false
                });
            }
        }

        foreach (var segment in doc.Descendants().Where(x => x.Name.LocalName == "trkseg"))
        {
            var values = segment.Elements().Where(x => x.Name.LocalName == "trkpt")
                .Where(x => TryLonLat(x, out _, out _))
                .Select(x =>
                {
                    TryLonLat(x, out double lon, out double lat);
                    return new GeoCoordinate(lon, lat, TryElevation(x));
                })
                .ToList();
            if (values.Count == 0) continue;
            var track = segment.Parent;
            string name = track?.Elements().FirstOrDefault(x => x.Name.LocalName == "name")?.Value ?? "";
            result.Add(new SpatialGeometry
            {
                Name = name,
                Type = "Track",
                Coordinates = values,
                Closed = false
            });
        }

        return result;
    }

    public static SingleGeometryAudit AuditGeometry(SpatialGeometry geometry, double tolerance = 1e-9)
    {
        var points = geometry.Coordinates.Select(p => (p.Longitude, p.Latitude)).ToList();
        string kind = geometry.Type ?? "";
        bool closed = points.Count > 0 && points[0] == points[^1];

        var duplicateCandidates = closed ? points.Take(points.Count - 1).ToList() : points;
        int duplicatePoints = duplicateCandidates.Count - duplicateCandidates.Distinct().Count();
        var errors = new List<string>();
        var warnings = new List<string>();

        if (points.Count == 0) errors.Add("空几何");
        foreach (var (lon, lat) in points)
        {
            try { ValidateLonLat(lon, lat); }
            catch (Exception ex) { errors.Add(ex.Message); }
        }

        if (duplicatePoints > 0) warnings.Add($"重复点 {duplicatePoints} 个");
        if (kind == "Polygon" && !closed) errors.Add("Polygon 外环未闭合");
        if ((kind is "LineString" or "Track" or "Polygon") && points.Count < 2)
            errors.Add("点数不足");

        var intersections = new List<(int A, int B)>();
        for (int i = 0; i < points.Count - 1; i++)
        {
            for (int j = i + 2; j < points.Count - 1; j++)
            {
                if (i == 0 && j == points.Count - 2 && closed) continue;
                if (SegmentsIntersect(points[i], points[i + 1], points[j], points[j + 1], tolerance))
                    intersections.Add((i, j));
            }
        }
        if (intersections.Count > 0) warnings.Add($"发现自交 {intersections.Count} 处");

        double area = 0d;
        if (kind == "Polygon" && points.Count >= 3)
        {
            for (int i = 0; i < points.Count; i++)
            {
                var a = points[i];
                var b = points[(i + 1) % points.Count];
                area += a.Longitude * b.Latitude - b.Longitude * a.Latitude;
            }
            area = Math.Abs(area / 2d);
        }

        string status = errors.Count > 0 ? "BLOCKED" : warnings.Count > 0 ? "REVIEW_REQUIRED" : "PASSED";
        return new SingleGeometryAudit(kind, points.Count, closed, duplicatePoints,
            intersections.Count, area, errors, warnings, status);
    }

    public static SpatialAuditSummary AuditGeometries(IEnumerable<SpatialGeometry> geometries)
    {
        var audits = geometries.Select(x => AuditGeometry(x)).ToList();
        if (audits.Count == 0)
            return new SpatialAuditSummary(0, "BLOCKED", ["未发现可审计几何"], []);

        string status = audits.Any(x => x.Status == "BLOCKED") ? "BLOCKED"
            : audits.Any(x => x.Status == "REVIEW_REQUIRED") ? "REVIEW_REQUIRED" : "PASSED";
        return new SpatialAuditSummary(audits.Count, status, [], audits);
    }

    public static SpatialRoundtripAudit AuditRoundtrip(
        IEnumerable<SpatialGeometry> original,
        IEnumerable<SpatialGeometry> regenerated,
        double tolerance = 1e-9)
    {
        var left = original.ToList();
        var right = regenerated.ToList();
        var differences = new List<string>();

        if (left.Count != right.Count)
            differences.Add($"geometry_count expected={left.Count} actual={right.Count}");

        int geometryCount = Math.Min(left.Count, right.Count);
        for (int i = 0; i < geometryCount; i++)
        {
            var a = left[i].Coordinates;
            var b = right[i].Coordinates;
            if (a.Count != b.Count)
            {
                differences.Add($"geometry[{i}] point_count expected={a.Count} actual={b.Count}");
                continue;
            }
            for (int j = 0; j < a.Count; j++)
            {
                if (Math.Abs(a[j].Longitude - b[j].Longitude) > tolerance
                    || Math.Abs(a[j].Latitude - b[j].Latitude) > tolerance)
                    differences.Add($"geometry[{i}] point[{j}] expected=({a[j].Longitude},{a[j].Latitude}) actual=({b[j].Longitude},{b[j].Latitude})");
            }
        }

        return new SpatialRoundtripAudit(
            differences.Count == 0 ? "PASSED" : "REVIEW_REQUIRED",
            differences.Count,
            differences,
            tolerance);
    }

    private static List<GeoCoordinate> ParseKmlCoordinateTokens(string text)
    {
        var result = new List<GeoCoordinate>();
        foreach (string token in (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = token.Split(',');
            if (parts.Length < 2) continue;
            double lon = ParseDouble(parts[0], "longitude");
            double lat = ParseDouble(parts[1], "latitude");
            ValidateLonLat(lon, lat);
            double? z = parts.Length > 2 && !string.IsNullOrWhiteSpace(parts[2])
                ? ParseDouble(parts[2], "elevation")
                : null;
            result.Add(new GeoCoordinate(lon, lat, z));
        }
        return result;
    }

    private static bool TryLonLat(XElement node, out double lon, out double lat)
    {
        lon = lat = 0d;
        if (!double.TryParse(node.Attribute("lon")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out lon)
            || !double.TryParse(node.Attribute("lat")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out lat))
            return false;
        try { ValidateLonLat(lon, lat); return true; }
        catch { return false; }
    }

    private static double? TryElevation(XElement node)
    {
        string? value = node.Elements().FirstOrDefault(x => x.Name.LocalName == "ele")?.Value;
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double z) ? z : null;
    }

    private static string CoordinateText(IEnumerable<GeoCoordinate> points, bool commaSeparated)
    {
        return string.Join(" ", points.Select(p =>
        {
            string separator = commaSeparated ? "," : " ";
            return p.Elevation.HasValue
                ? $"{Format(p.Longitude)}{separator}{Format(p.Latitude)}{separator}{Format(p.Elevation.Value)}"
                : $"{Format(p.Longitude)}{separator}{Format(p.Latitude)}";
        }));
    }

    private static double Orientation((double Longitude, double Latitude) a,
        (double Longitude, double Latitude) b, (double Longitude, double Latitude) c) =>
        (b.Longitude - a.Longitude) * (c.Latitude - a.Latitude)
        - (b.Latitude - a.Latitude) * (c.Longitude - a.Longitude);

    private static bool SegmentsIntersect(
        (double Longitude, double Latitude) a,
        (double Longitude, double Latitude) b,
        (double Longitude, double Latitude) c,
        (double Longitude, double Latitude) d,
        double tolerance)
    {
        double o1 = Orientation(a, b, c);
        double o2 = Orientation(a, b, d);
        double o3 = Orientation(c, d, a);
        double o4 = Orientation(c, d, b);
        return ((o1 > tolerance && o2 < -tolerance) || (o1 < -tolerance && o2 > tolerance))
            && ((o3 > tolerance && o4 < -tolerance) || (o3 < -tolerance && o4 > tolerance));
    }

    private static void ValidateLonLat(double lon, double lat)
    {
        if (!double.IsFinite(lon) || lon < -180 || lon > 180)
            throw new FormatException($"经度超出范围: {lon}");
        if (!double.IsFinite(lat) || lat < -90 || lat > 90)
            throw new FormatException($"纬度超出范围: {lat}");
    }

    private static double ParseDouble(string value, string label)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)
            || !double.IsFinite(number))
            throw new FormatException($"{label} 不是有效数字: {value}");
        return number;
    }

    private static string Format(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
