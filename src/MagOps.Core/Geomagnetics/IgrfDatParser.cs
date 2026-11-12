using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MagOps.Core.Geomagnetics;

/// <summary>
/// Parser for Oasis/Geosoft-style igrf.dat files.
/// Format:
///   year YYYY
///   g(n,m) h(n,m) gDot(n,m) hDot(n,m)
/// with degree/order implied by row order n=1..N, m=0..n.
/// </summary>
public static class IgrfDatParser
{
    public static IgrfModelSet Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        byte[] bytes = File.ReadAllBytes(path);
        string sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        string text = Encoding.UTF8.GetString(bytes);
        return Parse(text, Path.GetFullPath(path), sha256);
    }

    public static IgrfModelSet Parse(string text, string? sourcePath = null, string? sourceSha256 = null)
    {
        ArgumentNullException.ThrowIfNull(text);

        var epochs = new List<IgrfEpochModel>();
        int? currentYear = null;
        var currentRows = new List<double[]>();
        var seenYears = new HashSet<int>();

        void FlushCurrent()
        {
            if (!currentYear.HasValue)
                return;

            int year = currentYear.Value;
            if (!seenYears.Add(year))
                throw new FormatException($"Duplicate IGRF year block: {year}.");
            if (currentRows.Count == 0)
                throw new FormatException($"IGRF year {year} contains no coefficient rows.");

            int maxDegree = InferMaxDegree(currentRows.Count);
            var coefficients = new List<IgrfCoefficient>(currentRows.Count);
            int rowIndex = 0;
            for (int n = 1; n <= maxDegree; n++)
            {
                for (int m = 0; m <= n; m++)
                {
                    double[] row = currentRows[rowIndex++];
                    coefficients.Add(new IgrfCoefficient(
                        n,
                        m,
                        row[0],
                        row[1],
                        row[2],
                        row[3]));
                }
            }

            epochs.Add(new IgrfEpochModel(year, maxDegree, coefficients));
            currentRows.Clear();
        }

        using var reader = new StringReader(text);
        string? line;
        int lineNumber = 0;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNumber++;
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('/'))
                continue;

            if (trimmed.StartsWith("year", StringComparison.OrdinalIgnoreCase))
            {
                string[] yearParts = trimmed.Split(
                    (char[]?)null,
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (yearParts.Length != 2 ||
                    !int.TryParse(yearParts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedYear))
                {
                    throw new FormatException($"Invalid IGRF year header at line {lineNumber}: {line}");
                }

                FlushCurrent();
                currentYear = parsedYear;
                continue;
            }

            if (!currentYear.HasValue)
                throw new FormatException($"Coefficient row before first year block at line {lineNumber}.");

            string[] parts = trimmed.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length != 4)
                throw new FormatException($"Expected 4 coefficient values at line {lineNumber}: {line}");

            var values = new double[4];
            for (int i = 0; i < 4; i++)
            {
                if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]) ||
                    !double.IsFinite(values[i]))
                {
                    throw new FormatException($"Invalid coefficient at line {lineNumber}: {parts[i]}");
                }
            }

            currentRows.Add(values);
        }

        FlushCurrent();
        if (epochs.Count == 0)
            throw new FormatException("No IGRF year blocks were found.");

        return new IgrfModelSet(epochs, sourcePath, sourceSha256);
    }

    private static int InferMaxDegree(int rowCount)
    {
        // Sum_{n=1..N} (n+1) = N(N+3)/2.
        double root = (-3d + Math.Sqrt(9d + 8d * rowCount)) / 2d;
        int n = (int)Math.Round(root, MidpointRounding.AwayFromZero);
        if (n < 1 || n * (n + 3) / 2 != rowCount)
        {
            throw new FormatException(
                $"Coefficient row count {rowCount} does not match a complete n=1..N, m=0..n model.");
        }

        return n;
    }
}
