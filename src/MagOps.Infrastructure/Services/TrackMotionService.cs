using MagOps.Core.Domain;

namespace MagOps.Infrastructure.Services;

public sealed record TrackMotionObservation(string Source, DateTimeOffset Time, GeoCoordinate Coordinate,
    double? AltitudeM, double? SpeedMps = null, double? ClimbMps = null, double? Bearing = null, double? DurationSeconds = null);

/// <summary>Source-second median telemetry, measured over a centred four-second window.
/// Original timestamps and magnetic samples are never rewritten or removed.</summary>
public static class TrackMotionService
{
    public const string Version = "source-second-median-centred4-1";
    public static IReadOnlyList<TrackMotionObservation> Measure(IEnumerable<SurveyTrackPoint> points)
    {
        var result = new List<TrackMotionObservation>();
        foreach (var stream in points.Where(p => WorkareaSpatialService.ValidCoordinate(p.Coordinate.Longitude, p.Coordinate.Latitude))
                     .GroupBy(p => p.SourceFile, StringComparer.OrdinalIgnoreCase))
        {
            var a = stream.GroupBy(p => p.TimestampUtc.ToUnixTimeSeconds()).OrderBy(g => g.Key).Select(g =>
                new TrackMotionObservation(stream.Key, DateTimeOffset.FromUnixTimeSeconds(g.Key),
                    new(Median(g.Select(p => p.Coordinate.Longitude)), Median(g.Select(p => p.Coordinate.Latitude))),
                    g.Any(p => p.AltitudeM is { } h && double.IsFinite(h))
                        ? Median(g.Where(p => p.AltitudeM is { } h && double.IsFinite(h)).Select(p => p.AltitudeM!.Value)) : null)).ToArray();
            for (int i = 0; i < a.Length; i++)
            {
                int lo = i, hi = i;
                while (lo > 0 && (a[i].Time - a[lo - 1].Time).TotalSeconds <= 2 && (a[lo].Time - a[lo - 1].Time).TotalSeconds <= 1) lo--;
                while (hi + 1 < a.Length && (a[hi + 1].Time - a[i].Time).TotalSeconds <= 2 && (a[hi + 1].Time - a[hi].Time).TotalSeconds <= 1) hi++;
                double dt = (a[hi].Time - a[lo].Time).TotalSeconds;
                if (dt <= 0) { result.Add(a[i]); continue; }
                double distance = TrackSegmentationReferenceService.Distance(a[lo].Coordinate, a[hi].Coordinate);
                result.Add(a[i] with { SpeedMps = distance / dt,
                    ClimbMps = a[lo].AltitudeM is { } h0 && a[hi].AltitudeM is { } h1 ? (h1 - h0) / dt : null,
                    Bearing = distance >= 1 ? SurveyDirection.Bearing(a[lo].Coordinate, a[hi].Coordinate) : null, DurationSeconds = dt });
            }
        }
        return result;
    }
    private static double Median(IEnumerable<double> values)
    { var a = values.Order().ToArray(); int i = a.Length / 2; return a.Length % 2 == 0 ? (a[i - 1] + a[i]) / 2 : a[i]; }
}
