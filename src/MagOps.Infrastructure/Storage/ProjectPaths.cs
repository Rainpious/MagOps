using System.Text.Json;

namespace MagOps.Infrastructure.Storage;

public static class ProjectPaths
{
    public const string ProjectFileName = "project.json";
    public const string LegacyProjectFileName = "workspace.json";

    public static bool Compact(string root) => File.Exists(Path.Combine(root, ".magops", "folder-layout.json")) || File.Exists(Path.Combine(root, ProjectFileName));
    public static string Project(string root) => Compact(root) ? Path.Combine(Path.GetFullPath(root), ProjectFileName) : Json(root, ProjectFileName);

    // Discovery and read-only integrations support legacy projects without writes.
    // If both names exist, project.json is the sole authority.
    public static string ExistingProject(string root)
    {
        string current = Project(root);
        string legacy = Json(root, LegacyProjectFileName);
        return !File.Exists(current) && File.Exists(legacy) ? legacy : current;
    }

    public static bool HasProject(string root) => File.Exists(ExistingProject(root));

    public static string EnsureProjectFile(string root)
    {
        string current = Project(root);
        if (File.Exists(current) || !File.Exists(Json(root, LegacyProjectFileName)))
            return current;

        using var writeLock = WorkspaceFileLock.AcquireForRoot(root);
        string legacy = Json(root, LegacyProjectFileName);
        if (File.Exists(current) || !File.Exists(legacy))
            return current;

        // Validate before touching the original; preserve its bytes and unknown fields.
        using (JsonDocument document = JsonDocument.Parse(File.ReadAllText(legacy)))
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"旧项目配置不是 JSON 对象：{legacy}");
        }
        new SnapshotService().Create(root, "before-workspace-to-project-rename");
        File.Move(legacy, current, overwrite: false);
        return current;
    }

    public static string DataDirectory(string root) =>
        Compact(root) ? Path.Combine(Path.GetFullPath(root), ".magops") : Path.Combine(Path.GetFullPath(root), "00_项目管理", "magops");

    public static string Json(string root, string fileName) =>
        Path.Combine(DataDirectory(root), fileName);

    public static string TaskLock(string root) =>
        Path.Combine(Path.GetFullPath(root), "00_项目管理", "当前任务锁.md");

    public static readonly string[] ListFiles =
    [
        "subareas.json",
        "tasks.json",
        "artifacts.json",
        "equipment.json",
        "equipment_checklists.json",
        "equipment_tests.json",
        "flight_batches.json",
        "base_station_logs.json",
        "daily_logs.json",
        "field_daily_progress.json",
        "field_operations_settings.json",
        "field_data_catalog.json",
        "dji_flight_sessions.json",
        "dji_mcdat_catalog.json",
        "issues.json",
        "coordinates.json",
        "crs_definitions.json",
        "route_results.json",
        "qc_results.json",
        "processing_runs.json"
    ];
}
