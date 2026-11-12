using System.Drawing.Drawing2D;
using static MagOps.Desktop.TrackMapPalette;

namespace MagOps.Desktop;

internal static partial class TrackMapRenderer
{
    internal static float DrawLegend(Graphics graphics, TrackMapDisplayState display,
        Size viewport, Font font, int deviceDpi, int includedCount, int candidateCount,
        int selectedCount, bool hasExecution, bool hasReferenceGeometry, bool motherRoles = false)
    {
        int Width = viewport.Width;
        var Font = font;
        var items = new List<(string Label, Color Color, DashStyle Dash, float Width)>
        {
            ($"已纳入 {includedCount}段", IncludedColor, DashStyle.Solid, IncludedWidth),
            ($"作业候选 {candidateCount}段", CandidateColor, DashStyle.Solid, CandidateWidth),
            ("重合候选", CandidateColor, DashStyle.Dash, 1.5f),
            (selectedCount > 1 ? $"选中描边（{selectedCount}段）" : "选中描边", SelectedColor, DashStyle.Solid, 2f)
        };
        if (motherRoles)
        {
            items.Clear();
            items.Add(("附近查看", Color.MediumVioletRed, DashStyle.Solid, 3f));
            items.Add(("源段", Color.OrangeRed, DashStyle.Solid, 3f));
            items.Add(("目标母线", Color.DarkCyan, DashStyle.Solid, 3f));
            items.Add(("断开测线", Color.ForestGreen, DashStyle.Solid, 3f));
            items.Add(("连接处碎段", Color.DarkOrange, DashStyle.Solid, 3f));
            items.Add(("端部延伸", Color.SteelBlue, DashStyle.Solid, 3f));
            items.Add(("疑似重复", Color.BlueViolet, DashStyle.Solid, 3f));
            items.Add(("疑似冲突", Color.Crimson, DashStyle.Solid, 3f));
            items.Add(("附近参考（悬停查看原线号）", Color.FromArgb(160, 160, 178), DashStyle.Dash, 2f));
        }
        if (!motherRoles && display.ShowRawTrack && !display.OnlyCandidates) items.Add(("原始观测背景", Color.FromArgb(215, 220, 224), DashStyle.Solid, .7f));
        if (!motherRoles && display.ShowConnectors && !display.OnlyCandidates) items.Add(("连接 / 往返", Color.FromArgb(174, 137, 91), DashStyle.DashDot, AuxiliaryWidth));
        if (!motherRoles && display.ShowAuxiliary && !display.OnlyCandidates)
        {
            items.Add(("未归属", Color.FromArgb(175, 180, 185), DashStyle.Solid, AuxiliaryWidth));
            items.Add(("待复核", Color.FromArgb(195, 168, 186), DashStyle.Dash, AuxiliaryWidth));
            items.Add(("归属冲突", Color.FromArgb(195, 168, 186), DashStyle.Dot, AuxiliaryWidth));
        }
        if (!motherRoles && display.ShowDesignLines && !display.OnlyCandidates)
        {
            items.Add(("参考线", ReferenceColor, DashStyle.Dash, .6f));
            if (hasExecution && hasReferenceGeometry)
                items.Add(("参考线未覆盖", MissingColor, DashStyle.Dash, 1.6f));
        }
        float dpi = deviceDpi / 96f;
        float padding = 8 * dpi, sample = 24 * dpi, gap = 6 * dpi;
        float rowHeight = Math.Max(Font.Height + 8 * dpi, 24 * dpi);
        float x = padding, y = padding;
        var placed = new List<(string Label, Color Color, DashStyle Dash, float Width, float X, float Y)>();
        foreach (var item in items)
        {
            float width = sample + gap + graphics.MeasureString(item.Label, Font).Width + 16 * dpi;
            if (x > padding && x + width > Width - padding) { x = padding; y += rowHeight; }
            placed.Add((item.Label, item.Color, item.Dash, item.Width, x, y));
            x += width;
        }
        using var background = new SolidBrush(Color.FromArgb(242, Color.White));
        graphics.FillRectangle(background, 0, 0, Width, y + rowHeight);
        float legendHeight = y + rowHeight;
        foreach (var item in placed)
        {
            using var pen = new Pen(item.Color, item.Width) { DashStyle = item.Dash };
            float middle = item.Y + rowHeight / 2;
            graphics.DrawLine(pen, item.X, middle, item.X + sample, middle);
            graphics.DrawString(item.Label, Font, Brushes.DimGray, item.X + sample + gap,
                item.Y + (rowHeight - Font.Height) / 2);
        }
        return legendHeight;
    }
}
