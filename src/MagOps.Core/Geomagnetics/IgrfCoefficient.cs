namespace MagOps.Core.Geomagnetics;

/// <summary>
/// One Schmidt quasi-normalized IGRF Gauss coefficient tuple.
/// Units are nT for G/H and nT/year for GDot/HDot.
/// </summary>
public readonly record struct IgrfCoefficient(
    int Degree,
    int Order,
    double G,
    double H,
    double GDot,
    double HDot);
