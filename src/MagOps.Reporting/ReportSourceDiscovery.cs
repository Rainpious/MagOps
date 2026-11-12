using System.Text.Json;
using MagOps.Infrastructure.Services;
using MagOps.Infrastructure.Storage;
using Microsoft.VisualBasic.FileIO;

namespace MagOps.Reporting;

public sealed record ReportDiscoveryResult(ReportInputs Inputs, string Explanation);

/// <summary>Discover existing evidence without running processing or changing source files.</summary>
public static class ReportSourceDiscovery
{
    public static ReportDiscoveryResult Resolve(string root, string area, string areaId, ReportInputs inputs,
        string? assessmentRoot = null)
    {
        _ = ReportInputStore.DirectoryFor(root, area);
        string areaRoot = Path.GetFullPath(Path.Combine(root, area));
        var project = new WorkspaceService(new JsonStore()).GetWorkspaceReadOnly(root);
        var notes = new List<string>();
        inputs = inputs with
        {
            ProjectName = string.IsNullOrWhiteSpace(inputs.ProjectName) ? project.DisplayName : inputs.ProjectName,
            WorkareaName = string.IsNullOrWhiteSpace(inputs.WorkareaName)
                ? new WorkspaceService(new JsonStore()).GetWorkareas(root).FirstOrDefault(x => x.WorkareaId == areaId)?.DisplayName is { Length: > 0 } areaName
                ? areaName : Path.GetFileName(areaRoot) : inputs.WorkareaName,
            ReportDate = string.IsNullOrWhiteSpace(inputs.ReportDate) ? DateTime.Today.ToString("yyyy-MM-dd") : inputs.ReportDate
        };
        var layout = new FolderLayoutService();
        string processing = layout.ResolveWorkareaFolder(root, area, "workarea.processing");
        // Directory/header discovery is bounded; full hashes and CSV statistics belong to Check/Generate.
        var files = Walk(processing, notes).ToArray();
        bool autoCsv = string.IsNullOrWhiteSpace(inputs.ProductionCsv) ||
            inputs.ProductionCsv.Equals(inputs.AutoProductionCsv, StringComparison.OrdinalIgnoreCase);
        if (autoCsv)
        {
            var candidates = new List<(string Path, DateTime Time)>();
            foreach (string file in files.Where(f => f.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)))
            {
                string name = Path.GetFileName(file);
                if (!name.EndsWith("__MagOps_prelevel_dtgz.csv", StringComparison.OrdinalIgnoreCase) &&
                    !name.EndsWith("__MagOps_prelevel_dtgz_REVIEW_DRAFT.csv", StringComparison.OrdinalIgnoreCase) &&
                    !name.Equals("MagOps_日变磁测_处理用.csv", StringComparison.OrdinalIgnoreCase) &&
                    !name.Equals("MagOps_报告正式测线.csv", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    DateTime time = File.GetLastWriteTimeUtc(file);
                    string sidecar = file + GeneratedArtifactIdentity.Suffix;
                    if (File.Exists(sidecar))
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(sidecar));
                        var m = doc.RootElement;
                        string Get(string key) => m.TryGetProperty(key, out var v) ? v.GetString() ?? "" : "";
                        if (Get("producer") != "MagOps" || !Get("processing_stage").StartsWith("prelevel_dtgz", StringComparison.Ordinal) ||
                            (Get("project_id").Length > 0 && Get("project_id") != project.ProjectId) ||
                            (Get("workarea_id").Length > 0 && Get("workarea_id") != areaId)) continue;
                        if (m.TryGetProperty("generated_at_utc", out var at) && at.ValueKind == JsonValueKind.String && at.TryGetDateTimeOffset(out var generated)) time = generated.UtcDateTime;
                    }
                    using var reader = new TextFieldParser(file, System.Text.Encoding.UTF8, true);
                    reader.SetDelimiters(","); reader.HasFieldsEnclosedInQuotes = true;
                    var header = (reader.ReadFields() ?? []).Select(x => x.Trim().Trim('\uFEFF')).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    if (!new[] { "LineName", "LinePass", "LineTaskId", "Longitude", "Latitude", "DateUtc", "TimeUtc", "AltitudeM" }.All(header.Contains) || reader.EndOfData) continue;
                    candidates.Add((file, time));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or MalformedLineException)
                { notes.Add("跳过无法读取的处理成果：" + Path.GetFileName(file)); }
            }
            string selected = candidates.OrderByDescending(x => Path.GetFileName(x.Path).Equals("MagOps_报告正式测线.csv", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(x => x.Time).ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase).FirstOrDefault().Path ?? "";
            inputs = inputs with { ProductionCsv = selected, AutoProductionCsv = selected };
            notes.Add(selected.Length == 0 ? "未找到非空处理用CSV，请先保存核验输出，或手动选择。" :
                $"自动选择报告来源CSV（{candidates.Count}份候选，优先正式测线原始磁数据、同类按生成时间取最新；完整性仍需检查）：{selected}");
        }
        else notes.Add(File.Exists(inputs.ProductionCsv) ? "保留手动指定的处理用CSV。" : "手动指定的CSV已不存在，请重新选择或清空后重新识别。");

        bool autoAssessment = string.IsNullOrWhiteSpace(inputs.AssessmentDirectory) ||
            inputs.AssessmentDirectory.Equals(inputs.AutoAssessmentDirectory, StringComparison.OrdinalIgnoreCase);
        if (autoAssessment)
        {
            assessmentRoot ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MagOps", "flight-quality-assessments");
            var manifests = files.Where(f => Path.GetFileName(f) == "manifest.json")
                .Concat(Walk(layout.ResolveWorkareaFolder(root, area, "workarea.field"), notes).Where(f => Path.GetFileName(f) == "manifest.json"))
                .Concat(Walk(assessmentRoot, notes).Where(f => Path.GetFileName(f) == "manifest.json"));
            var batches = new List<(string Path, DateTime Time)>();
            foreach (string manifest in manifests.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
                    var m = doc.RootElement;
                    if (!m.TryGetProperty("SchemaVersion", out var schema) || schema.GetString() != FlightQualityAssessmentService.SchemaVersion ||
                        !m.TryGetProperty("Complete", out var complete) || complete.ValueKind != JsonValueKind.True) continue;
                    string directory = Path.GetDirectoryName(manifest)!;
                    using var parameters = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "input-and-parameters.json")));
                    if (!parameters.RootElement.TryGetProperty("SourceDirectory", out var source) ||
                        source.GetString() is not { Length: > 0 } sourcePath || !Within(sourcePath, areaRoot)) continue;
                    if (!File.Exists(Path.Combine(directory, "summary.json"))) continue;
                    batches.Add((directory, File.GetLastWriteTimeUtc(manifest)));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException) { }
            }
            string selected = batches.OrderByDescending(x => x.Time).ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase).FirstOrDefault().Path ?? "";
            inputs = inputs with { AssessmentDirectory = selected, AutoAssessmentDirectory = selected };
            notes.Add(selected.Length == 0 ? "未找到来源属于当前工区的完整飞行评估；可留空或手动选择。" :
                "自动选择当前工区最新完整评估（独立参考，不能证明与最终CSV选择范围相同）：" + selected);
        }
        else notes.Add("保留手动指定的飞行评估目录。");
        return new(inputs, string.Join(Environment.NewLine, notes));
    }

    private static bool Within(string path, string parent) => Path.IsPathFullyQualified(path) &&
        (Path.GetFullPath(path).TrimEnd('\\', '/').Equals(parent, StringComparison.OrdinalIgnoreCase) ||
        Path.GetFullPath(path).StartsWith(parent.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> Walk(string root, List<string> notes)
    {
        if (!Directory.Exists(root)) yield break;
        var pending = new Queue<(string Path, int Depth)>(); pending.Enqueue((root, 0));
        int visited = 0;
        while (pending.TryDequeue(out var entry))
        {
            if (++visited > 3000) { notes.Add("目录较多，自动查找已达上限；可手动选择遗漏的成果。"); yield break; }
            string[] files, directories;
            try { files = Directory.GetFiles(entry.Path); directories = Directory.GetDirectories(entry.Path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (string file in files) yield return file;
            if (entry.Depth >= 6) continue;
            foreach (string dir in directories)
            {
                string name = Path.GetFileName(dir);
                if (name.Contains(".previous", StringComparison.OrdinalIgnoreCase) || name.Contains("backup", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("备份", StringComparison.Ordinal) || name.Contains(".tmp", StringComparison.OrdinalIgnoreCase) || name.StartsWith('.')) continue;
                FileAttributes attributes;
                try { attributes = File.GetAttributes(dir); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                if ((attributes & FileAttributes.ReparsePoint) == 0) pending.Enqueue((dir, entry.Depth + 1));
            }
        }
    }
}
