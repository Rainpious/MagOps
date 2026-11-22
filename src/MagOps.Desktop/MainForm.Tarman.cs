using System.Diagnostics;
using MagOps.Reporting;

namespace MagOps.Desktop;

public sealed partial class MainForm
{
    private void ManageTarmanReport(string action)
    {
        if (_reportWorkspace == null || _reportLoadFailed) throw new InvalidOperationException("请先打开有效的工区报告资料。");
        var report = new TarmanReport(Root, CurrentWorkareaRelativePath, CurrentWorkareaId);
        Directory.CreateDirectory(report.DirectoryPath);
        if (action == "新建塔尔曼工作稿")
        {
            using var template = new OpenFileDialog { Title = "选择塔尔曼题注与拐点审阅模板", Filter = "Word 模板 (*.dotx)|*.dotx" };
            if (template.ShowDialog(this) != DialogResult.OK) return;
            string? chosen = SelectTarmanPlatform(report, "");
            if (chosen == null) return;
            using var save = NewWorkfileDialog(report.DirectoryPath, CurrentWorkareaId + "_塔尔曼工作稿.docx", action);
            if (save.ShowDialog(this) != DialogResult.OK) return;
            report.Create(template.FileName, save.FileName, chosen);
            SetStatus("已建稿：" + save.FileName);
            Process.Start(new ProcessStartInfo(save.FileName) { UseShellExecute = true });
            return;
        }

        using var open = new OpenFileDialog { Title = "选择本工区的塔尔曼工作稿", InitialDirectory = report.DirectoryPath, Filter = "Word 文档 (*.docx)|*.docx" };
        if (open.ShowDialog(this) != DialogResult.OK) return;
        var path = open.FileName;
        var saved = report.Selection(path);
        if (action == "打开塔尔曼工作稿")
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return;
        }
        if (action == "导出塔尔曼交付稿")
        {
            using var dest = NewWorkfileDialog(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + "_交付稿.docx", action);
            if (dest.ShowDialog(this) != DialogResult.OK) return;
            report.Export(path, dest.FileName);
            SetStatus("已导出：" + dest.FileName);
            return;
        }
        if (action == "塔尔曼第二轮装配")
        {
            using var mode = new Form { Text = "塔尔曼第二轮装配", StartPosition = FormStartPosition.CenterParent,
                ClientSize = new Size(720, 350), MinimizeBox = false };
            var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
            mode.Controls.Add(panel);
            panel.Controls.Add(new Label { Text = "装配位置及所需来源", AutoSize = true });
            foreach (var s in TarmanReport.AssemblySlots)
                panel.Controls.Add(new Label { Text = s.Position + "　" + s.Input, AutoSize = true });
            var actions = new FlowLayoutPanel { AutoSize = true };
            var install = new Button { Text = "建立框架", AutoSize = true, DialogResult = DialogResult.Yes };
            var import = new Button { Text = "从已有报告试填卷首及人员表…", AutoSize = true, DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "取消", AutoSize = true, DialogResult = DialogResult.Cancel };
            actions.Controls.AddRange([install, import, cancel]);panel.Controls.Add(actions);mode.CancelButton = cancel;
            var choice = mode.ShowDialog(this);
            if (choice != DialogResult.OK && choice != DialogResult.Yes) return;
            string? sourcePath = null;
            if (choice == DialogResult.OK)
            {
                using var source = new OpenFileDialog { Title = "选择含署名表及项目组人员表的原报告", Filter = "Word 报告 (*.docx)|*.docx" };
                if (source.ShowDialog(this) != DialogResult.OK) return;
                sourcePath = source.FileName;
            }
            var assemblyPreview = report.PreviewAssembly(path,sourcePath);
            string[] keys = [];
            if (sourcePath != null)
            {
                var accepted = ConfirmWorkfileChanges("历史报告试填 · 卷首、署名及表 4-1",
                    assemblyPreview.Changes.Select(c => new WorkfileChangeRow(c.Key,c.Label,c.Previous,c.Current,c.Source,c.Suggested,true,
                        c.Key == "staff" ? "逐行核对；保留原表人工备注" : "勾选后更新所有同字段引用")),
                    "历史示例来源：" + sourcePath + "。年龄按原报告记载，署名与项目岗位分别采用。其余位置记录历史对照来源。\n" + string.Join("；",assemblyPreview.HistoricalReferences));
                if (accepted == null || accepted.Length == 0) return;
                keys = accepted;
            }
            report.UpdateAssembly(path,assemblyPreview,keys);
            SetStatus(sourcePath==null ? "已建立第二轮框架：" + path : "已写入所选历史报告示例：" + path);
            return;
        }
        if (action == "更新塔尔曼图 5-1 偏航距")
        {
            var areaDir = Path.GetFullPath(CurrentWorkareaRelativePath, Root);
            using var route = new OpenFileDialog { Title = "选择本工区航线结果清单", InitialDirectory = areaDir, Filter = "航线结果清单|*route-v25.json|JSON|*.json" };
            if (route.ShowDialog(this) != DialogResult.OK) return;
            var inputCsv = ReportInputStore.Load(Root, CurrentWorkareaRelativePath).ProductionCsv;
            using var batch = new OpenFileDialog { Title = "选择处理批次的日变改正清单", InitialDirectory = File.Exists(inputCsv) ? Path.GetDirectoryName(inputCsv) : areaDir,
                Filter = "日变改正清单|*manifest*.json|JSON|*.json" };
            if (batch.ShowDialog(this) != DialogResult.OK) return;
            var view = BusyOperationDialog.Run(this, "核对图 5-1", "核实设计端点和实飞点，计算逐线平均偏航距…",
                () => report.PreviewOffsetFigure(path, route.FileName, batch.FileName));
            var keys = ConfirmWorkfileChanges("图 5-1 · 按设计顺序逐线绘柱", view.Changes.Select(c =>
                new WorkfileChangeRow(c.Key,c.Label,c.Previous,c.Current,c.Source,c.Suggested,true,
                    c.Key == "chart" ? "采用后写入嵌入式图表和来源记录" : "取消勾选可保留 Word 当前文字")),
                $"审阅试算 · {view.Batch.Design} · {view.Batch.Summary}。逐线均值最大线：{view.Batch.Maximum.Name}。来源：{batch.FileName}");
            if(keys == null || !keys.Contains("chart")) return;
            BusyOperationDialog.Run(this,"更新图 5-1","写入工作稿与来源记录…",() => {report.UpdateOffsetFigure(path,view,keys);return true;});
            SetStatus("已更新图 5-1 审阅试算和选定正文：" + path);
            return;
        }
        if (action == "更新塔尔曼日变站选址及基值")
        {
            if (_reportWorkspace.HasChanges) SaveReportInputs();
            var site = report.PreviewSite(path);
            var selected = ConfirmWorkfileChanges("核对日变站选址及基值", site.Values.Select(v =>
            {
                var p = v.Parameter;
                return new WorkfileChangeRow(p.Key, p.Label, p.Previous, p.Current, p.Source, p.Suggested,
                    p.Current != p.Source, v.Evidence);
            }), "经纬度、高程可采用本工区已核对的同一份基站日志。也可在报告人工补录中选择“基站”，按下列字段名填写数值、单位和来源并确认。基本磁场值采用站点核定值。");
            if (selected == null) return;
            report.UpdateSite(path, site, selected);
            SetStatus("日变站选址及基值已更新，上版工作稿保存在 " + path + ".bak");
            return;
        }
        if (action == "更新塔尔曼日变基站")
        {
            var stations = report.BaseStations().Select(e => new KeyValuePair<string,string>(
                PingquanReport.EquipmentKey(e), e.Model + "　" + PingquanReport.EquipmentKey(e))).ToArray();
            if (stations.Length == 0) throw new InvalidOperationException("请先在设备台账登记日变基站及型号。");
            var id = SelectTarmanSource("选择日变基站", "项目台账中的日变基站", stations, report.StationSelection(path));
            if (id == null) return;
            if (_reportWorkspace.HasChanges) SaveReportInputs();
            var station = report.PreviewStation(path, id);
            var selected = ConfirmWorkfileChanges("核对日变站仪器和观测说明", station.Parameters.Select(p =>
                new WorkfileChangeRow(p.Key, p.Label, p.Previous, p.Current, p.Source, p.Suggested,
                    p.Current != p.Source, p.Current == p.Source ? "值一致" : !p.Suggested ? "保留人工内容" :
                    p.Source.Contains("待核") ? "含待核项" : "采用来源")),
                $"正文合并为一段，旧参数中的人工修订作为备注保留。规格来源：{station.Specification}。工区实测资料取自报告人工补录中类别为“设备”、名称为“{TarmanReport.StationObservationName(id, station.Model)}”的已确认记录。");
            if (selected == null) return;
            report.UpdateStation(path, station, selected);
            SetStatus("日变基站已更新，上版工作稿保存在 " + path + ".bak");
            return;
        }
        if (action == "更新塔尔曼表 4-3 探头参数")
        {
            var probes = report.Probes().Select(e => new KeyValuePair<string,string>(
                PingquanReport.EquipmentKey(e), e.Model + "　" + PingquanReport.EquipmentKey(e))).ToArray();
            if (probes.Length == 0) throw new InvalidOperationException("请先在设备台账登记磁测探头及型号。");
            var id = SelectTarmanSource("选择表 4-3 磁测探头", "项目台账中的磁测探头", probes, report.ProbeSelection(path));
            if (id == null) return;
            var probe = report.PreviewProbe(path, id);
            var selected = ConfirmWorkfileChanges("核对塔尔曼表 4-3 磁测装置参数（正文随采用的类别和原子种类更新）", probe.Parameters.Select(p =>
                new WorkfileChangeRow(p.Key, p.Label, p.Previous, p.Current, p.Source,
                    p.Key == "type" ? p.Current != p.Source : p.Suggested, p.Key != "type" && p.Current != p.Source,
                    p.Current == p.Source ? "值一致" : p.Key == "type" ? "采用台账型号及类别" :
                    p.Suggested ? (p.Source.Contains("待核") ? "采用待核标记" : "采用型号资料") : "保留人工取值")));
            if (selected == null) return;
            report.UpdateProbe(path, probe, selected);
            SetStatus("表 4-3 和表前两段已更新，上版工作稿保存在 " + path + ".bak");
            return;
        }
        var platform = SelectTarmanPlatform(report, saved);
        if (platform == null) return;
        var preview = report.Preview(path, platform);
        var apply = ConfirmWorkfileChanges("核对塔尔曼表 4-2 飞行平台参数", preview.Parameters.Select(p =>
            new WorkfileChangeRow(p.Key, p.Label, p.Previous, p.Current, p.Source, p.Suggested,
                p.Key != "model" && p.Current != p.Source, p.Current == p.Source ? "值一致" :
                p.Key == "model" ? "采用所选型号" : p.Suggested ? "来源可更新" : "保留人工取值，可勾选采用来源")));
        if (apply == null) return;
        report.Update(path, preview, apply);
        SetStatus("表 4-2 已核对，上版工作稿保存在 " + path + ".bak");
    }

    private string? SelectTarmanPlatform(TarmanReport report, string saved)
    {
        var platforms = report.Platforms().Select(p => new KeyValuePair<string,string>(p.Id, p.Get("model"))).ToArray();
        if (platforms.Length == 0) throw new InvalidOperationException("请先在“出发整备 → 报告资料字典 → 飞行平台”登记型号。");
        return SelectTarmanSource("选择表 4-2 飞行平台", "项目资料字典中的飞行平台", platforms, saved);
    }

    private string? SelectTarmanSource(string title, string label, KeyValuePair<string,string>[] choices, string? saved)
    {
        using var dialog = new Form { Text = title, StartPosition = FormStartPosition.CenterParent, Size = new Size(525, 170) };
        var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top,
            AccessibleName = label, DisplayMember = "Value", ValueMember = "Key", DataSource = choices };
        dialog.Controls.Add(combo);
        AddWorkfileButtons(dialog, "采用型号");
        dialog.Load += (_, _) =>
        {
            combo.SelectedIndex = Array.FindIndex(choices, p => p.Key == saved);
            ((Button)dialog.AcceptButton!).Enabled = combo.SelectedIndex >= 0;
        };
        combo.SelectedIndexChanged += (_, _) => ((Button)dialog.AcceptButton!).Enabled = combo.SelectedIndex >= 0;
        return dialog.ShowDialog(this) == DialogResult.OK ? combo.SelectedValue as string : null;
    }
}
