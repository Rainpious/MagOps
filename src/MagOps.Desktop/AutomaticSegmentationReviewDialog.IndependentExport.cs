using MagOps.Application;
using MagOps.Infrastructure.Services;

namespace MagOps.Desktop;

internal sealed partial class AutomaticSegmentationReviewDialog
{
    private bool CheckIndependentLegExport()
    {
        if (!_spatialOnly || _audit is null || _seedRequest.MotherLineRestoration is not { } state) return true;
        string scope = AuditProductionService.RecognitionInputFingerprint(_analysis, _designTasks, BuildReviewedRequest(), _routeFingerprint);
        var remaining = MotherLineRestorationWorkflow.UnconfirmedIndependent(_audit, state, scope);
        if (remaining.Count == 0) return true;
        using var dialog = new IndependentLegExportDialog(remaining, _audit, state, _displayAnalysis);
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Command == "REVIEW")
        { ShowMotherLineRestoration(); return false; }
        var ids = remaining.Select(l => l.FlightLegId).ToArray();
        if (dialog.Command == "KEEP")
            state = state with { IndependentDecisionScope = scope, RetainedIndependentLegIds =
                (state.IndependentDecisionScope == scope ? state.RetainedIndependentLegIds : []).Concat(ids).Distinct().ToArray() };
        else
        {
            _reviewDecisions.Move(ids.Select(id => (id, false)), false);
            state = MotherLineRestorationWorkflow.Include(state, ids, false);
        }
        _seedRequest = BuildReviewedRequest() with { MotherLineRestoration = state };
        RefreshReviewedExecution();
        return _audit is not null && _audit.SelectedSamples.Count > 0;
    }
}
