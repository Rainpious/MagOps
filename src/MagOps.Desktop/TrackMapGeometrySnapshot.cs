using MagOps.Core.Domain;
using MagOps.Infrastructure.Services;

namespace MagOps.Desktop;

/// <summary>
/// Display-only geometry. Arrays are owned here and exposed as read-only spans;
/// source identities and every valid observation remain available to consumers.
/// </summary>
internal sealed class TrackMapGeometrySnapshot
{
    private readonly UavMagCanonicalSample[][] _sources;
    private readonly (double X, double Y)[][] _projected;
    private readonly bool[][] _continuousEdges;

    private TrackMapGeometrySnapshot(UavMagCanonicalSample[][] sources,
        (double X, double Y)[][] projected, bool[][] continuousEdges,
        GeoCoordinate origin, bool hasGeometry, double minX, double maxX, double minY, double maxY)
    {
        _sources = sources; _projected = projected; _continuousEdges = continuousEdges;
        Origin = origin; HasGeometry = hasGeometry;
        MinX = minX; MaxX = maxX; MinY = minY; MaxY = maxY;
    }

    internal static TrackMapGeometrySnapshot Empty { get; } = new([], [], [], new(0, 0), false,
        double.PositiveInfinity, double.NegativeInfinity, double.PositiveInfinity, double.NegativeInfinity);
    internal GeoCoordinate Origin { get; }
    internal bool HasGeometry { get; }
    internal double MinX { get; }
    internal double MaxX { get; }
    internal double MinY { get; }
    internal double MaxY { get; }
    internal int StreamCount => _sources.Length;
    internal ReadOnlySpan<UavMagCanonicalSample> Samples(int stream) => _sources[stream];
    internal ReadOnlySpan<(double X, double Y)> Projected(int stream) => _projected[stream];
    // Edge i joins point i-1 to point i. Point zero never has an incoming edge.
    internal bool IsContinuousEdge(int stream, int i) => _continuousEdges[stream][i];
    internal (double X, double Y) Project(GeoCoordinate point) => EndpointLineRecognizer.Delta(Origin, point);
    internal static bool IsContinuousTime(DateTimeOffset before, DateTimeOffset after) =>
        (after - before).TotalSeconds is >= 0 and <= 8;

    internal static TrackMapGeometrySnapshot Build(FieldMagFolderAnalysis analysis,
        IReadOnlyList<PlannedSurveyLineTask> tasks, TrackMapGeometrySnapshot? reusableSources = null)
    {
        var sources = reusableSources?._sources ?? analysis.UavSamples
            .Where(p => p.Longitude.HasValue && p.Latitude.HasValue &&
                WorkareaSpatialService.ValidCoordinate(p.Longitude.Value, p.Latitude.Value))
            .GroupBy(p => p.SourceFile, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(p => p.TimestampUtc).ThenBy(p => p.SourceRecord).ToArray()).ToArray();
        long count = 0; double longitude = 0, latitude = 0;
        foreach (var stream in sources)
        foreach (var point in stream)
        { count++; longitude += point.Longitude!.Value; latitude += point.Latitude!.Value; }
        foreach (var task in tasks)
        { count += 2; longitude += task.Start.Longitude + task.End.Longitude; latitude += task.Start.Latitude + task.End.Latitude; }
        GeoCoordinate origin = count > 0 ? new(longitude / count, latitude / count) : new(0, 0);
        double minX = double.PositiveInfinity, maxX = double.NegativeInfinity;
        double minY = double.PositiveInfinity, maxY = double.NegativeInfinity;
        (double X, double Y) ProjectAndExtend(GeoCoordinate point)
        {
            var xy = EndpointLineRecognizer.Delta(origin, point);
            minX = Math.Min(minX, xy.X); maxX = Math.Max(maxX, xy.X);
            minY = Math.Min(minY, xy.Y); maxY = Math.Max(maxY, xy.Y);
            return xy;
        }
        var projected = sources.Select(stream => stream.Select(point =>
            ProjectAndExtend(new(point.Longitude!.Value, point.Latitude!.Value))).ToArray()).ToArray();
        foreach (var task in tasks) { ProjectAndExtend(task.Start); ProjectAndExtend(task.End); }
        // Task-only changes can share immutable source arrays and time edges;
        // projection arrays must be rebuilt because the origin includes task endpoints.
        var edges = reusableSources?._continuousEdges ?? sources.Select(stream =>
        {
            var continuous = new bool[stream.Length];
            for (int i = 1; i < stream.Length; i++)
                continuous[i] = stream[i].SourceRecord == stream[i - 1].SourceRecord + 1 &&
                    IsContinuousTime(stream[i - 1].TimestampUtc, stream[i].TimestampUtc);
            return continuous;
        }).ToArray();
        return new(sources, projected, edges, origin, count > 0, minX, maxX, minY, maxY);
    }
}
