using MagOps.Core.SurveyPlanning;

namespace MagOps.Core.Routes;

public sealed record RouteBoundaryViolation(
    string SortieName,
    int SegmentNumber,
    string FromLine,
    string ToLine,
    double LengthM,
    SurveyBoundaryPoint From,
    SurveyBoundaryPoint To);

public sealed class RouteBoundarySafetyResult
{
    public List<RouteBoundaryViolation> Violations { get; init; } = [];
    public bool IsSafe => Violations.Count == 0;
}

/// <summary>
/// Hard geometry gate for route segments. A segment is accepted only when the
/// complete segment lies inside or on the active workarea/partition polygon.
/// Endpoints alone are not sufficient for concave polygons: the implementation
/// splits the segment at every polygon-edge intersection and tests every open
/// interval, so a straight connector that exits through a concave notch is
/// rejected even when both endpoints are valid boundary points.
/// </summary>
public static class RouteBoundarySafetyService
{
    public const double DefaultProjectedTolerance = 0.25d;

    public static RouteBoundarySafetyResult ValidatePlan(
        RoutePlanResult plan,
        IReadOnlyList<SurveyBoundaryPoint> boundary,
        double tolerance = DefaultProjectedTolerance) =>
        ValidatePlan(plan, SurveyRegionGeometry.FromSinglePolygon(boundary), tolerance);

    public static RouteBoundarySafetyResult ValidatePlan(
        RoutePlanResult plan,
        SurveyRegionGeometry region,
        double tolerance = DefaultProjectedTolerance)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(region);

        double effectiveTolerance = plan.Mode.Equals(
            "geographic",
            StringComparison.OrdinalIgnoreCase) &&
            Math.Abs(tolerance - DefaultProjectedTolerance) <= 1e-12
                ? 1e-8
                : tolerance;

        var approvals = ExplicitRouteConnectionStore.Load(plan.SourcePath);
        var violations = new List<RouteBoundaryViolation>();
        foreach (RouteSortie sortie in plan.Sorties)
        {
            for (int i = 1; i < sortie.Points.Count; i++)
            {
                RouteEndpoint fromEndpoint = sortie.Points[i - 1];
                RouteEndpoint toEndpoint = sortie.Points[i];
                // A transition between two different survey lines is a sortie
                // transfer leg, not a measured line segment. It may leave the
                // workarea polygon while the aircraft moves to the next line.
                // Same-line segments remain subject to the hard polygon gate;
                // this also preserves the safety check for an explicit
                // concave-line CONNECT decision.
                if (!string.Equals(
                        fromEndpoint.Line,
                        toEndpoint.Line,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (approvals.Any(a => a.Matches(fromEndpoint, toEndpoint))) continue;
                SurveyBoundaryPoint from = PhysicalPoint(fromEndpoint, plan.Mode);
                SurveyBoundaryPoint to = PhysicalPoint(toEndpoint, plan.Mode);
                if (IsSegmentInsideOrOnBoundary(
                        from,
                        to,
                        region,
                        effectiveTolerance))
                    continue;

                double lengthM = plan.Mode.Equals(
                    "geographic",
                    StringComparison.OrdinalIgnoreCase)
                    ? RouteCoordinateMath.Haversine(
                        fromEndpoint.X,
                        fromEndpoint.Y,
                        toEndpoint.X,
                        toEndpoint.Y)
                    : Distance(from, to);

                violations.Add(new RouteBoundaryViolation(
                    string.IsNullOrWhiteSpace(sortie.Name) ? "(未命名架次)" : sortie.Name,
                    i,
                    fromEndpoint.Line ?? "",
                    toEndpoint.Line ?? "",
                    lengthM,
                    from,
                    to));
            }
        }

        return new RouteBoundarySafetyResult { Violations = violations };
    }

    public static void EnsurePlanInsideBoundary(
        RoutePlanResult plan,
        IReadOnlyList<SurveyBoundaryPoint> boundary,
        string boundaryLabel,
        double tolerance = DefaultProjectedTolerance) =>
        EnsurePlanInsideBoundary(
            plan, SurveyRegionGeometry.FromSinglePolygon(boundary), boundaryLabel, tolerance);

    public static void EnsurePlanInsideBoundary(
        RoutePlanResult plan,
        SurveyRegionGeometry region,
        string boundaryLabel,
        double tolerance = DefaultProjectedTolerance)
    {
        RouteBoundarySafetyResult result = ValidatePlan(
            plan,
            region,
            tolerance);
        if (result.IsSafe)
            return;

        RouteBoundaryViolation first = result.Violations[0];
        string lineText = string.Equals(
                first.FromLine,
                first.ToLine,
                StringComparison.OrdinalIgnoreCase)
            ? first.FromLine
            : $"{first.FromLine} -> {first.ToLine}";
        string more = result.Violations.Count > 1
            ? $"；另有 {result.Violations.Count - 1} 段违规"
            : "";
        throw new InvalidOperationException(
            $"航线几何安全校验失败：{first.SortieName} 第 {first.SegmentNumber} 段" +
            $"（{lineText}，约 {first.LengthM:0.###} m）穿出{boundaryLabel}{more}。" +
            " 已拒绝该 RoutePlan，未写入正式成果；请返回凹形断线处理，将该连接改为 SPLIT 后重新生成，" +
            "或重新检查人工航点/分区边界。");
    }

    public static bool IsSegmentInsideOrOnBoundary(
        SurveyBoundaryPoint start,
        SurveyBoundaryPoint end,
        IReadOnlyList<SurveyBoundaryPoint> boundary,
        double tolerance = DefaultProjectedTolerance) =>
        IsSegmentInsideOrOnBoundary(
            start, end, SurveyRegionGeometry.FromSinglePolygon(boundary), tolerance);

    public static bool IsSegmentInsideOrOnBoundary(
        SurveyBoundaryPoint start,
        SurveyBoundaryPoint end,
        SurveyRegionGeometry region,
        double tolerance = DefaultProjectedTolerance)
    {
        ArgumentNullException.ThrowIfNull(region);
        if (tolerance < 0 || !double.IsFinite(tolerance))
            throw new ArgumentOutOfRangeException(nameof(tolerance));
        if (!SurveyRegionGeometryMath.ContainsOrOnBoundary(region, start, tolerance) ||
            !SurveyRegionGeometryMath.ContainsOrOnBoundary(region, end, tolerance))
            return false;

        double dx=end.X-start.X, dy=end.Y-start.Y;
        if (dx*dx+dy*dy <= tolerance*tolerance)
            return true;

        IReadOnlyList<double> cuts =
            SurveyRegionGeometryMath.SegmentBoundaryParameters(region, start, end);
        for (int i=1;i<cuts.Count;i++)
        {
            if (cuts[i]-cuts[i-1] <= 1e-10) continue;
            double t=(cuts[i]+cuts[i-1])/2d;
            var mid=new SurveyBoundaryPoint(start.X+dx*t,start.Y+dy*t);
            if (!SurveyRegionGeometryMath.ContainsOrOnBoundary(region, mid, tolerance))
                return false;
        }
        return true;
    }

    private static SurveyBoundaryPoint PhysicalPoint(
        RouteEndpoint point,
        string mode)
    {
        if (mode.Equals("projected", StringComparison.OrdinalIgnoreCase))
        {
            var en = RouteCoordinateMath.ResolveProjectedEN(point.X, point.Y);
            return new SurveyBoundaryPoint(en.E, en.N);
        }
        return new SurveyBoundaryPoint(point.X, point.Y);
    }

    private static bool IsInsideOrOnBoundary(
        SurveyBoundaryPoint point,
        IReadOnlyList<SurveyBoundaryPoint> polygon,
        double tolerance)
    {
        for (int i = 0; i < polygon.Count; i++)
        {
            if (DistancePointToSegment(
                    point,
                    polygon[i],
                    polygon[(i + 1) % polygon.Count]) <= tolerance)
            {
                return true;
            }
        }

        bool inside = false;
        int j = polygon.Count - 1;
        for (int i = 0; i < polygon.Count; i++)
        {
            SurveyBoundaryPoint pi = polygon[i];
            SurveyBoundaryPoint pj = polygon[j];
            bool crosses = (pi.Y > point.Y) != (pj.Y > point.Y);
            if (crosses)
            {
                double xAtY =
                    (pj.X - pi.X) * (point.Y - pi.Y) /
                    (pj.Y - pi.Y) + pi.X;
                if (point.X < xAtY)
                    inside = !inside;
            }
            j = i;
        }
        return inside;
    }

    private static void AddIntersectionParameters(
        SurveyBoundaryPoint p,
        SurveyBoundaryPoint p2,
        SurveyBoundaryPoint q,
        SurveyBoundaryPoint q2,
        ICollection<double> parameters)
    {
        double rx = p2.X - p.X;
        double ry = p2.Y - p.Y;
        double sx = q2.X - q.X;
        double sy = q2.Y - q.Y;
        double rxs = Cross(rx, ry, sx, sy);
        double qpx = q.X - p.X;
        double qpy = q.Y - p.Y;
        double qpxr = Cross(qpx, qpy, rx, ry);
        double scale = Math.Max(
            1d,
            Math.Sqrt((rx * rx + ry * ry) * (sx * sx + sy * sy)));
        double crossTolerance = Math.Max(1e-12, 1e-12 * scale);

        if (Math.Abs(rxs) > crossTolerance)
        {
            double t = Cross(qpx, qpy, sx, sy) / rxs;
            double u = Cross(qpx, qpy, rx, ry) / rxs;
            if (t >= -1e-10 && t <= 1d + 1e-10 &&
                u >= -1e-10 && u <= 1d + 1e-10)
            {
                parameters.Add(t);
            }
            return;
        }

        if (Math.Abs(qpxr) > crossTolerance)
            return;

        double rr = rx * rx + ry * ry;
        if (rr <= 1e-24)
            return;
        double t0 = (qpx * rx + qpy * ry) / rr;
        double t1 = t0 + (sx * rx + sy * ry) / rr;
        double lo = Math.Max(0d, Math.Min(t0, t1));
        double hi = Math.Min(1d, Math.Max(t0, t1));
        if (hi + 1e-10 < lo)
            return;
        parameters.Add(lo);
        parameters.Add(hi);
    }

    private static double DistancePointToSegment(
        SurveyBoundaryPoint p,
        SurveyBoundaryPoint a,
        SurveyBoundaryPoint b)
    {
        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        double lengthSquared = dx * dx + dy * dy;
        if (lengthSquared <= 1e-24)
            return Distance(p, a);
        double t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) /
                   lengthSquared;
        t = Math.Clamp(t, 0d, 1d);
        var closest = new SurveyBoundaryPoint(
            a.X + t * dx,
            a.Y + t * dy);
        return Distance(p, closest);
    }

    private static double Cross(
        double ax,
        double ay,
        double bx,
        double by) => ax * by - ay * bx;

    private static double Distance(
        SurveyBoundaryPoint a,
        SurveyBoundaryPoint b)
    {
        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static void ValidateBoundary(
        IReadOnlyList<SurveyBoundaryPoint> boundary)
    {
        ArgumentNullException.ThrowIfNull(boundary);
        if (boundary.Count < 3)
            throw new ArgumentException("有效工区边界至少需要 3 个点。", nameof(boundary));
    }
}
