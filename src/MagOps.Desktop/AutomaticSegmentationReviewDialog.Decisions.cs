using MagOps.Application;

namespace MagOps.Desktop;

internal sealed partial class AutomaticSegmentationReviewDialog
{
    private readonly ReviewDecisionState _reviewDecisions = new();

    // An edit is a command into the state. Reading a row here is confined to
    // the UI input boundary; request generation never scans the grid.
    private void CommitLegDecisionEdit(DataGridViewRow row)
    {
        if (row.Tag is not ReviewLegRow value) return;
        string choice = Convert.ToString(row.Cells["ManualTask"].Value) ?? "";
        string? taskId = value.TaskIdByOption.TryGetValue(choice, out var id) && !string.IsNullOrWhiteSpace(id) ? id : null;
        var next = new ReviewLegDecision(value.Leg.FlightLegId, row.Cells["Confirmed"].Value is true,
            row.Cells["Include"].Value is true, Convert.ToString(row.Cells["QC"].Value) switch
            { "通过" => "PASS", "不通过" => "FAIL", _ => "PENDING_QC" }, taskId, _reviewDecisions.Get(value.Leg.FlightLegId).ActualRepeat);
        if (!_populatingLegGrid)
        {
            var prior = _reviewDecisions.Get(value.Leg.FlightLegId);
            if (prior.TaskId != next.TaskId || prior.QualityStatus != next.QualityStatus || prior.Confirmed != next.Confirmed)
                ProtectMotherLineDecisions([value.Leg.FlightLegId]);
            if (prior.Included != next.Included) RecordMotherLineInclusion([value.Leg.FlightLegId], next.Included);
        }
        _reviewDecisions.Set(next);
    }

    private void ProjectLegDecision(DataGridViewRow row)
    {
        if (row.Tag is not ReviewLegRow value) return;
        var decision = _reviewDecisions.Get(value.Leg.FlightLegId);
        row.Cells["Confirmed"].Value = decision.Confirmed;
        row.Cells["Include"].Value = decision.Included;
        row.Cells["QC"].Value = decision.QualityStatus switch
            { "PASS" => "通过", "FAIL" => "不通过", _ => "待检查" };
    }
}
