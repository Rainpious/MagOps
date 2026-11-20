using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using MagOps.Core;
using MagOps.Core.Domain;
using MagOps.Core.Services;
using MagOps.Infrastructure.Storage;

namespace MagOps.Infrastructure.Services;

public sealed class WorkspaceService
{
    private readonly JsonStore _store;

    public WorkspaceService(JsonStore store) => _store = store;

    public WorkspaceRecord Initialize(string root, string? alias = null)
    {
        root = Path.GetFullPath(root);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"工作区不存在: {root}");

        using var writeLock = WorkspaceFileLock.AcquireForRoot(root);
        string dataDir = ProjectPaths.DataDirectory(root);
        Directory.CreateDirectory(dataDir);
        string workspacePath = ProjectPaths.EnsureProjectFile(root);

        if (File.Exists(workspacePath))
            return GetWorkspace(root);

        string display = string.IsNullOrWhiteSpace(alias) ? new DirectoryInfo(root).Name : alias.Trim();
        string now = Now();
        var workspace = new WorkspaceRecord
        {
            SchemaVersion = AppConstants.SchemaVersion,
            ProjectId = Slug(display),
            WorkspaceId = Slug(display),
            DisplayName = display,
            Root = root,
            WorkareaRoot = ProjectPaths.Compact(root) ? "工区" : "10_工区",
            Status = "PRECHECK",
            CreatedAt = now,
            UpdatedAt = now,
            Gates = AppConstants.GateNames.ToDictionary(x => x, _ => "PENDING"),
            GateRecords = new(),
            Notes = ["报告支持草稿装配；正式交付前须核对资料、统计范围并完成审阅。"]
        };
        _store.Write(workspacePath, workspace);

        foreach (string name in ProjectPaths.ListFiles)
        {
            string path = ProjectPaths.Json(root, name);
            if (name == "subareas.json")
                _store.Write(path, DiscoverWorkareas(root));
            else if (!File.Exists(path))
                _store.Write(path, Array.Empty<object>());
        }

        _store.AppendJsonLine(dataDir, "workspace_initialized", new { workspace_id = workspace.WorkspaceId });
        return workspace;
    }

    public WorkspaceRecord SetProjectTimeZone(string root, string timeZoneId)
    {
        _ = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        using var writeLock = WorkspaceFileLock.AcquireForRoot(root);
        WorkspaceRecord workspace = GetWorkspace(root);
        string previous = workspace.TimeZoneId;
        workspace.TimeZoneId = timeZoneId;
        workspace.UpdatedAt = Now();
        _store.Write(ProjectPaths.Project(root), workspace);
        _store.AppendJsonLine(ProjectPaths.DataDirectory(root), "project_time_zone_changed",
            new { previous_time_zone_id = previous, time_zone_id = timeZoneId });
        return workspace;
    }

    public static TimeZoneInfo ResolveProjectTimeZone(WorkspaceRecord workspace)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(workspace.TimeZoneId); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai"); }
        catch (InvalidTimeZoneException) { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai"); }
    }

    public WorkspaceRecord GetWorkspace(string root)
    {
        string path = ProjectPaths.EnsureProjectFile(root);
        if (!File.Exists(path))
            throw new InvalidOperationException($"尚未初始化项目管理数据，请先初始化: {ProjectPaths.DataDirectory(root)}");

        JsonNode? raw = JsonNode.Parse(File.ReadAllText(path));
        if (raw is not JsonObject obj
            || obj["schema_version"]?.GetValue<string>() != AppConstants.SchemaVersion
            || obj["gate_records"] is not JsonObject)
        {
            new MigrationService(_store).Migrate(root);
        }

        var ws = _store.Read(path, new WorkspaceRecord());
        if (ws.Gates.Count == 0)
            ws.Gates = AppConstants.GateNames.ToDictionary(x => x, _ => "PENDING");
        foreach (string gate in AppConstants.GateNames)
            ws.Gates.TryAdd(gate, "PENDING");
        return ws;
    }

    /// <summary>
    /// Reads workspace metadata without invoking schema migration or writing files.
    /// This is the boundary used by read-only integrations such as the CLI.
    /// </summary>
    public WorkspaceRecord GetWorkspaceReadOnly(string root)
    {
        string path = ProjectPaths.ExistingProject(Path.GetFullPath(root));
        if (!File.Exists(path))
            throw new InvalidOperationException($"尚未初始化项目管理数据，请先初始化: {ProjectPaths.DataDirectory(root)}");

        WorkspaceRecord ws = _store.Read(path, new WorkspaceRecord());
        foreach (string gate in AppConstants.GateNames)
            ws.Gates.TryAdd(gate, "PENDING");
        return ws;
    }

    /// <summary>
    /// Updates the persisted project root after the project directory (or one of its
    /// parent folders) has been renamed/moved. The workspace id and all project-relative
    /// business paths remain unchanged.
    /// </summary>
    public WorkspaceRecord RelinkProjectRoot(string root)
    {
        root = Path.GetFullPath(root);
        WorkspaceRecord current = GetWorkspace(root);
        if (NormalizeStoredRoot(current.Root).Equals(root, StringComparison.OrdinalIgnoreCase))
            return current;

        using var writeLock = WorkspaceFileLock.AcquireForRoot(root);
        WorkspaceRecord workspace = GetWorkspace(root);
        string previousRoot = NormalizeStoredRoot(workspace.Root);
        if (previousRoot.Equals(root, StringComparison.OrdinalIgnoreCase))
            return workspace;

        workspace.Root = root;
        workspace.UpdatedAt = Now();
        _store.Write(ProjectPaths.Project(root), workspace);
        _store.AppendJsonLine(
            ProjectPaths.DataDirectory(root),
            "project_root_relinked",
            new { previous_root = previousRoot, root });
        return workspace;
    }

    private static string NormalizeStoredRoot(string? value)
    {
        string root = (value ?? "").Trim();
        try { return root.Length == 0 ? root : Path.GetFullPath(root); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { return root; }
    }

    public List<WorkareaRecord> DiscoverWorkareas(string root)
    {
        root = Path.GetFullPath(root);
        var candidates = new List<(string path, string note)>();
        string standard = Path.Combine(root, ProjectPaths.Compact(root) ? "工区" : "10_工区");
        string legacy = Path.Combine(root, "10_子工区");
        string scan = Directory.Exists(standard) ? standard : legacy;

        if (Directory.Exists(scan))
        {
            candidates.AddRange(Directory.GetDirectories(scan)
                .OrderBy(x => Path.GetFileName(x), StringComparer.OrdinalIgnoreCase)
                .Select(x => (x, "由目录扫描发现，状态需人工确认。")));
        }

        var result = new List<WorkareaRecord>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, note) in candidates)
        {
            string name = Path.GetFileName(path);
            string id = Slug(name);
            if (!seen.Add(id)) continue;
            result.Add(new WorkareaRecord
            {
                WorkareaId = id,
                SubareaId = id,
                DisplayName = name,
                Path = Path.GetRelativePath(root, path).Replace('\\', '/'),
                Status = "PRECHECK",
                Gates = AppConstants.GateNames.ToDictionary(x => x, _ => "PENDING"),
                Notes = note,
                UpdatedAt = Now()
            });
        }
        return result;
    }

    public List<WorkareaRecord> SyncWorkareas(string root)
    {
        using var writeLock = WorkspaceFileLock.AcquireForRoot(root);
        var existing = GetWorkareas(root)
            .ToDictionary(x => string.IsNullOrWhiteSpace(x.WorkareaId) ? x.SubareaId : x.WorkareaId,
                StringComparer.OrdinalIgnoreCase);

        var rows = DiscoverWorkareas(root);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var old = existing.Values.FirstOrDefault(w => NormalizeWorkareaPath(w.Path).Equals(
                NormalizeWorkareaPath(row.Path), StringComparison.OrdinalIgnoreCase));
            old ??= existing.GetValueOrDefault(row.WorkareaId);
            if (old is not null)
            {
                row.WorkareaId = old.WorkareaId;
                row.SubareaId = old.SubareaId;
                row.DisplayName = old.DisplayName;
                row.PreviousDisplayNames = old.PreviousDisplayNames;
                row.Status = old.Status;
                row.Gates = old.Gates;
                row.Notes = old.Notes;
                row.UpdatedAt = old.UpdatedAt;
                row.Extra = old.Extra;
            }
            seen.Add(row.WorkareaId);
        }

        // A workarea can be registered before its first business write. Such a
        // record must survive directory synchronization; otherwise merely pressing
        // "同步工区" would erase the newly registered lazy workarea.
        foreach (WorkareaRecord old in existing.Values)
        {
            string id = string.IsNullOrWhiteSpace(old.WorkareaId) ? old.SubareaId : old.WorkareaId;
            if (!seen.Contains(id))
                rows.Add(old);
        }

        rows = rows
            .OrderBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _store.Write(ProjectPaths.Json(root, "subareas.json"), rows);
        return rows;
    }

    public WorkspaceRecord RenameProjectDisplayName(string root, string displayName)
    {
        displayName = ValidateDisplayName(displayName);
        using var writeLock = WorkspaceFileLock.AcquireForRoot(root);
        var workspace = GetWorkspace(root);
        if (workspace.DisplayName == displayName) return workspace;
        string previous = workspace.DisplayName;
        workspace.DisplayName = displayName;
        workspace.UpdatedAt = Now();
        _store.Write(ProjectPaths.Project(root), workspace);
        _store.AppendJsonLine(ProjectPaths.DataDirectory(root), "project_display_name_changed",
            new { previous, display_name = displayName, project_id = workspace.ProjectId });
        return workspace;
    }

    public WorkareaRecord RenameWorkareaDisplayName(string root, string workareaId, string displayName)
    {
        displayName = ValidateDisplayName(displayName);
        using var writeLock = WorkspaceFileLock.AcquireForRoot(root);
        _ = GetWorkspace(root);
        var rows = GetWorkareas(root);
        var row = rows.SingleOrDefault(w => w.WorkareaId.Equals(workareaId, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"未找到当前工区：{workareaId}");
        if (rows.Any(w => !ReferenceEquals(w, row) &&
            (w.DisplayName.Equals(displayName, StringComparison.OrdinalIgnoreCase) ||
             w.WorkareaId.Equals(displayName, StringComparison.OrdinalIgnoreCase) ||
             w.SubareaId.Equals(displayName, StringComparison.OrdinalIgnoreCase) ||
             w.PreviousDisplayNames.Contains(displayName, StringComparer.OrdinalIgnoreCase))))
            throw new InvalidOperationException("名称与另一工区的名称或身份冲突，请使用不同名称。");
        if (row.DisplayName == displayName) return row;
        string previous = row.DisplayName;
        if (!row.PreviousDisplayNames.Contains(previous, StringComparer.OrdinalIgnoreCase))
            row.PreviousDisplayNames.Add(previous);
        row.DisplayName = displayName;
        row.UpdatedAt = Now();
        _store.Write(ProjectPaths.Json(root, "subareas.json"), rows);
        _store.AppendJsonLine(ProjectPaths.DataDirectory(root), "workarea_display_name_changed",
            new { previous, display_name = displayName, workarea_id = row.WorkareaId, path = row.Path });
        return row;
    }

    private static string ValidateDisplayName(string name)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0 || name.Length > 100 || name.Any(char.IsControl))
            throw new ArgumentException("显示名称应为 1～100 个字符，且不包含换行或控制字符。");
        return name;
    }

    public WorkareaRecord RegisterWorkarea(string root, string displayName)
    {
        displayName = (displayName ?? "").Trim();
        if (displayName.Length == 0)
            throw new ArgumentException("工区名称不能为空。", nameof(displayName));

        using var writeLock = WorkspaceFileLock.AcquireForRoot(root);
        WorkspaceRecord workspace = GetWorkspace(root);
        List<WorkareaRecord> rows = GetWorkareas(root);
        string id = Slug(displayName);

        WorkareaRecord? existing = rows.FirstOrDefault(x =>
            x.WorkareaId.Equals(id, StringComparison.OrdinalIgnoreCase) ||
            x.SubareaId.Equals(id, StringComparison.OrdinalIgnoreCase) ||
            x.DisplayName.Equals(displayName, StringComparison.OrdinalIgnoreCase) ||
            x.PreviousDisplayNames.Contains(displayName, StringComparer.OrdinalIgnoreCase));
        if (existing is not null)
            return existing;

        string workareaRoot = string.IsNullOrWhiteSpace(workspace.WorkareaRoot)
            ? (ProjectPaths.Compact(root) ? "工区" : "10_工区")
            : workspace.WorkareaRoot.Trim().Trim('/', '\\');
        string workareaDirectory = Path.Combine(root, workareaRoot, id);
        bool createdBaseDirectory = !Directory.Exists(workareaDirectory);
        Directory.CreateDirectory(workareaDirectory);
        var row = new WorkareaRecord
        {
            WorkareaId = id,
            SubareaId = id,
            DisplayName = displayName,
            Path = $"{workareaRoot}/{id}",
            Status = "PRECHECK",
            Gates = AppConstants.GateNames.ToDictionary(x => x, _ => "PENDING"),
            Notes = "已登记工区；基础工区目录已创建，业务目录按需创建。",
            UpdatedAt = Now()
        };

        rows.Add(row);
        rows = rows.OrderBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
        try
        {
            _store.Write(ProjectPaths.Json(root, "subareas.json"), rows);
        }
        catch
        {
            if (createdBaseDirectory && Directory.Exists(workareaDirectory) &&
                !Directory.EnumerateFileSystemEntries(workareaDirectory).Any())
            {
                Directory.Delete(workareaDirectory);
            }
            throw;
        }
        _store.AppendJsonLine(
            ProjectPaths.DataDirectory(root),
            "workarea_registered",
            new { workarea_id = row.WorkareaId, display_name = row.DisplayName, path = row.Path });
        return row;
    }

    /// <summary>
    /// Finds the nearest initialized MagOps project that contains <paramref name="folderPath"/>.
    /// This uses the on-disk workspace marker instead of a remembered absolute path so a
    /// project can still be recovered after one of its parent folders is renamed or moved.
    /// </summary>
    public static string? FindInitializedProjectRoot(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
            return null;

        string full;
        try
        {
            full = Path.GetFullPath(folderPath);
        }
        catch
        {
            return null;
        }

        if (File.Exists(full))
            full = Path.GetDirectoryName(full) ?? "";
        if (full.Length == 0 || !Directory.Exists(full))
            return null;

        DirectoryInfo? current = new(full);
        while (current is not null)
        {
            if (ProjectPaths.HasProject(current.FullName))
                return current.FullName;
            current = current.Parent;
        }

        return null;
    }

    /// <summary>
    /// Registers an existing directory as a workarea, or explicitly re-links a stale
    /// workarea record to a directory selected from disk. Re-linking preserves the
    /// existing workarea id/status/gates so metadata keyed by workarea id remains stable.
    /// </summary>
    public WorkareaRecord AttachExistingWorkareaFolder(
        string root,
        string folderPath,
        string? relinkWorkareaId = null)
    {
        root = Path.GetFullPath(root);
        string folder = Path.GetFullPath(folderPath);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"项目目录不存在：{root}");
        if (!Directory.Exists(folder))
            throw new DirectoryNotFoundException($"工区目录不存在：{folder}");

        string relative = ProjectRelativeDirectory(root, folder);
        if (relative.Length == 0)
            throw new InvalidOperationException("请选择工区根目录，不能把项目根目录本身作为工区。");

        using var writeLock = WorkspaceFileLock.AcquireForRoot(root);
        _ = GetWorkspace(root);
        List<WorkareaRecord> rows = GetWorkareas(root);

        WorkareaRecord? exact = rows.FirstOrDefault(x =>
            NormalizeWorkareaPath(x.Path).Equals(relative, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
            return exact;

        if (!string.IsNullOrWhiteSpace(relinkWorkareaId))
        {
            string key = relinkWorkareaId.Trim();
            WorkareaRecord row = rows.FirstOrDefault(x =>
                    x.WorkareaId.Equals(key, StringComparison.OrdinalIgnoreCase) ||
                    x.SubareaId.Equals(key, StringComparison.OrdinalIgnoreCase))
                ?? throw new KeyNotFoundException($"未找到需要重新关联的工区：{key}");

            string previousPath = NormalizeWorkareaPath(row.Path);
            string previousLeaf = Path.GetFileName(previousPath.TrimEnd('/', '\\'));
            string selectedLeaf = new DirectoryInfo(folder).Name;
            bool displayFollowedFolderName =
                string.IsNullOrWhiteSpace(row.DisplayName) ||
                row.DisplayName.Equals(previousLeaf, StringComparison.OrdinalIgnoreCase) ||
                row.DisplayName.Equals(row.WorkareaId, StringComparison.OrdinalIgnoreCase) ||
                row.DisplayName.Equals(row.SubareaId, StringComparison.OrdinalIgnoreCase);

            row.Path = relative;
            if (displayFollowedFolderName && !string.IsNullOrWhiteSpace(selectedLeaf))
                row.DisplayName = selectedLeaf;
            row.UpdatedAt = Now();
            row.Notes = AppendWorkareaNote(
                row.Notes,
                $"已通过手动选择工区文件夹重新关联：{previousPath} -> {relative}");

            _store.Write(ProjectPaths.Json(root, "subareas.json"), rows);
            _store.AppendJsonLine(
                ProjectPaths.DataDirectory(root),
                "workarea_folder_relinked",
                new
                {
                    workarea_id = row.WorkareaId,
                    previous_path = previousPath,
                    path = relative
                });
            return row;
        }

        string displayName = new DirectoryInfo(folder).Name;
        string id = Slug(displayName);
        WorkareaRecord? conflicting = rows.FirstOrDefault(x =>
            x.WorkareaId.Equals(id, StringComparison.OrdinalIgnoreCase) ||
            x.SubareaId.Equals(id, StringComparison.OrdinalIgnoreCase) ||
            x.DisplayName.Equals(displayName, StringComparison.OrdinalIgnoreCase) ||
            x.PreviousDisplayNames.Contains(displayName, StringComparer.OrdinalIgnoreCase));
        if (conflicting is not null)
        {
            throw new InvalidOperationException(
                $"已存在同名/同编号工区“{conflicting.DisplayName}”（{conflicting.Path}）。" +
                "请使用重新关联方式指向所选文件夹，避免生成重复工区身份。");
        }

        var added = new WorkareaRecord
        {
            WorkareaId = id,
            SubareaId = id,
            DisplayName = displayName,
            Path = relative,
            Status = "PRECHECK",
            Gates = AppConstants.GateNames.ToDictionary(x => x, _ => "PENDING"),
            Notes = "通过“打开工区文件夹”登记现有目录。",
            UpdatedAt = Now()
        };

        rows.Add(added);
        rows = rows.OrderBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
        _store.Write(ProjectPaths.Json(root, "subareas.json"), rows);
        _store.AppendJsonLine(
            ProjectPaths.DataDirectory(root),
            "existing_workarea_folder_attached",
            new { workarea_id = added.WorkareaId, display_name = added.DisplayName, path = added.Path });
        return added;
    }

    public List<WorkareaRecord> GetWorkareas(string root) =>
        _store.Read(ProjectPaths.Json(root, "subareas.json"), new List<WorkareaRecord>());

    public List<TaskRecord> GetTasks(string root) =>
        _store.Read(ProjectPaths.Json(root, "tasks.json"), new List<TaskRecord>());

    public WorkspaceRecord SetProjectStatus(string root, string status, string note = "")
    {
        using var writeLock = WorkspaceFileLock.AcquireForRoot(root);
        var workspace = GetWorkspace(root);
        string current = workspace.Status;
        StateMachine.Validate(current, status, note);

        if (status.Equals("RELEASED", StringComparison.OrdinalIgnoreCase))
        {
            var incomplete = AppConstants.GateNames
                .Where(g => !workspace.Gates.TryGetValue(g, out string? value) || value != "PASSED")
                .ToArray();
            if (incomplete.Length > 0)
                throw new InvalidOperationException($"项目不能发布，以下门禁未通过: {string.Join(", ", incomplete)}");
        }

        workspace.Status = status;
        workspace.UpdatedAt = Now();
        if (!string.IsNullOrWhiteSpace(note)) workspace.Notes.Add(note.Trim());
        _store.Write(ProjectPaths.Project(root), workspace);
        _store.AppendJsonLine(ProjectPaths.DataDirectory(root), "workspace_status_changed",
            new { from = current, to = status, note });
        return workspace;
    }

    /// <summary>
    /// Explicitly commits the project-level central meridian. This is a governed
    /// project spatial-reference action, not a coordinate-tool side effect.
    /// Coordinate first-confirmation and the project header use the same explicit
    /// authority as routes. Registration/export remains read-only.
    /// </summary>
    public WorkspaceRecord ConfirmProjectCentralMeridian(
        string root,
        double centralMeridian,
        string source)
    {
        if (!double.IsFinite(centralMeridian) ||
            centralMeridian is < -180 or > 180)
        {
            throw new ArgumentOutOfRangeException(
                nameof(centralMeridian),
                "项目中央子午线必须位于 -180° 到 180° 之间。");
        }

        string normalizedSource = (source ?? "").Trim();
        if (normalizedSource.Length == 0)
        {
            throw new ArgumentException(
                "项目中央子午线必须记录明确的人工确认来源。",
                nameof(source));
        }

        using var writeLock = WorkspaceFileLock.AcquireForRoot(root);
        WorkspaceRecord workspace = GetWorkspace(root);
        double? previousCentralMeridian = workspace.CentralMeridian;
        string previousSource = workspace.CentralMeridianSource;
        string confirmedAt = Now();

        workspace.CentralMeridian = centralMeridian;
        workspace.CentralMeridianSource = normalizedSource;
        workspace.CentralMeridianConfirmedAt = confirmedAt;
        workspace.UpdatedAt = confirmedAt;

        _store.Write(ProjectPaths.Project(root), workspace);
        _store.AppendJsonLine(
            ProjectPaths.DataDirectory(root),
            "project_central_meridian_confirmed",
            new
            {
                previous_central_meridian = previousCentralMeridian,
                previous_source = previousSource,
                central_meridian = centralMeridian,
                source = workspace.CentralMeridianSource,
                confirmed_at = confirmedAt
            });

        return workspace;
    }

    /// <summary>
    /// One-time compatibility cleanup for project central meridians that were
    /// historically promoted by Coordinate/Oasis PLY output. Such values are not
    /// authoritative under the v0.9.1.7 spatial-reference contract.
    /// </summary>
    public WorkspaceRecord InvalidateLegacyCoordinateDerivedCentralMeridian(
        string root)
    {
        WorkspaceRecord current = GetWorkspace(root);
        if (!current.CentralMeridian.HasValue ||
            !IsLegacyCoordinateDerivedCentralMeridianSource(current.CentralMeridianSource))
            return current;

        using var writeLock = WorkspaceFileLock.AcquireForRoot(root);
        WorkspaceRecord workspace = GetWorkspace(root);
        if (!workspace.CentralMeridian.HasValue ||
            !IsLegacyCoordinateDerivedCentralMeridianSource(
                workspace.CentralMeridianSource))
        {
            return workspace;
        }

        double previousCentralMeridian = workspace.CentralMeridian.Value;
        string previousSource = workspace.CentralMeridianSource;
        string invalidatedAt = Now();

        workspace.CentralMeridian = null;
        workspace.CentralMeridianSource =
            "LEGACY_COORDINATE_DERIVED_INVALIDATED: " + previousSource;
        workspace.CentralMeridianConfirmedAt = "";
        workspace.UpdatedAt = invalidatedAt;

        _store.Write(ProjectPaths.Project(root), workspace);
        _store.AppendJsonLine(
            ProjectPaths.DataDirectory(root),
            "project_central_meridian_invalidated",
            new
            {
                previous_central_meridian = previousCentralMeridian,
                previous_source = previousSource,
                reason = "LEGACY_COORDINATE_DERIVED_NOT_PROJECT_AUTHORITY",
                invalidated_at = invalidatedAt
            });

        return workspace;
    }

    public static bool IsLegacyCoordinateDerivedCentralMeridianSource(
        string? source)
    {
        string value = (source ?? "").Trim();
        if (value.Length == 0)
            return false;

        return value.StartsWith("Oasis PLY", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("COORDINATE_", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("COORDINATE ", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("坐标登记", StringComparison.Ordinal) ||
               value.StartsWith("坐标导出", StringComparison.Ordinal) ||
               value.StartsWith("坐标转换", StringComparison.Ordinal);
    }

    public WorkareaRecord SetWorkareaStatus(string root, string workareaId, string status, string note = "")
    {
        using var writeLock = WorkspaceFileLock.AcquireForRoot(root);
        var rows = GetWorkareas(root);
        var row = WorkareaIdentityResolver.Resolve(rows, workareaId).Workarea
            ?? throw new KeyNotFoundException($"未找到工区: {workareaId}");

        string current = row.Status;
        StateMachine.Validate(current, status, note);
        row.Status = status;
        row.UpdatedAt = Now();
        if (!string.IsNullOrWhiteSpace(note)) row.Notes = note.Trim();
        _store.Write(ProjectPaths.Json(root, "subareas.json"), rows);
        _store.AppendJsonLine(ProjectPaths.DataDirectory(root), "workarea_status_changed",
            new { workarea_id = row.WorkareaId, from = current, to = status, note });
        return row;
    }

    public WorkspaceRecord SetGate(
        string root,
        string gate,
        string status,
        string note,
        IEnumerable<string>? evidenceArtifacts,
        string confirmedBy)
    {
        using var writeLock = WorkspaceFileLock.AcquireForRoot(root);
        if (!AppConstants.GateNames.Contains(gate))
            throw new ArgumentException($"无效门禁: {gate}");
        if (!AppConstants.GateStatuses.Contains(status))
            throw new ArgumentException($"无效门禁状态: {status}");

        var evidence = (evidenceArtifacts ?? [])
            .Select(x => x?.Trim() ?? "")
            .Where(x => x.Length > 0)
            .ToList();
        confirmedBy = (confirmedBy ?? "").Trim();

        if (status == "PASSED" && confirmedBy.Length == 0)
            throw new InvalidOperationException("门禁设为 PASSED 必须填写确认人");
        if (status == "PASSED" && evidence.Count == 0)
            throw new InvalidOperationException("门禁设为 PASSED 必须关联至少一项证据");
        if (new[] { "REVIEW_REQUIRED", "BLOCKED", "DEFERRED" }.Contains(status) && string.IsNullOrWhiteSpace(note))
            throw new InvalidOperationException($"门禁设为 {status} 必须填写原因或处理说明");

        var workspace = GetWorkspace(root);
        string previous = workspace.Gates.TryGetValue(gate, out string? old) ? old : "PENDING";
        workspace.Gates[gate] = status;

        var record = new GateRecord
        {
            Gate = gate,
            From = previous,
            Status = status,
            Note = note.Trim(),
            EvidenceArtifacts = evidence,
            ConfirmedBy = confirmedBy,
            ConfirmedAt = confirmedBy.Length > 0 ? Now() : ""
        };
        workspace.GateRecords[gate] = record;
        workspace.UpdatedAt = Now();
        _store.Write(ProjectPaths.Project(root), workspace);
        _store.AppendJsonLine(ProjectPaths.DataDirectory(root), "gate_changed", record);
        return workspace;
    }

    public TaskRecord AddTask(
        string root,
        string title,
        string workarea = "",
        string owner = "",
        string due = "",
        string priority = "P1",
        string notes = "")
    {
        using var writeLock = WorkspaceFileLock.AcquireForRoot(root);
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("任务标题不能为空");
        if (!new[] { "P0", "P1", "P2", "P3" }.Contains(priority))
            throw new ArgumentException("任务优先级必须是 P0、P1、P2 或 P3");

        var tasks = GetTasks(root);
        string prefix = $"TASK-{DateTime.Now:yyyyMMdd}-";
        int seq = tasks.Count(x => x.TaskId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) + 1;
        string now = Now();

        var task = new TaskRecord
        {
            TaskId = $"{prefix}{seq:00}",
            Title = title.Trim(),
            Workarea = workarea,
            Subarea = workarea,
            Owner = owner,
            Due = due,
            Priority = priority,
            Status = "TODO",
            Notes = notes,
            CreatedAt = now,
            UpdatedAt = now
        };
        tasks.Add(task);
        _store.Write(ProjectPaths.Json(root, "tasks.json"), tasks);
        _store.AppendJsonLine(ProjectPaths.DataDirectory(root), "task_added",
            new { task_id = task.TaskId, title = task.Title });
        return task;
    }

    public TaskRecord UpdateTask(string root, TaskRecord update)
    {
        using var writeLock = WorkspaceFileLock.AcquireForRoot(root);
        if (string.IsNullOrWhiteSpace(update.Title)) throw new ArgumentException("任务标题不能为空");
        if (!AppConstants.TaskStatuses.Contains(update.Status)) throw new ArgumentException($"无效任务状态: {update.Status}");
        if (!new[] { "P0", "P1", "P2", "P3" }.Contains(update.Priority))
            throw new ArgumentException("任务优先级必须是 P0、P1、P2 或 P3");

        var tasks = GetTasks(root);
        var task = tasks.FirstOrDefault(x => x.TaskId == update.TaskId)
            ?? throw new KeyNotFoundException($"未找到任务: {update.TaskId}");

        task.Title = update.Title.Trim();
        task.Workarea = update.Workarea;
        task.Subarea = update.Workarea;
        task.Owner = update.Owner;
        task.Due = update.Due;
        task.Priority = update.Priority;
        task.Status = update.Status;
        task.Notes = update.Notes;
        task.UpdatedAt = Now();

        _store.Write(ProjectPaths.Json(root, "tasks.json"), tasks);
        _store.AppendJsonLine(ProjectPaths.DataDirectory(root), "task_updated",
            new { task_id = task.TaskId, task.Title, task.Workarea, task.Owner, task.Due, task.Priority, task.Status });
        return task;
    }

    public TaskLockInfo ReadTaskLock(string root)
    {
        string path = ProjectPaths.TaskLock(root);
        if (!File.Exists(path))
            return new TaskLockInfo("未找到", "", "", "", "", "", path);

        string text = File.ReadAllText(path);
        string section = text.Split("## 填写规则", 2)[0];
        var fields = new Dictionary<string, string>();

        foreach (Match match in Regex.Matches(section, @"^\|\s*([^|\r\n]+?)\s*\|\s*([^|\r\n]*)\s*\|\s*$", RegexOptions.Multiline))
        {
            string key = match.Groups[1].Value.Trim();
            string value = match.Groups[2].Value.Trim();
            if (key is not "字段" and not "---") fields[key] = value;
        }

        return new TaskLockInfo(
            fields.GetValueOrDefault("状态", "未识别"),
            fields.GetValueOrDefault("任务编号", ""),
            fields.GetValueOrDefault("对话线", ""),
            fields.GetValueOrDefault("对话/任务标识", ""),
            fields.GetValueOrDefault("允许写入范围", ""),
            fields.GetValueOrDefault("交接状态", ""),
            path);
    }

    public string BuildSummaryText(string root, ArtifactService artifacts, FieldworkService fieldwork)
    {
        var ws = GetWorkspace(root);
        var workareas = GetWorkareas(root);
        var tasks = GetTasks(root);
        var artifactRows = artifacts.List(root);
        var equipment = fieldwork.ListEquipment(root);
        var flights = fieldwork.ListFlights(root);
        var issues = fieldwork.ListIssues(root);

        var sb = new StringBuilder();
        sb.AppendLine($"项目：{ws.DisplayName}");
        sb.AppendLine($"状态：{ws.Status}");
        sb.AppendLine($"根目录：{ws.Root}");
        sb.AppendLine($"工区：{workareas.Count} ｜ 任务：{tasks.Count} ｜ 资料：{artifactRows.Count}");
        sb.AppendLine($"设备：{equipment.Count} ｜ 飞行架次：{flights.Count} ｜ 问题：{issues.Count}");
        sb.AppendLine();
        sb.AppendLine("门禁：");
        foreach (string gate in AppConstants.GateNames)
            sb.AppendLine($"  {gate,-12} {ws.Gates.GetValueOrDefault(gate, "PENDING")}");
        sb.AppendLine();
        sb.AppendLine("未完成任务：");
        foreach (var task in tasks.Where(x => x.Status != "DONE").OrderBy(x => x.Priority).ThenBy(x => x.Due))
            sb.AppendLine($"  [{task.Priority}] {task.TaskId} {task.Title} / {task.Owner} / {task.Due} / {task.Status}");
        return sb.ToString();
    }

    public string WriteHandoff(string root, ArtifactService artifacts, FieldworkService fieldwork)
    {
        using var writeLock = WorkspaceFileLock.AcquireForRoot(root);
        var ws = GetWorkspace(root);
        var workareas = GetWorkareas(root);
        var tasks = GetTasks(root);
        var artifactRows = artifacts.List(root);
        var equipment = fieldwork.ListEquipment(root);
        var flights = fieldwork.ListFlights(root);
        var issues = fieldwork.ListIssues(root);
        var logs = fieldwork.ListDailyLogs(root);

        string handoffDirectory = new FolderLayoutService()
            .EnsureOperationDirectory(root, "project_handoff");
        string path = Path.Combine(handoffDirectory, $"项目交接_{DateTime.Now:yyyyMMdd_HHmm}.md");
        var lines = new List<string>
        {
            $"# {ws.DisplayName} 项目交接", "",
            $"- 生成时间（UTC）：{Now()}",
            $"- 项目根目录：`{ws.Root}`",
            $"- 项目状态：`{ws.Status}`", "",
            "## 门禁", "",
            "| 门禁 | 状态 |", "|---|---|"
        };
        lines.AddRange(AppConstants.GateNames.Select(g => $"| {g} | {ws.Gates.GetValueOrDefault(g, "PENDING")} |"));
        lines.AddRange(["", "## 工区", "", "| 工区 | 状态 | 路径 |", "|---|---|---|"]);
        lines.AddRange(workareas.Select(x => $"| {x.DisplayName} | {x.Status} | `{x.Path}` |"));
        lines.AddRange(["", "## 未完成任务", "", "| 编号 | 任务 | 工区 | 状态 | 负责人 |", "|---|---|---|---|---|"]);
        lines.AddRange(tasks.Where(x => x.Status != "DONE")
            .Select(x => $"| {x.TaskId} | {x.Title} | {x.Workarea} | {x.Status} | {x.Owner} |"));
        lines.AddRange([
            "", "## 资料统计", "",
            $"- 已登记文件数：{artifactRows.Count}", "",
            "## 外业协调", "",
            $"- 设备：{equipment.Count} 台",
            $"- 飞行批次：{flights.Count}",
            $"- 每日日志：{logs.Count}",
            $"- 未关闭问题：{issues.Count(x => x.Status is not "RESOLVED" and not "DEFERRED")}", ""
        ]);
        File.WriteAllLines(path, lines, new UTF8Encoding(false));
        _store.AppendJsonLine(ProjectPaths.DataDirectory(root), "handoff_written", new { path });
        return path;
    }

    private static string ProjectRelativeDirectory(string root, string folder)
    {
        string rootFull = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string folderFull = Path.GetFullPath(folder)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (folderFull.Equals(rootFull, StringComparison.OrdinalIgnoreCase))
            return "";

        string prefix = rootFull + Path.DirectorySeparatorChar;
        if (!folderFull.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("所选工区文件夹必须位于当前项目目录内部。");

        string relative = Path.GetRelativePath(rootFull, folderFull)
            .Replace('\\', '/')
            .Trim('/');
        if (relative.Length == 0 ||
            relative.Equals("..", StringComparison.Ordinal) ||
            relative.StartsWith("../", StringComparison.Ordinal) ||
            Path.IsPathRooted(relative))
        {
            throw new InvalidOperationException("无法把所选目录解析为安全的项目内工区路径。");
        }

        return relative;
    }

    private static string NormalizeWorkareaPath(string value) =>
        (value ?? "").Replace('\\', '/').Trim('/').Trim();

    private static string AppendWorkareaNote(string existing, string note)
    {
        string current = (existing ?? "").Trim();
        return current.Length == 0 ? note : current + "；" + note;
    }

    public static string Now() => DateTimeOffset.UtcNow.ToString("O");

    public static string Slug(string value)
    {
        string cleaned = Regex.Replace(value ?? "", @"[^0-9A-Za-z\u4e00-\u9fff_-]+", "-").Trim('-');
        return cleaned.Length > 0 ? cleaned : "workspace";
    }
}
