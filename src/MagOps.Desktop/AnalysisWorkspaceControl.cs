namespace MagOps.Desktop;

internal sealed record AnalysisWorkspaceActions(
    Action<string?> RunGroupAverage,
    Action<string?> RunTableSplit,
    Action<IReadOnlyList<string>?> RunCsvMerge,
    Action<string?> RunDynamicNoise,
    Action<string?> RunElevationKml);

internal sealed class AnalysisWorkspaceControl : UserControl
{
    private readonly AnalysisWorkspaceActions _actions;

    public AnalysisWorkspaceControl(
        AnalysisWorkspaceActions actions)
    {
        _actions = actions;

        Dock = DockStyle.Fill;
        Margin = Padding.Empty;
        Padding = Padding.Empty;
        BackColor = UiTheme.Workspace;

        Build();
    }

    private readonly DataGridView _resultGrid =
        GridFactory.Create();

    private readonly TextBox _resultInfo =
        new();

    public void ShowResult(
        object? dataSource,
        string info)
    {
        _resultGrid.DataSource =
            null;
        _resultGrid.DataSource =
            dataSource;
        GridFactory.SetEmptyText(_resultGrid, "本次操作没有结构化结果行；请查看下方说明与成果路径。");
        _resultInfo.Text =
            info;
    }

    internal void BeginOperation(string operation)
    {
        _resultGrid.DataSource = null;
        _resultGrid.Rows.Clear();
        GridFactory.SetEmptyText(_resultGrid, $"正在执行{operation}，请稍候。");
        _resultInfo.Text = $"正在执行：{operation}\r\n上次结果已清除。";
        Refresh();
    }

    internal void ShowFailure(string operation, string message)
    {
        _resultGrid.DataSource = null;
        _resultGrid.Rows.Clear();
        GridFactory.SetEmptyText(_resultGrid, "本次操作未完成；请查看下方原因并重试。");
        _resultInfo.Text = $"{operation}未完成。\r\n{message}";
    }

    private void Build()
    {
        var shell = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = UiTheme.PagePadding,
            Margin = Padding.Empty,
            BackColor = UiTheme.Workspace
        };
        shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 72));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 28));

        shell.Controls.Add(
            UiComponents.PageHeader(
                "分析工具（实验）",
                "集中使用数据分析与试验工具，选择输入后查看分析结果。"),
            0,
            0);

        var actionCard = UiComponents.Card(new Padding(12, 8, 12, 8));
        actionCard.AutoSize = true;
        actionCard.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        var actionLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        actionLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        actionLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        actionLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        actionLayout.Controls.Add(
            UiComponents.HelpLabel(
                "Sand 中旧 KML/KMZ 航线逻辑已停用。本页只提供已验证的表格、动态噪声与高程转换工具。"),
            0,
            0);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = true,
            AutoScroll = false,
            Padding = new Padding(0, 6, 0, 0),
            Margin = Padding.Empty
        };

        bool AcceptTable(string path) =>
            FileDropSupport.HasExtension(
                path,
                ".csv",
                ".txt",
                ".tsv");

        Button groupAverage = CreateActionButton(
            "分组平均",
            () => _actions.RunGroupAverage(null));
        FileDropSupport.EnableSingle(
            groupAverage,
            path => _actions.RunGroupAverage(path),
            AcceptTable);
        buttons.Controls.Add(groupAverage);

        Button splitTable = CreateActionButton(
            "拆分表格",
            () => _actions.RunTableSplit(null));
        FileDropSupport.EnableSingle(
            splitTable,
            path => _actions.RunTableSplit(path),
            AcceptTable);
        buttons.Controls.Add(splitTable);

        Button mergeCsv = CreateActionButton(
            "合并 CSV / 线号重排",
            () => _actions.RunCsvMerge(null));
        FileDropSupport.EnableMultiple(
            mergeCsv,
            paths => _actions.RunCsvMerge(paths),
            AcceptTable,
            minimumFileCount: 2);
        buttons.Controls.Add(mergeCsv);

        Button dynamicNoise = CreateActionButton(
            "动态噪声",
            () => _actions.RunDynamicNoise(null));
        FileDropSupport.EnableSingle(
            dynamicNoise,
            path => _actions.RunDynamicNoise(path),
            AcceptTable);
        buttons.Controls.Add(dynamicNoise);

        Button elevationKml = CreateActionButton(
            "高程 CSV → KML",
            () => _actions.RunElevationKml(null));
        FileDropSupport.EnableSingle(
            elevationKml,
            path => _actions.RunElevationKml(path),
            AcceptTable);
        buttons.Controls.Add(elevationKml);

        actionLayout.Controls.Add(buttons, 0, 1);
        actionCard.Controls.Add(actionLayout);
        shell.Controls.Add(actionCard, 0, 1);

        GridFactory.SetEmptyText(
            _resultGrid,
            "运行分析工具后，这里显示结构化结果。");
        shell.Controls.Add(_resultGrid, 0, 2);

        _resultInfo.Multiline = true;
        _resultInfo.ReadOnly = true;
        _resultInfo.ScrollBars = ScrollBars.Both;
        _resultInfo.WordWrap = false;
        _resultInfo.Dock = DockStyle.Fill;
        _resultGrid.AccessibleName = "分析工具结构化结果";
        _resultInfo.AccessibleName = "分析工具运行状态与结果说明";
        _resultInfo.Text =
            "等待执行分析工具。";
        shell.Controls.Add(_resultInfo, 0, 3);

        Controls.Add(shell);
    }

    internal T ExecuteAnalysis<T>(string operation, Func<T> execute)
    {
        BeginOperation(operation);
        try { return execute(); }
        catch (Exception ex)
        {
            ShowFailure(operation, ex.Message);
            throw;
        }
    }

    private static Button CreateActionButton(
        string text,
        Action action)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            MinimumSize = new Size(108, 36),
            Margin = new Padding(4, 2, 4, 2)
        };
        UiComponents.SetButtonRole(
            button,
            UiButtonRole.Neutral);
        button.Click +=
            (_, _) => action();
        return button;
    }
}
