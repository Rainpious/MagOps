using System.Text.Json;
using MagOps.Application;

namespace MagOps.Desktop;

public sealed partial class MainForm
{
    private TabPage BuildIntermediateTablePage()
    {
        var page = new TabPage("中间表格检查") { Padding = new Padding(12) };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.Percent, 100));
        var note = new Label { AutoSize = true, MaximumSize = new Size(1000, 0),
            Text = "选择 CSV/TSV 和显式映射 JSON：保留原列、原值及处理阶段，不分线、不再次改正。纯 XY 必须声明投影。地形候选按这些点检查瓦片边界覆盖，不自动绑定或下载。" };
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        var view = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, WordWrap = false, ScrollBars = ScrollBars.Both };
        string source = "", mapping = "";
        string? snapshot = null;
        actions.Controls.Add(Button("导入并检查", (_, _) => RunUi("中间表格检查", () =>
        {
            var history = FileInteractions.For(this, "intermediate-table");
            using var choose = new OpenFileDialog { Filter = "CSV/TSV|*.csv;*.tsv", CheckFileExists = true };
            history.Prepare(choose, source);
            if (choose.ShowDialog(this) != DialogResult.OK) return;
            var mapHistory = FileInteractions.For(this, "intermediate-table-mapping");
            using var map = new OpenFileDialog { Title = "选择显式映射 JSON", Filter = "JSON|*.json", CheckFileExists = true };
            mapHistory.Prepare(map, mapping);
            if (map.ShowDialog(this) != DialogResult.OK) return;
            string path = choose.FileName, config = map.FileName;
            var result = BusyOperationDialog.Run(this, "中间表格检查", "读取原值与明确坐标…", () => new IntermediateTableInspection().Inspect(path, config));
            if (!result.Ok) throw new InvalidDataException(string.Join("\r\n", result.Errors.Select(e => e.Message)));
            source = path; mapping = config;
            history.InputSucceeded(source); mapHistory.InputSucceeded(mapping);
            snapshot = JsonSerializer.Serialize(result.Result, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
            view.Text = snapshot;
        })));
        actions.Controls.Add(Button("原始点地形候选", (_, _) => RunUi("地形覆盖候选", () =>
        {
            if (source.Length == 0) throw new InvalidOperationException("请先导入表格和映射。");
            string input = source, config = mapping;
            var result = BusyOperationDialog.Run(this, "地形覆盖候选", "检查公共库瓦片边界与点范围…", () => new TerrainPointCoverage().Inspect(input, config));
            if (!result.Ok) throw new InvalidDataException(string.Join("\r\n", result.Errors.Select(e => e.Message)));
            view.Text = JsonSerializer.Serialize(result.Result, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        })));
        actions.Controls.Add(Button("按原始点下载 AW3D30", (_, _) => RunUi("下载 AW3D30", () =>
        {
            if (source.Length == 0) throw new InvalidOperationException("请先导入表格和映射。");
            string input = source, config = mapping;
            var read = BusyOperationDialog.Run(this, "读取点范围", "根据显式坐标计算下载范围…", () => new IntermediateTableInspection().Inspect(input, config));
            if (!read.Ok) throw new InvalidDataException(string.Join("；", read.Errors.Select(e => e.Message)));
            var points = read.Result!.Rows.Where(r => r.Longitude.HasValue && r.Latitude.HasValue).ToArray();
            if (points.Length == 0) throw new InvalidOperationException("没有有效经纬度点。");
            // A small margin includes neighbours needed near tile edges without resampling.
            using var dialog = new Aw3d30DownloadDialog(Math.Max(-180, points.Min(p => p.Longitude!.Value) - .001), Math.Max(-90, points.Min(p => p.Latitude!.Value) - .001),
                Math.Min(180, points.Max(p => p.Longitude!.Value) + .001), Math.Min(90, points.Max(p => p.Latitude!.Value) + .001));
            dialog.ShowDialog(this);
        })));
        actions.Controls.Add(Button("保存读取快照", (_, _) => RunUi("保存中间表格快照", () =>
        {
            if (snapshot is null) throw new InvalidOperationException("请先成功读取表格。");
            using var save = new SaveFileDialog { Filter = "JSON|*.json", FileName = "中间表格读取快照.json", OverwritePrompt = true };
            var history = FileInteractions.For(this, "intermediate-table-snapshot", "output"); history.Prepare(save);
            if (save.ShowDialog(this) != DialogResult.OK) return;
            MagOps.Infrastructure.Services.AtomicFileWriter.Write(save.FileName, stream => { using var writer = new StreamWriter(stream, leaveOpen: true); writer.Write(snapshot); });
            RegisterGeneratedBusinessFile(save.FileName, "intermediate_table_snapshot"); history.OutputSucceeded(save.FileName);
        })));
        _projectWorkareaContext.Changed += (_, _) => { source = mapping = ""; snapshot = null; view.Clear(); };
        layout.Controls.Add(note, 0, 0); layout.Controls.Add(actions, 0, 1); layout.Controls.Add(view, 0, 2); page.Controls.Add(layout); return page;
    }
}
