using System.Diagnostics;
using MagOps.Reporting;

namespace MagOps.Desktop;

public sealed partial class MainForm
{
    private void ManagePingquanReport(string action)
    {
        if (_reportWorkspace == null || _reportLoadFailed) throw new InvalidOperationException("请先打开有效的工区报告资料。");
        var report = new PingquanReport(Root, CurrentWorkareaRelativePath, CurrentWorkareaId);
        var inputs = _reportWorkspace.ReadInputs();
        Directory.CreateDirectory(report.DirectoryPath);
        const string filter = "Word 文档 (*.docx)|*.docx";
        if (action == "新建平泉工作稿")
        {
            using var save = NewWorkfileDialog(report.DirectoryPath, "平泉航磁工作稿.docx", "新建平泉工作稿");
            if (save.ShowDialog(this) != DialogResult.OK) return;
            var selection = SelectPingquanEquipment(report, new());
            if (selection == null) return;
            report.Create(save.FileName, selection, inputs.ProjectName);
            SetStatus("已建稿：" + save.FileName);
            Process.Start(new ProcessStartInfo(save.FileName) { UseShellExecute = true });
            return;
        }
        using var open = new OpenFileDialog { Filter = filter, InitialDirectory = report.DirectoryPath, Title = "选择本工区的平泉工作稿" };
        if (open.ShowDialog(this) != DialogResult.OK) return;
        string path = open.FileName;
        var saved = report.Selection(path);
        if (action == "打开平泉工作稿")
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return;
        }
        if (action == "导出平泉交付稿")
        {
            using var save = NewWorkfileDialog(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + "_交付稿.docx", "导出已保存的工作稿");
            if (save.ShowDialog(this) != DialogResult.OK) return;
            report.Export(path, save.FileName);
            SetStatus("已导出：" + save.FileName);
            return;
        }
        var chosen = SelectPingquanEquipment(report, saved);
        if (chosen == null) return;
        var preview = report.Preview(path, chosen, inputs.ProjectName);
        var apply = ConfirmWorkfileChanges("核对平泉工作稿更新", preview.Fields.Select(c =>
            new WorkfileChangeRow(c.Key, c.Label, c.Previous, c.Current, c.Source, c.Suggested, c.CanWrite, c.Reason)));
        if (apply == null) return;
        report.Update(path, preview, apply, inputs.ProjectName);
        SetStatus($"已更新 {apply.Length} 个字段；上版工作稿保存在 {path}.bak");
    }

    private sealed record WorkfileChangeRow(string Key, string Label, string Previous, string Current, string Source,
        bool Suggested, bool CanWrite, string Reason);

    private string[]? ConfirmWorkfileChanges(string title, IEnumerable<WorkfileChangeRow> changes, string? sourceNote = null)
    {
        using var dialog = new Form { Text = title, StartPosition = FormStartPosition.CenterParent, Size = new Size(970, 435) };
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AutoGenerateColumns = false,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            RowHeadersVisible = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
        };
        grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "采用来源", FillWeight = 65 });
        foreach (var column in new[] { "字段", "上次来源", "Word 当前值", "来源现值", "状态" })
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = column, ReadOnly = true });
        foreach (var c in changes)
        {
            int i = grid.Rows.Add(c.Suggested, c.Label, c.Previous, c.Current, c.Source, c.Reason);
            grid.Rows[i].Tag = c.Key;
            grid.Rows[i].Cells[0].ReadOnly = !c.CanWrite;
        }
        dialog.Controls.Add(grid);
        if (sourceNote != null)
        {
            dialog.Size = new Size(1080, 560);
            grid.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
            grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
            dialog.Controls.Add(new Label { Text = sourceNote, Dock = DockStyle.Top,
                AutoSize = true, MaximumSize = new Size(980, 0), Padding = new Padding(6) });
        }
        AddWorkfileButtons(dialog, "确认更新");
        if (dialog.ShowDialog(this) != DialogResult.OK) return null;
        grid.EndEdit();
        return grid.Rows.Cast<DataGridViewRow>().Where(r => Equals(r.Cells[0].Value, true)).Select(r => (string)r.Tag!).ToArray();
    }

    private SaveFileDialog NewWorkfileDialog(string directory, string name, string title)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "Word 文档 (*.docx)|*.docx",
            InitialDirectory = directory,
            FileName = name,
            Title = title,
            OverwritePrompt = false
        };
        dialog.FileOk += (_, e) =>
        {
            if (!File.Exists(dialog.FileName)) return;
            e.Cancel = true;
            MessageBox.Show(this, "此文件已存在，请另取文件名。", title, MessageBoxButtons.OK, MessageBoxIcon.Information);
        };
        return dialog;
    }

    private Dictionary<string, string>? SelectPingquanEquipment(PingquanReport report, Dictionary<string, string> saved)
    {
        using var dialog = new Form { Text = "选择本报告采用的台账设备", StartPosition = FormStartPosition.CenterParent, Size = new Size(620, 240) };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(12), AutoSize = true };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var choices = new Dictionary<string, ComboBox>();
        var devices = report.Equipment();
        foreach (var (key, label) in PingquanReport.Fields.Where(f => f.Key != "info.Project"))
        {
            string old = saved.GetValueOrDefault(key) ?? "";
            var items = new List<KeyValuePair<string, string>> { new("", "暂不选用") };
            items.AddRange(devices.Where(d => PingquanReport.EquipmentKey(d).Length > 0)
                .Select(d => new KeyValuePair<string, string>(PingquanReport.EquipmentKey(d), $"{d.Category}　{d.Model}　{PingquanReport.EquipmentKey(d)}")));
            if (old.Length > 0 && !items.Any(i => i.Key == old)) items.Add(new(old, "台账中已找不到：" + old));
            var combo = new ComboBox
            {
                Dock = DockStyle.Top,
                DropDownStyle = ComboBoxStyle.DropDownList,
                DisplayMember = "Value",
                ValueMember = "Key",
                DataSource = items
            };
            // BindingContext becomes available once the layout belongs to the form.
            dialog.Load += (_, _) => combo.SelectedValue = old;
            layout.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left });
            layout.Controls.Add(combo); choices[key] = combo;
        }
        dialog.Controls.Add(layout); AddWorkfileButtons(dialog, "采用选择");
        return dialog.ShowDialog(this) == DialogResult.OK ? choices.ToDictionary(k => k.Key, k => k.Value.SelectedValue as string ?? "") : null;
    }

    private static void AddWorkfileButtons(Form dialog, string label)
    {
        var ok = new Button { Text = label, AutoSize = true, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel };
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(5) };
        bar.Controls.Add(ok); bar.Controls.Add(cancel); dialog.Controls.Add(bar);
        dialog.AcceptButton = ok; dialog.CancelButton = cancel;
    }
}
