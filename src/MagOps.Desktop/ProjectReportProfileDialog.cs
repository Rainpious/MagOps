using MagOps.Infrastructure.Services;
using MagOps.Infrastructure.Storage;
using MagOps.Reporting;

namespace MagOps.Desktop;

internal sealed class ProjectReportProfileDialog : Form
{
    private readonly string _root;
    private readonly string _revision;
    private readonly Dictionary<string,DataGridView> _grids = [];
    private readonly Dictionary<string,HashSet<string>> _suggestions = [];
    private readonly Label _status = new() { TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Bottom, Height = 32 };
    private bool _edited;

    public ProjectReportProfileDialog(string root)
    {
        _root = root;
        var (profile, revision) = ProjectReportProfile.Load(root);
        _revision = revision;
        var project = new WorkspaceService(new JsonStore()).GetWorkspaceReadOnly(root);
        Text = "报告资料字典 · " + project.DisplayName;
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(720, 390);
        Size = new Size(1080, 560);
        var catalog = new EquipmentCatalogService(new JsonStore()).Load(root);
        AddOptions("platform", catalog.ModelsByCategory.GetValueOrDefault("无人机") ?? []);
        AddOptions("baseStation", catalog.ModelsByCategory.GetValueOrDefault("日变基站") ?? []);
        AddOptions("probe", catalog.ModelsByCategory.GetValueOrDefault(EquipmentCatalogService.MagnetometerSensorCategory) ?? []);
        var devices = new JsonStore().Read(ProjectPaths.Json(root,"equipment.json"), new List<MagOps.Core.Domain.EquipmentRecord>());
        foreach (var device in devices)
        {
            string kind = EquipmentCatalogService.NormalizeCategory(device.Category);
            if (kind == "日变基站") AddOptions("baseStation", [device.Model]);
            if (kind == "无人机") AddOptions("platform", [device.Model]);
            if (kind == EquipmentCatalogService.MagnetometerSensorCategory) AddOptions("probe", [device.Model]);
        }
        AddOptions("signature", ["编写人","项目负责人","单位负责人"]);
        var tabs = new TabControl { Dock = DockStyle.Fill };
        foreach (var section in ProjectReportProfile.Sections)
        {
            var page = new TabPage(section.Name);
            var grid = new DataGridView { Dock = DockStyle.Fill, AllowUserToAddRows = section.Key != "project",
                AllowUserToDeleteRows = section.Key != "project", RowHeadersWidth = 35, BackgroundColor = SystemColors.Window };
            foreach (var c in section.Columns) grid.Columns.Add(new DataGridViewTextBoxColumn
                { Name = c.Key, HeaderText = c.Name, ToolTipText = c.Unit.Length > 0 ? "请连同单位填写，例如 " + c.Unit : c.Name, Width = section.Key == "project" ? 280 : 150 });
            grid.EditingControlShowing += (_, e) =>
            {
                if (e.Control is not TextBox text || grid.CurrentCell?.OwningColumn is not { } column) return;
                string key = column.Name;
                string list = key switch { "unit" or "organization" or "client" => "units", "model" => section.Key, _ => key };
                text.AutoCompleteMode = AutoCompleteMode.None;
                if (!_suggestions.TryGetValue(list,out var options) || options.Count == 0) return;
                var values = new AutoCompleteStringCollection(); values.AddRange(options.ToArray());
                text.AutoCompleteCustomSource = values;
                text.AutoCompleteSource = AutoCompleteSource.CustomSource;
                text.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
            };
            grid.CellValueChanged += (_,__) => _edited = true;
            grid.UserDeletedRow += (_,__) => _edited = true;
            _grids.Add(section.Key, grid); page.Controls.Add(grid); tabs.TabPages.Add(page);
        }
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        var save = new Button { Text = "保存", AutoSize = true };
        save.Click += (_,_) => Run(() => { Collect().Save(_root,_revision); DialogResult = DialogResult.OK; });
        var reuse = new Button { Text = "复用其他项目资料…", AutoSize = true };
        reuse.Click += (_,_) => Run(Import);
        buttons.Controls.Add(save); buttons.Controls.Add(reuse);
        Controls.Add(tabs); Controls.Add(_status); Controls.Add(buttons);
        Populate(profile);
        _edited = false;
        tabs.SelectedIndexChanged += (_,_) => Hint();
        Hint();
        void Hint() => _status.Text = tabs.SelectedIndex switch {
            0 => "登记本项目的单位全称。项目名称取自项目档案。",
            1 => "出生资料可填年份或日期。年龄按报告年份计算。本项目岗位和署名角色单独登记。",
            _ => "按型号维护标称参数，值中保留单位、范围和测试条件。型号可从设备台账和型号目录联想选择。"
        };
        FormClosing += (_,e) => {
            if (DialogResult != DialogResult.OK && _edited &&
                MessageBox.Show(this,"放弃本次未保存的修改？",Text,MessageBoxButtons.YesNo) != DialogResult.Yes) e.Cancel = true;
        };
    }

    private void AddOptions(string group, IEnumerable<string> values)
    {
        if (!_suggestions.TryGetValue(group,out var choices)) _suggestions[group] = choices = new(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values.Where(v => !string.IsNullOrWhiteSpace(v))) choices.Add(value);
    }

    private void Populate(ProjectReportProfile profile)
    {
        foreach (var section in ProjectReportProfile.Sections)
        {
            var grid = _grids[section.Key]; grid.Rows.Clear();
            foreach (var entry in profile.Entries.Where(e => e.Section == section.Key))
            {
                int at = grid.Rows.Add(section.Columns.Select(c => (object)entry.Get(c.Key)).ToArray());
                grid.Rows[at].Tag = entry.Id;
                AddOptions("units", [entry.Get("unit"),entry.Get("client"),entry.Get("organization")]);
            }
            if (section.Key == "project" && grid.Rows.Count == 0) grid.Rows.Add();
        }
    }

    private ProjectReportProfile Collect()
    {
        var profile = new ProjectReportProfile();
        foreach (var section in ProjectReportProfile.Sections)
        {
            var grid = _grids[section.Key];
            if (!grid.EndEdit()) throw new InvalidDataException("请先完成当前单元格的编辑。");
            foreach (DataGridViewRow row in grid.Rows)
            {
                if (row.IsNewRow) continue;
                var entry = new ProfileEntry { Id = row.Tag as string ?? Guid.NewGuid().ToString("N"), Section = section.Key };
                row.Tag = entry.Id;
                foreach (var col in section.Columns)
                    if (row.Cells[col.Key].Value?.ToString()?.Trim() is { Length: > 0 } value) entry.Values.Add(col.Key,value);
                if (entry.Values.Count > 0) profile.Entries.Add(entry);
            }
        }
        return profile;
    }

    private void Import()
    {
        using var pick = new FolderBrowserDialog { Description = "选择提供名册和型号资料的 MagOps 项目", UseDescriptionForTitle = true };
        if (pick.ShowDialog(this) != DialogResult.OK) return;
        var (source,_) = ProjectReportProfile.Load(pick.SelectedPath);
        var current = Collect(); int before = current.Entries.Count;
        current.Reuse(source);
        foreach (var org in source.Entries.Where(x => x.Section == "project"))
            AddOptions("units", [org.Get("client"),org.Get("organization")]);
        Populate(current);
        _status.Text = $"复用了 {current.Entries.Count-before} 条人员和型号记录。单位全称可联想选用，请核对本项目岗位后保存。";
    }

    private void Run(Action action)
    {
        try { action(); } catch (Exception ex) { MessageBox.Show(this,ex.Message,Text,MessageBoxButtons.OK,MessageBoxIcon.Warning); }
    }
}
