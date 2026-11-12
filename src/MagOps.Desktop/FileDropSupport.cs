namespace MagOps.Desktop;

internal static class FileDropSupport
{
    internal sealed class Gate { internal bool Busy; }

    public static void EnableMultipleAsync(Control control, Func<IReadOnlyList<string>, Task> onFiles,
        Gate gate, Func<string, bool>? accept = null, Func<bool>? canImport = null,
        Action<string>? onError = null, bool allowDirectories = false)
    {
        bool Ready() => !gate.Busy && control.Enabled && (canImport?.Invoke() ?? true);
        string[] Paths(DragEventArgs e)
        {
            if (!allowDirectories) return GetFiles(e);
            if (e.Data?.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return [];
            try
            {
                if (paths.Any(p => !File.Exists(p) && !Directory.Exists(p))) return [];
                return paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { return []; }
        }
        bool Accepted(DragEventArgs e, string[] paths) => Ready() && (e.AllowedEffect & DragDropEffects.Copy) != 0 &&
            IsAccepted(paths, accept, 1, null);
        control.AllowDrop = true;
        control.DragEnter += (_, e) => e.Effect = Accepted(e, Paths(e)) ? DragDropEffects.Copy : DragDropEffects.None;
        control.DragDrop += async (_, e) =>
        {
            string[] paths = Paths(e); e.Effect = DragDropEffects.None;
            if (!Accepted(e, paths)) { onError?.Invoke("入口忙碌或拖入内容无效，本次未导入。"); return; }
            gate.Busy = true;
            try { await onFiles(paths); e.Effect = DragDropEffects.Copy; }
            catch (OperationCanceledException) { onError?.Invoke("导入已取消。"); }
            catch (Exception ex) { onError?.Invoke(ex.Message); }
            finally { gate.Busy = false; }
        };
    }

    public static void EnableDirectory(Control control, Action<string> onDirectory,
        Func<bool>? canImport = null, Action<string>? onError = null)
    {
        bool busy = false;
        string? DirectoryPath(DragEventArgs e)
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is not string[] { Length: 1 } paths ||
                !Directory.Exists(paths[0])) return null;
            try { return Path.GetFullPath(paths[0]); }
            catch (ArgumentException) { return null; }
        }
        bool Ready(DragEventArgs e) => !busy && control.Enabled && (canImport?.Invoke() ?? true) &&
            (e.AllowedEffect & DragDropEffects.Copy) != 0;
        control.AllowDrop = true;
        control.DragEnter += (_, e) => e.Effect = Ready(e) && DirectoryPath(e) is not null ? DragDropEffects.Copy : DragDropEffects.None;
        control.DragDrop += (_, e) =>
        {
            string? path = DirectoryPath(e); e.Effect = DragDropEffects.None;
            if (!Ready(e) || path is null) { onError?.Invoke("仅接收一个现存目录；不支持文件、混合投放或忙碌时导入。"); return; }
            busy = true;
            try { onDirectory(path); e.Effect = DragDropEffects.Copy; }
            catch (OperationCanceledException) { }
            catch (Exception ex) { onError?.Invoke(ex.Message); }
            finally { busy = false; }
        };
    }

    public static void EnableSingle(
        Control control,
        Action<string> onFile,
        Func<string, bool>? accept = null,
        Func<bool>? canImport = null,
        Action<string>? onError = null)
    {
        Enable(control, files => onFile(files[0]), accept, minimumFileCount: 1, maximumFileCount: 1, canImport, onError);
    }

    public static void EnableMultiple(
        Control control,
        Action<IReadOnlyList<string>> onFiles,
        Func<string, bool>? accept = null,
        int minimumFileCount = 1,
        Func<bool>? canImport = null,
        Action<string>? onError = null)
    {
        Enable(control, onFiles, accept, minimumFileCount, maximumFileCount: null, canImport, onError);
    }

    public static bool HasExtension(string path, params string[] extensions)
    {
        string extension = Path.GetExtension(path);
        return extensions.Any(x =>
            string.Equals(extension, x, StringComparison.OrdinalIgnoreCase));
    }

    private static void Enable(
        Control control,
        Action<IReadOnlyList<string>> onFiles,
        Func<string, bool>? accept,
        int minimumFileCount,
        int? maximumFileCount,
        Func<bool>? canImport,
        Action<string>? onError)
    {
        bool importing = false;
        bool Ready() => !importing && control.Enabled && (canImport?.Invoke() ?? true);
        control.AllowDrop = true;

        control.DragEnter += (_, e) =>
        {
            string[] files = GetFiles(e);
            e.Effect = Ready() && (e.AllowedEffect & DragDropEffects.Copy) != 0 && IsAccepted(files, accept, minimumFileCount, maximumFileCount)
                ? DragDropEffects.Copy
                : DragDropEffects.None;
        };

        control.DragDrop += (_, e) =>
        {
            string[] files = GetFiles(e);
            e.Effect = DragDropEffects.None;
            if (!Ready()) { onError?.Invoke("正在处理或当前入口不可用，请稍后重试。"); return; }
            if ((e.AllowedEffect & DragDropEffects.Copy) == 0 || !IsAccepted(files, accept, minimumFileCount, maximumFileCount))
            { onError?.Invoke("拖入内容不符合此入口要求，请核对文件类型和数量；不支持文件夹。"); return; }
            importing = true;
            try { onFiles(files); e.Effect = DragDropEffects.Copy; }
            catch (Exception ex) when (onError is not null) { onError(ex.Message); }
            finally { importing = false; }
        };
    }

    private static string[] GetFiles(DragEventArgs e)
    {
        if (e.Data?.GetDataPresent(DataFormats.FileDrop) != true ||
            e.Data.GetData(DataFormats.FileDrop) is not string[] paths)
        {
            return [];
        }

        // Reject the whole drop rather than silently importing a subset.
        try
        {
            if (paths.Length == 0 || paths.Any(p => string.IsNullOrWhiteSpace(p) || !File.Exists(p))) return [];
            return paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        { return []; }
    }

    private static bool IsAccepted(
        IReadOnlyList<string> files,
        Func<string, bool>? accept,
        int minimumFileCount,
        int? maximumFileCount)
    {
        if (files.Count < Math.Max(1, minimumFileCount))
            return false;
        if (maximumFileCount.HasValue && files.Count > maximumFileCount.Value)
            return false;
        return accept is null || files.All(accept);
    }
}
