using System.Text.Json;

namespace MagOps.Reporting;

public static class ReportFreshness
{
    public static string CheckLatest(string root, string area, ReportInputs current)
    {
        string directory = ReportInputStore.DirectoryFor(root, area);
        if (!Directory.Exists(directory)) return "尚未生成报告。";
        var latest = Directory.EnumerateDirectories(directory, "草稿_*").OrderByDescending(Path.GetFileName, StringComparer.Ordinal).FirstOrDefault();
        if (latest is null) return "尚未生成报告。";
        try
        {
            string snapshotPath = Path.Combine(latest, "snapshot.json");
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(latest, "manifest.json")));
            if (!manifest.RootElement.GetProperty("Complete").GetBoolean()) return "已有报告无法核对：报告包未完成。";
            var entries = manifest.RootElement.GetProperty("Files").EnumerateArray().Where(f => f.GetProperty("File").GetString() == "snapshot.json").ToArray();
            if (entries.Length != 1 || !ReportSnapshotBuilder.Hash(snapshotPath).Equals(entries[0].GetProperty("Sha256").GetString(), StringComparison.OrdinalIgnoreCase))
                return "已有报告无法核对：历史快照完整性校验失败。";
            var snapshot = JsonSerializer.Deserialize<ReportSnapshot>(File.ReadAllText(snapshotPath), ReportInputStore.Json)
                ?? throw new InvalidDataException("快照为空。");
            var changed = new List<string>(); var unreadable = new List<string>();
            foreach (var source in snapshot.Sources)
            {
                try
                {
                    if (!ReportSnapshotBuilder.Hash(source.Path).Equals(source.Sha256, StringComparison.OrdinalIgnoreCase))
                        changed.Add(source.Role + "：" + Path.GetFileName(source.Path));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { unreadable.Add(source.Role + "：" + Path.GetFileName(source.Path)); }
            }
            ReportInputs Comparable(ReportInputs input) => input with { AutoProductionCsv = "", AutoAssessmentDirectory = "" };
            if (JsonSerializer.Serialize(Comparable(current), ReportInputStore.Json) != JsonSerializer.Serialize(Comparable(snapshot.Inputs), ReportInputStore.Json))
                changed.Add("报告资料、来源选择、母线/检查配对或质量参数");
            if (changed.Count > 0 || unreadable.Count > 0)
                return "报告未实时更新。" + (changed.Count > 0 ? "已变化：" + string.Join("；", changed) + "。" : "") +
                    (unreadable.Count > 0 ? "无法核对（缺失或不可读）：" + string.Join("；", unreadable) + "。" : "") +
                    "旧稿与快照保留，请检查后显式重新生成。";
            return "已核对最近报告的选用来源与当前资料，未发现变化；质量及签审仍需人工确认。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException)
        { return "已有报告无法核对：" + ex.Message; }
    }
}
