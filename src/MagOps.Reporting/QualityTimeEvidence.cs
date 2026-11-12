using System.Globalization;
using MagOps.Core.Analysis;

namespace MagOps.Reporting;

public sealed record QualityTimeEvidence(double?[] Seconds, CadenceEvidence Cadence, string Mode);

public static class QualityTimeReader
{
    public static string SegmentKey(string[] row, int width, QualityColumns columns)
    {
        if (row.Length != width) return "";
        string Cell(int? c) => c is { } i && i >= 0 && i < width ? row[i].Trim() : "";
        string segment = columns.Segment is null ? "FILE" : Cell(columns.Segment);
        if (segment.Length == 0) return "";
        if (columns.Task is null) return segment;
        string task = Cell(columns.Task);
        // Length prefix prevents two different task/Pass combinations sharing a key.
        return task.Length == 0 ? "" : task.Length + ":" + task + "/" + segment;
    }
    public static QualityTimeEvidence Read(TextTable table, QualityColumns columns, int first = 1, int? last = null)
    {
        string Cell(string[] row, int? col) => row.Length == table.Headers.Length && col is { } i && i >= 0 && i < row.Length ? row[i].Trim() : "";
        var rows = table.Rows.Skip(first - 1).Take((last ?? table.Rows.Count) - first + 1).ToArray();
        double? Parse(string[] row)
        {
            if (columns.RelativeSeconds is not null)
                return double.TryParse(Cell(row, columns.RelativeSeconds), NumberStyles.Float, CultureInfo.InvariantCulture, out double n) && double.IsFinite(n) ? n : null;
            string text = Cell(row, columns.Timestamp);
            // Require a full calendar date; ambiguous mm:ss and date-free midnight are not guessed.
            string[] formats = ["yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK", "yyyy/MM/dd'T'HH:mm:ss.FFFFFFFK", "yyyy-MM-dd HH:mm:ss.FFFFFFFK", "yyyy/MM/dd HH:mm:ss.FFFFFFFK", "yyyyMMdd'T'HHmmss.FFFFFFF"];
            bool ParseDate(string value, out DateTimeOffset date) => DateTimeOffset.TryParseExact(value, formats, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out date);
            return ParseDate(text, out var time) || columns.Date is not null && ParseDate(Cell(row, columns.Date) + "T" + text, out time)
                ? (time - DateTimeOffset.UnixEpoch).TotalSeconds : null;
        }
        var seconds = rows.Select(Parse).ToArray();
        var segments = rows.Select(r => SegmentKey(r, table.Headers.Length, columns)).ToArray();
        return new(seconds, SamplingCadence.Analyze(seconds, segments), columns.RelativeSeconds is not null ? "相对秒" : columns.Timestamp is not null ? "完整日期时刻（只用于间隔，不认定原始时区）" : "无可用时间列");
    }
}
