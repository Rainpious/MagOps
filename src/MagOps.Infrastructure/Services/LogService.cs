using System.Text;

namespace MagOps.Infrastructure.Services;

public static class LogService
{
    public static string LogException(string? projectRoot, Exception exception, string context)
    {
        var lines = new[]
        {
            "============================================================",
            $"时间: {DateTimeOffset.Now:O}",
            $"软件版本: {MagOps.Core.AppConstants.AppVersion}",
            $"Windows: {Environment.OSVersion}",
            $".NET: {Environment.Version}",
            $"项目目录: {projectRoot ?? ""}",
            $"上下文: {context}",
            $"异常: {exception}",
            ""
        };
        return WriteLog(projectRoot, lines);
    }

    public static string LogMessage(string? projectRoot, string context, string message)
    {
        var lines = new[]
        {
            "------------------------------------------------------------",
            $"时间: {DateTimeOffset.Now:O}",
            $"软件版本: {MagOps.Core.AppConstants.AppVersion}",
            $"上下文: {context}",
            message,
            ""
        };
        return WriteLog(projectRoot, lines);
    }

    // Diagnostics must not replace the original exception with an I/O failure.
    // The optional local directory lets checks exercise both unavailable sinks
    // without changing the user's permissions or per-user log directory.
    internal static string WriteLog(string? projectRoot, string[] lines, string? localDirectory = null)
    {
        string local = localDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MagOps", "logs");
        if (!string.IsNullOrWhiteSpace(projectRoot) && Directory.Exists(projectRoot))
        {
            string projectLog = TryWrite(Path.Combine(projectRoot, "00_项目管理", "magops", "logs"));
            if (projectLog.Length > 0) return projectLog;
        }
        return TryWrite(local);

        string TryWrite(string directory)
        {
            try
            {
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, $"MagOps_{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllLines(path, lines, new UTF8Encoding(false));
                return path;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
            {
                return "";
            }
        }
    }
}
