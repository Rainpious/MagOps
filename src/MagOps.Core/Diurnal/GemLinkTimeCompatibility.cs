namespace MagOps.Core.Diurnal;

public static class GemLinkTimeCompatibility
{
    public static int ToTenthsOfSecondSinceUtcMidnight(
        DateTimeOffset timestamp)
    {
        DateTimeOffset utc = timestamp.ToUniversalTime();
        long ticksWithinSecond =
            utc.TimeOfDay.Ticks % TimeSpan.TicksPerSecond;
        int tenth =
            (int)(ticksWithinSecond /
                (TimeSpan.TicksPerSecond / 10));

        return utc.Hour * 36000 +
               utc.Minute * 600 +
               utc.Second * 10 +
               tenth;
    }

    public static bool TryParseGemLinkTime(
        string? value,
        out int tenthsOfSecondSinceMidnight)
    {
        tenthsOfSecondSinceMidnight = 0;
        string text = (value ?? "").Trim();
        if (text.Length == 0)
            return false;

        string main = text;
        string fraction = "";
        int dot = text.IndexOf('.');
        if (dot >= 0)
        {
            if (text.IndexOf('.', dot + 1) >= 0)
                return false;
            main = text[..dot];
            fraction = text[(dot + 1)..];
            if (fraction.Length == 0 ||
                fraction.Any(c => !char.IsDigit(c)))
            {
                return false;
            }
        }

        int hour;
        int minute;
        int second;
        if (main.Contains(':'))
        {
            string[] parts = main.Split(':');
            if (parts.Length != 3 ||
                !int.TryParse(parts[0], out hour) ||
                !int.TryParse(parts[1], out minute) ||
                !int.TryParse(parts[2], out second))
            {
                return false;
            }
        }
        else
        {
            if (main.Length != 6 ||
                main.Any(c => !char.IsDigit(c)) ||
                !int.TryParse(main[..2], out hour) ||
                !int.TryParse(main.Substring(2, 2), out minute) ||
                !int.TryParse(main.Substring(4, 2), out second))
            {
                return false;
            }
        }

        int tenth =
            fraction.Length == 0
                ? 0
                : fraction[0] - '0';

        // GEMLink TimeConv special case:
        // 240000.0 -> 000000.0.
        if (hour == 24 &&
            minute == 0 &&
            second == 0 &&
            tenth == 0)
        {
            tenthsOfSecondSinceMidnight = 0;
            return true;
        }

        if (hour is < 0 or > 23 ||
            minute is < 0 or > 59 ||
            second is < 0 or > 59)
        {
            return false;
        }

        tenthsOfSecondSinceMidnight =
            hour * 36000 +
            minute * 600 +
            second * 10 +
            tenth;
        return true;
    }
}
