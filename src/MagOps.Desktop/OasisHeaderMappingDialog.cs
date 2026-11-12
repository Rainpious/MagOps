using MagOps.Core.Analysis;

namespace MagOps.Desktop;

internal sealed class OasisHeaderMappingDialog : Form
{
    private const string IgnoreChoice = "<忽略，不合并此列>";
    private readonly DataGridView _grid = GridFactory.Create();
    private readonly OasisGdbMappingPlan _plan;

    internal IReadOnlyDictionary<string, string?> Result { get; private set; } =
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

    public OasisHeaderMappingDialog(
        IWin32Window owner,
        OasisGdbMappingPlan plan,
        IReadOnlyDictionary<string, string?>? previous = null)
    {
        _plan = plan;
        Text = "Oasis GDB 表头对应";
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        Width = 860;
        Height = 600;
        MinimumSize = new Size(700, 480);

        var shell = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(10),
            Margin = Padding.Empty
        };
        shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        shell.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(810, 0),
            Text =
                "检测到追加数据库中存在没有同名表头的列。请为每一列选择它在基准数据库中的对应表头，" +
                "或明确选择忽略。值为 i--- / i010 / i027 等形式的 Oasis 占位列会自动对应，不在此列表重复询问。",
            Padding = new Padding(2, 2, 2, 10)
        }, 0, 0);

        ConfigureGrid(previous);
        shell.Controls.Add(_grid, 0, 1);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = true,
            Padding = new Padding(0, 8, 0, 0),
            Margin = Padding.Empty
        };
        var ok = new Button { Text = "确定对应", AutoSize = true, Padding = new Padding(14, 4, 14, 4) };
        var cancel = new Button { Text = "取消", AutoSize = true, Padding = new Padding(14, 4, 14, 4) };
        ok.Click += (_, _) => AcceptMappings();
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);
        shell.Controls.Add(buttons, 0, 2);

        AcceptButton = ok;
        CancelButton = cancel;
        Controls.Add(shell);

        UiLayout.SizeDialogToOwner(this, owner, 0.68, 0.70, new Size(760, 500), new Size(1100, 820));
    }

    private void ConfigureGrid(IReadOnlyDictionary<string, string?>? previous)
    {
        _grid.AutoGenerateColumns = false;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.RowHeadersVisible = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;

        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Source",
            HeaderText = "追加数据库表头",
            ReadOnly = true,
            FillWeight = 46,
            MinimumWidth = 180
        });

        var targetColumn = new DataGridViewComboBoxColumn
        {
            Name = "Destination",
            HeaderText = "对应的基准数据库表头",
            FlatStyle = FlatStyle.Flat,
            FillWeight = 54,
            MinimumWidth = 220,
            DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton
        };
        targetColumn.Items.Add("");
        foreach (string destination in _plan.UnmatchedDestinationChannels)
            targetColumn.Items.Add(destination);
        targetColumn.Items.Add(IgnoreChoice);
        _grid.Columns.Add(targetColumn);

        foreach (string source in _plan.UnmatchedSourceChannels)
        {
            int rowIndex = _grid.Rows.Add(source, "");
            if (previous is null)
                continue;

            string? selected = TryGet(previous, source);
            if (selected is null && previous.Keys.Any(x => x.Equals(source, StringComparison.OrdinalIgnoreCase)))
                _grid.Rows[rowIndex].Cells[1].Value = IgnoreChoice;
            else if (!string.IsNullOrWhiteSpace(selected) &&
                     _plan.UnmatchedDestinationChannels.Contains(selected, StringComparer.OrdinalIgnoreCase))
                _grid.Rows[rowIndex].Cells[1].Value = selected;
        }
    }

    private void AcceptMappings()
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var usedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (DataGridViewRow row in _grid.Rows)
        {
            string source = Convert.ToString(row.Cells[0].Value)?.Trim() ?? "";
            string selected = Convert.ToString(row.Cells[1].Value)?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(source))
                continue;

            if (string.IsNullOrWhiteSpace(selected))
            {
                MessageBox.Show(
                    this,
                    $"请为“{source}”选择对应表头；如果确实不需要该列，请选择“{IgnoreChoice}”。",
                    "表头尚未选择",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            if (selected.Equals(IgnoreChoice, StringComparison.Ordinal))
            {
                result[source] = null;
                continue;
            }

            if (!usedTargets.Add(selected))
            {
                MessageBox.Show(
                    this,
                    $"基准表头“{selected}”被重复选择。一个基准表头只能对应一个追加表头。",
                    "表头对应重复",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }
            result[source] = selected;
        }

        // Reuse the core validation before the dialog closes.
        _ = OasisGdbHeaderMapping.ResolveMappings(_plan, result);
        Result = result;
        DialogResult = DialogResult.OK;
        Close();
    }

    private static string? TryGet(IReadOnlyDictionary<string, string?> source, string key)
    {
        if (source.TryGetValue(key, out string? value))
            return value;
        foreach (var pair in source)
        {
            if (pair.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                return pair.Value;
        }
        return null;
    }
}
