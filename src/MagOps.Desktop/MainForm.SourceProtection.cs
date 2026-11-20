using System.Text.Json;
using MagOps.Application;
using MagOps.Core.Domain;
using MagOps.Infrastructure.Services;
using MagOps.Infrastructure.Storage;

namespace MagOps.Desktop;

public sealed partial class MainForm
{
    private IReadOnlyList<ArtifactDependency>? _fieldExportAnalysisDependencies;
    private string _fieldExportAnalysisContext = "";

    private ArtifactDependency ExportRecord<T>(string table, string idField, string id, T value, string[] fields) =>
        ArtifactDependencies.Records(Root, ProjectPaths.Json(Root, table), new[] { value },
            new() { [idField] = id }, fields: fields);

    private bool ExportProtected(string path, string stage, IReadOnlyList<ArtifactDependency> dependencies,
        object options, Action<string> write, bool projectLevel = false)
    {
        if (!IsPathInsideCurrentProject(path)) {
            string folder = projectLevel ? _folderLayout.ResolveProjectFolder(Root, "project.delivery")
                : _folderLayout.EnsureOperationDirectory(Root, "processing_analysis", CurrentWorkareaRelativePath);
            Directory.CreateDirectory(folder);
            string retained = GlobalWorkspaceService.UniqueFile(Path.Combine(folder, Path.GetFileName(path)));
            if (!ExportProtected(retained, stage, dependencies, options, write, projectLevel)) return false;
            ArtifactCopyService.Copy(retained, path);
            RememberLatestGeneratedBusinessFile(path);
            SetStatus("已另存成果及来源记录：" + path);
            return true;
        }
        if (File.Exists(path))
        {
            DialogResult choice = MessageBox.Show(this,
                "目标成果已存在。是：覆盖（失败时恢复旧成果）；否：另存新版本；取消：不写入。",
                "成果覆盖", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (choice == DialogResult.Cancel) return false;
            if (choice == DialogResult.No)
            {
                using var dialog = new SaveFileDialog { Title = "另存成果新版本", InitialDirectory = Path.GetDirectoryName(path),
                    FileName = Path.GetFileNameWithoutExtension(path) + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + Path.GetExtension(path),
                    Filter = "同类型成果|*" + Path.GetExtension(path), OverwritePrompt = true };
                FileInteractions.For(this, "protected-" + stage, "output", projectLevel).Prepare(dialog, current: path);
                if (dialog.ShowDialog(this) != DialogResult.OK) return false;
                path = dialog.FileName;
            }
        }
        if (!IsPathInsideCurrentProject(path)) return ExportProtected(path, stage, dependencies, options, write, projectLevel);
        var ws = _workspace.GetWorkspace(Root);
        string workareaId = projectLevel ? "" : _workspace.GetWorkareas(Root)
            .FirstOrDefault(x => x.Path.Equals(CurrentWorkareaRelativePath, StringComparison.OrdinalIgnoreCase))?.WorkareaId ?? "";
        var context = new GeneratedArtifactContext(ws.ProjectId, workareaId);
        var result = new IndependentArtifactWorkflow(_store, _artifacts).Execute(
            new(Root, path, stage, context, dependencies, options), write);
        RememberLatestGeneratedBusinessFile(path);
        if (_artifactWorkspace.HasGridColumns()) ApplyArtifactChanges([result]);
        TransientNotice.Show(this, $"成果及来源记录已保存：{Path.GetFileName(path)}", "导出完成");
        return true;
    }

    private static readonly string[] FlightExportFields =
        ["batch_id", "date", "workarea", "aircraft", "magnetometer", "base_station", "route_version", "operator",
         "start", "end", "status", "result", "raw_artifacts", "issues", "notes", "task_lock_warning", "updated_at"];
    private static readonly string[] StationExportFields =
        ["record_id", "date", "station_id", "site", "longitude", "latitude", "elevation_m", "crs", "time_basis",
         "sampling_interval_s", "start", "end", "status", "artifacts", "notes", "task_lock_warning", "updated_at"];
    private static readonly string[] DailyLogExportFields =
        ["log_id", "date", "weather", "site", "completed_tasks", "new_files", "anomalies", "next_actions", "handoff_status", "notes", "updated_at"];
    private static readonly string[] IssueExportFields =
        ["issue_id", "title", "severity", "impact", "temporary_measure", "owner", "status", "resolution_evidence", "notes", "updated_at"];

    private IReadOnlyList<ArtifactDependency> CaptureFieldAnalysisSources(FieldRouteSnapshot route, DateTime date, string workareaName)
    {
        var dependencies = new List<ArtifactDependency>();
        string workareaRoot = Path.Combine(Root, CurrentWorkareaRelativePath);
        dependencies.Add(ArtifactDependencies.SourceSet(Root, workareaRoot, date.ToString("yyyy-MM-dd")));
        if (_fieldMagDataAnalysis is { } data)
        {
            dependencies.Add(ArtifactDependencies.SourceSet(Root, data.SourceDirectory));
            dependencies.AddRange(data.Files.Select(x => ArtifactDependencies.File(Root, x.Path,
                string.IsNullOrEmpty(x.SourceContentSha256) ? "MISSING_PARSED_HASH" : x.SourceContentSha256)));
        }
        foreach (string file in FieldOperationsAnalysisService.DailySourcePaths(workareaRoot, date)
                     .Concat(_fieldManualGsm19TFiles).Concat(_fieldManualUavFiles).Distinct(StringComparer.OrdinalIgnoreCase))
            dependencies.Add(ArtifactDependencies.File(Root, file));
        foreach (var sortie in route.Sorties.Where(x => x.SourceFile.Length > 0))
            dependencies.Add(ArtifactDependencies.File(Root, sortie.SourceFile,
                string.IsNullOrEmpty(sortie.SourceSha256) ? null : sortie.SourceSha256));
        if (File.Exists(route.SourcePath)) dependencies.Add(ArtifactDependencies.File(Root, route.SourcePath));
        foreach (var repeat in route.RepeatedMeasurements)
            dependencies.Add(ArtifactDependencies.File(Root, repeat.SourceFile, repeat.SourceSha256));
        dependencies.Add(ArtifactDependencies.Object(Root, ProjectPaths.ExistingProject(Root), ["time_zone_id"]));
        dependencies.Add(ArtifactDependencies.Query(Root, ProjectPaths.Json(Root, "field_operations_settings.json"),
            new() { ["workarea_path"] = CurrentWorkareaRelativePath.Replace('\\', '/').Trim('/') },
            fields: ["workarea_path", "expected_daily_sorties", "accepted_relocations", "field_data_folder"]));
        dependencies.Add(ArtifactDependencies.Query(Root, ProjectPaths.Json(Root, "field_daily_progress.json"),
            new() { ["workarea"] = workareaName }, date.AddDays(-1).ToString("yyyy-MM-dd"), ProgressDependencyFields));
        // DJI input rows affect the progress record, but registry scans and export registration do not.
        dependencies.Add(ArtifactDependencies.Query(Root, ProjectPaths.Json(Root, "dji_flight_sessions.json"),
            new() { ["workarea_path"] = CurrentWorkareaRelativePath.Replace('\\', '/').Trim('/') },
            fields: ["session_id", "source_file", "source_sha256", "internal_start_utc", "internal_end_utc", "duration_seconds", "total_distance_km"],
            timestampDateField: "internal_start_utc", dateEquals: date.ToString("yyyy-MM-dd"),
            timeZoneId: _fieldMagDataAnalysis?.ProjectTimeZoneId ?? "Asia/Shanghai"));
        return dependencies;
    }

    // These are the persisted facts consumed by progress calculations/reports; timestamps
    // added by saving an otherwise identical record must not invalidate its consumers.
    private static readonly string[] ProgressDependencyFields = typeof(FieldDailyProgressRecord).GetProperties()
        .Select(p => p.GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonPropertyNameAttribute), false)
            .Cast<System.Text.Json.Serialization.JsonPropertyNameAttribute>().SingleOrDefault()?.Name)
        .Where(x => x is not null and not "created_at" and not "updated_at").Cast<string>().ToArray();

    private IReadOnlyList<ArtifactDependency> ProgressExportSources(bool projectReport = false)
    {
        var row = RequireCurrentFieldProgressRecord();
        if (_fieldExportAnalysisDependencies is null || _fieldExportAnalysisContext != Path.GetFullPath(Root) + "|" + CurrentWorkareaRelativePath)
            throw new InvalidOperationException("分析来源尚未记录，请重新扫描并分析。");
        var status = ArtifactDependencies.Check(Root, _fieldExportAnalysisDependencies);
        if (status.Status != "CURRENT") throw new InvalidOperationException("分析来源已变化，请重新扫描：" + status.Reason);
        var result = _fieldExportAnalysisDependencies.ToList();
        if (projectReport)
        {
            result.Add(ArtifactDependencies.Records(Root, ProjectPaths.Json(Root, "field_daily_progress.json"),
                CurrentFieldProjectProgressRows(row), new() { ["workarea"] = row.Workarea,
                    ["route_fingerprint"] = row.RouteFingerprint, ["progress_time_zone"] = row.ProgressTimeZone },
                row.Date, ProgressDependencyFields));
        }
        else result.Add(ExportRecord("field_daily_progress.json", "progress_id", row.ProgressId, row, ProgressDependencyFields));
        return result;
    }

    private IReadOnlyList<ArtifactDependency> CaptureQcSources(string date, string[] required)
    {
        var result = new List<ArtifactDependency>();
        foreach (string table in new[] { "flight_batches.json", "base_station_logs.json" })
            result.Add(ArtifactDependencies.Query(Root, ProjectPaths.Json(Root, table), new() { ["date"] = date }));
        result.Add(ArtifactDependencies.Query(Root, ProjectPaths.Json(Root, "equipment.json"),
            fields: ["record_id", "equipment_id", "name", "model", "release_status"]));
        result.Add(ArtifactDependencies.Query(Root, ProjectPaths.Json(Root, "issues.json"),
            fields: ["issue_id", "severity", "status"]));
        foreach (string command in required.Distinct(StringComparer.OrdinalIgnoreCase))
            result.Add(ArtifactDependencies.Query(Root, ProjectPaths.Json(Root, "route_results.json"),
                new() { ["command"] = command }));
        foreach (string reference in _fieldwork.ListFlights(Root).Where(x => x.Date == date).SelectMany(x => x.RawArtifacts).Distinct())
            result.Add(ArtifactDependencies.ArtifactReference(Root, reference));
        return result;
    }
}
