using MagOps.Infrastructure.Services;

namespace MagOps.Desktop;

public sealed partial class MainForm
{
    private readonly Dictionary<string, FileInteractionSession> _pendingFileOutputs = new(StringComparer.OrdinalIgnoreCase);
    private string TrackFileOutput(string path, string operation)
    {
        if (_pendingFileOutputs.Count > 64) _pendingFileOutputs.Clear();
        _pendingFileOutputs[Path.GetFullPath(path)] = FileInteractions.For(this, operation, "output",
            _folderLayout.GetOperation(operation).Scope.Equals("project", StringComparison.OrdinalIgnoreCase));
        return path;
    }
    internal string FileInteractionBusinessDirectory(string operation)
    {
        if (!IsProjectInitialized()) return "";
        try
        {
            string key = operation switch
            {
                "coordinate-template" or "coordinate-boundary" or "route-boundary" => "coordinate_kml_range",
                "route-ply" => "coordinate_oasis_ply", "route-endpoints" => "route_endpoint_design",
                "route-project" => "route_project", "quality-kml" or "quality-kml-directory" or "external-route-kml" => "route_export",
                "coordinate-registry" or "spatial-convert" or "spatial-audit" or "reference-road" => "coordinate_export",
                "quality-output" => "processing_analysis", _ => operation
            };
            if (operation == "terrain-import") return new TerrainLibraryService().Settings().DefaultDirectory;
            if (operation is "field-data-folder" or "raw-intake" or "field-intake" or "quality-source")
            {
                string folder = operation is "raw-intake" or "field-intake" ? "workarea.raw" : operation == "quality-source" ? "workarea.processing" : "workarea.field";
                return CurrentWorkareaRelativePath.Length == 0 ? Root : _folderLayout.ResolveWorkareaFolderFromRoot(Path.Combine(Root, CurrentWorkareaRelativePath), folder);
            }
            return ManagedOutputPreview(key, CurrentWorkareaRelativePath);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or IOException or ArgumentException) { return ""; }
    }
    private bool _lastGeneratedIsDirectory;
    private string _lastFileHistoryWarning = "";
    internal (FileInteractionScope Scope, string WorkareaDirectory) FileInteractionContext(bool projectLevel)
    {
        if (!IsProjectInitialized()) return (new("", ""), "");
        string area = projectLevel ? "" : CurrentWorkareaId;
        string directory = !projectLevel && CurrentWorkareaDirectoryResolver.TryResolve(Root, CurrentWorkareaRelativePath, out string found, out _)
            ? found : "";
        return (new(Path.GetFullPath(Root), area), directory);
    }
    internal void FileHistoryWarning(string warning)
    {
        if (warning != _lastFileHistoryWarning)
        {
            _lastFileHistoryWarning = warning;
            if (IsDisposed || Disposing) return;
            if (IsHandleCreated)
            {
                try { BeginInvoke(() => { if (!IsDisposed && !Disposing) SetStatus(warning); }); }
                catch (InvalidOperationException) { /* Closing a UI must not undo completed output. */ }
            }
            else SetStatus(warning);
        }
    }
    internal void ShowRecentFileResult(string path, bool directory)
    {
        _lastGeneratedBusinessFile = Path.GetFullPath(path); _lastGeneratedIsDirectory = directory;
        _openLatestGeneratedArtifact.Enabled = true;
        _openLatestGeneratedArtifact.Text = "定位最近成果";
        _toolTip.SetToolTip(_openLatestGeneratedArtifact, _lastGeneratedBusinessFile);
    }
    private void RestoreLatestFileResult()
    {
        ClearLatestGeneratedBusinessFile();
        _pendingFileOutputs.Clear();
        var scope = FileInteractionContext(projectLevel: false).Scope;
        var history = new FileInteractionHistoryService();
        var raw = history.LastResult(scope, "raw-intake");
        _lastRawImportDirectory = raw is { IsDirectory: true } && Directory.Exists(raw.Path) ? raw.Path : "";
        var result = history.LastResult(scope);
        if (result is null) return;
        if (File.Exists(result.Path))
        {
            var metadata = GeneratedArtifactIdentity.Inspect(result.Path).Metadata;
            if (metadata is not null && IsProjectInitialized())
            {
                if (metadata.ProjectId != _workspace.GetWorkspace(Root).ProjectId ||
                    (metadata.ContextStatus == "WORKAREA" && metadata.WorkareaId != scope.WorkareaId) ||
                    (metadata.ContextStatus == "PROJECT" && scope.WorkareaId.Length > 0)) return;
            }
            if (result.ArtifactId.Length > 0 && IsProjectInitialized() && !_artifacts.List(Root).Any(x =>
                x.ArtifactId == result.ArtifactId && Path.GetFullPath(Path.Combine(Root, x.Path)).Equals(result.Path, StringComparison.OrdinalIgnoreCase))) return;
        }
        ShowRecentFileResult(result.Path, result.IsDirectory);
    }
}
