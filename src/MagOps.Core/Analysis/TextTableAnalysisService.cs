using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MagOps.Core.Routes;

namespace MagOps.Core.Analysis;

public sealed record TextTable(string[] Headers, List<string[]> Rows, char Delimiter, string Extension);

public sealed record GroupAverageRow(string Group, double? Average, int Count);

public sealed class GroupAverageResult
{
    public string GroupColumnName { get; init; } = "";
    public string ValueColumnName { get; init; } = "";
    public List<GroupAverageRow> Rows { get; init; } = [];
    public int SourceRowCount { get; init; }
    public int ValidValueCount { get; init; }
    public int SkippedValueCount { get; init; }
    public int Decimals { get; init; }

    public string ToTabText()
    {
        var lines = new List<string> { $"{GroupColumnName}\t{ValueColumnName}平均值" };
        lines.AddRange(Rows.Select(r =>
            $"{r.Group}\t{(r.Average.HasValue ? r.Average.Value.ToString($"F{Decimals}", CultureInfo.InvariantCulture) : "")}"));
        return string.Join("\n", lines);
    }
}

public sealed class TextTableAnalysisService
{
    private static readonly Regex InvalidFilenameChars =
        new(@"[\x00-\x1f<>:""/\\|?*]+", RegexOptions.Compiled);

    public TextTable Read(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is not ".csv" and not ".txt" and not ".tsv")
            throw new NotSupportedException(
                "C# v0.2 的 Sand 分析区目前只把文本表格（CSV/TXT/TSV）提升为可验证实现；XLSX/XLSM 保留为待验收项。");

        var parsed = DelimitedTextReader.ReadRows(path);
        char delimiter = ext == ".tsv" ? '\t' : parsed.Delimiter;
        if (parsed.Rows.Count == 0)
            throw new InvalidDataException("表格没有数据。");

        return new TextTable(
            parsed.Rows[0],
            parsed.Rows.Skip(1).Select(x => x.ToArray()).ToList(),
            delimiter,
            ext);
    }

    public GroupAverageResult ComputeGroupAverage(
        string sourcePath,
        int groupColumnNumber,
        int valueColumnNumber,
        int decimals = 2)
    {
        TextTable table = Read(sourcePath);
        if (groupColumnNumber < 1 || groupColumnNumber > table.Headers.Length)
            throw new ArgumentOutOfRangeException(nameof(groupColumnNumber),
                $"分组列号超出范围：请输入 1～{table.Headers.Length}。");
        if (valueColumnNumber < 1 || valueColumnNumber > table.Headers.Length)
            throw new ArgumentOutOfRangeException(nameof(valueColumnNumber),
                $"统计列号超出范围：请输入 1～{table.Headers.Length}。");
        if (decimals is < 0 or > 10)
            throw new ArgumentOutOfRangeException(nameof(decimals), "小数位数请输入 0～10。");
        if (table.Rows.Count == 0)
            throw new InvalidDataException("表格只有表头，没有数据行。");

        int gi = groupColumnNumber - 1;
        int vi = valueColumnNumber - 1;
        var sums = new Dictionary<string, double>();
        var counts = new Dictionary<string, int>();
        var allGroups = new HashSet<string>();
        int skipped = 0;

        foreach (string[] row in table.Rows)
        {
            string group = gi < row.Length ? row[gi].Trim() : "";
            allGroups.Add(group);
            string valueText = vi < row.Length ? row[vi].Trim() : "";
            if (valueText.Length == 0 ||
                !double.TryParse(valueText.Replace(",", ""),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ||
                !double.IsFinite(value))
            {
                skipped++;
                continue;
            }

            sums[group] = sums.GetValueOrDefault(group) + value;
            counts[group] = counts.GetValueOrDefault(group) + 1;
        }

        if (counts.Count == 0)
            throw new InvalidDataException("指定统计列中没有可用于计算平均值的有效数字。");

        string groupName = table.Headers[gi].Trim();
        if (groupName.Length == 0) groupName = $"第{groupColumnNumber}列";
        string valueName = table.Headers[vi].Trim();
        if (valueName.Length == 0) valueName = $"第{valueColumnNumber}列";

        var resultRows = allGroups
            .OrderBy(x => x, GroupValueComparer.Instance)
            .Select(group =>
            {
                int count = counts.GetValueOrDefault(group);
                double? avg = count > 0 ? sums[group] / count : null;
                return new GroupAverageRow(group, avg, count);
            })
            .ToList();

        return new GroupAverageResult
        {
            GroupColumnName = groupName,
            ValueColumnName = valueName,
            Rows = resultRows,
            SourceRowCount = table.Rows.Count,
            ValidValueCount = counts.Values.Sum(),
            SkippedValueCount = skipped,
            Decimals = decimals
        };
    }

    public IReadOnlyList<string> Split(
        string sourcePath,
        int columnNumber,
        string outputDirectory)
    {
        TextTable table = Read(sourcePath);
        if (columnNumber < 1 || columnNumber > table.Headers.Length)
            throw new ArgumentOutOfRangeException(nameof(columnNumber),
                $"拆分列号超出范围：请输入 1～{table.Headers.Length}。");

        int index = columnNumber - 1;
        var groups = new Dictionary<string, List<string[]>>();
        foreach (string[] row in table.Rows)
        {
            string value = index < row.Length ? row[index].Trim() : "";
            if (!groups.TryGetValue(value, out var list))
                groups[value] = list = [];
            list.Add(row);
        }

        if (groups.Count == 0)
            throw new InvalidDataException("表格没有可拆分的数据行。");

        Directory.CreateDirectory(outputDirectory);
        string stem = Path.GetFileNameWithoutExtension(sourcePath);
        string header = table.Headers[index].Trim();
        if (header.Length == 0) header = $"第{columnNumber}列";
        var outputs = new List<string>();

        foreach (var pair in groups.OrderBy(x => x.Key, GroupValueComparer.Instance))
        {
            string part = SanitizeFilenamePart(pair.Key);
            string target = Path.Combine(outputDirectory,
                $"{stem}_{SanitizeFilenamePart(header)}_{part}{table.Extension}");
            WriteTextTable(target, table.Headers, pair.Value, table.Delimiter);
            outputs.Add(target);
        }
        return outputs;
    }

    public RenumberResult BuildLineRenumberMapping(
        IEnumerable<string[]> rows,
        int lineColumnIndex,
        int xColumnIndex,
        int yColumnIndex,
        string direction)
    {
        if (direction is not "从西往东" and not "从南往北")
            throw new ArgumentException("线号重排方向只能选择“从西往东”或“从南往北”。", nameof(direction));
        if (new[] { lineColumnIndex, xColumnIndex, yColumnIndex }.Distinct().Count() != 3)
            throw new ArgumentException("线号列、X 列、Y 列必须选择三个不同字段。");

        var stats = new Dictionary<string, (double SumX, double SumY, int Valid, int Rows)>();
        int blank = 0, invalid = 0;

        foreach (string[] row in rows)
        {
            string line = lineColumnIndex < row.Length ? row[lineColumnIndex].Trim() : "";
            if (line.Length == 0) { blank++; continue; }

            double x = ParseFinite(row, xColumnIndex);
            double y = ParseFinite(row, yColumnIndex);
            var s = stats.GetValueOrDefault(line);
            s.Rows++;
            if (double.IsFinite(x) && double.IsFinite(y))
            {
                s.SumX += x; s.SumY += y; s.Valid++;
            }
            else invalid++;
            stats[line] = s;
        }

        if (stats.Count == 0)
            throw new InvalidDataException("线号列中没有可用于重排的有效线号。");

        var unusable = stats.Where(x => x.Value.Valid <= 0).Select(x => x.Key).Take(8).ToArray();
        if (unusable.Length > 0)
            throw new InvalidDataException("以下线号没有任何同时有效的 X/Y 坐标，无法判断空间位置：" + string.Join("、", unusable));

        var centers = stats.Select(x => new RenumberCenter(
            x.Key,
            x.Value.SumX / x.Value.Valid,
            x.Value.SumY / x.Value.Valid,
            x.Value.Rows,
            x.Value.Valid,
            "")).ToList();

        centers = direction == "从西往东"
            ? centers.OrderBy(x => x.CenterX).ThenBy(x => x.CenterY)
                .ThenBy(x => x.OldLine, GroupValueComparer.Instance).ToList()
            : centers.OrderBy(x => x.CenterY).ThenBy(x => x.CenterX)
                .ThenBy(x => x.OldLine, GroupValueComparer.Instance).ToList();

        var formatter = InferLineFormatter(centers.Select(x => x.OldLine).ToArray());
        var mapping = new Dictionary<string, string>();
        for (int i = 0; i < centers.Count; i++)
        {
            string next = formatter.Format(i + 1);
            mapping[centers[i].OldLine] = next;
            centers[i] = centers[i] with { NewLine = next };
        }

        return new RenumberResult(mapping, centers, direction, blank, invalid, formatter.Description);
    }

    public MergeResult MergeCsv(
        IReadOnlyList<string> sourcePaths,
        string outputPath,
        string alignMode = "按列位置合并",
        bool addSourceColumn = false,
        RenumberRequest? renumber = null)
    {
        if (sourcePaths.Count < 2)
            throw new ArgumentException("请至少选择 2 个表格文件进行合并。");
        if (!Path.GetExtension(outputPath).Equals(".csv", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("C# v0.2 的可验证合并输出先限定为 CSV；XLSX 输出等待真实样本验收。");

        var tables = sourcePaths.Select(Read).ToList();
        string[] outputHeaders;
        Func<TextTable, string[], string[]> converter;
        var mismatch = new List<string>();

        if (alignMode == "按列位置合并")
        {
            int maxCols = tables.Max(t => t.Headers.Length);
            var headers = tables[0].Headers.ToList();
            while (headers.Count < maxCols)
            {
                int ci = headers.Count;
                string fallback = tables.Skip(1)
                    .Select(t => ci < t.Headers.Length ? t.Headers[ci] : "")
                    .FirstOrDefault(x => x.Trim().Length > 0)
                    ?? "";
                headers.Add(fallback.Length > 0 ? fallback : $"Column{ci + 1}");
            }
            outputHeaders = headers.ToArray();

            string[] firstNorm = tables[0].Headers.Select(x => x.Trim()).ToArray();
            foreach (TextTable t in tables.Skip(1))
                if (!t.Headers.Select(x => x.Trim()).SequenceEqual(firstNorm))
                    mismatch.Add(Path.GetFileName(sourcePaths[tables.IndexOf(t)]));

            converter = (_, row) =>
            {
                var values = row.Take(maxCols).ToList();
                while (values.Count < maxCols) values.Add("");
                return values.ToArray();
            };
        }
        else if (alignMode == "按表头名称对齐（并集）")
        {
            var union = new List<string>();
            var display = new List<string>();
            var seen = new HashSet<string>();
            var keyMaps = new Dictionary<TextTable, string[]>();

            foreach (TextTable t in tables)
            {
                string[] keys = HeaderKeys(t.Headers);
                keyMaps[t] = keys;
                for (int i = 0; i < keys.Length; i++)
                {
                    if (!seen.Add(keys[i])) continue;
                    union.Add(keys[i]);
                    string d = t.Headers[i];
                    display.Add(d.Trim().Length > 0 ? d : $"Column{display.Count + 1}");
                }
            }
            outputHeaders = display.ToArray();
            var unionIndex = union.Select((x, i) => (x, i)).ToDictionary(x => x.x, x => x.i);
            string[] firstKeys = keyMaps[tables[0]];
            foreach (TextTable t in tables.Skip(1))
                if (!keyMaps[t].SequenceEqual(firstKeys))
                    mismatch.Add(Path.GetFileName(sourcePaths[tables.IndexOf(t)]));

            converter = (t, row) =>
            {
                var values = Enumerable.Repeat("", union.Count).ToArray();
                string[] keys = keyMaps[t];
                for (int i = 0; i < keys.Length && i < row.Length; i++)
                    values[unionIndex[keys[i]]] = row[i];
                return values;
            };
        }
        else
        {
            throw new ArgumentException("未知的合并方式。", nameof(alignMode));
        }

        var aligned = new List<(TextTable Table, string Source, string[] Row)>();
        for (int ti = 0; ti < tables.Count; ti++)
            aligned.AddRange(tables[ti].Rows.Select(row =>
                (tables[ti], Path.GetFileName(sourcePaths[ti]), converter(tables[ti], row))));

        RenumberResult? renumberInfo = null;
        if (renumber is not null)
            renumberInfo = BuildLineRenumberMapping(
                aligned.Select(x => x.Row),
                renumber.LineColumnIndex,
                renumber.XColumnIndex,
                renumber.YColumnIndex,
                renumber.Direction);

        var finalHeaders = outputHeaders.ToList();
        if (addSourceColumn)
        {
            string sourceHeader = "来源文件";
            int n = 2;
            while (finalHeaders.Any(x => x.Equals(sourceHeader, StringComparison.OrdinalIgnoreCase)))
                sourceHeader = "来源文件" + n++;
            finalHeaders.Add(sourceHeader);
        }

        var finalRows = new List<string[]>();
        foreach (var item in aligned)
        {
            string[] values = item.Row.ToArray();
            if (renumberInfo is not null)
            {
                int li = renumber!.LineColumnIndex;
                string old = li < values.Length ? values[li].Trim() : "";
                if (old.Length > 0 && renumberInfo.Mapping.TryGetValue(old, out string? newer))
                    values[li] = newer;
            }

            if (addSourceColumn)
                values = values.Concat([item.Source]).ToArray();
            finalRows.Add(values);
        }

        WriteTextTable(outputPath, finalHeaders.ToArray(), finalRows, ',');
        return new MergeResult(
            Path.GetFullPath(outputPath),
            finalRows.Count,
            mismatch,
            renumberInfo);
    }

    private static string SanitizeFilenamePart(string? value)
    {
        string text = (value ?? "").Trim();
        if (text.Length == 0) text = "空值";
        text = InvalidFilenameChars.Replace(text, "_");
        text = Regex.Replace(text, @"\s+", " ").Trim(' ', '.');
        if (text.Length == 0) text = "空值";

        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1","COM2","COM3","COM4","COM5","COM6","COM7","COM8","COM9",
            "LPT1","LPT2","LPT3","LPT4","LPT5","LPT6","LPT7","LPT8","LPT9"
        };
        if (reserved.Contains(text)) text = "_" + text;
        if (text.Length > 90) text = text[..90].TrimEnd(' ', '.');
        return text.Length > 0 ? text : "空值";
    }

    private static void WriteTextTable(string path, string[] headers, IEnumerable<string[]> rows, char delimiter)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        writer.WriteLine(string.Join(delimiter, headers.Select(x => Escape(x, delimiter))));
        foreach (string[] row in rows)
            writer.WriteLine(string.Join(delimiter, row.Select(x => Escape(x, delimiter))));
    }

    private static string Escape(string value, char delimiter)
    {
        string s = value ?? "";
        if (s.Contains('"')) s = s.Replace("\"", "\"\"");
        return s.IndexOfAny([delimiter, '"', '\r', '\n']) >= 0 ? "\"" + s + "\"" : s;
    }

    private static double ParseFinite(string[] row, int index)
    {
        if (index < 0 || index >= row.Length) return double.NaN;
        return double.TryParse(row[index].Trim().Replace(",", ""),
            NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v)
            ? v : double.NaN;
    }

    private static string[] HeaderKeys(string[] headers)
    {
        var counts = new Dictionary<string, int>();
        var keys = new string[headers.Length];
        for (int i = 0; i < headers.Length; i++)
        {
            string baseKey = headers[i].Trim().ToLowerInvariant();
            if (baseKey.Length == 0) baseKey = $"__blank_{i + 1}";
            int n = counts.GetValueOrDefault(baseKey) + 1;
            counts[baseKey] = n;
            keys[i] = n == 1 ? baseKey : $"{baseKey}__{n}";
        }
        return keys;
    }

    private static LineFormatter InferLineFormatter(IReadOnlyList<string> values)
    {
        var matches = values.Select(v => Regex.Match(v.Trim(), @"(?i)^(?<prefix>L\s*)?(?<sign>[+-]?)(?<num>\d+)$")).ToArray();
        if (matches.All(m => m.Success))
        {
            bool hasL = matches.All(m => m.Groups["prefix"].Success);
            int width = matches.Max(m => m.Groups["num"].Value.Length);
            return new LineFormatter(
                n => (hasL ? "L" : "") + n.ToString(new string('0', width), CultureInfo.InvariantCulture),
                hasL ? $"L 前缀 + {width} 位数字" : $"{width} 位数字");
        }
        return new LineFormatter(n => n.ToString(CultureInfo.InvariantCulture), "整数");
    }

    private sealed record LineFormatter(Func<int, string> Formatter, string Description)
    {
        public string Format(int n) => Formatter(n);
    }
}

public sealed record RenumberCenter(
    string OldLine, double CenterX, double CenterY, int RowCount, int ValidXyCount, string NewLine);

public sealed record RenumberResult(
    Dictionary<string, string> Mapping,
    List<RenumberCenter> Centers,
    string Direction,
    int BlankLineRows,
    int InvalidXyRows,
    string FormatDescription);

public sealed record RenumberRequest(
    int LineColumnIndex,
    int XColumnIndex,
    int YColumnIndex,
    string Direction);

public sealed record MergeResult(
    string OutputPath,
    int RowCount,
    List<string> HeaderMismatchFiles,
    RenumberResult? RenumberInfo);

internal sealed class GroupValueComparer : IComparer<string>
{
    public static readonly GroupValueComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        string a = (x ?? "").Trim();
        string b = (y ?? "").Trim();

        var ka = Key(a);
        var kb = Key(b);
        int c = ka.Kind.CompareTo(kb.Kind);
        if (c != 0) return c;
        if (ka.Kind == 0)
        {
            c = ka.Number.CompareTo(kb.Number);
            if (c != 0) return c;
        }
        c = string.Compare(ka.Text, kb.Text, StringComparison.OrdinalIgnoreCase);
        return c != 0 ? c : string.CompareOrdinal(a, b);
    }

    private static (int Kind, double Number, string Text) Key(string value)
    {
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double n))
            return (0, n, value);
        Match m = Regex.Match(value, @"(?i)^\s*L\s*([+-]?\d+(?:\.\d+)?)\s*$");
        if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out n))
            return (0, n, value);
        return (1, 0, value);
    }
}
