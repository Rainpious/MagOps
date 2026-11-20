using System.Text;
using System.Threading;

namespace MagOps.Desktop;

internal static class Program
{
    private static int _fatalReported;

    private static string CrashLogPath =>
        Path.Combine(Environment.CurrentDirectory, "MagOps_startup_crash.log");

    [STAThread]
    private static void Main()
    {
        _fatalReported = 0;

        try
        {
            ApplicationConfiguration.Initialize();
            System.Windows.Forms.Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

            System.Windows.Forms.Application.ThreadException += (_, e) =>
            {
                ReportFatal("UI thread", e.Exception, showDialog: true);
                Environment.ExitCode = 1;
                System.Windows.Forms.Application.Exit();
            };

            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                Exception exception = e.ExceptionObject as Exception
                    ?? new InvalidOperationException(
                        $"Unhandled exception object: {e.ExceptionObject}");
                ReportFatal("AppDomain", exception, showDialog: false);
                Environment.ExitCode = 1;
            };

            using var appearance = UiTheme.TrackApplication();
            using var parameterWheel = new ParameterWheelFilter();
            if (new MagOps.Infrastructure.Services.GlobalWorkspaceService().Load().Root.Length == 0)
            {
                using var setup = new WorkspaceSettingsDialog();
                if (setup.ShowDialog() != DialogResult.OK) return;
            }
            System.Windows.Forms.Application.Run(new MainForm());
        }
        catch (Exception ex)
        {
            ReportFatal("Application startup", ex, showDialog: true);
            Environment.ExitCode = 1;
        }
    }

    private static void ReportFatal(string context, Exception exception, bool showDialog)
    {
        if (Interlocked.Exchange(ref _fatalReported, 1) != 0)
            return;

        string details =
            "==========================================\r\n" +
            "MagOps fatal exception\r\n" +
            "==========================================\r\n" +
            $"Time: {DateTimeOffset.Now:O}\r\n" +
            $"Version: {MagOps.Core.AppConstants.AppVersion}\r\n" +
            $"Executable: {Environment.ProcessPath}\r\n" +
            $"Context: {context}\r\n" +
            $"Exception: {exception.GetType().FullName}\r\n" +
            $"Message: {exception.Message}\r\n\r\n" +
            exception +
            "\r\n";

        try
        {
            File.AppendAllText(
                CrashLogPath,
                details,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }
        catch
        {
            // Preserve the original exception even if logging fails.
        }

        try
        {
            Console.Error.WriteLine(details);
        }
        catch
        {
            // WinExe normally has no console; the file log remains authoritative.
        }

        if (!showDialog)
            return;

        try
        {
            MessageBox.Show(
                $"{exception.GetType().FullName}: {exception.Message}\r\n\r\n" +
                $"详细异常和堆栈已写入：\r\n{CrashLogPath}",
                "MagOps 启动/运行异常",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        catch
        {
            // Do not mask the original failure if the UI cannot show a dialog.
        }
    }
}
