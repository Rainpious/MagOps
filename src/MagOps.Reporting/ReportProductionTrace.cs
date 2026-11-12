using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.VisualBasic.FileIO;

namespace MagOps.Reporting;

internal static class ReportProductionTrace
{
    public static IReadOnlyList<ReportSurveyPoint> Attach(IReadOnlyList<ReportSurveyPoint> points,
        List<ReportSource> sources, List<ReportFact> facts, List<string> warnings)
    {
        var manifest = sources.SingleOrDefault(s => s.Role == "processing-manifest");
        if (manifest is null) { warnings.Add("缺少唯一生产清单，未建立逐点源记录映射；连续片段还需人工核对。"); return points; }
        using var doc = JsonDocument.Parse(File.ReadAllText(manifest.Path));
        var root = doc.RootElement;
        if (!root.TryGetProperty("production_trace_file", out var traceName))
        { warnings.Add("生产清单未提供逐点追溯表，未建立源记录映射。"); return points; }
        string name = traceName.GetString() ?? "";
        if (string.IsNullOrWhiteSpace(name) || Path.GetFileName(name) != name || name.Contains('/') || name.Contains('\\'))
            throw new InvalidDataException("生产追溯表路径非法。");
        string path = Path.Combine(Path.GetDirectoryName(manifest.Path)!, name);
        var identities = root.GetProperty("artifact_identities").EnumerateArray()
            .Where(i => i.GetProperty("file").GetString() == name).ToArray();
        if (identities.Length != 1) throw new InvalidDataException("生产清单缺少唯一追溯表哈希。");
        string hash = identities[0].GetProperty("content_sha256").GetString() ?? "";
        if (!ReportSnapshotBuilder.Hash(path).Equals(hash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("生产追溯表哈希校验失败。");
        using var stream = File.OpenRead(path);
        using Stream unpacked = name.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? new GZipStream(stream, CompressionMode.Decompress) : stream;
        using var reader = new TextFieldParser(unpacked, System.Text.Encoding.UTF8, true);
        reader.SetDelimiters(","); reader.HasFieldsEnclosedInQuotes = true;
        var header = reader.ReadFields() ?? [];
        var columns = header.Select((h, i) => (Name: h.Trim().Trim('\uFEFF'), Index: i)).ToDictionary(x => x.Name, x => x.Index, StringComparer.Ordinal);
        foreach (string required in new[] { "SourceId", "SourceRecord", "ProductionStatus", "CsvRow", "DateUtc", "TimeUtc", "LinePass", "SnapshotFingerprint" })
            if (!columns.ContainsKey(required)) throw new InvalidDataException("生产追溯表缺少字段：" + required);
        string Cell(string[] row, string key) => row[columns[key]];
        var byRow = points.ToDictionary(p => p.CsvRow);
        var seen = new HashSet<(string Source, int Record)>();
        var result = new Dictionary<long, ReportSurveyPoint>();
        string expectedFingerprint = root.GetProperty("production_snapshot_fingerprint").GetString() ?? "";
        if (string.IsNullOrWhiteSpace(expectedFingerprint)) throw new InvalidDataException("生产清单缺少选择快照指纹。");
        while (!reader.EndOfData)
        {
            var row = reader.ReadFields() ?? [];
            if (row.Length != header.Length) throw new InvalidDataException("生产追溯表列数错误。");
            if (Cell(row, "ProductionStatus") != "EXPORTED") continue;
            if (!long.TryParse(Cell(row, "CsvRow"), out long csvRow) || !byRow.TryGetValue(csvRow, out var point) || result.ContainsKey(csvRow))
                throw new InvalidDataException("生产追溯表CSV行号重复或超出范围。");
            string source = Cell(row, "SourceId");
            if (string.IsNullOrWhiteSpace(source) || !int.TryParse(Cell(row, "SourceRecord"), out int record) || record < 0 || !seen.Add((source, record)))
                throw new InvalidDataException("生产追溯源记录身份重复或无效。");
            if (Cell(row, "SnapshotFingerprint") != expectedFingerprint || Cell(row, "LinePass") != point.Pass ||
                !DateTimeOffset.TryParse(Cell(row, "DateUtc") + "T" + Cell(row, "TimeUtc"), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time) || time != point.Time)
                throw new InvalidDataException("生产追溯表与CSV时间、Pass或选择快照不一致。");
            result.Add(csvRow, point with { SourceId = source, SourceRecord = record });
        }
        if (result.Count != points.Count) throw new InvalidDataException("生产追溯表未完整覆盖CSV行。");
        if (ReportSnapshotBuilder.Hash(path) != hash || ReportSnapshotBuilder.Hash(manifest.Path) != manifest.Sha256)
            throw new IOException("读取时生产追溯来源发生变化。");
        sources.Add(new("production-trace", path, hash));
        facts.Add(new("生产逐点追溯", result.Count.ToString(), "点", "AVAILABLE", "production-trace;processing-manifest",
            "CSV/追溯表同属唯一哈希匹配清单，逐行核对UTC、Pass、选择指纹及SourceId/SourceRecord唯一性"));
        return points.Select(p => result[p.CsvRow]).ToArray();
    }
}
