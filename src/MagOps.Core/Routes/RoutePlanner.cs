using System.Text.RegularExpressions;

namespace MagOps.Core.Routes;

public sealed class RoutePlanner
{
    public const string AlgorithmVersion = "route-v25-csharp-0.3.0";

    private static readonly Regex LineNumberRegex =
        new(@"(?i)L\s*([+-]?\d+)", RegexOptions.Compiled);
    private static readonly Regex EndpointHintRegex =
        new(@"(?i)^\s*([01])\s*/\s*L\s*[+-]?\d+", RegexOptions.Compiled);

    public RoutePlanResult BuildFromCsv(string sourcePath, RoutePlanOptions options)
    {
        var raw = DelimitedTextReader.ReadRawRouteCsv(sourcePath, options.HeaderlessOrder).ToArray();
        var inference = InferSpatialLines(raw);

        string mode = options.CoordinateMode switch
        {
            RouteCoordinateMode.Projected => "projected",
            RouteCoordinateMode.Geographic => "geographic",
            _ => RouteCoordinateMath.IsGeographic(inference.Endpoints) ? "geographic" : "projected"
        };

        double centralMeridian = mode == "projected"
            ? options.CentralMeridian
              ?? RouteCoordinateMath.InferCentralMeridian(raw, sourcePath)
              ?? throw new InvalidOperationException("投影坐标未能自动确定中央经线，请手工填写。")
            : 0.0;

        if (options.MaxSortieKm <= 0)
            throw new ArgumentOutOfRangeException(nameof(options.MaxSortieKm), "最大单架次长度必须大于 0。");

        // Flight speed belongs to the frozen DJI real-time terrain-following
        // KMZ export workflow. Ordinary route planning is geometry-only and
        // must never require or validate that export-specific parameter.

        RouteEndpoint start = ResolveStartPoint(
            inference,
            options.SelectedStartPid,
            options.StartDirection,
            options.PartitionStartSide,
            mode);

        var orderInfo = GetLineOrder(
            inference.Grouped,
            inference.OrderedLines,
            start,
            mode,
            centralMeridian);

        double maxLenM = options.MaxSortieKm * 1000.0;
        var overRoundTrips = GetOverLimitRoundTrips(
            inference.Grouped, orderInfo.Order, orderInfo.StartEp, maxLenM, mode, centralMeridian);
        var sorties = SplitSorties(
            inference.Grouped, orderInfo.Order, orderInfo.StartEp, maxLenM, mode, centralMeridian);

        var diagnostic = GetLineGeometryDiagnostic(inference, mode);
        var warnings = new List<string>();
        if (!string.Equals(start.Line, inference.OrderedLines.First(), StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(start.Line, inference.OrderedLines.Last(), StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add("起点位于内部测线，覆盖全部测线时会存在一次较长跨区连接。");
        }

        int overCount = sorties.Count(s =>
            RouteCoordinateMath.RouteLength(s.Points, mode, centralMeridian) > maxLenM);
        if (overCount > 0)
            warnings.Add($"有 {overCount} 个最终架次超过 {options.MaxSortieKm:0.###} km 上限；建议增加作业分区或调整架次编排，不得通过删除正式测线缩小测区覆盖。");

        var result = new RoutePlanResult
        {
            SourcePath = Path.GetFullPath(sourcePath),
            Mode = mode,
            CentralMeridian = centralMeridian,
            Inference = inference,
            OrderedLines = orderInfo.Order,
            StartPoint = start,
            ActiveStartEndpoint = orderInfo.StartEp,
            Sorties = sorties,
            OverLimitRoundTrips = overRoundTrips,
            GeometryDiagnostic = diagnostic,
            Warnings = warnings,
            Options = options
        };
        if (LaunchSitePlanning.Enabled && options.TakeoffX.HasValue && options.TakeoffY.HasValue)
            result.RoundTripDistanceM = RouteCoordinateMath.RoundTripDistance(
                options.TakeoffX.Value, options.TakeoffY.Value, result.Sorties, mode, centralMeridian);
        return result;
    }

    public RouteLineInference InferSpatialLines(IReadOnlyList<RawRoutePoint> points)
    {
        if (points.Count < 2)
            throw new InvalidDataException("至少需要 2 个端点记录。");

        var parsed = new int?[points.Count];
        int parsedCount = 0;
        for (int i = 0; i < points.Count; i++)
        {
            parsed[i] = GetLineNumber(points[i].Label);
            if (parsed[i].HasValue) parsedCount++;
        }

        if (parsedCount > 0)
            return InferUsingLineNumbers(points, parsed, parsedCount);

        return InferUsingGeometry(points);
    }

    public static List<RouteEndpoint> BuildSnakeRoute(
        IReadOnlyDictionary<string, RouteEndpoint[]> grouped,
        IReadOnlyList<string> lines,
        int startEp)
    {
        var result = new List<RouteEndpoint>(lines.Count * 2);
        for (int i = 0; i < lines.Count; i++)
        {
            int entry = i % 2 == 0 ? startEp : 1 - startEp;
            int exit = 1 - entry;
            result.Add(grouped[lines[i]][entry]);
            result.Add(grouped[lines[i]][exit]);
        }
        return result;
    }

    public static RouteEndpoint GetStartPointFromCorner(
        IReadOnlyDictionary<string, RouteEndpoint[]> grouped,
        IReadOnlyList<string> lines,
        RouteStartDirection corner,
        string mode)
    {
        if (lines.Count == 0) throw new InvalidOperationException("没有测线。");

        var outside = new List<string> { lines[0] };
        if (!string.Equals(lines[^1], lines[0], StringComparison.OrdinalIgnoreCase))
            outside.Add(lines[^1]);

        var candidates = new List<(RouteEndpoint Point, double East, double North)>();
        foreach (string line in outside)
        {
            foreach (RouteEndpoint p in grouped[line])
            {
                if (mode == "projected")
                {
                    var en = RouteCoordinateMath.ResolveProjectedEN(p.X, p.Y);
                    candidates.Add((p, en.E, en.N));
                }
                else
                {
                    candidates.Add((p, p.X, p.Y));
                }
            }
        }

        return GetStartPointFromCandidates(
            candidates.Select(x => x.Point).ToArray(),
            corner,
            mode);
    }

    /// <summary>
    /// Selects one endpoint from an already constrained candidate set according
    /// to the user's corner preference. Partition planning uses this after first
    /// constraining candidates to shared cut-boundary endpoints, so both child
    /// workareas start from the same field side while preserving the familiar
    /// north/south/west/east preference along that boundary.
    /// </summary>
    public static RouteEndpoint GetStartPointFromCandidates(
        IReadOnlyList<RouteEndpoint> endpoints,
        RouteStartDirection corner,
        string mode)
    {
        if (endpoints.Count == 0)
            throw new InvalidOperationException("没有可用的起始端点候选。");

        var candidates = endpoints.Select(point =>
        {
            if (mode == "projected")
            {
                var en = RouteCoordinateMath.ResolveProjectedEN(point.X, point.Y);
                return (Point: point, East: en.E, North: en.N);
            }
            return (Point: point, East: point.X, North: point.Y);
        }).ToList();

        double minE = candidates.Min(c => c.East);
        double maxE = candidates.Max(c => c.East);
        double minN = candidates.Min(c => c.North);
        double maxN = candidates.Max(c => c.North);

        int cornerIndex = (int)corner;
        double targetE = cornerIndex >= 2 ? 1.0 : 0.0;
        double targetN = cornerIndex % 2 == 1 ? 1.0 : 0.0;

        RouteEndpoint? best = null;
        double bestScore = double.PositiveInfinity;
        foreach (var c in candidates)
        {
            double ne = maxE - minE > 0 ? (c.East - minE) / (maxE - minE) : 0.5;
            double nn = maxN - minN > 0 ? (c.North - minN) / (maxN - minN) : 0.5;
            double score = (ne - targetE) * (ne - targetE) +
                           (nn - targetN) * (nn - targetN);
            if (score < bestScore)
            {
                bestScore = score;
                best = c.Point;
            }
        }

        return best ?? throw new InvalidOperationException("无法确定起点。");
    }

    /// <summary>
    /// Selects a route start from an explicit cardinal side of a partition.
    /// The requested side is the primary constraint; the existing corner
    /// preference is only a secondary selector inside a narrow side band. This
    /// lets neighboring partitions deliberately choose East/West or
    /// North/South respectively and therefore launch from their common edge.
    /// </summary>
    public static RouteEndpoint GetStartPointFromSide(
        IReadOnlyList<RouteEndpoint> endpoints,
        RoutePartitionStartSide side,
        RouteStartDirection corner,
        string mode)
    {
        if (endpoints.Count == 0)
            throw new InvalidOperationException("没有可用的起始端点候选。");

        if (side == RoutePartitionStartSide.Automatic)
            return GetStartPointFromCandidates(endpoints, corner, mode);

        var candidates = endpoints.Select(point =>
        {
            if (mode == "projected")
            {
                var en = RouteCoordinateMath.ResolveProjectedEN(point.X, point.Y);
                return (Point: point, East: en.E, North: en.N);
            }

            return (Point: point, East: point.X, North: point.Y);
        }).ToList();

        double minE = candidates.Min(c => c.East);
        double maxE = candidates.Max(c => c.East);
        double minN = candidates.Min(c => c.North);
        double maxN = candidates.Max(c => c.North);
        double spanE = Math.Max(0d, maxE - minE);
        double spanN = Math.Max(0d, maxN - minN);

        double PrimaryDistance((RouteEndpoint Point, double East, double North) c) => side switch
        {
            RoutePartitionStartSide.North => spanN <= 1e-12 ? 0d : (maxN - c.North) / spanN,
            RoutePartitionStartSide.South => spanN <= 1e-12 ? 0d : (c.North - minN) / spanN,
            RoutePartitionStartSide.West => spanE <= 1e-12 ? 0d : (c.East - minE) / spanE,
            RoutePartitionStartSide.East => spanE <= 1e-12 ? 0d : (maxE - c.East) / spanE,
            _ => 0d
        };

        double bestPrimary = candidates.Min(PrimaryDistance);
        const double normalizedSideBand = 0.05d;
        RouteEndpoint[] sideCandidates = candidates
            .Where(candidate =>
                PrimaryDistance(candidate) <=
                bestPrimary + normalizedSideBand + 1e-12)
            .Select(candidate => candidate.Point)
            .ToArray();

        return GetStartPointFromCandidates(
            sideCandidates.Length > 0
                ? sideCandidates
                : endpoints,
            corner,
            mode);
    }

    /// <summary>
    /// Selects a start point for a complete snake traversal from the two outer
    /// survey lines only. This prevents an explicit partition side from picking
    /// an interior line endpoint, which would force GetLineOrder to leave one
    /// side of the grid, wrap across the workarea, and create the long diagonal
    /// connector seen in partition previews.
    /// </summary>
    public static RouteEndpoint GetTraversalStartPointFromSide(
        IReadOnlyDictionary<string, RouteEndpoint[]> grouped,
        IReadOnlyList<string> lines,
        RoutePartitionStartSide side,
        RouteStartDirection corner,
        string mode)
    {
        RouteEndpoint[] outside = GetTraversalBoundaryEndpoints(grouped, lines);
        return side == RoutePartitionStartSide.Automatic
            ? GetStartPointFromCandidates(outside, corner, mode)
            : GetStartPointFromSide(outside, side, corner, mode);
    }

    /// <summary>
    /// Applies an additional boundary constraint (for example the real shared
    /// partition cut) without ever selecting an interior survey line as the
    /// first line. Exact constrained outer endpoints are preferred. If a cut
    /// only intersects interior line endpoints because of numeric/topology
    /// details, the nearest outer endpoint is used as a safe traversal start.
    /// </summary>
    public static RouteEndpoint GetTraversalStartPointFromCandidates(
        IReadOnlyDictionary<string, RouteEndpoint[]> grouped,
        IReadOnlyList<string> lines,
        IReadOnlyList<RouteEndpoint> constrainedCandidates,
        RouteStartDirection corner,
        string mode)
    {
        RouteEndpoint[] outside = GetTraversalBoundaryEndpoints(grouped, lines);
        if (constrainedCandidates.Count == 0)
            return GetStartPointFromCandidates(outside, corner, mode);

        HashSet<string> constrainedPids = constrainedCandidates
            .Where(point => !string.IsNullOrWhiteSpace(point.Pid))
            .Select(point => point.Pid)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        RouteEndpoint[] exact = outside
            .Where(point => constrainedPids.Contains(point.Pid))
            .ToArray();
        if (exact.Length > 0)
            return GetStartPointFromCandidates(exact, corner, mode);

        static (double E, double N) Physical(RouteEndpoint point, string coordinateMode)
        {
            if (coordinateMode == "projected")
            {
                var en = RouteCoordinateMath.ResolveProjectedEN(point.X, point.Y);
                return (en.E, en.N);
            }
            return (point.X, point.Y);
        }

        var constrainedPhysical = constrainedCandidates
            .Select(point => Physical(point, mode))
            .ToArray();
        var ranked = outside
            .Select(point =>
            {
                (double e, double n) = Physical(point, mode);
                double distanceSquared = constrainedPhysical
                    .Select(target =>
                    {
                        double de = e - target.E;
                        double dn = n - target.N;
                        return de * de + dn * dn;
                    })
                    .DefaultIfEmpty(double.PositiveInfinity)
                    .Min();
                return (Point: point, DistanceSquared: distanceSquared);
            })
            .OrderBy(row => row.DistanceSquared)
            .ToArray();

        double best = ranked[0].DistanceSquared;
        double epsilon = Math.Max(1e-18, Math.Abs(best) * 1e-9);
        RouteEndpoint[] nearest = ranked
            .Where(row => row.DistanceSquared <= best + epsilon)
            .Select(row => row.Point)
            .ToArray();
        return GetStartPointFromCandidates(nearest, corner, mode);
    }

    private static RouteEndpoint[] GetTraversalBoundaryEndpoints(
        IReadOnlyDictionary<string, RouteEndpoint[]> grouped,
        IReadOnlyList<string> lines)
    {
        if (lines.Count == 0)
            throw new InvalidOperationException("没有测线。");

        var outsideLines = new List<string> { lines[0] };
        if (!string.Equals(lines[^1], lines[0], StringComparison.OrdinalIgnoreCase))
            outsideLines.Add(lines[^1]);

        RouteEndpoint[] endpoints = outsideLines
            .Where(grouped.ContainsKey)
            .SelectMany(line => grouped[line])
            .DistinctBy(point =>
                string.IsNullOrWhiteSpace(point.Pid)
                    ? $"{point.Line}|{point.Endpoint}|{point.X:R}|{point.Y:R}"
                    : point.Pid,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (endpoints.Length == 0)
            throw new InvalidOperationException("没有可用的外侧测线端点。");
        return endpoints;
    }

    public static (List<string> Order, int StartEp) GetLineOrder(
        IReadOnlyDictionary<string, RouteEndpoint[]> grouped,
        IReadOnlyList<string> lines,
        RouteEndpoint start,
        string mode,
        double centralMeridian)
    {
        int idx = lines.ToList().FindIndex(x => x.Equals(start.Line, StringComparison.OrdinalIgnoreCase));
        if (idx < 0) throw new InvalidOperationException("起点不属于已识别测线。");
        int n = lines.Count;

        if (idx == 0) return (lines.ToList(), start.Endpoint);
        if (idx == n - 1) return (lines.Reverse().ToList(), start.Endpoint);

        var leftRev = lines.Take(idx).Reverse().ToList();
        var through = lines.Skip(idx).ToList();
        var candidateA = through.Concat(leftRev).ToList();

        var throughLeft = lines.Take(idx + 1).Reverse().ToList();
        var right = lines.Skip(idx + 1).ToList();
        var candidateB = throughLeft.Concat(right).ToList();

        var ra = BuildSnakeRoute(grouped, candidateA, start.Endpoint);
        var rb = BuildSnakeRoute(grouped, candidateB, start.Endpoint);
        return RouteCoordinateMath.RouteLength(ra, mode, centralMeridian)
               <= RouteCoordinateMath.RouteLength(rb, mode, centralMeridian)
            ? (candidateA, start.Endpoint)
            : (candidateB, start.Endpoint);
    }

    public static List<RouteSortie> SplitSorties(
        IReadOnlyDictionary<string, RouteEndpoint[]> grouped,
        IReadOnlyList<string> lines,
        int startEp,
        double maxLenM,
        string mode,
        double centralMeridian)
    {
        var sorties = new List<RouteSortie>();
        int i = 0;
        int n = lines.Count;

        while (i < n)
        {
            int remain = n - i;
            int maxFit = 0;
            var routes = new Dictionary<int, List<RouteEndpoint>>();

            for (int count = 1; count <= remain; count++)
            {
                var sub = lines.Skip(i).Take(count).ToList();
                var route = BuildSnakeRoute(grouped, sub, startEp);
                routes[count] = route;
                if (RouteCoordinateMath.RouteLength(route, mode, centralMeridian) <= maxLenM)
                    maxFit = count;
                else
                    break;
            }

            int chosen;
            if (maxFit == remain && remain > 0)
            {
                chosen = remain;
            }
            else
            {
                var evens = Enumerable.Range(2, Math.Max(0, maxFit - 1))
                    .Where(c => c % 2 == 0)
                    .ToArray();
                if (evens.Length > 0) chosen = evens.Max();
                else if (remain >= 2) chosen = 2;
                else chosen = 1;
            }

            var chosenLines = lines.Skip(i).Take(chosen).ToList();
            var chosenRoute = routes.TryGetValue(chosen, out var cached)
                ? cached
                : BuildSnakeRoute(grouped, chosenLines, startEp);

            sorties.Add(new RouteSortie
            {
                Name = $"A{sorties.Count + 1:D2}",
                Points = chosenRoute,
                SourceLines = chosenLines
            });
            i += chosen;
        }

        return sorties;
    }

    public static List<RouteOverLimitRoundTrip> GetOverLimitRoundTrips(
        IReadOnlyDictionary<string, RouteEndpoint[]> grouped,
        IReadOnlyList<string> lines,
        int startEp,
        double maxLenM,
        string mode,
        double centralMeridian)
    {
        var result = new List<RouteOverLimitRoundTrip>();
        for (int i = 0; i + 1 < lines.Count; i += 2)
        {
            var pair = new[] { lines[i], lines[i + 1] };
            var route = BuildSnakeRoute(grouped, pair, startEp);
            double len = RouteCoordinateMath.RouteLength(route, mode, centralMeridian);
            if (len > maxLenM)
                result.Add(new RouteOverLimitRoundTrip(i / 2 + 1, pair[0], pair[1], len));
        }
        return result;
    }

    public static void RebuildSortie(
        RouteSortie sortie,
        IReadOnlyDictionary<string, RouteEndpoint[]> grouped,
        int activeStartEp)
    {
        if (sortie.SourceLines.Count == 0)
            throw new InvalidOperationException("当前架次曾手工编辑航点，请先重新自动生成后再调边界。");
        sortie.Points = BuildSnakeRoute(grouped, sortie.SourceLines, activeStartEp);
    }

    public static void MoveLastTwoToNext(
        IList<RouteSortie> sorties,
        int index,
        IReadOnlyDictionary<string, RouteEndpoint[]> grouped,
        int activeStartEp)
    {
        if (index < 0 || index >= sorties.Count - 1)
            throw new InvalidOperationException("请选择一个非最后架次。");

        RouteSortie a = sorties[index];
        RouteSortie b = sorties[index + 1];
        if (a.SourceLines.Count == 0 || b.SourceLines.Count == 0)
            throw new InvalidOperationException("当前架次曾手动编辑航点，请先重新自动生成后再调边界。");
        if (a.SourceLines.Count < 4)
            throw new InvalidOperationException("当前架次至少需要保留 2 条测线。");

        var moved = a.SourceLines.TakeLast(2).ToList();
        a.SourceLines = a.SourceLines.Take(a.SourceLines.Count - 2).ToList();
        b.SourceLines = moved.Concat(b.SourceLines).ToList();
        RebuildSortie(a, grouped, activeStartEp);
        RebuildSortie(b, grouped, activeStartEp);
    }

    public static void MoveFirstTwoFromNext(
        IList<RouteSortie> sorties,
        int index,
        IReadOnlyDictionary<string, RouteEndpoint[]> grouped,
        int activeStartEp)
    {
        if (index < 0 || index >= sorties.Count - 1)
            throw new InvalidOperationException("请选择一个非最后架次。");

        RouteSortie a = sorties[index];
        RouteSortie b = sorties[index + 1];
        if (a.SourceLines.Count == 0 || b.SourceLines.Count == 0)
            throw new InvalidOperationException("当前架次曾手工编辑航点，请先重新自动生成后再调边界。");

        int minKeep = index + 1 == sorties.Count - 1 ? 1 : 2;
        if (b.SourceLines.Count - 2 < minKeep)
            throw new InvalidOperationException($"下一架次至少需要保留 {minKeep} 条测线。");

        var moved = b.SourceLines.Take(2).ToList();
        b.SourceLines = b.SourceLines.Skip(2).ToList();
        a.SourceLines = a.SourceLines.Concat(moved).ToList();
        RebuildSortie(a, grouped, activeStartEp);
        RebuildSortie(b, grouped, activeStartEp);
    }

    public static void ReverseSortie(RouteSortie sortie)
    {
        // Keep the sortie's survey-line order and connectors intact. Changing
        // the connection direction means swapping each line's two endpoints,
        // rather than reversing the entire waypoint sequence.
        for (int i = 0; i + 1 < sortie.Points.Count; i += 2)
            (sortie.Points[i], sortie.Points[i + 1]) =
                (sortie.Points[i + 1], sortie.Points[i]);
        sortie.SourceLines = [];
    }

    public static RouteGeometryDiagnostic GetLineGeometryDiagnostic(
        RouteLineInference info,
        string mode)
    {
        if (info.OrderedLines.Count == 0)
            return new("未知", "未知", double.NaN, 0);

        double sumC = 0;
        double sumS = 0;
        var lengths = new List<double>();
        var centers = new List<(double E, double N)>();

        foreach (string line in info.OrderedLines)
        {
            RouteEndpoint[] pair = info.Grouped[line];
            if (pair.Length < 2) continue;

            double e0, n0, e1, n1;
            if (mode == "projected")
            {
                var a = RouteCoordinateMath.ResolveProjectedEN(pair[0].X, pair[0].Y);
                var b = RouteCoordinateMath.ResolveProjectedEN(pair[1].X, pair[1].Y);
                (e0, n0, e1, n1) = (a.E, a.N, b.E, b.N);
            }
            else
            {
                double latm = ((pair[0].Y + pair[1].Y) / 2.0) * Math.PI / 180.0;
                e0 = pair[0].X * 111320.0 * Math.Cos(latm);
                e1 = pair[1].X * 111320.0 * Math.Cos(latm);
                n0 = pair[0].Y * 111320.0;
                n1 = pair[1].Y * 111320.0;
            }

            double dE = e1 - e0;
            double dN = n1 - n0;
            double len = Math.Sqrt(dE * dE + dN * dN);
            if (len <= 1e-6) continue;

            double az = Math.Atan2(dE, dN);
            sumC += Math.Cos(2.0 * az);
            sumS += Math.Sin(2.0 * az);
            lengths.Add(len);
            centers.Add(((e0 + e1) / 2.0, (n0 + n1) / 2.0));
        }

        if (lengths.Count == 0)
            return new("未知", "未知", double.NaN, 0);

        double azm = 0.5 * Math.Atan2(sumS, sumC) * 180.0 / Math.PI;
        if (azm < 0) azm += 180.0;
        string lineDir = azm <= 45.0 || azm >= 135.0 ? "南北向" : "东西向";

        double spanE = centers.Max(c => c.E) - centers.Min(c => c.E);
        double spanN = centers.Max(c => c.N) - centers.Min(c => c.N);
        string progressDir = spanN >= spanE ? "南北向" : "东西向";

        return new(
            lineDir,
            progressDir,
            azm,
            RouteCoordinateMath.Median(lengths) / 1000.0);
    }

    private RouteLineInference InferUsingLineNumbers(
        IReadOnlyList<RawRoutePoint> points,
        int?[] parsed,
        int parsedCount)
    {
        if (parsedCount != points.Count)
            throw new InvalidDataException("检测到 L 线号，但部分点名无法解析 L 后的有符号整数。请检查点名。");

        var byLine = new Dictionary<int, List<int>>();
        for (int i = 0; i < points.Count; i++)
        {
            int line = parsed[i]!.Value;
            if (!byLine.TryGetValue(line, out var list))
                byLine[line] = list = [];
            list.Add(i);
        }

        int[] lineNumbers = byLine.Keys.ToArray();
        double tol = GetSpanAndTolerance(points);
        var pairs = new Dictionary<int, RawRoutePoint[]>();
        int merged = 0;

        foreach (int line in lineNumbers)
        {
            int[] ids = byLine[line].ToArray();
            var h0 = new List<int>();
            var h1 = new List<int>();
            var un = new List<int>();
            foreach (int idx in ids)
            {
                int? hint = GetEndpointHint(points[idx].Label);
                if (hint == 0) h0.Add(idx);
                else if (hint == 1) h1.Add(idx);
                else un.Add(idx);
            }

            RawRoutePoint r0;
            RawRoutePoint r1;
            if (h0.Count > 0 && h1.Count > 0 && un.Count == 0)
            {
                var c0 = GetClusters(points, h0, tol);
                var c1 = GetClusters(points, h1, tol);
                if (c0.Count != 1 || c1.Count != 1)
                    throw new InvalidDataException($"L{line} 的同一端号出现多个不同空间位置，请检查重复记录。");
                r0 = GetRepresentative(points, c0[0]);
                r1 = GetRepresentative(points, c1[0]);
                merged += h0.Count - 1 + h1.Count - 1;
            }
            else
            {
                var clusters = GetClusters(points, ids, tol);
                if (clusters.Count != 2)
                    throw new InvalidDataException(
                        $"L{line} 的 {ids.Length} 条记录只能归并为 {clusters.Count} 个空间点，应为 2 个。");
                r0 = GetRepresentative(points, clusters[0]);
                r1 = GetRepresentative(points, clusters[1]);
                merged += ids.Length - 2;
            }

            double dx = r0.X - r1.X;
            double dy = r0.Y - r1.Y;
            if (Math.Sqrt(dx * dx + dy * dy) <= tol)
                throw new InvalidDataException($"L{line} 的两个端点坐标重合。");
            pairs[line] = [r0, r1];
        }

        bool isGeo = RouteCoordinateMath.IsGeographic(points);
        var physicalPairs = new Dictionary<int, RawRoutePoint[]>();
        foreach (int line in lineNumbers)
        {
            var pp = new RawRoutePoint[2];
            for (int i = 0; i < 2; i++)
            {
                RawRoutePoint rp = pairs[line][i];
                if (isGeo)
                    pp[i] = new RawRoutePoint(rp.X, rp.Y, rp.Label, rp.Row);
                else
                {
                    var en = RouteCoordinateMath.ResolveProjectedEN(rp.X, rp.Y);
                    pp[i] = new RawRoutePoint(en.E, en.N, rp.Label, rp.Row);
                }
            }
            physicalPairs[line] = pp;
        }

        var along = GetAlongVectorFromPairs(physicalPairs, lineNumbers);
        double crossX = -along.Y;
        double crossY = along.X;
        if (crossX < -1e-12 || (Math.Abs(crossX) <= 1e-12 && crossY < 0))
        {
            crossX = -crossX;
            crossY = -crossY;
        }

        var orderedNumbers = lineNumbers
            .Select(line =>
            {
                RawRoutePoint[] pair = physicalPairs[line];
                double midE = (pair[0].X + pair[1].X) / 2.0;
                double midN = (pair[0].Y + pair[1].Y) / 2.0;
                int firstRow = Math.Min(pairs[line][0].Row, pairs[line][1].Row);
                return new
                {
                    LineNumber = line,
                    Cross = midE * crossX + midN * crossY,
                    MidE = midE,
                    MidN = midN,
                    FirstRow = firstRow
                };
            })
            .OrderBy(x => x.Cross)
            .ThenBy(x => x.MidE)
            .ThenBy(x => x.MidN)
            .ThenBy(x => x.FirstRow)
            .Select(x => x.LineNumber)
            .ToArray();

        var grouped = new Dictionary<string, RouteEndpoint[]>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<string>();
        var endpoints = new List<RouteEndpoint>();
        int seq = 1;

        foreach (int line in orderedNumbers)
        {
            RawRoutePoint[] rawPair = pairs[line];
            RawRoutePoint[] phys = physicalPairs[line];
            double a0 = phys[0].X * along.X + phys[0].Y * along.Y;
            double a1 = phys[1].X * along.X + phys[1].Y * along.Y;
            RawRoutePoint lo = a0 <= a1 ? rawPair[0] : rawPair[1];
            RawRoutePoint hi = a0 <= a1 ? rawPair[1] : rawPair[0];

            string lineId = $"L{line}";
            var p0 = new RouteEndpoint
            {
                Line = lineId, Endpoint = 0, X = lo.X, Y = lo.Y,
                Label = lo.Label, Pid = $"P{seq:D3}-0"
            };
            var p1 = new RouteEndpoint
            {
                Line = lineId, Endpoint = 1, X = hi.X, Y = hi.Y,
                Label = hi.Label, Pid = $"P{seq:D3}-1"
            };
            grouped[lineId] = [p0, p1];
            ordered.Add(lineId);
            endpoints.AddRange([p0, p1]);
            seq++;
        }

        return new RouteLineInference
        {
            Grouped = grouped,
            OrderedLines = ordered,
            Endpoints = endpoints,
            MatchMode = "L",
            Merged = merged
        };
    }

    private RouteLineInference InferUsingGeometry(IReadOnlyList<RawRoutePoint> points)
    {
        if (points.Count % 2 != 0)
            throw new InvalidDataException($"未检测到 L 线号，且端点数量为奇数 {points.Count}，无法 XY 配对。");

        double bestDeg = 0;
        double bestScore = double.PositiveInfinity;
        for (int i = 0; i < 360; i++)
        {
            double deg = i * 0.5;
            double score = GetPairScore(points, deg * Math.PI / 180.0);
            if (score < bestScore)
            {
                bestScore = score;
                bestDeg = deg;
            }
        }

        double fineCenter = bestDeg;
        bestScore = double.PositiveInfinity;
        for (int i = -60; i <= 60; i++)
        {
            double deg = fineCenter + i * 0.01;
            while (deg < 0) deg += 180;
            while (deg >= 180) deg -= 180;
            double score = GetPairScore(points, deg * Math.PI / 180.0);
            if (score < bestScore)
            {
                bestScore = score;
                bestDeg = deg;
            }
        }

        double theta = bestDeg * Math.PI / 180.0;
        double ax = Math.Cos(theta);
        double ay = Math.Sin(theta);
        if (Math.Abs(ay) >= Math.Abs(ax))
        {
            if (ay < 0) { ax = -ax; ay = -ay; }
        }
        else if (ax < 0)
        {
            ax = -ax; ay = -ay;
        }

        double crossX = -ay;
        double crossY = ax;
        if (crossX < 0)
        {
            crossX = -crossX;
            crossY = -crossY;
        }

        var items = points
            .Select((p, i) => new
            {
                I = i,
                Cross = p.X * crossX + p.Y * crossY,
                Along = p.X * ax + p.Y * ay
            })
            .OrderBy(x => x.Cross)
            .ToArray();

        var rawPairs = new List<(double C, int Lo, int Hi)>();
        for (int k = 0; k < items.Length; k += 2)
        {
            var a = items[k];
            var b = items[k + 1];
            int lo = a.Along <= b.Along ? a.I : b.I;
            int hi = a.Along <= b.Along ? b.I : a.I;
            rawPairs.Add(((a.Cross + b.Cross) / 2.0, lo, hi));
        }
        rawPairs = rawPairs.OrderBy(x => x.C).ToList();

        var grouped = new Dictionary<string, RouteEndpoint[]>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<string>();
        var endpoints = new List<RouteEndpoint>();
        int seq = 1;

        foreach (var rp in rawPairs)
        {
            string lineId = $"G{seq:D3}";
            RawRoutePoint a = points[rp.Lo];
            RawRoutePoint b = points[rp.Hi];
            var p0 = new RouteEndpoint
            {
                Line = lineId, Endpoint = 0, X = a.X, Y = a.Y,
                Label = a.Label, Pid = $"P{seq:D3}-0"
            };
            var p1 = new RouteEndpoint
            {
                Line = lineId, Endpoint = 1, X = b.X, Y = b.Y,
                Label = b.Label, Pid = $"P{seq:D3}-1"
            };
            grouped[lineId] = [p0, p1];
            ordered.Add(lineId);
            endpoints.AddRange([p0, p1]);
            seq++;
        }

        return new RouteLineInference
        {
            Grouped = grouped,
            OrderedLines = ordered,
            Endpoints = endpoints,
            MatchMode = "XY",
            Merged = 0
        };
    }

    private static RouteEndpoint ResolveStartPoint(
        RouteLineInference inference,
        string selectedStartPid,
        RouteStartDirection direction,
        RoutePartitionStartSide partitionStartSide,
        string mode)
    {
        if (!string.IsNullOrWhiteSpace(selectedStartPid))
        {
            RouteEndpoint? match = inference.Endpoints.FirstOrDefault(
                p => p.Pid.Equals(selectedStartPid.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match is null)
                throw new InvalidOperationException($"指定起点 PID 不存在：{selectedStartPid}");
            return match;
        }

        if (partitionStartSide != RoutePartitionStartSide.Automatic)
        {
            return GetTraversalStartPointFromSide(
                inference.Grouped,
                inference.OrderedLines,
                partitionStartSide,
                direction,
                mode);
        }

        return GetStartPointFromCorner(inference.Grouped, inference.OrderedLines, direction, mode);
    }

    private static int? GetLineNumber(string? label)
    {
        Match m = LineNumberRegex.Match(label ?? "");
        return m.Success && int.TryParse(m.Groups[1].Value, out int line) ? line : null;
    }

    private static int? GetEndpointHint(string? label)
    {
        Match m = EndpointHintRegex.Match(label ?? "");
        return m.Success && int.TryParse(m.Groups[1].Value, out int endpoint) ? endpoint : null;
    }

    private static double GetSpanAndTolerance(IReadOnlyList<RawRoutePoint> points)
    {
        double minX = points.Min(p => p.X);
        double maxX = points.Max(p => p.X);
        double minY = points.Min(p => p.Y);
        double maxY = points.Max(p => p.Y);
        double span = Math.Sqrt((maxX - minX) * (maxX - minX) + (maxY - minY) * (maxY - minY));
        double floor = RouteCoordinateMath.IsGeographic(points) ? 1e-8 : 1e-3;
        return Math.Max(span * 1e-6, floor);
    }

    private static List<List<int>> GetClusters(
        IReadOnlyList<RawRoutePoint> points,
        IEnumerable<int> indices,
        double tolerance)
    {
        var clusters = new List<List<int>>();
        foreach (int idx in indices)
        {
            RawRoutePoint p = points[idx];
            bool matched = false;
            for (int ci = 0; ci < clusters.Count; ci++)
            {
                List<int> cluster = clusters[ci];
                double cx = cluster.Average(j => points[j].X);
                double cy = cluster.Average(j => points[j].Y);
                double dx = p.X - cx;
                double dy = p.Y - cy;
                if (Math.Sqrt(dx * dx + dy * dy) <= tolerance)
                {
                    cluster.Add(idx);
                    matched = true;
                    break;
                }
            }
            if (!matched)
                clusters.Add([idx]);
        }
        return clusters;
    }

    private static RawRoutePoint GetRepresentative(
        IReadOnlyList<RawRoutePoint> points,
        IReadOnlyList<int> indices)
    {
        double sx = 0, sy = 0;
        string label = "";
        int minRow = int.MaxValue;
        foreach (int idx in indices)
        {
            RawRoutePoint p = points[idx];
            sx += p.X;
            sy += p.Y;
            if (label.Length == 0 && p.Label.Trim().Length > 0)
                label = p.Label.Trim();
            minRow = Math.Min(minRow, p.Row);
        }
        return new RawRoutePoint(sx / indices.Count, sy / indices.Count, label, minRow);
    }

    private static double GetPairScore(IReadOnlyList<RawRoutePoint> points, double theta)
    {
        double nx = -Math.Sin(theta);
        double ny = Math.Cos(theta);
        double[] vals = points.Select(p => p.X * nx + p.Y * ny).OrderBy(x => x).ToArray();

        var pairDiff = new List<double>();
        var centers = new List<double>();
        for (int i = 0; i < vals.Length; i += 2)
        {
            pairDiff.Add(Math.Abs(vals[i + 1] - vals[i]));
            centers.Add((vals[i + 1] + vals[i]) / 2.0);
        }

        var gaps = new List<double>();
        for (int i = 1; i < centers.Count; i++)
        {
            double gap = Math.Abs(centers[i] - centers[i - 1]);
            if (gap > 1e-12) gaps.Add(gap);
        }

        double typical = gaps.Count > 0
            ? RouteCoordinateMath.Median(gaps)
            : Math.Max(Math.Abs(vals[^1] - vals[0]), 1.0);
        return pairDiff.Average() / (typical + 1e-12);
    }

    private static (double X, double Y) GetAlongVectorFromPairs(
        IReadOnlyDictionary<int, RawRoutePoint[]> pairs,
        IEnumerable<int> lineNumbers)
    {
        double c2 = 0, s2 = 0;
        int used = 0;

        foreach (int line in lineNumbers)
        {
            RawRoutePoint[] pair = pairs[line];
            double dx = pair[1].X - pair[0].X;
            double dy = pair[1].Y - pair[0].Y;
            if (Math.Sqrt(dx * dx + dy * dy) <= 1e-12) continue;
            double a = Math.Atan2(dy, dx);
            c2 += Math.Cos(2 * a);
            s2 += Math.Sin(2 * a);
            used++;
        }

        if (used == 0)
            throw new InvalidDataException("同一测线的两个端点坐标重合，无法识别测线方向。");

        double theta = 0.5 * Math.Atan2(s2, c2);
        double ax = Math.Cos(theta);
        double ay = Math.Sin(theta);
        double norm = Math.Sqrt(ax * ax + ay * ay);
        ax /= norm;
        ay /= norm;

        if (Math.Abs(ay) >= Math.Abs(ax))
        {
            if (ay < 0) { ax = -ax; ay = -ay; }
        }
        else if (ax < 0)
        {
            ax = -ax; ay = -ay;
        }
        return (ax, ay);
    }
}
