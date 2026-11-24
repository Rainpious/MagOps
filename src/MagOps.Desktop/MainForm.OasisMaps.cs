using System.Globalization;
using MagOps.Infrastructure.Services;

namespace MagOps.Desktop;

public sealed partial class MainForm
{
    private string _latestOasisExport = "";
    private bool _oasisInputIsManual;
    private CancellationTokenSource? _oasisLookup;

    private void BuildOasisMapTab()
    {
        var page = new TabPage("Oasis成图");
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, ColumnCount = 4, RowCount = 1, Padding = new Padding(8)
        };
        row.ColumnStyles.Add(new(SizeType.AutoSize));
        row.ColumnStyles.Add(new(SizeType.Percent, 100));
        row.ColumnStyles.Add(new(SizeType.AutoSize));
        row.ColumnStyles.Add(new(SizeType.AutoSize));
        const string inputHint = "拖入分线成果 CSV 或 CSV.GZ，或粘贴文件路径";
        var input = new TextBox
        {
            AccessibleName = "分线成果文件", Anchor = AnchorStyles.Left | AnchorStyles.Right,
            PlaceholderText = inputHint
        };
        string InputPath() => input.Text.Trim().Trim('"');
        bool filling = false;
        void Fill(string path)
        {
            filling = true;
            try { input.Text = path; }
            finally { filling = false; }
        }

        var browse = Button("浏览…", (_, _) =>
        {
            using var open = new OpenFileDialog { Filter = "航磁测点 CSV|*.csv;*.csv.gz", Title = "选择分线后的 dTgz 或 dT 成果" };
            FileInteractions.For(this, "processing_oasis").Prepare(open, InputPath());
            if (open.ShowDialog(this) == DialogResult.OK) input.Text = open.FileName;
        });
        var draw = Button("生成图件与 KMZ…", (_, _) => RunUi("Oasis 航磁成图", () => DrawOasisMaps(InputPath())), UiButtonRole.Primary);
        draw.Enabled = false;
        input.TextChanged += (_, _) =>
        {
            if (!filling)
            {
                _oasisLookup?.Cancel();
                _oasisInputIsManual = input.TextLength > 0;
            }
            draw.Enabled = OasisSurveyInput.IsSurveyFile(InputPath()) && File.Exists(InputPath());
        };
        FileDropSupport.EnableSingle(input, path => input.Text = path, OasisSurveyInput.IsSurveyFile,
            onError: _ => SetStatus("请拖入一份分线成果 .csv 或 .csv.gz 文件。"));
        row.Controls.Add(new Label { Text = "分线成果", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        row.Controls.Add(input, 1, 0);
        row.Controls.Add(browse, 2, 0);
        row.Controls.Add(draw, 3, 0);
        page.Controls.Add(row);
        async void RefreshInput()
        {
            _oasisLookup?.Cancel();
            var area = _projectWorkareaContext.Current;
            if (!area.HasProject || !area.HasWorkarea || (_oasisInputIsManual && File.Exists(InputPath()))) return;
            _oasisInputIsManual = false;
            Fill("");
            input.PlaceholderText = "正在查找当前工区的处理用 CSV…";
            using var lookup = new CancellationTokenSource();
            _oasisLookup = lookup;
            string recent = _latestOasisExport;
            try
            {
                string found = await Task.Run(() =>
                {
                    string directory = _folderLayout.ResolveOperationDirectory(area.ProjectRoot, "processing_diurnal", area.WorkareaRelativePath);
                    var context = new GeneratedArtifactContext(_workspace.GetWorkspaceReadOnly(area.ProjectRoot).ProjectId, area.WorkareaId);
                    return OasisSurveyInput.FindProcessingFile(directory, context, recent, lookup.Token);
                }, lookup.Token);
                if (!lookup.IsCancellationRequested && !page.IsDisposed) Fill(found);
            }
            catch (OperationCanceledException) when (lookup.IsCancellationRequested) { }
            catch (Exception ex)
            {
                if (!lookup.IsCancellationRequested && !page.IsDisposed) SetStatus($"查找工区处理用 CSV 失败：{ex.Message}");
            }
            finally
            {
                if (ReferenceEquals(_oasisLookup, lookup))
                {
                    _oasisLookup = null;
                    if (!page.IsDisposed) input.PlaceholderText = inputHint;
                }
            }
        }
        page.Disposed += (_, _) => _oasisLookup?.Cancel();
        RegisterFeaturePage(FeatureIds.OasisProcessing, page, activated: RefreshInput,
            deactivated: () => _oasisLookup?.Cancel(), contextChanged: _ =>
            {
                _oasisLookup?.Cancel();
                _latestOasisExport = "";
                _oasisInputIsManual = false;
                Fill("");
                if (IsFeatureActive(FeatureIds.OasisProcessing)) RefreshInput();
            });
    }

    private void DrawOasisMaps(string inputPath)
    {
        var survey = BusyOperationDialog.Run(this, "检查航磁测点", "读取测线、通道与坐标配准信息…", () => OasisSurveyInput.Read(inputPath));
        FileInteractions.For(this, "processing_oasis").InputSucceeded(inputPath);
        var longest = survey.Lines.MaxBy(line =>
            double.Hypot(line.Points[^1].X - line.Points[0].X, line.Points[^1].Y - line.Points[0].Y))!.Points;
        double azimuth = (Math.Atan2(longest[^1].X - longest[0].X, longest[^1].Y - longest[0].Y) * 180 / Math.PI + 180) % 180;
        double rad = azimuth * Math.PI / 180;
        var tracks = survey.Lines.GroupBy(l => l.Name).Select(ls => ls.SelectMany(l => l.Points).Average(p => p.X * Math.Cos(rad) - p.Y * Math.Sin(rad))).Order().ToArray();
        var gaps = tracks.Zip(tracks.Skip(1), (a, b) => b - a).Where(d => d > 0).Order().ToArray();
        if (gaps.Length == 0) throw new InvalidDataException("无法从测线估算间距。");
        double spacing = Math.Max(1, Math.Round(gaps[gaps.Length / 2]));
        using var dialog = new Form
        {
            Text = "Oasis 航磁成图",
            StartPosition = FormStartPosition.CenterParent,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false
        };
        var layout = new TableLayoutPanel { AutoSize = true, Padding = new Padding(14), ColumnCount = 2, Dock = DockStyle.Fill };
        dialog.Controls.Add(layout);
        void Row(string label, Control control)
        {
            int r = layout.RowCount++;
            layout.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, r);
            layout.Controls.Add(control, 1, r);
        }
        TextBox Field(string label, string initial)
        {
            var control = new TextBox { Text = initial, Width = 300 };
            Row(label, control);
            return control;
        }
        Row("输入通道", new Label { AutoSize = true, Text = survey.IsLevelled ? "dT · 从成品网格化开始" : "dTgz · 先调用 Oasis 微调平" });
        var executable = Field("本机 omscore.exe", FindOasisEngine());
        var locate = new Button { Text = "选择 Oasis 安装程序…", AutoSize = true };
        locate.Click += (_, _) =>
        {
            using var picker = new OpenFileDialog { Filter = "Oasis 原生程序|omscore.exe" };
            if (picker.ShowDialog(dialog) == DialogResult.OK) executable.Text = picker.FileName;
        };
        Row("", locate);
        var distance = Field("实测线距（m）", spacing.ToString(CultureInfo.InvariantCulture));
        var bearing = Field("测线方位（°，自格网北顺时针）", (Math.Round(azimuth, 2) % 180).ToString("F2", CultureInfo.InvariantCulture));
        var advanced = new CheckBox { Text = "调整格距试算与滤波参数", AutoSize = true };
        Row("", advanced);
        int firstAdvancedRow = layout.RowCount;
        var strip = Field("连续空隙判定长度（m）", spacing.ToString(CultureInfo.InvariantCulture));
        var points = survey.Lines.SelectMany(l => l.Points).ToArray();
        double width = points.Max(p => p.X) - points.Min(p => p.X), height = points.Max(p => p.Y) - points.Min(p => p.Y);
        int firstCell = 1;
        while ((Math.Ceiling(width / firstCell) + 3) * (Math.Ceiling(height / firstCell) + 3) > 12000000) firstCell++;
        var first = Field("候选格距起点（m）", firstCell.ToString());
        var step = Field("候选格距步长（m）", "1");
        var last = Field("候选格距上限（m）", Math.Max(Math.Max(firstCell, 3), Math.Ceiling(spacing / 2)).ToString(CultureInfo.InvariantCulture));
        var hanning = Field("汉宁滤波遍数", "5");
        var amplitude = Field("化极振幅校正倾角（°）", "20");
        var advancedFields = layout.Controls.Cast<Control>().Where(c => layout.GetRow(c) >= firstAdvancedRow).ToArray();
        foreach (var c in advancedFields) c.Visible = false;
        advanced.CheckedChanged += (_, _) => { foreach (var c in advancedFields) c.Visible = advanced.Checked; };
        var presets = new CheckedListBox { CheckOnClick = true, Height = 64, Width = 300, IntegralHeight = false };
        foreach (int h in new[] { 0, 20, 50, 100 }) presets.Items.Add(h, h == 20);
        Row("上延距离（m，可多选）", presets);
        var heights = Field("其他上延距离（m）", "");
        heights.PlaceholderText = "可自定多个，例如 30, 75";
        var context = _projectWorkareaContext.Current;
        string area = !_oasisInputIsManual && context.HasWorkarea
            ? Path.GetFileName(context.WorkareaRelativePath) : Path.GetFileNameWithoutExtension(inputPath);
        var title = Field("图件名称", area);
        var scale = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
        scale.Items.AddRange(["自动", "全量程", "手动锁定"]);
        scale.SelectedIndex = 0;
        Row("色标", scale);
        var hint = new Label { AutoSize = true, Text = "磁场用全量程，一阶、二阶垂导用 P99" };
        Row("", hint);
        int lockRow = layout.RowCount;
        var fieldLimit = Field("磁场上限（± nT）", "");
        var firstLimit = Field("一阶垂导上限（± nT/m）", "");
        var secondLimit = Field("二阶垂导上限（± nT/m²）", "");
        var lockFields = layout.Controls.Cast<Control>().Where(c => layout.GetRow(c) >= lockRow).ToArray();
        void UpdateScale()
        {
            foreach (var c in lockFields) c.Visible = scale.SelectedIndex == 2;
            hint.Text = scale.SelectedIndex switch { 1 => "三组色标均按最大绝对值向上圆整",
                2 => "各量纲使用填写值，多个高度共用范围", _ => "磁场用全量程，一阶、二阶垂导用 P99" };
        }
        UpdateScale();
        scale.SelectedIndexChanged += (_, _) => UpdateScale();
        var format = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList }; format.Items.AddRange(["PNG", "JPG"]); format.SelectedIndex = 0;
        Row("成果图片（同时保存 KMZ）", format);
        var run = new Button { Text = "生成成果", AutoSize = true, DialogResult = DialogResult.OK };
        Row("", run); dialog.AcceptButton = run;
        dialog.FormClosing += (_, e) =>
        {
            if (dialog.DialogResult != DialogResult.OK || scale.SelectedIndex != 2) return;
            foreach (var box in new[] { fieldLimit, firstLimit, secondLimit })
            {
                if (double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) &&
                    double.IsFinite(value) && value > 0) continue;
                e.Cancel = true;
                MessageBox.Show(dialog, "请填写大于零的有限色标上限。", "锁定色标");
                box.Focus();
                break;
            }
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        static double Number(string s) => double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
        var options = new OasisMapOptions(executable.Text.Trim(), Number(distance.Text), Number(bearing.Text),
            int.Parse(first.Text), int.Parse(step.Text), int.Parse(last.Text), Number(strip.Text),
            presets.CheckedItems.Cast<int>().Select(h => (double)h).Concat(
                heights.Text.Split([',', '，', ';', '；', ' '], StringSplitOptions.RemoveEmptyEntries).Select(Number)).Distinct().ToArray(), format.Text,
            int.Parse(hanning.Text), Number(amplitude.Text))
        {
            AreaTitle = title.Text,
            Scale = scale.SelectedIndex == 2
                ? new(OasisScaleMode.Locked, Number(fieldLimit.Text), Number(firstLimit.Text), Number(secondLimit.Text))
                : new(scale.SelectedIndex == 1 ? OasisScaleMode.Full : OasisScaleMode.Auto)
        };
        var parent = ResolveToolOutputDirectory("processing_oasis", "Oasis 航磁成图");
        string result = BusyOperationDialog.Run(this, "Oasis 航磁成图", "正在调用 Oasis、选择成品格距并生成图件…",
            () => OasisMapJob.Run(inputPath, parent, options, OasisMapImage.Export));
        var maps = Directory.GetFiles(Path.Combine(result, "maps")).Order().Append(Path.Combine(result, "result.json")).ToList();
        RegisterGeneratedBusinessFiles(maps, "oasis_magnetic_maps");
        using var report = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(result, "result.json")));
        var limits = report.RootElement.GetProperty("palette").GetProperty("symmetricLimitsByUnit").EnumerateObject();
        string summary = string.Join("    ", limits.Select(p => $"±{p.Value.GetDouble():G7} {p.Name}"));
        SetStatus("成图完成    " + summary);
        MessageBox.Show(this, summary + Environment.NewLine + Path.Combine(result, "maps"), "航磁成果已保存", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private static string FindOasisEngine()
    {
        var candidates = new List<string>();
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed))
            foreach (string folder in new[] { "Program Files (x86)", "Program Files" })
                candidates.Add(Path.Combine(drive.Name, folder, "Geosoft", "Oasis montaj", "bin", "omscore.exe"));
        return candidates.FirstOrDefault(File.Exists) ?? "";
    }
}
