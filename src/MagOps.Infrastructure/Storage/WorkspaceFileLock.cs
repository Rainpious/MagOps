using System.Collections.Concurrent;
using System.Text.Json;

namespace MagOps.Infrastructure.Storage;

/// <summary>
/// Cross-process, process-local re-entrant write lock compatible with the Python v0.3.7
/// 00_项目管理/magops/.write.lock convention.
/// </summary>
public static class WorkspaceFileLock
{
    private static readonly AsyncLocal<Dictionary<string, int>?> Held = new();

    public static IDisposable AcquireForRoot(string root, TimeSpan? timeout = null, TimeSpan? staleAfter = null) =>
        AcquireDirectory(ProjectPaths.DataDirectory(root), timeout, staleAfter);

    public static IDisposable AcquireDirectory(string managementDirectory, TimeSpan? timeout = null, TimeSpan? staleAfter = null)
    {
        string directory = Path.GetFullPath(managementDirectory);
        string key = directory.ToUpperInvariant();
        var held = Held.Value ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        if (held.TryGetValue(key, out int count) && count > 0)
        {
            held[key] = count + 1;
            return new Releaser(key, null, ownsFile: false);
        }

        Directory.CreateDirectory(directory);
        string lockPath = Path.Combine(directory, ".write.lock");
        TimeSpan wait = timeout ?? TimeSpan.FromSeconds(5);
        TimeSpan stale = staleAfter ?? TimeSpan.FromHours(1);
        DateTime deadline = DateTime.UtcNow + wait;

        while (true)
        {
            try
            {
                using var stream = new FileStream(lockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                var payload = new
                {
                    pid = Environment.ProcessId,
                    created_at = DateTimeOffset.UtcNow.ToString("O")
                };
                JsonSerializer.Serialize(stream, payload);
                stream.Flush(true);
                held[key] = 1;
                return new Releaser(key, lockPath, ownsFile: true);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new UnauthorizedAccessException(
                    $"无法取得项目写入权限：{lockPath}\r\n" +
                    "当前操作需要保存项目数据，已停止。请检查该目录的写入权限、只读属性或安全软件拦截；" +
                    "这不是“项目正在被其他进程写入”的超时错误。", ex);
            }
            catch (IOException) when (File.Exists(lockPath))
            {
                try
                {
                    if (OwnerHasExited(lockPath) || DateTime.UtcNow - File.GetLastWriteTimeUtc(lockPath) > stale)
                    {
                        File.Delete(lockPath);
                        continue;
                    }
                }
                catch (FileNotFoundException)
                {
                    continue;
                }

                if (DateTime.UtcNow >= deadline)
                    throw new TimeoutException($"工作区正在被其他进程写入: {lockPath}");
                Thread.Sleep(50);
            }
        }
    }

    private static bool OwnerHasExited(string lockPath)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(lockPath));
            if (!doc.RootElement.TryGetProperty("pid", out var property) ||
                !property.TryGetInt32(out int pid) || pid <= 0) return false;
            try { using var owner = System.Diagnostics.Process.GetProcessById(pid); return owner.HasExited; }
            catch (ArgumentException) { return true; }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or System.ComponentModel.Win32Exception)
        { return false; }
    }

    public static bool IsManagementPath(string path, out string managementDirectory)
    {
        string full = Path.GetFullPath(path);
        string? dir = Path.GetDirectoryName(full);
        while (!string.IsNullOrWhiteSpace(dir))
        {
            if (string.Equals(Path.GetFileName(dir), "magops", StringComparison.OrdinalIgnoreCase)
                && string.Equals(Path.GetFileName(Path.GetDirectoryName(dir)), "00_项目管理", StringComparison.OrdinalIgnoreCase))
            {
                managementDirectory = dir;
                return true;
            }
            string? parent = Path.GetDirectoryName(dir);
            if (parent == dir) break;
            dir = parent;
        }
        managementDirectory = "";
        return false;
    }

    private sealed class Releaser : IDisposable
    {
        private readonly string _key;
        private readonly string? _lockPath;
        private readonly bool _ownsFile;
        private bool _disposed;

        public Releaser(string key, string? lockPath, bool ownsFile)
        {
            _key = key;
            _lockPath = lockPath;
            _ownsFile = ownsFile;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            var held = Held.Value;
            if (held is null || !held.TryGetValue(_key, out int count)) return;

            if (count > 1)
            {
                held[_key] = count - 1;
                return;
            }

            held.Remove(_key);
            if (_ownsFile && !string.IsNullOrWhiteSpace(_lockPath))
            {
                try { File.Delete(_lockPath); }
                catch (FileNotFoundException) { }
            }
        }
    }
}
