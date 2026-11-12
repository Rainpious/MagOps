namespace MagOps.Core.Coordinates;

public enum CoordinateInputMode
{
    Dms,
    DecimalDegrees,
    ProjectedXY
}

public enum XyAxisOrder
{
    EastingNorthing,
    NorthingEasting
}

public enum CoordinateProjectionKind { GaussKruger, Utm, KertauRsoMalaya }

public sealed record ProjectionParameters(
    string Datum = "CGCS2000",
    double? CentralMeridian = null,
    double FalseEasting = 500000d,
    double FalseNorthing = 0d,
    XyAxisOrder AxisOrder = XyAxisOrder.EastingNorthing,
    CoordinateProjectionKind Kind = CoordinateProjectionKind.GaussKruger,
    int UtmZone = 48,
    bool SouthernHemisphere = false
);

public sealed record CoordinateConversionResult(
    double Longitude,
    double Latitude,
    string LongitudeDecimal,
    string LatitudeDecimal,
    string LongitudeDms,
    string LatitudeDms,
    double X,
    double Y,
    double Easting,
    double Northing,
    string AxisOrderLabel,
    double CentralMeridian,
    string Datum,
    string? PointId = null,
    CoordinateProjectionKind ProjectionKind = CoordinateProjectionKind.GaussKruger
);

public sealed record CoordinateInputPair(
    string First,
    string Second,
    string? PointId = null);

public sealed record BatchConversionResult(
    IReadOnlyList<CoordinateConversionResult> Results,
    double CentralMeridian,
    IReadOnlyList<double> SuggestedMeridians,
    string Warning
);
