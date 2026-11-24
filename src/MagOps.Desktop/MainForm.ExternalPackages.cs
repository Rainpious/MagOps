using System.Text.Json;
using MagOps.Infrastructure.Services;

namespace MagOps.Desktop;

public sealed partial class MainForm
{
    private TabPage BuildExternalPackagesPage()
    {
        var page = new TabPage("外部成果登记") { Padding = new Padding(12) };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1 };
        layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.Controls.Add(new Label { Text = "选取包含全部伴随文件的成果目录及声明 JSON，先预览再登记。保留原目录，完整复制到当前工区分析结果；内部引用未验证，登记不代表质量通过。", AutoSize = true, MaximumSize = new Size(1050, 0) }, 0, 0);
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        actions.Controls.Add(Button("Oasis成图", (_, _) => NavigateFeature(FeatureIds.OasisProcessing)));
        var view = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, WordWrap = false, ScrollBars = ScrollBars.Both };
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        string input = "", declarationPath = "", registered = "";
        ExternalPackageManifest? preview = null;
        actions.Controls.Add(Button("保存声明模板", (_, _) => RunUi("外部成果声明模板", () =>
        {
            using var save = new SaveFileDialog { Filter = "JSON|*.json", FileName = "外部成果声明.json", OverwritePrompt = true };
            var history = FileInteractions.For(this, "external-declaration", "output"); history.Prepare(save);
            if (PrepareAutomaticSave(save, "processing_oasis") != DialogResult.OK) return;
            var declaration = new ExternalPackageDeclaration("workarea", CurrentWorkareaId, "", "processing", "", "", "", "未确认", "未确认", "", []);
            AtomicFileWriter.Write(save.FileName, s => JsonSerializer.Serialize(s, declaration, options)); history.OutputSucceeded(save.FileName);
        })));
        actions.Controls.Add(Button("选择文件族并预览", (_, _) => RunUi("预览外部成果", () =>
        {
            if (!IsProjectInitialized() || string.IsNullOrWhiteSpace(CurrentWorkareaId)) throw new InvalidOperationException("请先选择项目及工区。");
            using var folder = new FolderBrowserDialog { Description = "选择包含主文件和全部伴随文件的目录" };
            var history = FileInteractions.For(this, "external-package"); history.Prepare(folder, input);
            if (folder.ShowDialog(this) != DialogResult.OK) return;
            using var mapping = new OpenFileDialog { Filter = "JSON|*.json", Title = "选择外部成果声明", CheckFileExists = true };
            var maps = FileInteractions.For(this, "external-declaration"); maps.Prepare(mapping, declarationPath);
            if (mapping.ShowDialog(this) != DialogResult.OK) return;
            string root = Root, source = folder.SelectedPath, config = mapping.FileName;
            var declaration = ExternalPackageService.ReadDeclaration(config);
            if (declaration.Scope == "project" || declaration.WorkareaId != CurrentWorkareaId) throw new InvalidOperationException("此入口登记当前工区或日包；项目级包使用 CLI 显式登记。");
            var result = BusyOperationDialog.Run(this, "外部成果预览", "枚举文件族、校验身份并计算哈希…", () => ExternalPackageService.Preview(root, source, declaration));
            input = source; declarationPath = config; preview = result; registered = "";
            history.DirectorySucceeded(source); maps.InputSucceeded(config); view.Text = JsonSerializer.Serialize(result, options);
        })));
        actions.Controls.Add(Button("确认登记预览文件族", (_, _) => RunUi("登记外部成果", () =>
        {
            if (preview is null) throw new InvalidOperationException("请先成功预览外部成果。");
            string root = Root, source = input; var expected = preview;
            if (expected.Declaration.WorkareaId != CurrentWorkareaId) throw new InvalidOperationException("当前工区已经变化，请重新预览。");
            string parent = _folderLayout.ResolveOperationDirectory(root, "processing_analysis", CurrentWorkareaRelativePath);
            string output = Path.Combine(parent, "外部成果_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N")[..8]);
            registered = BusyOperationDialog.Run(this, "登记外部成果", "复制完整文件族并校验后登记…", () => ExternalPackageService.Register(root, source, output, expected.Declaration, expected: expected));
            preview = null; view.Text = "已登记，状态 REVIEW_REQUIRED：\r\n" + registered;
            FileInteractions.For(this, "external-package", "output").OutputSucceeded(registered);
            if (_artifactWorkspace.HasGridColumns()) RefreshArtifacts();
        })));
        actions.Controls.Add(Button("复核文件与来源", (_, _) => RunUi("复核外部成果包", () =>
        {
            using var dialog = new OpenFileDialog { Filter = "外部成果清单|external-package.json", CheckFileExists = true };
            var history = FileInteractions.For(this, "external-package-review"); history.Prepare(dialog, registered);
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            string root = Root, path = dialog.FileName;
            var result = BusyOperationDialog.Run(this, "复核外部成果包", "检查文件及来源是否缺失或变化…", () => ExternalPackageService.Inspect(root, path));
            history.InputSucceeded(path); view.Text = JsonSerializer.Serialize(result, options);
        })));
        _projectWorkareaContext.Changed += (_, _) => { preview = null; input = declarationPath = registered = ""; view.Clear(); };
        layout.Controls.Add(actions, 0, 1); layout.Controls.Add(view, 0, 2); page.Controls.Add(layout); return page;
    }
}
