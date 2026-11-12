using MagOps.Core.Domain;
using MagOps.Infrastructure.Services;

namespace MagOps.Desktop;

public sealed partial class MainForm
{
    private readonly TabControl _projectWorkspaceTabs = new();
    private readonly ComboBox _directoryWorkarea = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _directoryName = new();
    private readonly TextBox _directoryDetails = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false
    };
    private readonly CheckBox _directoryConsent = new() { Text = "已核对预览，确认更名并更新列出的路径引用", AutoSize = true };
    private readonly CheckBox _directoryRecoveryConsent = new() { Text = "确认恢复所选未完成事务", AutoSize = true };
    private readonly ComboBox _directoryRecovery = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private Button? _directoryApply;
    private Button? _directoryRecover;
    private WorkareaDirectoryRenamePlan? _directoryPlan;
    private bool _directoryBusy;
    private bool _refreshingDirectories;

    private sealed record DirectoryAreaChoice(string Id, string Path, string Name)
    {
        public override string ToString() => $"{Name}  [{Id}]  ·  {Path}";
    }

    private TabPage BuildDirectoryManagementPage()
    {
        var page = new TabPage("目录管理") { AutoScroll = true, BackColor = UiTheme.Workspace };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Padding = new Padding(12), Margin = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void Add(Control control)
        {
            control.Dock = DockStyle.Fill;
            control.Margin = new Padding(0, 0, 0, 8);
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(control, 0, layout.RowCount++);
        }
        layout.RowCount = 0;
        Add(new Label { AutoSize = true, Text = "工区目录更名：先预览引用，再勾选确认并执行。稳定工区身份与显示名称保留。\r\n当前支持同一父目录内更名；项目根目录更名与删除仍待实施。", ForeColor = UiTheme.TextSecondary });
        Add(new Label { AutoSize = true, Text = "选择已登记工区（名称 / 稳定身份 / 项目内路径）" });
        Add(_directoryWorkarea);
        Add(new Label { AutoSize = true, Text = "新的文件夹名称" });
        Add(_directoryName);
        var actions = new FlowLayoutPanel { AutoSize = true, WrapContents = true };
        actions.Controls.Add(Button("预览更名影响", async (_, _) => await PreviewDirectoryRenameAsync(), UiButtonRole.Primary));
        actions.Controls.Add(Button("刷新目录", (_, _) => RunUi("刷新目录", RefreshDirectoryManagement), UiButtonRole.Subtle));
        actions.Controls.Add(Button("打开所选目录", (_, _) => RunUi("打开目录", () =>
        {
            if (_directoryWorkarea.SelectedItem is not DirectoryAreaChoice area) return;
            string path = Path.GetFullPath(Path.Combine(Root, area.Path));
            if (!Directory.Exists(path)) throw new DirectoryNotFoundException(path);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }), UiButtonRole.Subtle));
        Add(actions);
        _directoryDetails.Height = 240;
        Add(_directoryDetails);
        Add(_directoryConsent);
        _directoryApply = Button("执行已预览的更名", async (_, _) => await ApplyDirectoryRenameAsync());
        _directoryApply.Enabled = false;
        Add(_directoryApply);
        Add(new Label { AutoSize = true, Text = "未完成事务恢复（只显示当前项目；恢复前将检查备份及事务后修改）", ForeColor = UiTheme.TextSecondary });
        Add(_directoryRecovery);
        Add(_directoryRecoveryConsent);
        _directoryRecover = Button("恢复所选事务到更名前", async (_, _) => await RecoverDirectoryRenameAsync());
        _directoryRecover.Enabled = false;
        Add(_directoryRecover);
        _directoryName.TextChanged += (_, _) => InvalidateDirectoryPreview();
        _directoryWorkarea.SelectedIndexChanged += (_, _) =>
        {
            InvalidateDirectoryPreview();
            if (_directoryWorkarea.SelectedItem is DirectoryAreaChoice area)
            {
                _directoryName.Text = Path.GetFileName(area.Path);
                _directoryDetails.Text = $"项目：{Root}\r\n工区：{area.Name} [{area.Id}]\r\n当前目录：{Path.GetFullPath(Path.Combine(Root, area.Path))}\r\n请输入新目录名并预览。";
            }
        };
        _directoryConsent.CheckedChanged += (_, _) => UpdateDirectoryActionState();
        _directoryRecoveryConsent.CheckedChanged += (_, _) => UpdateDirectoryActionState();
        _directoryRecovery.SelectedIndexChanged += (_, _) =>
        {
            _directoryRecoveryConsent.Checked = false;
            UpdateDirectoryActionState();
            if (_directoryRecovery.SelectedItem is string journal)
            {
                try
                {
                    var manifest = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(journal, "transaction.json")))!;
                    _directoryDetails.Text = $"待恢复事务：{journal}\r\n更名前：{manifest["Source"]}\r\n更名后：{manifest["Target"]}\r\n\r\n恢复将回到更名前的路径与记录。备份损坏、目录状态不符或事务后已有其他修改时会阻止恢复。";
                }
                catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
                { _directoryDetails.Text = "无法读取事务清单：" + ex.Message; }
            }
        };
        _projectWorkareaContext.Changed += (_, _) =>
        {
            InvalidateDirectoryPreview();
            _directoryRecoveryConsent.Checked = false;
            _directoryRecovery.Items.Clear();
            _directoryWorkarea.Items.Clear();
            _directoryDetails.Text = "项目或工区上下文已变化，请刷新目录后重新预览。";
        };
        page.Controls.Add(layout);
        return page;
    }

    private void InvalidateDirectoryPreview()
    {
        if (_directoryPlan is not null) _directoryDetails.Text = "原预览已失效，请重新预览更名影响。";
        _directoryPlan = null;
        _directoryConsent.Checked = false;
        UpdateDirectoryActionState();
    }

    private void UpdateDirectoryActionState()
    {
        if (_directoryApply is not null) _directoryApply.Enabled = !_directoryBusy && _directoryConsent.Checked && _directoryPlan?.CanApply == true;
        if (_directoryRecover is not null) _directoryRecover.Enabled = !_directoryBusy && _directoryRecoveryConsent.Checked && _directoryRecovery.SelectedItem is string;
    }

    private void RefreshDirectoryManagement()
    {
        if (_directoryBusy || _refreshingDirectories) return;
        _refreshingDirectories = true;
        try
        {
            InvalidateDirectoryPreview();
            _directoryRecoveryConsent.Checked = false;
            _directoryRecovery.Items.Clear();
            string selected = (_directoryWorkarea.SelectedItem as DirectoryAreaChoice)?.Id ?? CurrentWorkareaId;
            _directoryWorkarea.Items.Clear();
            if (!IsProjectInitialized()) { _directoryDetails.Text = "请先打开已初始化项目。"; return; }
            foreach (var row in _workspace.GetWorkareas(Root))
                _directoryWorkarea.Items.Add(new DirectoryAreaChoice(row.WorkareaId, row.Path, row.DisplayName));
            _directoryWorkarea.SelectedItem = _directoryWorkarea.Items.Cast<DirectoryAreaChoice>().FirstOrDefault(a => a.Id == selected);
            if (_directoryWorkarea.SelectedIndex < 0 && _directoryWorkarea.Items.Count > 0) _directoryWorkarea.SelectedIndex = 0;
            foreach (string journal in WorkareaDirectoryRenameService.PendingTransactions(Root)) _directoryRecovery.Items.Add(journal);
            if (_directoryRecovery.Items.Count > 0) _directoryRecovery.SelectedIndex = 0;
            if (_directoryWorkarea.Items.Count == 0) _directoryDetails.Text = $"项目：{Root}\r\n暂无已登记工区。";
        }
        finally { _refreshingDirectories = false; UpdateDirectoryActionState(); }
    }

    private async Task PreviewDirectoryRenameAsync()
    {
        if (_directoryBusy) return;
        InvalidateDirectoryPreview();
        if (_directoryWorkarea.SelectedItem is not DirectoryAreaChoice area) return;
        // Save/discard unsaved workspace edits before the preview is captured.
        if (!ConfirmContextSwitch("预览工区目录更名")) return;
        string root = Root, name = _directoryName.Text, preferences = _userContext.StoragePath;
        long revision = _projectWorkareaContext.Current.Revision;
        await RunDirectoryOperationAsync(async () =>
        {
            var plan = await Task.Run(() => new WorkareaDirectoryRenameService().Preview(root, area.Id, name, preferences));
            if (IsDisposed || Root != root || _projectWorkareaContext.Current.Revision != revision) return;
            _directoryPlan = plan;
            _directoryDetails.Text = $"源：{plan.SourcePath}\r\n目标：{plan.TargetPath}\r\n工区身份：{plan.WorkareaId}\r\n\r\n" +
                (plan.CanApply ? "检查通过；执行前将再次检查文件变化。" : "存在阻断，未移动目录：\r\n" + string.Join("\r\n", plan.Blockers)) +
                "\r\n\r\n将更新的引用文件：\r\n" + string.Join("\r\n", plan.ChangedFiles);
        });
    }

    private async Task ApplyDirectoryRenameAsync()
    {
        if (_directoryBusy || !_directoryConsent.Checked || _directoryPlan is not { CanApply: true } plan) return;
        if (Root != plan.ProjectRoot || !ConfirmContextSwitch("执行工区目录更名")) { InvalidateDirectoryPreview(); return; }
        await RunDirectoryOperationAsync(async () =>
        {
            await Task.Run(() => new WorkareaDirectoryRenameService().Apply(plan));
            string current = _userContext.Load(plan.ProjectRoot).CurrentWorkareaPath;
            ResetWorkareaContext();
            SetCurrentWorkareaRelativePath(current, confirmContextSwitch: false);
            RefreshAll();
            _directoryDetails.Text = $"目录更名已完成：\r\n{plan.SourcePath}\r\n→ {plan.TargetPath}\r\n\r\n稳定身份与显示名称保留。已有航线工程的路径引用已更新，可重新打开工程继续。";
            SetStatus("工区目录更名完成。");
        });
        _directoryPlan = null;
        _directoryConsent.Checked = false;
        UpdateDirectoryActionState();
    }

    private async Task RecoverDirectoryRenameAsync()
    {
        if (_directoryBusy || !_directoryRecoveryConsent.Checked || _directoryRecovery.SelectedItem is not string journal) return;
        if (!ConfirmContextSwitch("恢复工区更名事务")) return;
        string root = Root, preferences = _userContext.StoragePath;
        await RunDirectoryOperationAsync(async () =>
        {
            await Task.Run(() => new WorkareaDirectoryRenameService().Recover(root, journal, preferences));
            string current = _userContext.Load(root).CurrentWorkareaPath;
            ResetWorkareaContext();
            SetCurrentWorkareaRelativePath(current, confirmContextSwitch: false);
            RefreshAll();
            _directoryDetails.Text = "事务已恢复到更名前的目录与引用；请刷新目录查看。";
            _directoryRecovery.Items.Remove(journal);
            SetStatus("工区目录更名事务已恢复。");
        });
        InvalidateDirectoryPreview();
        _directoryRecoveryConsent.Checked = false;
    }

    private async Task RunDirectoryOperationAsync(Func<Task> action)
    {
        bool enabled = Enabled, cursor = UseWaitCursor;
        _directoryBusy = true; Enabled = false; UseWaitCursor = true;
        UpdateDirectoryActionState();
        try { await action(); }
        catch (Exception ex)
        {
            InvalidateDirectoryPreview();
            _directoryDetails.Text = "目录操作未完成：" + ex.Message + "\r\n请刷新目录，核对未完成事务，再重新预览。";
            SetStatus("目录操作未完成，请查看目录管理页的反馈。");
        }
        finally
        {
            // Publish idle only after controls and cursor have finished restoring.
            if (!IsDisposed) { Enabled = enabled; UseWaitCursor = cursor; }
            _directoryBusy = false;
            if (!IsDisposed) UpdateDirectoryActionState();
        }
    }
}
