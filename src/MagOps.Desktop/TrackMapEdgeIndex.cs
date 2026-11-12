namespace MagOps.Desktop;

/// <summary>
/// Single display-edge contract. A screen segment may join two observations
/// only when the source stream is time-continuous and both observations have
/// the same flight-leg identity. This is display/hit-test topology only; it
/// never removes or rewrites source observations used by recognition/export.
/// </summary>
internal sealed class TrackMapEdgeIndex
{
    private readonly string[][] _legIds;
    private readonly bool[][] _edges;

    private TrackMapEdgeIndex(string[][] legIds, bool[][] edges)
    { _legIds = legIds; _edges = edges; }

    internal static TrackMapEdgeIndex Empty { get; } = new([], []);
    internal string LegId(int stream, int pointIndex) => _legIds[stream][pointIndex];
    internal bool IsContinuous(int stream, int pointIndex) => _edges[stream][pointIndex];

    internal static TrackMapEdgeIndex Build(TrackMapGeometrySnapshot geometry,
        IReadOnlyDictionary<(string SourceFile, int SourceRecord), string> sampleLegIds)
    {
        var ids = new string[geometry.StreamCount][];
        var edges = new bool[geometry.StreamCount][];
        for (int s = 0; s < geometry.StreamCount; s++)
        {
            var samples = geometry.Samples(s);
            ids[s] = new string[samples.Length];
            edges[s] = new bool[samples.Length];
            for (int i = 0; i < samples.Length; i++)
            {
                ids[s][i] = sampleLegIds.GetValueOrDefault((samples[i].SourceFile, samples[i].SourceRecord), "");
                // Empty identity retains the existing auxiliary-trace rendering;
                // hit testing still requires a nonempty flight-leg identity.
                edges[s][i] = i > 0 && geometry.IsContinuousEdge(s, i) && ids[s][i - 1] == ids[s][i];
            }
        }
        return new(ids, edges);
    }

    internal static bool IsContinuous(MagOps.Core.Domain.FlightLegSample before,
        MagOps.Core.Domain.FlightLegSample after) =>
        before.SourceFile.Equals(after.SourceFile, StringComparison.OrdinalIgnoreCase) &&
        after.SourceRecord == before.SourceRecord + 1 &&
        TrackMapGeometrySnapshot.IsContinuousTime(before.TimestampUtc, after.TimestampUtc);
}
