using MagOps.Infrastructure.Storage;

namespace MagOps.Infrastructure.Services;

public enum FolderMigrationAction
{
    MoveDirectory,
    MergeDirectory
}

public sealed record FolderMigrationItem(
    string SourceRelativePath,
    string TargetRelativePath,
    FolderMigrationAction Action,
    string Reason);

public sealed record FolderMigrationConflict(
    string SourceRelativePath,
    string TargetRelativePath,
    string RelativeEntry,
    string Reason);

public sealed record FolderMigrationPlan(
    string ProjectRoot,
    IReadOnlyList<FolderMigrationItem> Items,
    IReadOnlyList<FolderMigrationConflict> Conflicts)
{
    public bool CanExecute => Items.Count > 0 && Conflicts.Count == 0;
}

public sealed record FolderMigrationResult(
    int MovedDirectories,
    int MovedFiles,
    string BackupRoot);

/// <summary>
/// Executes only an explicit migration plan. It never infers legacy aliases,
/// never moves a project during discovery, and refuses any file collision.
/// </summary>
public sealed class FolderLayoutMigrationService
{
    public FolderMigrationPlan Preview(
        string projectRoot,
        IReadOnlyList<FolderMigrationItem> requestedItems)
    {
        string root = Path.GetFullPath(projectRoot);
        var items = new List<FolderMigrationItem>();
        var conflicts = new List<FolderMigrationConflict>();

        foreach (FolderMigrationItem requested in requestedItems)
        {
            string sourceRel = NormalizeRelative(requested.SourceRelativePath);
            string targetRel = NormalizeRelative(requested.TargetRelativePath);
            if (sourceRel.Equals(targetRel, StringComparison.OrdinalIgnoreCase))
                continue;

            string source = SafeCombine(root, sourceRel);
            string target = SafeCombine(root, targetRel);
            if (!Directory.Exists(source))
                continue;

            foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                string entry = Path.GetRelativePath(source, file).Replace('\\', '/');
                string destination = Path.Combine(target, entry.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(destination))
                {
                    conflicts.Add(new FolderMigrationConflict(
                        sourceRel, targetRel, entry,
                        "目标已存在同名文件；显式迁移禁止覆盖。"));
                }
            }

            items.Add(requested with
            {
                SourceRelativePath = sourceRel,
                TargetRelativePath = targetRel
            });
        }

        return new FolderMigrationPlan(root, items, conflicts);
    }

    public FolderMigrationResult Execute(FolderMigrationPlan plan, bool confirmed)
    {
        if (!confirmed)
            throw new InvalidOperationException("目录迁移必须由用户显式确认。");
        if (plan.Conflicts.Count > 0)
            throw new InvalidOperationException("目录迁移存在文件冲突，必须先人工处理。");
        if (plan.Items.Count == 0)
            return new FolderMigrationResult(0, 0, "");

        string root = Path.GetFullPath(plan.ProjectRoot);
        using var writeLock = WorkspaceFileLock.AcquireForRoot(root);

        // Preview is evidence shown to the user, not a lease on the filesystem.
        // Revalidate after taking the project write lock before the first move.
        FolderMigrationPlan current = Preview(root, plan.Items);
        if (current.Conflicts.Count > 0)
            throw new InvalidOperationException("目录迁移确认后检测到新的文件冲突；未移动任何文件。");

        var operations = current.Items
            .Select(x => (Item: x,
                Source: SafeCombine(root, x.SourceRelativePath),
                Target: SafeCombine(root, x.TargetRelativePath)))
            .Where(x => Directory.Exists(x.Source))
            .ToList();
        if (operations.Count == 0)
            return new FolderMigrationResult(0, 0, "");

        foreach (var left in operations)
        foreach (var right in operations)
        {
            if (left.Source.Equals(right.Source, StringComparison.OrdinalIgnoreCase) &&
                left.Target.Equals(right.Target, StringComparison.OrdinalIgnoreCase))
                continue;
            if (IsSameOrDescendant(left.Source, right.Target) ||
                IsSameOrDescendant(left.Target, right.Source))
                throw new InvalidOperationException(
                    $"目录迁移计划存在重叠路径，不能安全执行：{left.Item.SourceRelativePath}。");
        }

        string backup = Path.Combine(
            ProjectPaths.DataDirectory(root),
            "folder-migration-backup",
            DateTimeOffset.UtcNow.ToString("yyyyMMdd_HHmmssfff"));

        // Backup every source before any move starts.
        foreach (var operation in operations)
            CopyDirectory(operation.Source,
                SafeCombine(backup, operation.Item.SourceRelativePath));

        int movedDirectories = 0;
        int movedFiles = 0;
        var rollback = new Stack<Action>();
        try
        {
            foreach (var operation in operations)
            {
                if (!Directory.Exists(operation.Target))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(operation.Target)!);
                    int count = Directory.EnumerateFiles(
                        operation.Source, "*", SearchOption.AllDirectories).Count();
                    Directory.Move(operation.Source, operation.Target);
                    string source = operation.Source;
                    string target = operation.Target;
                    rollback.Push(() =>
                    {
                        if (Directory.Exists(target) && !Directory.Exists(source))
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
                            Directory.Move(target, source);
                        }
                    });
                    movedDirectories++;
                    movedFiles += count;
                    continue;
                }

                foreach (string file in Directory.EnumerateFiles(
                             operation.Source, "*", SearchOption.AllDirectories).ToArray())
                {
                    string destination = Path.Combine(
                        operation.Target, Path.GetRelativePath(operation.Source, file));
                    if (File.Exists(destination))
                        throw new InvalidOperationException(
                            $"迁移期间发现文件冲突：{destination}");
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Move(file, destination);
                    string source = file;
                    string target = destination;
                    rollback.Push(() =>
                    {
                        if (File.Exists(target) && !File.Exists(source))
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
                            File.Move(target, source);
                        }
                    });
                    movedFiles++;
                }

                DeleteEmptyDirectories(operation.Source);
                if (Directory.Exists(operation.Source) &&
                    !Directory.EnumerateFileSystemEntries(operation.Source).Any())
                    Directory.Delete(operation.Source);
                movedDirectories++;
            }
        }
        catch (Exception migrationError)
        {
            var rollbackErrors = new List<string>();
            while (rollback.Count > 0)
            {
                try { rollback.Pop()(); }
                catch (Exception ex) { rollbackErrors.Add(ex.Message); }
            }

            if (rollbackErrors.Count > 0)
                throw new InvalidOperationException(
                    "目录迁移失败且自动回滚不完整；项目已阻断，必须人工复核。原始错误：" +
                    migrationError.Message + "；回滚错误：" +
                    string.Join(" | ", rollbackErrors), migrationError);

            throw new InvalidOperationException(
                "目录迁移失败，已自动回滚到迁移前状态：" +
                migrationError.Message, migrationError);
        }

        return new FolderMigrationResult(movedDirectories, movedFiles, backup);
    }

    private static bool IsSameOrDescendant(string path, string potentialParent)
    {
        string full = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string parent = Path.GetFullPath(potentialParent)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return full.Equals(parent, StringComparison.OrdinalIgnoreCase) ||
               full.StartsWith(parent + Path.DirectorySeparatorChar,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeRelative(string value)
    {
        string normalized = (value ?? "").Replace('\\', '/').Trim('/');
        if (normalized.Length == 0 || Path.IsPathRooted(normalized) ||
            normalized.Split('/').Any(x => x is "." or ".." || x.Length == 0))
            throw new InvalidDataException($"迁移路径必须是安全的项目相对路径：{value}");
        return normalized;
    }

    private static string SafeCombine(string root, string relative)
    {
        string baseFull = Path.GetFullPath(root);
        string combined = Path.GetFullPath(Path.Combine(
            baseFull, relative.Replace('/', Path.DirectorySeparatorChar)));
        string prefix = baseFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                        + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"迁移路径越出项目范围：{relative}");
        return combined;
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string destinationFile = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
            File.Copy(file, destinationFile, overwrite: false);
        }
    }

    private static void DeleteEmptyDirectories(string root)
    {
        foreach (string directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                     .OrderByDescending(x => x.Length))
        {
            if (!Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);
        }
    }
}
