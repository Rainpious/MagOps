namespace MagOps.Desktop;

public sealed partial class MainForm
{
    private void BuildQualityCheckTab()
    {
        var workspace = new QualityCheckWorkspaceControl();
        workspace.ResolveOffsetReview = () => ProcessingReviewIsCurrent() && _processingReview is { IsDisposed: false } review
            ? review.CaptureOffsetReferences() : null;
        RegisterFeatureControl(FeatureIds.QualityChecks, workspace);
    }
}
