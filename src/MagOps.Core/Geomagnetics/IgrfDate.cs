namespace MagOps.Core.Geomagnetics;

public static class IgrfDate
{
    /// <summary>
    /// Converts a UTC timestamp to decimal year using the actual length of that UTC year.
    /// IGRF secular variation is annual/decadal; time-of-day only produces a tiny fraction
    /// of a year, but retaining it keeps MagOps timestamp semantics deterministic.
    /// </summary>
    public static double ToDecimalYear(DateTimeOffset timestamp)
    {
        DateTimeOffset utc = timestamp.ToUniversalTime();
        var start = new DateTimeOffset(utc.Year, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var end = start.AddYears(1);
        return utc.Year + (utc - start).TotalSeconds / (end - start).TotalSeconds;
    }

    public static double ToDecimalYear(DateOnly date)
    {
        var timestamp = new DateTimeOffset(
            date.Year,
            date.Month,
            date.Day,
            0,
            0,
            0,
            TimeSpan.Zero);
        return ToDecimalYear(timestamp);
    }
}
