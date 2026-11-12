using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MagOps.Infrastructure.Services;

public sealed record FileInteractionScope(string ProjectRoot, string WorkareaId)
{
    public string Key => ProjectRoot.Length == 0 ? "temporary" :
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            Path.GetFullPath(ProjectRoot).TrimEnd('\\', '/').ToUpperInvariant() + "|" + WorkareaId.ToUpperInvariant())));
}

public sealed record RecentFileResult(string Path, bool Relative, bool IsDirectory, string ArtifactId = "");
public sealed class FileInteractionHistory
{
    public int SchemaVersion { get; set; } = 1;
    public Dictionary<string, string> Directories { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, RecentFileResult> Results { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>Local UI preferences only. Never chooses a managed production destination.</summary>
public sealed class FileInteractionHistoryService
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public string StoragePath { get; }
    public FileInteractionHistoryService(string? storagePath = null) => StoragePath = Path.GetFullPath(
        storagePath ?? Environment.GetEnvironmentVariable("MAGOPS_FILE_INTERACTION_HISTORY") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MagOps", "file-interaction-history.json"));

    private FileInteractionHistory Read()
    {
        if (!File.Exists(StoragePath)) return new();
        var value = JsonSerializer.Deserialize<FileInteractionHistory>(File.ReadAllText(StoragePath), Options)
            ?? throw new JsonException("Empty file history.");
        if (value.SchemaVersion != 1) throw new NotSupportedException("位置记录版本不受支持，原记录保留。");
        value.Directories ??= new(); value.Results ??= new();
        if (value.Directories.Any(x => x.Value is null) || value.Results.Any(x => x.Value is null || x.Value.Path is null))
            throw new JsonException("Invalid preference entry.");
        return value;
    }
    private FileInteractionHistory ReadOptional()
    {
        try { return Read(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        { return new(); }
    }
    private static string DirectoryKey(FileInteractionScope scope, string operation, string direction) =>
        scope.Key + "|" + direction + "|" + operation;

    public string InitialDirectory(FileInteractionScope scope, string operation, string direction,
        string current = "", string businessDirectory = "", string workareaDirectory = "")
    {
        ReadOptional().Directories.TryGetValue(DirectoryKey(scope, operation, direction), out string? previous);
        foreach (string? candidate in new[] { current, previous, businessDirectory, workareaDirectory,
                     scope.ProjectRoot, Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) })
        {
            string? usable = ExistingDirectory(candidate);
            if (usable is not null) return usable;
        }
        return "";
    }
    private static string? ExistingDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            string full = Path.GetFullPath(path);
            if (File.Exists(full)) full = Path.GetDirectoryName(full) ?? "";
            if (!Directory.Exists(full)) return null;
            // Exists alone can hide a permissions failure. Probe enumeration without creating anything.
            using var enumerator = Directory.EnumerateFileSystemEntries(full).GetEnumerator();
            _ = enumerator.MoveNext();
            return full;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return null; }
    }

    public bool RememberInputs(FileInteractionScope scope, string operation, IEnumerable<string> files, out string? warning)
    {
        warning = null;
        string[] paths = files.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (paths.Length == 0 || paths.Any(p => !File.Exists(p))) return true;
        string[] directories = paths.Select(p => Path.GetDirectoryName(p)!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        // A multi-directory drop has no single unambiguous browse location.
        return directories.Length != 1 || RememberDirectory(scope, operation, "input", directories[0], out warning);
    }
    public bool RememberDirectory(FileInteractionScope scope, string operation, string direction, string directory, out string? warning)
    {
        warning = null;
        if (!Directory.Exists(directory)) return true;
        string full = Path.GetFullPath(directory);
        return Update(doc => doc.Directories[DirectoryKey(scope, operation, direction)] = full, out warning);
    }
    public bool RememberResult(FileInteractionScope scope, string operation, string path, bool directory,
        string artifactId, out string? warning)
    {
        warning = null;
        if (!(directory ? Directory.Exists(path) : File.Exists(path))) return true;
        string full = Path.GetFullPath(path), stored = full;
        bool relative = false;
        if (scope.ProjectRoot.Length > 0)
        {
            string candidate = Path.GetRelativePath(scope.ProjectRoot, full);
            relative = !Path.IsPathRooted(candidate) && candidate != ".." && !candidate.StartsWith(".." + Path.DirectorySeparatorChar);
            if (relative) stored = candidate.Replace('\\', '/');
        }
        var result = new RecentFileResult(stored, relative, directory, artifactId);
        return Update(doc =>
        {
            doc.Results[scope.Key] = result;
            doc.Results[scope.Key + "|" + operation] = result;
            doc.Directories[DirectoryKey(scope, operation, "output")] = directory ? full : Path.GetDirectoryName(full)!;
        }, out warning);
    }
    public RecentFileResult? LastResult(FileInteractionScope scope, string operation = "")
    {
        string key = operation.Length == 0 ? scope.Key : scope.Key + "|" + operation;
        if (!ReadOptional().Results.TryGetValue(key, out var result)) return null;
        try
        {
            string full = result.Relative ? Path.GetFullPath(Path.Combine(scope.ProjectRoot, result.Path)) : Path.GetFullPath(result.Path);
            if (result.Relative && !full.StartsWith(Path.GetFullPath(scope.ProjectRoot).TrimEnd('\\', '/') + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase)) return null;
            return result with { Path = full, Relative = false };
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { return null; }
    }

    private bool Update(Action<FileInteractionHistory> edit, out string? warning)
    {
        warning = null;
        try
        {
            string mutexName = "Local\\MagOps.FileHistory." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(StoragePath.ToUpperInvariant())));
            using var mutex = new Mutex(false, mutexName);
            bool held;
            try { held = mutex.WaitOne(TimeSpan.FromSeconds(2)); }
            catch (AbandonedMutexException) { held = true; }
            if (!held) throw new IOException("位置记录正被其他进程更新。");
            try
            {
                FileInteractionHistory document;
                try { document = Read(); }
                catch (JsonException)
                {
                    // Preserve the exact corrupt preference before starting a clean one.
                    if (File.Exists(StoragePath)) File.Copy(StoragePath, StoragePath + ".corrupt-" + Guid.NewGuid().ToString("N"));
                    document = new();
                }
                edit(document);
                AtomicFileWriter.Write(StoragePath, stream => JsonSerializer.Serialize(stream, document, Options));
            }
            finally { mutex.ReleaseMutex(); }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or System.Security.SecurityException)
        { warning = "操作已完成，但浏览位置未记住：" + ex.Message; return false; }
    }
}
