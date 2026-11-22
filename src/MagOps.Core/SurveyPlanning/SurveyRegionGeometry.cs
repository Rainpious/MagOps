namespace MagOps.Core.SurveyPlanning;

public sealed record SurveyRegionPolygon(
    IReadOnlyList<SurveyBoundaryPoint> OuterRing,
    IReadOnlyList<IReadOnlyList<SurveyBoundaryPoint>> Holes);

public sealed class SurveyRegionGeometry
{
    public IReadOnlyList<SurveyRegionPolygon> Polygons { get; }

    public SurveyRegionGeometry(IEnumerable<SurveyRegionPolygon> polygons)
    {
        ArgumentNullException.ThrowIfNull(polygons);
        Polygons = polygons.Select(NormalizePolygon).ToArray();
        if (Polygons.Count == 0)
            throw new InvalidDataException("Region must contain at least one Polygon.");
    }

    public static SurveyRegionGeometry FromSinglePolygon(
        IReadOnlyList<SurveyBoundaryPoint> boundary) =>
        new([new SurveyRegionPolygon(boundary, [])]);

    public bool IsSimpleSinglePolygon =>
        Polygons.Count == 1 && Polygons[0].Holes.Count == 0;

    public IReadOnlyList<SurveyBoundaryPoint> RequireSimpleSinglePolygon(
        string operation)
    {
        if (!IsSimpleSinglePolygon)
            throw new NotSupportedException(
                $"{operation} currently requires one Polygon without holes; " +
                "the Region was not silently flattened.");
        return Polygons[0].OuterRing;
    }

    public IEnumerable<IReadOnlyList<SurveyBoundaryPoint>> Rings()
    {
        foreach (SurveyRegionPolygon polygon in Polygons)
        {
            yield return polygon.OuterRing;
            foreach (IReadOnlyList<SurveyBoundaryPoint> hole in polygon.Holes)
                yield return hole;
        }
    }

    private static SurveyRegionPolygon NormalizePolygon(SurveyRegionPolygon polygon)
    {
        IReadOnlyList<SurveyBoundaryPoint> outer = NormalizeRing(
            polygon.OuterRing, "outer ring");
        IReadOnlyList<IReadOnlyList<SurveyBoundaryPoint>> holes =
            (polygon.Holes ?? [])
            .Select((hole, i) => (IReadOnlyList<SurveyBoundaryPoint>)NormalizeRing(
                hole, $"hole {i + 1}"))
            .ToArray();
        return new SurveyRegionPolygon(outer, holes);
    }

    private static List<SurveyBoundaryPoint> NormalizeRing(
        IReadOnlyList<SurveyBoundaryPoint> source,
        string label)
    {
        ArgumentNullException.ThrowIfNull(source);
        var result = new List<SurveyBoundaryPoint>();
        foreach (SurveyBoundaryPoint point in source)
        {
            if (!double.IsFinite(point.X) || !double.IsFinite(point.Y))
                throw new InvalidDataException($"Region {label} contains non-finite coordinates.");
            if (result.Count == 0 || Distance(result[^1], point) > 1e-9)
                result.Add(point);
        }
        if (result.Count > 1 && Distance(result[0], result[^1]) <= 1e-9)
            result.RemoveAt(result.Count - 1);
        if (result.Count < 3 || Math.Abs(SignedArea(result)) <= 1e-12)
            throw new InvalidDataException($"Region {label} must contain a non-zero-area ring.");
        return result;
    }

    private static double SignedArea(IReadOnlyList<SurveyBoundaryPoint> ring)
    {
        double twice = 0;
        for (int i = 0; i < ring.Count; i++)
        {
            SurveyBoundaryPoint a = ring[i];
            SurveyBoundaryPoint b = ring[(i + 1) % ring.Count];
            twice += a.X * b.Y - b.X * a.Y;
        }
        return twice / 2d;
    }

    private static double Distance(SurveyBoundaryPoint a, SurveyBoundaryPoint b)
    {
        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}

public static class SurveyRegionGeometryMath
{
    public static bool ContainsOrOnBoundary(
        SurveyRegionGeometry region,
        SurveyBoundaryPoint point,
        double tolerance = 1e-7, bool includeBoundary = true)
    {
        ArgumentNullException.ThrowIfNull(region);
        if (tolerance < 0 || !double.IsFinite(tolerance))
            throw new ArgumentOutOfRangeException(nameof(tolerance));

        foreach (SurveyRegionPolygon polygon in region.Polygons)
        {
            PointRingLocation outer = Locate(point, polygon.OuterRing, tolerance);
            if (outer == PointRingLocation.Outside)
                continue;
            if (outer == PointRingLocation.Boundary)
            {
                if (includeBoundary) return true;
                continue;
            }

            bool excluded = false;
            foreach (IReadOnlyList<SurveyBoundaryPoint> hole in polygon.Holes)
            {
                PointRingLocation inner = Locate(point, hole, tolerance);
                if (inner == PointRingLocation.Boundary)
                {
                    if (includeBoundary) return true;
                    excluded = true;
                    break;
                }
                if (inner == PointRingLocation.Inside)
                {
                    excluded = true;
                    break;
                }
            }
            if (!excluded)
                return true;
        }
        return false;
    }

    public static IReadOnlyList<double> SegmentBoundaryParameters(
        SurveyRegionGeometry region,
        SurveyBoundaryPoint start,
        SurveyBoundaryPoint end)
    {
        ArgumentNullException.ThrowIfNull(region);
        var values = new List<double> { 0d, 1d };
        foreach (IReadOnlyList<SurveyBoundaryPoint> ring in region.Rings())
        {
            for (int i = 0; i < ring.Count; i++)
                AddIntersectionParameters(start, end, ring[i],
                    ring[(i + 1) % ring.Count], values);
        }
        return values.Select(x => Math.Clamp(x, 0d, 1d))
            .OrderBy(x => x)
            .Aggregate(new List<double>(), (list, value) =>
            {
                if (list.Count == 0 || Math.Abs(value - list[^1]) > 1e-10)
                    list.Add(value);
                return list;
            });
    }

    private enum PointRingLocation { Outside, Inside, Boundary }

    private static PointRingLocation Locate(
        SurveyBoundaryPoint point,
        IReadOnlyList<SurveyBoundaryPoint> ring,
        double tolerance)
    {
        for (int i = 0; i < ring.Count; i++)
            if (DistancePointToSegment(point, ring[i], ring[(i + 1) % ring.Count]) <= tolerance)
                return PointRingLocation.Boundary;

        bool inside = false;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            SurveyBoundaryPoint a = ring[i];
            SurveyBoundaryPoint b = ring[j];
            if ((a.Y > point.Y) != (b.Y > point.Y))
            {
                double xAtY = (b.X - a.X) * (point.Y - a.Y) /
                              (b.Y - a.Y) + a.X;
                if (point.X < xAtY) inside = !inside;
            }
        }
        return inside ? PointRingLocation.Inside : PointRingLocation.Outside;
    }

    private static double DistancePointToSegment(
        SurveyBoundaryPoint p, SurveyBoundaryPoint a, SurveyBoundaryPoint b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double length2 = dx * dx + dy * dy;
        if (length2 <= 1e-24)
            return Math.Sqrt((p.X-a.X)*(p.X-a.X)+(p.Y-a.Y)*(p.Y-a.Y));
        double t = Math.Clamp(((p.X-a.X)*dx+(p.Y-a.Y)*dy)/length2, 0d, 1d);
        double x = a.X + dx*t, y = a.Y + dy*t;
        return Math.Sqrt((p.X-x)*(p.X-x)+(p.Y-y)*(p.Y-y));
    }

    private static void AddIntersectionParameters(
        SurveyBoundaryPoint p, SurveyBoundaryPoint p2,
        SurveyBoundaryPoint q, SurveyBoundaryPoint q2,
        ICollection<double> parameters)
    {
        double rx=p2.X-p.X, ry=p2.Y-p.Y, sx=q2.X-q.X, sy=q2.Y-q.Y;
        double rxs=Cross(rx,ry,sx,sy), qpx=q.X-p.X, qpy=q.Y-p.Y;
        if (Math.Abs(rxs) > 1e-12)
        {
            double t=Cross(qpx,qpy,sx,sy)/rxs;
            double u=Cross(qpx,qpy,rx,ry)/rxs;
            if (t>=-1e-10 && t<=1d+1e-10 && u>=-1e-10 && u<=1d+1e-10)
                parameters.Add(t);
            return;
        }
        if (Math.Abs(Cross(qpx,qpy,rx,ry)) > 1e-12)
            return;
        double rr=rx*rx+ry*ry;
        if (rr<=1e-24) return;
        double t0=((q.X-p.X)*rx+(q.Y-p.Y)*ry)/rr;
        double t1=((q2.X-p.X)*rx+(q2.Y-p.Y)*ry)/rr;
        if (t0>=-1e-10 && t0<=1d+1e-10) parameters.Add(t0);
        if (t1>=-1e-10 && t1<=1d+1e-10) parameters.Add(t1);
    }

    private static double Cross(double ax,double ay,double bx,double by)=>ax*by-ay*bx;
}
