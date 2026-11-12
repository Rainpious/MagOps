using System.Runtime.CompilerServices;

namespace MagOps.Desktop;

/// <summary>
/// Small reusable WinForms component helpers. These intentionally wrap native
/// controls instead of introducing a third-party UI framework.
/// </summary>
internal static class UiComponents
{
    private sealed class RoleHolder
    {
        public RoleHolder(UiButtonRole role)
        {
            Role = role;
        }

        public UiButtonRole Role { get; }
    }

    private static readonly ConditionalWeakTable<Button, RoleHolder> ButtonRoles = new();

    public static Button Button(
        string text,
        EventHandler click,
        UiButtonRole role = UiButtonRole.Neutral,
        int minimumWidth = 0,
        int minimumHeight = 34)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(minimumWidth, minimumHeight),
            Padding = new Padding(10, 2, 10, 2),
            Margin = new Padding(4, 2, 4, 2)
        };
        button.Click += click;
        SetButtonRole(button, role);
        return button;
    }

    public static void SetButtonRole(Button button, UiButtonRole role)
    {
        ButtonRoles.Remove(button);
        ButtonRoles.Add(button, new RoleHolder(role));
        UiTheme.ApplyButton(button, role);
    }

    public static UiButtonRole GetButtonRole(Button button) =>
        ButtonRoles.TryGetValue(button, out RoleHolder? holder) && holder is not null
            ? holder.Role
            : UiButtonRole.Neutral;


    public static Panel Card(Padding? padding = null) => new()
    {
        Dock = DockStyle.Fill,
        BackColor = UiTheme.Surface,
        BorderStyle = BorderStyle.FixedSingle,
        Padding = padding ?? new Padding(12),
        Margin = Padding.Empty
    };

    public static Control StatTile(string caption, Label value, string hint = "")
    {
        var tile = new Panel
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            AutoScroll = false,
            MinimumSize = new Size(0, 70),
            BackColor = UiTheme.SurfaceMuted,
            BorderStyle = BorderStyle.FixedSingle,
            Padding = new Padding(10, 7, 10, 7),
            Margin = Padding.Empty
        };

        bool hasHint =
            !string.IsNullOrWhiteSpace(
                hint);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            AutoScroll = false,
            ColumnCount = 1,
            RowCount = 3,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = UiTheme.SurfaceMuted
        };
        layout.ColumnStyles.Add(
            new ColumnStyle(
                SizeType.Percent,
                100));
        if (hasHint)
        {
            layout.RowStyles.Add(
                new RowStyle(
                    SizeType.Percent,
                    24));
            layout.RowStyles.Add(
                new RowStyle(
                    SizeType.Percent,
                    50));
            layout.RowStyles.Add(
                new RowStyle(
                    SizeType.Percent,
                    26));
        }
        else
        {
            layout.RowStyles.Add(
                new RowStyle(
                    SizeType.Percent,
                    34));
            layout.RowStyles.Add(
                new RowStyle(
                    SizeType.Percent,
                    66));
            layout.RowStyles.Add(
                new RowStyle(
                    SizeType.Absolute,
                    0));
        }

        var captionLabel = new Label
        {
            Text = caption,
            Dock = DockStyle.Fill,
            AutoSize = false,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = UiTheme.TextSecondary,
            Margin = Padding.Empty
        };
        layout.Controls.Add(
            captionLabel,
            0,
            0);

        value.Dock = DockStyle.Fill;
        value.AutoSize = false;
        value.AutoEllipsis = true;
        value.MinimumSize = Size.Empty;
        value.TextAlign =
            ContentAlignment.MiddleLeft;
        value.Font = new Font(
            SystemFonts.MessageBoxFont?.FontFamily ??
                FontFamily.GenericSansSerif,
            15F,
            FontStyle.Bold);
        value.ForeColor =
            UiTheme.TextPrimary;
        value.Margin =
            Padding.Empty;
        layout.Controls.Add(
            value,
            0,
            1);

        if (hasHint)
        {
            var hintLabel = new Label
            {
                Text = hint,
                Dock = DockStyle.Fill,
                AutoSize = false,
                AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = UiTheme.TextSecondary,
                Margin = Padding.Empty
            };
            layout.Controls.Add(
                hintLabel,
                0,
                2);
        }

        tile.Controls.Add(layout);
        return tile;
    }

    public static void ApplyStatusBadge(Label label, string status)
    {
        string normalized = (status ?? "").Trim().ToUpperInvariant();
        (Color background, Color foreground) = normalized switch
        {
            "PASSED" or "DONE" or "COMPLETED" or "RELEASED" or "ACTIVE"
                => (UiTheme.SuccessSoft, UiTheme.Success),
            "BLOCKED" or "FAILED"
                => (UiTheme.DangerSoft, UiTheme.Danger),
            "REVIEW_REQUIRED" or "PAUSED" or "DEFERRED" or "IN_PROGRESS"
                => (UiTheme.WarningSoft, UiTheme.Warning),
            _ => (UiTheme.SurfaceMuted, UiTheme.TextSecondary)
        };

        label.BackColor = background;
        label.ForeColor = foreground;
        label.Padding = new Padding(8, 3, 8, 3);
        label.Margin = Padding.Empty;
        label.TextAlign = ContentAlignment.MiddleCenter;
    }

    public static Control PageHeader(string title, string description)
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = new Padding(2, 2, 2, 8),
            BackColor = UiTheme.Workspace
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        layout.Controls.Add(new Label
        {
            Text = title,
            AutoSize = true,
            Font = new Font(SystemFonts.MessageBoxFont?.FontFamily ?? FontFamily.GenericSansSerif, 16F, FontStyle.Bold),
            ForeColor = UiTheme.TextPrimary,
            Margin = Padding.Empty
        }, 0, 0);

        layout.Controls.Add(new Label
        {
            Text = description,
            AutoSize = true,
            ForeColor = UiTheme.TextSecondary,
            Margin = new Padding(0, 3, 0, 0)
        }, 0, 1);

        return layout;
    }

    public static Label SectionLabel(string step, string title) => new()
    {
        Text = $"{step}  {title}",
        AutoSize = true,
        Font = new Font(SystemFonts.MessageBoxFont?.FontFamily ?? FontFamily.GenericSansSerif, 10F, FontStyle.Bold),
        ForeColor = UiTheme.TextPrimary,
        Margin = new Padding(0, 0, 0, 6)
    };

    public static Label HelpLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = UiTheme.TextSecondary,
        Margin = new Padding(4, 4, 4, 4)
    };
}
