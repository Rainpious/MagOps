using System.Globalization;
using MagOps.Core.Analysis;

namespace MagOps.Desktop;

public sealed partial class MainForm
{
    private void BuildAnalysisTab()
    {
        RegisterFeatureControl(
            FeatureIds.AnalysisTools,
            _analysisWorkspace);
    }

    private void RunGroupAverage(string? selectedPath = null)
    {
        string source = ChooseAnalysisSource("选择用于分组平均的表格", selectedPath);
        var values = PromptDialog.Show(this, "分组平均",
        [
            new("group", "分组列号（从 1 开始）", "1", Required: true),
            new("value", "统计列号（从 1 开始）", "2", Required: true),
            new("decimals", "小数位数", "2", Required: true)
        ]);
        if (values is null) return;

        GroupAverageResult result = _analysisWorkspace.ExecuteAnalysis("分组平均", () => _tableAnalysis.ComputeGroupAverage(
            source,
            int.Parse(values["group"], CultureInfo.InvariantCulture),
            int.Parse(values["value"], CultureInfo.InvariantCulture),
            int.Parse(values["decimals"], CultureInfo.InvariantCulture)));

        _analysisWorkspace.ShowResult(
            result.Rows,
            $"源文件：{source}\r\n数据行：{result.SourceRowCount}；有效数值：{result.ValidValueCount}；" +
            $"跳过：{result.SkippedValueCount}；分组：{result.Rows.Count}\r\n\r\n" +
            result.ToTabText());
        SetStatus("分组平均完成。");
    }

    private void RunTableSplit(string? selectedPath = null)
    {
        string source = ChooseAnalysisSource("选择要拆分的文本表格", selectedPath);
        var values = PromptDialog.Show(this, "拆分表格",
        [
            new("column", "拆分列号（从 1 开始）", "1", Required: true)
        ]);
        if (values is null) return;

        string analysisRoot = ResolveToolOutputDirectory(
            "processing_analysis",
            "表格拆分输出");
        string outputDirectory = Path.Combine(
            analysisRoot,
            $"表格拆分_{DateTime.Now:yyyyMMdd_HHmmss}");
        Directory.CreateDirectory(outputDirectory);

        var outputs = _analysisWorkspace.ExecuteAnalysis("拆分表格", () => _tableAnalysis.Split(
            source,
            int.Parse(values["column"], CultureInfo.InvariantCulture),
            outputDirectory));

        _analysisWorkspace.ShowResult(
            outputs
                .Select((p, i) =>
                    new
                    {
                        序号 = i + 1,
                        文件 = p
                    })
                .ToList(),
            $"拆分完成：{outputs.Count} 个文件\r\n" +
            string.Join("\r\n", outputs));
        RegisterGeneratedBusinessFiles(outputs, "analysis_split");
        SetStatus($"拆分表格完成：{outputs.Count} 个文件。");
        TransientNotice.Show(this, $"拆分结果已生成：{outputs.Count} 个文件", "文件写入成功");
    }

    private void RunCsvMerge(IReadOnlyList<string>? selectedFiles = null)
    {
        string[] fileNames;
        if (selectedFiles is null)
        {
            using var open = new OpenFileDialog
            {
                Filter = "文本表格 (*.csv;*.txt;*.tsv)|*.csv;*.txt;*.tsv",
                Multiselect = true,
                Title = "选择至少两个待合并表格"
            };
            if (open.ShowDialog(this) != DialogResult.OK) return;
            fileNames = open.FileNames;
        }
        else
        {
            fileNames = selectedFiles
                .Where(File.Exists)
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        if (fileNames.Length < 2)
            throw new InvalidOperationException("请至少选择 2 个表格文件。");
        if (fileNames.Any(path =>
                !FileDropSupport.HasExtension(path, ".csv", ".txt", ".tsv")))
        {
            throw new InvalidDataException("CSV 合并只接受 CSV/TXT/TSV 文件。");
        }

        var settings = PromptDialog.Show(this, "CSV 合并",
        [
            new("mode", "方式：按列位置合并 / 按表头名称对齐（并集）", "按列位置合并", Required: true),
            new("source", "添加来源文件列：是/否", "否", Required: true),
            new("name", "输出文件名", $"合并结果_{DateTime.Now:yyyyMMdd_HHmmss}.csv", Required: true)
        ]);
        if (settings is null) return;

        RenumberRequest? renumber = null;
        if (MessageBox.Show(this,
                "是否按线号空间中心重新编号？\r\n仅改线号字段，不改变数据行顺序。",
                "线号重排", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
        {
            var r = PromptDialog.Show(this, "线号重排",
            [
                new("line", "线号列号（从 1 开始）", "1", Required: true),
                new("x", "X 列号（从 1 开始）", "2", Required: true),
                new("y", "Y 列号（从 1 开始）", "3", Required: true),
                new("direction", "方向：从西往东 / 从南往北", "从西往东", Required: true)
            ]);
            if (r is null) return;
            renumber = new RenumberRequest(
                int.Parse(r["line"], CultureInfo.InvariantCulture) - 1,
                int.Parse(r["x"], CultureInfo.InvariantCulture) - 1,
                int.Parse(r["y"], CultureInfo.InvariantCulture) - 1,
                r["direction"]);
        }

        string outputName = Path.GetFileName(settings["name"].Trim());
        if (!outputName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            outputName += ".csv";

        string outputPath = ResolveToolOutputFilePath(
            "processing_analysis",
            "CSV 合并输出",
            outputName,
            "CSV 文件 (*.csv)|*.csv|所有文件 (*.*)|*.*",
            ".csv");

        MergeResult result = _analysisWorkspace.ExecuteAnalysis("合并 CSV", () => _tableAnalysis.MergeCsv(
            fileNames,
            outputPath,
            settings["mode"],
            settings["source"].Trim().Equals("是", StringComparison.OrdinalIgnoreCase),
            renumber));

        _analysisWorkspace.ShowResult(
            result.RenumberInfo?.Centers,
            $"合并完成：{result.OutputPath}\r\n数据行：{result.RowCount}\r\n" +
            $"表头不一致文件：{string.Join("、", result.HeaderMismatchFiles)}\r\n" +
            (result.RenumberInfo is null
                ? "未执行线号重排。"
                : $"线号重排：{result.RenumberInfo.Direction}；共 {result.RenumberInfo.Centers.Count} 条线；" +
                  $"格式：{result.RenumberInfo.FormatDescription}"));
        RegisterGeneratedBusinessFiles([result.OutputPath], "analysis_merge");
        SetStatus("CSV 合并完成。");
        TransientNotice.Show(this, $"CSV 合并文件已生成：{Path.GetFileName(result.OutputPath)}", "文件写入成功");
    }

    private void RunDynamicNoise(string? selectedPath = null)
    {
        string source = ChooseAnalysisSource("选择动态噪声源表", selectedPath);
        var values = PromptDialog.Show(this, "动态噪声",
        [
            new("t", "T / 磁异常列号（从 1 开始）", "1", Required: true),
            new("group", "测线/分组列号（留空=ALL）", ""),
            new("x", "X/经度列号（梯度过滤时必填）", ""),
            new("y", "Y/纬度列号（梯度过滤时必填）", ""),
            new("mode", "坐标：自动判断 / 经纬度（WGS84） / 平面坐标（米制）", "自动判断"),
            new("limit", "水平梯度阈值 nT/km", "600")
        ]);
        if (values is null) return;

        int? OptionalColumn(string key) =>
            string.IsNullOrWhiteSpace(values[key])
                ? null
                : int.Parse(values[key], CultureInfo.InvariantCulture) - 1;

        DynamicNoiseResult result = _analysisWorkspace.ExecuteAnalysis("动态噪声", () =>
        {
            bool useGradient = OptionalColumn("x").HasValue || OptionalColumn("y").HasValue;
            return _dynamicNoise.Compute(
                source,
                int.Parse(values["t"], CultureInfo.InvariantCulture) - 1,
                OptionalColumn("group"),
                OptionalColumn("x"),
                OptionalColumn("y"),
                values["mode"],
                useGradient,
                double.Parse(values["limit"], CultureInfo.InvariantCulture));
        });

        _analysisWorkspace.ShowResult(
            result.Lines,
            $"有效测线：{result.Summary.ValidLineCount}/{result.Summary.LineCount}\r\n" +
            $"Sv 最大：{result.Summary.SvMax:G8} ({result.Summary.SvMaxGroup})\r\n" +
            $"Sv 最小：{result.Summary.SvMin:G8} ({result.Summary.SvMinGroup})\r\n" +
            $"Sv 平均：{result.Summary.SvMean:G8}；中位：{result.Summary.SvMedian:G8}\r\n" +
            $"无效 T：{result.Summary.TotalInvalidT}；梯度超限：{result.Summary.TotalHighGradient}；" +
            $"梯度不可用：{result.Summary.TotalGradientUnavailable}");
        SetStatus("动态噪声计算完成。");
    }

    private void RunElevationKml(string? selectedPath = null)
    {
        string source = ChooseAnalysisSource("选择图新高程 CSV", selectedPath);
        var values = PromptDialog.Show(this, "高程 CSV → KML",
        [
            new("offset", "计划离地高度 / Z 增量（m）", "0", Required: true),
            new("name", "输出文件名", Path.GetFileNameWithoutExtension(source) + "_高程.kml", Required: true)
        ]);
        if (values is null) return;

        string outputName = Path.GetFileName(values["name"].Trim());
        if (!outputName.EndsWith(".kml", StringComparison.OrdinalIgnoreCase))
            outputName += ".kml";

        string outputPath = ResolveToolOutputFilePath(
            "processing_analysis",
            "高程 KML 输出",
            outputName,
            "KML 文件 (*.kml)|*.kml|所有文件 (*.*)|*.*",
            ".kml");

        var points = _analysisWorkspace.ExecuteAnalysis("高程 CSV → KML", () =>
        {
            var loaded = _elevation.Read(source);
            _elevation.WriteKml(
                outputPath,
                loaded,
                double.Parse(values["offset"], CultureInfo.InvariantCulture),
                "带高程航线",
                Path.GetFileName(source));
            return loaded;
        });

        _analysisWorkspace.ShowResult(
            points.Take(500).ToList(),
            $"高程点：{points.Count}\r\n" +
            $"累计距离：{points[^1].DistanceM / 1000.0:F3} km\r\n" +
            $"输出：{outputPath}\r\n" +
            "KML 使用 altitudeMode=absolute，Z=terrain_z+计划离地高度。");
        RegisterGeneratedBusinessFiles([outputPath], "analysis_elevation");
        SetStatus("高程 KML 已生成。");
        TransientNotice.Show(this, $"高程 KML 已生成：{Path.GetFileName(outputPath)}", "文件写入成功");
    }

    private string ChooseAnalysisSource(string title, string? selectedPath = null)
    {
        string filePath = selectedPath?.Trim() ?? "";
        if (filePath.Length == 0)
        {
            using var dialog = new OpenFileDialog
            {
                Filter = "文本表格 (*.csv;*.txt;*.tsv)|*.csv;*.txt;*.tsv|所有文件 (*.*)|*.*",
                Title = title
            };
            if (dialog.ShowDialog(this) != DialogResult.OK)
                throw new OperationCanceledException("用户取消了文件选择。");
            filePath = dialog.FileName;
        }

        if (!File.Exists(filePath) ||
            !FileDropSupport.HasExtension(filePath, ".csv", ".txt", ".tsv"))
        {
            throw new InvalidDataException("请选择有效的 CSV/TXT/TSV 表格文件。");
        }

        return Path.GetFullPath(filePath);
    }
}
