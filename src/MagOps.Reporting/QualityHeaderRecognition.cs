namespace MagOps.Reporting;

public sealed record QualityHeaderSuggestion(string Field, int? Column, int[] Candidates, string Note);

/// <summary>Names suggest fields; explicit nT suffixes describe units, never continuity or datum.</summary>
public static class QualityHeaderRecognition
{
    private static readonly Dictionary<string, string[]> Aliases = new()
    {
        ["mag"] = ["RawMagNt", "MagNt", "Mag", "Magnetic", "磁场", "磁场值", "原始磁场", "原始磁场nT", "磁场nT", "原始磁通道", "磁总场", "总磁场", "T"],
        ["segment"] = ["LinePass", "Pass", "Segment", "SegmentId", "LegId", "采集段", "采集段号", "航段号", "段号"],
        ["time"] = ["RelativeSeconds", "ElapsedSeconds", "Seconds", "TimeSeconds", "相对时间秒", "相对秒", "相对时间", "经过秒数"],
        ["timestamp"] = ["Timestamp", "TimestampUtc", "DateTime", "DateTimeUtc", "Utc", "TimeUtc", "日期时间", "时间戳", "采集时间", "时刻", "时间"],
        ["date"] = ["DateUtc", "Date", "日期", "采集日期"],
        ["mother"] = ["MotherId", "MotherLineId", "MotherLine", "LineName", "Line", "LineNo", "测线", "测线号", "母测线", "母线号"],
        ["task"] = ["LineTaskId", "TaskId", "MissionId", "任务ID", "架次ID", "采集任务"],
        ["source"] = ["SourceFile", "SourcePath", "RawSourceFile", "SourceId", "来源文件", "原始文件", "来源标识"],
        ["sourceRecord"] = ["SourceRecord", "SourceRow", "RawRecordIndex", "原始行号", "源记录号", "原始记录号"],
        ["lon"] = ["Longitude", "Lon", "Lng", "经度", "经度度"],
        ["lat"] = ["Latitude", "Lat", "纬度", "纬度度"],
        ["alt"] = ["AltitudeM", "Altitude", "ElevationM", "Elevation", "海拔", "海拔高度", "高程", "高程米", "实测高程", "实测高程米", "飞行海拔"]
    };

    private static string Normalize(string text) => new(text.Trim().TrimStart('\uFEFF')
        .Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    public static bool HasExplicitNtUnit(string header)
    {
        string normalized = Normalize(header);
        if (normalized is "rawmagnt" or "magnt" or "原始磁场nt" or "磁场nt") return true;
        return System.Text.RegularExpressions.Regex.IsMatch(header.Trim(), @"(?:\(\s*nT\s*\)|（\s*nT\s*）|\[\s*nT\s*\]|\s+nT)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    public static QualityHeaderSuggestion[] Suggest(IReadOnlyList<string> headers) => Aliases.Select(pair =>
    {
        var names = pair.Value.Select(Normalize).ToHashSet();
        int[] candidates = Enumerable.Range(0, headers.Count).Where(i => names.Contains(Normalize(headers[i]))).ToArray();
        if (pair.Key == "mag")
        {
            var raw = new[] { "RawMagNt", "原始磁场", "原始磁场nT", "原始磁通道" }.Select(Normalize).ToHashSet();
            int[] preferred = Enumerable.Range(0, headers.Count).Where(i => raw.Contains(Normalize(headers[i])) ||
                Normalize(headers[i]).StartsWith("rawmagnt", StringComparison.Ordinal)).ToArray();
            if (preferred.Length > 0) candidates = preferred;
        }
        return new QualityHeaderSuggestion(pair.Key, candidates.Length == 1 ? candidates[0] : null, candidates,
            candidates.Length switch
            {
                1 => "已按表头识别，仍需核对含义和单位",
                > 1 => "多个候选，请手动选择：" + string.Join("、", candidates.Select(i => $"{i + 1}. {headers[i]}")),
                _ => "未识别，请按需手动选择"
            });
    }).ToArray();
}
