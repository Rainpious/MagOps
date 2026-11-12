namespace MagOps.Core.SurveyPlanning;

/// <summary>
/// Creates regular survey lines from a projected XY polygon. Line identity is
/// spatial, not file-order based: the first positive-length line on the
/// south/west side receives InitialLineNumber and numbers increase toward
/// north/east. Reversing or rotating the PLY/KML vertex list therefore does not
/// renumber the survey grid.
///
/// The former odd-grid outward extension implementation is retained behind the
/// explicit ExtendForEvenSnakeCompletion flag for future redesign only. The
/// flag defaults to false and the Desktop production workflow never enables it
/// until passable-road / field-access calculations are added.
/// </summary>
public sealed class SurveyLinePlanner
{
    private const double GeometryTolerance = 1e-7;
    private const double MergeTolerance = 1e-5;
    private const int MaxCandidateLines = 200_000;

    public SurveyLinePlanResult Plan(
        IReadOnlyList<SurveyBoundaryPoint> boundary,
        SurveyLinePlanOptions options) =>
        Plan(SurveyRegionGeometry.FromSinglePolygon(boundary), options);

    public SurveyLinePlanResult Plan(
        SurveyRegionGeometry region,
        SurveyLinePlanOptions options)
    {
        ArgumentNullException.ThrowIfNull(region);
        ArgumentNullException.ThrowIfNull(options);

        ValidateOptions(options);
        List<List<SurveyBoundaryPoint>> rings = region.Rings()
            .Select(ring => NormalizeBoundary(ring)).ToList();
        foreach (List<SurveyBoundaryPoint> ring in rings)
            ValidateBoundary(ring);
        List<SurveyBoundaryPoint> polygon = region.Polygons[0].OuterRing.ToList();
        List<SurveyBoundaryPoint> allOuter = region.Polygons
            .SelectMany(p => p.OuterRing).ToList();

        // The centroid is only a numeric reference for the local frame. Grid
        // phase is anchored to the minimum south/west normal coordinate below,
        // so polygon vertex order cannot affect line placement or numbering.
        SurveyBoundaryPoint origin = new(
            allOuter.Average(point => point.X),
            allOuter.Average(point => point.Y));
        double azimuth = NormalizeAzimuth(options.AzimuthDegrees);
        double radians = azimuth * Math.PI / 180.0;

        // Azimuth is clockwise from north.
        double directionX = Math.Sin(radians);
        double directionY = Math.Cos(radians);

        // Start from a perpendicular and orient its positive side according to
        // the agreed field convention: dominant north/south progression grows
        // south -> north; otherwise progression grows west -> east.
        double normalX = Math.Cos(radians);
        double normalY = -Math.Sin(radians);
        if (Math.Abs(normalY) >= Math.Abs(normalX))
        {
            if (normalY < 0d)
            {
                normalX = -normalX;
                normalY = -normalY;
            }
        }
        else if (normalX < 0d)
        {
            normalX = -normalX;
            normalY = -normalY;
        }

        LocalPoint[] transformed = allOuter
            .Select(point => ToLocal(
                point,
                origin,
                directionX,
                directionY,
                normalX,
                normalY))
            .ToArray();

        double minV = transformed.Min(x => x.V);
        double maxV = transformed.Max(x => x.V);
        double spanV = Math.Max(0d, maxV - minV);
        long candidateCount =
            (long)Math.Floor((spanV + GeometryTolerance) / options.SpacingM) + 1L;

        if (candidateCount <= 0)
        {
            return new SurveyLinePlanResult
            {
                Boundary = polygon,
                Origin = origin,
                Options = CloneOptions(options, azimuth)
            };
        }

        if (candidateCount > MaxCandidateLines)
        {
            throw new InvalidOperationException(
                $"候选航线数量 {candidateCount} 过大；请检查线间距和 PLY 坐标单位。" +
                "PLY 自动划线要求平面米制坐标。\n");
        }

        var lines = new List<SurveyPlannedLine>();
        int tangentOnly = 0;

        for (int ordinal = 0; ordinal < candidateCount; ordinal++)
        {
            double offset = minV + ordinal * options.SpacingM;
            List<SurveyLineSegment> segments = ClipInfiniteLine(
                region,
                rings,
                origin,
                directionX,
                directionY,
                normalX,
                normalY,
                offset);

            if (segments.Count == 0)
            {
                tangentOnly++;
                continue;
            }

            int lineNumber = NextLineNumber(
                options.InitialLineNumber,
                options.LineNumberIncrement,
                lines.Count);
            lines.Add(new SurveyPlannedLine
            {
                LineNumber = lineNumber,
                Segments = segments
            });
        }

        // FROZEN / PENDING MODIFICATION:
        // Do not enable this in production merely to obtain an even snake.
        // The additional outside line changes field access and launch/return
        // requirements. Re-enable only after a passable-road / field-access
        // calculation method is implemented and accepted.
        int extensionCount = 0;
        if (options.ExtendForEvenSnakeCompletion &&
            lines.Count > 0 &&
            lines.Count % 2 == 1)
        {
            SurveyPlannedLine source = lines[^1];
            var shifted = source.Segments
                .Select(segment => new SurveyLineSegment(
                    Shift(segment.Start, normalX, normalY, options.SpacingM),
                    Shift(segment.End, normalX, normalY, options.SpacingM)))
                .ToList();

            if (shifted.Count > 0)
            {
                lines.Add(new SurveyPlannedLine
                {
                    LineNumber = NextLineNumber(
                        options.InitialLineNumber,
                        options.LineNumberIncrement,
                        lines.Count),
                    Segments = shifted,
                    IsCoverageExtension = true
                });
                extensionCount = 1;
            }
        }

        return new SurveyLinePlanResult
        {
            Boundary = polygon,
            Origin = origin,
            Options = CloneOptions(options, azimuth),
            Lines = lines,
            CandidateLineCount = checked((int)candidateCount),
            TangentOnlyLineCount = tangentOnly,
            CoverageExtensionLineCount = extensionCount
        };
    }

    private static int NextLineNumber(
        int initial,
        int increment,
        int zeroBasedIndex)
    {
        try
        {
            return checked(initial + zeroBasedIndex * increment);
        }
        catch (OverflowException)
        {
            throw new InvalidOperationException(
                "航线编号超出 Int32 范围；请调整初始线号或线号增量。");
        }
    }

    private static SurveyBoundaryPoint Shift(
        SurveyBoundaryPoint point,
        double normalX,
        double normalY,
        double distance) =>
        new(
            point.X + normalX * distance,
            point.Y + normalY * distance);

    private static SurveyLinePlanOptions CloneOptions(
        SurveyLinePlanOptions options,
        double azimuth) =>
        new()
        {
            SpacingM = options.SpacingM,
            AzimuthDegrees = azimuth,
            InitialLineNumber = options.InitialLineNumber,
            LineNumberIncrement = options.LineNumberIncrement,
            ExtendForEvenSnakeCompletion = options.ExtendForEvenSnakeCompletion
        };

    private static void ValidateOptions(SurveyLinePlanOptions options)
    {
        if (!double.IsFinite(options.SpacingM) || options.SpacingM <= 0)
            throw new InvalidOperationException("飞行线间距必须为大于 0 的有限数值（米）。");

        if (!double.IsFinite(options.AzimuthDegrees))
            throw new InvalidOperationException("飞行线方位角必须为有限数值。");

        if (options.LineNumberIncrement <= 0)
            throw new InvalidOperationException("飞行线序号增量必须大于 0，线号按南/西向北/东递增。");
    }

    private static double NormalizeAzimuth(double value)
    {
        double normalized = value % 360.0;
        if (normalized < 0)
            normalized += 360.0;
        if (Math.Abs(normalized - 360.0) < 1e-12)
            normalized = 0;
        return normalized;
    }

    private static List<SurveyBoundaryPoint> NormalizeBoundary(
        IReadOnlyList<SurveyBoundaryPoint> source)
    {
        var result = new List<SurveyBoundaryPoint>();
        foreach (SurveyBoundaryPoint point in source)
        {
            if (!double.IsFinite(point.X) || !double.IsFinite(point.Y))
                throw new InvalidDataException("PLY 边界包含非有限 X/Y 坐标。");

            if (result.Count == 0 || Distance(result[^1], point) > GeometryTolerance)
                result.Add(point);
        }

        if (result.Count > 1 && Distance(result[0], result[^1]) <= GeometryTolerance)
            result.RemoveAt(result.Count - 1);

        return result;
    }

    private static void ValidateBoundary(IReadOnlyList<SurveyBoundaryPoint> polygon)
    {
        if (polygon.Count < 3)
            throw new InvalidDataException("PLY 边界至少需要 3 个不同的 XY 点。");

        double area2 = 0;
        for (int i = 0; i < polygon.Count; i++)
        {
            SurveyBoundaryPoint a = polygon[i];
            SurveyBoundaryPoint b = polygon[(i + 1) % polygon.Count];
            area2 += a.X * b.Y - b.X * a.Y;
        }

        if (Math.Abs(area2) <= GeometryTolerance)
            throw new InvalidDataException("PLY 边界面积为 0 或点序无效，无法自动划线。");
    }

    private static List<SurveyLineSegment> ClipInfiniteLine(
        SurveyRegionGeometry region,
        IReadOnlyList<List<SurveyBoundaryPoint>> rings,
        SurveyBoundaryPoint origin,
        double directionX,
        double directionY,
        double normalX,
        double normalY,
        double offset)
    {
        var intersections = new List<double>();
        foreach (List<SurveyBoundaryPoint> ring in rings)
        {
            LocalPoint[] transformed = ring.Select(point => ToLocal(
                point, origin, directionX, directionY, normalX, normalY)).ToArray();
            for (int i=0;i<transformed.Length;i++)
            {
                LocalPoint a=transformed[i], b=transformed[(i+1)%transformed.Length];
                double da=a.V-offset, db=b.V-offset;
                if (Math.Abs(da)<=GeometryTolerance && Math.Abs(db)<=GeometryTolerance)
                {
                    intersections.Add(a.U); intersections.Add(b.U); continue;
                }
                if ((da < -GeometryTolerance && db < -GeometryTolerance) ||
                    (da > GeometryTolerance && db > GeometryTolerance)) continue;
                double dv=b.V-a.V;
                if (Math.Abs(dv)<=GeometryTolerance) continue;
                double t=(offset-a.V)/dv;
                if (t < -GeometryTolerance || t > 1d+GeometryTolerance) continue;
                t=Math.Clamp(t,0d,1d);
                intersections.Add(a.U+(b.U-a.U)*t);
            }
        }
        if (intersections.Count<2) return [];
        intersections.Sort();
        List<double> unique=Deduplicate(intersections);
        var segments=new List<SurveyLineSegment>();
        for (int i=0;i<unique.Count-1;i++)
        {
            double u0=unique[i],u1=unique[i+1];
            if (u1-u0<=GeometryTolerance) continue;
            SurveyBoundaryPoint mid=FromLocal((u0+u1)/2d,offset,origin,
                directionX,directionY,normalX,normalY);
            if (!SurveyRegionGeometryMath.ContainsOrOnBoundary(region,mid,GeometryTolerance))
                continue;
            SurveyBoundaryPoint start=FromLocal(u0,offset,origin,
                directionX,directionY,normalX,normalY);
            SurveyBoundaryPoint end=FromLocal(u1,offset,origin,
                directionX,directionY,normalX,normalY);
            if (Distance(start,end)>GeometryTolerance)
                segments.Add(new SurveyLineSegment(start,end));
        }
        return segments;
    }

    private static List<SurveyLineSegment> ClipInfiniteLine(
        IReadOnlyList<SurveyBoundaryPoint> polygon,
        IReadOnlyList<LocalPoint> transformed,
        SurveyBoundaryPoint origin,
        double directionX,
        double directionY,
        double normalX,
        double normalY,
        double offset)
    {
        var intersections = new List<double>();

        for (int i = 0; i < transformed.Count; i++)
        {
            LocalPoint a = transformed[i];
            LocalPoint b = transformed[(i + 1) % transformed.Count];
            double da = a.V - offset;
            double db = b.V - offset;

            if (Math.Abs(da) <= GeometryTolerance &&
                Math.Abs(db) <= GeometryTolerance)
            {
                // Boundary edge lies on the candidate line. Include both ends;
                // midpoint-in-polygon below decides which intervals belong to
                // the polygon and avoids manufacturing geometry outside it.
                intersections.Add(a.U);
                intersections.Add(b.U);
                continue;
            }

            if ((da < -GeometryTolerance && db < -GeometryTolerance) ||
                (da > GeometryTolerance && db > GeometryTolerance))
            {
                continue;
            }

            double deltaV = b.V - a.V;
            if (Math.Abs(deltaV) <= GeometryTolerance)
                continue;

            double t = (offset - a.V) / deltaV;
            if (t < -GeometryTolerance || t > 1.0 + GeometryTolerance)
                continue;

            t = Math.Clamp(t, 0.0, 1.0);
            intersections.Add(a.U + (b.U - a.U) * t);
        }

        if (intersections.Count < 2)
            return [];

        intersections.Sort();
        List<double> unique = Deduplicate(intersections);
        if (unique.Count < 2)
            return [];

        var segments = new List<SurveyLineSegment>();
        for (int i = 0; i < unique.Count - 1; i++)
        {
            double startU = unique[i];
            double endU = unique[i + 1];
            if (endU - startU <= GeometryTolerance)
                continue;

            double middleU = (startU + endU) / 2.0;
            SurveyBoundaryPoint middle = FromLocal(
                middleU,
                offset,
                origin,
                directionX,
                directionY,
                normalX,
                normalY);

            if (!IsInsideOrOnBoundary(middle, polygon))
                continue;

            SurveyBoundaryPoint start = FromLocal(
                startU,
                offset,
                origin,
                directionX,
                directionY,
                normalX,
                normalY);
            SurveyBoundaryPoint end = FromLocal(
                endU,
                offset,
                origin,
                directionX,
                directionY,
                normalX,
                normalY);

            if (Distance(start, end) > GeometryTolerance)
                segments.Add(new SurveyLineSegment(start, end));
        }

        return MergeTouchingSegments(segments);
    }

    private static List<double> Deduplicate(IReadOnlyList<double> sorted)
    {
        var result = new List<double>();
        foreach (double value in sorted)
        {
            if (result.Count == 0 ||
                Math.Abs(value - result[^1]) > MergeTolerance)
            {
                result.Add(value);
            }
            else
            {
                result[^1] = (result[^1] + value) / 2.0;
            }
        }
        return result;
    }

    private static List<SurveyLineSegment> MergeTouchingSegments(
        IReadOnlyList<SurveyLineSegment> source)
    {
        if (source.Count <= 1)
            return source.ToList();

        var result = new List<SurveyLineSegment>();
        foreach (SurveyLineSegment segment in source)
        {
            if (result.Count > 0 &&
                Distance(result[^1].End, segment.Start) <= MergeTolerance)
            {
                SurveyLineSegment previous = result[^1];
                result[^1] = new SurveyLineSegment(previous.Start, segment.End);
            }
            else
            {
                result.Add(segment);
            }
        }
        return result;
    }

    private static bool IsInsideOrOnBoundary(
        SurveyBoundaryPoint point,
        IReadOnlyList<SurveyBoundaryPoint> polygon)
    {
        for (int i = 0; i < polygon.Count; i++)
        {
            if (IsPointOnSegment(
                    point,
                    polygon[i],
                    polygon[(i + 1) % polygon.Count]))
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

    private static bool IsPointOnSegment(
        SurveyBoundaryPoint p,
        SurveyBoundaryPoint a,
        SurveyBoundaryPoint b)
    {
        double vx = b.X - a.X;
        double vy = b.Y - a.Y;
        double wx = p.X - a.X;
        double wy = p.Y - a.Y;
        double length = Math.Sqrt(vx * vx + vy * vy);
        double cross = vx * wy - vy * wx;
        if (Math.Abs(cross) > GeometryTolerance * Math.Max(1.0, length))
            return false;

        double dot = wx * vx + wy * vy;
        if (dot < -GeometryTolerance)
            return false;
        double lengthSquared = vx * vx + vy * vy;
        return dot <= lengthSquared + GeometryTolerance;
    }

    private static LocalPoint ToLocal(
        SurveyBoundaryPoint point,
        SurveyBoundaryPoint origin,
        double directionX,
        double directionY,
        double normalX,
        double normalY)
    {
        double dx = point.X - origin.X;
        double dy = point.Y - origin.Y;
        return new LocalPoint(
            dx * directionX + dy * directionY,
            dx * normalX + dy * normalY);
    }

    private static SurveyBoundaryPoint FromLocal(
        double u,
        double v,
        SurveyBoundaryPoint origin,
        double directionX,
        double directionY,
        double normalX,
        double normalY) =>
        new(
            origin.X + u * directionX + v * normalX,
            origin.Y + u * directionY + v * normalY);

    private static double Distance(
        SurveyBoundaryPoint a,
        SurveyBoundaryPoint b)
    {
        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private readonly record struct LocalPoint(double U, double V);
}
