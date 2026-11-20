namespace MagOps.Infrastructure.Services;

/// <summary>Copy an already committed artifact and its evidence, restoring both on failure.</summary>
public static class ArtifactCopyService
{
    public static void Copy(string source, string destination, Action<int>? checkpoint = null)
    {
        source = Path.GetFullPath(source); destination = Path.GetFullPath(destination);
        if (source.Equals(destination, StringComparison.OrdinalIgnoreCase)) return;
        string parent = Path.GetDirectoryName(destination)!; Directory.CreateDirectory(parent);
        using var guard = MagOps.Infrastructure.Storage.WorkspaceFileLock.AcquireDirectory(parent);
        var pairs = new List<(string Source, string Target)> { (source, destination) };
        if (File.Exists(source + GeneratedArtifactIdentity.Suffix)) pairs.Add((source + GeneratedArtifactIdentity.Suffix, destination + GeneratedArtifactIdentity.Suffix));
        else if (File.Exists(destination + GeneratedArtifactIdentity.Suffix)) throw new IOException("目标已有来源记录，不能用无来源文件覆盖。");
        string stage = Path.Combine(parent, ".magops-copy-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(stage);
        var backups = new List<(string Target, string? Backup)>(); bool recovered = false;
        try {
            for (int i = 0; i < pairs.Count; i++) {
                File.Copy(pairs[i].Source, Path.Combine(stage, i + ".new"));
                if (GeneratedArtifactIdentity.Hash(pairs[i].Source) != GeneratedArtifactIdentity.Hash(Path.Combine(stage, i + ".new"))) throw new IOException("复制期间来源发生变化。");
                string? backup = File.Exists(pairs[i].Target) ? Path.Combine(stage, i + ".old") : null;
                if (backup is not null) File.Copy(pairs[i].Target, backup);
                backups.Add((pairs[i].Target, backup));
            }
            for (int i = 0; i < pairs.Count; i++) {
                AtomicFileWriter.Write(pairs[i].Target, stream => { using var input = File.OpenRead(Path.Combine(stage, i + ".new")); input.CopyTo(stream); });
                checkpoint?.Invoke(i);
            }
            recovered = true;
        } catch {
            foreach (var item in backups) {
                if (item.Backup is not null) File.Copy(item.Backup, item.Target, true);
                else if (File.Exists(item.Target)) File.Delete(item.Target);
            }
            recovered = true; throw;
        } finally {
            // If restoration itself failed, retain the recovery files.
            if (recovered) Directory.Delete(stage, true);
        }
    }
}
