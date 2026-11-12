using System.Text;

namespace MagOps.Core.Analysis;

public sealed record TableHeaderComparison(
    string FirstPath,
    string SecondPath,
    string[] FirstHeaders,
    string[] SecondHeaders,
    bool Compatible,
    bool SameOrder,
    IReadOnlyList<string> OnlyInFirst,
    IReadOnlyList<string> OnlyInSecond,
    string Message);

public sealed record SameHeaderMergeResult(
    string OutputPath,
    long FirstDataRows,
    long SecondDataRows,
    long TotalDataRows,
    int ColumnCount,
    bool SecondTableReordered);

/// <summary>
/// Strict two-table merge for Excel-style text tables (CSV/TXT/TSV).
/// The two inputs must contain the same named header fields. Column order may differ;
/// table B is reordered to table A before rows are appended. The output contains one
/// header row followed by all rows from A and then all rows from B.
/// </summary>
public sealed class SameHeaderTableMergeService
{
    private static readonly string[] SupportedExtensions = [".csv", ".txt", ".tsv"];

    public TableHeaderComparison CompareHeaders(string firstPath, string secondPath)
    {
        ValidateSource(firstPath, "表 A");
        ValidateSource(secondPath, "表 B");
        if (Path.GetFullPath(firstPath).Equals(Path.GetFullPath(secondPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("表 A 和表 B 不能选择同一个文件。");

        string[] first = ReadHeader(firstPath);
        string[] second = ReadHeader(secondPath);
        string[] firstKeys = NormalizeHeaders(first, firstPath);
        string[] secondKeys = NormalizeHeaders(second, secondPath);

        bool sameOrder = firstKeys.SequenceEqual(secondKeys, StringComparer.OrdinalIgnoreCase);
        var firstSet = new HashSet<string>(firstKeys, StringComparer.OrdinalIgnoreCase);
        var secondSet = new HashSet<string>(secondKeys, StringComparer.OrdinalIgnoreCase);

        string[] onlyInFirst = first
            .Where((_, i) => !secondSet.Contains(firstKeys[i]))
            .ToArray();
        string[] onlyInSecond = second
            .Where((_, i) => !firstSet.Contains(secondKeys[i]))
            .ToArray();

        bool compatible = first.Length == second.Length &&
                          onlyInFirst.Length == 0 &&
                          onlyInSecond.Length == 0;

        string message;
        if (compatible && sameOrder)
        {
            message = $"表头一致：共 {first.Length} 列，可直接按 A → B 合并。";
        }
        else if (compatible)
        {
            message = $"表头字段一致：共 {first.Length} 列，但列顺序不同；合并时会自动按表 A 的表头顺序对齐表 B。";
        }
        else
        {
            var parts = new List<string>
            {
                $"表头不一致：表 A {first.Length} 列，表 B {second.Length} 列。"
            };
            if (onlyInFirst.Length > 0)
                parts.Add("仅表 A：" + string.Join("、", onlyInFirst));
            if (onlyInSecond.Length > 0)
                parts.Add("仅表 B：" + string.Join("、", onlyInSecond));
            message = string.Join(" ", parts);
        }

        return new TableHeaderComparison(
            Path.GetFullPath(firstPath),
            Path.GetFullPath(secondPath),
            first,
            second,
            compatible,
            sameOrder,
            onlyInFirst,
            onlyInSecond,
            message);
    }

    public SameHeaderMergeResult Merge(string firstPath, string secondPath, string outputPath)
    {
        TableHeaderComparison comparison = CompareHeaders(firstPath, secondPath);
        if (!comparison.Compatible)
            throw new InvalidDataException(comparison.Message);

        string outputFull = Path.GetFullPath(outputPath);
        string firstFull = Path.GetFullPath(firstPath);
        string secondFull = Path.GetFullPath(secondPath);
        if (outputFull.Equals(firstFull, StringComparison.OrdinalIgnoreCase) ||
            outputFull.Equals(secondFull, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("合并输出不能覆盖任一源表，请选择新的输出文件名。");
        }

        string? directory = Path.GetDirectoryName(outputFull);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        string temp = outputFull + ".magops-merge-" + Guid.NewGuid().ToString("N") + ".tmp";
        long firstRows = 0;
        long secondRows = 0;

        try
        {
            using (var writer = new StreamWriter(
                       temp,
                       false,
                       new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)))
            {
                WriteRecord(writer, comparison.FirstHeaders, ',');

                firstRows = AppendSource(
                    firstPath,
                    comparison.FirstHeaders,
                    comparison.FirstHeaders,
                    writer,
                    sourceIsFirst: true);

                secondRows = AppendSource(
                    secondPath,
                    comparison.SecondHeaders,
                    comparison.FirstHeaders,
                    writer,
                    sourceIsFirst: false);
            }

            File.Move(temp, outputFull, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            throw;
        }

        return new SameHeaderMergeResult(
            outputFull,
            firstRows,
            secondRows,
            firstRows + secondRows,
            comparison.FirstHeaders.Length,
            !comparison.SameOrder);
    }

    private static long AppendSource(
        string path,
        string[] sourceHeaders,
        string[] outputHeaders,
        TextWriter writer,
        bool sourceIsFirst)
    {
        string[] sourceKeys = NormalizeHeaders(sourceHeaders, path);
        string[] outputKeys = NormalizeHeaders(outputHeaders, path);
        var sourceIndex = sourceKeys
            .Select((key, index) => (key, index))
            .ToDictionary(x => x.key, x => x.index, StringComparer.OrdinalIgnoreCase);
        int[] order = outputKeys.Select(key => sourceIndex[key]).ToArray();

        using TextReader reader = OpenReader(path, out char delimiter);
        using IEnumerator<string[]> records = ReadRecords(reader, delimiter).GetEnumerator();
        if (!MoveToNextNonBlank(records, out string[]? header))
            throw new InvalidDataException($"表格没有表头：{Path.GetFileName(path)}");

        string[] actualKeys = NormalizeHeaders(header!, path);
        if (!actualKeys.SequenceEqual(sourceKeys, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException($"读取 {Path.GetFileName(path)} 时表头发生变化，请重新选择文件后再试。");

        long rowNumber = 1;
        long dataRows = 0;
        while (records.MoveNext())
        {
            rowNumber++;
            string[] row = records.Current;
            if (IsBlank(row))
                continue;
            if (row.Length != sourceHeaders.Length)
            {
                throw new InvalidDataException(
                    $"{Path.GetFileName(path)} 第 {rowNumber} 行列数为 {row.Length}，与表头 {sourceHeaders.Length} 列不一致。请先修正该行再合并。");
            }

            string[] output = sourceIsFirst
                ? row
                : order.Select(index => row[index]).ToArray();
            WriteRecord(writer, output, ',');
            dataRows++;
        }

        return dataRows;
    }

    private static string[] ReadHeader(string path)
    {
        using TextReader reader = OpenReader(path, out char delimiter);
        using IEnumerator<string[]> records = ReadRecords(reader, delimiter).GetEnumerator();
        if (!MoveToNextNonBlank(records, out string[]? header))
            throw new InvalidDataException($"表格没有表头：{Path.GetFileName(path)}");
        _ = NormalizeHeaders(header!, path);
        return header!;
    }

    private static string[] NormalizeHeaders(string[] headers, string path)
    {
        if (headers.Length == 0)
            throw new InvalidDataException($"表格没有表头字段：{Path.GetFileName(path)}");

        string[] keys = headers.Select(x => (x ?? "").Trim()).ToArray();
        int blank = Array.FindIndex(keys, string.IsNullOrWhiteSpace);
        if (blank >= 0)
            throw new InvalidDataException($"{Path.GetFileName(path)} 的第 {blank + 1} 列表头为空，无法安全按表头合并。");

        string? duplicate = keys
            .GroupBy(x => x, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1)
            ?.Key;
        if (!string.IsNullOrWhiteSpace(duplicate))
            throw new InvalidDataException($"{Path.GetFileName(path)} 存在重复表头“{duplicate}”，无法唯一对齐列。");

        return keys;
    }

    private static void ValidateSource(string path, string label)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new FileNotFoundException($"请选择有效的{label}文件。", path);

        string extension = Path.GetExtension(path);
        if (!SupportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            throw new NotSupportedException($"{label}当前只支持 CSV/TXT/TSV：{Path.GetFileName(path)}");
    }

    private static TextReader OpenReader(string path, out char delimiter)
    {
        Encoding encoding = DetectEncoding(path);
        delimiter = DetectDelimiter(path, encoding);
        return new StreamReader(path, encoding, detectEncodingFromByteOrderMarks: true);
    }

    private static Encoding DetectEncoding(string path)
    {
        using (var stream = File.OpenRead(path))
        {
            Span<byte> bom = stackalloc byte[3];
            int count = stream.Read(bom);
            if (count >= 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF)
                return new UTF8Encoding(true);
            if (count >= 2 && bom[0] == 0xFF && bom[1] == 0xFE)
                return Encoding.Unicode;
            if (count >= 2 && bom[0] == 0xFE && bom[1] == 0xFF)
                return Encoding.BigEndianUnicode;
        }

        if (IsValidUtf8(path))
            return new UTF8Encoding(false);

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        try { return Encoding.GetEncoding(54936); }
        catch { return Encoding.GetEncoding(936); }
    }

    private static bool IsValidUtf8(string path)
    {
        var utf8 = new UTF8Encoding(false, true);
        Decoder decoder = utf8.GetDecoder();
        byte[] bytes = new byte[65536];
        char[] chars = new char[65536];

        try
        {
            using var stream = File.OpenRead(path);
            while (true)
            {
                int read = stream.Read(bytes, 0, bytes.Length);
                bool flush = read == 0;
                decoder.Convert(
                    bytes,
                    0,
                    read,
                    chars,
                    0,
                    chars.Length,
                    flush,
                    out _,
                    out _,
                    out _);
                if (flush)
                    return true;
            }
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static char DetectDelimiter(string path, Encoding encoding)
    {
        using var reader = new StreamReader(path, encoding, detectEncodingFromByteOrderMarks: true);
        string? line;
        do
        {
            line = reader.ReadLine();
        } while (line is not null && line.Trim().Length == 0);

        if (line is null)
            throw new InvalidDataException($"表格没有数据：{Path.GetFileName(path)}");

        var counts = new Dictionary<char, int>
        {
            [','] = CountOutsideQuotes(line, ','),
            ['\t'] = CountOutsideQuotes(line, '\t'),
            [';'] = CountOutsideQuotes(line, ';')
        };
        var best = counts.OrderByDescending(x => x.Value).First();
        if (best.Value <= 0)
        {
            string extension = Path.GetExtension(path);
            return extension.Equals(".tsv", StringComparison.OrdinalIgnoreCase) ? '\t' : ',';
        }
        return best.Key;
    }

    private static int CountOutsideQuotes(string text, char delimiter)
    {
        int count = 0;
        bool quoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '"')
            {
                if (quoted && i + 1 < text.Length && text[i + 1] == '"')
                {
                    i++;
                    continue;
                }
                quoted = !quoted;
                continue;
            }
            if (!quoted && c == delimiter)
                count++;
        }
        return count;
    }

    private static IEnumerable<string[]> ReadRecords(TextReader reader, char delimiter)
    {
        var row = new List<string>();
        var field = new StringBuilder();
        bool quoted = false;

        while (true)
        {
            int raw = reader.Read();
            if (raw < 0)
                break;
            char c = (char)raw;

            if (quoted)
            {
                if (c == '"')
                {
                    int next = reader.Peek();
                    if (next == '"')
                    {
                        _ = reader.Read();
                        field.Append('"');
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
                if (c == '\r' && reader.Peek() == '\n')
                    _ = reader.Read();
                row.Add(field.ToString());
                field.Clear();
                yield return row.ToArray();
                row.Clear();
                continue;
            }

            field.Append(c);
        }

        if (quoted)
            throw new InvalidDataException("CSV/TXT 中存在未闭合的引号字段。");

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            yield return row.ToArray();
        }
    }

    private static bool MoveToNextNonBlank(IEnumerator<string[]> records, out string[]? row)
    {
        while (records.MoveNext())
        {
            if (IsBlank(records.Current))
                continue;
            row = records.Current;
            return true;
        }
        row = null;
        return false;
    }

    private static bool IsBlank(IEnumerable<string> row) =>
        row.All(x => string.IsNullOrWhiteSpace(x));

    private static void WriteRecord(TextWriter writer, IReadOnlyList<string> values, char delimiter)
    {
        for (int i = 0; i < values.Count; i++)
        {
            if (i > 0) writer.Write(delimiter);
            WriteCell(writer, values[i] ?? "", delimiter);
        }
        writer.WriteLine();
    }

    private static void WriteCell(TextWriter writer, string value, char delimiter)
    {
        bool quote = value.Contains(delimiter) ||
                     value.Contains('"') ||
                     value.Contains('\r') ||
                     value.Contains('\n');
        if (!quote)
        {
            writer.Write(value);
            return;
        }

        writer.Write('"');
        writer.Write(value.Replace("\"", "\"\"", StringComparison.Ordinal));
        writer.Write('"');
    }
}
