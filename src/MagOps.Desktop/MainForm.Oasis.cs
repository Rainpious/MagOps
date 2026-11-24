using MagOps.Core.Analysis;
using MagOps.Infrastructure.Services;

namespace MagOps.Desktop;

public sealed partial class MainForm
{
    private void BuildExperimentalToolsTab()
    {
        _oasisGdbA.AccessibleName = "基准 GDB A 路径";
        _oasisGdbB.AccessibleName = "追加 GDB B 路径";
        _oasisPython.AccessibleName = "Oasis Python 解释器路径";
        _oasisOutputName.AccessibleName = "合并 GDB 成果文件名";
        _oasisHeaderGrid.AccessibleName = "Oasis 表头对应";
        _oasisInfo.AccessibleName = "Oasis 处理说明与结果";
        var page = new TabPage("实验工具") { Padding = Padding.Empty };
        var shell = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(8),
            Margin = Padding.Empty
        };
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var mergeBox = new GroupBox
        {
            Text = "按表头对应追加合并 Geosoft/Oasis GDB",
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(10, 8, 10, 10),
            Margin = new Padding(0, 8, 0, 8)
        };

        var form = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 5,
            RowCount = 5,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        form.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        form.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        form.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        form.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        for (int i = 0; i < form.RowCount; i++)
            form.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        static Label FieldLabel(string text) => new()
        {
            Text = text,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(3, 8, 10, 3)
        };

        ConfigureOasisPathBox(_oasisGdbA, "选择或拖入作为基准的 .gdb（输出以它为基础）");
        ConfigureOasisPathBox(_oasisGdbB, "选择或拖入需要追加的数据 .gdb");
        ConfigureOasisPathBox(_oasisPython, "自动检测；失败时请选择 Oasis/Geosoft Python 的 python.exe");

        var chooseA = Button("选择 A", (_, _) => RunUi("选择基准 GDB", () => ChooseOasisGdb(_oasisGdbA)));
        var chooseB = Button("选择 B", (_, _) => RunUi("选择追加 GDB", () => ChooseOasisGdb(_oasisGdbB)));
        var choosePython = Button("选择 Python", (_, _) => RunUi("选择 Geosoft Python", ChooseOasisPython));
        var detectPython = Button("自动检测", async (_, _) =>
            await RunUiAsync("检测 Geosoft Python", DetectOasisPythonAsync));

        FileDropSupport.EnableSingle(
            _oasisGdbA,
            path => RunUi("选择基准 GDB", () => SetOasisGdb(_oasisGdbA, path)),
            AcceptOasisGdb);
        FileDropSupport.EnableSingle(
            chooseA,
            path => RunUi("选择基准 GDB", () => SetOasisGdb(_oasisGdbA, path)),
            AcceptOasisGdb);
        FileDropSupport.EnableSingle(
            _oasisGdbB,
            path => RunUi("选择追加 GDB", () => SetOasisGdb(_oasisGdbB, path)),
            AcceptOasisGdb);
        FileDropSupport.EnableSingle(
            chooseB,
            path => RunUi("选择追加 GDB", () => SetOasisGdb(_oasisGdbB, path)),
            AcceptOasisGdb);
        FileDropSupport.EnableSingle(
            _oasisPython,
            path => RunUi("选择 Geosoft Python", () => SetOasisPython(path)),
            AcceptPythonExecutable);
        FileDropSupport.EnableSingle(
            choosePython,
            path => RunUi("选择 Geosoft Python", () => SetOasisPython(path)),
            AcceptPythonExecutable);

        form.Controls.Add(FieldLabel("基准 GDB A"), 0, 0);
        form.Controls.Add(_oasisGdbA, 1, 0);
        form.Controls.Add(chooseA, 2, 0);
        form.SetColumnSpan(chooseA, 3);

        form.Controls.Add(FieldLabel("追加 GDB B"), 0, 1);
        form.Controls.Add(_oasisGdbB, 1, 1);
        form.Controls.Add(chooseB, 2, 1);
        form.SetColumnSpan(chooseB, 3);

        form.Controls.Add(FieldLabel("Oasis Python"), 0, 2);
        form.Controls.Add(_oasisPython, 1, 2);
        form.Controls.Add(choosePython, 2, 2);
        form.Controls.Add(detectPython, 3, 2);
        form.SetColumnSpan(detectPython, 2);

        _oasisOutputName.Text = $"Oasis_GDB_合并_{DateTime.Now:yyyyMMdd_HHmmss}.gdb";
        _oasisOutputName.Dock = DockStyle.Fill;
        _oasisOutputName.Margin = new Padding(0, 3, 6, 3);
        form.Controls.Add(FieldLabel("输出文件名"), 0, 3);
        form.Controls.Add(_oasisOutputName, 1, 3);

        var compare = Button("读取 / 对应表头", async (_, _) =>
            await RunUiAsync("读取 Oasis GDB", InspectOasisGdbsAsync));
        var merge = Button("追加合并 GDB", async (_, _) =>
            await RunUiAsync("合并 Oasis GDB", MergeOasisGdbsAsync));
        form.Controls.Add(compare, 2, 3);
        form.Controls.Add(merge, 3, 3);
        form.SetColumnSpan(merge, 2);

        _oasisStatus.Text = "请选择两个 GDB 数据库。";
        _oasisStatus.AutoSize = true;
        _oasisStatus.Padding = new Padding(3, 5, 3, 3);
        _oasisStatus.ForeColor = SystemColors.GrayText;
        form.Controls.Add(FieldLabel("对应状态"), 0, 4);
        form.Controls.Add(_oasisStatus, 1, 4);
        form.SetColumnSpan(_oasisStatus, 4);

        const string rules =
                "规则：同名表头自动对应；没有同名表头时弹出选择窗口，由用户指定 B 的表头对应到 A 的哪个表头。" +
                "内容为 i--- / i010 / i027 等 i+三位编号形式的特殊占位列会按内容识别，即使列名不同也自动对应。" +
                "输入 GDB 不会原地修改，结果始终写入新的 .gdb。";

        mergeBox.Controls.Add(form);
        shell.Controls.Add(mergeBox, 0, 0);

        GridFactory.SetEmptyText(_oasisHeaderGrid, "读取两个 GDB 后，这里显示自动匹配、占位列匹配和用户选择的表头对应关系。");
        var results = new TabControl { Dock = DockStyle.Fill };
        var headers = new TabPage("表头对应");
        headers.Controls.Add(_oasisHeaderGrid);
        results.TabPages.Add(headers);
        shell.Controls.Add(results, 0, 1);

        _oasisInfo.Multiline = true;
        _oasisInfo.ReadOnly = true;
        _oasisInfo.ScrollBars = ScrollBars.Both;
        _oasisInfo.WordWrap = false;
        _oasisInfo.Dock = DockStyle.Fill;
        _oasisInfo.Text =
            "用途：把连续采集得到的两个 Oasis/Geosoft GDB 按 channel 表头对应后追加到一个新数据库。\r\n" +
            "GDB 读写通过 Geosoft 官方 GX API 完成，不直接修改 GDB 二进制结构。\r\n" + rules;
        var details = new TabPage("处理说明与结果");
        details.Controls.Add(_oasisInfo);
        results.TabPages.Add(details);

        page.Controls.Add(shell);
        _oasisGdbA.TextChanged += (_, _) => InvalidateOasisInspection();
        _oasisGdbB.TextChanged += (_, _) => InvalidateOasisInspection();
        RegisterFeaturePage(FeatureIds.ExperimentalTools, page);
    }

    private static bool AcceptOasisGdb(string path) =>
        FileDropSupport.HasExtension(path, ".gdb");

    private static bool AcceptPythonExecutable(string path) =>
        FileDropSupport.HasExtension(path, ".exe") &&
        Path.GetFileName(path).Equals("python.exe", StringComparison.OrdinalIgnoreCase);

    private static void ConfigureOasisPathBox(TextBox box, string placeholder)
    {
        box.Dock = DockStyle.Fill;
        box.Margin = new Padding(0, 3, 6, 3);
        box.PlaceholderText = placeholder;
    }

    private void ChooseOasisGdb(TextBox target)
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "Geosoft/Oasis 数据库 (*.gdb)|*.gdb|所有文件 (*.*)|*.*",
            Title = ReferenceEquals(target, _oasisGdbA) ? "选择基准 GDB A" : "选择追加 GDB B"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            throw new OperationCanceledException();
        SetOasisGdb(target, dialog.FileName);
    }

    private void SetOasisGdb(TextBox target, string path)
    {
        if (!File.Exists(path) || !AcceptOasisGdb(path))
            throw new InvalidDataException("请选择 Geosoft/Oasis .gdb 数据库文件。 ");

        target.Text = Path.GetFullPath(path);
        InvalidateOasisInspection();
        UpdateOasisOutputName();
        if (File.Exists(_oasisGdbA.Text.Trim()) && File.Exists(_oasisGdbB.Text.Trim()))
            SetOasisStatus("两个 GDB 已选择，请读取 / 对应表头。", null);
        else
            SetOasisStatus("已选择一个 GDB，请继续选择另一个。", null);
    }

    private void UpdateOasisOutputName()
    {
        string a = File.Exists(_oasisGdbA.Text.Trim())
            ? Path.GetFileNameWithoutExtension(_oasisGdbA.Text.Trim())
            : "基准";
        string b = File.Exists(_oasisGdbB.Text.Trim())
            ? Path.GetFileNameWithoutExtension(_oasisGdbB.Text.Trim())
            : "追加";
        _oasisOutputName.Text = $"{SafeExcelMergeNamePart(a)}_{SafeExcelMergeNamePart(b)}_表头对应合并.gdb";
    }

    private void ChooseOasisPython()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "Python 解释器 (python.exe)|python.exe|可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
            Title = "选择已安装 Geosoft GX Python 模块的 python.exe"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            throw new OperationCanceledException();
        SetOasisPython(dialog.FileName);
    }

    private void SetOasisPython(string path)
    {
        if (!File.Exists(path) || !AcceptPythonExecutable(path))
            throw new InvalidDataException("请选择 python.exe。 ");
        _oasisPython.Text = Path.GetFullPath(path);
        if (_oasisGdb.TryProbeGeosoftPython(_oasisPython.Text, out string message))
        {
            SetOasisStatus(message, true);
            return;
        }
        SetOasisStatus(message, false);
        throw new InvalidOperationException(message);
    }

    private async Task DetectOasisPythonAsync()
    {
        SetOasisStatus("正在检测 Oasis/Geosoft Python 环境…", null);
        string preferred = _oasisPython.Text.Trim();
        string resolved = await Task.Run(() => _oasisGdb.ResolveGeosoftPython(preferred));
        _oasisPython.Text = resolved;
        SetOasisStatus($"Geosoft GX Python 可用：{resolved}", true);
        SetStatus("Oasis/Geosoft Python 环境检测通过。 ");
    }

    private async Task InspectOasisGdbsAsync()
    {
        bool wasEnabled = Enabled;
        Enabled = false;
        try { await InspectOasisGdbsCoreAsync(); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            InvalidateOasisInspection();
            ShowOasisFailure("GDB 表头读取", ex);
            throw;
        }
        finally { if (!IsDisposed) Enabled = wasEnabled; }
    }

    private async Task InspectOasisGdbsCoreAsync()
    {
        string a = _oasisGdbA.Text.Trim();
        string b = _oasisGdbB.Text.Trim();
        if (!File.Exists(a) || !File.Exists(b))
            throw new InvalidOperationException("请先选择基准 GDB A 和追加 GDB B。 ");
        if (Path.GetFullPath(a).Equals(Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("基准 GDB 与追加 GDB 不能是同一个文件。 ");

        InvalidateOasisInspection();
        SetOasisStatus("正在通过 Geosoft GX API 读取 GDB 表头…", null);
        _oasisInfo.Text = "正在读取当前 GDB 表头；本轮读取完成前不使用旧对应关系。";
        string python = await EnsureOasisPythonAsync();
        var inspections = await Task.Run(() =>
        {
            OasisGdbInspection first = _oasisGdb.Inspect(python, a);
            OasisGdbInspection second = _oasisGdb.Inspect(python, b);
            return (First: first, Second: second);
        });

        _oasisInspectionA = inspections.First;
        _oasisInspectionB = inspections.Second;
        _oasisMappingPlan = OasisGdbHeaderMapping.BuildPlan(_oasisInspectionA, _oasisInspectionB);
        _oasisManualMappings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        RefreshOasisHeaderGrid();
        UpdateOasisInspectionInfo();

        if (_oasisMappingPlan.NeedsUserMapping)
        {
            MessageBox.Show(
                this,
                "追加数据库中存在没有同名表头的列。\r\n\r\n" +
                "MagOps 不会猜测这些列的含义；请在下一窗口选择对应的基准表头。" +
                "i--- / i010 / i027 等占位列若已识别，会自动对应，不需要手动选择。",
                "发现不同名表头",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            if (!PromptForOasisMappings())
            {
                SetOasisStatus("表头已读取，但不同名表头尚未完成对应。", false);
                return;
            }
        }

        RefreshOasisHeaderGrid();
        UpdateOasisInspectionInfo();
        SetOasisStatus("表头对应已确认，可以追加合并 GDB。", true);
        SetStatus("Oasis GDB 表头对应完成。 ");
    }

    private async Task MergeOasisGdbsAsync()
    {
        bool wasEnabled = Enabled;
        Enabled = false;
        try { await MergeOasisGdbsCoreAsync(); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            ShowOasisFailure("GDB 合并", ex);
            throw;
        }
        finally { if (!IsDisposed) Enabled = wasEnabled; }
    }

    private async Task MergeOasisGdbsCoreAsync()
    {
        if (!OasisInspectionMatchesCurrentFiles())
            await InspectOasisGdbsAsync();

        if (_oasisMappingPlan is null || _oasisInspectionA is null || _oasisInspectionB is null)
            throw new InvalidOperationException("请先读取两个 GDB 的表头。 ");

        if (_oasisMappingPlan.NeedsUserMapping && !AllOasisManualChoicesRecorded())
        {
            if (!PromptForOasisMappings())
                throw new OperationCanceledException();
        }

        IReadOnlyList<OasisChannelMapping> mappings =
            OasisGdbHeaderMapping.ResolveMappings(_oasisMappingPlan, _oasisManualMappings);

        string outputName = Path.GetFileName(_oasisOutputName.Text.Trim());
        if (string.IsNullOrWhiteSpace(outputName))
            throw new InvalidOperationException("请输入输出 GDB 文件名。 ");
        if (!outputName.EndsWith(".gdb", StringComparison.OrdinalIgnoreCase))
            outputName += ".gdb";

        string outputPath = ResolveToolOutputFilePath(
            "processing_oasis",
            "Oasis GDB 表头对应合并输出",
            outputName,
            "Geosoft/Oasis 数据库 (*.gdb)|*.gdb|所有文件 (*.*)|*.*",
            ".gdb");

        string python = await EnsureOasisPythonAsync();
        SetOasisStatus("正在通过 Geosoft GX API 追加合并 GDB，请勿关闭程序…", null);
        _oasisInfo.Text = "正在追加合并当前 GDB，完成后在此显示本轮成果位置。";
        OasisGdbMergeExecutionResult result = await Task.Run(() =>
            _oasisGdb.Merge(
                python,
                _oasisInspectionA.Path,
                _oasisInspectionB.Path,
                outputPath,
                mappings));

        RegisterGeneratedBusinessFiles([result.OutputPath], "oasis_gdb_header_merge");
        RefreshOasisHeaderGrid();
        _oasisInfo.Text =
            $"合并完成：{result.OutputPath}\r\n" +
            $"输出大小：{result.OutputBytes / 1024d / 1024d:F2} MB\r\n" +
            $"基准表头数：{_oasisInspectionA.Channels.Count}\r\n" +
            $"追加表头数：{_oasisInspectionB.Channels.Count}\r\n" +
            $"实际对应表头数：{mappings.Count}\r\n\r\n" +
            BuildOasisMappingSummary(mappings) +
            "\r\n\r\n输入的两个原始 GDB 未被修改；输出是基准 A 的副本，再按以上表头对应追加 B 的数据。";
        SetOasisStatus("GDB 追加合并完成。", true);
        if (_oasisInfo.Parent is TabPage details && details.Parent is TabControl results) results.SelectedTab = details;
        SetStatus("Oasis GDB 表头对应合并完成。 ");
        TransientNotice.Show(this, $"Oasis GDB 已生成：{Path.GetFileName(result.OutputPath)}", "文件写入成功");
    }

    private async Task<string> EnsureOasisPythonAsync()
    {
        string current = _oasisPython.Text.Trim();
        if (!string.IsNullOrWhiteSpace(current) &&
            _oasisGdb.TryProbeGeosoftPython(current, out _))
            return current;

        string resolved = await Task.Run(() => _oasisGdb.ResolveGeosoftPython(current));
        _oasisPython.Text = resolved;
        return resolved;
    }

    private bool PromptForOasisMappings()
    {
        if (_oasisMappingPlan is null || !_oasisMappingPlan.NeedsUserMapping)
            return true;

        using var dialog = new OasisHeaderMappingDialog(this, _oasisMappingPlan, _oasisManualMappings);
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return false;

        _oasisManualMappings = dialog.Result.ToDictionary(
            x => x.Key,
            x => x.Value,
            StringComparer.OrdinalIgnoreCase);
        _ = OasisGdbHeaderMapping.ResolveMappings(_oasisMappingPlan, _oasisManualMappings);
        RefreshOasisHeaderGrid();
        UpdateOasisInspectionInfo();
        return true;
    }

    private void RefreshOasisHeaderGrid()
    {
        if (_oasisHeaderGrid.Parent is TabPage headers && headers.Parent is TabControl results) results.SelectedTab = headers;
        var rows = new List<OasisHeaderPreviewRow>();
        if (_oasisMappingPlan is not null)
        {
            foreach (OasisChannelMapping mapping in _oasisMappingPlan.AutomaticMappings)
                rows.Add(new OasisHeaderPreviewRow(mapping.DestinationChannel, mapping.SourceChannel, mapping.MatchMode));

            foreach (string source in _oasisMappingPlan.UnmatchedSourceChannels)
            {
                bool hasChoice = TryGetOasisManualMapping(source, out string? destination);
                rows.Add(new OasisHeaderPreviewRow(
                    hasChoice && !string.IsNullOrWhiteSpace(destination) ? destination! : "",
                    source,
                    hasChoice
                        ? (string.IsNullOrWhiteSpace(destination) ? "用户选择：忽略" : "用户选择")
                        : "需用户选择"));
            }

            var representedDestinations = new HashSet<string>(
                rows.Where(x => !string.IsNullOrWhiteSpace(x.基准A表头)).Select(x => x.基准A表头),
                StringComparer.OrdinalIgnoreCase);
            foreach (string destination in _oasisMappingPlan.UnmatchedDestinationChannels)
            {
                if (!representedDestinations.Contains(destination))
                    rows.Add(new OasisHeaderPreviewRow(destination, "", "A 中保留；B 无对应数据"));
            }
        }

        _oasisHeaderGrid.DataSource = null;
        _oasisHeaderGrid.DataSource = rows;
        UiLayout.ApplyFillWeights(
            _oasisHeaderGrid,
            new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
            {
                [nameof(OasisHeaderPreviewRow.基准A表头)] = 150,
                [nameof(OasisHeaderPreviewRow.追加B表头)] = 150,
                [nameof(OasisHeaderPreviewRow.对应方式)] = 120
            },
            minimumWidth: 90);
    }

    private void UpdateOasisInspectionInfo()
    {
        if (_oasisInspectionA is null || _oasisInspectionB is null || _oasisMappingPlan is null)
            return;

        int exact = _oasisMappingPlan.AutomaticMappings.Count(x => x.MatchMode == "同名自动");
        int placeholders = _oasisMappingPlan.AutomaticMappings.Count(x => x.MatchMode == "占位列自动");
        string placeholderA = _oasisInspectionA.PlaceholderChannels.Count == 0
            ? "未检测到"
            : string.Join("、", _oasisInspectionA.PlaceholderChannels);
        string placeholderB = _oasisInspectionB.PlaceholderChannels.Count == 0
            ? "未检测到"
            : string.Join("、", _oasisInspectionB.PlaceholderChannels);

        _oasisInfo.Text =
            $"基准 A：{_oasisInspectionA.Path}\r\n" +
            $"  表头：{_oasisInspectionA.Channels.Count}；Line/Group：{_oasisInspectionA.Lines.Count}；占位列：{placeholderA}\r\n" +
            $"追加 B：{_oasisInspectionB.Path}\r\n" +
            $"  表头：{_oasisInspectionB.Channels.Count}；Line/Group：{_oasisInspectionB.Lines.Count}；占位列：{placeholderB}\r\n\r\n" +
            $"同名自动对应：{exact}\r\n" +
            $"占位列跨名称自动对应：{placeholders}\r\n" +
            $"B 中需用户处理的不同名表头：{_oasisMappingPlan.UnmatchedSourceChannels.Count}\r\n" +
            $"A 中尚无 B 数据的表头：{_oasisMappingPlan.UnmatchedDestinationChannels.Count}\r\n\r\n" +
            (_oasisMappingPlan.NeedsUserMapping
                ? "不同名表头不会自动猜测；必须由用户选择对应目标或明确忽略。"
                : "所有追加表头已通过同名或特殊占位列规则自动对应。 ");
    }

    private bool OasisInspectionMatchesCurrentFiles()
    {
        if (_oasisInspectionA is null || _oasisInspectionB is null)
            return false;
        string a = _oasisGdbA.Text.Trim();
        string b = _oasisGdbB.Text.Trim();
        return File.Exists(a) && File.Exists(b) &&
               Path.GetFullPath(a).Equals(_oasisInspectionA.Path, StringComparison.OrdinalIgnoreCase) &&
               Path.GetFullPath(b).Equals(_oasisInspectionB.Path, StringComparison.OrdinalIgnoreCase);
    }

    private bool AllOasisManualChoicesRecorded()
    {
        if (_oasisMappingPlan is null)
            return false;
        return _oasisMappingPlan.UnmatchedSourceChannels.All(source =>
            _oasisManualMappings.Keys.Any(key => key.Equals(source, StringComparison.OrdinalIgnoreCase)));
    }

    private bool TryGetOasisManualMapping(string source, out string? destination)
    {
        if (_oasisManualMappings.TryGetValue(source, out destination))
            return true;
        foreach (var pair in _oasisManualMappings)
        {
            if (!pair.Key.Equals(source, StringComparison.OrdinalIgnoreCase))
                continue;
            destination = pair.Value;
            return true;
        }
        destination = null;
        return false;
    }

    private void InvalidateOasisInspection()
    {
        _oasisInspectionA = null;
        _oasisInspectionB = null;
        _oasisMappingPlan = null;
        _oasisManualMappings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        _oasisHeaderGrid.DataSource = null;
        _oasisInfo.Text = "输入路径已调整，原表头对应和处理结果不再代表当前输入。请重新读取 / 对应表头。";
        SetOasisStatus("请重新读取 / 对应表头。", null);
    }

    private void ShowOasisFailure(string operation, Exception error)
    {
        SetOasisStatus(operation + "未完成，详情见处理说明与结果。", false);
        _oasisInfo.Text = operation + "未完成：" + error.Message + Environment.NewLine + "请修正输入文件、Python 环境或输出设置后重试。";
        if (_oasisInfo.Parent is TabPage details && details.Parent is TabControl tabs) tabs.SelectedTab = details;
    }

    private void SetOasisStatus(string text, bool? ok)
    {
        _oasisStatus.Text = text;
        _oasisStatus.ForeColor = ok switch
        {
            true => Color.DarkGreen,
            false => Color.DarkRed,
            _ => SystemColors.GrayText
        };
    }

    private static string BuildOasisMappingSummary(IReadOnlyList<OasisChannelMapping> mappings)
    {
        if (mappings.Count == 0)
            return "没有可追加的对应表头。";
        return "表头对应：\r\n" + string.Join(
            "\r\n",
            mappings.Select(x => $"  B:{x.SourceChannel}  →  A:{x.DestinationChannel}  [{x.MatchMode}]"));
    }

    private sealed record OasisHeaderPreviewRow(
        string 基准A表头,
        string 追加B表头,
        string 对应方式);
}
