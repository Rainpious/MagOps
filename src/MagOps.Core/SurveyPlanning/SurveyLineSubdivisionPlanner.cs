namespace MagOps.Core.SurveyPlanning;

public enum SurveySubdivisionAxis
{
    AlongSurveyLine = 0,
    ProjectedX = 1,
    ProjectedY = 2,
    Custom = 3
}


public enum SurveySubdivisionTemplate
{
    PinTopOneBottomTwo = 0,
    PinBottomOneTopTwo = 1,
    PinLeftOneRightTwo = 2,
    PinRightOneLeftTwo = 3
}

public sealed record SurveySubdivisionCutOperation(
    string OperationId,
    string TargetRegionId,
    SurveySubdivisionCutLine Cut);

public sealed class SurveySubdivisionRegion
{
    public string RegionId { get; init; } = "";
    public string ParentRegionId { get; init; } = "";
    public string CreatedByOperationId { get; init; } = "";
    public List<SurveyBoundaryPoint> Boundary { get; init; } = [];
}

public sealed class SurveySubdivisionOperationPlan
{
    public string RootRegionId { get; init; } = "ROOT";
    public string TemplateName { get; init; } = "custom";
    public List<SurveySubdivisionCutOperation> Operations { get; init; } = [];
}

public sealed record SurveySubdivisionCutLine(
    SurveyBoundaryPoint Start,
    SurveyBoundaryPoint End)
{
    public double Length => Math.Sqrt(
        Math.Pow(End.X - Start.X, 2) +
        Math.Pow(End.Y - Start.Y, 2));
}

/// <summary>
/// Splits one already-planned survey-line grid into child workareas. Child
/// flight lines are always clipped from the mother plan; SurveyLinePlanner is
/// never re-run per child. This preserves the same origin, spacing, azimuth
/// and mother-line alignment across every subarea.
/// </summary>
public sealed class SurveyLineSubdivisionPlanner
{
    private const double Tolerance = 1e-7;
    private const int MaxSubareaCount = 50;

    public static bool SupportsRegionTopology(
        SurveyRegionGeometry region,
        out string reason)
    {
        ArgumentNullException.ThrowIfNull(region);
        if (region.IsSimpleSinglePolygon)
        {
            reason = "";
            return true;
        }
        reason =
            "当前分区裁剪器只支持单 Polygon 且无内洞；MultiPolygon/holes " +
            "必须使用 topology-preserving partition engine，不能静默展平。";
        return false;
    }

    /// <summary>
    /// Backward-compatible v0.77 behavior: split into equal slabs along the
    /// survey-line direction.
    /// </summary>
    public SurveyLineSubdivisionResult Subdivide(
        SurveyLinePlanResult motherPlan,
        SurveyRegionGeometry region,
        int subareaCount)
    {
        ArgumentNullException.ThrowIfNull(region);
        if (!SupportsRegionTopology(region, out string reason))
            throw new NotSupportedException(reason);
        return Subdivide(motherPlan, subareaCount);
    }

    public SurveyLineSubdivisionResult Subdivide(
        SurveyLinePlanResult motherPlan,
        int subareaCount)
    {
        IReadOnlyList<SurveySubdivisionCutLine> cuts = CreateEqualCuts(
            motherPlan,
            SurveySubdivisionAxis.AlongSurveyLine,
            subareaCount);
        return Subdivide(
            motherPlan,
            cuts,
            SurveySubdivisionAxis.AlongSurveyLine);
    }

    public IReadOnlyList<SurveySubdivisionCutLine> CreateEqualCuts(
        SurveyLinePlanResult motherPlan,
        SurveySubdivisionAxis axis,
        int subareaCount)
    {
        ValidateMother(motherPlan);
        if (axis == SurveySubdivisionAxis.Custom)
            throw new InvalidOperationException("手绘切分线必须由用户在预览中绘制，不能自动等分。");
        if (subareaCount is < 2 or > MaxSubareaCount)
            throw new InvalidOperationException(
                $"子区块数量必须在 2～{MaxSubareaCount} 之间。");

        double minX = motherPlan.Boundary.Min(point => point.X);
        double maxX = motherPlan.Boundary.Max(point => point.X);
        double minY = motherPlan.Boundary.Min(point => point.Y);
        double maxY = motherPlan.Boundary.Max(point => point.Y);
        double diagonal = Math.Max(
            Math.Sqrt(Math.Pow(maxX - minX, 2) + Math.Pow(maxY - minY, 2)),
            1d);
        double reach = diagonal * 2d + 100d;

        var cuts = new List<SurveySubdivisionCutLine>(subareaCount - 1);
        if (axis == SurveySubdivisionAxis.ProjectedX)
        {
            double span = maxX - minX;
            if (span <= Tolerance)
                throw new InvalidDataException("母区 X / 经度方向宽度为 0，无法按 X 轴切分。");
            for (int i = 1; i < subareaCount; i++)
            {
                double x = minX + span * i / subareaCount;
                cuts.Add(new SurveySubdivisionCutLine(
                    new SurveyBoundaryPoint(x, minY - reach),
                    new SurveyBoundaryPoint(x, maxY + reach)));
            }
            return cuts;
        }

        if (axis == SurveySubdivisionAxis.ProjectedY)
        {
            double span = maxY - minY;
            if (span <= Tolerance)
                throw new InvalidDataException("母区 Y / 纬度方向宽度为 0，无法按 Y 轴切分。");
            for (int i = 1; i < subareaCount; i++)
            {
                double y = minY + span * i / subareaCount;
                cuts.Add(new SurveySubdivisionCutLine(
                    new SurveyBoundaryPoint(minX - reach, y),
                    new SurveyBoundaryPoint(maxX + reach, y)));
            }
            return cuts;
        }

        DirectionFrame frame = DirectionFrame.From(
            motherPlan.Origin,
            motherPlan.Options.AzimuthDegrees);
        double minU = motherPlan.Boundary.Min(frame.U);
        double maxU = motherPlan.Boundary.Max(frame.U);
        double spanU = maxU - minU;
        if (!double.IsFinite(spanU) || spanU <= Tolerance)
            throw new InvalidDataException("母区沿测线方向的长度为 0，无法切分。");

        // A constant-U cut is perpendicular to the survey-line direction.
        double normalX = -frame.DirectionY;
        double normalY = frame.DirectionX;
        for (int i = 1; i < subareaCount; i++)
        {
            double u = minU + spanU * i / subareaCount;
            SurveyBoundaryPoint center = new(
                motherPlan.Origin.X + frame.DirectionX * u,
                motherPlan.Origin.Y + frame.DirectionY * u);
            cuts.Add(new SurveySubdivisionCutLine(
                new SurveyBoundaryPoint(
                    center.X - normalX * reach,
                    center.Y - normalY * reach),
                new SurveyBoundaryPoint(
                    center.X + normalX * reach,
                    center.Y + normalY * reach)));
        }
        return cuts;
    }

    public SurveyLineSubdivisionResult Subdivide(
        SurveyLinePlanResult motherPlan,
        IReadOnlyList<SurveySubdivisionCutLine> cuts,
        SurveySubdivisionAxis axis,
        double overlapLengthM = 0)
    {
        ValidateMother(motherPlan);
        ValidateOverlapLength(overlapLengthM);
        cuts ??= [];
        if (cuts.Count == 0)
            throw new InvalidOperationException("至少需要 1 条有效切分线。");
        if (cuts.Count >= MaxSubareaCount)
            throw new InvalidOperationException(
                $"切分线最多允许 {MaxSubareaCount - 1} 条。");

        List<List<SurveyBoundaryPoint>> pieces =
        [NormalizePolygon(motherPlan.Boundary)];
        var effective = new Dictionary<List<SurveyBoundaryPoint>, List<SurveyBoundaryPoint>>
        {
            [pieces[0]] = pieces[0]
        };

        foreach (SurveySubdivisionCutLine cut in cuts)
        {
            ValidateCut(cut);
            var next = new List<List<SurveyBoundaryPoint>>();
            foreach (List<SurveyBoundaryPoint> piece in pieces)
            {
                List<SurveyBoundaryPoint> left = ClipPolygonByLine(
                    piece,
                    cut,
                    keepLeft: true);
                List<SurveyBoundaryPoint> right = ClipPolygonByLine(
                    piece,
                    cut,
                    keepLeft: false);

                bool leftValid = HasArea(left);
                bool rightValid = HasArea(right);
                if (leftValid && rightValid)
                {
                    effective[left] = ClipPolygonByLine(effective[piece],
                        OffsetCut(cut, true, overlapLengthM, motherPlan.Options.AzimuthDegrees), true);
                    effective[right] = ClipPolygonByLine(effective[piece],
                        OffsetCut(cut, false, overlapLengthM, motherPlan.Options.AzimuthDegrees), false);
                    next.Add(left);
                    next.Add(right);
                }
                else
                {
                    next.Add(piece);
                }
            }
            pieces = next;
        }

        pieces = pieces
            .Where(HasArea)
            .OrderBy(piece => SortKey(piece, motherPlan, axis).Primary)
            .ThenBy(piece => SortKey(piece, motherPlan, axis).Secondary)
            .ToList();

        if (pieces.Count < 2)
            throw new InvalidOperationException("切分线没有把母区分成至少两个有效子区。");
        if (pieces.Count > MaxSubareaCount)
            throw new InvalidOperationException(
                $"切分结果产生 {pieces.Count} 个子区，超过上限 {MaxSubareaCount}。");

        DirectionFrame alongFrame = DirectionFrame.From(
            motherPlan.Origin,
            motherPlan.Options.AzimuthDegrees);
        var blocks = new List<SurveyLineSubdivisionBlock>(pieces.Count);
        for (int i = 0; i < pieces.Count; i++)
        {
            List<SurveyBoundaryPoint> boundary = effective[pieces[i]];
            var childLines = new List<SurveyPlannedLine>();
            double childMinU = boundary.Min(alongFrame.U);
            double childMaxU = boundary.Max(alongFrame.U);
            foreach (SurveyPlannedLine motherLine in motherPlan.Lines)
            {
                var segments = new List<SurveyLineSegment>();
                foreach (SurveyLineSegment motherSegment in motherLine.Segments)
                {
                    // The optional parity-completion line intentionally lies one
                    // spacing outside the original mother polygon. For the normal
                    // production split (cuts perpendicular to the survey lines),
                    // clip it by the child's along-line slab instead of the old
                    // polygon so the added coverage survives into every partition.
                    if (motherLine.IsCoverageExtension &&
                        axis == SurveySubdivisionAxis.AlongSurveyLine)
                    {
                        SurveyLineSegment? clipped = ClipSegmentToAlongRange(
                            motherSegment,
                            alongFrame,
                            childMinU,
                            childMaxU);
                        if (clipped is not null)
                            segments.Add(clipped);
                    }
                    else
                    {
                        segments.AddRange(
                            ClipSegmentToPolygon(motherSegment, boundary));
                    }
                }

                segments = segments
                    .Where(segment => segment.Length > Tolerance)
                    .ToList();
                if (segments.Count == 0)
                    continue;

                childLines.Add(new SurveyPlannedLine
                {
                    LineNumber = motherLine.LineNumber,
                    Segments = segments,
                    IsCoverageExtension = motherLine.IsCoverageExtension
                });
            }

            blocks.Add(new SurveyLineSubdivisionBlock
            {
                Index = i + 1,
                StartAlongM = boundary.Min(alongFrame.U),
                EndAlongM = boundary.Max(alongFrame.U),
                Boundary = boundary,
                Lines = childLines
            });
        }

        double alongMin = motherPlan.Boundary.Min(alongFrame.U);
        double alongMax = motherPlan.Boundary.Max(alongFrame.U);

        return new SurveyLineSubdivisionResult
        {
            MotherPlan = motherPlan,
            OverlapLengthM = overlapLengthM,
            AlongMinM = alongMin,
            AlongMaxM = alongMax,
            Axis = axis,
            Cuts = cuts.ToList(),
            Subareas = blocks
        };
    }

    public SurveySubdivisionOperationPlan CreatePinTemplate(
        SurveyLinePlanResult motherPlan,
        SurveySubdivisionTemplate template,
        double primaryRatio = 0.5,
        double secondaryRatio = 0.5)
    {
        ValidateMother(motherPlan);
        if (primaryRatio is <= 0.05 or >= 0.95 || secondaryRatio is <= 0.05 or >= 0.95)
            throw new InvalidOperationException("复杂分区比例必须位于 5%～95% 之间。");

        double minX = motherPlan.Boundary.Min(p => p.X);
        double maxX = motherPlan.Boundary.Max(p => p.X);
        double minY = motherPlan.Boundary.Min(p => p.Y);
        double maxY = motherPlan.Boundary.Max(p => p.Y);
        double reach = Math.Max(Math.Sqrt(Math.Pow(maxX-minX,2)+Math.Pow(maxY-minY,2)), 1d) * 2d + 100d;

        bool horizontalPrimary = template is SurveySubdivisionTemplate.PinTopOneBottomTwo or SurveySubdivisionTemplate.PinBottomOneTopTwo;
        bool singlePositive = template is SurveySubdivisionTemplate.PinTopOneBottomTwo or SurveySubdivisionTemplate.PinRightOneLeftTwo;
        SurveySubdivisionCutLine primary;
        SurveySubdivisionCutLine secondary;
        string target;
        if (horizontalPrimary)
        {
            double y = singlePositive
                ? minY + (maxY-minY) * (1d-primaryRatio)
                : minY + (maxY-minY) * primaryRatio;
            primary = new(new(minX-reach,y), new(maxX+reach,y));
            double x = minX + (maxX-minX) * secondaryRatio;
            secondary = new(new(x,minY-reach), new(x,maxY+reach));
            target = singlePositive ? "R02" : "R01";
        }
        else
        {
            double x = singlePositive
                ? minX + (maxX-minX) * (1d-primaryRatio)
                : minX + (maxX-minX) * primaryRatio;
            primary = new(new(x,minY-reach), new(x,maxY+reach));
            double y = minY + (maxY-minY) * secondaryRatio;
            secondary = new(new(minX-reach,y), new(maxX+reach,y));
            target = singlePositive ? "R01" : "R02";
        }
        return new SurveySubdivisionOperationPlan
        {
            TemplateName = template.ToString(),
            Operations =
            [
                new("CUT01", "ROOT", primary),
                new("CUT02", target, secondary)
            ]
        };
    }

    public SurveyLineSubdivisionResult SubdivideByOperations(
        SurveyLinePlanResult motherPlan,
        SurveySubdivisionOperationPlan plan,
        double overlapLengthM = 0)
    {
        ValidateMother(motherPlan);
        ValidateOverlapLength(overlapLengthM);
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Operations.Count == 0)
            throw new InvalidOperationException("复杂分区至少需要一个局部切割操作。");

        var regions = new Dictionary<string,List<SurveyBoundaryPoint>>(StringComparer.OrdinalIgnoreCase)
        {
            [plan.RootRegionId] = NormalizePolygon(motherPlan.Boundary)
        };
        var effectiveRegions = new Dictionary<string, List<SurveyBoundaryPoint>>(regions, StringComparer.OrdinalIgnoreCase);
        var leaves = new List<string> { plan.RootRegionId };
        int nextRegion = 1;
        foreach (SurveySubdivisionCutOperation op in plan.Operations)
        {
            ValidateCut(op.Cut);
            if (!regions.TryGetValue(op.TargetRegionId, out List<SurveyBoundaryPoint>? target) || !leaves.Contains(op.TargetRegionId, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException($"局部切割 {op.OperationId} 的目标区域 {op.TargetRegionId} 不存在或已被继续切割。");
            List<SurveyBoundaryPoint> left = ClipPolygonByLine(target, op.Cut, true);
            List<SurveyBoundaryPoint> right = ClipPolygonByLine(target, op.Cut, false);
            if (!HasArea(left) || !HasArea(right))
                throw new InvalidOperationException($"局部切割 {op.OperationId} 没有把目标区域 {op.TargetRegionId} 分成两个有效子区。");
            string a=$"R{nextRegion++:D2}"; string b=$"R{nextRegion++:D2}";
            regions[a]=left; regions[b]=right;
            effectiveRegions[a] = ClipPolygonByLine(effectiveRegions[op.TargetRegionId],
                OffsetCut(op.Cut, true, overlapLengthM, motherPlan.Options.AzimuthDegrees), true);
            effectiveRegions[b] = ClipPolygonByLine(effectiveRegions[op.TargetRegionId],
                OffsetCut(op.Cut, false, overlapLengthM, motherPlan.Options.AzimuthDegrees), false);
            int idx=leaves.FindIndex(x=>x.Equals(op.TargetRegionId,StringComparison.OrdinalIgnoreCase));
            leaves.RemoveAt(idx); leaves.Insert(idx,a); leaves.Insert(idx+1,b);
        }
        var pieces=leaves.Where(id=>HasArea(regions[id]))
            .OrderByDescending(id=>regions[id].Average(p=>p.Y))
            .ThenBy(id=>regions[id].Average(p=>p.X))
            .Select(id=>effectiveRegions[id]).ToList();
        return BuildResult(motherPlan, pieces, SurveySubdivisionAxis.Custom, plan.Operations.Select(x=>x.Cut).ToList(), overlapLengthM);
    }

    private SurveyLineSubdivisionResult BuildResult(
        SurveyLinePlanResult motherPlan,
        List<List<SurveyBoundaryPoint>> pieces,
        SurveySubdivisionAxis axis,
        IReadOnlyList<SurveySubdivisionCutLine> cuts,
        double overlapLengthM = 0)
    {
        if (pieces.Count < 2 || pieces.Count > MaxSubareaCount)
            throw new InvalidOperationException($"切分结果子区数量 {pieces.Count} 无效。");
        DirectionFrame alongFrame = DirectionFrame.From(motherPlan.Origin, motherPlan.Options.AzimuthDegrees);
        var blocks = new List<SurveyLineSubdivisionBlock>(pieces.Count);
        for (int i=0;i<pieces.Count;i++)
        {
            List<SurveyBoundaryPoint> boundary=pieces[i];
            var childLines=new List<SurveyPlannedLine>();
            foreach (SurveyPlannedLine motherLine in motherPlan.Lines)
            {
                var segments=new List<SurveyLineSegment>();
                foreach (SurveyLineSegment motherSegment in motherLine.Segments)
                    segments.AddRange(ClipSegmentToPolygon(motherSegment,boundary));
                segments=segments.Where(x=>x.Length>Tolerance).ToList();
                if (segments.Count>0) childLines.Add(new SurveyPlannedLine { LineNumber=motherLine.LineNumber, Segments=segments, IsCoverageExtension=motherLine.IsCoverageExtension });
            }
            blocks.Add(new SurveyLineSubdivisionBlock { Index=i+1, StartAlongM=boundary.Min(alongFrame.U), EndAlongM=boundary.Max(alongFrame.U), Boundary=boundary, Lines=childLines });
        }
        return new SurveyLineSubdivisionResult { MotherPlan=motherPlan, OverlapLengthM=overlapLengthM, AlongMinM=motherPlan.Boundary.Min(alongFrame.U), AlongMaxM=motherPlan.Boundary.Max(alongFrame.U), Axis=axis, Cuts=cuts.ToList(), Subareas=blocks };
    }

    private static void ValidateOverlapLength(double overlapLengthM)
    {
        if (!double.IsFinite(overlapLengthM) || overlapLengthM < 0)
            throw new ArgumentOutOfRangeException(nameof(overlapLengthM), "分区测线重叠长度必须为有限非负数。");
    }

    // Shift each cut outward by half the requested along-line overlap.
    // The projection handles oblique cuts; cuts parallel to survey lines do not
    // create artificial overlap between different mother lines. Replaying cuts
    // against the mother polygon keeps the outer workarea boundary unchanged.
    private static SurveySubdivisionCutLine OffsetCut(
        SurveySubdivisionCutLine cut, bool keepLeft, double overlapLengthM, double azimuthDegrees)
    {
        if (overlapLengthM == 0) return cut;
        double nx = -(cut.End.Y - cut.Start.Y) / cut.Length;
        double ny = (cut.End.X - cut.Start.X) / cut.Length;
        double angle = azimuthDegrees * Math.PI / 180;
        double alongProjection = Math.Abs(nx * Math.Sin(angle) + ny * Math.Cos(angle));
        if (alongProjection <= Tolerance)
            return cut; // this cut is parallel to the survey lines
        double offset = overlapLengthM / 2d * alongProjection;
        if (keepLeft) offset = -offset;
        return new SurveySubdivisionCutLine(
            new(cut.Start.X + nx * offset, cut.Start.Y + ny * offset),
            new(cut.End.X + nx * offset, cut.End.Y + ny * offset));
    }

    public int SuggestSubareaCount(
        SurveyLinePlanResult motherPlan,
        double maxSortieKm)
    {
        ArgumentNullException.ThrowIfNull(motherPlan);
        if (!double.IsFinite(maxSortieKm) || maxSortieKm <= 0)
            throw new InvalidOperationException("最大单架次长度必须大于 0。");

        double maxSegment = motherPlan.Lines
            .SelectMany(line => line.Segments)
            .Select(segment => segment.Length)
            .DefaultIfEmpty(0d)
            .Max();

        if (maxSegment <= Tolerance)
            return 1;

        double estimatedRoundTrip =
            maxSegment * 2.0 + Math.Max(0d, motherPlan.Options.SpacingM);
        int count = (int)Math.Ceiling(
            estimatedRoundTrip / (maxSortieKm * 1000.0));
        return Math.Clamp(count, 1, MaxSubareaCount);
    }

    private static void ValidateMother(SurveyLinePlanResult motherPlan)
    {
        ArgumentNullException.ThrowIfNull(motherPlan);
        if (motherPlan.Boundary.Count < 3)
            throw new InvalidDataException("母区边界至少需要 3 个有效点。");
        if (motherPlan.Lines.Count == 0)
            throw new InvalidOperationException("母区尚未生成有效测线，无法分区。");
    }

    private static void ValidateCut(SurveySubdivisionCutLine cut)
    {
        if (!double.IsFinite(cut.Start.X) || !double.IsFinite(cut.Start.Y) ||
            !double.IsFinite(cut.End.X) || !double.IsFinite(cut.End.Y) ||
            cut.Length <= Tolerance)
        {
            throw new InvalidDataException("切分线必须由两个不同的有效 XY 点组成。");
        }
    }

    private static List<SurveyBoundaryPoint> ClipPolygonByLine(
        IReadOnlyList<SurveyBoundaryPoint> polygon,
        SurveySubdivisionCutLine cut,
        bool keepLeft)
    {
        if (polygon.Count == 0)
            return [];

        double Side(SurveyBoundaryPoint point)
        {
            double dx = cut.End.X - cut.Start.X;
            double dy = cut.End.Y - cut.Start.Y;
            return dx * (point.Y - cut.Start.Y) -
                   dy * (point.X - cut.Start.X);
        }

        bool Inside(SurveyBoundaryPoint point)
        {
            double side = Side(point);
            return keepLeft
                ? side >= -Tolerance
                : side <= Tolerance;
        }

        SurveyBoundaryPoint Intersect(
            SurveyBoundaryPoint a,
            SurveyBoundaryPoint b)
        {
            double sa = Side(a);
            double sb = Side(b);
            double denominator = sa - sb;
            if (Math.Abs(denominator) <= Tolerance)
                return a;
            double t = Math.Clamp(sa / denominator, 0d, 1d);
            return Lerp(a, b, t);
        }

        var output = new List<SurveyBoundaryPoint>();
        SurveyBoundaryPoint previous = polygon[^1];
        bool previousInside = Inside(previous);
        foreach (SurveyBoundaryPoint current in polygon)
        {
            bool currentInside = Inside(current);
            if (currentInside)
            {
                if (!previousInside)
                    output.Add(Intersect(previous, current));
                output.Add(current);
            }
            else if (previousInside)
            {
                output.Add(Intersect(previous, current));
            }
            previous = current;
            previousInside = currentInside;
        }
        return NormalizePolygon(output);
    }

    private static SurveyLineSegment? ClipSegmentToAlongRange(
        SurveyLineSegment segment,
        DirectionFrame frame,
        double minU,
        double maxU)
    {
        double startU = frame.U(segment.Start);
        double endU = frame.U(segment.End);
        double delta = endU - startU;
        if (Math.Abs(delta) <= Tolerance)
            return null;

        double tA = (minU - startU) / delta;
        double tB = (maxU - startU) / delta;
        double lo = Math.Max(0d, Math.Min(tA, tB));
        double hi = Math.Min(1d, Math.Max(tA, tB));
        if (hi - lo <= Tolerance)
            return null;

        return new SurveyLineSegment(
            Lerp(segment.Start, segment.End, lo),
            Lerp(segment.Start, segment.End, hi));
    }

    private static List<SurveyLineSegment> ClipSegmentToPolygon(
        SurveyLineSegment segment,
        IReadOnlyList<SurveyBoundaryPoint> polygon)
    {
        var parameters = new List<double> { 0d, 1d };
        for (int i = 0; i < polygon.Count; i++)
        {
            SurveyBoundaryPoint a = polygon[i];
            SurveyBoundaryPoint b = polygon[(i + 1) % polygon.Count];
            if (TrySegmentIntersectionParameter(
                    segment.Start,
                    segment.End,
                    a,
                    b,
                    out double t))
            {
                parameters.Add(t);
            }
        }

        double[] ordered = parameters
            .Where(value => value >= -Tolerance && value <= 1d + Tolerance)
            .Select(value => Math.Clamp(value, 0d, 1d))
            .OrderBy(value => value)
            .DistinctBy(value => Math.Round(value, 10))
            .ToArray();

        var result = new List<SurveyLineSegment>();
        for (int i = 0; i < ordered.Length - 1; i++)
        {
            double a = ordered[i];
            double b = ordered[i + 1];
            if (b - a <= Tolerance)
                continue;
            SurveyBoundaryPoint middle = Lerp(
                segment.Start,
                segment.End,
                (a + b) / 2d);
            if (!PointInOrOnPolygon(middle, polygon))
                continue;
            result.Add(new SurveyLineSegment(
                Lerp(segment.Start, segment.End, a),
                Lerp(segment.Start, segment.End, b)));
        }
        return result;
    }

    private static bool TrySegmentIntersectionParameter(
        SurveyBoundaryPoint p,
        SurveyBoundaryPoint p2,
        SurveyBoundaryPoint q,
        SurveyBoundaryPoint q2,
        out double t)
    {
        t = 0d;
        double rx = p2.X - p.X;
        double ry = p2.Y - p.Y;
        double sx = q2.X - q.X;
        double sy = q2.Y - q.Y;
        double cross = rx * sy - ry * sx;
        if (Math.Abs(cross) <= Tolerance)
            return false;

        double qpx = q.X - p.X;
        double qpy = q.Y - p.Y;
        double u = (qpx * ry - qpy * rx) / cross;
        double candidate = (qpx * sy - qpy * sx) / cross;
        if (candidate < -Tolerance || candidate > 1d + Tolerance ||
            u < -Tolerance || u > 1d + Tolerance)
        {
            return false;
        }

        t = Math.Clamp(candidate, 0d, 1d);
        return true;
    }

    private static bool PointInOrOnPolygon(
        SurveyBoundaryPoint point,
        IReadOnlyList<SurveyBoundaryPoint> polygon)
    {
        bool inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            SurveyBoundaryPoint a = polygon[j];
            SurveyBoundaryPoint b = polygon[i];
            if (DistancePointToSegment(point, a, b) <= 1e-6)
                return true;

            bool crosses = (a.Y > point.Y) != (b.Y > point.Y);
            if (!crosses)
                continue;
            double x = (b.X - a.X) * (point.Y - a.Y) /
                       (b.Y - a.Y) + a.X;
            if (point.X < x)
                inside = !inside;
        }
        return inside;
    }

    private static double DistancePointToSegment(
        SurveyBoundaryPoint p,
        SurveyBoundaryPoint a,
        SurveyBoundaryPoint b)
    {
        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        double denom = dx * dx + dy * dy;
        if (denom <= Tolerance)
            return Distance(p, a);
        double t = Math.Clamp(
            ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / denom,
            0d,
            1d);
        return Distance(p, new SurveyBoundaryPoint(a.X + dx * t, a.Y + dy * t));
    }

    private static (double Primary, double Secondary) SortKey(
        IReadOnlyList<SurveyBoundaryPoint> polygon,
        SurveyLinePlanResult motherPlan,
        SurveySubdivisionAxis axis)
    {
        double x = polygon.Average(point => point.X);
        double y = polygon.Average(point => point.Y);
        return axis switch
        {
            SurveySubdivisionAxis.ProjectedX => (x, y),
            SurveySubdivisionAxis.ProjectedY => (y, x),
            SurveySubdivisionAxis.AlongSurveyLine =>
                (DirectionFrame.From(
                    motherPlan.Origin,
                    motherPlan.Options.AzimuthDegrees).U(
                        new SurveyBoundaryPoint(x, y)), x),
            _ => (x, y)
        };
    }

    private static bool HasArea(IReadOnlyList<SurveyBoundaryPoint> polygon) =>
        polygon.Count >= 3 && Math.Abs(SignedArea(polygon)) > 1e-4;

    private static double SignedArea(IReadOnlyList<SurveyBoundaryPoint> polygon)
    {
        double area2 = 0d;
        for (int i = 0; i < polygon.Count; i++)
        {
            SurveyBoundaryPoint a = polygon[i];
            SurveyBoundaryPoint b = polygon[(i + 1) % polygon.Count];
            area2 += a.X * b.Y - b.X * a.Y;
        }
        return area2 / 2d;
    }

    private static SurveyBoundaryPoint Lerp(
        SurveyBoundaryPoint a,
        SurveyBoundaryPoint b,
        double t) =>
        new(
            a.X + (b.X - a.X) * t,
            a.Y + (b.Y - a.Y) * t);

    private static List<SurveyBoundaryPoint> NormalizePolygon(
        IReadOnlyList<SurveyBoundaryPoint> source)
    {
        var result = new List<SurveyBoundaryPoint>();
        foreach (SurveyBoundaryPoint point in source)
        {
            if (result.Count == 0 || Distance(result[^1], point) > Tolerance)
                result.Add(point);
        }
        if (result.Count > 1 && Distance(result[0], result[^1]) <= Tolerance)
            result.RemoveAt(result.Count - 1);
        return result;
    }

    private static double Distance(
        SurveyBoundaryPoint a,
        SurveyBoundaryPoint b)
    {
        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private readonly record struct DirectionFrame(
        SurveyBoundaryPoint Origin,
        double DirectionX,
        double DirectionY)
    {
        public static DirectionFrame From(
            SurveyBoundaryPoint origin,
            double azimuthDegrees)
        {
            double normalized = azimuthDegrees % 360.0;
            if (normalized < 0)
                normalized += 360.0;
            double radians = normalized * Math.PI / 180.0;
            return new DirectionFrame(
                origin,
                Math.Sin(radians),
                Math.Cos(radians));
        }

        public double U(SurveyBoundaryPoint point)
        {
            double dx = point.X - Origin.X;
            double dy = point.Y - Origin.Y;
            return dx * DirectionX + dy * DirectionY;
        }
    }
}

public sealed class SurveyLineSubdivisionBlock
{
    public int Index { get; init; }
    public double StartAlongM { get; init; }
    public double EndAlongM { get; init; }
    public List<SurveyBoundaryPoint> Boundary { get; init; } = [];
    public List<SurveyPlannedLine> Lines { get; init; } = [];

    public double AlongLengthM => EndAlongM - StartAlongM;
    public double MaxSegmentLengthM => Lines
        .SelectMany(line => line.Segments)
        .Select(segment => segment.Length)
        .DefaultIfEmpty(0d)
        .Max();
}

public sealed class SurveyLineSubdivisionResult
{
    public SurveyLinePlanResult MotherPlan { get; init; } = new();
    public double OverlapLengthM { get; init; }
    public double AlongMinM { get; init; }
    public double AlongMaxM { get; init; }
    public SurveySubdivisionAxis Axis { get; init; } = SurveySubdivisionAxis.AlongSurveyLine;
    public List<SurveySubdivisionCutLine> Cuts { get; init; } = [];
    public List<SurveyLineSubdivisionBlock> Subareas { get; init; } = [];
}
