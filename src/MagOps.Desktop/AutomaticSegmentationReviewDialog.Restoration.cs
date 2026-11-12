using MagOps.Application;
using MagOps.Infrastructure.Services;

namespace MagOps.Desktop;

internal sealed partial class AutomaticSegmentationReviewDialog
{
    private readonly CheckBox _autoRestoreMotherLines = new() { Text = "自动恢复母测线", AutoSize = true };
    private bool _updatingRestoration;
    private int _restorationDepth;

    private void InitializeMotherLineRestoration()
    {
        if (!_spatialOnly) return;
        bool legacy = _seedRequest.MotherLineRestoration is null && (_seedRequest.DefaultMatchedSelectionsApplied ||
            _seedRequest.ManualIncludedFlightLegIds.Count > 0 || _seedRequest.ManualLegAssignments.Count > 0 || _seedRequest.ConfirmedPassIds.Count > 0);
        _seedRequest = _seedRequest with { MotherLineRestoration = _seedRequest.MotherLineRestoration ?? new() { ProtectLegacySelection = legacy } };
        _updatingRestoration = true;
        try { _autoRestoreMotherLines.Checked = _seedRequest.MotherLineRestoration.Enabled; }
        finally { _updatingRestoration = false; }
    }

    private void ProtectMotherLineDecisions(IEnumerable<string> ids)
    {
        if (!_spatialOnly || _seedRequest.MotherLineRestoration is not { } state) return;
        _seedRequest = _seedRequest with { MotherLineRestoration = MotherLineRestorationWorkflow.Protect(state, ids) };
    }

    private void RecordMotherLineInclusion(IEnumerable<string> ids, bool include)
    {
        if (!_spatialOnly || _seedRequest.MotherLineRestoration is not { } state) return;
        _seedRequest = _seedRequest with { MotherLineRestoration = MotherLineRestorationWorkflow.Include(state, ids, include) };
    }

    // Runs after initial default selection. Only this spatial-CSV workflow can opt into restoration.
    private bool RestoreMotherLinesIfNeeded()
    {
        if (!_spatialOnly || _audit is null || _restorationDepth > 4) return false;
        string originalState = System.Text.Json.JsonSerializer.Serialize(_seedRequest.MotherLineRestoration);
        InitializeMotherLineRestoration();
        var state = _seedRequest.MotherLineRestoration!;
        string scope = AuditProductionService.RecognitionInputFingerprint(_analysis, _designTasks, _seedRequest, _routeFingerprint);
        if (state.ScopeKey.Length > 0 && state.ScopeKey != scope)
            state = new() { Enabled = state.Enabled, Options = state.Options };
        state = state with { ScopeKey = scope };
        if (state.ProtectLegacySelection)
        {
            var accepted = _reviewDecisions.AcceptedLegIds();
            state = MotherLineRestorationWorkflow.Include(state, _audit.Execution.Legs.Where(l => !accepted.Contains(l.FlightLegId)).Select(l => l.FlightLegId), false);
            var protectedIds = _seedRequest.ManualLegAssignments.Select(a => a.FlightLegId)
                .Concat(_seedRequest.QualityConfirmations.Select(q => q.PassId.EndsWith("/pass") ? q.PassId[..^5] : q.PassId));
            state = MotherLineRestorationWorkflow.Protect(state, protectedIds) with { ProtectLegacySelection = false };
        }
        string key = MotherLineRestorationService.CheckKey(scope, _audit.Execution, state, _reviewDecisions.AcceptedLegIds(),
            _audit.ProductionAnomalies.Select(a => a.AnomalyId + ":" + a.ProductionDecision));
        if (state.CheckedKey == key) { _seedRequest = _seedRequest with { MotherLineRestoration = state }; return false; }
        var candidates = CurrentMotherLineCandidates(ref state);
        state = MotherLineRestorationWorkflow.Refresh(state, candidates);
        if (_restorationDepth >= 4) state = state with { Groups = state.Groups.Select(g => g.Status == "HIGH" ? g with { Status = "REVIEW", AllowShortInclusion = false, Reason = g.Reason + "；已达到本次自动重判上限，请整体核对" } : g).ToArray() };
        if (state.Enabled && _restorationDepth < 4)
            state = MotherLineRestorationWorkflow.Apply(state, state.Groups.Where(g => g.Status == "HIGH").Select(g => g.Id), _reviewDecisions, _audit.Execution, true);
        state = state with { CheckedKey = key };
        _seedRequest = BuildReviewedRequest() with { MotherLineRestoration = state };
        if (System.Text.Json.JsonSerializer.Serialize(state) == originalState) return false;
        _restorationDepth++;
        try { RefreshReviewedExecution(); } finally { _restorationDepth--; }
        return true;
    }

    private IReadOnlyList<MotherLineRestoreGroup> CurrentMotherLineCandidates(ref MotherLineRestorationState state)
    {
        if (_audit is null) return [];
        var result = MotherLineGroupingService.Candidates(_audit, state, _reviewDecisions.AcceptedLegIds());
        state = state with { UnassignedEvidence = result.Evidence };
        return result.Groups;
    }

    private string MotherLineRestorationSummary()
    {
        var groups = _seedRequest.MotherLineRestoration?.Groups ?? [];
        var applied = groups.Where(g => g.Status is "APPLIED_AUTO" or "APPLIED_MANUAL").ToArray();
        int endpointLegs = applied.Where(g => g.Id.StartsWith("endpoint:", StringComparison.Ordinal)).SelectMany(g => g.ShortLegIds).Distinct().Count();
        int remaining = _seedRequest.MotherLineRestoration?.UnassignedEvidence.Count ?? 0;
        return $"母线恢复：已应用 {applied.Length} 组、补入 {applied.Sum(g => g.AddedRecords):N0} 点；端点已归属 {endpointLegs} 段，剩余未归属 {remaining} 段；待核对 {groups.Count(g => g.Status is "HIGH" or "REVIEW")} 组，疑似保留 {groups.Count(g => g.Status == "SUSPECT")} 组，冲突 {groups.Count(g => g.Status == "CONFLICT")} 组。";
    }

    private void RecheckMotherLines()
    {
        if (_seedRequest.MotherLineRestoration is not { } state) return;
        _seedRequest = BuildReviewedRequest() with { MotherLineRestoration = state with { CheckedKey = "" } };
        RefreshReviewedExecution();
    }

    private TabControl? _reviewWorkspaces;
    private TabPage? _motherLinePage;
    private MotherLineRestorationDialog? _motherLineWorkspace;
    private bool _motherLineCommandPending;

    private void RefreshMotherLineWorkspace()
    {
        if (_motherLinePage is null || _motherLineCommandPending) return;
        int filter = _motherLineWorkspace?.FilterIndex ?? 0;
        string? currentGroup = _motherLineWorkspace?.CurrentGroupId;
        _motherLineWorkspace?.Dispose();
        _motherLineWorkspace = null;
        _motherLinePage.Controls.Clear();
        if (_audit is null || _seedRequest.MotherLineRestoration is not { } state)
        {
            _motherLinePage.Controls.Add(new Label { Dock = DockStyle.Fill, Text = "请先完成航段识别，再进入母线整理。", TextAlign = ContentAlignment.MiddleCenter });
            return;
        }
        var workspace = new MotherLineRestorationDialog(state, _audit, _displayAnalysis)
        {
            TopLevel = false, FormBorderStyle = FormBorderStyle.None, Dock = DockStyle.Fill,
            MinimumSize = Size.Empty
        };
        workspace.ConfigureEmbeddedLayout();
        workspace.FilterIndex = filter;
        workspace.RestoreCurrentGroup(currentGroup);
        workspace.ReturnRequested += SelectLegReviewWorkspace;
        workspace.CommandRequested += () =>
        {
            if (_motherLineCommandPending) return;
            _motherLineCommandPending = true;
            workspace.Enabled = false;
            // Finish the current click before replacing its controls. The owning review
            // remains the only business-state authority throughout the command.
            BeginInvoke((Action)(() =>
            {
                try
                {
                    if (!IsDisposed && !Disposing) ApplyMotherLineCommand(workspace);
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or InvalidDataException)
                {
                    _status.Text = "母线操作未完成，请检查当前候选：" + ex.Message;
                }
                finally
                {
                    _motherLineCommandPending = false;
                    if (!IsDisposed && !Disposing) RefreshMotherLineWorkspace();
                }
            }));
        };
        _motherLineWorkspace = workspace;
        _motherLinePage.Controls.Add(workspace);
        workspace.Show();
    }

    private void ShowMotherLineRestoration()
    {
        RecheckMotherLines();
        if (_audit is null || _seedRequest.MotherLineRestoration is not { } state) return;
        if (_reviewWorkspaces is not null && _motherLinePage is not null)
        {
            if (_reviewWorkspaces.SelectedTab == _motherLinePage) RefreshMotherLineWorkspace();
            else _reviewWorkspaces.SelectedTab = _motherLinePage;
            return;
        }
        using var dialog = new MotherLineRestorationDialog(state, _audit, _displayAnalysis);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        ApplyMotherLineCommand(dialog);
    }

    private void ApplyMotherLineCommand(MotherLineRestorationDialog dialog)
    {
        if (_audit is null || _seedRequest.MotherLineRestoration is not { } state) return;
        // Re-evaluate current candidates before a batch command; old history alone cannot grant inclusion.
        var currentCandidates = CurrentMotherLineCandidates(ref state);
        state = MotherLineRestorationWorkflow.Refresh(state, currentCandidates);
        int beforeApplied = state.Groups.Count(g => g.Status is "APPLIED_AUTO" or "APPLIED_MANUAL");
        int beforeUndone = state.Groups.Count(g => g.Status == "UNDONE");
        if (dialog.Command == "ASSIGN" && dialog.TargetOverride is { } explicitTarget)
        {
            state = MotherLineRestorationWorkflow.RecordManual(state, dialog.LegIds.Select(id => new ManualFlightLegAssignment(id, explicitTarget, "用户明确指定完整组合")).ToArray(), _reviewDecisions, _audit.Execution, _tasks);
        }
        state = dialog.Command switch
        {
            "APPLY" => MotherLineRestorationWorkflow.Apply(state, dialog.GroupIds, _reviewDecisions, _audit.Execution, false),
            "UNDO" => MotherLineRestorationWorkflow.Undo(state, dialog.GroupIds, _reviewDecisions),
            "REJECT" => MotherLineRestorationWorkflow.Reject(state, dialog.GroupIds),
            "REOPEN" => MotherLineRestorationWorkflow.Reopen(state, dialog.GroupIds),
            "RELEASE" => state with { ProtectedLegIds = state.ProtectedLegIds.Except(dialog.LegIds).ToArray(), CheckedKey = "" },
            "RELEASE_INCLUDE" => state with { ExcludedLegIds = state.ExcludedLegIds.Except(dialog.LegIds).ToArray(), CheckedKey = "" },
            "EXCLUDE" => MotherLineRestorationWorkflow.Include(state, dialog.LegIds, false),
            "INCLUDE" => MotherLineRestorationWorkflow.Include(state, dialog.LegIds, true),
            _ => state
        };
        if (dialog.Command is "EXCLUDE" or "INCLUDE")
        {
            foreach (string id in dialog.LegIds)
                _reviewDecisions.Set(_reviewDecisions.Get(id) with { Included = dialog.Command == "INCLUDE", Confirmed = dialog.Command == "INCLUDE" && _reviewDecisions.Get(id).Confirmed });
            state = state with { Groups = state.Groups.Select(g => dialog.GroupIds.Contains(g.Id) && dialog.Command == "EXCLUDE" ? g with
                { Status = "EXCLUDED" } : g).ToArray() };
            if (dialog.Command == "INCLUDE") state = MotherLineRestorationWorkflow.Reopen(state, dialog.GroupIds);
        }
        _seedRequest = BuildReviewedRequest() with { MotherLineRestoration = state };
        RefreshReviewedExecution();
        int changed = dialog.Command == "UNDO" ? state.Groups.Count(g => g.Status == "UNDONE") - beforeUndone :
            state.Groups.Count(g => g.Status is "APPLIED_AUTO" or "APPLIED_MANUAL") - beforeApplied;
        _status.Text = MotherLineRestorationSummary() + (dialog.Command is "APPLY" or "UNDO" ? $"本次{dialog.Command}处理{changed}组，保留/跳过{Math.Max(0, dialog.GroupIds.Count - changed)}组。" : "") + "人工决定和冲突保持保护。";
    }

    private void UndoLatestMotherLineBatch()
    {
        if (_seedRequest.MotherLineRestoration is not { } state) return;
        string? batch = state.Groups.LastOrDefault(g => g.Status is "APPLIED_AUTO" or "APPLIED_MANUAL")?.BatchId;
        if (batch is null) { _status.Text = "没有可撤销的归并批次。"; return; }
        var ids = state.Groups.Where(g => g.BatchId == batch && g.Status is "APPLIED_AUTO" or "APPLIED_MANUAL").Select(g => g.Id).ToArray();
        var next = MotherLineRestorationWorkflow.Undo(state, ids, _reviewDecisions);
        _seedRequest = BuildReviewedRequest() with { MotherLineRestoration = next };
        RefreshReviewedExecution();
        _status.Text = $"已撤销{next.Groups.Count(g => ids.Contains(g.Id) && g.Status == "UNDONE")}组，共{ids.Length}组；后续决定有依赖或已修改的组保留，请在母线整理中查看。编号随归属恢复。";
    }

    private void EditMotherLineRestorationOptions()
    {
        if (_seedRequest.MotherLineRestoration is not { } state) return;
        using var dialog = new MotherLineRestoreOptionsDialog(state.Options);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _seedRequest = BuildReviewedRequest() with { MotherLineRestoration = state with { Options = dialog.Result!, CheckedKey = "" } };
        RefreshReviewedExecution();
    }
}
