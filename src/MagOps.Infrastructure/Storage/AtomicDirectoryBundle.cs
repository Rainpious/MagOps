namespace MagOps.Infrastructure.Storage;

/// <summary>
/// Stages a complete directory on the destination volume, validates it, and
/// keeps one previous snapshot. On an exception after the old directory moves,
/// restores it. The lock spans the complete read/modify/commit operation.
/// </summary>
public static class AtomicDirectoryBundle
{
    public static T Execute<T>(string destination, Func<string, T> build,
        Action<string, T> validate, bool preserveUnrelatedFiles = false,
        Action? afterOldMovedForTest = null)
    {
        string target = Path.GetFullPath(destination);
        string parent = Path.GetDirectoryName(target)
            ?? throw new ArgumentException("Destination must have a parent directory.");
        using var writeLock = WorkspaceFileLock.AcquireDirectory(parent);
        Directory.CreateDirectory(parent);
        string snapshot = target + ".previous";
        if (!Directory.Exists(target) && Directory.Exists(snapshot))
            Directory.Move(snapshot, target);
        string stage = target + ".staging-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(stage);
        try
        {
            if (preserveUnrelatedFiles && Directory.Exists(target))
                CopyDirectory(target, stage);
            T value = build(stage);
            validate(stage, value);
            if (Directory.Exists(snapshot)) Directory.Delete(snapshot, true);
            bool hadPrevious = Directory.Exists(target);
            if (hadPrevious) Directory.Move(target, snapshot);
            try
            {
                afterOldMovedForTest?.Invoke();
                Directory.Move(stage, target);
            }
            catch
            {
                if (hadPrevious && !Directory.Exists(target) && Directory.Exists(snapshot))
                    Directory.Move(snapshot, target);
                throw;
            }
            return value;
        }
        finally
        {
            if (Directory.Exists(stage)) Directory.Delete(stage, true);
        }
    }

    public static void RestorePrevious(string destination)
    {
        string target = Path.GetFullPath(destination);
        string parent = Path.GetDirectoryName(target)
            ?? throw new ArgumentException("Destination must have a parent directory.");
        using var writeLock = WorkspaceFileLock.AcquireDirectory(parent);
        string snapshot = target + ".previous";
        if (!Directory.Exists(snapshot))
            throw new InvalidOperationException(
                $"Previous bundle snapshot is unavailable: {snapshot}");

        string failed = target + ".failed-" + Guid.NewGuid().ToString("N");
        if (Directory.Exists(target))
            Directory.Move(target, failed);
        try
        {
            Directory.Move(snapshot, target);
            if (Directory.Exists(failed))
                Directory.Delete(failed, true);
        }
        catch
        {
            if (!Directory.Exists(target) && Directory.Exists(failed))
                Directory.Move(failed, target);
            throw;
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string path = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.Copy(file, path);
        }
    }
}
