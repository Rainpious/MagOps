using MagOps.Reporting;

namespace MagOps.Desktop;

internal sealed class ReportFieldDictionaryDialog : Form
{
    public ReportFieldDictionaryDialog()
    {
        Text = "报告模板字段字典";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(1150, 650);
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 42 };
        var chapter = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 145, AccessibleName = "章节" };
        chapter.Items.Add("全部章节");
        chapter.Items.AddRange(ReportFieldDictionary.Fields.Select(f => f.Chapter).Distinct().ToArray());
        chapter.SelectedIndex = 0;
        var search = new TextBox { Width = 260, PlaceholderText = "搜索名称、标识或标签", AccessibleName = "搜索字段" };
        var check = new Button { Text = "校验模板…", AutoSize = true };
        check.Click += (_, _) =>
        {
            using var pick = new OpenFileDialog { Filter = "Word 模板|*.docx" };
            if (pick.ShowDialog(this) != DialogResult.OK) return;
            try { ReportFieldDictionary.Validate(pick.FileName); MessageBox.Show(this,"必需字段和控件检查通过。",Text); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "模板校验"); }
        };
        bar.Controls.AddRange([chapter, search, check]);
        var grid = new DataGridView { ReadOnly = true, Dock = DockStyle.Fill, AllowUserToAddRows = false,
            AllowUserToDeleteRows = false, RowHeadersVisible = false, AutoGenerateColumns = false,
            AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells, AccessibleName = "模板字段" };
        foreach (var (property, title, width) in new[] { ("Chapter","章节",92), ("Name","字段",155), ("Tag","标签或父表",190),
            ("Unit","单位",55), ("Source","来源",125), ("Condition","适用条件",250), ("Missing","缺值处理",130),
            ("Rule","模板要求",120), ("Format","格式",110), ("Kind","类型",85), ("Id","稳定标识",180) })
            grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName=property, HeaderText=title, Width=width,
                DefaultCellStyle=new() { WrapMode=DataGridViewTriState.True } });
        void Filter()
        {
            var query=search.Text.Trim();
            grid.DataSource=ReportFieldDictionary.Fields.Where(f => (chapter.SelectedIndex==0 || f.Chapter==(string?)chapter.SelectedItem)
                && new[] {f.Id,f.Name,f.Tag,f.Chapter}.Any(s=>s.Contains(query,StringComparison.OrdinalIgnoreCase))).ToList();
        }
        search.TextChanged += (_,_)=>Filter();
        chapter.SelectedIndexChanged += (_,_)=>Filter();
        Controls.Add(grid); Controls.Add(bar); Filter();
    }
}
