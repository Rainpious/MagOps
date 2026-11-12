namespace MagOps.Desktop;

internal static class CurrentWorkareaDirectoryResolver
{
    internal static bool TryResolve(
        string projectRoot,
        string workareaRelativePath,
        out string directory,
        out string reason)
    {
        directory = "";
        if (string.IsNullOrWhiteSpace(projectRoot) ||
            !Directory.Exists(projectRoot) ||
            !MagOps.Infrastructure.Storage.ProjectPaths.HasProject(projectRoot))
        {
            reason = "当前项目目录不存在。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(workareaRelativePath))
        {
            reason = "请先选择当前工区。";
            return false;
        }

        if (Path.IsPathRooted(workareaRelativePath))
        {
            reason = "当前工区路径必须是项目内的相对路径。";
            return false;
        }

        try
        {
            string root = Path.GetFullPath(projectRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string candidate = Path.GetFullPath(Path.Combine(root, workareaRelativePath));
            string prefix = root + Path.DirectorySeparatorChar;
            if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                reason = "当前工区路径超出项目目录范围，已阻止打开。";
                return false;
            }

            if (!Directory.Exists(candidate))
            {
                reason = "当前工区目录尚不存在；选择或创建有效工区后即可打开。";
                return false;
            }

            string relative = Path.GetRelativePath(root, candidate);
            string current = root;
            foreach (string part in relative.Split(
                         [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                         StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    reason = "当前工区路径包含符号链接或目录联接，无法确认其实际位置，已阻止打开。";
                    return false;
                }
            }

            directory = candidate;
            reason = "当前工区目录：" + candidate;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            reason = "当前工区路径无效：" + ex.Message;
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            reason = "无法检查当前工区目录：" + ex.Message;
            return false;
        }
    }
}
