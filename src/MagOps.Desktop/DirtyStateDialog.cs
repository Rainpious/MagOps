namespace MagOps.Desktop;

internal enum DirtyStateDecision
{
    Cancel = 0,
    SaveAndContinue = 1,
    DiscardAndContinue = 2
}

internal sealed class DirtyStateDialog : Form
{
    private DirtyStateDecision _decision = DirtyStateDecision.Cancel;

    private DirtyStateDialog(
        IReadOnlyList<DirtyFeatureSnapshot> dirtyStates,
        string targetDescription)
    {
        Text = "未保存内容";
        Width = 620;
        Height = 390;
        MinimumSize = new Size(520, 320);
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        ShowInTaskbar = false;
        MinimizeBox = false;
        MaximizeBox = false;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(16),
            Margin = Padding.Empty
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var title = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Text = "当前存在未保存内容",
            Font = new Font(
                SystemFonts.MessageBoxFont?.FontFamily ?? FontFamily.GenericSansSerif,
                12F,
                FontStyle.Bold),
            ForeColor = UiTheme.Warning,
            Margin = new Padding(0, 0, 0, 8)
        };
        root.Controls.Add(title, 0, 0);

        var explanation = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            MaximumSize = new Size(560, 0),
            Text = $"准备{targetDescription}。请先处理以下未保存内容：",
            ForeColor = UiTheme.TextPrimary,
            Margin = new Padding(0, 0, 0, 8)
        };
        root.Controls.Add(explanation, 0, 1);

        var details = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            WordWrap = true,
            BackColor = UiTheme.SurfaceMuted,
            ForeColor = UiTheme.TextPrimary,
            Text = string.Join(
                Environment.NewLine,
                dirtyStates.Select(x => $"• {x.DisplayName}：{x.Reason}"))
        };
        root.Controls.Add(details, 0, 2);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = true,
            Margin = new Padding(0, 12, 0, 0)
        };

        var cancel = new Button
        {
            Text = "取消",
            AutoSize = true,
            DialogResult = DialogResult.Cancel
        };
        UiComponents.SetButtonRole(cancel, UiButtonRole.Subtle);
        cancel.Click += (_, _) =>
        {
            _decision = DirtyStateDecision.Cancel;
            Close();
        };

        var discard = new Button
        {
            Text = "丢弃并继续",
            AutoSize = true
        };
        UiComponents.SetButtonRole(discard, UiButtonRole.Danger);
        discard.Click += (_, _) =>
        {
            _decision = DirtyStateDecision.DiscardAndContinue;
            DialogResult = DialogResult.OK;
            Close();
        };

        bool canSaveAll = dirtyStates.All(x => x.CanSave);
        var save = new Button
        {
            Text = "保存并继续",
            AutoSize = true,
            Enabled = canSaveAll
        };
        UiComponents.SetButtonRole(save, UiButtonRole.Primary);
        save.Click += (_, _) =>
        {
            _decision = DirtyStateDecision.SaveAndContinue;
            DialogResult = DialogResult.OK;
            Close();
        };

        actions.Controls.Add(cancel);
        actions.Controls.Add(discard);
        actions.Controls.Add(save);
        root.Controls.Add(actions, 0, 3);

        AcceptButton = canSaveAll ? save : null;
        CancelButton = cancel;

        Controls.Add(root);
        UiTheme.Apply(this);
        UiLayout.FitFormToWorkingArea(this);
    }

    public static DirtyStateDecision Ask(
        IWin32Window owner,
        IReadOnlyList<DirtyFeatureSnapshot> dirtyStates,
        string targetDescription)
    {
        if (dirtyStates.Count == 0)
            return DirtyStateDecision.SaveAndContinue;

        using var dialog = new DirtyStateDialog(
            dirtyStates,
            targetDescription);
        DialogResult result = dialog.ShowDialog(owner);
        return result == DialogResult.OK
            ? dialog._decision
            : DirtyStateDecision.Cancel;
    }
}
