namespace MagOps.Desktop;

/// <summary>
/// Manages only the additional DMS separator characters configured by the user.
/// CoordinateService already recognizes the standard DMS units plus common row/
/// field delimiters, so they are shown as read-only reference instead of being
/// duplicated in the editable value.
/// </summary>
internal sealed class CoordinateSeparatorDialog : Form
{
    private readonly ListBox _configured = new();
    private readonly TextBox _newSeparator = new();
    private readonly Button _removeButton = new();
    private readonly Button _clearButton = new();
    private readonly List<char> _values = [];

    public string Separators { get; private set; } = "";

    public CoordinateSeparatorDialog(string currentSeparators)
    {
        Text = "DMS 分隔符管理";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(620, 500);
        MinimumSize = new Size(540, 420);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.Sizable;

        LoadCurrent(currentSeparators);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(14),
            Margin = Padding.Empty,
            BackColor = UiTheme.Workspace
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        root.Controls.Add(UiComponents.PageHeader(
            "DMS 分隔符管理",
            "系统内置已识别 Tab、逗号、分号、中文逗号/分号、竖线、斜杠，以及 ° ′ ″ 度分秒等单位符号。这里仅维护额外的自定义分隔符。"), 0, 0);

        var listCard = UiComponents.Card(new Padding(10));
        var listLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        listLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        listLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        listLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        listLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        listLayout.Controls.Add(new Label
        {
            Text = "当前自定义分隔符",
            AutoSize = true,
            Font = new Font(SystemFonts.MessageBoxFont?.FontFamily ?? FontFamily.GenericSansSerif, 10F, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 8)
        }, 0, 0);

        _configured.Dock = DockStyle.Fill;
        _configured.IntegralHeight = false;
        _configured.SelectedIndexChanged += (_, _) => UpdateButtons();
        listLayout.Controls.Add(_configured, 0, 1);

        var listActions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Margin = new Padding(8, 25, 0, 0),
            Padding = Padding.Empty
        };
        _removeButton.Text = "删除选中";
        _removeButton.AutoSize = true;
        _removeButton.MinimumSize = new Size(96, 34);
        _removeButton.Click += (_, _) => RemoveSelected();
        UiComponents.SetButtonRole(_removeButton, UiButtonRole.Subtle);
        _clearButton.Text = "清空自定义";
        _clearButton.AutoSize = true;
        _clearButton.MinimumSize = new Size(96, 34);
        _clearButton.Click += (_, _) => ClearConfigured();
        UiComponents.SetButtonRole(_clearButton, UiButtonRole.Subtle);
        listActions.Controls.Add(_removeButton);
        listActions.Controls.Add(_clearButton);
        listLayout.Controls.Add(listActions, 1, 1);
        listCard.Controls.Add(listLayout);
        root.Controls.Add(listCard, 0, 1);

        var addRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 3,
            Margin = new Padding(0, 10, 0, 0),
            Padding = Padding.Empty
        };
        addRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        addRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        addRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        addRow.Controls.Add(new Label
        {
            Text = "新增分隔符",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 8, 8, 0)
        }, 0, 0);
        _newSeparator.Dock = DockStyle.Top;
        _newSeparator.PlaceholderText = "输入一个或多个符号，例如 -  或  #";
        addRow.Controls.Add(_newSeparator, 1, 0);
        addRow.Controls.Add(UiComponents.Button(
            "添加",
            (_, _) => AddSeparators(),
            UiButtonRole.Neutral,
            minimumWidth: 76), 2, 0);
        root.Controls.Add(addRow, 0, 2);

        root.Controls.Add(UiComponents.HelpLabel(
            "建议只添加非字母、非数字符号。Tab 可直接由系统内置识别，无需额外添加。"), 0, 3);

        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = true,
            Margin = new Padding(0, 12, 0, 0),
            Padding = Padding.Empty
        };
        var ok = UiComponents.Button(
            "应用",
            (_, _) => ApplyAndClose(),
            UiButtonRole.Primary,
            minimumWidth: 92);
        var cancel = UiComponents.Button(
            "取消",
            (_, _) =>
            {
                DialogResult = DialogResult.Cancel;
                Close();
            },
            UiButtonRole.Subtle,
            minimumWidth: 92);
        footer.Controls.Add(ok);
        footer.Controls.Add(cancel);
        root.Controls.Add(footer, 0, 4);

        Controls.Add(root);
        AcceptButton = ok;
        CancelButton = cancel;
        RefreshList();
        UiTheme.Apply(this);
        UiLayout.FitFormToWorkingArea(this);
    }

    private void LoadCurrent(string currentSeparators)
    {
        string normalized = (currentSeparators ?? "")
            .Replace("\\t", "\t", StringComparison.Ordinal);
        foreach (char value in normalized)
        {
            if (!_values.Contains(value))
                _values.Add(value);
        }
    }

    private void AddSeparators()
    {
        string raw = _newSeparator.Text
            .Replace("\\t", "\t", StringComparison.Ordinal);
        if (string.IsNullOrEmpty(raw))
            return;

        foreach (char value in raw)
        {
            if (char.IsLetterOrDigit(value))
            {
                MessageBox.Show(
                    this,
                    "字母和数字容易与坐标值、N/E/S/W 半球标记冲突，请改用符号类分隔符。",
                    "分隔符不适用",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }
        }

        foreach (char value in raw)
        {
            if (!_values.Contains(value))
                _values.Add(value);
        }
        _newSeparator.Clear();
        RefreshList();
    }

    private void RemoveSelected()
    {
        int index = _configured.SelectedIndex;
        if (index < 0 || index >= _values.Count)
            return;
        _values.RemoveAt(index);
        RefreshList();
        if (_values.Count > 0)
            _configured.SelectedIndex = Math.Min(index, _values.Count - 1);
    }

    private void ClearConfigured()
    {
        _values.Clear();
        RefreshList();
    }

    private void RefreshList()
    {
        _configured.BeginUpdate();
        try
        {
            _configured.Items.Clear();
            foreach (char value in _values)
                _configured.Items.Add(DisplaySeparator(value));
        }
        finally
        {
            _configured.EndUpdate();
        }
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        _removeButton.Enabled = _configured.SelectedIndex >= 0;
        _clearButton.Enabled = _values.Count > 0;
        UiTheme.ApplyButton(_removeButton, UiComponents.GetButtonRole(_removeButton));
        UiTheme.ApplyButton(_clearButton, UiComponents.GetButtonRole(_clearButton));
    }

    private void ApplyAndClose()
    {
        Separators = string.Concat(_values.Select(value => value == '\t' ? "\\t" : value.ToString()));
        DialogResult = DialogResult.OK;
        Close();
    }

    internal static string DisplaySeparator(char value) => value switch
    {
        '\t' => "Tab（\\t）",
        ' ' => "空格",
        _ => $"{value}    U+{(int)value:X4}"
    };
}
