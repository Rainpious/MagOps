using System.Text.Json;
using MagOps.Application;
using MagOps.Infrastructure.Services;

namespace MagOps.Desktop;

public sealed partial class MainForm
{
    private TabPage BuildGroundMagPage()
    {
        var page = new TabPage("地面磁测") { Padding = new Padding(12), AutoScroll = true };
        var layout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3 };
        layout.ColumnStyles.Add(new(SizeType.AutoSize));
        layout.ColumnStyles.Add(new(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new(SizeType.AutoSize));
        var input = new TextBox { Dock = DockStyle.Fill, AccessibleName = "地面磁测原始目录" };
        var mapping = new TextBox { Dock = DockStyle.Fill, AccessibleName = "地面时间与选择映射JSON" };
        var summary = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both,
            WordWrap = false, Dock = DockStyle.Fill, Height = 320 };
        layout.Controls.Add(new Label { Text = "原始目录", AutoSize = true }, 0, 0);
        layout.Controls.Add(input, 1, 0);
        layout.Controls.Add(Button("选择…", (_, _) => RunUi("选择地面资料", () =>
        {
            var history = FileInteractions.For(this, "ground-raw");
            using var dialog = new FolderBrowserDialog { Description = "选择仪器原始资料目录" };
            history.Prepare(dialog, input.Text);
            if (dialog.ShowDialog(this) == DialogResult.OK) input.Text = dialog.SelectedPath;
        })), 2, 0);
        layout.Controls.Add(new Label { Text = "映射 JSON", AutoSize = true }, 0, 1);
        layout.Controls.Add(mapping, 1, 1);
        layout.Controls.Add(Button("选择…", (_, _) => RunUi("选择地面映射", () =>
        {
            var history = FileInteractions.For(this, "ground-mapping");
            using var dialog = new OpenFileDialog { Filter = "JSON|*.json", CheckFileExists = true };
            history.Prepare(dialog, mapping.Text);
            if (dialog.ShowDialog(this) == DialogResult.OK) mapping.Text = dialog.FileName;
        })), 2, 1);
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        actions.Controls.Add(Button("检查原始资料", (_, _) => RunUi("检查地面资料", () =>
        {
            string folder = input.Text, map = mapping.Text;
            var result = BusyOperationDialog.Run(this, "地面磁测检查", "读取原始 session；不猜测时间基准…",
                () => new GroundMagInspection().Inspect(folder, map));
            if (!result.Ok) throw new InvalidDataException(string.Join("\r\n", result.Errors.Select(e => e.Message)));
            RememberInputs();
            summary.Text = JsonSerializer.Serialize(result.Result, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        })));
        actions.Controls.Add(Button("导出试算与点统计", (_, _) => RunUi("导出地面试算", () =>
        {
            EnsureInitialized();
            if (string.IsNullOrWhiteSpace(CurrentWorkareaId)) throw new InvalidOperationException("请先选择工区。");
            string folder = input.Text, map = mapping.Text;
            string output = Path.Combine(EnsureManagedOutputDirectory("processing_diurnal", "地面磁测试算"),
                "地面试算_" + DateTime.Now.ToString("yyyyMMdd-HHmmssfff"));
            var context = new GeneratedArtifactContext(_workspace.GetWorkspace(Root).ProjectId, CurrentWorkareaId);
            var run = _processingRuns.Begin(Root, "ground_diurnal_review", context);
            try
            {
                var result = BusyOperationDialog.Run(this, "地面磁测输出", "保存原值、时间假设、日变试算及分组统计…",
                    () => new GroundOutputWorkflow().Export(folder, map, output, context));
                if (!result.Ok) throw new InvalidDataException(string.Join("\r\n", result.Errors.Select(e => e.Message)));
                var ids = result.Result!.Files.Where(p => !p.EndsWith(GeneratedArtifactIdentity.Suffix, StringComparison.OrdinalIgnoreCase))
                    .Select(p => _artifacts.RegisterGeneratedFile(Root, p, "ground_diurnal_review").ArtifactId).ToArray();
                _processingRuns.Complete(Root, run.ProcessingRunId, ids);
                if (_saveElsewhere || !new GlobalWorkspaceService().Load().AutomaticOutput) {
                    string destination = Path.Combine(ChooseCustomOutputDirectory("processing_diurnal", "另存地面试算"), Path.GetFileName(output));
                    foreach (string file in result.Result.Files.Where(p => !p.EndsWith(GeneratedArtifactIdentity.Suffix, StringComparison.OrdinalIgnoreCase)))
                        ArtifactCopyService.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
                    output = destination;
                }
                RememberInputs();
                FileInteractions.For(this, "ground-output", "output").OutputSucceeded(output, directory: true);
                summary.Text = $"{result.Result.Status}\r\n{output}\r\n记录 {result.Result.RecordCount}；点分组 {result.Result.PointGroupCount}。\r\n需复核，非已验收生产成果。";
                RefreshArtifacts();
            }
            catch (Exception ex)
            {
                if (_processingRuns.List(Root).Any(r => r.ProcessingRunId == run.ProcessingRunId && r.Status == "STARTED"))
                    _processingRuns.Fail(Root, run.ProcessingRunId, ex, "GROUND_OUTPUT_FAILED");
                throw;
            }
        })));
        layout.Controls.Add(actions, 0, 2); layout.SetColumnSpan(actions, 3);
        var note = new Label { AutoSize = true, MaximumSize = new Size(950, 0),
            Text = "先检查获取 session ID，再提供时间/选择映射 JSON。未知时间不补造 UTC；未改正记录保留原因。按仪器、session、状态和完整点号分别统计，均值/中位数并列，SQ 不自动剔除。不做背景插值。" };
        layout.Controls.Add(note, 0, 3); layout.SetColumnSpan(note, 3);
        layout.Controls.Add(summary, 0, 4); layout.SetColumnSpan(summary, 3);
        page.Controls.Add(layout);
        _projectWorkareaContext.Changed += (_, _) => { input.Clear(); mapping.Clear(); summary.Clear(); };
        return page;

        void RememberInputs()
        {
            FileInteractions.For(this, "ground-raw").DirectorySucceeded(input.Text);
            if (!string.IsNullOrWhiteSpace(mapping.Text)) FileInteractions.For(this, "ground-mapping").InputSucceeded(mapping.Text);
        }
    }
}
