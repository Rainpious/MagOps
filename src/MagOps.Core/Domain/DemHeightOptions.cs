namespace MagOps.Core.Domain;

/// <summary>Workarea-scoped, explicit compatibility evidence; never changes raw altitude.</summary>
public sealed record DemHeightOptions
{
    public string DemPath { get; init; } = "";
    public bool CalculateOnImport { get; init; } = true;
    public string ObservationHeightDatum { get; init; } = "";
    public string DemHeightDatum { get; init; } = "";
    public string CompatibilityEvidence { get; init; } = "";
    public bool CompatibilityConfirmed { get; init; }
    public string ConfirmedDemSha256 { get; init; } = "";
}
