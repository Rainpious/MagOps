using MagOps.Core.Domain;
using MagOps.Infrastructure.Services;

namespace MagOps.Desktop;

public sealed partial class MainForm
{
    private DemHeightOptions _demHeightOptions = new();
    private DemHeightSnapshot? _demHeightSnapshot;

    private void CalculateImportedDemHeight(FieldMagFolderAnalysis raw)
    {
        // Derived snapshot only. Never mutates raw samples or recognition/production fingerprints.
        _demHeightSnapshot = null;
        if (!_demHeightOptions.CalculateOnImport || string.IsNullOrWhiteSpace(_demHeightOptions.DemPath)) return;
        _demHeightSnapshot = DemHeightService.Calculate(raw, _demHeightOptions);
    }

    private void ConfigureProcessingDemHeight(AutomaticSegmentationReviewDialog review, string output)
    {
        var options = _demHeightOptions;
        if (_demHeightSnapshot is { } old && (!DemHeightService.DemIsCurrent(old) || !DemHeightService.SourcesAreCurrent(old))) _demHeightSnapshot = null;
        if (string.IsNullOrWhiteSpace(options.DemPath))
        {
            // Only suggest the project's existing terrain. A suggestion never confirms its datum.
            string? terrain = TryLoadProjectTerrainForRoute();
            if (terrain is not null) options = options with { DemPath = terrain };
        }
        review.ConfigureDemHeight(options, _demHeightSnapshot, output,
            (settings, snapshot) =>
            {
                if (!ReferenceEquals(_processingReview, review)) return;
                settings = BindProcessingTerrainSelection(settings);
                _demHeightOptions = settings; _demHeightSnapshot = snapshot; SaveFieldOperationsSettings();
            },
            settings =>
            {
                if (!ReferenceEquals(_processingReview, review)) return;
                settings = BindProcessingTerrainSelection(settings);
                _demHeightOptions = settings; _demHeightSnapshot = null; SaveFieldOperationsSettings();
            }, RememberLatestGeneratedBusinessFile, () => RunUi("下载工区地形", DownloadAw3d30Terrain));
    }

    private DemHeightOptions BindProcessingTerrainSelection(DemHeightOptions settings)
    {
        if(IsProjectInitialized() && !string.IsNullOrWhiteSpace(CurrentWorkareaRelativePath) &&
            File.Exists(settings.DemPath) && settings.DemPath.EndsWith(".terrain.json",StringComparison.OrdinalIgnoreCase))
        {
            string manifest = new WorkareaTerrainService().Archive(Root,CurrentWorkareaRelativePath,settings.DemPath,_folderLayout);
            settings = settings with { DemPath = manifest };
            _routeWorkspace.SetTerrainDemPath(manifest);
        }
        return settings;
    }
    private void ShowProcessingDemHeight()
    {
        BeginProcessingReviewCore(true);
        _processingReview?.ShowDemHeightPage();
    }
}
