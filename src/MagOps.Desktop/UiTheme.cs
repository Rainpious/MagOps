
namespace MagOps.Desktop;

internal enum UiButtonRole
{
    Neutral,
    Primary,
    Subtle,
    Danger
}

/// <summary>
/// Central visual tokens for the WinForms desktop shell.
/// Keep this class dependency-free so the production UI remains self-contained.
/// </summary>
internal static class UiTheme
{
    public static readonly Color Workspace = Color.FromArgb(246, 248, 250);
    public static readonly Color Surface = Color.FromArgb(255, 255, 255);
    public static readonly Color SurfaceMuted = Color.FromArgb(241, 245, 249);
    public static readonly Color Border = Color.FromArgb(214, 222, 230);
    public static readonly Color BorderStrong = Color.FromArgb(184, 196, 208);

    public static readonly Color TextPrimary = Color.FromArgb(31, 41, 55);
    public static readonly Color TextSecondary = Color.FromArgb(91, 103, 116);
    public static readonly Color Accent = Color.FromArgb(15, 118, 138);
    public static readonly Color AccentHover = Color.FromArgb(14, 104, 122);
    public static readonly Color AccentSoft = Color.FromArgb(224, 242, 246);
    public static readonly Color Success = Color.FromArgb(34, 120, 78);
    public static readonly Color SuccessSoft = Color.FromArgb(229, 246, 237);
    public static readonly Color Warning = Color.FromArgb(158, 97, 7);
    public static readonly Color WarningSoft = Color.FromArgb(255, 246, 219);
    public static readonly Color RepeatedMeasurement = Color.FromArgb(112, 77, 170);
    public static readonly Color Danger = Color.FromArgb(180, 52, 52);
    public static readonly Color DangerSoft = Color.FromArgb(252, 235, 235);
    public static readonly Color Disabled = Color.FromArgb(148, 163, 184);

    public static readonly Padding PagePadding = new(12);
    public static readonly Padding SectionPadding = new(12, 10, 12, 10);

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Control, object> Observed = new();

    // A single application scope also covers existing dialogs that do not call Apply explicitly.
    internal static IDisposable TrackApplication() => new AppearanceScope();

    private sealed class AppearanceScope : IDisposable
    {
        private System.Runtime.CompilerServices.ConditionalWeakTable<Form, object> _forms = new();
        private bool _highContrast = SystemInformation.HighContrast;
        internal AppearanceScope() => System.Windows.Forms.Application.Idle += ApplyPending;
        private void ApplyPending(object? sender, EventArgs e)
        {
            if (_highContrast != SystemInformation.HighContrast)
            {
                _highContrast = SystemInformation.HighContrast;
                _forms = new();
            }
            foreach (Form form in System.Windows.Forms.Application.OpenForms.Cast<Form>().ToArray())
            {
                if (form.IsDisposed || _forms.TryGetValue(form, out _)) continue;
                _forms.Add(form, new());
                Apply(form);
            }
        }
        public void Dispose() => System.Windows.Forms.Application.Idle -= ApplyPending;
    }

    public static void Apply(Control root)
    {
        ApplyRecursive(root);
        root.Invalidate(true);
    }

    private static void ApplyRecursive(Control control)
    {
        if (!Observed.TryGetValue(control, out _))
        {
            Observed.Add(control, new());
            control.ControlAdded += (_, e) => { if (e.Control is not null) ApplyRecursive(e.Control); };
            if (control is Button observedButton)
            {
                observedButton.EnabledChanged += (_, _) => ApplyButton(observedButton, UiComponents.GetButtonRole(observedButton));
                observedButton.AccessibleName ??= observedButton.Text.Replace("&", "");
            }
        }
        switch (control)
        {
            case Form form:
                form.BackColor = Workspace;
                form.ForeColor = TextPrimary;
                break;

            case TabPage tabPage:
                tabPage.BackColor = Workspace;
                tabPage.ForeColor = TextPrimary;
                break;

            case GroupBox group:
                group.BackColor = Surface;
                group.ForeColor = TextPrimary;
                break;

            case Button button:
                ApplyButton(button, UiComponents.GetButtonRole(button));
                break;

            case DataGridView grid:
                ApplyGrid(grid);
                break;

            case TextBox textBox:
                textBox.BackColor = textBox.ReadOnly ? SurfaceMuted : Surface;
                textBox.ForeColor = TextPrimary;
                textBox.BorderStyle = BorderStyle.FixedSingle;
                break;

            case RichTextBox richTextBox:
                richTextBox.BackColor = richTextBox.ReadOnly ? SurfaceMuted : Surface;
                richTextBox.ForeColor = TextPrimary;
                richTextBox.BorderStyle = BorderStyle.FixedSingle;
                break;

            case ComboBox combo:
                combo.BackColor = Surface;
                combo.ForeColor = TextPrimary;
                break;

            case ListBox list:
                list.BackColor = Surface;
                list.ForeColor = TextPrimary;
                list.BorderStyle = BorderStyle.FixedSingle;
                break;

            case StatusStrip status:
                status.BackColor = Surface;
                status.ForeColor = TextSecondary;
                status.RenderMode = ToolStripRenderMode.System;
                break;
        }

        if (SystemInformation.HighContrast)
        {
            switch (control)
            {
                case Form or TabPage or GroupBox:
                    control.BackColor = SystemColors.Control; control.ForeColor = SystemColors.ControlText; break;
                case TextBoxBase or ComboBox or ListBox:
                    control.BackColor = SystemColors.Window; control.ForeColor = SystemColors.WindowText; break;
            }
        }
        foreach (Control child in control.Controls)
            ApplyRecursive(child);
    }

    public static void ApplyButton(Button button, UiButtonRole role)
    {
        if (SystemInformation.HighContrast)
        {
            button.FlatStyle = FlatStyle.Standard;
            button.UseVisualStyleBackColor = true;
            button.BackColor = SystemColors.Control;
            button.ForeColor = button.Enabled ? SystemColors.ControlText : SystemColors.GrayText;
            return;
        }
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 1;
        button.UseVisualStyleBackColor = false;
        button.Cursor = button.Enabled ? Cursors.Hand : Cursors.Default;

        switch (role)
        {
            case UiButtonRole.Primary:
                button.BackColor = Accent;
                button.ForeColor = Color.White;
                button.FlatAppearance.BorderColor = Accent;
                button.FlatAppearance.MouseOverBackColor = AccentHover;
                button.FlatAppearance.MouseDownBackColor = AccentHover;
                break;

            case UiButtonRole.Subtle:
                button.BackColor = SurfaceMuted;
                button.ForeColor = TextSecondary;
                button.FlatAppearance.BorderColor = Border;
                button.FlatAppearance.MouseOverBackColor = AccentSoft;
                button.FlatAppearance.MouseDownBackColor = AccentSoft;
                break;

            case UiButtonRole.Danger:
                button.BackColor = DangerSoft;
                button.ForeColor = Danger;
                button.FlatAppearance.BorderColor = Color.FromArgb(231, 188, 188);
                button.FlatAppearance.MouseOverBackColor = Color.FromArgb(248, 221, 221);
                button.FlatAppearance.MouseDownBackColor = Color.FromArgb(248, 221, 221);
                break;

            default:
                button.BackColor = Surface;
                button.ForeColor = TextPrimary;
                button.FlatAppearance.BorderColor = BorderStrong;
                button.FlatAppearance.MouseOverBackColor = SurfaceMuted;
                button.FlatAppearance.MouseDownBackColor = AccentSoft;
                break;
        }

        if (!button.Enabled)
        {
            button.BackColor = SurfaceMuted;
            button.ForeColor = Disabled;
            button.FlatAppearance.BorderColor = Border;
            button.Cursor = Cursors.Default;
        }
    }

    public static void ApplyGrid(DataGridView grid)
    {
        grid.BackgroundColor = Surface;
        grid.BorderStyle = BorderStyle.FixedSingle;
        grid.GridColor = Border;
        grid.EnableHeadersVisualStyles = false;
        grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
        grid.ColumnHeadersDefaultCellStyle.BackColor = SurfaceMuted;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = TextPrimary;
        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = SurfaceMuted;
        grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = TextPrimary;
        grid.DefaultCellStyle.BackColor = Surface;
        grid.DefaultCellStyle.ForeColor = TextPrimary;
        grid.DefaultCellStyle.SelectionBackColor = AccentSoft;
        grid.DefaultCellStyle.SelectionForeColor = TextPrimary;
        grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(250, 251, 252);
        grid.RowHeadersDefaultCellStyle.BackColor = SurfaceMuted;
        grid.RowHeadersDefaultCellStyle.ForeColor = TextSecondary;
        grid.ColumnHeadersHeight = Math.Max(grid.ColumnHeadersHeight, 34);
        grid.RowTemplate.Height = Math.Max(grid.RowTemplate.Height, 30);
        if (SystemInformation.HighContrast)
        {
            grid.EnableHeadersVisualStyles = true;
            grid.BackgroundColor = SystemColors.Window;
            grid.DefaultCellStyle.BackColor = SystemColors.Window;
            grid.DefaultCellStyle.ForeColor = SystemColors.WindowText;
            grid.DefaultCellStyle.SelectionBackColor = SystemColors.Highlight;
            grid.DefaultCellStyle.SelectionForeColor = SystemColors.HighlightText;
            grid.AlternatingRowsDefaultCellStyle.BackColor = SystemColors.Window;
        }
    }
}
