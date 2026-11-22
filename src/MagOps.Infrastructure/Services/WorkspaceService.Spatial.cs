using MagOps.Core.Coordinates;
using MagOps.Core.Domain;
using MagOps.Infrastructure.Storage;

namespace MagOps.Infrastructure.Services;

public sealed partial class WorkspaceService
{
    // A read-only view for existing spatial consumers. Never persist this view to project.json.
    public WorkspaceRecord ReadWorkareaSpatialContext(string root, string workarea)
    {
        var project = GetWorkspaceReadOnly(root);
        var area = GetWorkareas(root).FirstOrDefault(x =>
            x.Path.Equals(workarea, StringComparison.OrdinalIgnoreCase));
        var meridian = EffectiveMeridian(root, project, area);
        project.CentralMeridian = meridian.Value;
        project.CentralMeridianSource = meridian.Source;
        project.CentralMeridianConfirmedAt = meridian.ConfirmedAt;
        return project;
    }

    private WorkareaMeridian EffectiveMeridian(string root, WorkspaceRecord project, WorkareaRecord? area)
    {
        if (area is null) return new();
        if (area.Meridian is not null) return area.Meridian;
        var range = new WorkareaSpatialService().LoadRange(root, area.Path, _store);
        if (range?.Projection is { Kind: CoordinateProjectionKind.GaussKruger, CentralMeridian: double local })
            return new(local, "历史工区已确认的坐标范围", area.UpdatedAt);
        return RouteCentralMeridianPolicy.ConfirmedProjectValue(project) is double old
            ? new(old, "继承自旧项目：" + project.CentralMeridianSource, project.CentralMeridianConfirmedAt)
            : new();
    }

    internal string SpatialFingerprint(string root, WorkspaceRecord project, string? areaId)
    {
        if (string.IsNullOrWhiteSpace(areaId))
            return ProductionSnapshotService.ProjectSpatialReferenceFingerprint(project.CentralMeridian);
        var area = GetWorkareas(root).FirstOrDefault(x => x.WorkareaId.Equals(areaId, StringComparison.OrdinalIgnoreCase));
        return area is null ? "WORKAREA_MISSING" :
            ProductionSnapshotService.ProjectSpatialReferenceFingerprint(EffectiveMeridian(root, project, area).Value);
    }

    public WorkspaceRecord ConfirmWorkareaMeridian(string root, string workarea, double value, string source)
    {
        if (!double.IsFinite(value) || value is < -180 or > 180 || string.IsNullOrWhiteSpace(source))
            throw new ArgumentException("请指定有效的工区中央子午线和确认来源。");
        using var guard = WorkspaceFileLock.AcquireForRoot(root);
        var rows = GetWorkareas(root);
        var area = RequireArea(rows, workarea);
        area.UpdatedAt = Now();
        area.Meridian = new(value, source, area.UpdatedAt);
        _store.Write(ProjectPaths.Json(root, "subareas.json"), rows);
        return ReadWorkareaSpatialContext(root, area.Path);
    }

    private static WorkareaRecord RequireArea(List<WorkareaRecord> rows, string workarea) =>
        rows.FirstOrDefault(x => x.Path.Equals(workarea, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException("请先选择已登记的工区。");

    public void BindWorkareaBoundary(string root, string workarea, string source, ProjectionParameters? projection, string? sourceName = null)
    {
        const string ext = ".kml";
        using var guard = WorkspaceFileLock.AcquireForRoot(root);
        var rows = GetWorkareas(root);
        var area = RequireArea(rows, workarea);
        // Keep one read handle through validation/copy so the published revision is immutable.
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        new KmlSurveySubdivisionService().ReadRegionPolygons(source);
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(input));
        string dir = Path.Combine(root, area.Path, ".magops", "boundaries", hash);
        Directory.CreateDirectory(dir);
        string target = Path.Combine(dir, "工区边界" + ext);
        if (!File.Exists(target))
        {
            input.Position = 0;
            AtomicFileWriter.Write(target, stream => input.CopyTo(stream));
        }
        else if (!GeneratedArtifactIdentity.Hash(target).Equals(hash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("工区边界存档校验失败。");
        area.Boundary = new(Path.GetRelativePath(Path.Combine(root, area.Path), target), hash, Path.GetFileName(sourceName ?? source), projection);
        if (projection is { Kind: CoordinateProjectionKind.GaussKruger, CentralMeridian: double cm })
        {
            var effective = EffectiveMeridian(root, GetWorkspaceReadOnly(root), area);
            if (effective.Value is double previous && Math.Abs(previous - cm) > 1e-9)
                throw new InvalidOperationException("工区中央子午线已改变，请重新核对边界所用的投影。");
            if (area.Meridian?.Value is null)
                area.Meridian = new(cm, "已确认的工区边界", Now());
        }
        area.UpdatedAt = Now();
        _store.Write(ProjectPaths.Json(root, "subareas.json"), rows);
    }

    public static string ResolveBoundBoundary(string root, WorkareaRecord area)
    {
        var boundary = area.Boundary ?? throw new InvalidOperationException("工区尚未绑定边界。");
        string path = TerrainLibraryService.SafePath(Path.Combine(root, area.Path), boundary.RelativePath);
        if (!File.Exists(path) || !GeneratedArtifactIdentity.Hash(path).Equals(boundary.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("当前工区已绑定的边界缺失或内容变化，请重新指定边界。");
        return path;
    }
}
