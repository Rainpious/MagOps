using MagOps.Infrastructure.Services;

namespace MagOps.Desktop;

internal sealed class WorkspaceSettingsDialog : Form
{
    private readonly TextBox _root = new() { Dock = DockStyle.Fill, AccessibleName = "全局工作目录" };
    private readonly TextBox _projects = new() { Dock = DockStyle.Fill, AccessibleName = "自定义项目目录" };
    private readonly TextBox _standalone = new() { Dock = DockStyle.Fill, AccessibleName = "自定义独立工具目录" };
    private readonly CheckBox _automatic = new() { Text = "自动保存到对应目录", AutoSize = true };
    public WorkspaceSettingsDialog()
    {
        Text = "工作目录设置"; Width = 900; Height = 350; MinimumSize = new Size(780, 350);
        StartPosition = FormStartPosition.CenterParent;
        var service = new GlobalWorkspaceService(); var settings = service.Load();
        _root.Text = settings.Root.Length == 0 ? GlobalWorkspaceService.DefaultRoot : settings.Root; _projects.Text = settings.ProjectsDirectory; _standalone.Text = settings.StandaloneDirectory;
        _automatic.Checked = settings.AutomaticOutput;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 4, AutoSize = true };
        layout.ColumnStyles.Add(new(SizeType.AutoSize)); layout.ColumnStyles.Add(new(SizeType.Percent, 100)); layout.ColumnStyles.Add(new(SizeType.AutoSize)); layout.ColumnStyles.Add(new(SizeType.AutoSize));
        AddRow("全局工作目录", _root, 0); AddRow("项目目录（可选）", _projects, 1); AddRow("独立工具目录（可选）", _standalone, 2);
        layout.Controls.Add(_automatic, 1, 3);
        var note = new Label { AutoSize = true, MaximumSize = new Size(690, 0), Text =
            "默认使用系统“文档”中的 MagOps 文件夹，也可以自行选择。其他目录可跳过，留空沿用全局结构。\n以后可随时在“工作目录…”中修改。现有项目和公共库保持原位，更改设置不搬动文件。" };
        layout.Controls.Add(note, 0, 4); layout.SetColumnSpan(note, 4);
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var save = new Button { Text = settings.Root.Length == 0 ? "开始使用" : "保存设置", AutoSize = true }; var cancel = new Button { Text = "取消", AutoSize = true, DialogResult = DialogResult.Cancel };
        save.Click += (_, _) => {
            try {
                service.Save(new() { Root = _root.Text.Trim(), ProjectsDirectory = _projects.Text.Trim(), StandaloneDirectory = _standalone.Text.Trim(), AutomaticOutput = _automatic.Checked });
                service.EnsureDefaultLibrary(); DialogResult = DialogResult.OK; Close();
            } catch (Exception ex) { MessageBox.Show(this, ex.Message, "无法保存", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        };
        var skip = new Button { Text = "跳过自定义项", AutoSize = true };
        skip.Click += (_, _) => { _projects.Clear(); _standalone.Clear(); save.PerformClick(); };
        buttons.Controls.Add(save); buttons.Controls.Add(skip); buttons.Controls.Add(cancel); layout.Controls.Add(buttons, 0, 5); layout.SetColumnSpan(buttons, 4);
        void RefreshInheritedPaths() {
            string root = string.IsNullOrWhiteSpace(_root.Text) ? GlobalWorkspaceService.DefaultRoot : _root.Text.Trim();
            _projects.PlaceholderText = Path.Combine(root, "项目");
            _standalone.PlaceholderText = Path.Combine(root, "临时任务");
        }
        _root.TextChanged += (_, _) => RefreshInheritedPaths(); RefreshInheritedPaths();
        Controls.Add(layout); AcceptButton = save; CancelButton = cancel; UiTheme.Apply(this);
        void AddRow(string label, TextBox box, int row) {
            layout.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 7, 10, 10) }, 0, row);
            layout.Controls.Add(box, 1, row);
            var browse = new Button { Text = "选择…", AutoSize = true };
            browse.Click += (_, _) => { using var picker = new FolderBrowserDialog { Description = label, UseDescriptionForTitle = true, SelectedPath = box.Text }; if (picker.ShowDialog(this) == DialogResult.OK) box.Text = picker.SelectedPath; };
            layout.Controls.Add(browse, 2, row);
            var reset = new Button { Text = row == 0 ? "使用默认" : "沿用全局", AutoSize = true };
            reset.Click += (_, _) => { if (row == 0) box.Text = GlobalWorkspaceService.DefaultRoot; else box.Clear(); };
            layout.Controls.Add(reset, 3, row);
        }
    }
}
