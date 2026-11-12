namespace MagOps.Core.Routes;

/// <summary>
/// Splits an overlong mother survey line into protected execution segments.
/// SegmentStableId is persistent machine identity; MotherLine.S{Sequence} is display only.
/// Re-cutting an existing segment preserves unaffected stable IDs and creates new opaque IDs
/// only for the two geometries created by that cut.
/// </summary>
public sealed class RouteExecutionSegmentPlanner
{
    public const double MinimumSegmentLengthM = 1.0;

    public RouteExecutionSegmentPlan ApplySingleCut(
        RoutePlanResult plan, string motherLine, double cutX, double cutY)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (string.IsNullOrWhiteSpace(motherLine))
            throw new ArgumentException("必须指定母测线或执行段。", nameof(motherLine));

        string sourceKey = ResolveSourceKey(plan, motherLine);
        RouteEndpoint[] source = plan.Inference.Grouped[sourceKey];
        if (source.Length != 2)
            throw new InvalidOperationException("待切分线段必须恰好有两个端点。");

        RouteExecutionSegment? sourceSegment = plan.ExecutionSegments
            .FirstOrDefault(x => x.SegmentId.Equals(sourceKey, StringComparison.OrdinalIgnoreCase));
        string businessMother = sourceSegment?.MotherLine ?? sourceKey;

        double maxLenM = plan.Options.MaxSortieKm * 1000d;
        double sourceLengthM = Distance(source[0], source[1], plan.Mode, plan.CentralMeridian);
        bool belongsToOverlongSortie = plan.Sorties.Any(sortie =>
            sortie.SourceLines.Any(line => line.Equals(sourceKey, StringComparison.OrdinalIgnoreCase)) &&
            RouteCoordinateMath.RouteLength(sortie.Points, plan.Mode, plan.CentralMeridian) > maxLenM);
        // Existing protected segments may be re-cut deliberately even when the individual
        // piece is below max length; the minimum-segment gate still applies.
        if (sourceSegment is null && sourceLengthM <= maxLenM && !belongsToOverlongSortie)
            throw new InvalidOperationException("只有导致测线或架次超出目标长度的母测线才允许首次切分。");

        double dx = source[1].X - source[0].X, dy = source[1].Y - source[0].Y;
        double denominator = dx * dx + dy * dy;
        if (denominator <= 0d) throw new InvalidOperationException("线段端点重合，无法切分。");
        double t = ((cutX - source[0].X) * dx + (cutY - source[0].Y) * dy) / denominator;
        if (t <= 0d || t >= 1d)
            throw new ArgumentOutOfRangeException(nameof(cutX), "切分点必须位于线段内部。");

        var cut = new RouteEndpoint {
            Line = sourceKey, Endpoint = 0,
            X = source[0].X + t * dx, Y = source[0].Y + t * dy,
            Label = $"{businessMother}.CUT", Pid = $"{businessMother}.CUT"
        };
        double firstLength = Distance(source[0], cut, plan.Mode, plan.CentralMeridian);
        double secondLength = Distance(cut, source[1], plan.Mode, plan.CentralMeridian);
        if (firstLength < MinimumSegmentLengthM || secondLength < MinimumSegmentLengthM)
            throw new InvalidOperationException("切分点会产生过短执行段。");
        if (sourceSegment is null && (firstLength > maxLenM || secondLength > maxLenM))
            throw new InvalidOperationException("该切分点仍会产生超长执行段，请调整切点后重试。");

        string operationId = "cut-" + Guid.NewGuid().ToString("N");
        string stableA = "segment-" + Guid.NewGuid().ToString("N");
        string stableB = "segment-" + Guid.NewGuid().ToString("N");
        string tempA = "__segment_" + stableA;
        string tempB = "__segment_" + stableB;

        var grouped = plan.Inference.Grouped.ToDictionary(
            pair => pair.Key, pair => pair.Value.Select(x => x.Clone()).ToArray(),
            StringComparer.OrdinalIgnoreCase);
        grouped.Remove(sourceKey);
        grouped[tempA] = [Clone(source[0], tempA, 0), Clone(cut, tempA, 1)];
        grouped[tempB] = [Clone(cut, tempB, 0), Clone(source[1], tempB, 1)];

        int sourceOrderIndex = plan.OrderedLines.FindIndex(x =>
            x.Equals(sourceKey, StringComparison.OrdinalIgnoreCase));
        if (sourceOrderIndex < 0)
            throw new InvalidOperationException($"当前顺序中找不到待切分线段 {sourceKey}。");
        bool reverseTraversal = sourceSegment is null
            ? SourceStartEndpoint(plan, sourceOrderIndex) == 1
            : ExistingSegmentStartEndpoint(plan, sourceSegment, source) == 1;
        var ordered = plan.OrderedLines.ToList();
        ordered.RemoveAt(sourceOrderIndex);
        ordered.InsertRange(sourceOrderIndex, reverseTraversal ? [tempB, tempA] : [tempA, tempB]);

        var segments = plan.ExecutionSegments
            .Where(x => !x.SegmentId.Equals(sourceKey, StringComparison.OrdinalIgnoreCase))
            .Select(CloneSegment)
            .ToList();
        var created = new[] {
            NewSegment(businessMother, stableA, operationId, cut, grouped[tempA]),
            NewSegment(businessMother, stableB, operationId, cut, grouped[tempB])
        };
        segments.AddRange(created);

        NormalizeMotherSegmentLabels(businessMother, ordered, grouped, segments);

        var sorties = BuildProtectedSorties(plan, grouped, ordered, segments);
        var inference = new RouteLineInference {
            Grouped = grouped, OrderedLines = ordered,
            Endpoints = grouped.Values.SelectMany(x => x).ToList(),
            MatchMode = plan.Inference.MatchMode, Merged = plan.Inference.Merged
        };
        var adapted = CopyPlan(plan, inference, ordered, sorties, segments,
            $"母测线 {businessMother} 的执行段已更新；机器身份与 S 序号分离。");
        return new RouteExecutionSegmentPlan(adapted, created);
    }

    public RouteExecutionSegmentPlan ApplySingleCutLine(
        RoutePlanResult plan, string motherLine,
        double cutStartX, double cutStartY, double cutEndX, double cutEndY)
    {
        string sourceKey = ResolveSourceKey(plan, motherLine);
        RouteEndpoint[] source = plan.Inference.Grouped[sourceKey];
        if (!TryGetInteriorIntersection(source, cutStartX, cutStartY, cutEndX, cutEndY,
                out double x, out double y))
            throw new InvalidOperationException("切割线必须穿过待切分线段内部。");
        RouteExecutionSegmentPlan result = ApplySingleCut(plan, sourceKey, x, y);
        foreach (RouteExecutionSegment segment in result.Segments) {
            segment.CutLineStartX = cutStartX; segment.CutLineStartY = cutStartY;
            segment.CutLineEndX = cutEndX; segment.CutLineEndY = cutEndY;
        }
        return result;
    }

    public RouteExecutionSegmentPlan ApplyCutLineToLines(
        RoutePlanResult plan, IEnumerable<string> motherLines,
        double cutStartX, double cutStartY, double cutEndX, double cutEndY)
    {
        RouteExecutionSegmentPlan current = new(plan, []);
        var created = new List<RouteExecutionSegment>();
        foreach (string requested in motherLines.Distinct(StringComparer.OrdinalIgnoreCase).ToArray())
        {
            // A mother may already consist of multiple execution segments. Cut every current
            // piece intersected by this operation; this is what enables S1/S2/S3+ persistence.
            string[] candidates = CurrentPieces(current.Plan, requested).ToArray();
            foreach (string candidate in candidates)
            {
                if (!current.Plan.Inference.Grouped.TryGetValue(candidate, out RouteEndpoint[]? points) ||
                    !TryGetInteriorIntersection(points, cutStartX, cutStartY, cutEndX, cutEndY, out _, out _))
                    continue;
                current = ApplySingleCutLine(current.Plan, candidate,
                    cutStartX, cutStartY, cutEndX, cutEndY);
                created.AddRange(current.Segments);
            }
        }
        if (created.Count == 0)
            throw new InvalidOperationException("切割线未穿过任何可切分测线/执行段。");
        return new RouteExecutionSegmentPlan(current.Plan, created);
    }

    private static IEnumerable<string> CurrentPieces(RoutePlanResult plan, string requested)
    {
        if (plan.Inference.Grouped.ContainsKey(requested)) return [requested];
        string[] segments = plan.ExecutionSegments
            .Where(x => x.MotherLine.Equals(requested, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.Sequence).Select(x => x.SegmentId).ToArray();
        return segments;
    }

    private static string ResolveSourceKey(RoutePlanResult plan, string requested)
    {
        string? exact = plan.Inference.Grouped.Keys.FirstOrDefault(x =>
            x.Equals(requested, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;
        RouteExecutionSegment[] segments = plan.ExecutionSegments
            .Where(x => x.MotherLine.Equals(requested, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.Sequence).ToArray();
        if (segments.Length == 1) return segments[0].SegmentId;
        if (segments.Length > 1)
            throw new InvalidOperationException(
                $"母测线 {requested} 已有 {segments.Length} 个执行段，请指定具体 S 序号或使用切割线批量操作。");
        throw new InvalidOperationException($"未找到测线/执行段 {requested}。");
    }

    private static RouteExecutionSegment CloneSegment(RouteExecutionSegment source) => new()
    {
        MotherLine = source.MotherLine,
        SegmentId = source.SegmentId,
        SegmentStableId = source.SegmentStableId,
        CutOperationId = source.CutOperationId,
        Sequence = source.Sequence,
        CutX = source.CutX,
        CutY = source.CutY,
        CutLineStartX = source.CutLineStartX,
        CutLineStartY = source.CutLineStartY,
        CutLineEndX = source.CutLineEndX,
        CutLineEndY = source.CutLineEndY,
        Start = source.Start.Clone(),
        End = source.End.Clone(),
        ProtectedConnection = source.ProtectedConnection
    };

    private static RouteExecutionSegment NewSegment(
        string mother, string stableId, string operationId,
        RouteEndpoint cut, RouteEndpoint[] endpoints) => new() {
            MotherLine = mother, SegmentStableId = stableId, CutOperationId = operationId,
            CutX = cut.X, CutY = cut.Y, Start = endpoints[0], End = endpoints[1],
            ProtectedConnection = true
        };

    private static void NormalizeMotherSegmentLabels(
        string mother, List<string> ordered, Dictionary<string, RouteEndpoint[]> grouped,
        List<RouteExecutionSegment> segments)
    {
        RouteExecutionSegment[] family = segments.Where(x =>
            x.MotherLine.Equals(mother, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (family.Length == 0) return;

        bool useX =
            family.Max(x => Math.Max(x.Start.X, x.End.X)) - family.Min(x => Math.Min(x.Start.X, x.End.X))
            >= family.Max(x => Math.Max(x.Start.Y, x.End.Y)) - family.Min(x => Math.Min(x.Start.Y, x.End.Y));
        RouteExecutionSegment[] sorted = family
            .OrderBy(x => useX ? Math.Min(x.Start.X, x.End.X) : Math.Min(x.Start.Y, x.End.Y))
            .ToArray();

        // Capture every old key before changing display labels. Two-phase temporary renaming
        // avoids collisions such as old S2 becoming S3 while old S3 still exists.
        var changes = new List<(RouteExecutionSegment Segment, string Old, string Final)>();
        for (int i = 0; i < sorted.Length; i++)
            changes.Add((sorted[i], sorted[i].SegmentId, $"{mother}.S{i + 1}"));

        var temporaryKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in changes)
        {
            string oldKey = change.Old;
            if (string.IsNullOrWhiteSpace(oldKey))
                oldKey = "__segment_" + change.Segment.SegmentStableId;
            if (!grouped.TryGetValue(oldKey, out RouteEndpoint[]? points))
                throw new InvalidOperationException($"执行段几何缺失：{oldKey}");
            string temporary = "__rename_" + change.Segment.SegmentStableId;
            grouped.Remove(oldKey);
            grouped[temporary] = points;
            int orderIndex = ordered.FindIndex(x => x.Equals(oldKey, StringComparison.OrdinalIgnoreCase));
            if (orderIndex < 0) throw new InvalidOperationException($"执行段顺序缺失：{oldKey}");
            ordered[orderIndex] = temporary;
            temporaryKeys[change.Segment.SegmentStableId] = temporary;
        }

        for (int i = 0; i < changes.Count; i++)
        {
            var change = changes[i];
            string temporary = temporaryKeys[change.Segment.SegmentStableId];
            RouteEndpoint[] points = grouped[temporary];
            grouped.Remove(temporary);
            grouped[change.Final] = points.Select((point, endpoint) =>
                Clone(point, change.Final, endpoint)).ToArray();
            int orderIndex = ordered.FindIndex(x =>
                x.Equals(temporary, StringComparison.OrdinalIgnoreCase));
            ordered[orderIndex] = change.Final;
            change.Segment.Sequence = i + 1;
            change.Segment.SegmentId = change.Final;
            change.Segment.Start = grouped[change.Final][0];
            change.Segment.End = grouped[change.Final][1];
        }
    }

    private static List<RouteSortie> BuildProtectedSorties(
        RoutePlanResult plan, Dictionary<string,RouteEndpoint[]> grouped,
        List<string> ordered, List<RouteExecutionSegment> segments)
    {
        var segmentByLabel=segments.ToDictionary(x=>x.SegmentId,StringComparer.OrdinalIgnoreCase);
        var sorties=new List<RouteSortie>(); var ordinary=new List<string>();
        void Flush() {
            if(ordinary.Count==0)return;
            sorties.AddRange(RoutePlanner.SplitSorties(grouped, ordinary, plan.ActiveStartEndpoint,
                plan.Options.MaxSortieKm*1000d, plan.Mode, plan.CentralMeridian));
            ordinary.Clear();
        }
        foreach(string line in ordered) {
            if(segmentByLabel.TryGetValue(line,out RouteExecutionSegment? segment)) {
                Flush();
                int start = SegmentStartEndpoint(
                    segment, ordered, segmentByLabel, plan.ActiveStartEndpoint);
                sorties.Add(new RouteSortie {
                    Points=RoutePlanner.BuildSnakeRoute(grouped,[line],start),
                    SourceLines=[line],
                    TaskReferences=[new RouteTaskReference {
                        LineName=segment.MotherLine, SegmentIndex=segment.Sequence,
                        ExecutionSegmentId=segment.SegmentId,
                        ExecutionSegmentStableId=segment.SegmentStableId,
                        ProtectedExecutionSegment=true
                    }]
                });
            } else ordinary.Add(line);
        }
        Flush();
        for(int i=0;i<sorties.Count;i++) sorties[i].Name=$"A{i+1:00}";
        return sorties;
    }

    private static int SegmentStartEndpoint(
        RouteExecutionSegment segment, IReadOnlyList<string> ordered,
        IReadOnlyDictionary<string, RouteExecutionSegment> segmentByLabel,
        int fallbackStartEndpoint)
    {
        RouteExecutionSegment[] family = ordered
            .Where(segmentByLabel.ContainsKey)
            .Select(label => segmentByLabel[label])
            .Where(candidate => candidate.MotherLine.Equals(
                segment.MotherLine, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (family.Length >= 2)
            return family[0].Sequence > family[^1].Sequence ? 1 : 0;
        return fallbackStartEndpoint;
    }

    private static int ExistingSegmentStartEndpoint(
        RoutePlanResult plan, RouteExecutionSegment segment, RouteEndpoint[] source)
    {
        RouteSortie? sortie = plan.Sorties.FirstOrDefault(candidate =>
            candidate.TaskReferences.Any(reference =>
                (!string.IsNullOrWhiteSpace(segment.SegmentStableId) &&
                 reference.ExecutionSegmentStableId.Equals(
                     segment.SegmentStableId, StringComparison.OrdinalIgnoreCase)) ||
                reference.ExecutionSegmentId.Equals(
                    segment.SegmentId, StringComparison.OrdinalIgnoreCase)));
        if (sortie is null || sortie.Points.Count == 0)
            return 0;
        RouteEndpoint first = sortie.Points[0];
        double toStart = Distance(first, source[0], plan.Mode, plan.CentralMeridian);
        double toEnd = Distance(first, source[1], plan.Mode, plan.CentralMeridian);
        return toEnd < toStart ? 1 : 0;
    }

    private static int SourceStartEndpoint(RoutePlanResult plan,int orderIndex) =>
        orderIndex < 0 || orderIndex % 2 == 0 ? plan.ActiveStartEndpoint : 1-plan.ActiveStartEndpoint;

    private static RoutePlanResult CopyPlan(
        RoutePlanResult plan, RouteLineInference inference, List<string> ordered,
        List<RouteSortie> sorties, List<RouteExecutionSegment> segments, string warning) => new() {
            SourcePath=plan.SourcePath, Mode=plan.Mode, CentralMeridian=plan.CentralMeridian,
            Inference=inference, OrderedLines=ordered, StartPoint=plan.StartPoint,
            ActiveStartEndpoint=plan.ActiveStartEndpoint, Sorties=sorties,
            ExecutionSegments=segments, OverLimitRoundTrips=[], GeometryDiagnostic=plan.GeometryDiagnostic,
            Options=plan.Options, PartitionCode=plan.PartitionCode,
            PartitionManifestPath=plan.PartitionManifestPath,
            Warnings=[..plan.Warnings,warning], RoundTripDistanceM=plan.RoundTripDistanceM,
            CheckLines=plan.CheckLines.ToList(), DesignBasis=plan.DesignBasis,
            AccessPlanning=plan.AccessPlanning
        };

    private static RouteEndpoint Clone(RouteEndpoint source,string line,int endpoint)=>new() {
        Line=line,Endpoint=endpoint,X=source.X,Y=source.Y,Label=source.Label,
        Pid=string.IsNullOrWhiteSpace(source.Pid)?$"{line}-{endpoint}":$"{source.Pid}.{endpoint}"
    };
    private static double Distance(RouteEndpoint a,RouteEndpoint b,string mode,double cm)=>
        RouteCoordinateMath.RouteLength([a,b],mode,cm);

    private static bool TryGetInteriorIntersection(RouteEndpoint[] source,
        double x1,double y1,double x2,double y2,out double x,out double y) {
        x=y=0d;if(source.Length!=2)return false;
        double rx=source[1].X-source[0].X,ry=source[1].Y-source[0].Y;
        double sx=x2-x1,sy=y2-y1,cross=rx*sy-ry*sx;
        if(Math.Abs(cross)<=1e-12)return false;
        double qx=x1-source[0].X,qy=y1-source[0].Y;
        double t=(qx*sy-qy*sx)/cross,u=(qx*ry-qy*rx)/cross;
        if(t<=0d||t>=1d||u<0d||u>1d)return false;
        x=source[0].X+t*rx;y=source[0].Y+t*ry;return true;
    }
}

public sealed record RouteExecutionSegmentPlan(
    RoutePlanResult Plan, IReadOnlyList<RouteExecutionSegment> Segments);
