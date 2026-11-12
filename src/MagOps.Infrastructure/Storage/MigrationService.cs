using System.Text.Json.Nodes;
using MagOps.Core;

namespace MagOps.Infrastructure.Storage;

public sealed record ManagementValidationResult(
    string Status,
    string SchemaVersion,
    IReadOnlyList<string> Errors);

public sealed record ManagementMigrationResult(
    string Status,
    string From,
    string To,
    string SnapshotPath,
    ManagementValidationResult Validation);

public sealed class MigrationService
{
    private readonly JsonStore _store;
    private readonly SnapshotService _snapshots = new();

    public MigrationService(JsonStore store) => _store = store;

    public ManagementValidationResult Validate(string root)
    {
        string directory = ProjectPaths.DataDirectory(root);
        var errors = new List<string>();
        string workspacePath = ProjectPaths.ExistingProject(root);

        JsonNode? workspace = ReadNode(workspacePath);
        if (workspace is not JsonObject obj)
        {
            errors.Add("project.json 缺失或不是对象（兼容旧 workspace.json）");
        }
        else
        {
            if (obj["schema_version"]?.GetValue<string>() != AppConstants.SchemaVersion)
                errors.Add($"schema_version 不是当前版本 {AppConstants.SchemaVersion}");
            if (obj["gates"] is not JsonObject gates
                || AppConstants.GateNames.Any(name => !gates.ContainsKey(name)))
                errors.Add("workspace.gates 不完整");
            if (obj["gate_records"] is not null && obj["gate_records"] is not JsonObject)
                errors.Add("workspace.gate_records 必须是对象");
        }

        foreach (string name in ProjectPaths.ListFiles)
        {
            string path = Path.Combine(directory, name);
            JsonNode? node = ReadNode(path);
            if (node is not JsonArray)
                errors.Add($"{name} 必须是数组");
        }

        return new ManagementValidationResult(
            errors.Count == 0 ? "PASS" : "FAIL",
            AppConstants.SchemaVersion,
            errors);
    }

    public ManagementMigrationResult Migrate(string root)
    {
        using var writeLock = WorkspaceFileLock.AcquireForRoot(root);
        string directory = ProjectPaths.DataDirectory(root);
        string workspacePath = ProjectPaths.EnsureProjectFile(root);
        if (ReadNode(workspacePath) is not JsonObject workspace)
            throw new InvalidOperationException($"尚未初始化项目管理数据: {workspacePath}");

        string before = workspace["schema_version"]?.GetValue<string>() ?? "0.1";
        bool nestedWorkarea = string.Equals(
            Directory.GetParent(Path.GetFullPath(root))?.Name,
            "10_工区",
            StringComparison.OrdinalIgnoreCase);

        string? parentProjectRoot = nestedWorkarea
            ? Directory.GetParent(Path.GetFullPath(root))?.Parent?.FullName
            : null;
        JsonObject? parentWorkspace = !string.IsNullOrWhiteSpace(parentProjectRoot)
            ? ReadNode(ProjectPaths.ExistingProject(parentProjectRoot)) as JsonObject
            : null;
        string parentProjectId = parentWorkspace?["project_id"]?.GetValue<string>() ?? "";

        bool changed =
            before != AppConstants.SchemaVersion
            || !workspace.ContainsKey("gate_records")
            || !workspace.ContainsKey("project_id")
            || (nestedWorkarea && (
                !workspace.ContainsKey("workarea_id")
                || (parentProjectId.Length > 0
                    && workspace["project_id"]?.GetValue<string>() != parentProjectId)));

        foreach (string name in ProjectPaths.ListFiles)
            if (!File.Exists(Path.Combine(directory, name)))
                changed = true;

        string snapshot = changed
            ? _snapshots.Create(root, $"before-migrate-{before}-to-{AppConstants.SchemaVersion}") ?? ""
            : "";

        workspace["schema_version"] = AppConstants.SchemaVersion;
        workspace["root"] = Path.GetFullPath(root);
        workspace["project_id"] ??= workspace["workspace_id"]?.DeepClone() ?? "";
        workspace["entity_type"] ??= "project";
        workspace["workarea_root"] ??= "10_工区";

        if (nestedWorkarea)
        {
            workspace["entity_type"] = "workarea";
            workspace["workarea_id"] ??= workspace["workspace_id"]?.DeepClone() ?? "";
            workspace["parent_project_root"] ??= parentProjectRoot ?? "";
            if (parentProjectId.Length > 0)
                workspace["project_id"] = parentProjectId;
        }

        if (workspace["gates"] is not JsonObject gates)
        {
            gates = new JsonObject();
            workspace["gates"] = gates;
        }
        foreach (string gate in AppConstants.GateNames)
            gates[gate] ??= "PENDING";
        workspace["gate_records"] ??= new JsonObject();

        if (changed)
        {
            if (workspace["migrations"] is not JsonArray migrations)
            {
                migrations = new JsonArray();
                workspace["migrations"] = migrations;
            }
            migrations.Add(new JsonObject
            {
                ["from"] = before,
                ["to"] = AppConstants.SchemaVersion,
                ["at"] = DateTimeOffset.UtcNow.ToString("O")
            });
            workspace["updated_at"] = DateTimeOffset.UtcNow.ToString("O");
            _store.Write(workspacePath, workspace);
        }

        foreach (string name in ProjectPaths.ListFiles)
        {
            string path = Path.Combine(directory, name);
            if (!File.Exists(path))
                _store.Write(path, new JsonArray());
        }

        string workareasPath = Path.Combine(directory, "subareas.json");
        if (ReadNode(workareasPath) is JsonArray workareas)
        {
            bool workareasChanged = false;
            foreach (JsonNode? node in workareas)
            {
                if (node is not JsonObject row) continue;
                if (!row.ContainsKey("workarea_id") && row["subarea_id"] is not null)
                {
                    row["workarea_id"] = row["subarea_id"]!.DeepClone();
                    workareasChanged = true;
                }
                if (!row.ContainsKey("subarea_id") && row["workarea_id"] is not null)
                {
                    row["subarea_id"] = row["workarea_id"]!.DeepClone();
                    workareasChanged = true;
                }
                if (row["entity_type"]?.GetValue<string>() != "workarea")
                {
                    row["entity_type"] = "workarea";
                    workareasChanged = true;
                }
            }
            if (workareasChanged) _store.Write(workareasPath, workareas);
        }

        var validation = Validate(root);
        if (validation.Status != "PASS")
            throw new InvalidDataException("迁移后数据校验失败: " + string.Join("; ", validation.Errors));

        return new ManagementMigrationResult(
            changed ? "MIGRATED" : "CURRENT",
            before,
            AppConstants.SchemaVersion,
            snapshot,
            validation);
    }

    private static JsonNode? ReadNode(string path)
    {
        if (!File.Exists(path)) return null;
        string text = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text)) return null;
        return JsonNode.Parse(text);
    }
}
