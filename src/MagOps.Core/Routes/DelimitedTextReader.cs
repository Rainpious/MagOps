using System.Globalization;
using System.Text;

namespace MagOps.Core.Routes;

public static class DelimitedTextReader
{
    private static readonly HashSet<string> KnownHeaders =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "point", "pointname", "name", "line", "测线", "线号", "点名", "航点", "航点名",
            "x", "y", "easting", "northing", "lon", "longitude", "lat", "latitude", "经度", "纬度"
        };

    public static IReadOnlyList<RawRoutePoint> ReadRawRouteCsv(
        string path,
        string headerlessOrder = "Point,Y,X")
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("原始航线端点 CSV 不存在。", path);

        var parsed = ReadRows(path);
        var rows = parsed.Rows;
        bool hasHeader = rows[0].Any(v => KnownHeaders.Contains(v.Trim()));
        var points = new List<RawRoutePoint>();

        if (hasHeader)
        {
            string[] headers = rows[0];
            int xi = FindHeaderIndex(headers, ["x", "easting", "lon", "longitude", "经度"]);
            int yi = FindHeaderIndex(headers, ["y", "northing", "lat", "latitude", "纬度"]);
            int li = FindHeaderIndex(headers,
                ["point", "pointname", "name", "line", "测线", "线号", "点名", "航点", "航点名"]);
            if (xi < 0 || yi < 0)
                throw new InvalidDataException("有表头 CSV 必须包含 X/Y 或经度/纬度字段。");

            for (int r = 1; r < rows.Count; r++)
            {
                string[] row = rows[r];
                if (row.Length <= Math.Max(xi, yi))
                    throw new InvalidDataException($"第 {r + 1} 行列数不足。");

                double x = ParseNumber(row[xi], $"第 {r + 1} 行 X");
                double y = ParseNumber(row[yi], $"第 {r + 1} 行 Y");
                string label = li >= 0 && li < row.Length ? row[li] : "";
                points.Add(new RawRoutePoint(x, y, label, r + 1));
            }
        }
        else
        {
            for (int r = 0; r < rows.Count; r++)
            {
                string[] row = rows[r];
                string label = "";
                double x;
                double y;

                switch (headerlessOrder)
                {
                    case "Point,Y,X":
                        if (row.Length < 3) throw new InvalidDataException($"第 {r + 1} 行与 Point,Y,X 不匹配。");
                        label = row[0];
                        y = ParseNumber(row[1], $"第 {r + 1} 行 Y");
                        x = ParseNumber(row[2], $"第 {r + 1} 行 X");
                        break;
                    case "Point,X,Y":
                        if (row.Length < 3) throw new InvalidDataException($"第 {r + 1} 行与 Point,X,Y 不匹配。");
                        label = row[0];
                        x = ParseNumber(row[1], $"第 {r + 1} 行 X");
                        y = ParseNumber(row[2], $"第 {r + 1} 行 Y");
                        break;
                    case "Y,X":
                        if (row.Length < 2) throw new InvalidDataException($"第 {r + 1} 行与 Y,X 不匹配。");
                        y = ParseNumber(row[0], $"第 {r + 1} 行 Y");
                        x = ParseNumber(row[1], $"第 {r + 1} 行 X");
                        break;
                    case "X,Y":
                        if (row.Length < 2) throw new InvalidDataException($"第 {r + 1} 行与 X,Y 不匹配。");
                        x = ParseNumber(row[0], $"第 {r + 1} 行 X");
                        y = ParseNumber(row[1], $"第 {r + 1} 行 Y");
                        break;
                    default:
                        throw new InvalidDataException($"未知无表头列顺序：{headerlessOrder}");
                }

                points.Add(new RawRoutePoint(x, y, label, r + 1));
            }
        }

        if (points.Count < 2)
            throw new InvalidDataException("至少需要 2 个端点记录。");
        return points;
    }

    public static (List<string[]> Rows, Encoding Encoding, char Delimiter) ReadRows(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        Encoding encoding = DetectEncoding(bytes);
        string text = encoding.GetString(bytes).TrimStart('\uFEFF');
        string first = text.Split(["\r\n", "\n", "\r"], StringSplitOptions.None)
            .FirstOrDefault(x => x.Trim().Length > 0)
            ?? throw new InvalidDataException("CSV 没有数据。");

        var choices = new[]
        {
            (Delimiter: ',', Count: first.Count(c => c == ',')),
            (Delimiter: '\t', Count: first.Count(c => c == '\t')),
            (Delimiter: ';', Count: first.Count(c => c == ';'))
        };
        char delimiter = choices.OrderByDescending(x => x.Count).First().Count > 0
            ? choices.OrderByDescending(x => x.Count).First().Delimiter
            : ',';

        List<string[]> rows = ParseDelimited(text, delimiter)
            .Select(row => row.Select(v => v.Trim()).ToArray())
            .Where(row => row.Any(v => v.Length > 0))
            .ToList();

        if (rows.Count == 0)
            throw new InvalidDataException("CSV 没有数据。");

        return (rows, encoding, delimiter);
    }

    public static double ParseNumber(string? text, string label)
    {
        string value = (text ?? "").Trim();
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
            return v;
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out v))
            return v;
        throw new FormatException($"{label} 不是有效数字：{text}");
    }

    private static int FindHeaderIndex(string[] headers, IEnumerable<string> names)
    {
        var wanted = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < headers.Length; i++)
            if (wanted.Contains(headers[i].Trim()))
                return i;
        return -1;
    }

    private static Encoding DetectEncoding(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return new UTF8Encoding(true);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode;
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode;

        try
        {
            _ = new UTF8Encoding(false, true).GetString(bytes);
            return new UTF8Encoding(false);
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            try { return Encoding.GetEncoding(54936); }
            catch { return Encoding.GetEncoding(936); }
        }
    }

    private static IEnumerable<string[]> ParseDelimited(string text, char delimiter)
    {
        var row = new List<string>();
        var field = new StringBuilder();
        bool quoted = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];

            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    field.Append(c);
                }
                continue;
            }

            if (c == '"' && field.Length == 0)
            {
                quoted = true;
                continue;
            }

            if (c == delimiter)
            {
                row.Add(field.ToString());
                field.Clear();
                continue;
            }

            if (c == '\r' || c == '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                    i++;
                row.Add(field.ToString());
                field.Clear();
                yield return row.ToArray();
                row.Clear();
                continue;
            }

            field.Append(c);
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            yield return row.ToArray();
        }
    }
}
