using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MagOps.Infrastructure.Storage;

namespace MagOps.Infrastructure.Services;

public sealed class UserContextState
{
    [JsonPropertyName("project_root")]
    public string ProjectRoot { get; set; } = "";

    [JsonPropertyName("current_workarea_path")]
    public string CurrentWorkareaPath { get; set; } = "";

    [JsonPropertyName("updated_at")]
    public string UpdatedAt { get; set; } = "";

    [JsonPropertyName("last_route_project_path")]
    public string LastRouteProjectPath { get; set; } = "";

    [JsonPropertyName("last_partition_manifest_path")]
    public string LastPartitionManifestPath { get; set; } = "";

    [JsonPropertyName("last_partition_code")]
    public string LastPartitionCode { get; set; } = "";

    public bool RouteScopeConfirmed { get; set; }

    [JsonPropertyName("partition_overview_active")]
    public bool PartitionOverviewActive { get; set; }
}

public sealed class UserContextDocument
{
    [JsonPropertyName("schema_version")]
    public string SchemaVersion { get; set; } = UserContextService.SchemaVersion;

    [JsonPropertyName("route_contexts_by_workarea")]
    public Dictionary<string, UserContextState> RouteContexts { get; set; } = new();

    [JsonPropertyName("projects")]
    public Dictionary<string, UserContextState> Projects { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Per-Windows-user UI context. Current workarea is not project business state:
/// different Windows users may open the same project while working on different
/// workareas. The default file lives under LocalApplicationData/MagOps.
/// </summary>
public sealed class UserContextService
{
    public const string SchemaVersion = "user-context-1";
    public const string FileName = "user-context.json";

    internal static readonly object LocalWriteLock = new();

    private readonly JsonStore _store;
    private readonly string _storagePath;

    public UserContextService(JsonStore store, string? storagePath = null)
    {
        _store = store;
        _storagePath = string.IsNullOrWhiteSpace(storagePath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MagOps",
                FileName)
            : Path.GetFullPath(storagePath);
    }

    public string StoragePath => _storagePath;

    public UserContextState Load(string root)
    {
        string fullRoot = NormalizeRoot(root);
        UserContextDocument document = ReadDocument();
        string key = ProjectKey(fullRoot);

        if (!document.Projects.TryGetValue(key, out UserContextState? state) || state is null)
            return new UserContextState { ProjectRoot = fullRoot };

        return new UserContextState
        {
            ProjectRoot = fullRoot,
            CurrentWorkareaPath = NormalizeRelativePath(state.CurrentWorkareaPath),
            UpdatedAt = state.UpdatedAt ?? "",
            RouteScopeConfirmed = state.RouteScopeConfirmed,
            LastRouteProjectPath = CompatiblePath(state.LastRouteProjectPath)
            ,LastPartitionManifestPath = CompatiblePath(state.LastPartitionManifestPath)
            ,LastPartitionCode = state.LastPartitionCode ?? ""
            ,PartitionOverviewActive = state.PartitionOverviewActive
        };
        string CompatiblePath(string? path) => state.RouteScopeConfirmed || string.IsNullOrEmpty(state.CurrentWorkareaPath) ||
            IsUnderArea(fullRoot, state.CurrentWorkareaPath, path) ? NormalizePath(path) : "";
    }

    private static bool IsUnderArea(string root, string area, string? path) =>
        area.Length > 0 && !string.IsNullOrEmpty(path) && NormalizePath(path).StartsWith(
            Path.GetFullPath(Path.Combine(root, area)).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    public UserContextState SaveLastRouteProject(string root, string projectPath)
    {
        string fullRoot = NormalizeRoot(root);
        string key = ProjectKey(fullRoot);
        lock (LocalWriteLock)
        {
            UserContextDocument document = ReadDocument();
            document.Projects.TryGetValue(key, out UserContextState? existing);
            var state = new UserContextState
            {
                ProjectRoot = fullRoot,
                CurrentWorkareaPath = NormalizeRelativePath(existing?.CurrentWorkareaPath),
                RouteScopeConfirmed = true,
                LastRouteProjectPath = NormalizePath(projectPath),
                LastPartitionManifestPath = existing is { RouteScopeConfirmed: false } &&
                    !IsUnderArea(fullRoot, NormalizeRelativePath(existing.CurrentWorkareaPath), existing.LastPartitionManifestPath)
                    ? "" : NormalizePath(existing?.LastPartitionManifestPath),
                LastPartitionCode = existing?.LastPartitionCode ?? "",
                PartitionOverviewActive = existing?.PartitionOverviewActive ?? false,
                UpdatedAt = DateTimeOffset.UtcNow.ToString("O")
            };
            document.Projects[key] = state;
            _store.Write(_storagePath, document);
            return state;
        }
    }

    public UserContextState SavePartitionState(
        string root, string manifestPath, string partitionCode, bool overviewActive)
    {
        string fullRoot = NormalizeRoot(root);
        string key = ProjectKey(fullRoot);
        lock (LocalWriteLock)
        {
            UserContextDocument document = ReadDocument();
            document.Projects.TryGetValue(key, out UserContextState? existing);
            var state = new UserContextState
            {
                ProjectRoot = fullRoot,
                CurrentWorkareaPath = NormalizeRelativePath(existing?.CurrentWorkareaPath),
                RouteScopeConfirmed = existing?.RouteScopeConfirmed ?? false,
                LastRouteProjectPath = NormalizePath(existing?.LastRouteProjectPath),
                LastPartitionManifestPath = NormalizePath(manifestPath),
                LastPartitionCode = partitionCode ?? "",
                PartitionOverviewActive = overviewActive,
                UpdatedAt = DateTimeOffset.UtcNow.ToString("O")
            };
            document.Projects[key] = state;
            _store.Write(_storagePath, document);
            return state;
        }
    }

    public UserContextState SaveCurrentWorkarea(string root, string relativePath)
    {
        string fullRoot = NormalizeRoot(root);
        string key = ProjectKey(fullRoot);
        var state = new UserContextState
        {
            ProjectRoot = fullRoot,
            CurrentWorkareaPath = NormalizeRelativePath(relativePath),
            UpdatedAt = DateTimeOffset.UtcNow.ToString("O")
        };

        lock (LocalWriteLock)
        {
            UserContextDocument document = ReadDocument();
            if (document.Projects.TryGetValue(key, out UserContextState? existing))
            {
                string oldArea = NormalizeRelativePath(existing.CurrentWorkareaPath);
                if (!string.Equals(oldArea, state.CurrentWorkareaPath, StringComparison.OrdinalIgnoreCase))
                {
                    document.RouteContexts[ScopeKey(oldArea)] = existing;
                    var previous = existing;
                    if (!document.RouteContexts.TryGetValue(ScopeKey(state.CurrentWorkareaPath), out existing) &&
                        oldArea.Length == 0 && IsUnderArea(fullRoot, state.CurrentWorkareaPath, previous.LastRouteProjectPath))
                        existing = previous;
                }
                state.RouteScopeConfirmed = existing?.RouteScopeConfirmed ?? false;
                state.LastRouteProjectPath = NormalizePath(existing?.LastRouteProjectPath);
                state.LastPartitionManifestPath = NormalizePath(existing?.LastPartitionManifestPath);
                state.LastPartitionCode = existing?.LastPartitionCode ?? "";
                state.PartitionOverviewActive = existing?.PartitionOverviewActive ?? false;
            }
            document.Projects[key] = state;
            _store.Write(_storagePath, document);
        }

        return state;

        string ScopeKey(string area) => key + "|" + area.ToUpperInvariant();
    }

    private UserContextDocument ReadDocument()
    {
        UserContextDocument document;
        try
        {
            document = _store.Read(_storagePath, new UserContextDocument());
        }
        catch (JsonException)
        {
            // A corrupt optional UI-preference file must never prevent the
            // project itself from opening. The next successful selection will
            // rewrite a clean document.
            return new UserContextDocument();
        }
        catch (IOException)
        {
            return new UserContextDocument();
        }
        catch (UnauthorizedAccessException)
        {
            return new UserContextDocument();
        }

        if (!string.Equals(document.SchemaVersion, SchemaVersion, StringComparison.OrdinalIgnoreCase))
            return new UserContextDocument();

        document.RouteContexts ??= new();
        document.Projects ??=
            new Dictionary<string, UserContextState>(StringComparer.OrdinalIgnoreCase);
        return document;
    }

    private static string ProjectKey(string fullRoot)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(fullRoot.ToUpperInvariant());
        byte[] hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash)[..24].ToLowerInvariant();
    }

    private static string NormalizeRoot(string value) =>
        Path.GetFullPath(value)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static string NormalizeRelativePath(string? value) =>
        (value ?? "").Replace('\\', '/').Trim().Trim('/');

    private static string NormalizePath(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "" : Path.GetFullPath(value.Trim());
}
