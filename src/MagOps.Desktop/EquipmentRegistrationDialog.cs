using MagOps.Infrastructure.Services;

namespace MagOps.Desktop;

internal sealed record EquipmentRegistrationValues(
    string Category,
    string Model,
    string EquipmentId,
    string EquipmentType,
    string SerialNo,
    string Notes);

internal sealed class EquipmentRegistrationDialog : Form
{
    private readonly EquipmentCatalog _catalog;
    private readonly ComboBox _category = new();
    private readonly ComboBox _model = new();
    private readonly TextBox _equipmentId = new();
    private readonly TextBox _equipmentType = new();
    private readonly TextBox _serialNo = new();
    private readonly TextBox _notes = new();

    internal EquipmentRegistrationValues? Result { get; private set; }

    public EquipmentRegistrationDialog(EquipmentCatalog catalog)
    {
        _catalog = catalog;

        Text = "新增设备";
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        MinimumSize = new Size(620, 430);
        Size = new Size(690, 500);

        var shell = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(14),
            Margin = Padding.Empty
        };
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var fields = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 6,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 118));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _category.DropDownStyle = ComboBoxStyle.DropDownList;
        _category.Dock = DockStyle.Fill;
        _category.Items.AddRange(_catalog.Categories.Cast<object>().ToArray());
        if (_category.Items.Count > 0)
            _category.SelectedIndex = 0;
        _category.SelectedIndexChanged += (_, _) => RefreshModels();

        // The model is always a ComboBox. Categories with maintained models
        // use list-only selection; categories whose catalog is still empty
        // remain editable so field work is not blocked.
        _model.DropDownStyle = ComboBoxStyle.DropDown;
        _model.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
        _model.AutoCompleteSource = AutoCompleteSource.ListItems;
        _model.Dock = DockStyle.Fill;

        _equipmentId.Dock = DockStyle.Fill;
        _equipmentType.Dock = DockStyle.Fill;
        _serialNo.Dock = DockStyle.Fill;
        _notes.Dock = DockStyle.Fill;
        _notes.Multiline = true;
        _notes.ScrollBars = ScrollBars.Vertical;
        _notes.MinimumSize = new Size(0, 110);

        AddField(fields, 0, "设备类别", _category);
        AddField(fields, 1, "设备型号", _model);
        AddField(fields, 2, "设备编号", _equipmentId);
        AddField(fields, 3, "设备类型", _equipmentType);
        AddField(fields, 4, "序列号", _serialNo);
        AddField(fields, 5, "备注", _notes);

        shell.Controls.Add(fields, 0, 0);

        var bottom = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0, 10, 0, 0)
        };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        bottom.Controls.Add(new Label
        {
            Text = "设备型号使用下拉目录；已有型号目录的类别直接选择，尚未维护型号的类别允许现场录入。",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            MaximumSize = new Size(440, 0),
            Margin = new Padding(0, 7, 10, 0)
        }, 0, 0);

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = true,
            Margin = Padding.Empty
        };

        var ok = new Button
        {
            Text = "确定",
            AutoSize = true,
            MinimumSize = new Size(88, 36),
            Margin = new Padding(4)
        };
        var cancel = new Button
        {
            Text = "取消",
            AutoSize = true,
            MinimumSize = new Size(88, 36),
            DialogResult = DialogResult.Cancel,
            Margin = new Padding(4)
        };

        ok.Click += (_, _) => Accept();
        cancel.Click += (_, _) => Close();

        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);
        bottom.Controls.Add(buttons, 1, 0);
        shell.Controls.Add(bottom, 0, 1);

        Controls.Add(shell);
        AcceptButton = ok;
        CancelButton = cancel;

        Shown += (_, _) =>
        {
            RefreshModels();
            UiLayout.Normalize(this);
            _model.Focus();
        };
        DpiChanged += (_, _) =>
            BeginInvoke((Action)(() => UiLayout.Normalize(this)));
    }

    private static void AddField(
        TableLayoutPanel grid,
        int row,
        string label,
        Control control)
    {
        grid.RowStyles.Add(new RowStyle(
            row == 5 ? SizeType.Percent : SizeType.AutoSize,
            row == 5 ? 100 : 0));

        grid.Controls.Add(new Label
        {
            Text = label,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(3, 9, 10, 7)
        }, 0, row);

        control.Margin = new Padding(3, 5, 3, 5);
        grid.Controls.Add(control, 1, row);
    }

    private void RefreshModels()
    {
        string category =
            EquipmentCatalogService.NormalizeCategory(
                _category.SelectedItem?.ToString());

        string previous = _model.Text.Trim();
        _model.BeginUpdate();
        try
        {
            _model.Items.Clear();
            if (_catalog.ModelsByCategory.TryGetValue(
                    category,
                    out List<string>? models))
            {
                _model.Items.AddRange(
                    models
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Cast<object>()
                        .ToArray());
            }

            _model.DropDownStyle = _model.Items.Count > 0
                ? ComboBoxStyle.DropDownList
                : ComboBoxStyle.DropDown;

            int previousIndex = -1;
            if (previous.Length > 0)
            {
                for (int i = 0; i < _model.Items.Count; i++)
                {
                    if (string.Equals(
                            _model.Items[i]?.ToString(),
                            previous,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        previousIndex = i;
                        break;
                    }
                }
            }

            if (previousIndex >= 0)
            {
                _model.SelectedIndex = previousIndex;
            }
            else if (_model.Items.Count > 0)
            {
                _model.SelectedIndex = 0;
            }
            else
            {
                _model.Text = "";
            }
        }
        finally
        {
            _model.EndUpdate();
        }
    }

    private void Accept()
    {
        string category =
            EquipmentCatalogService.NormalizeCategory(
                _category.SelectedItem?.ToString());
        string model = _model.Text.Trim();

        if (category.Length == 0)
        {
            MessageBox.Show(
                this,
                "请选择设备类别。",
                "新增设备",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        if (model.Length == 0)
        {
            MessageBox.Show(
                this,
                "请选择或输入设备型号。",
                "新增设备",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            _model.Focus();
            return;
        }

        Result = new EquipmentRegistrationValues(
            category,
            model,
            _equipmentId.Text.Trim(),
            _equipmentType.Text.Trim(),
            _serialNo.Text.Trim(),
            _notes.Text.Trim());

        DialogResult = DialogResult.OK;
        Close();
    }
}
