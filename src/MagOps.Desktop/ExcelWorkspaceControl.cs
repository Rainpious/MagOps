namespace MagOps.Desktop;

internal enum ExcelMergeSourceSlot
{
    First = 0,
    Second = 1
}

internal sealed record ExcelHeaderPreviewRow(
    int? 表A列,
    string 表A表头,
    int? 表B列,
    string 表B表头,
    string 状态);

internal sealed class ExcelWorkspaceControl : UserControl
{
    private readonly Action<ExcelMergeSourceSlot, string> _sourceSelected;
    private readonly Action _checkHeaders;
    private readonly Func<Task> _merge;

    private readonly TextBox _firstPath = new();
    private readonly TextBox _secondPath = new();
    private readonly TextBox _outputName = new();
    private readonly Label _status = new();
    private readonly DataGridView _headerGrid = GridFactory.Create();
    private readonly TextBox _info = new();

    public ExcelWorkspaceControl(
        Action<ExcelMergeSourceSlot, string> sourceSelected,
        Action checkHeaders,
        Func<Task> merge)
    {
        _sourceSelected = sourceSelected;
        _checkHeaders = checkHeaders;
        _merge = merge;

        Dock = DockStyle.Fill;
        Margin = Padding.Empty;
        Padding = Padding.Empty;
        BackColor = UiTheme.Workspace;

        Build();
        _firstPath.TextChanged += (_, _) => InvalidateInputs();
        _secondPath.TextChanged += (_, _) => InvalidateInputs();
        _outputName.TextChanged += (_, _) =>
        {
            SetStatus("输出文件名已调整，尚未生成当前设置的成果。", compatible: null);
            ShowInfo("输出文件名已调整。请检查当前输入和输出文件名后重新合并。");
        };
        _firstPath.AccessibleName = "表 A 来源路径";
        _secondPath.AccessibleName = "表 B 来源路径";
        _outputName.AccessibleName = "合并成果文件名";
        _headerGrid.AccessibleName = "两表表头对应";
        _info.AccessibleName = "表格合并说明与结果";
    }

    internal string GetFirstPath() =>
        _firstPath.Text.Trim();

    private void InvalidateInputs()
    {
        _headerGrid.DataSource = null;
        SetStatus("输入路径已调整，请重新检查表头。", compatible: null);
        ShowInfo("输入路径已调整，原表头对应和处理结果不再代表当前输入。请重新检查表头后合并。");
    }

    internal void ShowFailure(string operation, Exception error, bool clearHeaders)
    {
        if (clearHeaders) _headerGrid.DataSource = null;
        SetStatus(operation + "未完成，详情见处理说明与结果。", compatible: false);
        ShowInfo(operation + "未完成：" + error.Message + Environment.NewLine + "请修正输入或输出设置后重试。", activate: true);
    }

    internal string GetSecondPath() =>
        _secondPath.Text.Trim();

    internal string GetOutputName() =>
        _outputName.Text.Trim();

    internal void SetOutputName(
        string value)
    {
        _outputName.Text =
            value;
    }

    internal static bool AcceptSource(string path) =>
        FileDropSupport.HasExtension(
            path,
            ".csv",
            ".txt",
            ".tsv");

    public void SetSource(
        ExcelMergeSourceSlot slot,
        string path)
    {
        TextBox target =
            slot == ExcelMergeSourceSlot.First
                ? _firstPath
                : _secondPath;
        target.Text =
            path;
    }

    public void SetStatus(
        string text,
        bool? compatible)
    {
        _status.Text =
            text;
        _status.ForeColor =
            compatible switch
            {
                true => Color.DarkGreen,
                false => Color.DarkRed,
                _ => UiTheme.TextSecondary
            };
    }

    public void ShowHeaderRows(
        IReadOnlyList<ExcelHeaderPreviewRow> rows)
    {
        _headerGrid.DataSource =
            null;
        _headerGrid.DataSource =
            rows;
        if (_headerGrid.Parent is TabPage page && page.Parent is TabControl tabs) tabs.SelectedTab = page;

        UiLayout.ApplyFillWeights(
            _headerGrid,
            new Dictionary<string, float>(
                StringComparer.OrdinalIgnoreCase)
            {
                [nameof(ExcelHeaderPreviewRow.表A列)] = 40,
                [nameof(ExcelHeaderPreviewRow.表A表头)] = 140,
                [nameof(ExcelHeaderPreviewRow.表B列)] = 40,
                [nameof(ExcelHeaderPreviewRow.表B表头)] = 140,
                [nameof(ExcelHeaderPreviewRow.状态)] = 90
            },
            minimumWidth: 60);
    }

    public void ShowInfo(
        string text, bool activate = false)
    {
        _info.Text =
            text;
        if (activate && _info.Parent is TabPage page && page.Parent is TabControl tabs) tabs.SelectedTab = page;
    }

    private void Build()
    {
        var shell = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = UiTheme.PagePadding,
            Margin = Padding.Empty,
            BackColor = UiTheme.Workspace
        };
        shell.RowStyles.Add(
            new RowStyle(
                SizeType.AutoSize));
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(
            new RowStyle(
                SizeType.AutoSize));
        shell.RowStyles.Add(
            new RowStyle(
                SizeType.Percent,
                100));

        shell.Controls.Add(
            UiComponents.PageHeader(
                "Excel处理",
                "合并相同表头的 CSV/TXT/TSV 表格，检查输入后导出合并成果。"),
            0,
            0);

        Panel mergeCard =
            UiComponents.Card(
                new Padding(
                    12,
                    10,
                    12,
                    10));
        mergeCard.AutoSize = true;
        mergeCard.AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var form = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 4,
            RowCount = 5,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        form.ColumnStyles.Add(
            new ColumnStyle(
                SizeType.AutoSize));
        form.ColumnStyles.Add(
            new ColumnStyle(
                SizeType.Percent,
                100));
        form.ColumnStyles.Add(
            new ColumnStyle(
                SizeType.AutoSize));
        form.ColumnStyles.Add(
            new ColumnStyle(
                SizeType.AutoSize));
        for (int i = 0; i < form.RowCount; i++)
        {
            form.RowStyles.Add(
                new RowStyle(
                    SizeType.AutoSize));
        }

        form.Controls.Add(
            UiComponents.SectionLabel(
                "1",
                "合并相同表头的两个表格"),
            0,
            0);
        form.SetColumnSpan(
            form.GetControlFromPosition(
                0,
                0)!,
            4);

        ConfigurePathBox(
            _firstPath,
            "选择或拖入第一个 CSV/TXT/TSV 表格");
        ConfigurePathBox(
            _secondPath,
            "选择或拖入第二个 CSV/TXT/TSV 表格");

        Button chooseA =
            CreateButton(
                "选择 A",
                () =>
                    ChooseSource(
                        ExcelMergeSourceSlot.First));
        Button chooseB =
            CreateButton(
                "选择 B",
                () =>
                    ChooseSource(
                        ExcelMergeSourceSlot.Second));

        EnableSourceDrop(
            chooseA,
            ExcelMergeSourceSlot.First);
        EnableSourceDrop(
            chooseB,
            ExcelMergeSourceSlot.Second);
        EnableSourceDrop(
            _firstPath,
            ExcelMergeSourceSlot.First);
        EnableSourceDrop(
            _secondPath,
            ExcelMergeSourceSlot.Second);

        form.Controls.Add(
            FieldLabel("表 A"),
            0,
            1);
        form.Controls.Add(
            _firstPath,
            1,
            1);
        form.Controls.Add(
            chooseA,
            2,
            1);
        form.SetColumnSpan(
            chooseA,
            2);

        form.Controls.Add(
            FieldLabel("表 B"),
            0,
            2);
        form.Controls.Add(
            _secondPath,
            1,
            2);
        form.Controls.Add(
            chooseB,
            2,
            2);
        form.SetColumnSpan(
            chooseB,
            2);

        SetOutputName(
            $"合并结果_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
        _outputName.Dock =
            DockStyle.Fill;
        _outputName.Margin =
            new Padding(
                0,
                3,
                6,
                3);

        form.Controls.Add(
            FieldLabel("输出文件名"),
            0,
            3);
        form.Controls.Add(
            _outputName,
            1,
            3);

        Button check =
            CreateButton(
                "检查表头",
                _checkHeaders);
        Button merge =
            CreateAsyncButton(
                "合并数据",
                _merge);
        UiComponents.SetButtonRole(
            merge,
            UiButtonRole.Primary);
        form.Controls.Add(
            check,
            2,
            3);
        form.Controls.Add(
            merge,
            3,
            3);

        SetStatus(
            "请选择两个表格。",
            compatible: null);
        _status.AutoSize =
            true;
        _status.Padding =
            new Padding(
                3,
                5,
                3,
                3);
        form.Controls.Add(
            FieldLabel("表头状态"),
            0,
            4);
        form.Controls.Add(
            _status,
            1,
            4);
        form.SetColumnSpan(
            _status,
            3);

        const string rules = "规则：两个文件必须具有相同表头字段；列顺序不同时会自动按表 A 重排表 B。输出只保留一行表头，数据按 A → B 追加。当前支持 CSV/TXT/TSV；XLSX/XLSM 暂不直接读写。";

        mergeCard.Controls.Add(
            form);
        shell.Controls.Add(
            mergeCard,
            0,
            1);

        GridFactory.SetEmptyText(
            _headerGrid,
            "选择两个表格后，这里显示表头对应关系。");
        var results = new TabControl { Dock = DockStyle.Fill };
        var headers = new TabPage("表头对应");
        headers.Controls.Add(_headerGrid);
        results.TabPages.Add(headers);
        shell.Controls.Add(results, 0, 2);

        _info.Multiline =
            true;
        _info.ReadOnly =
            true;
        _info.ScrollBars =
            ScrollBars.Both;
        _info.WordWrap =
            false;
        _info.Dock =
            DockStyle.Fill;
        _info.Text =
            "用途：把两个同结构数据表纵向合并。\r\n" +
            "为避免误拼接，表头字段不一致时会阻止输出，并列出双方独有字段。\r\n" + rules;
        var details = new TabPage("处理说明与结果");
        details.Controls.Add(_info);
        results.TabPages.Add(details);

        Controls.Add(
            shell);
    }

    private void ChooseSource(
        ExcelMergeSourceSlot slot)
    {
        using var dialog =
            new OpenFileDialog
            {
                Filter =
                    "表格文件 (*.csv;*.txt;*.tsv)|*.csv;*.txt;*.tsv|所有文件 (*.*)|*.*",
                Title =
                    slot == ExcelMergeSourceSlot.First
                        ? "选择表 A"
                        : "选择表 B"
            };
        if (dialog.ShowDialog(this) !=
            DialogResult.OK)
        {
            throw new OperationCanceledException();
        }

        _sourceSelected(
            slot,
            dialog.FileName);
    }

    private void EnableSourceDrop(
        Control control,
        ExcelMergeSourceSlot slot)
    {
        FileDropSupport.EnableSingle(
            control,
            path =>
                _sourceSelected(
                    slot,
                    path),
            AcceptSource);
    }

    private static void ConfigurePathBox(
        TextBox box,
        string placeholder)
    {
        box.Dock =
            DockStyle.Fill;
        box.Margin =
            new Padding(
                0,
                3,
                6,
                3);
        box.PlaceholderText =
            placeholder;
    }

    private static Label FieldLabel(
        string text) =>
        new()
        {
            Text =
                text,
            AutoSize =
                true,
            Anchor =
                AnchorStyles.Left,
            Margin =
                new Padding(
                    3,
                    8,
                    10,
                    3)
        };

    private static Button CreateButton(
        string text,
        Action action)
    {
        var button =
            new Button
            {
                Text =
                    text,
                AutoSize =
                    true,
                MinimumSize =
                    new Size(
                        94,
                        34),
                Margin =
                    new Padding(
                        4,
                        2,
                        4,
                        2)
            };
        UiComponents.SetButtonRole(
            button,
            UiButtonRole.Neutral);
        button.Click +=
            (_, _) =>
                action();
        return button;
    }

    private static Button CreateAsyncButton(
        string text,
        Func<Task> action)
    {
        var button =
            new Button
            {
                Text =
                    text,
                AutoSize =
                    true,
                MinimumSize =
                    new Size(
                        94,
                        34),
                Margin =
                    new Padding(
                        4,
                        2,
                        4,
                        2)
            };
        UiComponents.SetButtonRole(
            button,
            UiButtonRole.Neutral);
        button.Click +=
            async (_, _) =>
                await action();
        return button;
    }
}
