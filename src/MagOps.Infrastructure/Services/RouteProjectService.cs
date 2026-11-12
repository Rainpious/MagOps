using System.Text.Json;
using MagOps.Core.Routes;
using MagOps.Infrastructure.Storage;

namespace MagOps.Infrastructure.Services;

using System.Text.Json.Serialization;

public sealed class RouteProjectService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    public void Save(
        string path,
        RoutePlanResult plan,
        string outputDirectory,
        string headerlessOrder,
        string coordModeText,
        string startDirectionText,
        string partitionStartSideText,
        RouteSubdivisionWorkspace? subdivisionWorkspace = null)
    {
        string full = Path.GetFullPath(path);
        using var writeLock = WorkspaceFileLock.AcquireDirectory(Path.GetDirectoryName(full)!);
        RouteProjectFile? previous = File.Exists(full) ? Load(full) : null;
        RouteIdentityService.EnsureReferences(plan.Sorties, previous?.Sorties);
        var project = new RouteProjectFile
        {
            AccessPlanning = plan.AccessPlanning,
            CheckLines = plan.CheckLines.ToList(),
            DesignBasis = plan.DesignBasis,
            SchemaVersion = subdivisionWorkspace is null ? "magops-route-project-2" : "magops-route-project-3",
            SubdivisionWorkspace = subdivisionWorkspace,
            CsvPath = plan.SourcePath,
            CsvLastWriteUtc = File.Exists(plan.SourcePath)
                ? File.GetLastWriteTimeUtc(plan.SourcePath).ToString("O")
                : "",
            CoordMode = coordModeText,
            HeaderlessOrder = headerlessOrder,
            AxisOrderText = headerlessOrder,
            CentralMeridian = plan.CentralMeridian,
            MaxSortieKm = plan.Options.MaxSortieKm,
            OutputDirectory = outputDirectory,
            PartitionCode = plan.PartitionCode,
            PartitionManifestPath = plan.PartitionManifestPath,
            StartDirection = startDirectionText,
            PartitionStartSide = partitionStartSideText,
            TakeoffAnchorMode = plan.Options.TakeoffAnchorMode.ToString(),
            FlightDirection = plan.Options.FlightDirection.ToString(),
            TakeoffAnchorRef = plan.Options.TakeoffAnchorRef,
            CustomTakeoffX = plan.Options.CustomTakeoffX,
            CustomTakeoffY = plan.Options.CustomTakeoffY,
            SelectedStartPid = plan.StartPoint.Pid,
            TakeoffX = plan.Options.TakeoffX,
            TakeoffY = plan.Options.TakeoffY,
            ActiveStartEndpoint = plan.ActiveStartEndpoint,
            KmzAglM = plan.Options.KmzAglM,
            KmzSpeedMps = plan.Options.KmzSpeedMps,
            Sorties = plan.Sorties.Select(CloneSortie).ToList(),
            ExecutionSegments = plan.ExecutionSegments.ToList()
        };

        if (project.AccessPlanning is { } access) RouteAccessPlanner.Validate(access);
        RouteCheckLines.Validate(project.CheckLines);
        ValidateSubdivisionWorkspace(project);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        string staged = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(staged, JsonSerializer.Serialize(project, JsonOptions));
            File.Move(staged, full, true);
        }
        finally
        {
            if (File.Exists(staged)) File.Delete(staged);
        }
    }

    public RouteProjectFile Load(string path)
    {
        string json = File.ReadAllText(path);
        RouteProjectFile project = JsonSerializer.Deserialize<RouteProjectFile>(json, JsonOptions)
            ?? throw new InvalidDataException("航线工程 JSON 无效。");
        if (project.SchemaVersion is not "magops-route-project-1" and not "magops-route-project-2" and not "magops-route-project-3")
            throw new InvalidDataException($"不支持的航线工程版本：{project.SchemaVersion}");
        if (project.Sorties.Count == 0 && project.SubdivisionWorkspace is null)
            throw new InvalidDataException("航线工程中没有架次。");
        if (project.SchemaVersion == "magops-route-project-1")
            RouteProjectMigration.MigrateV1ExecutionSegmentIdentity(project);
        if (project.AccessPlanning is { } access) RouteAccessPlanner.Validate(access);
        RouteProjectMigration.ValidateExecutionSegmentIdentity(project);
        RouteCheckLines.Validate(project.CheckLines);
        ValidateSubdivisionWorkspace(project);
        return project;
    }

    private static void ValidateSubdivisionWorkspace(RouteProjectFile project)
    {
        var workspace = project.SubdivisionWorkspace;
        if (workspace is null)
        {
            if (project.SchemaVersion == "magops-route-project-3")
                throw new InvalidDataException("分区工程缺少完整工作区快照。");
            return;
        }
        if (workspace.SchemaVersion != "magops-route-subdivision-workspace-1" || workspace.Partitions.Count == 0 ||
            string.IsNullOrWhiteSpace(workspace.MotherKmlContent) ||
            workspace.Partitions.Select(p => p.Index).Distinct().Count() != workspace.Partitions.Count ||
            workspace.Partitions.Any(p => string.IsNullOrWhiteSpace(p.PartitionCode)) ||
            workspace.Partitions.Select(p => p.PartitionCode).Distinct(StringComparer.OrdinalIgnoreCase).Count() != workspace.Partitions.Count ||
            (!string.IsNullOrEmpty(workspace.ActivePartitionCode) && !workspace.Partitions.Any(p => p.PartitionCode == workspace.ActivePartitionCode)))
            throw new InvalidDataException("分区工作区版本、身份或活动分区无效。");
        using var manifest = JsonDocument.Parse(workspace.ManifestJson);
        if (!manifest.RootElement.TryGetProperty("subarea_count", out var count) || count.GetInt32() != workspace.Partitions.Count)
            throw new InvalidDataException("分区清单与工作区子区数量不一致。");
        if (!manifest.RootElement.TryGetProperty("children", out var children) ||
            !children.EnumerateArray().Select(c => c.GetProperty("partition_code").GetString() ?? "")
                .ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(workspace.Partitions.Select(p => p.PartitionCode)))
            throw new InvalidDataException("分区清单与工作区子区身份不一致。");
        foreach (var partition in workspace.Partitions)
        {
            if (manifest.RootElement.TryGetProperty("mother_line_identity", out _) &&
                (!manifest.RootElement.TryGetProperty("datum", out var datum) || datum.ValueKind != JsonValueKind.String ||
                    datum.GetString() != partition.Datum))
                throw new InvalidDataException($"{partition.PartitionCode} 与母区的投影基准不一致。");
            if (!double.IsFinite(partition.CentralMeridian) || partition.CentralMeridian != project.CentralMeridian ||
                !double.IsFinite(partition.MaxSortieKm) || partition.MaxSortieKm <= 0 || !double.IsFinite(partition.AglM) ||
                partition.Boundary.Count < 3 || partition.Boundary.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y)))
                throw new InvalidDataException($"{partition.PartitionCode} 的边界或投影无效。");
            if (partition.Plan is not { } plan) continue;
            RouteCheckLines.Validate(plan.CheckLines);
            if (plan.AccessPlanning is { } access) RouteAccessPlanner.Validate(access);
            if (plan.PartitionCode != partition.PartitionCode || plan.CentralMeridian != partition.CentralMeridian ||
                !plan.SourcePath.Equals(partition.RouteSourcePath, StringComparison.OrdinalIgnoreCase) ||
                plan.Sorties.Any(s => s.Points.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y))))
                throw new InvalidDataException($"{partition.PartitionCode} 的航线身份或坐标无效。");
            RouteProjectMigration.ValidateExecutionSegmentIdentity(new() { Sorties = plan.Sorties, ExecutionSegments = plan.ExecutionSegments });
            if (partition.EditedSorties.Concat(partition.ReversedSorties).Concat(partition.BoundaryAdjustedSorties)
                .Any(i => i < 0 || i >= plan.Sorties.Count))
                throw new InvalidDataException($"{partition.PartitionCode} 的编辑架次索引无效。");
        }
        PartitionMotherLineIdentityService.ValidateSavedReferences(manifest.RootElement,
            workspace.Partitions.Where(p => p.Plan is not null)
                .Select(p => (p.PartitionCode, (IReadOnlyList<RouteSortie>)p.Plan!.Sorties))
                .Append((project.PartitionCode, project.Sorties)), project.CentralMeridian);
    }

    private static RouteSortie CloneSortie(RouteSortie source) => new()
    {
        Name = source.Name,
        SourceLines = source.SourceLines.ToList(),
        TaskReferences = source.TaskReferences.Select(r => new RouteTaskReference
        {
            LineName = r.LineName,
            SurveyTaskStableId = r.SurveyTaskStableId,
            SurveyLineStableId = r.SurveyLineStableId,
            MotherLineStableId = r.MotherLineStableId,
            SurveyPurpose = r.SurveyPurpose,
            SegmentIndex = r.SegmentIndex,
            RepeatIndex = r.RepeatIndex,
            GeometryFingerprint = r.GeometryFingerprint,
            ExecutionSegmentId = r.ExecutionSegmentId,
            ExecutionSegmentStableId = r.ExecutionSegmentStableId,
            ProtectedExecutionSegment = r.ProtectedExecutionSegment
        }).ToList(),
        Points = source.Points.Select(p => p.Clone()).ToList()
    };
}
