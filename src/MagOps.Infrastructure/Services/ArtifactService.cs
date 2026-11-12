using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MagOps.Core;
using MagOps.Core.Domain;
using MagOps.Infrastructure.Storage;

namespace MagOps.Infrastructure.Services;

public sealed record ArtifactScanResult(
    int Count,
    int ScannedCount,
    int MissingCount,
    bool WithHash,
    bool Truncated,
    string Path);

public sealed record ArtifactModelMigrationResult(
    int Total,
    int Changed,
    int Assigned,
    int ProjectLevel,
    int ReviewRequired,
    int NewIds);

public sealed class ArtifactService
{
    private readonly JsonStore _store;
    private readonly WorkspaceService _workspace;

    private static readonly Dictionary<string, (string role, string stage)> DirectoryRoles =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["01_原始资料"] = ("raw", "raw"),
            ["01_原始材料"] = ("raw", "raw"),
            ["01_项目依据与公共资料"] = ("raw", "raw"),
            ["02_资料登记与字段确认"] = ("registry", "registry"),
            ["03_总体空间框架与工区划分"] = ("coordinate", "coordinate"),
            ["04_项目计划与资源协调"] = ("project_management", "management"),
            ["05_质量安全与生产门禁"] = ("project_management", "management"),
            ["06_跨工区数据汇总"] = ("derived", "processing"),
            ["07_项目综合图件"] = ("figure", "reporting"),
            ["08_项目总报告与交付"] = ("report", "reporting"),
            ["09_公共工具与模板"] = ("other", "management"),
            ["02_中间成果"] = ("processing", "processing"),
            ["03_坐标与空间资料"] = ("coordinate", "coordinate"),
            ["03_成果文件"] = ("derived", "reporting"),
            ["04_航线设计与任务文件"] = ("route", "route"),
            ["05_外业记录与采集成果"] = ("field", "field"),
            ["06_数据处理"] = ("processing", "processing"),
            ["07_图件"] = ("figure", "reporting"),
            ["08_报告编译"] = ("report", "reporting"),
            ["09_GIS工程与软件项目"] = ("external_software", "gis"),
            ["10_工区"] = ("workarea", "project"),
            ["10_子工区"] = ("workarea", "project"),
            ["99_待整理与待确认"] = ("unclassified", "unclassified"),
            ["99_待确认"] = ("unclassified", "unclassified"),
            ["00_项目管理"] = ("project_management", "management"),
            ["数据导出"] = ("derived", "processing"),
            ["GPX"] = ("coordinate", "coordinate"),
            ["GUI"] = ("external_software", "gis"),
            ["报告编写"] = ("report", "reporting")
        };

    public ArtifactService(JsonStore store, WorkspaceService workspace)
    {
        _store = store;
        _workspace = workspace;
    }

    public List<ArtifactRecord> List(string root) =>
        _store.Read(ProjectPaths.Json(root, "artifacts.json"), new List<ArtifactRecord>());

    public IReadOnlyList<ArtifactRecord> RefreshProjectSpatialReferenceStatus(string root)
        => RefreshFreshnessStatus(root);

    public IReadOnlyList<ArtifactRecord> RefreshFreshnessStatus(string root)
    {
        using var writeLock = WorkspaceFileLock.AcquireForRoot(root);
        root = Path.GetFullPath(root);
        List<ArtifactRecord> rows = List(root);
        var changed = new List<ArtifactRecord>();
        var sourceHashCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var externalCache = new Dictionary<string, ExternalPackageInspection>(StringComparer.OrdinalIgnoreCase);
        foreach (ArtifactRecord row in rows)
        {
            string path = Path.GetFullPath(Path.Combine(root, row.Path.Replace('/', Path.DirectorySeparatorChar)));
            if (row.SourceKind == "external_package")
            {
                if (!externalCache.TryGetValue(row.SourceRef, out var inspection))
                {
                    try { inspection = ExternalPackageService.Inspect(root, ProjectChangePreview.Within(root, row.SourceRef)); }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or ArgumentException or System.Text.Json.JsonException)
                    { inspection = new(row.VersionFamily, "STALE", ["外部包清单无法验证：" + ex.Message]); }
                    externalCache[row.SourceRef] = inspection;
                }
                string status = inspection.Status == "REVIEW_REQUIRED" ? "UNVERIFIED" : "STALE";
                string quality = status == "STALE" ? "STALE" : "REVIEW_REQUIRED";
                string externalReason = string.Join("；", inspection.Issues);
                string changeStatus = File.Exists(path) ? "UNCHANGED" : "MISSING";
                if (row.SourceFreshnessStatus != status || row.QualityStatus != quality || row.FreshnessReason != externalReason || row.ChangeStatus != changeStatus)
                {
                    row.SourceFreshnessStatus = status; row.QualityStatus = quality; row.FreshnessReason = externalReason;
                    row.ChangeStatus = changeStatus; changed.Add(row);
                }
                continue;
            }
            if (!File.Exists(path))
            {
                row.SourceFreshnessStatus = "UNVERIFIED";
                row.FreshnessReason = "成果文件缺失。";
                row.ChangeStatus = "MISSING";
                changed.Add(row);
                continue;
            }
            if (row.ChangeStatus == "MISSING") row.ChangeStatus = "UNCHANGED";
            string spatialStatus = GeneratedArtifactIdentity.InspectProjectSpatialReferenceStatus(path);
            string sourceStatus = GeneratedArtifactIdentity.InspectSourceFreshnessStatus(path, sourceHashCache);
            var evidence = GeneratedArtifactIdentity.InspectIndependentFreshness(path, sourceHashCache);
            string reason = evidence.Reason;
            if (row.FreshnessReason == reason && row.SpatialReferenceStatus.Equals(spatialStatus, StringComparison.OrdinalIgnoreCase) &&
                row.SourceFreshnessStatus.Equals(sourceStatus, StringComparison.OrdinalIgnoreCase)) continue;
            row.SpatialReferenceStatus = spatialStatus;
            row.SourceFreshnessStatus = sourceStatus;
            row.FreshnessReason = reason;
            changed.Add(row);
        }

        if (changed.Count > 0)
        {
            _store.Write(ProjectPaths.Json(root, "artifacts.json"), rows);
            _store.AppendJsonLine(ProjectPaths.DataDirectory(root), "generated_artifact_freshness_refreshed",
                new { changed = changed.Select(x => new { x.ArtifactId, x.Path, x.SpatialReferenceStatus, x.SourceFreshnessStatus }).ToArray() });
        }
        return changed;
    }

    public ArtifactModelMigrationResult EnsureV03Metadata(string root)
    {
        using var writeLock = WorkspaceFileLock.AcquireForRoot(root);
        root = Path.GetFullPath(root);

        List<ArtifactRecord> rows = List(root);
        List<WorkareaRecord> workareas = _workspace.GetWorkareas(root);
        var validWorkareaIds = workareas
            .Where(x => !string.IsNullOrWhiteSpace(x.WorkareaId))
            .Select(x => x.WorkareaId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        int changed = 0;
        int newIds = 0;
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (ArtifactRecord row in rows)
        {
            bool rowChanged = false;

            if (string.IsNullOrWhiteSpace(row.ArtifactId) || !seenIds.Add(row.ArtifactId))
            {
                row.ArtifactId = NewArtifactId();
                seenIds.Add(row.ArtifactId);
                newIds++;
                rowChanged = true;
            }

            string status = (row.WorkareaAssignmentStatus ?? "").Trim().ToUpperInvariant();
            bool validStatus = status is "ASSIGNED" or "PROJECT" or "REVIEW_REQUIRED";

            if (!validStatus)
            {
                var assignment = ResolveWorkareaAssignment(row.Path, workareas);
                row.WorkareaRef = assignment.WorkareaRef;
                row.WorkareaAssignmentStatus = assignment.Status;
                rowChanged = true;
            }
            else if (status == "ASSIGNED")
            {
                if (string.IsNullOrWhiteSpace(row.WorkareaRef) ||
                    !validWorkareaIds.Contains(row.WorkareaRef))
                {
                    row.WorkareaRef = "";
                    row.WorkareaAssignmentStatus = "REVIEW_REQUIRED";
                    rowChanged = true;
                }
            }
            else if (status == "REVIEW_REQUIRED")
            {
                var assignment = ResolveWorkareaAssignment(row.Path, workareas);
                if (assignment.Status == "ASSIGNED")
                {
                    row.WorkareaRef = assignment.WorkareaRef;
                    row.WorkareaAssignmentStatus = "ASSIGNED";
                    rowChanged = true;
                }
            }
            else if (status == "PROJECT" && !string.IsNullOrWhiteSpace(row.WorkareaRef))
            {
                row.WorkareaRef = "";
                rowChanged = true;
            }

            row.PreviousPaths ??= [];
            if (rowChanged)
                changed++;
        }

        if (changed > 0)
        {
            rows = rows.OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToList();
            _store.Write(ProjectPaths.Json(root, "artifacts.json"), rows);
            _store.AppendJsonLine(
                ProjectPaths.DataDirectory(root),
                "artifact_model_v03_migrated",
                new
                {
                    total = rows.Count,
                    changed,
                    new_ids = newIds,
                    assigned = rows.Count(x => x.WorkareaAssignmentStatus == "ASSIGNED"),
                    project_level = rows.Count(x => x.WorkareaAssignmentStatus == "PROJECT"),
                    review_required = rows.Count(x => x.WorkareaAssignmentStatus == "REVIEW_REQUIRED")
                });
        }

        return new ArtifactModelMigrationResult(
            rows.Count,
            changed,
            rows.Count(x => x.WorkareaAssignmentStatus == "ASSIGNED"),
            rows.Count(x => x.WorkareaAssignmentStatus == "PROJECT"),
            rows.Count(x => x.WorkareaAssignmentStatus == "REVIEW_REQUIRED"),
            newIds);
    }

    public ArtifactScanResult Scan(string root, bool withHash = false, int? maxFiles = null)
    {
        using var writeLock = WorkspaceFileLock.AcquireForRoot(root);
        _workspace.GetWorkspace(root);
        root = Path.GetFullPath(root);

        // Artifact scanning must not create/guess workareas. Workarea ownership
        // comes only from the explicit workarea registry.
        List<WorkareaRecord> workareas = _workspace.GetWorkareas(root);
        var oldRows = List(root);
        var old = oldRows.Where(x => !string.IsNullOrWhiteSpace(x.Path))
            .ToDictionary(x => x.Path, StringComparer.OrdinalIgnoreCase);

        var records = new List<ArtifactRecord>();
        var sourceHashCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool truncated = false;

        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            string rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            string[] parts = rel.Split('/');
            if (parts.Any(p => AppConstants.ExcludedDirectories.Contains(p))) continue;
            if (parts.Length >= 2 && parts[0] == "00_项目管理" && parts[1] == "magops") continue;
            string identityTarget = file.EndsWith(GeneratedArtifactIdentity.Suffix, StringComparison.OrdinalIgnoreCase)
                ? file[..^GeneratedArtifactIdentity.Suffix.Length] : file;
            if (GeneratedArtifactIdentity.IsHistoricalBundlePath(file) ||
                GeneratedArtifactIdentity.Inspect(identityTarget).IsHistoricalVersion) continue;

            if (maxFiles.HasValue && records.Count >= maxFiles.Value)
            {
                truncated = true;
                break;
            }

            old.TryGetValue(rel, out ArtifactRecord? previous);
            records.Add(BuildRecord(root, file, rel, previous, withHash, workareas, sourceHashCache));
        }

        var seen = records.Select(x => x.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var reconciledOldPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // When a full-hash scan sees a file at a new path and there is exactly
        // one vanished old record with the same SHA-256, carry the stable
        // ArtifactId and manual lineage metadata forward instead of inventing
        // a new identity. Ambiguous duplicate hashes are deliberately ignored.
        if (withHash)
        {
            var oldMissingByHash = oldRows
                .Where(x =>
                    !seen.Contains(x.Path) &&
                    !string.IsNullOrWhiteSpace(x.Sha256))
                .GroupBy(x => x.Sha256!, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() == 1)
                .ToDictionary(g => g.Key, g => g.Single(), StringComparer.OrdinalIgnoreCase);

            var newByHash = records
                .Where(x =>
                    !string.IsNullOrWhiteSpace(x.Sha256) &&
                    (!old.TryGetValue(x.Path, out ArtifactRecord? priorAtCurrentPath) ||
                     string.IsNullOrWhiteSpace(priorAtCurrentPath.Sha256)))
                .GroupBy(x => x.Sha256!, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() == 1)
                .ToDictionary(g => g.Key, g => g.Single(), StringComparer.OrdinalIgnoreCase);

            foreach (var pair in newByHash)
            {
                if (!oldMissingByHash.TryGetValue(pair.Key, out ArtifactRecord? previous))
                    continue;

                AdoptMovedArtifactIdentity(pair.Value, previous);
                reconciledOldPaths.Add(previous.Path);
            }
        }

        if (truncated)
        {
            foreach (var previous in oldRows.Where(x => !seen.Contains(x.Path) && !reconciledOldPaths.Contains(x.Path)))
            {
                previous.ScanStatus = "NOT_SCANNED";
                records.Add(previous);
            }
        }
        else
        {
            foreach (var previous in oldRows.Where(x => !seen.Contains(x.Path) && !reconciledOldPaths.Contains(x.Path)))
            {
                previous.ChangeStatus = "MISSING";
                previous.ScanStatus = "SCANNED";
                previous.LastScannedAt = WorkspaceService.Now();
                records.Add(previous);
            }
        }

        records = records.OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToList();
        if (records.Where(x => x.ChangeStatus != "MISSING").GroupBy(x => x.ArtifactId, StringComparer.OrdinalIgnoreCase)
            .Any(g => !string.IsNullOrWhiteSpace(g.Key) && g.Count() > 1))
            throw new InvalidOperationException("多个现存文件携带同一 Artifact ID，需要确认副本/快照关系后再登记；原登记未改写。");
        _store.Write(ProjectPaths.Json(root, "artifacts.json"), records);
        _store.AppendJsonLine(ProjectPaths.DataDirectory(root), "artifacts_scanned",
            new
            {
                count = records.Count,
                with_hash = withHash,
                missing_count = records.Count(x => x.ChangeStatus == "MISSING"),
                moved_identity_reconciled = reconciledOldPaths.Count
            });

        return new ArtifactScanResult(
            records.Count,
            seen.Count,
            records.Count(x => x.ChangeStatus == "MISSING"),
            withHash,
            truncated,
            ProjectPaths.Json(root, "artifacts.json"));
    }

    public ArtifactRecord Update(
        string root,
        string relativePath,
        string? role = null,
        bool? selected = null,
        string? qualityStatus = null,
        string? processingStage = null,
        string? notes = null,
        IEnumerable<string>? derivedFrom = null,
        string? versionLabel = null,
        string? workareaRef = null,
        string? workareaAssignmentStatus = null)
    {
        using var writeLock = WorkspaceFileLock.AcquireForRoot(root);
        var rows = List(root);
        string normalized = relativePath.Replace('\\', '/');
        var row = rows.FirstOrDefault(x =>
                x.Path.Equals(normalized, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"未找到资料: {relativePath}");

        if (role is not null) row.Role = role;
        if (selected.HasValue) row.Selected = selected;
        if (qualityStatus is not null) row.QualityStatus = qualityStatus;
        if (processingStage is not null) row.ProcessingStage = processingStage;
        if (notes is not null) row.Notes = notes;
        if (derivedFrom is not null) row.DerivedFrom = derivedFrom.ToList();
        if (versionLabel is not null) row.VersionLabel = versionLabel;
        if (workareaRef is not null) row.WorkareaRef = workareaRef;
        if (workareaAssignmentStatus is not null)
            row.WorkareaAssignmentStatus = workareaAssignmentStatus;
        row.MetadataOrigin = "manual";

        _store.Write(ProjectPaths.Json(root, "artifacts.json"), rows);
        _store.AppendJsonLine(ProjectPaths.DataDirectory(root), "artifact_updated",
            new
            {
                path = normalized,
                role,
                selected,
                quality_status = qualityStatus,
                processing_stage = processingStage,
                version_label = versionLabel,
                workarea_ref = workareaRef,
                workarea_assignment_status = workareaAssignmentStatus
            });
        return row;
    }

    public IReadOnlyList<ArtifactRecord> RegisterRawImportBatch(
        string root,
        RawImportBatchResult batch)
    {
        using var writeLock = WorkspaceFileLock.AcquireForRoot(root);
        root = Path.GetFullPath(root);
        var rows = List(root);

        // Raw import has an explicit target workarea and may have just created
        // that directory lazily, so refresh the workarea registry here.
        List<WorkareaRecord> workareas = _workspace.SyncWorkareas(root);
        var changedRows = new List<ArtifactRecord>();

        foreach (RawImportItem imported in batch.Items.Where(
                     x => x.Status == RawImportStatuses.Archived &&
                          !string.IsNullOrWhiteSpace(x.ArchivedRelativePath)))
        {
            string relative = imported.ArchivedRelativePath.Replace('\\', '/');
            string full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full))
                continue;

            ArtifactRecord? previous = rows.FirstOrDefault(x =>
                x.Path.Equals(relative, StringComparison.OrdinalIgnoreCase));
            ArtifactRecord row = BuildRecord(
                root,
                full,
                relative,
                previous,
                withHash: false,
                workareas: workareas);
            row.Role = "raw";
            row.ProcessingStage = "raw";
            row.SourceKind = "raw_import";
            row.SourceRef = imported.SourcePath;
            row.ExternalOrigin = imported.SourcePath;
            row.Sha256 = imported.Sha256;
            row.HashStatus = "COMPUTED";
            row.MetadataOrigin = "raw_import";
            row.ScanStatus = "SCANNED";
            row.LastScannedAt = WorkspaceService.Now();
            row.Notes = string.IsNullOrWhiteSpace(row.Notes)
                ? "由“添加原始资料”复制归档，源文件保留不移动。"
                : row.Notes;

            Upsert(rows, row);
            changedRows.Add(row);
        }

        // The batch manifest is a registry/audit artifact, not raw measurement data.
        if (File.Exists(batch.ManifestPath))
        {
            changedRows.Add(RegisterManifestRow(
                rows,
                root,
                batch.ManifestPath,
                "raw_import_manifest",
                "原始资料导入批次清单（内部管理资料，可能包含本机绝对源路径）。",
                workareas));
        }

        string sanitizedManifest = Path.Combine(
            batch.DestinationDirectory,
            "导入清单_脱敏.json");
        if (File.Exists(sanitizedManifest))
        {
            changedRows.Add(RegisterManifestRow(
                rows,
                root,
                sanitizedManifest,
                "raw_import_public_manifest",
                "原始资料导入脱敏清单，可用于不暴露本机源路径的对外核对。",
                workareas));
        }

        rows = rows.OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToList();
        _store.Write(ProjectPaths.Json(root, "artifacts.json"), rows);
        string manifestRelativeForLog = !string.IsNullOrWhiteSpace(batch.ManifestPath) &&
                                       File.Exists(batch.ManifestPath)
            ? Path.GetRelativePath(root, batch.ManifestPath).Replace('\\', '/')
            : "";
        _store.AppendJsonLine(
            ProjectPaths.DataDirectory(root),
            "raw_materials_archived",
            new
            {
                status = batch.Status,
                archived_count = batch.ArchivedCount,
                skipped_duplicate_count = batch.SkippedDuplicateCount,
                failed_count = batch.FailedCount,
                manifest_path = manifestRelativeForLog
            });

        return changedRows;
    }

    public void ValidateGeneratedArtifactContext(
        string root,
        GeneratedArtifactContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        root = Path.GetFullPath(root);
        WorkspaceRecord workspace = _workspace.GetWorkspace(root);
        if (!context.ProjectId.Equals(workspace.ProjectId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"成果上下文 ProjectId 与当前项目不一致：{context.ProjectId}。");
        if (!context.ContextStatus.Equals("WORKAREA", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(context.WorkareaId))
            throw new InvalidOperationException(
                "正式工区成果必须提供 canonical WorkareaId。");
        if (!_workspace.GetWorkareas(root).Any(x =>
                x.WorkareaId.Equals(context.WorkareaId, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException(
                $"成果上下文引用未知 WorkareaId：{context.WorkareaId}。");
    }

    public string? ResolveStableArtifactIdForGeneratedPath(string root, string fullPath)
    {
        root = Path.GetFullPath(root);
        fullPath = Path.GetFullPath(fullPath);
        string prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                        + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"生成文件不在项目目录内，无法解析稳定 Artifact ID：{fullPath}");

        string relative = Path.GetRelativePath(root, fullPath).Replace('\\', '/');
        List<ArtifactRecord> rows = List(root);
        ArtifactRecord? previous = rows.FirstOrDefault(x =>
            x.Path.Equals(relative, StringComparison.OrdinalIgnoreCase));
        if (previous is not null && !string.IsNullOrWhiteSpace(previous.ArtifactId))
        {
            if (rows.Any(x =>
                    !x.Path.Equals(relative, StringComparison.OrdinalIgnoreCase) &&
                    x.ArtifactId.Equals(previous.ArtifactId, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException(
                    $"既有 Artifact ID 已被其他路径占用，不能自动复用：{previous.ArtifactId}。");
            // Registry identity predates P1 sidecars in legacy projects. For the exact
            // same registered path it is the deterministic stable identity to carry
            // forward; a stale sidecar from an interrupted/older export is replaced
            // only inside the next atomic staging commit.
            return previous.ArtifactId;
        }

        if (File.Exists(fullPath) &&
            GeneratedArtifactIdentity.Inspect(fullPath) is
                { Status: "VERIFIED", Metadata: not null } identity)
        {
            if (rows.Any(x =>
                    x.ArtifactId.Equals(identity.Metadata.ArtifactId, StringComparison.OrdinalIgnoreCase) &&
                    !x.Path.Equals(relative, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException(
                    $"配套身份 ID 已登记到其他路径，需要人工复核：{identity.Metadata.ArtifactId}。");
            return identity.Metadata.ArtifactId;
        }
        return null;
    }

    public ArtifactRecord RegisterGeneratedFile(
        string root,
        string fullPath,
        string sourceKind = "generated")
    {
        using var writeLock = WorkspaceFileLock.AcquireForRoot(root);
        root = Path.GetFullPath(root);
        fullPath = Path.GetFullPath(fullPath);

        string prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                        + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"生成文件不在项目目录内，无法登记：{fullPath}");
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("生成文件不存在，无法登记。", fullPath);

        string relative = Path.GetRelativePath(root, fullPath).Replace('\\', '/');
        var rows = List(root);
        List<WorkareaRecord> workareas = _workspace.GetWorkareas(root);
        ArtifactRecord? previous = rows.FirstOrDefault(x =>
            x.Path.Equals(relative, StringComparison.OrdinalIgnoreCase));
        ArtifactRecord row = BuildRecord(
            root,
            fullPath,
            relative,
            previous,
            withHash: false,
            workareas: workareas);
        var identity = GeneratedArtifactIdentity.Inspect(fullPath);
        if (identity.IsHistoricalVersion || GeneratedArtifactIdentity.IsHistoricalBundlePath(fullPath))
            throw new InvalidOperationException("这是同一成果的历史快照，不登记到当前成果列表。");
        if (identity.Status == "VERIFIED")
        {
            if (identity.Metadata!.ContextStatus.Equals("WORKAREA", StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(identity.Metadata.WorkareaId) ||
                 !workareas.Any(x => x.WorkareaId.Equals(identity.Metadata.WorkareaId, StringComparison.OrdinalIgnoreCase))))
            {
                throw new InvalidOperationException(
                    $"成果元数据引用未知 WorkareaId：{identity.Metadata.WorkareaId}。禁止按路径或显示名猜测归属。");
            }

            if (rows.Any(x => x.ArtifactId == identity.Metadata!.ArtifactId &&
                !x.Path.Equals(relative, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("其他路径已登记此 Artifact ID，需要确认副本或移动关系。");
            if (previous is not null && previous.ArtifactId != identity.Metadata!.ArtifactId)
                throw new InvalidOperationException("配套身份 ID 与既有 Artifact 登记冲突，需要人工复核。");
            if (previous is null) row.ArtifactId = identity.Metadata!.ArtifactId;
            if (previous is null || previous.MetadataOrigin is "scan" or "processing_diurnal" or "generated")
            {
                row.Role = identity.Metadata!.Role switch
                {
                    "quality_report" => "report",
                    "report" => "report",
                    "route" => "route",
                    "manifest" => "registry",
                    _ => "processing"
                };
                row.ProcessingStage = identity.Metadata.ProcessingStage;
            }
            row.Sha256 = identity.Metadata!.ContentSha256;
            row.HashStatus = "COMPUTED";
            row.QualityStatus = identity.IsProductionStale ? "STALE" : identity.Metadata.QualityStatus;
            row.SpatialReferenceStatus = GeneratedArtifactIdentity.InspectProjectSpatialReferenceStatus(fullPath);
            row.SourceFreshnessStatus = GeneratedArtifactIdentity.InspectSourceFreshnessStatus(fullPath);
            row.FreshnessReason = GeneratedArtifactIdentity.InspectIndependentFreshness(fullPath).Reason;
            GeneratedArtifactLineage lineage = identity.Metadata.Lineage ?? GeneratedArtifactLineage.Empty;
            row.SourceFileStableIds = lineage.SourceFileStableIds.ToList();
            row.GsmSessionStableIds = lineage.GsmSessionStableIds.ToList();
            row.SurveyTaskStableIds = lineage.SurveyTaskStableIds.ToList();
            row.SurveyLineStableIds = lineage.SurveyLineStableIds.ToList();
            row.ExecutionSegmentStableIds = lineage.ExecutionSegmentStableIds.ToList();
        }
        row.SourceKind = sourceKind;
        row.MetadataOrigin = sourceKind;
        Upsert(rows, row);

        rows = rows.OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToList();
        _store.Write(ProjectPaths.Json(root, "artifacts.json"), rows);
        _store.AppendJsonLine(
            ProjectPaths.DataDirectory(root),
            "artifact_registered_incrementally",
            new { path = relative, source_kind = sourceKind });
        return row;
    }

    // Backward-compatible wrapper kept for older code/tests.
    public void MarkRawImport(string root, IEnumerable<RawImportItem> importedItems)
    {
        var items = importedItems.ToList();
        if (items.Count == 0)
            return;

        string manifest = items
            .Select(x => x.ArchivedRelativePath)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => Path.Combine(root, Path.GetDirectoryName(x.Replace('/', Path.DirectorySeparatorChar)) ?? "", "导入清单.json"))
            .FirstOrDefault(File.Exists) ?? "";

        RegisterRawImportBatch(
            root,
            new RawImportBatchResult(
                "COMPLETED",
                "",
                manifest.Length > 0 ? Path.GetDirectoryName(manifest)! : root,
                manifest,
                items));
    }

    private ArtifactRecord RegisterManifestRow(
        List<ArtifactRecord> rows,
        string root,
        string fullPath,
        string sourceKind,
        string notes,
        IReadOnlyList<WorkareaRecord> workareas)
    {
        string relative = Path.GetRelativePath(root, fullPath).Replace('\\', '/');
        ArtifactRecord? previous = rows.FirstOrDefault(x =>
            x.Path.Equals(relative, StringComparison.OrdinalIgnoreCase));
        ArtifactRecord row = BuildRecord(
            root,
            fullPath,
            relative,
            previous,
            withHash: true,
            workareas: workareas);
        row.Role = "registry";
        row.ProcessingStage = "registry";
        row.SourceKind = sourceKind;
        row.MetadataOrigin = "raw_import";
        row.Notes = notes;
        Upsert(rows, row);
        return row;
    }

    private static void Upsert(List<ArtifactRecord> rows, ArtifactRecord row)
    {
        int index = rows.FindIndex(x =>
            x.Path.Equals(row.Path, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
            rows[index] = row;
        else
            rows.Add(row);
    }

    private ArtifactRecord BuildRecord(
        string root,
        string fullPath,
        string relativePath,
        ArtifactRecord? previous,
        bool withHash,
        IReadOnlyList<WorkareaRecord> workareas,
        IDictionary<string, string>? sourceHashCache = null)
    {
        var info = new FileInfo(fullPath);
        string modified = info.LastWriteTimeUtc.ToString("O");
        var (role, stage) = SuggestRole(relativePath);
        bool preserveMetadata =
            previous is not null &&
            !string.IsNullOrWhiteSpace(previous.MetadataOrigin) &&
            !previous.MetadataOrigin.Equals("scan", StringComparison.OrdinalIgnoreCase);
        bool shouldHash = withHash || !string.IsNullOrWhiteSpace(previous?.Sha256);
        string? digest = shouldHash ? Sha256(fullPath) : null;

        string change;
        if (previous is null) change = "NEW";
        else if (shouldHash && !string.IsNullOrWhiteSpace(previous.Sha256))
            change = string.Equals(previous.Sha256, digest, StringComparison.OrdinalIgnoreCase)
                ? "UNCHANGED" : "CHANGED_SINCE_LAST_SCAN";
        else
            change = previous.SizeBytes == info.Length && previous.ModifiedAt == modified
                ? "UNCHANGED" : "CHANGED_SINCE_LAST_SCAN";

        var (family, version) = VersionInfo(info.Name);
        var identity = GeneratedArtifactIdentity.Inspect(fullPath);

        (string WorkareaRef, string Status) assignment =
            ResolveArtifactAssignment(identity, previous, relativePath, workareas);

        return new ArtifactRecord
        {
            ArtifactId = !string.IsNullOrWhiteSpace(previous?.ArtifactId)
                ? previous!.ArtifactId
                : identity.Status == "VERIFIED" ? identity.Metadata!.ArtifactId : NewArtifactId(),
            WorkareaRef = assignment.WorkareaRef,
            WorkareaAssignmentStatus = assignment.Status,
            PreviousPaths = previous?.PreviousPaths?.ToList() ?? [],
            Path = relativePath,
            Name = info.Name,
            Role = preserveMetadata
                ? previous!.Role
                : (string.IsNullOrWhiteSpace(previous?.Role) || previous.Role is "other" or "unclassified"
                    ? role
                    : previous.Role),
            SourceKind = previous?.SourceKind ?? "directory_scan",
            SourceRef = previous?.SourceRef ?? "",
            Sha256 = digest,
            HashStatus = shouldHash ? "COMPUTED" : "NOT_COMPUTED",
            SizeBytes = info.Length,
            ModifiedAt = modified,
            ProcessingStage = preserveMetadata
                ? previous!.ProcessingStage
                : previous?.ProcessingStage ?? stage,
            VersionFamily = previous?.VersionFamily ?? family,
            VersionLabel = previous?.VersionLabel ?? version,
            Selected = previous?.Selected,
            QualityStatus = identity.IsProductionStale ? "STALE" : previous?.QualityStatus ?? "UNREVIEWED",
            SpatialReferenceStatus = GeneratedArtifactIdentity.InspectProjectSpatialReferenceStatus(fullPath),
            SourceFreshnessStatus = GeneratedArtifactIdentity.InspectSourceFreshnessStatus(fullPath, sourceHashCache),
            FreshnessReason = GeneratedArtifactIdentity.InspectIndependentFreshness(fullPath, sourceHashCache).Reason,
            DerivedFrom = previous?.DerivedFrom?.ToList() ?? [],
            SourceFileStableIds = previous?.SourceFileStableIds?.ToList() ?? [],
            GsmSessionStableIds = previous?.GsmSessionStableIds?.ToList() ?? [],
            SurveyTaskStableIds = previous?.SurveyTaskStableIds?.ToList() ?? [],
            SurveyLineStableIds = previous?.SurveyLineStableIds?.ToList() ?? [],
            ExecutionSegmentStableIds = previous?.ExecutionSegmentStableIds?.ToList() ?? [],
            ProcessingRunId = previous?.ProcessingRunId ?? "",
            Notes = previous?.Notes ?? "",
            ExternalOrigin = previous?.ExternalOrigin ?? "",
            LineageRelation = previous?.LineageRelation ?? "",
            MetadataOrigin = preserveMetadata ? previous!.MetadataOrigin : "scan",
            ChangeStatus = change,
            ScanStatus = "SCANNED",
            LastScannedAt = WorkspaceService.Now(),
            Extra = previous?.Extra
        };
    }

    private static (string WorkareaRef, string Status) ResolveArtifactAssignment(
        ArtifactIdentityInspection identity,
        ArtifactRecord? previous,
        string relativePath,
        IReadOnlyList<WorkareaRecord> workareas)
    {
        if (identity is { Status: "VERIFIED", Metadata: not null })
        {
            GeneratedArtifactMetadata metadata = identity.Metadata;
            if (metadata.ContextStatus.Equals("WORKAREA", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(metadata.WorkareaId))
                    return ("", "REVIEW_REQUIRED");

                WorkareaRecord[] matches = workareas.Where(x =>
                    x.WorkareaId.Equals(metadata.WorkareaId, StringComparison.OrdinalIgnoreCase)).ToArray();
                return matches.Length == 1
                    ? (matches[0].WorkareaId, "ASSIGNED")
                    : ("", "REVIEW_REQUIRED");
            }

            if (metadata.ContextStatus.Equals("PROJECT", StringComparison.OrdinalIgnoreCase))
                return ("", "PROJECT");
        }

        return ResolveExistingOrPathAssignment(previous, relativePath, workareas);
    }

    private static (string WorkareaRef, string Status) ResolveExistingOrPathAssignment(
        ArtifactRecord? previous,
        string relativePath,
        IReadOnlyList<WorkareaRecord> workareas)
    {
        if (previous is not null)
        {
            string status = (previous.WorkareaAssignmentStatus ?? "").Trim().ToUpperInvariant();
            if (status == "ASSIGNED" &&
                !string.IsNullOrWhiteSpace(previous.WorkareaRef) &&
                workareas.Any(x =>
                    x.WorkareaId.Equals(previous.WorkareaRef, StringComparison.OrdinalIgnoreCase)))
            {
                return (previous.WorkareaRef, "ASSIGNED");
            }

            if (status == "PROJECT")
                return ("", "PROJECT");

            if (status == "REVIEW_REQUIRED")
                return ("", "REVIEW_REQUIRED");
        }

        return ResolveWorkareaAssignment(relativePath, workareas);
    }

    private static (string WorkareaRef, string Status) ResolveWorkareaAssignment(
        string relativePath,
        IReadOnlyList<WorkareaRecord> workareas)
    {
        string normalized = relativePath.Replace('\\', '/').TrimStart('/');

        foreach (WorkareaRecord workarea in workareas
                     .Where(x =>
                         !string.IsNullOrWhiteSpace(x.WorkareaId) &&
                         !string.IsNullOrWhiteSpace(x.Path))
                     .OrderByDescending(x => x.Path.Length))
        {
            string workareaPath = workarea.Path
                .Replace('\\', '/')
                .Trim('/')
                .Trim();
            if (workareaPath.Length == 0)
                continue;

            if (normalized.Equals(workareaPath, StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith(workareaPath + "/", StringComparison.OrdinalIgnoreCase))
            {
                return (workarea.WorkareaId, "ASSIGNED");
            }
        }

        if (normalized.StartsWith("10_工区/", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("10_子工区/", StringComparison.OrdinalIgnoreCase))
        {
            return ("", "REVIEW_REQUIRED");
        }

        return ("", "PROJECT");
    }

    private static void AdoptMovedArtifactIdentity(
        ArtifactRecord current,
        ArtifactRecord previous)
    {
        current.ArtifactId = !string.IsNullOrWhiteSpace(previous.ArtifactId)
            ? previous.ArtifactId
            : NewArtifactId();
        current.Role = previous.Role;
        current.SourceKind = previous.SourceKind;
        current.SourceRef = previous.SourceRef;
        current.ProcessingStage = previous.ProcessingStage;
        current.VersionFamily = previous.VersionFamily;
        current.VersionLabel = previous.VersionLabel;
        current.Selected = previous.Selected;
        current.QualityStatus = previous.QualityStatus;
        current.DerivedFrom = previous.DerivedFrom?.ToList() ?? [];
        current.Notes = previous.Notes;
        current.ExternalOrigin = previous.ExternalOrigin;
        current.LineageRelation = previous.LineageRelation;
        current.MetadataOrigin = previous.MetadataOrigin;
        current.PreviousPaths = previous.PreviousPaths?.ToList() ?? [];
        if (!current.PreviousPaths.Contains(previous.Path, StringComparer.OrdinalIgnoreCase))
            current.PreviousPaths.Add(previous.Path);
        current.ChangeStatus = "MOVED";
    }

    public static (string Role, string Stage) SuggestRole(string relativePath)
    {
        foreach (string part in relativePath.Replace('\\', '/').Split('/'))
            if (DirectoryRoles.TryGetValue(part, out var value))
                return value;
        return ("other", "unclassified");
    }

    public static (string Family, string Label) VersionInfo(string name)
    {
        string stem = Path.GetFileNameWithoutExtension(name);
        foreach (string pattern in new[]
        {
            @"(?i)(?:^|[_\-. ])(v\d+(?:\.\d+)*)",
            @"(?i)(?:^|[_\-. ])(rev(?:ision)?[-_ ]?\d*)",
            @"(改\d*)",
            @"(?:^|[_\-. ])(\d{8}(?:[_-]\d{4,6})?)"
        })
        {
            var m = Regex.Match(stem, pattern);
            if (!m.Success) continue;
            string label = m.Groups[1].Value;
            string family = (stem[..m.Index] + stem[(m.Index + m.Length)..]).Trim(' ', '_', '-', '.', '改');
            family = Regex.Replace(family, @"[_\- ]{2,}", "_");
            return (family.Length > 0 ? family : stem, label);
        }
        return (stem, "original");
    }

    private static string NewArtifactId() =>
        "art-" + Guid.NewGuid().ToString("N")[..20];

    public static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
