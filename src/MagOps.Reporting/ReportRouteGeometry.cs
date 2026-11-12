using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace MagOps.Reporting;

internal sealed class ReportRouteGeometry
{
    internal sealed record Vertex(double Lon, double Lat, double? Height);
    internal sealed record Hit(double Along, double Offset, double? Height);
    internal sealed record HorizontalHit(double? Offset, string Status);
    public required Vertex[] Vertices { get; init; }
    public required string AltitudeMode { get; init; }
    private int[]? _segments;
    public ReportRouteGeometry Restrict(ReportRouteGeometry corridor) => new()
    {
        Vertices = Vertices, AltitudeMode = AltitudeMode,
        _segments = Enumerable.Range(1, Vertices.Length - 1).Where(i => corridor.OnAxis(Vertices[i - 1], Vertices[i])).ToArray()
    };
    public double LengthM => Enumerable.Range(1, Vertices.Length - 1)
        .Sum(i => Distance(Vertices[i - 1].Lon, Vertices[i - 1].Lat, Vertices[i].Lon, Vertices[i].Lat));

    public bool IsStraight
    {
        get
        {
            var a = Vertices[0]; var b = Vertices[^1];
            var (x, y) = XY(b.Lon, b.Lat, a.Lon, a.Lat);
            double length = Math.Sqrt(x * x + y * y);
            double previous = double.NegativeInfinity;
            return length >= .01 && Vertices.All(v =>
            {
                var (px, py) = XY(v.Lon, v.Lat, a.Lon, a.Lat);
                double along = (px * x + py * y) / length;
                bool monotonic = along >= previous - .01 && along >= -.01 && along <= length + .01;
                previous = along;
                return monotonic && Math.Abs(px * y - py * x) / length <= 1;
            });
        }
    }

    /// <summary>Horizontal finite-polyline distance, independent of height and matching tolerance.</summary>
    public HorizontalHit EvaluateHorizontal(double lon, double lat)
    {
        double best = double.PositiveInfinity;
        bool outside = false;
        foreach (int i in _segments ??= Enumerable.Range(1, Vertices.Length - 1).ToArray())
        {
            var a = Vertices[i - 1]; var b = Vertices[i];
            // Use the segment midpoint for both axes. A start-latitude scale changes
            // the distance when the same oblique design line is reversed.
            double Wrap(double degrees) => degrees - 360 * Math.Floor((degrees + 180) / 360);
            double deltaLon = Wrap(b.Lon - a.Lon);
            double midLon = a.Lon + deltaLon / 2, midLat = (a.Lat + b.Lat) / 2;
            double unit = Math.PI / 180 * 6371008.8;
            double eastUnit = unit * Math.Cos(midLat * Math.PI / 180);
            double x = deltaLon * eastUnit, y = (b.Lat - a.Lat) * unit;
            double px = Wrap(lon - midLon) * eastUnit + x / 2;
            double py = (lat - midLat) * unit + y / 2;
            double squared = x * x + y * y;
            if (squared <= 0 || !double.IsFinite(squared)) continue;
            double raw = (px * x + py * y) / squared;
            double t = Math.Clamp(raw, 0, 1);
            double distance = Math.Sqrt(Math.Pow(px - t * x, 2) + Math.Pow(py - t * y, 2));
            bool endOutside = i == 1 && raw < -1e-8 || i == Vertices.Length - 1 && raw > 1 + 1e-8;
            if (distance < best - 1e-4)
            { best = distance; outside = endOutside; }
            else if (Math.Abs(distance - best) <= 1e-4)
            { best = Math.Min(best, distance); outside &= endOutside; }
        }
        return !double.IsFinite(best) ? new(null, "INVALID_GEOMETRY")
            : outside ? new(null, "END_OUTSIDE") : new(best, "VALID");
    }

    public static ReportRouteGeometry Read(string path, string name)
    {
        using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var doc = XDocument.Load(reader);
        var marks = doc.Descendants().Where(e => e.Name.LocalName == "Placemark")
            .Where(e => string.IsNullOrWhiteSpace(name) || e.Elements().Any(c => c.Name.LocalName == "name" && c.Value == name))
            .SelectMany(e => e.Descendants().Where(c => c.Name.LocalName == "LineString")).ToArray();
        if (marks.Length != 1) throw new InvalidDataException("KML测线必须唯一，请指定准确线名称：" + Path.GetFileName(path));
        var line = marks[0];
        string coordinates = line.Elements().SingleOrDefault(e => e.Name.LocalName == "coordinates")?.Value
            ?? throw new InvalidDataException("KML测线缺少coordinates。");
        double Parse(string text) => double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
        var vertices = new List<Vertex>();
        foreach (string tuple in coordinates.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            string[] fields = tuple.Split(',');
            if (fields.Length is < 2 or > 3) throw new InvalidDataException("KML坐标列数无效。");
            double lon = Parse(fields[0]), lat = Parse(fields[1]);
            double? height = fields.Length == 3 && fields[2].Length > 0 ? Parse(fields[2]) : null;
            if (!double.IsFinite(lon) || !double.IsFinite(lat) || lon is < -180 or > 180 || lat is < -90 or > 90 ||
                height is { } h && !double.IsFinite(h)) throw new InvalidDataException("KML坐标或高度无效。");
            if (vertices.Count == 0 || Distance(vertices[^1].Lon, vertices[^1].Lat, lon, lat) > 1e-6)
                vertices.Add(new(lon, lat, height));
        }
        if (vertices.Count < 2) throw new InvalidDataException("KML测线至少需要两个不同位置。");
        return new() { Vertices = vertices.ToArray(), AltitudeMode = line.Elements().FirstOrDefault(e => e.Name.LocalName == "altitudeMode")?.Value ?? "clampToGround" };
    }

    public Hit? Match(double lon, double lat, double tolerance, ReportRouteGeometry? corridor = null)
    {
        Hit? best = null;
        bool ambiguous = false;
        double accumulated = 0;
        foreach (int i in _segments ??= Enumerable.Range(1, Vertices.Length - 1).ToArray())
        {
            var a = Vertices[i - 1]; var b = Vertices[i];
            if (corridor is not null && !corridor.OnAxis(a, b)) continue;
            var (bx, by) = XY(b.Lon, b.Lat, a.Lon, a.Lat);
            var (px, py) = XY(lon, lat, a.Lon, a.Lat);
            double squared = bx * bx + by * by;
            double fraction = (px * bx + py * by) / squared;
            double length = Distance(a.Lon, a.Lat, b.Lon, b.Lat);
            // Do not project beyond either endpoint or bridge separate geometries.
            if (fraction >= -1e-8 && fraction <= 1 + 1e-8)
            {
                fraction = Math.Clamp(fraction, 0, 1);
                double offset = Math.Sqrt(Math.Pow(px - fraction * bx, 2) + Math.Pow(py - fraction * by, 2));
                double? height = a.Height is { } ah && b.Height is { } bh ? ah + fraction * (bh - ah) : null;
                if (offset <= tolerance && (best is null || offset < best.Offset - 1e-4))
                { best = new(accumulated + fraction * length, offset, height); ambiguous = false; }
                else if (best is not null && Math.Abs(offset - best.Offset) <= 1e-4 &&
                         (height is null != best.Height is null || height is { } z && best.Height is { } h && Math.Abs(z - h) > .01)) ambiguous = true;
            }
            accumulated += length;
        }
        return ambiguous ? null : best;
    }

    private bool OnAxis(Vertex a, Vertex b)
    {
        var start = Vertices[0]; var end = Vertices[^1];
        var (dx, dy) = XY(end.Lon, end.Lat, start.Lon, start.Lat);
        double length = Math.Sqrt(dx * dx + dy * dy);
        if (length < .01) return false;
        var (ax, ay) = XY(a.Lon, a.Lat, start.Lon, start.Lat);
        var (bx, by) = XY(b.Lon, b.Lat, start.Lon, start.Lat);
        double crossA = Math.Abs(ax * dy - ay * dx) / length, crossB = Math.Abs(bx * dy - by * dx) / length;
        double alongA = (ax * dx + ay * dy) / length, alongB = (bx * dx + by * dy) / length;
        return crossA <= 1 && crossB <= 1 && Math.Max(alongA, alongB) >= 0 && Math.Min(alongA, alongB) <= length;
    }

    public static (double X, double Y) XY(double lon, double lat, double originLon, double originLat) =>
        ((lon - originLon) * Math.PI / 180 * 6371008.8 * Math.Cos(originLat * Math.PI / 180),
         (lat - originLat) * Math.PI / 180 * 6371008.8);

    public static double Distance(double lon1, double lat1, double lon2, double lat2)
    {
        double r = Math.PI / 180;
        double a = Math.Pow(Math.Sin((lat2 - lat1) * r / 2), 2) + Math.Cos(lat1 * r) * Math.Cos(lat2 * r) * Math.Pow(Math.Sin((lon2 - lon1) * r / 2), 2);
        return 6371008.8 * 2 * Math.Asin(Math.Sqrt(Math.Clamp(a, 0, 1)));
    }
}
