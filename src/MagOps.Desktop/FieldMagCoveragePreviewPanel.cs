using System.ComponentModel;
using System.Drawing.Drawing2D;
using MagOps.Infrastructure.Services;

namespace MagOps.Desktop;

internal sealed class FieldMagCoveragePreviewPanel : Control
{
    private FieldMagDailyCoverageSummary? _summary;
    private readonly PreviewNavigation _navigation;

    public FieldMagCoveragePreviewPanel()
    {
        DoubleBuffered = true;
        BackColor = UiTheme.Surface;
        ForeColor = UiTheme.TextPrimary;
        SetStyle(ControlStyles.Selectable, true);
        MinimumSize = new Size(0, Math.Max(74, 62 + Font.Height));
        Height = Math.Max(78, MinimumSize.Height);
        Margin = new Padding(0, 4, 0, 0);
        _navigation = new PreviewNavigation(this, horizontalOnly: true);
        AccessibleName = "日覆盖时间轴";
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public FieldMagDailyCoverageSummary? Summary
    {
        get => _summary;
        set
        {
            _summary = value;
            _navigation.SetDataKey(value is null ? null : new
            {
                value.BeijingDate,
                Segments = string.Join("|", value.Segments.Select(s =>
                    $"{s.StartUtc.UtcDateTime.Ticks}:{s.EndUtc.UtcDateTime.Ticks}:{s.Kind}:{s.Reason}"))
            });
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var focusCue = _navigation.BeginPaint(e.Graphics);
        Graphics g = e.Graphics;
        g.Clear(BackColor);

        using var borderPen = new Pen(UiTheme.Border);
        g.DrawRectangle(borderPen, 0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));

        if (_summary is null || !_summary.HasUav)
        {
            TextRenderer.DrawText(
                g,
                "所选作业日暂无可检查的 UAV 磁测记录。",
                Font,
                new Rectangle(8, 8, Math.Max(0, Width - 16), Math.Max(0, Height - 16)),
                UiTheme.TextSecondary,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            return;
        }

        const int labelWidth = 54;
        int left = labelWidth;
        int right = Math.Max(left + 20, Width - 8);
        int laneHeight = 12;
        int uavY = 16;
        int baseY = 42;

        DrawLaneBackground(g, left, right, uavY, laneHeight);
        DrawLaneBackground(g, left, right, baseY, laneHeight);

        DateTimeOffset startUtc = FieldMagCoverageService.BeijingDayStartUtc(_summary.BeijingDate);
        DateTimeOffset endUtc = startUtc.AddDays(1);
        PointF Map(DateTimeOffset time, float y) => _navigation.Apply(new(
            (float)(left + (time - startUtc).TotalSeconds / TimeSpan.FromDays(1).TotalSeconds * (right - left)), y));
        PointF visibleStart = _navigation.Unapply(new(left, 0));
        PointF visibleEnd = _navigation.Unapply(new(right, 0));
        GraphicsState clip = g.Save();
        g.SetClip(new RectangleF(left, 8, right - left, Math.Max(1, Height - 8)));

        foreach (FieldMagCoverageSegment segment in _summary.Segments)
        {
            int y;
            Color color;
            switch (segment.Kind)
            {
                case FieldMagCoverageSegmentKind.UavCovered:
                    y = uavY;
                    color = UiTheme.Success;
                    break;
                case FieldMagCoverageSegmentKind.UavUncovered:
                    y = uavY;
                    color = UiTheme.Danger;
                    break;
                case FieldMagCoverageSegmentKind.BaseValidCoverage:
                    y = baseY;
                    color = UiTheme.Accent;
                    break;
                case FieldMagCoverageSegmentKind.BaseRejected:
                    y = baseY;
                    color = UiTheme.Warning;
                    break;
                default:
                    continue;
            }

            DateTimeOffset clippedStart = segment.StartUtc < startUtc ? startUtc : segment.StartUtc;
            DateTimeOffset clippedEnd = segment.EndUtc >= endUtc ? endUtc.AddTicks(-1) : segment.EndUtc;
            if (clippedEnd < startUtc || clippedStart >= endUtc || clippedEnd < clippedStart)
                continue;

            int x1 = (int)Math.Round(Map(clippedStart, y).X);
            int x2 = (int)Math.Round(Map(clippedEnd, y).X);
            if (x2 < x1) (x1, x2) = (x2, x1);
            if (x2 <= x1)
                x2 = x1 + 2;
            if (x2 <= left || x1 >= right) continue;
            x1 = Math.Clamp(x1, left, right - 1);
            x2 = Math.Clamp(x2, x1 + 1, right);

            using var brush = new SolidBrush(color);
            g.FillRectangle(brush, x1, y, x2 - x1, laneHeight);
        }
        g.Restore(clip);

        TextRenderer.DrawText(g, "UAV", Font, new Point(8, uavY - 2), UiTheme.TextSecondary);
        TextRenderer.DrawText(g, "19T", Font, new Point(8, baseY - 2), UiTheme.TextSecondary);

        // Label the visible interval, rather than leaving all day ticks off-screen after zoom.
        double firstSeconds = (visibleStart.X - left) / (right - left) * 86400d;
        double lastSeconds = (visibleEnd.X - left) / (right - left) * 86400d;
        for (int i = 0; i < 5; i++)
        {
            int x = left + (int)Math.Round((right - left) * i / 4d);
            double seconds = firstSeconds + (lastSeconds - firstSeconds) * i / 4d;
            string tick = seconds < 0 || seconds > 86400 ? "" : seconds == 86400 ? "24:00" :
                TimeSpan.FromSeconds(seconds).ToString(@"hh\:mm");
            using var tickPen = new Pen(UiTheme.BorderStrong);
            g.DrawLine(tickPen, x, baseY + laneHeight + 1, x, baseY + laneHeight + 4);
            int textWidth = TextRenderer.MeasureText(g, tick, Font, Size.Empty, TextFormatFlags.NoPadding).Width + 4;
            int textLeft = Math.Clamp(x - textWidth / 2, 0, Math.Max(0, Width - textWidth));
            TextRenderer.DrawText(
                g,
                tick,
                Font,
                new Rectangle(textLeft, baseY + laneHeight + 4, textWidth, Font.Height),
                UiTheme.TextSecondary,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
        }
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        MinimumSize = new Size(0, Math.Max(74, 62 + Font.Height));
        Height = Math.Max(Height, MinimumSize.Height);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _navigation.Dispose();
        base.Dispose(disposing);
    }

    private static void DrawLaneBackground(
        Graphics g,
        int left,
        int right,
        int y,
        int height)
    {
        using var brush = new SolidBrush(UiTheme.SurfaceMuted);
        using var pen = new Pen(UiTheme.Border);
        g.FillRectangle(brush, left, y, Math.Max(1, right - left), height);
        g.DrawRectangle(pen, left, y, Math.Max(1, right - left), height);
    }
}
