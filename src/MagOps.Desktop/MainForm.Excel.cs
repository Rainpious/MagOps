using MagOps.Core.Analysis;

namespace MagOps.Desktop;

public sealed partial class MainForm
{
    private void BuildExcelTab()
    {
        RegisterFeatureControl(
            FeatureIds.ExcelProcessing,
            _excelWorkspace);
    }

    private void SetExcelMergeSource(
        ExcelMergeSourceSlot slot,
        string path)
    {
        if (!File.Exists(path) ||
            !ExcelWorkspaceControl.AcceptSource(path))
        {
            throw new InvalidDataException(
                "请选择 CSV/TXT/TSV 表格文件。");
        }

        _excelWorkspace.SetSource(
            slot,
            Path.GetFullPath(path));
        UpdateExcelMergeOutputName();

        if (File.Exists(_excelWorkspace.GetFirstPath()) &&
            File.Exists(_excelWorkspace.GetSecondPath()))
        {
            RefreshExcelMergeComparison();
        }
        else
        {
            _excelWorkspace.SetStatus(
                "已选择一个表格，请继续选择另一个表格。",
                compatible: null);
        }
    }

    private void UpdateExcelMergeOutputName()
    {
        string a =
            File.Exists(
                _excelWorkspace.GetFirstPath())
                ? Path.GetFileNameWithoutExtension(
                    _excelWorkspace.GetFirstPath())
                : "表A";
        string b =
            File.Exists(
                _excelWorkspace.GetSecondPath())
                ? Path.GetFileNameWithoutExtension(
                    _excelWorkspace.GetSecondPath())
                : "表B";

        _excelWorkspace.SetOutputName(
            $"{SafeExcelMergeNamePart(a)}_{SafeExcelMergeNamePart(b)}_合并.csv");
    }

    private static string SafeExcelMergeNamePart(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string cleaned = new string(value.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "表格" : cleaned.Trim();
    }

    private void RefreshExcelMergeComparison()
    {
        try { RefreshExcelMergeComparisonCore(); }
        catch (Exception ex)
        {
            _excelWorkspace.ShowFailure("表头检查", ex, clearHeaders: true);
            throw;
        }
    }

    private void RefreshExcelMergeComparisonCore()
    {
        string first =
            _excelWorkspace.GetFirstPath();
        string second =
            _excelWorkspace.GetSecondPath();
        if (!File.Exists(first) || !File.Exists(second))
            throw new InvalidOperationException("请先选择表 A 和表 B。\r\n也可以直接把文件拖到对应输入框或选择按钮上。");

        TableHeaderComparison result = _sameHeaderTableMerge.CompareHeaders(first, second);
        _excelWorkspace.SetStatus(
            result.Message,
            result.Compatible);

        var secondIndex = result.SecondHeaders
            .Select((header, index) => (Key: header.Trim(), Index: index))
            .ToDictionary(x => x.Key, x => x.Index, StringComparer.OrdinalIgnoreCase);

        var rows = new List<ExcelHeaderPreviewRow>();
        var firstKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < result.FirstHeaders.Length; i++)
        {
            string header = result.FirstHeaders[i];
            firstKeys.Add(header.Trim());
            if (secondIndex.TryGetValue(header.Trim(), out int bi))
            {
                rows.Add(new ExcelHeaderPreviewRow(
                    i + 1,
                    header,
                    bi + 1,
                    result.SecondHeaders[bi],
                    i == bi ? "一致" : "自动对齐"));
            }
            else
            {
                rows.Add(new ExcelHeaderPreviewRow(i + 1, header, null, "", "表 B 缺少"));
            }
        }

        for (int i = 0; i < result.SecondHeaders.Length; i++)
        {
            string header = result.SecondHeaders[i];
            if (!firstKeys.Contains(header.Trim()))
                rows.Add(new ExcelHeaderPreviewRow(null, "", i + 1, header, "仅表 B"));
        }

        _excelWorkspace.ShowHeaderRows(
            rows);
        _excelWorkspace.ShowInfo(
            $"表 A：{result.FirstPath}\r\n" +
            $"表 B：{result.SecondPath}\r\n\r\n" +
            result.Message +
            (result.Compatible
                ? "\r\n\r\n合并时只写一行表头，先写表 A 的全部数据行，再写表 B 的全部数据行。"
                : "\r\n\r\n当前不会生成结果文件，请先统一两个表格的表头字段。\r\n" +
                  $"仅表 A：{string.Join("、", result.OnlyInFirst)}\r\n" +
                  $"仅表 B：{string.Join("、", result.OnlyInSecond)}"), activate: !result.Compatible);
        SetStatus(result.Compatible ? "Excel 表头检查通过。" : "Excel 表头不一致，已阻止合并。");
    }

    private async Task MergeExcelTablesAsync()
    {
        bool wasEnabled = Enabled;
        Enabled = false;
        try { await MergeExcelTablesCoreAsync(); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _excelWorkspace.ShowFailure("表格合并", ex, clearHeaders: false);
            throw;
        }
        finally { if (!IsDisposed) Enabled = wasEnabled; }
    }

    private async Task MergeExcelTablesCoreAsync()
    {
        string first =
            _excelWorkspace.GetFirstPath();
        string second =
            _excelWorkspace.GetSecondPath();
        if (!File.Exists(first) || !File.Exists(second))
            throw new InvalidOperationException("请先选择表 A 和表 B。");

        TableHeaderComparison comparison = _sameHeaderTableMerge.CompareHeaders(first, second);
        if (!comparison.Compatible)
        {
            RefreshExcelMergeComparison();
            _excelWorkspace.SetStatus(
                comparison.Message,
                compatible: false);
            throw new InvalidDataException(comparison.Message);
        }

        string outputName =
            Path.GetFileName(
                _excelWorkspace.GetOutputName());
        if (string.IsNullOrWhiteSpace(outputName))
            throw new InvalidOperationException("请输入输出文件名。");
        if (!outputName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            outputName += ".csv";

        string outputPath = ResolveToolOutputFilePath(
            "processing_excel",
            "Excel 同表头合并输出",
            outputName,
            "CSV 文件 (*.csv)|*.csv|所有文件 (*.*)|*.*",
            ".csv");

        SetStatus("正在合并表格，请稍候…");
        _excelWorkspace.ShowInfo("正在合并当前选择的表格，完成后在此显示本轮成果位置。", activate: true);
        SameHeaderMergeResult result = await Task.Run(() =>
            _sameHeaderTableMerge.Merge(first, second, outputPath));
        RegisterGeneratedBusinessFiles([result.OutputPath], "excel_same_header_merge");

        _excelWorkspace.ShowInfo(
            $"合并完成：{result.OutputPath}\r\n" +
            $"列数：{result.ColumnCount}\r\n" +
            $"表 A 数据行：{result.FirstDataRows:N0}\r\n" +
            $"表 B 数据行：{result.SecondDataRows:N0}\r\n" +
            $"合计数据行：{result.TotalDataRows:N0}\r\n" +
            $"表 B 列顺序自动调整：{(result.SecondTableReordered ? "是" : "否")}\r\n" +
            "输出编码：UTF-8 BOM；输出格式：CSV。", activate: true);
        _excelWorkspace.SetStatus(
            $"合并完成：{result.TotalDataRows:N0} 行数据。",
            compatible: true);
        SetStatus("Excel 同表头表格合并完成。");
        TransientNotice.Show(this, $"合并文件已生成：{Path.GetFileName(result.OutputPath)}", "文件写入成功");
    }

}
