using System.Security.Cryptography;
using System.Text;
using P = MagOps.Core.SurveyPlanning.SurveyBoundaryPoint;
using S = MagOps.Core.SurveyPlanning.SurveyLineSegment;

namespace MagOps.Core.Routes;

public sealed record TieLayout(string BatchId, int Order, int Count, double IntervalM, bool Edited = false)
{
    public string? FirstMotherId { get; init; }
    public bool? FirstLegReverse { get; init; }
    public double LateralShiftM { get; init; }
}

public sealed record TieFlightBudget(double SpeedMps = 10, double AvailableMinutes = 0, double AccessAllowanceM = 0)
{
    public double Limit(double projectM)
    {
        if (!double.IsFinite(projectM) || projectM <= 0 || !double.IsFinite(SpeedMps) || SpeedMps <= 0 ||
            !double.IsFinite(AvailableMinutes) || AvailableMinutes < 0 || !double.IsFinite(AccessAllowanceM) || AccessAllowanceM < 0)
            throw new InvalidDataException("请核对最长航程、平均航速、可用航时和起降往返预留。");
        double limit = AvailableMinutes == 0 ? projectM : Math.Min(projectM, AvailableMinutes * 60 * SpeedMps);
        if (limit - AccessAllowanceM < RouteExecutionSegmentPlanner.MinimumSegmentLengthM)
            throw new InvalidDataException("扣除起降往返预留后，没有可用执行航程。");
        return limit;
    }
}

public sealed record TieFlightLeg(string MotherId, string MotherName, string SegmentId, int Sequence, int Pieces,
    double FromFraction, double ToFraction, P Start, P End, double InsideM, double ExtensionM, double FlightM)
{
    public double ObservationM => InsideM + ExtensionM;
}
public sealed record TieSortie(string Name, string Pair, TieFlightLeg[] Legs, double ConnectionM, double ReturnM,
    TieFlightBudget Budget, double ProjectMaxM, double LimitM)
{
    public P LaunchRecovery { get; init; } = Legs[0].Start;
    public string LaunchBasis { get; init; } = "执行起点";
    public double OutboundM { get; init; }
    public string Pairing { get; init; } = "";
    public double TotalM => OutboundM + Legs.Sum(l => l.FlightM) + ConnectionM + ReturnM + Budget.AccessAllowanceM;
    public double Minutes => TotalM / Budget.SpeedMps / 60;
}

public static class TieFlightPlanner
{
    public static List<TieSortie> Build(IEnumerable<RouteCheckLine> lines, string version, double projectM, double bearing)
    {
        var result = new List<TieSortie>();
        var groups = lines.Where(l => l.Design?.Version == version)
            .GroupBy(l => (l.Design!.Layout?.BatchId, Pair: l.Design.Layout is { } layout ? (layout.Order - 1) / 2 : 0, l.Design.FlightBudget));
        double angle = bearing * Math.PI / 180;
        foreach (var group in groups)
        {
            var sorted = group.OrderBy(l => l.Design!.Layout?.Order ?? 0)
                .ThenBy(l => l.Design!.Approach.End.X * Math.Cos(angle) - l.Design!.Approach.End.Y * Math.Sin(angle)).ToArray();
            for (int i = 0; i < sorted.Length;)
            {
                var first = sorted[i++];
                RouteCheckLine? second = i < sorted.Length && Parallel(first, sorted[i]) ? sorted[i++] : null;
                if (second != null && first.Design!.Layout?.FirstMotherId == second.Id) (first, second) = (second, first);
                bool aligned = second == null || Dot(first, second) > 0;
                void Single(RouteCheckLine m, string reason)
                {
                    double from = 0;
                    foreach (double to in m.Design!.CutFractions.Order().Append(1d))
                    {
                        Allocate(result, new(m, from, to), null, true, projectM, reason);
                        from = to;
                    }
                }
                if (second == null) { Single(first, "无相邻配线"); continue; }
                var axis = new S(first.Design!.Approach.Start, first.Design.Departure.End);
                double Station(P p) => ((p.X - axis.Start.X) * (axis.End.X - axis.Start.X) + (p.Y - axis.Start.Y) * (axis.End.Y - axis.Start.Y)) / axis.Length;
                double bStart = Station(second.Design!.Approach.Start), bEnd = Station(second.Design.Departure.End);
                double overlapLo = Math.Max(0, Math.Min(bStart, bEnd)), overlapHi = Math.Min(axis.Length, Math.Max(bStart, bEnd));
                if (overlapHi - overlapLo < RouteExecutionSegmentPlanner.MinimumSegmentLengthM)
                { Single(first, "无共同切分区间"); Single(second, "无共同切分区间"); continue; }
                double BF(double station) => Math.Clamp((station - bStart) / (bEnd - bStart), 0, 1);
                double AF(double station) => Math.Clamp(station / axis.Length, 0, 1);
                var stations = first.Design.CutFractions.Select(f => f * axis.Length)
                    .Concat(second.Design.CutFractions.Select(f => bStart + f * (bEnd - bStart))).Order().ToArray();
                // Keep the original ends paired; a manual cut is transferred along
                // the normal between lines, not by equal percentages of unequal lines.
                var boundaries = new List<(double A, double B)> { (0, aligned ? 0 : 1) };
                foreach (double station in stations)
                {
                    var next = (A: AF(station), B: BF(station));
                    if (Math.Abs(next.A - boundaries[^1].A) > 1e-12 || Math.Abs(next.B - boundaries[^1].B) > 1e-12)
                        boundaries.Add(next);
                }
                boundaries.Add((1, aligned ? 1 : 0));
                if (boundaries.Zip(boundaries.Skip(1)).Any(w => TooShort(w.Second.A - w.First.A, axis.Length) ||
                        TooShort(w.Second.B - w.First.B, Span(second))))
                { Single(first, "相邻切点不足最小段长"); Single(second, "相邻切点不足最小段长"); continue; }
                foreach (var (start, end) in boundaries.Zip(boundaries.Skip(1)))
                {
                    var a = new Window(first, start.A, end.A);
                    var b = new Window(second, Math.Min(start.B, end.B), Math.Max(start.B, end.B));
                    if (a.To - a.From < 1e-12) { if (b.To > b.From) Allocate(result, b, null, true, projectM, "对应线已到端点"); }
                    else if (b.To - b.From < 1e-12) Allocate(result, a, null, true, projectM, "对应线已到端点");
                    else Allocate(result, a, b, aligned, projectM, "");
                }

            }
        }
        var labels = result.SelectMany(s => s.Legs).GroupBy(l => l.MotherId)
            .SelectMany(g => g.OrderBy(l => Math.Min(l.FromFraction, l.ToFraction))
                .Select((leg, i) => leg with { Sequence = i + 1, Pieces = g.Count() })).ToDictionary(l => l.SegmentId);
        return result.Select(s => s with { Legs = s.Legs.Select(l => labels[l.SegmentId]).ToArray() }).ToList();
    }

    private static bool TooShort(double fraction, double length) => Math.Abs(fraction) > 1e-12 && Math.Abs(fraction) * length < RouteExecutionSegmentPlanner.MinimumSegmentLengthM;

    private sealed record Window(RouteCheckLine Mother, double From, double To)
    {
        public TieFlightLeg Slice(int index, int count, bool reverse) =>
            Leg(Mother, From + (To - From) * index / count, From + (To - From) * (index + 1) / count, reverse);
    }

    private static void Allocate(List<TieSortie> result, Window first, Window? second, bool aligned, double projectM, string singleReason)
    {
        var budget = first.Mother.Design!.FlightBudget;
        double limit = budget.Limit(projectM), available = limit - budget.AccessAllowanceM;
        string pair = second == null ? first.Mother.Name : first.Mother.Name + " / " + second.Mother.Name;
        double cm = first.Mother.CentralMeridian;
        TieSortie Make(TieFlightLeg[] legs, double connection)
        {
            var start = legs[0].Start;
            var end = legs[^1].End;
            bool cutSide = Interior(legs[0].FromFraction) || (legs.Length == 2 && Interior(legs[^1].ToFraction));
            var launchMother = legs[0].MotherId == first.Mother.Id ? first.Mother : second!.Mother;
            bool manualStart = AtManualCut(launchMother, legs[0].FromFraction);
            bool manualEnd = legs.Length == 2 && AtManualCut(second!.Mother, legs[^1].ToFraction);
            var home = cutSide && legs.Length == 2 ? new P((start.X + end.X) / 2, (start.Y + end.Y) / 2) : start;
            if (manualStart != manualEnd) home = manualStart ? start : end;
            return new("", pair, legs, connection, FlightDistance(end, home, cm), budget, projectM, limit)
            {
                LaunchRecovery = home,
                OutboundM = FlightDistance(home, start, cm),
                LaunchBasis = manualStart != manualEnd ? "指定母线切点" : cutSide ? (legs.Length == 2 ? "对应切点一侧中点" : "母线切点") : "执行起点",
                Pairing = legs.Length == 2 ? "对应段成对" : second == null ? singleReason : "配对航程超限"
            };
        }
        TieSortie? Prefer(TieSortie a, TieSortie b, bool? preferReverse = null)
        {
            if (a.TotalM > limit) return b.TotalM <= limit ? b : null;
            if (b.TotalM > limit) return a;
            int Rank(TieSortie s)
            {
                var leg = s.Legs[0];
                if (first.Mother.Design!.CutFractions.Any(f => Math.Abs(f - leg.FromFraction) < 1e-12) ||
                   s.Legs.Length == 2 && second!.Mother.Design!.CutFractions.Any(f => Math.Abs(f - s.Legs[^1].ToFraction) < 1e-12)) return 2;
                return s.LaunchBasis == "执行起点" ? 0 : 1;
            }
            int ar = Rank(a), br = Rank(b);
            if (ar != br) return ar > br ? a : b;
            if (preferReverse.HasValue && ar > 0) return preferReverse.Value ? b : a;
            return a.TotalM <= b.TotalM ? a : b;
        }
        void Add(TieSortie sortie) => result.Add(sortie with { Name = $"C{result.Count + 1:000}" });
        int Pieces(Window window)
        {
            var whole = window.Slice(0, 1, false);
            double length = whole.FlightM;
            if (length < RouteExecutionSegmentPlanner.MinimumSegmentLengthM)
                throw new InvalidDataException($"{window.Mother.Name} 的切点之间不足 1 m，请调整切点。");
            double n = Math.Ceiling((length + FlightDistance(whole.End, whole.Start, window.Mother.CentralMeridian)) / available);
            if (n > 4096) throw new InvalidDataException("单条切割线需要超过 4096 个执行段，请核对航程单位。");
            return Math.Max(1, (int)n);
        }
        int firstCount = Pieces(first);
        if (second != null)
        {
            var lineA = first.Slice(0, 1, false); var lineB = second.Slice(0, 1, !aligned);
            double dx = lineA.End.X - lineA.Start.X, dy = lineA.End.Y - lineA.Start.Y;
            double length = Math.Sqrt(dx * dx + dy * dy);
            double Pos(P pt) => ((pt.X - lineA.Start.X) * dx + (pt.Y - lineA.Start.Y) * dy) / length;
            double bs = Pos(lineB.Start), be = Pos(lineB.End);
            double lo = Math.Max(0, bs), hi = Math.Min(length, be);
            // Interior automatic cuts share a perpendicular station on both lines.
            double Fraction(Window w, int i, int n, bool onA)
            {
                double low = onA || aligned ? w.From : w.To, high = onA || aligned ? w.To : w.From;
                if (i == 0) return low;
                if (i == n) return high;
                double station = lo + (hi - lo) * i / n;
                double t = onA ? station / length : (station - bs) / (be - bs);
                return low + (high - low) * t;
            }
            List<TieSortie>? TryPair(int n)
            {
                if (n > 1 && hi <= lo) return null;
                var candidates = new List<TieSortie>();
                for (int i = 0; i < n; i++)
                {
                    double a0 = Fraction(first, i, n, true), a1 = Fraction(first, i + 1, n, true);
                    double b0 = Fraction(second, i, n, false), b1 = Fraction(second, i + 1, n, false);
                    var a = new Window(first.Mother, a0, a1); var b = new Window(second.Mother, Math.Min(b0, b1), Math.Max(b0, b1));
                    var x = a.Slice(0, 1, false); var y = b.Slice(0, 1, aligned);
                    var rx = a.Slice(0, 1, true); var ry = b.Slice(0, 1, !aligned);
                    var forward = Make([x, y], FlightDistance(x.End, y.Start, cm));
                    var reverse = Make([rx, ry], FlightDistance(rx.End, ry.Start, cm));
                    var chosen = first.Mother.Design!.Layout?.FirstLegReverse switch
                    {
                        true => reverse.TotalM <= limit ? reverse : null,
                        false => forward.TotalM <= limit ? forward : null,
                        null => Prefer(forward, reverse, n > 1 ? i % 2 == 0 && i + 1 < n : null)
                    };
                    if (chosen == null || chosen.Legs.Any(l => l.FlightM < RouteExecutionSegmentPlanner.MinimumSegmentLengthM)) return null;
                    candidates.Add(chosen);
                }
                return candidates;
            }
            // Bound by minimum legal segment length. Pairing may use more sorties than flying solo.
            int max = (int)Math.Clamp(Math.Floor(Math.Max(0, hi - lo) / RouteExecutionSegmentPlanner.MinimumSegmentLengthM), 1, 4096);
            int n = 1, lastFail = 0;
            List<TieSortie>? paired;
            while ((paired = TryPair(n)) == null && n < max) { lastFail = n; n = Math.Min(max, n * 2); }
            if (paired != null)
            {
                while (n - lastFail > 1)
                {
                    int mid = (lastFail + n) / 2; var attempt = TryPair(mid);
                    if (attempt == null) lastFail = mid; else { n = mid; paired = attempt; }
                }
                foreach (var sortie in paired) Add(sortie);
                return;
            }
            // Staggered outer ends can make just the head or tail infeasible. Pair the
            // common span before assigning the remaining observation to single sorties.
            double trim = RouteExecutionSegmentPlanner.MinimumSegmentLengthM * 1.01;
            if (hi - lo >= trim && (lo >= trim || length - hi >= trim || lo - bs >= trim || be - hi >= trim))
            {
                double a0 = first.From + (first.To - first.From) * lo / length, a1 = first.From + (first.To - first.From) * hi / length;
                double b0 = second.From + (second.To - second.From) * (aligned ? (lo - bs) / (be - bs) : (be - hi) / (be - bs));
                double b1 = second.From + (second.To - second.From) * (aligned ? (hi - bs) / (be - bs) : (be - lo) / (be - bs));
                Window Trim(Window w, double from, double to)
                {
                    double span = Span(w.Mother);
                    if ((from - w.From) * span < trim) from = w.From;
                    if ((w.To - to) * span < trim) to = w.To;
                    if (from > w.From) Allocate(result, new(w.Mother, w.From, from), null, true, projectM, "错端余段");
                    if (to < w.To) Allocate(result, new(w.Mother, to, w.To), null, true, projectM, "错端余段");
                    return new(w.Mother, from, to);
                }
                var ca = Trim(first, a0, a1); var cb = Trim(second, b0, b1);
                if (ca != first || cb != second) { Allocate(result, ca, cb, aligned, projectM, ""); return; }
            }

        }
        foreach (var window in second == null ? new[] { first } : new[] { first, second })
        {
            int n = Pieces(window);
            while (n <= 4096)
            {
                var pieces = Enumerable.Range(0, n).Select(i =>
                {
                    var a = Make([window.Slice(i, n, false)], 0); var b = Make([window.Slice(i, n, true)], 0);
                    bool manualA = window.Mother.Design!.CutFractions.Any(f => Math.Abs(f - a.Legs[0].FromFraction) < 1e-12);
                    bool manualB = window.Mother.Design.CutFractions.Any(f => Math.Abs(f - b.Legs[0].FromFraction) < 1e-12);
                    if (manualB != manualA && a.TotalM <= limit && b.TotalM <= limit) return manualB ? b : a;
                    return Prefer(a, b, n > 1 ? i % 2 == 0 && i + 1 < n : null) ?? a;
                }).ToArray();
                if (pieces.Any(s => s.Legs[0].FlightM < RouteExecutionSegmentPlanner.MinimumSegmentLengthM))
                    throw new InvalidDataException("可用航程不足以形成有效执行段。");
                if (pieces.All(s => s.TotalM <= limit))
                {
                    foreach (var piece in pieces) Add(piece);
                    break;
                }
                if (++n > 4096) throw new InvalidDataException("执行段数量过多，请核对航程单位。");
            }
        }
    }
    private static bool AtManualCut(RouteCheckLine line, double f) => line.Design!.CutFractions.Any(c => Math.Abs(c - f) < 1e-12);
    private static bool Interior(double f) => f > 1e-12 && f < 1 - 1e-12;
    private static double Span(RouteCheckLine line) => new S(line.Design!.Approach.Start, line.Design.Departure.End).Length;
    private static double Dot(RouteCheckLine a, RouteCheckLine b) =>
        (a.End.X - a.Start.X) * (b.End.X - b.Start.X) + (a.End.Y - a.Start.Y) * (b.End.Y - b.Start.Y);
    private static bool Parallel(RouteCheckLine a, RouteCheckLine b)
    {
        var da = a.Design!; var db = b.Design!;
        return Math.Abs(Dot(a, b)) >= Math.Cos(Math.PI / 180) *
            new S(da.Approach.Start, da.Departure.End).Length * new S(db.Approach.Start, db.Departure.End).Length;
    }
    private static double FlightDistance(P a, P b, double meridian) =>
        RouteCoordinateMath.RouteLength([new() { X = a.X, Y = a.Y }, new() { X = b.X, Y = b.Y }], "projected", meridian);

    private static TieFlightLeg Leg(RouteCheckLine line, double lo, double hi, bool reverse)
    {
        var d = line.Design!;
        P a = d.Approach.Start, b = d.Departure.End;
        double length = new S(a, b).Length;
        P At(double f) => new(a.X + (b.X - a.X) * f, a.Y + (b.Y - a.Y) * f);
        double Overlap(S s)
        {
            double start = new S(a, s.Start).Length;
            return Math.Max(0, Math.Min(length * hi, start + s.Length) - Math.Max(length * lo, start));
        }
        P start = At(reverse ? hi : lo), end = At(reverse ? lo : hi);
        string key = FormattableString.Invariant($"{line.Id}|{a.X:R}|{a.Y:R}|{b.X:R}|{b.Y:R}|{lo:R}|{hi:R}");
        string id = "check-segment-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..24];
        return new(line.Id, line.Name, id, 0, 0, reverse ? hi : lo, reverse ? lo : hi, start, end,
            d.Inside.Sum(Overlap), Overlap(d.Approach) + Overlap(d.Departure), FlightDistance(start, end, line.CentralMeridian));
    }
}
