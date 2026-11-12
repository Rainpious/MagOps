using System.Globalization;

namespace MagOps.Desktop;

internal sealed record PromptField(
    string Key,
    string Label,
    string Value = "",
    IReadOnlyList<string>? Choices = null,
    bool Multiline = false,
    bool Required = false);

internal static class PromptDialog
{
    public static Dictionary<string, string>? Show(
        IWin32Window owner,
        string title,
        IEnumerable<PromptField> fields,
        int width = 620,
        int height = 420,
        string? note = null)
    {
        var items = fields.ToList();

        using var form = new Form
        {
            Text = title,
            Width = Math.Max(480, width),
            Height = Math.Max(220, Math.Min(height, 520)),
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.CenterParent,
            AutoScaleMode = AutoScaleMode.Dpi
        };

        var host = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = false,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(12),
            Margin = Padding.Empty,
            ColumnCount = 2,
            RowCount = items.Count + 1 + (string.IsNullOrWhiteSpace(note) ? 0 : 1)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var controls = new Dictionary<string, Control>(StringComparer.OrdinalIgnoreCase);
        Label? noteLabel = null;
        int row = 0;
        foreach (PromptField field in items)
        {
            var label = new Label
            {
                Text = field.Required ? field.Label + " *" : field.Label,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(3, 8, 8, 4)
            };

            Control control;
            if (field.Choices is { Count: > 0 })
            {
                var combo = new ComboBox
                {
                    Dock = DockStyle.Fill,
                    DropDownStyle = ComboBoxStyle.DropDownList,
                    Margin = new Padding(0, 3, 0, 3)
                };
                foreach (string raw in field.Choices)
                    combo.Items.Add(UiText.Choice(raw));

                int selected = -1;
                for (int i = 0; i < combo.Items.Count; i++)
                {
                    if (combo.Items[i] is UiChoice choice &&
                        choice.Value.Equals(field.Value, StringComparison.OrdinalIgnoreCase))
                    {
                        selected = i;
                        break;
                    }
                }

                if (selected >= 0)
                    combo.SelectedIndex = selected;
                else if (combo.Items.Count > 0)
                    combo.SelectedIndex = 0;

                control = combo;
            }
            else
            {
                var textBox = new TextBox
                {
                    Text = field.Value,
                    Dock = DockStyle.Fill,
                    Multiline = field.Multiline,
                    ScrollBars = ScrollBars.None,
                    Margin = new Padding(0, 3, 0, 3)
                };
                if (field.Multiline)
                {
                    textBox.MinimumSize = new Size(0, 68);
                    textBox.TextChanged += (_, _) =>
                        BeginInvokeSafe(form, FitToContent);
                }

                control = textBox;
            }

            layout.Controls.Add(label, 0, row);
            layout.Controls.Add(control, 1, row);
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            controls[field.Key] = control;
            row++;
        }

        if (!string.IsNullOrWhiteSpace(note))
        {
            noteLabel = new Label
            {
                Text = note,
                AutoSize = true,
                ForeColor = SystemColors.GrayText,
                Margin = new Padding(3, 5, 3, 8)
            };
            layout.Controls.Add(noteLabel, 0, row);
            layout.SetColumnSpan(noteLabel, 2);
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            row++;
        }

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = true,
            AutoScroll = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 4, 0, 0),
            Padding = new Padding(0, 2, 0, 0)
        };
        var ok = new Button
        {
            Text = "确定",
            DialogResult = DialogResult.OK,
            AutoSize = true,
            MinimumSize = new Size(88, 34),
            Margin = new Padding(4, 2, 0, 2)
        };
        var cancel = new Button
        {
            Text = "取消",
            DialogResult = DialogResult.Cancel,
            AutoSize = true,
            MinimumSize = new Size(88, 34),
            Margin = new Padding(4, 2, 0, 2)
        };
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);
        layout.Controls.Add(buttons, 0, row);
        layout.SetColumnSpan(buttons, 2);
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        host.Controls.Add(layout);
        form.Controls.Add(host);
        form.AcceptButton = ok;
        form.CancelButton = cancel;

        void FitToContent()
        {
            Screen screen = owner is Control ownerControl
                ? Screen.FromControl(ownerControl)
                : Screen.FromPoint(Cursor.Position);
            Rectangle area = screen.WorkingArea;

            int clientWidth = Math.Clamp(
                Math.Max(480, width - 16),
                440,
                Math.Max(440, area.Width - 80));

            layout.MinimumSize = new Size(clientWidth, 0);
            layout.MaximumSize = new Size(clientWidth, 0);
            if (noteLabel is not null)
                noteLabel.MaximumSize = new Size(Math.Max(300, clientWidth - 30), 0);

            int editorWidth = Math.Max(
                220,
                clientWidth - 130 - layout.Padding.Horizontal - 24);
            foreach (TextBox textBox in controls.Values.OfType<TextBox>().Where(x => x.Multiline))
            {
                Size measured = TextRenderer.MeasureText(
                    (textBox.Text ?? "") + Environment.NewLine,
                    textBox.Font,
                    new Size(editorWidth, int.MaxValue),
                    TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
                int desiredEditorHeight = Math.Clamp(measured.Height + 16, 68, 190);
                textBox.MinimumSize = new Size(0, desiredEditorHeight);
                textBox.ScrollBars = measured.Height + 16 > 190
                    ? ScrollBars.Vertical
                    : ScrollBars.None;
            }

            UiLayout.Normalize(form);
            layout.PerformLayout();

            Size preferred = layout.GetPreferredSize(new Size(clientWidth, 0));
            int requiredHeight = preferred.Height + 4;
            int maxClientHeight = Math.Max(260, area.Height - 100);
            bool needsScroll = requiredHeight > maxClientHeight;

            host.AutoScroll = needsScroll;
            form.ClientSize = new Size(
                clientWidth,
                Math.Min(requiredHeight, maxClientHeight));

            // Only genuinely long forms get a scrollbar. Small prompts fit their
            // actual content, avoiding clipped half-height buttons and empty space.
            if (!needsScroll)
                host.AutoScrollPosition = Point.Empty;
        }

        form.Shown += (_, _) =>
        {
            FitToContent();

            Control? first = items.Count > 0 &&
                             controls.TryGetValue(items[0].Key, out Control? control)
                ? control
                : null;
            first?.Focus();
            if (first is TextBox tb)
                tb.SelectAll();
        };
        form.DpiChanged += (_, _) =>
            BeginInvokeSafe(form, FitToContent);

        while (form.ShowDialog(owner) == DialogResult.OK)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            bool missing = false;
            foreach (PromptField field in items)
            {
                string value = controls[field.Key] switch
                {
                    TextBox tb => tb.Text.Trim(),
                    ComboBox cb => cb.SelectedItem is UiChoice choice
                        ? choice.Value
                        : cb.SelectedItem?.ToString()?.Trim() ?? "",
                    _ => ""
                };

                if (field.Required && value.Length == 0)
                {
                    MessageBox.Show(
                        form,
                        $"{field.Label} 不能为空。",
                        "需要填写",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                    controls[field.Key].Focus();
                    missing = true;
                    break;
                }

                result[field.Key] = value;
            }

            if (!missing)
                return result;

            form.DialogResult = DialogResult.None;
            FitToContent();
        }

        return null;
    }

    private static void BeginInvokeSafe(Control control, Action action)
    {
        if (!control.IsDisposed && control.IsHandleCreated)
            control.BeginInvoke(action);
    }
}

internal sealed class EquipmentChecklistForm : Form
{
    private sealed record TestStatusChoice(string Value, string Display);

    private readonly MagOps.Infrastructure.Services.FieldworkService _service;
    private readonly string _root;
    private readonly string _equipmentRef;
    private readonly Func<string, string, string> _ensureOutputDirectory;
    private readonly Action<string> _registerArtifact;

    private readonly TableLayoutPanel _requiredPanel = new();
    private readonly TableLayoutPanel _optionalPanel = new();
    private readonly TabControl _itemTabs = new();
    private readonly FlowLayoutPanel _testCards = new();
    private readonly Label _templateValue = new();
    private readonly Label _requiredValue = new();
    private readonly Label _optionalValue = new();
    private readonly Label _testValue = new();
    private readonly Label _statusValue = new();

    private readonly Dictionary<string, CheckBox> _checkBoxes =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, NumericUpDown> _quantityEditors =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MagOps.Infrastructure.Services.EquipmentChecklistItemView> _itemById =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<CheckBox> _checkOrder = [];
    private readonly HashSet<string> _dirtyTestTypes =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ComboBox> _testStatusEditors =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TextBox> _testNoteEditors =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Panel> _testCardByType =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _testOrder = [];

    private readonly Button _previousButton = new();
    private readonly Button _nextButton = new();
    private readonly Button _markNextButton = new();

    private MagOps.Infrastructure.Services.ChecklistView _view;
    private bool _renderingChecklist;
    private bool _nextButtonIsSave;
    private bool _checklistDirty;
    private bool _loadingTests;
    private bool _testsDirty;
    private bool _closingAfterPersist;
    private int _navigationIndex = -1;
    private int _testNavigationIndex = -1;
    private IReadOnlyList<MagOps.Infrastructure.Services.EquipmentTestDefinition> _testDefinitions = [];

    public EquipmentChecklistForm(
        MagOps.Infrastructure.Services.FieldworkService service,
        string root,
        string equipmentRef,
        Func<string, string, string> ensureOutputDirectory,
        Action<string> registerArtifact)
    {
        _service = service;
        _root = root;
        _equipmentRef = equipmentRef;
        _ensureOutputDirectory = ensureOutputDirectory;
        _registerArtifact = registerArtifact;
        _view = service.GetChecklist(root, equipmentRef);

        Text = $"设备清点 / 测试 - {_view.Equipment.Model} {_view.Equipment.EquipmentId}";
        Size = new Size(1240, 800);
        MinimumSize = new Size(820, 470);
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        KeyPreview = true;

        var outer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 1,
            RowCount = 3,
            Margin = Padding.Empty
        };
        outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var header = new GroupBox
        {
            Text = "设备清点状态",
            Dock = DockStyle.Fill,
            AutoSize = true,
            Padding = new Padding(10, 7, 10, 8),
            Margin = new Padding(0, 0, 0, 8)
        };

        var headerGrid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 5,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        headerGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 32));
        headerGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 17));
        headerGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 17));
        headerGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 17));
        headerGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 17));
        headerGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        headerGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        AddHeaderBlock(headerGrid, 0, "模板", _templateValue);
        AddHeaderBlock(headerGrid, 1, "必备项", _requiredValue);
        AddHeaderBlock(headerGrid, 2, "可选项", _optionalValue);
        AddHeaderBlock(headerGrid, 3, "设备测试", _testValue);
        AddHeaderBlock(headerGrid, 4, "综合状态", _statusValue);

        var ruleNote = new Label
        {
            Text = "清点与设备测试集中在同一窗口：必备项决定清点放行，可选项记录现场完整性；设备测试按设备实际功能生成。所有修改在保存/关闭时写入项目记录。",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(3, 6, 3, 0)
        };
        headerGrid.Controls.Add(ruleNote, 0, 1);
        headerGrid.SetColumnSpan(ruleNote, 5);

        header.Controls.Add(headerGrid);
        outer.Controls.Add(header, 0, 0);

        _itemTabs.Dock = DockStyle.Fill;
        _itemTabs.Multiline = false;

        var requiredTab = new TabPage("必备项")
        {
            Padding = new Padding(6)
        };
        var optionalTab = new TabPage("可选项")
        {
            Padding = new Padding(6)
        };
        var testTab = new TabPage("设备测试")
        {
            Padding = new Padding(6)
        };

        requiredTab.Controls.Add(BuildChecklistScroll(_requiredPanel));
        optionalTab.Controls.Add(BuildChecklistScroll(_optionalPanel));
        ConfigureIntegratedTestCards();
        testTab.Controls.Add(_testCards);
        _itemTabs.TabPages.Add(requiredTab);
        _itemTabs.TabPages.Add(optionalTab);
        _itemTabs.TabPages.Add(testTab);
        _itemTabs.SelectedIndexChanged += (_, _) =>
        {
            UpdateNavigationButtons();
            BeginInvoke((Action)FocusFirstUnresolvedInActiveTab);
        };
        outer.Controls.Add(_itemTabs, 0, 1);

        var bottomBar = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0, 6, 0, 0),
            Padding = Padding.Empty
        };
        bottomBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottomBar.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        bottomBar.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var utilityButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = true,
            AutoScroll = false,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };

        var navigationButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = true,
            AutoScroll = false,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };

        _previousButton.Text = "上一步  Alt+P";
        _previousButton.AutoSize = true;
        _previousButton.MinimumSize = new Size(104, 36);
        _previousButton.CausesValidation = false;
        UiComponents.SetButtonRole(_previousButton, UiButtonRole.Subtle);

        _nextButton.Text = "下一步  Alt+N";
        _nextButton.AutoSize = true;
        _nextButton.MinimumSize = new Size(118, 36);
        _nextButton.CausesValidation = false;
        UiComponents.SetButtonRole(_nextButton, UiButtonRole.Primary);

        var addCustom = UiComponents.Button(
            "添加自定义项",
            (_, _) => AddCustom(),
            UiButtonRole.Subtle,
            minimumHeight: 36);
        var export = UiComponents.Button(
            "导出清单 CSV",
            (_, _) => ExportCsv(),
            UiButtonRole.Subtle,
            minimumHeight: 36);

        _previousButton.Click += (_, _) => PreviousChecklistStep();
        _nextButton.Click += (_, _) => NextChecklistStep();
        _markNextButton.Text = "勾选并下一项  Alt+C";
        _markNextButton.AutoSize = true;
        _markNextButton.MinimumSize = new Size(172, 36);
        _markNextButton.CausesValidation = false;
        UiComponents.SetButtonRole(_markNextButton, UiButtonRole.Primary);
        _markNextButton.Click += (_, _) => MarkCurrentPassedAndMoveNext();

        utilityButtons.Controls.AddRange([addCustom, export]);

        // Keep navigation on its own wrapping row so utilities cannot push it out of view.
        navigationButtons.Controls.Add(_previousButton);
        navigationButtons.Controls.Add(_markNextButton);
        navigationButtons.Controls.Add(_nextButton);

        bottomBar.Controls.Add(utilityButtons, 0, 0);
        bottomBar.Controls.Add(navigationButtons, 0, 1);
        outer.Controls.Add(bottomBar, 0, 2);

        Controls.Add(outer);

        FormClosing += (_, e) =>
        {
            if (_closingAfterPersist || (!_checklistDirty && !_testsDirty))
                return;

            try
            {
                PersistAllCurrentState(writeTestSummary: true);
            }
            catch (Exception ex)
            {
                e.Cancel = true;
                MessageBox.Show(
                    this,
                    $"保存当前清点内容失败：{ex.Message}",
                    "设备清点",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        };

        KeyDown += (_, e) =>
        {
            if (e.Alt && e.KeyCode == Keys.N)
            {
                NextChecklistStep();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
            else if (e.Alt && e.KeyCode == Keys.P)
            {
                PreviousChecklistStep();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
            else if (e.Alt && e.KeyCode == Keys.C)
            {
                MarkCurrentPassedAndMoveNext();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        };

        Shown += (_, _) =>
        {
            UiLayout.SizeDialogToOwner(
                this,
                Owner,
                widthRatio: 0.86,
                heightRatio: 0.88,
                minimum: new Size(900, 560),
                maximum: new Size(1600, 1020));
            RenderItems();
            LoadIntegratedTestRows();
            _itemTabs.SelectedIndex = 0;
            UiTheme.Apply(this);
            UiLayout.Normalize(this);
            FocusFirstUnresolvedInActiveTab();
        };

        DpiChanged += (_, _) => BeginInvoke((Action)(() =>
        {
            if (_checklistDirty)
                PersistCurrentChecklist();
            RenderItems();
            if (!_testsDirty)
                LoadIntegratedTestRows();
            UiTheme.Apply(this);
            UiLayout.Normalize(this);
        }));
    }

    private static void AddHeaderBlock(
        TableLayoutPanel grid,
        int column,
        string caption,
        Label value)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(3)
        };
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        panel.Controls.Add(new Label
        {
            Text = caption,
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = Padding.Empty
        }, 0, 0);

        value.AutoSize = false;
        value.Dock = DockStyle.Fill;
        value.Height = 26;
        value.AutoEllipsis = true;
        value.Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold);
        value.TextAlign = ContentAlignment.MiddleLeft;
        value.Margin = new Padding(0, 2, 0, 0);

        panel.Controls.Add(value, 0, 1);
        grid.Controls.Add(panel, column, 0);
    }

    private static Control BuildChecklistScroll(TableLayoutPanel panel)
    {
        var scroll = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BorderStyle = BorderStyle.FixedSingle
        };

        panel.Dock = DockStyle.Top;
        panel.AutoSize = true;
        panel.ColumnCount = 5;
        panel.ColumnStyles.Clear();
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 62));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 175));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 255));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 125));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        scroll.Controls.Add(panel);
        return scroll;
    }

    private void ConfigureIntegratedTestCards()
    {
        _testCards.Dock = DockStyle.Fill;
        _testCards.AutoScroll = true;
        _testCards.WrapContents = false;
        _testCards.FlowDirection = FlowDirection.TopDown;
        _testCards.Padding = new Padding(8);
        _testCards.Margin = Padding.Empty;
        _testCards.BackColor = UiTheme.Workspace;
        _testCards.SizeChanged += (_, _) => ResizeIntegratedTestCards();
    }

    private void ResizeIntegratedTestCards()
    {
        int width = Math.Max(
            420,
            _testCards.ClientSize.Width -
            _testCards.Padding.Horizontal -
            SystemInformation.VerticalScrollBarWidth -
            8);

        foreach (Panel card in _testCardByType.Values)
            card.Width = width;
    }

    private Panel BuildIntegratedTestCard(
        int index,
        MagOps.Infrastructure.Services.EquipmentTestDefinition definition,
        MagOps.Core.Domain.EquipmentTestRecord? previous)
    {
        string testType = definition.TestType;
        var card = new Panel
        {
            Height = 148,
            BackColor = UiTheme.Surface,
            BorderStyle = BorderStyle.FixedSingle,
            Padding = new Padding(10),
            Margin = new Padding(0, 0, 0, 8),
            Tag = testType
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 3,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = Color.Transparent
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 48));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 155));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var sequence = new Label
        {
            Text = (index + 1).ToString(),
            Dock = DockStyle.Fill,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font(Font ?? SystemFonts.DefaultFont, FontStyle.Bold),
            BackColor = UiTheme.AccentSoft,
            ForeColor = UiTheme.Accent,
            Margin = new Padding(0, 0, 8, 4)
        };
        layout.Controls.Add(sequence, 0, 0);
        layout.SetRowSpan(sequence, 2);

        var title = new Label
        {
            Text = definition.TestType,
            AutoSize = true,
            Font = new Font(Font ?? SystemFonts.DefaultFont, FontStyle.Bold),
            ForeColor = UiTheme.TextPrimary,
            Margin = new Padding(0, 2, 6, 4)
        };
        layout.Controls.Add(title, 1, 0);

        var status = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Dock = DockStyle.Top,
            Margin = new Padding(4, 0, 6, 4),
            DisplayMember = nameof(TestStatusChoice.Display),
            ValueMember = nameof(TestStatusChoice.Value),
            DataSource = MagOps.Core.AppConstants.EquipmentTestStatuses
                .Select(x => new TestStatusChoice(x, UiText.Display(x)))
                .ToList()
        };
        status.SelectedValue = previous?.Status ?? "PENDING";
        if (status.SelectedIndex < 0 && status.Items.Count > 0)
            status.SelectedIndex = 0;
        layout.Controls.Add(status, 2, 0);

        var last = new Label
        {
            Text = $"上次：{FormatChecklistTime(previous?.TestedAt)}",
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = UiTheme.TextSecondary,
            Margin = new Padding(4, 2, 0, 4),
            AutoEllipsis = true
        };
        layout.Controls.Add(last, 3, 0);

        var description = new Label
        {
            Text = definition.Check,
            AutoSize = true,
            MaximumSize = new Size(900, 0),
            ForeColor = UiTheme.TextPrimary,
            Margin = new Padding(0, 5, 8, 4)
        };
        layout.Controls.Add(description, 1, 1);
        layout.SetColumnSpan(description, 3);

        var noteCaption = new Label
        {
            Text = "现场说明 / 异常记录",
            AutoSize = true,
            ForeColor = UiTheme.TextSecondary,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 7, 8, 0)
        };
        layout.Controls.Add(noteCaption, 1, 2);

        var note = new TextBox
        {
            Text = previous?.Note ?? "",
            Dock = DockStyle.Fill,
            Margin = new Padding(4, 4, 0, 0),
            PlaceholderText = "可选：记录测试现象、异常或复测说明"
        };
        layout.Controls.Add(note, 2, 2);
        layout.SetColumnSpan(note, 2);

        void SelectCard()
        {
            _testNavigationIndex = index;
            UpdateIntegratedTestCardSelection();
            UpdateNavigationButtons();
        }

        card.Click += (_, _) => SelectCard();
        layout.Click += (_, _) => SelectCard();
        title.Click += (_, _) => SelectCard();
        description.Click += (_, _) => SelectCard();
        sequence.Click += (_, _) => SelectCard();
        status.Enter += (_, _) => SelectCard();
        note.Enter += (_, _) => SelectCard();

        status.SelectedValueChanged += (_, _) =>
        {
            SelectCard();
            if (_loadingTests)
                return;
            _dirtyTestTypes.Add(testType);
            _testsDirty = true;
            UpdateTestTabTitle();
        };
        note.TextChanged += (_, _) =>
        {
            SelectCard();
            if (_loadingTests)
                return;
            _dirtyTestTypes.Add(testType);
            _testsDirty = true;
        };

        _testStatusEditors[testType] = status;
        _testNoteEditors[testType] = note;
        _testCardByType[testType] = card;
        _testOrder.Add(testType);

        card.Controls.Add(layout);
        return card;
    }

    private void LoadIntegratedTestRows()
    {
        _loadingTests = true;
        try
        {
            _testDefinitions = MagOps.Infrastructure.Services.EquipmentTestProfiles.For(_view.Equipment);
            Dictionary<string, MagOps.Core.Domain.EquipmentTestRecord> latest = _service
                .ListEquipmentTests(_root, _equipmentRef)
                .GroupBy(x => x.TestType, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.Last(),
                    StringComparer.OrdinalIgnoreCase);

            _testCards.SuspendLayout();
            _testCards.Controls.Clear();
            _testStatusEditors.Clear();
            _testNoteEditors.Clear();
            _testCardByType.Clear();
            _testOrder.Clear();

            for (int index = 0; index < _testDefinitions.Count; index++)
            {
                MagOps.Infrastructure.Services.EquipmentTestDefinition definition =
                    _testDefinitions[index];
                latest.TryGetValue(
                    definition.TestType,
                    out MagOps.Core.Domain.EquipmentTestRecord? previous);
                _testCards.Controls.Add(
                    BuildIntegratedTestCard(index, definition, previous));
            }

            if (_testDefinitions.Count == 0)
            {
                _testCards.Controls.Add(new Label
                {
                    Text = "当前设备没有定义测试项目。",
                    AutoSize = true,
                    ForeColor = UiTheme.TextSecondary,
                    Margin = new Padding(12, 18, 12, 18)
                });
            }

            _testNavigationIndex = _testOrder.Count > 0 ? 0 : -1;
            _dirtyTestTypes.Clear();
            _testsDirty = false;
            ResizeIntegratedTestCards();
            UpdateIntegratedTestCardSelection();
        }
        finally
        {
            _testCards.ResumeLayout(true);
            _loadingTests = false;
        }

        UpdateTestTabTitle();
    }

    private bool IsTestTabActive() => _itemTabs.SelectedIndex == 2;

    private void UpdateIntegratedTestCardSelection()
    {
        for (int index = 0; index < _testOrder.Count; index++)
        {
            if (!_testCardByType.TryGetValue(_testOrder[index], out Panel? card))
                continue;
            card.BackColor = index == _testNavigationIndex
                ? UiTheme.AccentSoft
                : UiTheme.Surface;
        }
    }

    private void FocusTestRow(int index)
    {
        if (_testOrder.Count == 0)
            return;

        index = Math.Clamp(index, 0, _testOrder.Count - 1);
        _testNavigationIndex = index;
        UpdateIntegratedTestCardSelection();

        string testType = _testOrder[index];
        if (_testCardByType.TryGetValue(testType, out Panel? card))
            _testCards.ScrollControlIntoView(card);
        if (_testStatusEditors.TryGetValue(testType, out ComboBox? status))
            status.Focus();

        UpdateNavigationButtons();
    }

    private void MoveTestSelection(int delta)
    {
        if (_testOrder.Count == 0)
            return;

        int current = _testNavigationIndex;
        if (current < 0)
            current = 0;
        FocusTestRow(Math.Clamp(current + delta, 0, _testOrder.Count - 1));
    }

    private void MarkCurrentTestPassedAndMoveNext()
    {
        if (_testOrder.Count == 0)
            return;

        int index = Math.Clamp(
            _testNavigationIndex < 0 ? 0 : _testNavigationIndex,
            0,
            _testOrder.Count - 1);
        string testType = _testOrder[index];
        if (_testStatusEditors.TryGetValue(testType, out ComboBox? status))
        {
            status.SelectedValue = "PASSED";
            _dirtyTestTypes.Add(testType);
            _testsDirty = true;
        }
        UpdateTestTabTitle();

        if (index < _testOrder.Count - 1)
            FocusTestRow(index + 1);
        else
            FocusTestRow(index);
    }

    private void UpdateTestTabTitle()
    {
        if (_itemTabs.TabPages.Count < 3)
            return;

        int total = _testOrder.Count;
        int passed = _testOrder.Count(testType =>
            _testStatusEditors.TryGetValue(testType, out ComboBox? status) &&
            string.Equals(
                status.SelectedValue?.ToString(),
                "PASSED",
                StringComparison.OrdinalIgnoreCase));
        _itemTabs.TabPages[2].Text = $"设备测试  {passed}/{total}";
        _testValue.Text = $"{passed}/{total}";
    }

    private static string FormatChecklistTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "未记录";
        return DateTimeOffset.TryParse(value, out DateTimeOffset parsed)
            ? parsed.LocalDateTime.ToString("yyyy-MM-dd HH:mm")
            : value;
    }

    private void RenderItems()
    {
        _renderingChecklist = true;
        try
        {
            _view = _service.GetChecklist(_root, _equipmentRef);

            _templateValue.Text = _view.Available
                ? _view.Template?.Title ?? "自定义清单"
                : "当前无模板";

            _checkBoxes.Clear();
            _quantityEditors.Clear();
            _itemById.Clear();
            _checkOrder.Clear();
            _navigationIndex = -1;

            foreach (var item in _view.Items)
                _itemById[item.Item.ItemId] = item;

            List<MagOps.Infrastructure.Services.EquipmentChecklistItemView> required =
                _view.Items
                    .Where(x =>
                        x.Item.Requirement ==
                        MagOps.Infrastructure.Services.EquipmentTemplates.Required)
                    .ToList();

            List<MagOps.Infrastructure.Services.EquipmentChecklistItemView> optional =
                _view.Items
                    .Where(x =>
                        x.Item.Requirement ==
                        MagOps.Infrastructure.Services.EquipmentTemplates.Optional)
                    .ToList();

            RenderGroup(_requiredPanel, required, requiredGroup: true);
            RenderGroup(_optionalPanel, optional, requiredGroup: false);
        }
        finally
        {
            _renderingChecklist = false;
        }

        UpdateLiveSummary();
        _checklistDirty = false;
    }

    private void RenderGroup(
        TableLayoutPanel panel,
        IReadOnlyList<MagOps.Infrastructure.Services.EquipmentChecklistItemView> items,
        bool requiredGroup)
    {
        panel.SuspendLayout();
        try
        {
            panel.Controls.Clear();
            panel.RowStyles.Clear();
            panel.RowCount = 1;

            string[] headers =
            [
                "核验",
                "类型",
                "名称",
                "数量",
                "清点内容"
            ];

            for (int col = 0; col < headers.Length; col++)
            {
                panel.Controls.Add(new Label
                {
                    Text = headers[col],
                    AutoSize = false,
                    Dock = DockStyle.Fill,
                    Height = 30,
                    TextAlign = ContentAlignment.MiddleLeft,
                    Font = new Font(Font ?? SystemFonts.DefaultFont, FontStyle.Bold),
                    Padding = new Padding(4)
                }, col, 0);
            }
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));

            if (items.Count == 0)
            {
                int emptyRow = panel.RowCount++;
                var empty = new Label
                {
                    Text = requiredGroup
                        ? "当前没有必备清点项。"
                        : "当前没有可选清点项。",
                    AutoSize = true,
                    ForeColor = SystemColors.GrayText,
                    Margin = new Padding(12, 18, 12, 18)
                };
                panel.Controls.Add(empty, 0, emptyRow);
                panel.SetColumnSpan(empty, 5);
                panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                return;
            }

            foreach (var sectionGroup in items.GroupBy(
                         x => string.IsNullOrWhiteSpace(x.Item.Section)
                             ? "其他"
                             : x.Item.Section))
            {
                int sectionRow = panel.RowCount++;
                var section = new Label
                {
                    Text = sectionGroup.Key,
                    AutoSize = false,
                    Dock = DockStyle.Fill,
                    Height = 34,
                    TextAlign = ContentAlignment.MiddleLeft,
                    Font = new Font(Font ?? SystemFonts.DefaultFont, FontStyle.Bold),
                    BackColor = SystemColors.ControlLight,
                    Padding = new Padding(9, 6, 6, 5),
                    Margin = new Padding(0, 7, 0, 2)
                };
                panel.Controls.Add(section, 0, sectionRow);
                panel.SetColumnSpan(section, 5);
                panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));

                foreach (var itemView in sectionGroup)
                {
                    int row = panel.RowCount++;
                    string itemId = itemView.Item.ItemId;

                    var check = new CheckBox
                    {
                        Checked = itemView.Checked,
                        Tag = itemId,
                        AutoSize = true,
                        Anchor = AnchorStyles.Top | AnchorStyles.Left,
                        Margin = new Padding(9, 9, 3, 3),
                        AccessibleName = $"核验 {itemView.Item.Name}"
                    };
                    check.Enter += (_, _) =>
                    {
                        _navigationIndex = _checkOrder.IndexOf(check);
                        UpdateNavigationButtons();
                    };
                    check.CheckedChanged += (_, _) =>
                    {
                        _navigationIndex = _checkOrder.IndexOf(check);
                        if (!_renderingChecklist)
                        {
                            _checklistDirty = true;
                            UpdateLiveSummary();
                        }
                    };
                    check.Click += (_, _) =>
                    {
                        if (_renderingChecklist || !check.Checked)
                            return;

                        EnsureChecklistQuantityForPass(check);
                        BeginInvoke((Action)NextChecklistStep);
                    };
                    check.KeyDown += (_, e) =>
                    {
                        if (e.KeyCode == Keys.Enter)
                        {
                            NextChecklistStep();
                            e.Handled = true;
                            e.SuppressKeyPress = true;
                        }
                    };

                    panel.Controls.Add(check, 0, row);
                    _checkBoxes[itemId] = check;
                    _checkOrder.Add(check);

                    panel.Controls.Add(new Label
                    {
                        Text = itemView.Item.Category,
                        AutoSize = true,
                        MaximumSize = new Size(165, 0),
                        Margin = new Padding(5, 7, 8, 7)
                    }, 1, row);

                    panel.Controls.Add(new Label
                    {
                        Text = itemView.Item.Name,
                        AutoSize = true,
                        MaximumSize = new Size(245, 0),
                        Font = new Font(Font ?? SystemFonts.DefaultFont, FontStyle.Bold),
                        Margin = new Padding(5, 7, 8, 7)
                    }, 2, row);

                    Control quantityControl;
                    if (itemView.Item.QuantityEditable)
                    {
                        var quantity = new NumericUpDown
                        {
                            Minimum = 0,
                            Maximum = 999,
                            Value = Math.Clamp(
                                itemView.Quantity ??
                                itemView.Item.DefaultQuantity,
                                0,
                                999),
                            Tag = itemId,
                            Width = 62,
                            Increment = 1,
                            TextAlign = HorizontalAlignment.Center,
                            Margin = new Padding(0)
                        };
                        quantity.Enter += (_, _) =>
                        {
                            if (_checkBoxes.TryGetValue(
                                    itemId,
                                    out CheckBox? owner))
                            {
                                _navigationIndex =
                                    _checkOrder.IndexOf(owner);
                                UpdateNavigationButtons();
                            }
                        };
                        quantity.ValueChanged += (_, _) =>
                        {
                            if (_checkBoxes.TryGetValue(
                                    itemId,
                                    out CheckBox? owner))
                            {
                                _navigationIndex =
                                    _checkOrder.IndexOf(owner);
                            }

                            if (!_renderingChecklist)
                            {
                                _checklistDirty = true;
                                UpdateLiveSummary();
                            }
                        };

                        var quantityHost = new FlowLayoutPanel
                        {
                            AutoSize = true,
                            WrapContents = false,
                            Margin = new Padding(5, 6, 3, 5),
                            Padding = Padding.Empty
                        };
                        quantityHost.Controls.Add(quantity);
                        quantityHost.Controls.Add(new Label
                        {
                            Text = itemView.Item.QuantityUnit,
                            AutoSize = true,
                            Margin = new Padding(5, 5, 0, 0)
                        });

                        _quantityEditors[itemId] = quantity;
                        quantityControl = quantityHost;
                    }
                    else
                    {
                        quantityControl = new Label
                        {
                            Text = itemView.Item.Expected,
                            AutoSize = true,
                            MaximumSize = new Size(135, 0),
                            Margin = new Padding(5, 7, 4, 5)
                        };
                    }

                    panel.Controls.Add(quantityControl, 3, row);

                    panel.Controls.Add(new Label
                    {
                        Text = itemView.Item.Check,
                        AutoSize = true,
                        MaximumSize = new Size(760, 0),
                        Margin = new Padding(5, 7, 10, 9)
                    }, 4, row);

                    panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                }
            }
        }
        finally
        {
            panel.ResumeLayout(true);
        }
    }

    private void UpdateLiveSummary()
    {
        int requiredTotal = 0;
        int requiredChecked = 0;
        int optionalTotal = 0;
        int optionalChecked = 0;

        foreach (var itemView in _view.Items)
        {
            bool satisfied = IsLiveItemSatisfied(itemView);
            if (itemView.Item.Requirement ==
                MagOps.Infrastructure.Services.EquipmentTemplates.Required)
            {
                requiredTotal++;
                if (satisfied)
                    requiredChecked++;
            }
            else
            {
                optionalTotal++;
                if (satisfied)
                    optionalChecked++;
            }
        }

        string status = requiredTotal == 0
            ? "NOT_REQUIRED"
            : requiredChecked == requiredTotal
                ? "PASSED"
                : "PENDING";

        _requiredValue.Text = $"{requiredChecked}/{requiredTotal}";
        _optionalValue.Text = $"{optionalChecked}/{optionalTotal}";
        _statusValue.Text = UiText.Display(status);

        if (_itemTabs.TabPages.Count >= 2)
        {
            _itemTabs.TabPages[0].Text =
                $"必备项  {requiredChecked}/{requiredTotal}";
            _itemTabs.TabPages[1].Text =
                $"可选项  {optionalChecked}/{optionalTotal}";
        }

        UpdateNavigationButtons();
    }

    private bool IsLiveItemSatisfied(
        MagOps.Infrastructure.Services.EquipmentChecklistItemView itemView)
    {
        if (!_checkBoxes.TryGetValue(
                itemView.Item.ItemId,
                out CheckBox? check) ||
            !check.Checked)
        {
            return false;
        }

        if (!itemView.Item.QuantityEditable)
            return true;

        int value = _quantityEditors.TryGetValue(
                itemView.Item.ItemId,
                out NumericUpDown? quantity)
            ? (int)quantity.Value
            : itemView.Quantity ??
              itemView.Item.DefaultQuantity;

        return value >= Math.Max(
            itemView.Item.MinQuantity,
            1);
    }

    private IReadOnlyList<CheckBox> ChecklistOrderForTab(int tabIndex)
    {
        if (tabIndex is < 0 or > 1)
            return [];

        string requirement = tabIndex == 1
            ? MagOps.Infrastructure.Services.EquipmentTemplates.Optional
            : MagOps.Infrastructure.Services.EquipmentTemplates.Required;

        return _checkOrder
            .Where(check =>
            {
                if (check.Tag is not string id ||
                    !_itemById.TryGetValue(
                        id,
                        out MagOps.Infrastructure.Services.EquipmentChecklistItemView? item))
                {
                    return false;
                }
                return item.Item.Requirement == requirement;
            })
            .ToList();
    }

    private int ActiveChecklistLocalIndex()
    {
        IReadOnlyList<CheckBox> active = ChecklistOrderForTab(_itemTabs.SelectedIndex);
        if (active.Count == 0)
            return -1;

        int global = FindFocusedChecklistIndex();
        if (global >= 0 && global < _checkOrder.Count)
        {
            CheckBox current = _checkOrder[global];
            for (int i = 0; i < active.Count; i++)
            {
                if (ReferenceEquals(active[i], current))
                    return i;
            }
        }

        return 0;
    }

    private void NextChecklistStep()
    {
        if (IsTestTabActive())
        {
            if (_testOrder.Count == 0 ||
                _testNavigationIndex >= _testOrder.Count - 1 ||
                _nextButtonIsSave)
            {
                SaveChecklistAndClose();
                return;
            }

            MoveTestSelection(1);
            return;
        }

        IReadOnlyList<CheckBox> active = ChecklistOrderForTab(_itemTabs.SelectedIndex);
        int local = ActiveChecklistLocalIndex();
        if (active.Count > 0 && local >= 0 && local < active.Count - 1)
        {
            FocusChecklistControl(active[local + 1]);
            return;
        }

        if (_itemTabs.SelectedIndex == 0)
        {
            _itemTabs.SelectedIndex = 1;
            BeginInvoke((Action)FocusFirstUnresolvedInActiveTab);
            return;
        }

        if (_itemTabs.SelectedIndex == 1)
        {
            _itemTabs.SelectedIndex = 2;
            BeginInvoke((Action)FocusFirstUnresolvedInActiveTab);
            return;
        }

        SaveChecklistAndClose();
    }

    private void PreviousChecklistStep()
    {
        if (IsTestTabActive())
        {
            if (_testOrder.Count > 0 && _testNavigationIndex > 0)
            {
                MoveTestSelection(-1);
                return;
            }

            _itemTabs.SelectedIndex = 1;
            IReadOnlyList<CheckBox> optional = ChecklistOrderForTab(1);
            if (optional.Count > 0)
                BeginInvoke((Action)(() => FocusChecklistControl(optional[^1])));
            else
                BeginInvoke((Action)FocusFirstUnresolvedInActiveTab);
            return;
        }

        IReadOnlyList<CheckBox> active = ChecklistOrderForTab(_itemTabs.SelectedIndex);
        int local = ActiveChecklistLocalIndex();
        if (active.Count > 0 && local > 0)
        {
            FocusChecklistControl(active[local - 1]);
            return;
        }

        if (_itemTabs.SelectedIndex == 1)
        {
            _itemTabs.SelectedIndex = 0;
            IReadOnlyList<CheckBox> required = ChecklistOrderForTab(0);
            if (required.Count > 0)
                BeginInvoke((Action)(() => FocusChecklistControl(required[^1])));
            else
                BeginInvoke((Action)FocusFirstUnresolvedInActiveTab);
        }
    }

    private void MarkCurrentPassedAndMoveNext()
    {
        if (IsTestTabActive())
        {
            MarkCurrentTestPassedAndMoveNext();
            return;
        }

        IReadOnlyList<CheckBox> active = ChecklistOrderForTab(_itemTabs.SelectedIndex);
        int local = ActiveChecklistLocalIndex();
        if (active.Count == 0 || local < 0)
            return;

        CheckBox check = active[Math.Clamp(local, 0, active.Count - 1)];
        EnsureChecklistQuantityForPass(check);
        check.Checked = true;
        NextChecklistStep();
    }

    private void EnsureChecklistQuantityForPass(CheckBox check)
    {
        if (check.Tag is not string itemId ||
            !_itemById.TryGetValue(
                itemId,
                out MagOps.Infrastructure.Services.EquipmentChecklistItemView? item) ||
            !item.Item.QuantityEditable ||
            !_quantityEditors.TryGetValue(
                itemId,
                out NumericUpDown? quantity))
        {
            return;
        }

        int required = Math.Max(
            item.Item.MinQuantity,
            1);
        if (quantity.Value < required)
            quantity.Value = required;
    }

    private int FindFocusedChecklistIndex()
    {
        // Button clicks move focus away from the current checklist item before
        // the click handler runs, so the persistent navigation index is authoritative.
        if (_navigationIndex >= 0 &&
            _navigationIndex < _checkOrder.Count)
        {
            return _navigationIndex;
        }

        for (int i = 0; i < _checkOrder.Count; i++)
        {
            CheckBox check = _checkOrder[i];
            if (check.Focused || check.ContainsFocus)
                return i;

            if (check.Tag is string id &&
                _quantityEditors.TryGetValue(
                    id,
                    out NumericUpDown? quantity) &&
                (quantity.Focused || quantity.ContainsFocus))
            {
                return i;
            }
        }

        return -1;
    }

    private void FocusFirstUnresolvedInActiveTab()
    {
        if (IsTestTabActive())
        {
            if (_testOrder.Count == 0)
            {
                UpdateNavigationButtons();
                return;
            }

            int targetIndex = 0;
            for (int i = 0; i < _testOrder.Count; i++)
            {
                string testType = _testOrder[i];
                if (!_testStatusEditors.TryGetValue(testType, out ComboBox? status) ||
                    !string.Equals(
                        status.SelectedValue?.ToString(),
                        "PASSED",
                        StringComparison.OrdinalIgnoreCase))
                {
                    targetIndex = i;
                    break;
                }
            }
            FocusTestRow(targetIndex);
            return;
        }

        string requirement = _itemTabs.SelectedIndex == 1
            ? MagOps.Infrastructure.Services.EquipmentTemplates.Optional
            : MagOps.Infrastructure.Services.EquipmentTemplates.Required;

        // Never jump to another tab just because every item in the current tab
        // is already satisfied. The tab itself is the workflow step the operator
        // chose, so keep focus inside it and let “下一步” perform the transition.
        CheckBox? target = FindFirstUnresolved(requirement)
            ?? FindFirstByRequirement(requirement);

        if (target is not null)
        {
            FocusChecklistControl(target);
            return;
        }

        UpdateNavigationButtons();
    }

    private CheckBox? FindFirstUnresolved(string requirement) =>
        _checkOrder.FirstOrDefault(check =>
        {
            if (check.Tag is not string id ||
                !_itemById.TryGetValue(
                    id,
                    out MagOps.Infrastructure.Services.EquipmentChecklistItemView? item))
            {
                return false;
            }

            return item.Item.Requirement == requirement &&
                   !IsLiveItemSatisfied(item);
        });

    private CheckBox? FindFirstByRequirement(string requirement) =>
        _checkOrder.FirstOrDefault(check =>
        {
            if (check.Tag is not string id ||
                !_itemById.TryGetValue(
                    id,
                    out MagOps.Infrastructure.Services.EquipmentChecklistItemView? item))
            {
                return false;
            }

            return item.Item.Requirement == requirement;
        });

    private void FocusChecklistControl(CheckBox check)
    {
        int index = _checkOrder.IndexOf(check);
        if (index >= 0)
            _navigationIndex = index;

        if (check.Tag is string id &&
            _itemById.TryGetValue(
                id,
                out MagOps.Infrastructure.Services.EquipmentChecklistItemView? item))
        {
            _itemTabs.SelectedIndex =
                item.Item.Requirement ==
                MagOps.Infrastructure.Services.EquipmentTemplates.Optional
                    ? 1
                    : 0;
        }

        check.Focus();

        Control? parent = check.Parent;
        while (parent is not null)
        {
            if (parent is ScrollableControl scrollable &&
                scrollable.AutoScroll)
            {
                scrollable.ScrollControlIntoView(check);
                break;
            }
            parent = parent.Parent;
        }

        UpdateNavigationButtons();
    }

    private void UpdateNavigationButtons()
    {
        _markNextButton.Text = IsTestTabActive() ? "本项通过并下一项  Alt+C" : "勾选并下一项  Alt+C";
        _markNextButton.Enabled = IsTestTabActive() ? _testOrder.Count > 0 : ChecklistOrderForTab(_itemTabs.SelectedIndex).Count > 0;
        if (IsTestTabActive())
        {
            int count = _testOrder.Count;
            int current = count == 0
                ? -1
                : Math.Clamp(_testNavigationIndex < 0 ? 0 : _testNavigationIndex, 0, count - 1);

            bool hasPreviousStep = current > 0 || ChecklistOrderForTab(1).Count > 0;
            _previousButton.Enabled = hasPreviousStep;
            bool finish = count == 0 || current >= count - 1;
            SetNextButtonMode(finish, finish ? "保存并关闭" : "下一步  Alt+N");
            return;
        }

        IReadOnlyList<CheckBox> active = ChecklistOrderForTab(_itemTabs.SelectedIndex);
        int local = ActiveChecklistLocalIndex();
        bool onOptional = _itemTabs.SelectedIndex == 1;

        _previousButton.Enabled = onOptional || local > 0;
        bool atEnd = active.Count == 0 || local >= active.Count - 1;
        string text = atEnd
            ? onOptional
                ? "下一步：设备测试"
                : "下一步：可选项"
            : "下一步  Alt+N";
        SetNextButtonMode(saveMode: false, text);
    }

    private void SetNextButtonMode(bool saveMode, string? text = null)
    {
        _nextButtonIsSave = saveMode;
        _nextButton.Text = string.IsNullOrWhiteSpace(text)
            ? saveMode ? "保存并关闭" : "下一步  Alt+N"
            : text;
        _nextButton.MinimumSize = new Size(
            saveMode ? 118 : 132,
            36);
        UiComponents.SetButtonRole(_nextButton, UiButtonRole.Primary);
        UiTheme.ApplyButton(
            _previousButton,
            UiComponents.GetButtonRole(_previousButton));
        UiTheme.ApplyButton(
            _nextButton,
            UiComponents.GetButtonRole(_nextButton));
    }

    private void PersistCurrentChecklist()
    {
        List<string> checkedIds = _checkBoxes
            .Where(x => x.Value.Checked)
            .Select(x => x.Key)
            .ToList();

        Dictionary<string, int> quantities = _quantityEditors
            .ToDictionary(
                x => x.Key,
                x => (int)x.Value.Value,
                StringComparer.OrdinalIgnoreCase);

        _view = _service.SaveChecklist(
            _root,
            _equipmentRef,
            checkedIds,
            quantities);
        _checklistDirty = false;
    }

    private IReadOnlyList<MagOps.Core.Domain.EquipmentTestRecord> PersistCurrentTests(
        bool writeSummary)
    {
        if (!_testsDirty || _dirtyTestTypes.Count == 0)
            return [];

        var saved = new List<MagOps.Core.Domain.EquipmentTestRecord>();
        foreach (string testType in _testOrder)
        {
            if (!_dirtyTestTypes.Contains(testType))
                continue;

            string status = _testStatusEditors.TryGetValue(
                    testType,
                    out ComboBox? statusEditor)
                ? statusEditor.SelectedValue?.ToString() ?? "PENDING"
                : "PENDING";
            string note = _testNoteEditors.TryGetValue(
                    testType,
                    out TextBox? noteEditor)
                ? noteEditor.Text
                : "";
            saved.Add(_service.RecordEquipmentTest(
                _root,
                _equipmentRef,
                testType,
                status,
                note));
        }

        _dirtyTestTypes.Clear();
        _testsDirty = false;
        _view = _service.GetChecklist(_root, _equipmentRef);

        if (writeSummary && saved.Count > 0)
        {
            HashSet<string> currentTypes = _testDefinitions
                .Select(x => x.TestType)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, MagOps.Core.Domain.EquipmentTestRecord> latestByType = _service
                .ListEquipmentTests(_root, _equipmentRef)
                .Where(x => currentTypes.Contains(x.TestType))
                .GroupBy(x => x.TestType, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.Last(),
                    StringComparer.OrdinalIgnoreCase);
            List<MagOps.Core.Domain.EquipmentTestRecord> latest = _testDefinitions
                .Where(definition => latestByType.ContainsKey(definition.TestType))
                .Select(definition => latestByType[definition.TestType])
                .ToList();

            string directory = _ensureOutputDirectory(
                "equipment_test_export",
                "保存设备测试报告");
            string fileName = SafeFileName(
                $"设备测试_{_view.Equipment.Model}_{_view.Equipment.EquipmentId}_{DateTime.Now:yyyyMMdd_HHmmss}.md");
            string path = Path.Combine(directory, fileName);

            MagOps.Infrastructure.Services.FieldworkDocumentExportService
                .WriteEquipmentTestSummary(
                    path,
                    _view.Equipment,
                    latest);
            _registerArtifact(path);
        }

        return saved;
    }

    private void PersistAllCurrentState(bool writeTestSummary)
    {
        if (_checklistDirty)
            PersistCurrentChecklist();
        PersistCurrentTests(writeTestSummary);
    }

    private void SaveChecklistAndClose()
    {
        PersistAllCurrentState(writeTestSummary: true);

        // Complete / save / Esc / close all persist the current real checklist
        // and device-test state so reopening resumes from the last record.
        _closingAfterPersist = true;
        DialogResult = DialogResult.OK;
        Close();
    }

    private void AddCustom()
    {
        if (_checklistDirty)
            PersistCurrentChecklist();

        var values = PromptDialog.Show(
            this,
            "添加自定义项",
            [
                new("section", "分区", "自定义"),
                new("category", "类型", "工具"),
                new("name", "名称", Required: true),
                new("check", "清点内容", "按现场任务需要确认数量、状态和可用性。", Multiline: true),
                new(
                    "requirement",
                    "等级",
                    MagOps.Infrastructure.Services.EquipmentTemplates.Optional,
                    [
                        MagOps.Infrastructure.Services.EquipmentTemplates.Required,
                        MagOps.Infrastructure.Services.EquipmentTemplates.Optional
                    ]),
                new("quantity", "默认数量", "1")
            ],
            width: 720,
            note: "分区会作为清单中的独立区块标题；类型用于填写设备或附件的通用名称。");

        if (values is null)
            return;

        if (!int.TryParse(values["quantity"], out int quantity))
            quantity = 1;

        _service.AddCustomChecklistItem(
            _root,
            _equipmentRef,
            values["name"],
            values["category"],
            values["requirement"],
            quantity,
            values["section"],
            values["check"]);

        RenderItems();
        _itemTabs.SelectedIndex =
            values["requirement"] ==
            MagOps.Infrastructure.Services.EquipmentTemplates.Required
                ? 0
                : 1;
        FocusFirstUnresolvedInActiveTab();
    }

    private void ExportCsv()
    {
        PersistAllCurrentState(writeTestSummary: false);
        string directory = _ensureOutputDirectory(
            "equipment_checklist_export",
            "导出设备清点 CSV");
        string fileName = SafeFileName(
            $"设备清点_{_view.Equipment.Model}_{_view.Equipment.EquipmentId}_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
        string path = Path.Combine(directory, fileName);

        _service.ExportEquipmentChecklistCsv(
            _root,
            _equipmentRef,
            path);
        _registerArtifact(path);

        TransientNotice.Show(this, $"设备清单 CSV 已生成：{Path.GetFileName(path)}", "文件写入成功");
    }

    private static string SafeFileName(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        return string.Concat(
            value.Select(ch =>
                invalid.Contains(ch)
                    ? '_'
                    : ch));
    }
}

