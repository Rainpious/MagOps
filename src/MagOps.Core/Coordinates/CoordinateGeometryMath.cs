namespace MagOps.Core.Coordinates;

public static class CoordinateGeometryMath
{
    /// <summary>
    /// Calculates the planar polygon area from a converted coordinate batch.
    /// Easting/Northing are already expressed in metres in one confirmed
    /// projected CRS, so the shoelace result is square metres.
    /// The first point does not need to be repeated at the end.
    /// </summary>
    public static double PolygonAreaSquareMeters(
        IReadOnlyList<CoordinateConversionResult> points)
    {
        if (points.Count < 3)
            return 0d;

        double originE = points[0].Easting, originN = points[0].Northing;
        double twiceArea = 0d;
        for (int i = 0; i < points.Count; i++)
        {
            CoordinateConversionResult a = points[i];
            CoordinateConversionResult b = points[(i + 1) % points.Count];
            twiceArea += (a.Easting - originE) * (b.Northing - originN) - (b.Easting - originE) * (a.Northing - originN);
        }

        return Math.Abs(twiceArea) / 2d;
    }

    public static (double Easting, double Northing, bool IsAreaCentroid) Centroid(
        IReadOnlyList<CoordinateConversionResult> points)
    {
        if (points.Count == 0 || points.Any(p => !double.IsFinite(p.Easting) || !double.IsFinite(p.Northing)))
            throw new ArgumentException("重心计算需要有效投影坐标。");
        // An explicit closing duplicate must not bias the degenerate vertex-mean fallback.
        int count = points.Count;
        if (count > 1 && points[0].Easting == points[^1].Easting && points[0].Northing == points[^1].Northing) count--;
        double ox = points.Take(count).Average(p => p.Easting), oy = points.Take(count).Average(p => p.Northing);
        double sum = 0, sx = 0, sy = 0;
        for (int i = 0; i < count; i++)
        {
            var a = points[i]; var b = points[(i + 1) % count];
            double ax = a.Easting - ox, ay = a.Northing - oy, bx = b.Easting - ox, by = b.Northing - oy;
            double cross = ax * by - bx * ay;
            sum += cross; sx += (ax + bx) * cross; sy += (ay + by) * cross;
        }
        return count >= 3 && Math.Abs(sum) > 1e-6
            ? (ox + sx / (3 * sum), oy + sy / (3 * sum), true) : (ox, oy, false);
    }
}
