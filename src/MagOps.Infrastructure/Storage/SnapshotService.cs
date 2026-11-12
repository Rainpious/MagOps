using System.Text.RegularExpressions;

namespace MagOps.Infrastructure.Storage;

public sealed class SnapshotService
{
    public string? Create(string root, string reason = "manual")
    {
        using var writeLock = WorkspaceFileLock.AcquireForRoot(root);
        string source = ProjectPaths.DataDirectory(root);
        if (!Directory.Exists(source)) return null;

        var files = Directory.GetFiles(source)
            .Where(x => !string.Equals(Path.GetFileName(x), ".write.lock", StringComparison.OrdinalIgnoreCase))
            // JsonStore stages writes here before replacing committed metadata.
            // Temporary files are neither durable state nor safe snapshot inputs.
            .Where(x => !string.Equals(Path.GetExtension(x), ".tmp", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (files.Length == 0) return null;

        string safe = Regex.Replace(reason ?? "manual", @"[^0-9A-Za-z_-]+", "-").Trim('-');
        if (safe.Length == 0) safe = "snapshot";
        string stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmssfffffff'Z'");
        string target = Path.Combine(source, "snapshots", $"{stamp}_{safe}");
        Directory.CreateDirectory(target);

        foreach (string file in files)
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: false);
        return target;
    }

    public IReadOnlyList<(string Name, string Path, int FileCount)> List(string root)
    {
        string directory = Path.Combine(ProjectPaths.DataDirectory(root), "snapshots");
        if (!Directory.Exists(directory)) return [];
        return Directory.GetDirectories(directory)
            .OrderByDescending(x => x, StringComparer.OrdinalIgnoreCase)
            .Select(x => (Path.GetFileName(x), x, Directory.GetFiles(x).Length))
            .ToList();
    }
}
