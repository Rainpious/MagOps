namespace MagOps.Core.Geomagnetics;

/// <summary>
/// IGRF main-field elements in local geodetic North/East/Down coordinates.
/// </summary>
public sealed record GeomagneticFieldResult(
    double NorthNt,
    double EastNt,
    double DownNt,
    double HorizontalNt,
    double TotalNt,
    double DeclinationDeg,
    double InclinationDeg,
    double DecimalYear,
    int ModelEpochYear,
    int MaxDegree,
    string? CoefficientSourceSha256)
{
    public double XNt => NorthNt;
    public double YNt => EastNt;
    public double ZNt => DownNt;
    public double HNt => HorizontalNt;
    public double FNt => TotalNt;
    public double DDeg => DeclinationDeg;
    public double IDeg => InclinationDeg;
}
