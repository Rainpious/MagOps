namespace MagOps.Core.Diurnal;

/// <summary>Production base interpolation is isolated by the sample's UTC day.</summary>
public static class DiurnalDatePolicy
{
    public static DateTime UtcDay(DateTimeOffset timestamp) =>
        timestamp.ToUniversalTime().Date;
}
