using MagOps.Core.Coordinates;

namespace MagOps.Core.Domain;

public sealed record WorkareaSpatialRange(string WorkareaPath, string Source,
    ProjectionParameters Projection, double MinEasting, double MinNorthing,
    double MaxEasting, double MaxNorthing, double CentroidEasting, double CentroidNorthing)
{
    public double DistanceSquared(double easting, double northing)
    {
        double dx = Math.Max(Math.Max(MinEasting - easting, 0d), easting - MaxEasting);
        double dy = Math.Max(Math.Max(MinNorthing - northing, 0d), northing - MaxNorthing);
        return dx * dx + dy * dy;
    }

    public bool ContainsBuffered(double easting, double northing, double bufferM) =>
        easting >= MinEasting - bufferM && easting <= MaxEasting + bufferM &&
        northing >= MinNorthing - bufferM && northing <= MaxNorthing + bufferM;
}

public sealed record WorkareaScreeningOptions(double CoarseDistanceM = 20000d, double BufferM = 1000d);
public sealed record WorkareaScreeningDecision(string GroupId, string Decision, string Reason);
public sealed record WorkareaExcludedSample(string SourceFile, int SourceRecord);
public sealed record WorkareaScreeningInput(WorkareaSpatialRange? Range, WorkareaScreeningOptions Options)
{
    public string SourceFingerprint { get; init; } = "";
    public string RouteFingerprint { get; init; } = "";
    public string ContextFingerprint { get; init; } = "";
    public IReadOnlyList<WorkareaScreeningDecision> Decisions { get; init; } = [];
    public IReadOnlyList<WorkareaExcludedSample> ExcludedSamples { get; init; } = [];
}
