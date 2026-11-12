using System.Text.Json;
using MagOps.Infrastructure.Services;

namespace MagOps.Desktop;

internal sealed partial class AutomaticSegmentationReviewDialog
{
    private TabPage? _snapshotPage;
    private bool _snapshotPending;

    internal void OfferSavedSnapshot(AuditProductionRequest snapshot, string path)
    {
        SegmentationReviewStore.ValidateRestoration(snapshot.MotherLineRestoration);
        _snapshotPending = true;
        _snapshotPage ??= new TabPage("已有核验记录");
        foreach (Control old in _snapshotPage.Controls.Cast<Control>().ToArray()) old.Dispose();
        if (!_reviewWorkspaces!.TabPages.Contains(_snapshotPage)) _reviewWorkspaces.TabPages.Add(_snapshotPage);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new(12) };
        layout.ColumnStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.Absolute, 54));
        layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.Controls.Add(new Label { Dock = DockStyle.Fill, Text = "发现已有核验记录；当前选择保持原状。恢复前请查看差异，或忽略记录后继续新核验。" }, 0, 0);
        var problems = SnapshotProblems(snapshot);
        string time = File.Exists(path) ? File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm:ss") : "时间未知";
        layout.Controls.Add(new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both,
            Text = $"记录：{path}\r\n保存时间：{time}\r\n" +
                $"窗口 {snapshot.MissionWindows.Count}；显式纳入 {snapshot.ManualIncludedFlightLegIds.Count} 段；人工归属 {snapshot.ManualLegAssignments.Count}；质量决定 {snapshot.QualityConfirmations.Count}。\r\n" +
                $"来源：{snapshot.SourceFingerprint}\r\n航线：{snapshot.RouteFingerprint}\r\n" +
                (problems.Count == 0 ? "来源与当前输入一致，可查看差异后恢复。" : "此记录已过期或不适用，仅供查看：\r\n" + string.Join("\r\n", problems)) }, 0, 1);
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        actions.Controls.Add(UiComponents.Button("查看差异并恢复", (_, _) => ShowSnapshotDifferences(snapshot, path), UiButtonRole.Primary));
        actions.Controls.Add(UiComponents.Button("忽略此记录", (_, _) => DismissSavedSnapshot()));
        layout.Controls.Add(actions, 0, 2);
        _snapshotPage.Controls.Add(layout);
        _reviewWorkspaces.SelectedTab = _snapshotPage;
    }

    internal void DismissSavedSnapshot()
    {
        _snapshotPending = false;
        if (_snapshotPage is not null)
        {
            _reviewWorkspaces!.TabPages.Remove(_snapshotPage);
            _snapshotPage.Dispose(); _snapshotPage = null;
        }
        SelectLegReviewWorkspace();
        _status.Text = "已忽略已有记录；当前选择保持原状。后续保存将使用当前核验结果。";
    }

    private IReadOnlyList<string> SnapshotProblems(AuditProductionRequest snapshot)
    {
        var reasons = new List<string>();
        if (snapshot.SourceFingerprint != _analysis.Fingerprint) reasons.Add("原始数据或中间 CSV 内容已变化。");
        if (snapshot.RouteFingerprint != _routeFingerprint) reasons.Add("当前航线任务版本已变化。");
        if (snapshot.RecognitionVersion != AuditProductionService.RecognitionVersion(snapshot)) reasons.Add("识别算法版本已变化。");
        if (snapshot.TrackSegmentationFingerprint != TrackSegmentationReferenceService.Fingerprint(snapshot.TrackSegmentation)) reasons.Add("记录中的识别参数指纹不一致。");
        if (snapshot.ProjectCentralMeridian is { } saved && _referenceCentralMeridian is { } current && Math.Abs(saved - current) > 1e-9)
            reasons.Add("项目中央经线已变化。");
        if (_seedRequest.TrackSegmentation is not null && snapshot.TrackSegmentation is not null &&
            TrackSegmentationReferenceService.Fingerprint(_seedRequest.TrackSegmentation) != TrackSegmentationReferenceService.Fingerprint(snapshot.TrackSegmentation))
            reasons.Add("当前识别参数与记录不同，请按当前输入重新核验。");
        if (_seedRequest.SpatialScreening is { } currentScope && snapshot.SpatialScreening is { } savedScope &&
            (currentScope.Range != savedScope.Range || currentScope.Options != savedScope.Options))
            reasons.Add("工区筛选范围或规则已变化。");
        if (_taskScopeBackup is not null) reasons.Add("请先应用或取消当前任务范围修改。");
        return reasons;
    }

    private void ShowSnapshotDifferences(AuditProductionRequest snapshot, string path)
    {
        var problems = SnapshotProblems(snapshot);
        string revision = WorkspaceReviewFingerprint();
        using var dialog = new ReviewSnapshotDifferenceDialog(BuildReviewedRequest(), snapshot, problems);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            // Re-read exactly the reviewed file; a newer or altered file requires a new comparison.
            if (!File.Exists(path))
            { _status.Text = "保存记录已被移走或删除，请重新载入；当前选择保持原状。"; return; }
            if (ProductionSnapshotService.RequestFingerprint(SegmentationReviewStore.LoadFile(path)) !=
                ProductionSnapshotService.RequestFingerprint(snapshot))
            { _status.Text = "保存记录已变化，请重新载入并查看差异。"; return; }
            if (!TryRestoreSavedSnapshot(snapshot, revision, true)) return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException)
        { _status.Text = "恢复未完成：" + ex.Message; }
    }

    internal bool TryRestoreSavedSnapshot(AuditProductionRequest snapshot, string expectedRevision, bool confirmed)
    {
        if (!confirmed || WorkspaceReviewFingerprint() != expectedRevision || SnapshotProblems(snapshot).Count > 0)
        { _status.Text = "当前输入或选择已变化，未替换任何决定；请重新查看差异并确认。"; return false; }
        AuditProductionRequest previous = BuildReviewedRequest();
        bool wasPending = _snapshotPending;
        try
        {
            ApplyLoadedSnapshot(snapshot);
            if (_audit is null) throw new InvalidDataException("记录无法用于当前核验，已恢复之前的选择。");
            DismissSavedSnapshot();
            _status.Text = "已按确认的差异恢复核验记录；请检查航段，保存后再用于成果。";
            return true;
        }
        catch
        {
            ApplyLoadedSnapshot(previous);
            _snapshotPending = wasPending;
            throw;
        }
    }
}

internal sealed class ReviewSnapshotDifferenceDialog : Form
{
    internal ReviewSnapshotDifferenceDialog(AuditProductionRequest current, AuditProductionRequest saved, IReadOnlyList<string> problems)
    {
        Text = "恢复核验记录：比较并确认"; Size = new(1000, 680); MinimumSize = new(780, 520);
        StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1, Padding = new(10) };
        layout.ColumnStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.Absolute, 72)); layout.RowStyles.Add(new(SizeType.Percent, 62));
        layout.RowStyles.Add(new(SizeType.Percent, 38)); layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.Controls.Add(new TextBox { Dock = DockStyle.Fill, ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical,
            Text = problems.Count > 0 ? "不可恢复：" + string.Join("；", problems) : "确认后将以右列记录替换当前窗口、纳入范围、人工归属、质量及母线决定；取消保持当前选择。原始观测不变。" }, 0, 0);
        var grid = new DataGridView { Name = "SnapshotDifferences", Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false,
            RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill };
        grid.Columns.Add("Field", "内容"); grid.Columns.Add("Current", "当前值"); grid.Columns.Add("Saved", "记录值");
        var details = new TextBox { Dock = DockStyle.Fill, ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Both,
            WordWrap = false, Font = new Font("Consolas", 9f) };
        void ShowDetails(int index)
        {
            if (index < 0 || index >= grid.Rows.Count) return;
            details.Text = $"字段：{grid.Rows[index].Cells[0].Value}\r\n当前值：\r\n{grid.Rows[index].Cells[1].ToolTipText}\r\n\r\n记录值：\r\n{grid.Rows[index].Cells[2].ToolTipText}";
        }
        void Row(string name, object? left, object? right)
        {
            string a = JsonSerializer.Serialize(left), b = JsonSerializer.Serialize(right);
            int index = grid.Rows.Add(name, a, b);
            if (a != b) grid.Rows[index].DefaultCellStyle.BackColor = UiTheme.WarningSoft;
            grid.Rows[index].Cells[1].ToolTipText = a; grid.Rows[index].Cells[2].ToolTipText = b;
        }
        Row("来源指纹", current.SourceFingerprint, saved.SourceFingerprint);
        Row("航线指纹", current.RouteFingerprint, saved.RouteFingerprint);
        Row("中央经线", current.ProjectCentralMeridian, saved.ProjectCentralMeridian);
        Row("飞行窗口与任务", current.MissionWindows, saved.MissionWindows);
        Row("纳入航段", current.ManualIncludedFlightLegIds, saved.ManualIncludedFlightLegIds);
        Row("确认测线", current.ConfirmedPassIds, saved.ConfirmedPassIds);
        Row("人工归属", current.ManualLegAssignments, saved.ManualLegAssignments);
        Row("质量决定", current.QualityConfirmations, saved.QualityConfirmations);
        Row("异常决定", current.AnomalyDecisions, saved.AnomalyDecisions);
        Row("人工覆盖", current.Overrides, saved.Overrides);
        Row("空间筛选", current.SpatialScreening, saved.SpatialScreening);
        Row("识别参数", current.TrackSegmentation, saved.TrackSegmentation);
        Row("飞行质量参数", current.FlightQualityOptions, saved.FlightQualityOptions);
        Row("母线归并与保护", current.MotherLineRestoration, saved.MotherLineRestoration);
        grid.CurrentCellChanged += (_, _) => ShowDetails(grid.CurrentCell?.RowIndex ?? -1);
        layout.Controls.Add(grid, 0, 1);
        layout.Controls.Add(details, 0, 2);
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var cancel = UiComponents.Button("取消，保留当前选择", (_, _) => { DialogResult = DialogResult.Cancel; Close(); });
        var restore = UiComponents.Button("确认恢复并替换当前选择", (_, _) => { DialogResult = DialogResult.OK; Close(); }, UiButtonRole.Primary);
        restore.Enabled = problems.Count == 0;
        actions.Controls.Add(cancel); actions.Controls.Add(restore); layout.Controls.Add(actions, 0, 3); Controls.Add(layout);
        if (grid.Rows.Count > 0) ShowDetails(0);
        CancelButton = cancel;
    }
}
