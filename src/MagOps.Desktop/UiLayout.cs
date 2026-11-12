using System.ComponentModel;
using System.Text;
using MagOps.Infrastructure.Services;

namespace MagOps.Desktop;

/// <summary>
/// Central WinForms layout policy.
/// The project previously accumulated several one-off fixes for DPI/AutoSize overlap.
/// This class keeps the invariants in one place and is called after startup, resize and DPI changes.
/// </summary>
internal static class UiLayout
{
    public static void Normalize(Control root)
    {
        if (root is Form form)
            FitFormToWorkingArea(form);

        NormalizeRecursive(root);
        root.PerformLayout();
    }

    private static void NormalizeRecursive(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            if (string.IsNullOrWhiteSpace(child.AccessibleName))
            {
                string accessible = (child.Text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
                if (accessible.Length > 0 &&
                    child is Button or CheckBox or RadioButton or GroupBox or TabPage or Label)
                {
                    child.AccessibleName = accessible;
                }
            }

            if (child is TabControl tabs)
            {
                if (!Equals(tabs.Tag, "AllowMultilineTabs")) tabs.Multiline = false;
                if (tabs.Dock == DockStyle.None)
                    tabs.Dock = DockStyle.Fill;
            }

            if (child is Button button)
            {
                Size preferred = button.GetPreferredSize(Size.Empty);
                int safeHeight = Math.Max(
                    preferred.Height,
                    TextRenderer.MeasureText(
                        button.Text ?? "",
                        button.Font,
                        new Size(int.MaxValue, int.MaxValue),
                        TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Height + 12);
                if (button.MinimumSize.Height < safeHeight)
                    button.MinimumSize = new Size(button.MinimumSize.Width, safeHeight);
                if (!button.AutoSize && button.Height < safeHeight)
                    button.Height = safeHeight;
            }

            if (child is RadioButton radio && radio.Appearance == Appearance.Button)
            {
                Size preferred = radio.GetPreferredSize(Size.Empty);
                int safeHeight = Math.Max(
                    preferred.Height,
                    TextRenderer.MeasureText(
                        radio.Text ?? "",
                        radio.Font,
                        new Size(int.MaxValue, int.MaxValue),
                        TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Height + 12);
                if (radio.MinimumSize.Height < safeHeight)
                    radio.MinimumSize = new Size(radio.MinimumSize.Width, safeHeight);
            }

            if (child is DataGridView grid)
            {
                grid.Dock = DockStyle.Fill;
                grid.RowHeadersVisible = false;
                grid.ShowCellToolTips = true;
                grid.AllowUserToResizeColumns = true;
                grid.AllowUserToResizeRows = false;
                grid.DefaultCellStyle.WrapMode = DataGridViewTriState.False;
                grid.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.False;
            }

            if (child is FlowLayoutPanel flow && !flow.WrapContents)
                flow.AutoScroll = true;

            // A frequent historical overlap cause was AutoSize labels inside
            // TableLayoutPanel cells: rendered text could extend into adjacent cells
            // after DPI scaling. Single-line docked labels are forced to stay inside
            // their assigned cell and use ellipsis when needed.
            if (child is Label label &&
                parent is TableLayoutPanel &&
                label.Dock != DockStyle.None &&
                !label.Text.Contains('\n') &&
                !label.Text.Contains('\r') &&
                label.MaximumSize.Width == 0)
            {
                label.AutoSize = false;
                label.AutoEllipsis = true;
            }

            NormalizeRecursive(child);
        }
    }

    public static IReadOnlyList<string> Audit(Control root)
    {
        var issues = new List<string>();
        AuditRecursive(root, issues, root.Name.Length > 0 ? root.Name : root.GetType().Name);
        return issues;
    }

    private static void AuditRecursive(Control parent, List<string> issues, string path)
    {
        bool parentAutoScroll = parent is ScrollableControl scrollable && scrollable.AutoScroll;
        if (parent.ClientSize.Width > 0 && parent.ClientSize.Height > 0 && !parentAutoScroll)
        {
            foreach (Control child in parent.Controls)
            {
                if (!child.Visible || child.Dock == DockStyle.Fill)
                    continue;

                // A small tolerance avoids false positives caused by borders.
                if (child.Left < -4 || child.Top < -4 ||
                    child.Right > parent.ClientSize.Width + 4 ||
                    child.Bottom > parent.ClientSize.Height + 4)
                {
                    issues.Add($"{path}/{Describe(child)}: 控件超出父容器可视区域");
                }
            }
        }

        foreach (Control child in parent.Controls)
        {
            if (child is Button button)
            {
                int preferredHeight = button.GetPreferredSize(Size.Empty).Height;
                if (button.Height + 2 < preferredHeight)
                    issues.Add($"{path}/{Describe(button)}: 按钮高度不足，可能裁切文字");
            }
            else if (child is RadioButton radio && radio.Appearance == Appearance.Button)
            {
                int preferredHeight = radio.GetPreferredSize(Size.Empty).Height;
                if (radio.Height + 2 < preferredHeight)
                    issues.Add($"{path}/{Describe(radio)}: 工区按钮高度不足，可能裁切文字");
            }
        }

        if (parent is TableLayoutPanel table)
        {
            foreach (Control child in table.Controls)
            {
                if (child is not Label label || !label.Visible)
                    continue;

                Size measured = TextRenderer.MeasureText(
                    label.Text ?? "",
                    label.Font,
                    new Size(int.MaxValue, int.MaxValue),
                    TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);

                bool singleLine = !(label.Text?.Contains('\n') ?? false) &&
                                  !(label.Text?.Contains('\r') ?? false);
                if (singleLine && measured.Width > Math.Max(1, label.ClientSize.Width) + 8 &&
                    !label.AutoEllipsis && label.AutoSize)
                {
                    issues.Add($"{path}/{Describe(label)}: 单行标签可能越界，PreferredWidth={measured.Width}, Width={label.Width}");
                }
            }
        }

        foreach (Control child in parent.Controls)
            AuditRecursive(child, issues, $"{path}/{Describe(child)}");
    }

    public static void AuditAndLog(Control root, string? projectRoot, string context)
    {
        IReadOnlyList<string> issues = Audit(root);
        if (issues.Count == 0)
            return;

        LogService.LogMessage(
            projectRoot,
            context,
            "UI 布局审计发现潜在裁切/遮挡：\r\n" + string.Join("\r\n", issues.Take(80)));
    }


    /// <summary>
    /// Hotfix guard for small displays / high DPI: no form minimum or normal-state
    /// size may exceed the current monitor working area. Design-time preferred
    /// sizes remain intact when the monitor has enough room.
    /// </summary>
    public static void FitFormToWorkingArea(Form form, IWin32Window? owner = null)
    {
        Screen screen = owner is Control ownerControl
            ? Screen.FromControl(ownerControl)
            : form.IsHandleCreated
                ? Screen.FromControl(form)
                : Screen.FromPoint(Cursor.Position);

        Rectangle area = screen.WorkingArea;
        int maxWidth = Math.Max(320, area.Width - 24);
        int maxHeight = Math.Max(240, area.Height - 48);

        form.MinimumSize = new Size(
            Math.Min(form.MinimumSize.Width, maxWidth),
            Math.Min(form.MinimumSize.Height, maxHeight));

        if (form.WindowState != FormWindowState.Normal)
            return;

        int width = Math.Clamp(form.Width, Math.Max(1, form.MinimumSize.Width), maxWidth);
        int height = Math.Clamp(form.Height, Math.Max(1, form.MinimumSize.Height), maxHeight);
        if (width != form.Width || height != form.Height)
            form.Size = new Size(width, height);
    }

    public static void SizeDialogToOwner(
        Form form,
        IWin32Window? owner,
        double widthRatio,
        double heightRatio,
        Size minimum,
        Size? maximum = null)
    {
        Screen screen = owner is Control control
            ? Screen.FromControl(control)
            : Screen.FromPoint(Cursor.Position);

        Rectangle area = screen.WorkingArea;
        int screenMaxWidth = Math.Max(420, area.Width - 40);
        int screenMaxHeight = Math.Max(320, area.Height - 60);
        int effectiveMinWidth = Math.Min(minimum.Width, screenMaxWidth);
        int effectiveMinHeight = Math.Min(minimum.Height, screenMaxHeight);

        int width = Math.Max(
            effectiveMinWidth,
            (int)Math.Round(area.Width * widthRatio));
        int height = Math.Max(
            effectiveMinHeight,
            (int)Math.Round(area.Height * heightRatio));

        if (maximum.HasValue)
        {
            width = Math.Min(width, maximum.Value.Width);
            height = Math.Min(height, maximum.Value.Height);
        }

        width = Math.Clamp(width, effectiveMinWidth, screenMaxWidth);
        height = Math.Clamp(height, effectiveMinHeight, screenMaxHeight);

        if (form.MinimumSize.Width > screenMaxWidth ||
            form.MinimumSize.Height > screenMaxHeight)
        {
            form.MinimumSize = new Size(
                Math.Min(form.MinimumSize.Width, screenMaxWidth),
                Math.Min(form.MinimumSize.Height, screenMaxHeight));
        }

        form.Size = new Size(width, height);
    }

    public static void ApplyFillWeights(
        DataGridView grid,
        IReadOnlyDictionary<string, float> weights,
        int minimumWidth = 70)
    {
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        foreach (DataGridViewColumn column in grid.Columns)
        {
            if (!column.Visible)
                continue;

            column.MinimumWidth = minimumWidth;
            column.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            column.FillWeight = weights.TryGetValue(column.DataPropertyName, out float weight)
                ? Math.Max(10, weight)
                : 60;
        }
    }

    public static void ApplyArtifactCommonView(DataGridView grid, bool full)
    {
        var common = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Path", "Name", "Role", "ProcessingStage", "Selected",
            "QualityStatus", "ChangeStatus", "ModifiedAt"
        };

        foreach (DataGridViewColumn column in grid.Columns)
        {
            // Columns intentionally hidden by GridHeaders stay hidden.
            if (column.DataPropertyName is
                "ArtifactId" or "WorkareaRef" or "WorkareaAssignmentStatus" or
                "PreviousPaths" or "SourceKind" or "SourceRef" or "Sha256" or
                "VersionFamily" or "DerivedFrom" or "ExternalOrigin" or
                "LineageRelation" or "MetadataOrigin" or "ScanStatus" or
                "LastScannedAt" or "Extra")
                continue;

            column.Visible = full || common.Contains(column.DataPropertyName);
        }

        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        var weights = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
        {
            ["Path"] = 240,
            ["Name"] = 150,
            ["Role"] = 85,
            ["ProcessingStage"] = 85,
            ["Selected"] = 50,
            ["QualityStatus"] = 75,
            ["ChangeStatus"] = 85,
            ["ModifiedAt"] = 100,
            ["VersionLabel"] = 60,
            ["HashStatus"] = 70,
            ["SizeBytes"] = 65,
            ["Notes"] = 120
        };

        foreach (DataGridViewColumn column in grid.Columns)
        {
            if (!column.Visible)
                continue;
            column.MinimumWidth = column.DataPropertyName == "Path" ? 180 : 65;
            column.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            column.FillWeight = weights.TryGetValue(column.DataPropertyName, out float weight)
                ? weight
                : 70;
        }
    }

    private static string Describe(Control control)
    {
        string name = string.IsNullOrWhiteSpace(control.Name) ? control.GetType().Name : control.Name;
        string text = (control.Text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        if (text.Length > 24)
            text = text[..24] + "…";
        return text.Length == 0 ? name : $"{name}[{text}]";
    }
}

internal sealed class EmptyStateDataGridView : DataGridView
{
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string EmptyText { get; set; } = "暂无数据";

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (Rows.Count > 0 || string.IsNullOrWhiteSpace(EmptyText))
            return;

        Rectangle rect = ClientRectangle;
        rect.Inflate(-24, -24);
        TextRenderer.DrawText(
            e.Graphics,
            EmptyText,
            Font,
            rect,
            SystemColors.GrayText,
            TextFormatFlags.HorizontalCenter |
            TextFormatFlags.VerticalCenter |
            TextFormatFlags.WordBreak |
            TextFormatFlags.NoPrefix);
    }
}
