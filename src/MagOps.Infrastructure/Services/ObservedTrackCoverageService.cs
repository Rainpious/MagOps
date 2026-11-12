using MagOps.Core.Domain;

namespace MagOps.Infrastructure.Services;

/// <summary>Sampling support on an independent line; never spans sources or missing observations.</summary>
public static class ObservedTrackCoverageService
{
    public const string Version = "observed-continuous-coverage-1";
    private sealed record Observation(DateTimeOffset Time, double Along, GeoCoordinate Position);
    public static IReadOnlyList<CoverageInterval> Calculate(IReadOnlyList<FlightLegSample> samples,
        PlannedSurveyLineTask task, WholeLegMatchOptions options)
    {
        var (dx, dy) = EndpointLineRecognizer.Delta(task.Start, task.End);
        double length = Math.Sqrt(dx * dx + dy * dy);
        if (length < .5) return [];
        var intervals = new List<CoverageInterval>();
        foreach (var source in samples.GroupBy(p => p.SourceFile, StringComparer.OrdinalIgnoreCase))
        {
            // Multiple magnetic readings can carry the same GPS/UTC second.
            // They remain in Pass.Samples; only coverage observations collapse.
            var observations = source.GroupBy(p => p.TimestampUtc).OrderBy(g => g.Key).Select(g =>
            {
                var p = new GeoCoordinate(Median(g.Select(p => p.Longitude)), Median(g.Select(p => p.Latitude)));
                var (x, y) = EndpointLineRecognizer.Delta(task.Start, p);
                return new Observation(g.Key, (x * dx + y * dy) / length, p);
            }).ToArray();
            var gaps = observations.Zip(observations.Skip(1), (a, b) => (b.Time - a.Time).TotalSeconds).Where(g => g > 0).ToArray();
            if (gaps.Length == 0) continue;
            double cadence = Median(gaps);
            double limit = Math.Min(options.MaximumTimeGapSeconds, Math.Max(1, 5 * cadence));
            // A missing GPS second is not evidence of a continuous traversal.
            // Honour the existing evidence-gap setting; low-rate sources are
            // supported at their own cadence, without silently spanning gaps.
            limit = Math.Min(limit, Math.Max(options.MaximumEvidenceGapSeconds, cadence * 1.5));
            var steps = observations.Zip(observations.Skip(1), (a, b) =>
                ((b.Time - a.Time).TotalSeconds, Math.Abs(b.Along - a.Along)))
                .Where(s => s.Item1 > 0 && s.Item1 <= limit && s.Item2 / s.Item1 <= 50).Select(s => s.Item2).ToArray();
            double support = steps.Length == 0 ? 0 : Math.Min(options.EndpointSearchM, Median(steps) / 2);
            // Footprints represent normal discrete sampling. No fixed 10 m
            // expansion; a 20 m missing interval retains its uncovered body.
            foreach (var o in observations)
                if (support > 0) intervals.Add(new((o.Along - support) / length, (o.Along + support) / length));
            for (int i = 1; i < observations.Length; i++)
            {
                var a = observations[i - 1]; var b = observations[i];
                double dt = (b.Time - a.Time).TotalSeconds;
                if (dt <= 0 || dt > limit || TrackSegmentationReferenceService.Distance(a.Position, b.Position) / dt > 50) continue;
                intervals.Add(new(Math.Min(a.Along, b.Along) / length, Math.Max(a.Along, b.Along) / length));
            }
        }
        return SurveyCoverage.Union(intervals);
    }
    private static double Median(IEnumerable<double> source)
    {
        var a = source.Order().ToArray();
        return a.Length == 0 ? 0 : a.Length % 2 == 0 ? (a[a.Length / 2 - 1] + a[a.Length / 2]) / 2 : a[a.Length / 2];
    }
}
