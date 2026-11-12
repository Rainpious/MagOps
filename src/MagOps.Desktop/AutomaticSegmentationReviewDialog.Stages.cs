namespace MagOps.Desktop;

internal sealed partial class AutomaticSegmentationReviewDialog
{
    private TabPage? _processingDataPage;
    private TabPage? _processingOutputPage;
    internal event Action? WorkspaceStateChanged;
    internal string WorkspaceStage => _reviewWorkspaces?.SelectedTab?.Text ?? "航段核验";
    internal string WorkspaceState => _snapshotPending ? "已有核验记录待处理" :
        _taskScopeBackup is not null ? "任务范围编辑中" :
        _analysisInputChanged ? "输入已调整，待重新核验" :
        _audit is null ? "尚无有效核验结果" :
        HasUnsavedWorkspaceReview ? "有未保存修改" : "当前核验无未保存修改";
    private void NotifyWorkspaceStateChanged() => WorkspaceStateChanged?.Invoke();

    internal void ConfigureProcessingStages(Control sourceActions, Control outputActions)
    {
        if (_processingDataPage is not null) throw new InvalidOperationException("当前会话已经配置处理工作区。");
        var workspaces = _reviewWorkspaces!;
        var review = workspaces.TabPages.Cast<TabPage>().Single(p => p.Text == "航段核验");
        _processingDataPage = new TabPage("数据与识别") { AutoScroll = true };
        _processingOutputPage = new TabPage("成果与处理") { AutoScroll = true };
        workspaces.TabPages.Insert(0, _processingDataPage);
        if (_motherLinePage is null)
        {
            var unavailable = new TabPage("母线整理");
            unavailable.Controls.Add(new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
                Text = "母线归并目前适用于中间 CSV 空间分线。\r\n当前原始数据核验请在“航段核验”中确认设计测线归属。" });
            workspaces.TabPages.Add(unavailable);
        }
        workspaces.TabPages.Add(_processingOutputPage);

        var root = (TableLayoutPanel)Controls[0];
        var modeBar = root.GetControlFromPosition(0, 0)!;
        root.Controls.Remove(modeBar);
        var inputs = StageLayout("选择来源并设置识别参数；更新识别前先保存或放弃当前航段决定。", sourceActions);
        inputs.Controls.Add(modeBar, 0, 2);
        inputs.RowCount = 3;
        inputs.RowStyles.Add(new(SizeType.AutoSize));
        _processingDataPage.Controls.Add(inputs);
        _processingOutputPage.Controls.Add(StageLayout(
            _spatialOnly ? "按当前核验选择导出空间分线 CSV；保留原列原值，Line / LineOrder 位于前两列。" :
                "使用当前右侧纳入航段进行处理；日变、T0、IGRF 和成果校验沿用现有规则。", outputActions));
        workspaces.SelectedTab = review;
    }

    private static TableLayoutPanel StageLayout(string description, Control actions)
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, RowCount = 2, Padding = new(8) };
        layout.ColumnStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.Absolute, 54));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.Controls.Add(new Label { Dock = DockStyle.Fill, Text = description }, 0, 0);
        layout.Controls.Add(actions, 0, 1);
        return layout;
    }

    private void SelectLegReviewWorkspace() => _reviewWorkspaces!.SelectedTab =
        _reviewWorkspaces.TabPages.Cast<TabPage>().Single(p => p.Text == "航段核验");

    internal void ActivateReviewWorkspace()
    {
        if (_snapshotPending && _snapshotPage is not null) _reviewWorkspaces!.SelectedTab = _snapshotPage;
        else SelectLegReviewWorkspace();
        Focus();
    }
}
