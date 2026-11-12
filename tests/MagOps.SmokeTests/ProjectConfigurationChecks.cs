using System.Text.Json.Nodes;
using MagOps.Core.Coordinates;
using MagOps.Infrastructure.Services;
using MagOps.Infrastructure.Storage;

namespace MagOps.SmokeTests;

internal static partial class Program
{
    private static void RunIndependentProjectMeridianSuggestion()
    {
        const string input = "89.84484746,47.22794472\n89.84464761,47.29630457\n89.78845666,47.29621463";
        var stale = CoordinateService.ConvertBatch(input, CoordinateInputMode.DecimalDegrees,
            new ProjectionParameters(CentralMeridian: 102), autoCentralMeridian: false);
        Equal(102d, stale.CentralMeridian, 1e-12);
        var suggestion = CoordinateService.SuggestBatchGk3CentralMeridian(stale.Results);
        Equal(90d, suggestion.CentralMeridian, 1e-12);
        Equal(1, suggestion.PointMeridians.Count);
        var automatic = CoordinateService.ConvertBatch(input, CoordinateInputMode.DecimalDegrees,
            new ProjectionParameters(), autoCentralMeridian: true);
        Equal(suggestion.CentralMeridian, automatic.CentralMeridian, 1e-12);
        var corrected = CoordinateService.ConvertBatch(input, CoordinateInputMode.DecimalDegrees,
            new ProjectionParameters(CentralMeridian: suggestion.CentralMeridian), autoCentralMeridian: false);
        True(Math.Abs(stale.Results[0].X - corrected.Results[0].X) > 100_000,
            "correcting the stale CM must actually recompute projected coordinates");
        var crossZone = CoordinateService.ConvertBatch("88,47\n92,47", CoordinateInputMode.DecimalDegrees,
            new ProjectionParameters(CentralMeridian: 102), autoCentralMeridian: false);
        Equal(2, CoordinateService.SuggestBatchGk3CentralMeridian(crossZone.Results).PointMeridians.Count);
    }

    private static void RunProjectConfigurationMigration()
    {
        WithTempRoot(root =>
        {
            var service = new WorkspaceService(new JsonStore());
            service.Initialize(root, "committed-snapshot");
            string project = ProjectPaths.Project(root);
            byte[] committed = File.ReadAllBytes(project);
            string staged = project + ".tmp";
            File.WriteAllText(staged, "incomplete-write");
            string snapshot = new SnapshotService().Create(root, "committed-only")!;
            True(committed.SequenceEqual(File.ReadAllBytes(Path.Combine(snapshot, Path.GetFileName(project)))),
                "snapshot must retain committed metadata exactly");
            True(!File.Exists(Path.Combine(snapshot, Path.GetFileName(staged))),
                "uncommitted temporary writes must not become restore inputs");
            Equal("incomplete-write", File.ReadAllText(staged));
        });
        WithTempRoot(root =>
        {
            var store = new JsonStore();
            var service = new WorkspaceService(store);
            var initial = service.Initialize(root, "配置迁移");
            string current = ProjectPaths.Project(root);
            string legacy = ProjectPaths.Json(root, ProjectPaths.LegacyProjectFileName);
            True(File.Exists(current) && !File.Exists(legacy), "new projects must only create project.json");
            True(initial.CentralMeridian is null, "new projects must not inherit a central meridian");
            service.ConfirmProjectCentralMeridian(root, 102, "USER_COORDINATE_REFERENCE_CONFIRMED");
            JsonObject node = JsonNode.Parse(File.ReadAllText(current))!.AsObject();
            node["custom_extension"] = new JsonObject { ["keep"] = "未知字段保留" };
            File.WriteAllText(current, node.ToJsonString());
            File.Move(current, legacy);
            string before = File.ReadAllText(legacy);
            string child = Path.Combine(root, "10_工区", "test");
            Directory.CreateDirectory(child);
            Equal(root, WorkspaceService.FindInitializedProjectRoot(child)!);
            Equal(102d, service.GetWorkspaceReadOnly(root).CentralMeridian!.Value, 1e-12);
            Equal("PASS", new MigrationService(store).Validate(root).Status);
            True(!File.Exists(current) && File.Exists(legacy), "read-only APIs must not migrate legacy files");
            var reopened = service.Initialize(root, "must-not-reinitialize");
            Equal(initial.ProjectId, reopened.ProjectId);
            Equal(initial.DisplayName, reopened.DisplayName);
            Equal(102d, reopened.CentralMeridian!.Value, 1e-12);
            True(File.Exists(current) && !File.Exists(legacy), "normal opening must rename the legacy project file");
            Equal(before, File.ReadAllText(current));
            var snapshots = new SnapshotService().List(root);
            Equal(1, snapshots.Count);
            Equal(before, File.ReadAllText(Path.Combine(snapshots[0].Path, ProjectPaths.LegacyProjectFileName)));
            service.GetWorkspace(root);
            Equal(1, new SnapshotService().List(root).Count);
            service.ConfirmProjectCentralMeridian(root, 90, "USER_COORDINATE_REFERENCE_CORRECTED");
            Equal(90d, service.GetWorkspaceReadOnly(root).CentralMeridian!.Value, 1e-12);
            Equal("未知字段保留", JsonNode.Parse(File.ReadAllText(current))!["custom_extension"]!["keep"]!.GetValue<string>());
            // A stale duplicate never takes precedence and is never overwritten.
            File.WriteAllText(legacy, before);
            Equal(90d, service.GetWorkspace(root).CentralMeridian!.Value, 1e-12);
            Equal(before, File.ReadAllText(legacy));
            Equal(root, WorkspaceService.FindInitializedProjectRoot(child)!);
        });
        WithTempRoot(root =>
        {
            Directory.CreateDirectory(ProjectPaths.DataDirectory(root));
            string legacy = ProjectPaths.Json(root, ProjectPaths.LegacyProjectFileName);
            File.WriteAllText(legacy, "[]");
            Throws<InvalidDataException>(() => ProjectPaths.EnsureProjectFile(root));
            Equal("[]", File.ReadAllText(legacy));
            True(!File.Exists(ProjectPaths.Project(root)), "invalid legacy metadata must remain untouched");
        });
        WithTempRoot(root =>
        {
            var store = new JsonStore();
            var service = new WorkspaceService(store);
            var original = service.Initialize(root, "schema-migration");
            var node = JsonNode.Parse(File.ReadAllText(ProjectPaths.Project(root)))!.AsObject();
            node["schema_version"] = "0.1";
            node.Remove("gate_records");
            node["custom_extension"] = "keep-through-schema-migration";
            File.WriteAllText(ProjectPaths.Project(root), node.ToJsonString());
            File.Move(ProjectPaths.Project(root), ProjectPaths.Json(root, ProjectPaths.LegacyProjectFileName));
            var migrated = service.GetWorkspace(root);
            Equal(original.ProjectId, migrated.ProjectId);
            Equal(MagOps.Core.AppConstants.SchemaVersion, migrated.SchemaVersion);
            Equal("PASS", new MigrationService(store).Validate(root).Status);
            Equal("keep-through-schema-migration", migrated.Extra!["custom_extension"].GetString()!);
            Equal(2, new SnapshotService().List(root).Count);
            True(!File.Exists(ProjectPaths.Json(root, ProjectPaths.LegacyProjectFileName)),
                "old schema migration must also move metadata to project.json");
        });
    }
}
