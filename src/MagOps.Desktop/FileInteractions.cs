using MagOps.Infrastructure.Services;

namespace MagOps.Desktop;

internal sealed class FileInteractionSession
{
    private readonly MainForm? _main;
    internal FileInteractionScope Scope { get; }
    private readonly string _operation, _direction, _workareaDirectory;
    private readonly FileInteractionHistoryService _history = new();
    internal FileInteractionSession(Control owner, string operation, string direction = "input", bool projectLevel = false, bool global = false)
    {
        _main = FileInteractions.FindMain(owner);
        (Scope, _workareaDirectory) = global || _main is null
            ? (new FileInteractionScope("", ""), "") : _main.FileInteractionContext(projectLevel);
        _operation = operation; _direction = direction;
    }
    internal void Prepare(FileDialog dialog, string current = "", string business = "")
    {
        string initial = Initial(current, business.Length == 0 ? dialog.InitialDirectory : business);
        if (initial.Length > 0) dialog.InitialDirectory = initial;
        dialog.RestoreDirectory = true;
    }
    internal void Prepare(FolderBrowserDialog dialog, string current = "", string business = "")
    {
        string initial = Initial(current, business);
        if (initial.Length > 0) dialog.SelectedPath = initial;
    }
    internal string Initial(string current = "", string business = "") =>
        _history.InitialDirectory(Scope, _operation, _direction, current,
            business.Length > 0 ? business : _main?.FileInteractionBusinessDirectory(_operation) ?? "", _workareaDirectory);
    internal void InputSucceeded(params string[] paths)
    {
        _history.RememberInputs(Scope, _operation, paths, out string? warning); Notice(warning);
    }
    internal void DirectorySucceeded(string directory)
    {
        _history.RememberDirectory(Scope, _operation, _direction, directory, out string? warning); Notice(warning);
    }
    internal void OutputSucceeded(string path, bool directory = false, string artifactId = "")
    {
        _history.RememberResult(Scope, _operation, path, directory, artifactId, out string? warning);
        if (_main is not null && _main.FileInteractionContext(
                Scope.ProjectRoot.Length > 0 && Scope.WorkareaId.Length == 0).Scope.Key == Scope.Key)
            _main.ShowRecentFileResult(path, directory);
        Notice(warning);
    }
    private void Notice(string? warning) { if (warning is not null) _main?.FileHistoryWarning(warning); }
}

internal static class FileInteractions
{
    internal static MainForm? FindMain(Control owner)
    {
        for (Form? form = owner as Form ?? owner.FindForm(); form is not null; form = form.Owner)
            if (form is MainForm main) return main;
        return null;
    }
    internal static FileInteractionSession For(Control owner, string operation, string direction = "input", bool projectLevel = false, bool global = false) =>
        new(owner, operation, direction, projectLevel, global);
}
