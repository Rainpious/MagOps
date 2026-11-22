using MagOps.Core.SurveyPlanning;
using P = MagOps.Core.SurveyPlanning.SurveyBoundaryPoint;
using S = MagOps.Core.SurveyPlanning.SurveyLineSegment;

namespace MagOps.Core.Routes;

public sealed record MainSurveySegment(string LineId, string Label, string TaskId, S Geometry);
public sealed record TieCrossing(string LineId, string Label, string TaskId, P Point);
public sealed record TieLineDesign(string Version, string Datum, double Meridian, string BoundaryPath,
    double Spacing, S Approach, S Departure, S[] Inside, TieCrossing[] Crossings)
{
    public const string IndustryStandard = "DZ/T 0405—2022 §5.4.4";
    public const string RegionalStandard = "DB63/T 1933—2021 §5.4.4";
    public string Standard { get; init; } = IndustryStandard;
    public string DesignNote { get; init; } = "";
    public string? ExistingControlLineId { get; init; }
    public double[] CutFractions { get; init; } = [];
    public TieLayout? Layout { get; init; }
    public TieFlightBudget FlightBudget { get; init; } = new();
    public double InsideM => Inside.Sum(s => s.Length);
    public double ExtensionM => Approach.Length + Departure.Length;
    public double TotalM => InsideM + ExtensionM;
    public bool ApproachRecommended => Recommended(Approach.Length);
    public bool DepartureRecommended => Recommended(Departure.Length);
    public bool ExtensionsRecommended => ApproachRecommended && DepartureRecommended;
    public double InterruptedM => Math.Max(0, new S(Approach.End, Departure.Start).Length - InsideM);

    public bool SameAlignment(TieLineDesign other)
    {
        if (Version != other.Version) return false;
        var a = Approach.End;
        var b = Departure.Start;
        double length = new S(a, b).Length;
        double Offset(P p) => Math.Abs((p.X - a.X) * (b.Y - a.Y) - (p.Y - a.Y) * (b.X - a.X)) / length;
        return Offset(other.Approach.End) < .01 && Offset(other.Departure.Start) < .01;
    }
    private bool Recommended(double length) => length >= 3 * Spacing - 1e-6 && length <= 5 * Spacing + 1e-6;

    public static TieLineDesign Draw(SurveyRegionGeometry region, IReadOnlyList<MainSurveySegment> mains,
        P through, double bearing, double spacing, double before, double after,
        string version, string datum, double meridian, string boundaryPath)
    {
        if (!double.IsFinite(spacing) || spacing <= 0 || !double.IsFinite(before) || !double.IsFinite(after) ||
            before < 0 || after < 0 || !double.IsFinite(bearing) || !double.IsFinite(through.X) || !double.IsFinite(through.Y))
            throw new ArgumentException("请核对方位、坐标、主测线距与两端外延。");
        var angle = bearing * Math.PI / 180;
        double dx = Math.Sin(angle), dy = Math.Cos(angle);
        double Along(P p) => (p.X - through.X) * dx + (p.Y - through.Y) * dy;
        P At(double t) => new(through.X + t * dx, through.Y + t * dy);
        var limits = region.Rings().SelectMany(r => r).Select(Along).ToArray();
        double lo = limits.Min() - 1, hi = limits.Max() + 1;
        var cuts = SurveyRegionGeometryMath.SegmentBoundaryParameters(region, At(lo), At(hi))
            .Select(t => lo + t * (hi - lo)).ToArray();
        var inside = new List<S>();
        for (int i = 1; i < cuts.Length; i++)
        {
            double a = cuts[i - 1], b = cuts[i];
            if (b - a > 1e-6 && SurveyRegionGeometryMath.ContainsOrOnBoundary(region, At((a + b) / 2), includeBoundary: false))
                inside.Add(new(At(a), At(b)));
        }
        if (inside.Count == 0) throw new InvalidDataException("切割线没有穿过有效工区。");
        var hits = new List<TieCrossing>();
        foreach (var main in mains)
        {
            var a = main.Geometry.Start; var b = main.Geometry.End;
            double Side(P p) => dx * (p.Y - through.Y) - dy * (p.X - through.X);
            double sa = Side(a), sb = Side(b);
            if (Math.Abs(sa) < 1e-6 || Math.Abs(sb) < 1e-6 || Math.Sign(sa) == Math.Sign(sb)) continue;
            double t = sa / (sa - sb);
            var point = new P(a.X + t * (b.X - a.X), a.Y + t * (b.Y - a.Y));
            if (inside.Any(s => Along(point) >= Along(s.Start) - 1e-6 && Along(point) <= Along(s.End) + 1e-6))
                hits.Add(new(main.LineId, main.Label, main.TaskId, point));
        }
        var first = inside[0].Start; var last = inside[^1].End;
        return new(version, datum, meridian, boundaryPath, spacing, new(At(Along(first) - before), first),
            new(last, At(Along(last) + after)), inside.ToArray(), hits.ToArray());
    }

    public static Dictionary<string, int> Coverage(IEnumerable<MainSurveySegment> mains, IEnumerable<RouteCheckLine> lines, string version)
    {
        var current = lines.DistinctBy(l => l.Id).Where(l => l.Design?.Version == version).ToArray();
        var controls = current.Select(l => l.Design!.ExistingControlLineId).Where(id => id != null).ToHashSet();
        var counts = mains.Select(m => m.LineId).Distinct().Where(id => !controls.Contains(id)).ToDictionary(id => id, _ => 0);
        foreach (var line in current)
            if (line.Design is { } design && design.Version == version)
                foreach (var id in design.Crossings.Select(h => h.LineId).Distinct())
                    if (counts.ContainsKey(id)) counts[id]++;
        return counts;
    }
}
