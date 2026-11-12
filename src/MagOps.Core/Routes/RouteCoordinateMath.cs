using System.Globalization;
using System.Text.RegularExpressions;

namespace MagOps.Core.Routes;

public static class RouteCoordinateMath
{
    public const double EarthRadiusM = 6371008.8;

    private static readonly Regex CmRegex = new(
        @"(?i)(?:中央\s*(?:子午线|经线)|CM|TM)\s*[_=：:\-]?\s*(\d{2,3}(?:\.\d+)?)",
        RegexOptions.Compiled);

    public static bool IsGeographic(IEnumerable<RawRoutePoint> points) =>
        IsGeographicCore(points.Select(p => (p.X, p.Y)));

    public static bool IsGeographic(IEnumerable<RouteEndpoint> points) =>
        IsGeographicCore(points.Select(p => (p.X, p.Y)));

    public static double Haversine(double lon1, double lat1, double lon2, double lat2)
    {
        double p1 = DegreesToRadians(lat1);
        double p2 = DegreesToRadians(lat2);
        double dp = DegreesToRadians(lat2 - lat1);
        double dl = DegreesToRadians(lon2 - lon1);
        double a = Math.Sin(dp / 2) * Math.Sin(dp / 2)
                 + Math.Cos(p1) * Math.Cos(p2) * Math.Sin(dl / 2) * Math.Sin(dl / 2);
        return 2 * EarthRadiusM * Math.Asin(Math.Sqrt(a));
    }

    public static double RouteLength(
        IReadOnlyList<RouteEndpoint> points,
        string mode,
        double centralMeridian)
    {
        if (points.Count < 2) return 0.0;

        if (mode.Equals("geographic", StringComparison.OrdinalIgnoreCase))
        {
            double sumGeo = 0;
            for (int i = 1; i < points.Count; i++)
                sumGeo += Haversine(points[i - 1].X, points[i - 1].Y, points[i].X, points[i].Y);
            return sumGeo;
        }

        var lonLat = points.Select(p => ToLonLat(p, mode, centralMeridian)).ToArray();
        double sum = 0;
        for (int i = 1; i < lonLat.Length; i++)
        {
            var a = lonLat[i - 1];
            var b = lonLat[i];

            // v25 parity: same approximate distance used by the historical
            // KML/KMZ-visible route length and DJI metadata.
            double dx = (b.Lon - a.Lon) * 111320.0 * Math.Cos(DegreesToRadians(b.Lat));
            double dy = (b.Lat - a.Lat) * 111320.0;
            sum += Math.Sqrt(dx * dx + dy * dy);
        }
        return sum;
    }

    public static double Distance(double x1, double y1, double x2, double y2, string mode, double centralMeridian)
    {
        if (mode.Equals("geographic", StringComparison.OrdinalIgnoreCase))
            return Haversine(x1, y1, x2, y2);
        var a = ToLonLat(new RouteEndpoint { X = x1, Y = y1 }, mode, centralMeridian);
        var b = ToLonLat(new RouteEndpoint { X = x2, Y = y2 }, mode, centralMeridian);
        double dx = (b.Lon - a.Lon) * 111320.0 * Math.Cos(DegreesToRadians(b.Lat));
        double dy = (b.Lat - a.Lat) * 111320.0;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    public static double RoundTripDistance(
        double takeoffX, double takeoffY,
        IReadOnlyList<RouteSortie> sorties,
        string mode, double centralMeridian)
    {
        double total = 0;
        foreach (var sortie in sorties)
        {
            if (sortie.Points.Count == 0) continue;
            total += Distance(takeoffX, takeoffY, sortie.Points[0].X, sortie.Points[0].Y, mode, centralMeridian);
            total += RouteLength(sortie.Points, mode, centralMeridian);
            var last = sortie.Points[^1];
            total += Distance(last.X, last.Y, takeoffX, takeoffY, mode, centralMeridian);
        }
        return total;
    }

    public static (double E, double N, string Axis) ResolveProjectedEN(double x, double y)
    {
        bool xEast = IsGkEastingLike(x);
        bool yEast = IsGkEastingLike(y);

        if (yEast && !xEast)
            return (y, x, "X=N / Y=E");
        if (xEast && !yEast)
            return (x, y, "X=E / Y=N");

        if (Math.Abs(x) > 1_000_000.0 && Math.Abs(y) < 1_000_000.0)
            return (y, x, "X=N / Y=E");
        if (Math.Abs(y) > 1_000_000.0 && Math.Abs(x) < 1_000_000.0)
            return (x, y, "X=E / Y=N");

        return (x, y, "X=E / Y=N (ambiguous)");
    }

    public static bool IsGkEastingLike(double value)
    {
        double v = Math.Abs(value);
        if (v >= 100_000.0 && v <= 900_000.0) return true;

        if (v >= 10_000_000.0 && v < 61_000_000.0)
        {
            int zone = (int)Math.Floor(v / 1_000_000.0);
            double local = v - zone * 1_000_000.0;
            return zone is >= 10 and <= 60 && local is >= 100_000.0 and <= 900_000.0;
        }
        return false;
    }

    public static (double Lon, double Lat) GkToWgs84(double easting, double northing, double cm)
    {
        double absE = Math.Abs(easting);
        int zone = (int)Math.Floor(absE / 1_000_000.0);
        double local = absE - zone * 1_000_000.0;
        if (zone is >= 1 and <= 60 && local is >= 100_000 and <= 900_000)
            easting = easting < 0 ? -local : local;

        const double a = 6378137.0;
        double f = 1.0 / 298.257223563;
        double e2 = f * (2 - f);
        double ep2 = e2 / (1 - e2);
        double x = easting - 500000.0;
        double mu = northing / (a * (1 - e2 / 4 - 3 * e2 * e2 / 64 - 5 * e2 * e2 * e2 / 256));
        double e1 = (1 - Math.Sqrt(1 - e2)) / (1 + Math.Sqrt(1 - e2));

        double phi1 = mu
            + (3 * e1 / 2 - 27 * Math.Pow(e1, 3) / 32) * Math.Sin(2 * mu)
            + (21 * e1 * e1 / 16 - 55 * Math.Pow(e1, 4) / 32) * Math.Sin(4 * mu)
            + (151 * Math.Pow(e1, 3) / 96) * Math.Sin(6 * mu)
            + (1097 * Math.Pow(e1, 4) / 512) * Math.Sin(8 * mu);

        double sin1 = Math.Sin(phi1);
        double cos1 = Math.Cos(phi1);
        double tan1 = Math.Tan(phi1);
        double n1 = a / Math.Sqrt(1 - e2 * sin1 * sin1);
        double r1 = a * (1 - e2) / Math.Pow(1 - e2 * sin1 * sin1, 1.5);
        double t1 = tan1 * tan1;
        double c1 = ep2 * cos1 * cos1;
        double d = x / n1;

        double lat = phi1 - (n1 * tan1 / r1) * (
            d * d / 2
            - (5 + 3 * t1 + 10 * c1 - 4 * c1 * c1 - 9 * ep2) * Math.Pow(d, 4) / 24
            + (61 + 90 * t1 + 298 * c1 + 45 * t1 * t1 - 252 * ep2 - 3 * c1 * c1) * Math.Pow(d, 6) / 720);

        double lon = DegreesToRadians(cm) + (
            d
            - (1 + 2 * t1 + c1) * Math.Pow(d, 3) / 6
            + (5 - 2 * c1 + 28 * t1 - 3 * c1 * c1 + 8 * ep2 + 24 * t1 * t1) * Math.Pow(d, 5) / 120) / cos1;

        return (RadiansToDegrees(lon), RadiansToDegrees(lat));
    }

    public static (double Lon, double Lat) ToLonLat(
        RouteEndpoint point,
        string mode,
        double centralMeridian)
    {
        if (mode.Equals("geographic", StringComparison.OrdinalIgnoreCase))
            return (point.X, point.Y);

        var en = ResolveProjectedEN(point.X, point.Y);
        return GkToWgs84(en.E, en.N, centralMeridian);
    }

    public static double? InferCentralMeridian(
        IReadOnlyList<RawRoutePoint> points,
        string sourcePath)
    {
        Match m = CmRegex.Match(sourcePath ?? "");
        if (m.Success &&
            double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double explicitCm) &&
            explicitCm is >= -180 and <= 180)
            return explicitCm;

        string fold = (sourcePath ?? "").ToLowerInvariant();
        // A project/folder identifier is not a CRS parameter. 020537 used to
        // force 99E here, which could silently override the actual project CM.
        foreach (string token in new[] { "22603", "22610", "22624" })
            if (fold.Contains(token))
                return 114.0;

        if (IsGeographic(points))
        {
            double med = Median(points.Select(p => p.X));
            return Math.Round(med / 3.0) * 3.0;
        }

        var eastings = points
            .Select(p => Math.Abs(ResolveProjectedEN(p.X, p.Y).E))
            .OrderBy(x => x)
            .ToArray();
        double projectedMed = Median(eastings);
        int zone = (int)Math.Floor(projectedMed / 1_000_000.0);
        double local = projectedMed - zone * 1_000_000.0;
        if (zone is >= 1 and <= 60 && local is >= 100_000 and <= 900_000)
        {
            double cm = zone * 3.0;
            if (cm <= 180) return cm;
        }

        return null;
    }

    public static double Median(IEnumerable<double> values)
    {
        double[] sorted = values.OrderBy(x => x).ToArray();
        if (sorted.Length == 0) return 0;
        int mid = sorted.Length / 2;
        return sorted.Length % 2 == 1
            ? sorted[mid]
            : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    private static bool IsGeographicCore(IEnumerable<(double X, double Y)> points)
    {
        var materialized = points.ToArray();
        if (materialized.Length == 0) return false;
        return materialized.All(p => p.X is >= -180 and <= 180 && p.Y is >= -90 and <= 90);
    }

    private static double DegreesToRadians(double value) => value * Math.PI / 180.0;
    private static double RadiansToDegrees(double value) => value * 180.0 / Math.PI;
}
