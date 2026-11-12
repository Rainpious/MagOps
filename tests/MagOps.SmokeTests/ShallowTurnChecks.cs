using System.Collections;
using System.Reflection;
using MagOps.Core.Domain;
using MagOps.Infrastructure.Services;

namespace MagOps.SmokeTests;

internal static class ShallowTurnChecks
{
    internal static void Run()
    {
        const double metre = 1 / 111319.49;
        var start = new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.Zero);
        var refiner = typeof(TrackSegmentationService).Assembly.GetType("MagOps.Infrastructure.Services.TrackShallowTurnRefiner")!
            .GetMethod("Refine", BindingFlags.Static | BindingFlags.NonPublic)!;
        List<SurveyTrackPoint> Track(Func<double, double> y, bool reverse = false, bool coarse = false) => Enumerable.Range(0, 801)
            .Select(i => { double x = (reverse ? 800 - i : i) * .8;
                return new SurveyTrackPoint(coarse ? start.AddSeconds(i / 10) : start.AddMilliseconds(i * 100),
                    new(x * metre, y(x) * metre), "shallow.uav", i) { AltitudeM = 100 }; }).ToList();
        object[] Refine(List<SurveyTrackPoint> points) => ((IEnumerable)refiner.Invoke(null,
            [points, 0, points.Count - 1, points.Count - 1, new TrackRecognitionOptions(), new WholeLegMatchOptions(), 0, false])!).Cast<object>().ToArray();
        int Value(object span, string name) => (int)span.GetType().GetProperty(name)!.GetValue(span)!;
        void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
        double approachSlope = Math.Tan(8 * Math.PI / 180);
        foreach (bool reverse in new[] { false, true })
        foreach (bool coarse in new[] { false, true })
        {
            var points = Track(x => Math.Min(x, 160) * approachSlope, reverse, coarse);
            var spans = Refine(points);
            Check(spans.Length == 2, "shallow entry/exit separates in both directions and UTC resolutions");
            int expected = reverse ? 600 : 200, cut = Value(spans[1], "First");
            Check(Math.Abs(cut - expected) <= 2, "cut returns to the geometric turn, within 1.6 metres");
            Check(Value(spans[0], "Last") + 1 == cut && Value(spans[0], "First") == 0 && Value(spans[1], "Last") == 800,
                "each original point is represented once, including the boundary");
            Check(points[Value(spans[0], "Confirmed")].TimestampUtc >= points[cut].TimestampUtc.AddSeconds(2),
                "confirmation time stays separate from the backtracked cut");
        }
        Check(Refine(Track(x => 2.5 * Math.Sin(x / 18))).Length == 1, "normal corridor wind noise is not split");
        Check(Refine(Track(x => 12 * Math.Sin(x * Math.PI / 640))).Length == 1, "smooth broad wind bow lacks a localized turn");
        Check(Refine(Track(x => x is >= 100 and <= 104 ? 12 : 0)).Length == 1, "brief outliers do not create a body boundary");
        Check(Refine(Track(x => x * .08)).Length == 1, "one straight slanted leg remains one leg");
        var split = typeof(TrackSegmentationService).GetMethod("SplitRuns", BindingFlags.Static | BindingFlags.NonPublic)!;
        var runs = ((IEnumerable)split.Invoke(null, ["fixture", Track(x => Math.Min(x, 160) * approachSlope),
            new TrackRecognitionOptions(), new WholeLegMatchOptions()])!).Cast<object>().ToArray();
        Check(runs.Length == 2 && runs.All(r => (bool)r.GetType().GetProperty("BodyRefined")!.GetValue(r)!),
            "production splitting uses geometric refinement before identity grouping");
    }
}
