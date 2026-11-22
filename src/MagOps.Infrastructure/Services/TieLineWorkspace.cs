using System.Security.Cryptography;
using System.Text.Json;
using MagOps.Core.Coordinates;
using MagOps.Core.Routes;
using MagOps.Core.SurveyPlanning;
using P = MagOps.Core.SurveyPlanning.SurveyBoundaryPoint;

namespace MagOps.Infrastructure.Services;

public sealed class TieLineWorkspace
{
    public SurveyRegionGeometry Region { get; }
    public MainSurveySegment[] Mains { get; }
    public string Version { get; }
    public double Bearing { get; }
    public string BoundaryPath { get; }
    public double Meridian { get; }
    public string Datum { get; }
    public double MaxSortieM { get; }
    public int PendingPartitions { get; }
    public TieLineWorkspace(string boundaryPath, IEnumerable<RoutePlanResult> plans, string datum, int pendingPartitions = 0, double? projectMaxM = null, double? boundaryMeridian = null)
    {
        var all = plans.Distinct().ToArray();
        Meridian = all.Length > 0 ? all.Select(p => p.CentralMeridian).Distinct().Single()
            : boundaryMeridian ?? throw new InvalidDataException("请先确认工区中央经线。");
        Datum = datum;
        MaxSortieM = projectMaxM ?? all.Min(p => p.Options.MaxSortieKm) * 1000;
        if (!double.IsFinite(MaxSortieM) || MaxSortieM <= 0) throw new InvalidDataException("工程单架次最长航程必须大于零。");
        PendingPartitions = pendingPartitions;
        BoundaryPath = Path.GetFullPath(boundaryPath);
        Region = Path.GetExtension(boundaryPath).Equals(".ply", StringComparison.OrdinalIgnoreCase)
            ? SurveyRegionGeometry.FromSinglePolygon(new PlySurveyLineService().ReadBoundary(boundaryPath).Select(p => EN(p.X, p.Y)).ToArray())
            : new KmlSurveySubdivisionService().ReadProjectedRegion(boundaryPath, Meridian, datum);
        var mains = new List<MainSurveySegment>();
        foreach (var plan in all)
        {
            RouteIdentityService.EnsureReferences(plan.Sorties);
            P Point(RouteEndpoint p)
            {
                if (plan.Mode == "projected") return EN(p.X, p.Y);
                var (e, n) = CoordinateProjectionService.Forward(p.X, p.Y, new(datum, Meridian));
                return new(e, n);
            }
            foreach (var sortie in plan.Sorties)
                for (int i = 0; i < sortie.SourceLines.Count; i++)
                {
                    var a = sortie.Points[2 * i]; var b = sortie.Points[2 * i + 1];
                    var fp = RouteIdentityService.Fingerprint(a, b);
                    var id = sortie.TaskReferences.Single(t => t.GeometryFingerprint == fp && (t.LineName == sortie.SourceLines[i] || t.ExecutionSegmentId == sortie.SourceLines[i]));
                    if (id.RepeatIndex > 0 || id.SurveyPurpose is "CHECK" or "REPEAT" or "QUALITY_REPEAT") continue;
                    string mother = string.IsNullOrEmpty(id.MotherLineStableId) ? id.SurveyLineStableId : id.MotherLineStableId;
                    mains.Add(new(mother, id.LineName, id.SurveyTaskStableId, new(Point(a), Point(b))));
                }
        }
        Mains = mains.Distinct().OrderBy(m => m.TaskId, StringComparer.Ordinal).ThenBy(m => m.Geometry.Start.X).ToArray();
        var longest = Mains.MaxBy(m => m.Geometry.Length)?.Geometry;
        Bearing = longest is null ? 0 : (Math.Atan2(longest.End.X - longest.Start.X, longest.End.Y - longest.Start.Y) * 180 / Math.PI + 270) % 180;
        Version = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { Region.Polygons, Mains, Meridian, Datum })));
    }

    public RouteSortie ToWorkRoute(TieSortie flight)
    {
        var route = new RouteSortie { Name = flight.Name };
        var projection = new ProjectionParameters(Datum, Meridian);
        P? previous = null;
        foreach (var leg in flight.Legs)
        {
            foreach (var point in new[] { leg.Start, leg.End })
            {
                if (point == previous) continue;
                var ll = CoordinateProjectionService.Inverse(point.X, point.Y, projection);
                route.Points.Add(new RouteEndpoint { X = ll.Longitude, Y = ll.Latitude });
                previous = point;
            }
            route.TaskReferences.Add(new RouteTaskReference { LineName = leg.MotherName,
                SurveyLineStableId = leg.MotherId, SurveyPurpose = "CHECK" });
        }
        return route;
    }

    public (double WorkM, double FlightM) MeasureWorkFlight(TieSortie flight)
    {
        var route = ToWorkRoute(flight);
        double work = RouteCoordinateMath.RouteLength(route.Points, "geographic", Meridian);
        var home = CoordinateProjectionService.Inverse(flight.LaunchRecovery.X, flight.LaunchRecovery.Y,
            new ProjectionParameters(Datum, Meridian));
        var first = route.Points[0];
        var last = route.Points[^1];
        return (work, work + RouteCoordinateMath.Haversine(home.Longitude, home.Latitude, first.X, first.Y)
            + RouteCoordinateMath.Haversine(last.X, last.Y, home.Longitude, home.Latitude));
    }

    private (double X, double Y, double Min, double Max) LateralRange(double bearing)
    {
        double radians = bearing * Math.PI / 180, x = Math.Cos(radians), y = -Math.Sin(radians);
        var offsets = Region.Rings().SelectMany(r => r).Select(p => p.X * x + p.Y * y).ToArray();
        return (x, y, offsets.Min(), offsets.Max());
    }

    public int SuggestedCount(double bearing, double interval)
    {
        if (!double.IsFinite(interval) || interval <= 0) throw new ArgumentException("切割线距必须大于零。");
        var axis = LateralRange(bearing);
        int count = (int)Math.Clamp(Math.Floor((axis.Max - axis.Min) / interval), 1, 1000);
        return count < 2 ? (axis.Max - axis.Min > interval ? 2 : 1) : count - count % 2;
    }

    public List<RouteCheckLine> ParallelLayout(int count, double interval, double bearing, double mainSpacing,
        double before, double after, string standard, string note, TieFlightBudget budget, IEnumerable<RouteCheckLine> retained,
        double lateralShiftM = 0)
    {
        if (count is < 1 or > 1000 || !double.IsFinite(interval) || interval <= 0)
            throw new ArgumentException("请输入 1～1000 条切割线和正的切割线距。");
        var axis = LateralRange(bearing);
        double span = (count - 1) * interval;
        if (span >= axis.Max - axis.Min) throw new InvalidDataException("按当前数量与线距布设，最外侧切割线将越出工区。请减少数量或缩小间距。");
        double margin = (axis.Max - axis.Min - span) / 2;
        if (!double.IsFinite(lateralShiftM) || Math.Abs(lateralShiftM) >= margin)
            throw new ArgumentException($"切割线横移量绝对值须小于 {margin:0.###} m，以使两侧母线贯穿工区。");
        double start = (axis.Min + axis.Max - span) / 2 + lateralShiftM;
        var anchor = Region.Polygons[0].OuterRing[0];
        double origin = anchor.X * axis.X + anchor.Y * axis.Y;
        var retainedLines = retained.ToArray();
        var names = retainedLines.Select(l => l.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = new List<RouteCheckLine>();
        string group = Guid.NewGuid().ToString("N");
        int number = 1;
        for (int i = 0; i < count; i++)
        {
            while (names.Contains($"Q{number:00}")) number++;
            double offset = start + i * interval - origin;
            var through = new P(anchor.X + axis.X * offset, anchor.Y + axis.Y * offset);
            var line = Draw($"Q{number++:00}", through, bearing, mainSpacing, before, after);
            if (Mains.Length > 0 && line.Design!.Crossings.Length == 0)
                throw new InvalidDataException($"第 {i + 1} 条切割线没有交会主测线，请调整数量、方向或间距。");
            if (retainedLines.Any(l => l.Design?.SameAlignment(line.Design!) == true))
                throw new InvalidDataException("与保留的切割线重合，请调整布设参数，或选择重排现有绘制线。");
            added.Add(line with
            {
                Design = line.Design! with
                {
                    Standard = standard,
                    DesignNote = note,
                    FlightBudget = budget,
                    Layout = new(group, i + 1, count, interval) { LateralShiftM = lateralShiftM }
                }
            });
        }
        return added;
    }

    // Retain order-based pair membership for older batches with four or more lines.
    public RouteCheckLine[] PairMembers(IReadOnlyList<RouteCheckLine> lines, RouteCheckLine selected)
    {
        if (selected.Design?.Version != Version || selected.Design.Layout is not { } layout) return [];
        var pair = lines.Where(l => l.Design?.Version == Version && l.Design.Layout is { } other &&
            other.BatchId == layout.BatchId && (other.Order - 1) / 2 == (layout.Order - 1) / 2)
            .OrderBy(l => l.Design!.Layout!.Order).ToArray();
        if (pair.Length != 2) return [];
        // Older projects allow a single member to be rotated. Preserve individually edited geometry.
        var first = pair[0].Design!; var second = pair[1].Design!;
        var a = first.Approach.End; var b = first.Departure.Start;
        var c = second.Approach.End; var d = second.Departure.Start;
        double cross = (b.X-a.X)*(d.Y-c.Y) - (b.Y-a.Y)*(d.X-c.X);
        return Math.Abs(cross) <= 1e-7 * Math.Sqrt((Math.Pow(b.X-a.X,2)+Math.Pow(b.Y-a.Y,2)) *
            (Math.Pow(d.X-c.X,2)+Math.Pow(d.Y-c.Y,2))) ? pair : [];
    }

    public List<RouteCheckLine> CreatePairs(int groups, double interval, double bearing, double mainSpacing,
        double before, double after, string standard, string note, TieFlightBudget budget,
        IReadOnlyList<RouteCheckLine> retained, P? firstPoint = null)
    {
        if (groups is < 1 or > 500) throw new ArgumentException("请输入 1～500 组，每组两条。");
        var axis = LateralRange(bearing);
        int count = groups * 2;
        double centeredFirst = (axis.Min + axis.Max - (count - 1) * interval) / 2;
        double shift = firstPoint is { } p ? p.X * axis.X + p.Y * axis.Y - centeredFirst : 0;
        var added = ParallelLayout(count, interval, bearing, mainSpacing, before, after, standard, note, budget, retained, shift);
        for (int n = 0; n < added.Count; n += 2)
        {
            string pairId = Guid.NewGuid().ToString("N");
            for (int k = 0; k < 2; k++)
            {
                var line = added[n + k];
                added[n + k] = line with { Design = line.Design! with { Layout = new(pairId, k + 1, 2, interval) { LateralShiftM = shift } } };
            }
        }
        return added;
    }

    public List<RouteCheckLine> PositionPair(IReadOnlyList<RouteCheckLine> lines, RouteCheckLine anchor,
        P through, double signedGap)
    {
        var pair = PairMembers(lines, anchor);
        if (pair.Length != 2 || !double.IsFinite(signedGap) || Math.Abs(signedGap) < .01)
            throw new InvalidDataException("需选择完整线组，组内线距至少 0.01 m。");
        if (pair.Any(l => l.Design!.ExistingControlLineId != null))
            throw new InvalidDataException("采用正式测线几何的控制线不能平移。");
        var guide = anchor.Design!;
        var a = guide.Approach.End; var b = guide.Departure.Start;
        double bearing = Math.Atan2(b.X - a.X, b.Y - a.Y);
        double x = Math.Cos(bearing), y = -Math.Sin(bearing);
        var result = lines.ToList();
        foreach (var member in pair)
        {
            var old = member.Design!;
            double distance = member.Id == anchor.Id ? 0 : signedGap;
            var point = new P(through.X + distance * x, through.Y + distance * y);
            var oldStart = old.Approach.End; var oldEnd = old.Departure.Start;
            double memberBearing = Math.Atan2(oldEnd.X-oldStart.X, oldEnd.Y-oldStart.Y) * 180 / Math.PI;
            var moved = Draw(member.Name, point, memberBearing, old.Spacing,
                old.Approach.Length, old.Departure.Length, member.Id);
            if (Mains.Length > 0 && moved.Design!.Crossings.Length == 0)
                throw new InvalidDataException($"{member.Name} 没有交会正式测线，请调整位置。");
            if (lines.Except(pair).Any(l => l.Design?.SameAlignment(moved.Design!) == true))
                throw new InvalidDataException($"{member.Name} 与其他切割线重合。");
            double delta = (point.X - old.Approach.End.X) * x + (point.Y - old.Approach.End.Y) * y;
            var layout = old.Layout!;
            result[result.FindIndex(l => l.Id == member.Id)] = moved with { Design = moved.Design! with
            {
                Standard = old.Standard, DesignNote = old.DesignNote, CutFractions = old.CutFractions,
                FlightBudget = old.FlightBudget, Layout = layout with { IntervalM = Math.Abs(signedGap),
                    LateralShiftM = layout.LateralShiftM + delta, Edited = true }
            } };
        }
        return result;
    }

    public RouteCheckLine RegisterControl(string lineId, double spacing)
    {
        var segments = Mains.Where(m => m.LineId == lineId).ToArray();
        var axis = segments.MaxBy(m => m.Geometry.Length)?.Geometry ?? throw new InvalidDataException("未找到所选原测线。");
        double length = axis.Length;
        if (length <= 0) throw new InvalidDataException("所选原线无有效长度。");
        double dx = (axis.End.X - axis.Start.X) / length, dy = (axis.End.Y - axis.Start.Y) / length;
        double Along(P p) => (p.X - axis.Start.X) * dx + (p.Y - axis.Start.Y) * dy;
        var endpoints = segments.SelectMany(m => new[] { m.Geometry.Start, m.Geometry.End }).ToArray();
        if (endpoints.Any(p => Math.Abs((p.X - axis.Start.X) * dy - (p.Y - axis.Start.Y) * dx) > .01))
            throw new InvalidDataException("原线含不同方向的测段，请分别核对。");
        double angle = Math.Atan2(dx, dy) * 180 / Math.PI;
        var draft = Draw(segments[0].Label, axis.Start, angle, spacing, 0, 0);
        var guide = draft.Design!;
        var covered = segments.Select(m => (Lo: Math.Min(Along(m.Geometry.Start), Along(m.Geometry.End)), Hi: Math.Max(Along(m.Geometry.Start), Along(m.Geometry.End)))).OrderBy(r => r.Lo).ToArray();
        double before = Along(guide.Approach.End) - covered[0].Lo;
        double after = covered.Max(r => r.Hi) - Along(guide.Departure.Start);
        if (before < -.01 || after < -.01) throw new InvalidDataException("所选原线未到达工区两侧外边界。");
        var control = Draw(segments[0].Label, axis.Start, angle, spacing, Math.Max(0, before), Math.Max(0, after));
        var d = control.Design!;
        foreach (var observation in d.Inside.Prepend(d.Approach).Append(d.Departure))
        {
            double cursor = Along(observation.Start), end = Along(observation.End);
            foreach (var r in covered)
            {
                if (r.Lo > cursor + .01) break;
                cursor = Math.Max(cursor, r.Hi);
            }
            if (cursor < end - .01) throw new InvalidDataException("原线在设计观测范围内有断口，不能整线登记。");
        }
        return control with { Design = d with { ExistingControlLineId = lineId } };
    }

    private static P EN(double x, double y)
    {
        var (e, n, _) = RouteCoordinateMath.ResolveProjectedEN(x, y);
        if (e > 10_000_000) e %= 1_000_000;
        return new(e, n);
    }

    public RouteCheckLine Draw(string name, P through, double bearing, double spacing, double before, double after, string? id = null)
    {
        var design = TieLineDesign.Draw(Region, Mains, through, bearing, spacing, before, after, Version, Datum, Meridian, BoundaryPath);
        return new(id ?? "check-" + Guid.NewGuid().ToString("N"), RouteCheckLines.Cross, name.Trim(), "", "", "",
            new() { X = design.Approach.Start.X, Y = design.Approach.Start.Y }, new() { X = design.Departure.End.X, Y = design.Departure.End.Y })
        { CoordinateMode = "projected", CentralMeridian = Meridian, Design = design };
    }
}
