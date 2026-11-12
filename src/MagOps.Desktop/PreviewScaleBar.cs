namespace MagOps.Desktop;

/// <summary>
/// Draws a compact metric scale reference for route/spatial preview panels.
/// The caller supplies real-world metres represented by one screen pixel.
/// </summary>
internal static class PreviewScaleBar
{
    public static void Draw(
        Graphics graphics,
        Control owner,
        double metersPerPixel)
    {
        if (!double.IsFinite(metersPerPixel) || metersPerPixel <= 0d ||
            owner.ClientSize.Width < 120 || owner.ClientSize.Height < 70)
        {
            return;
        }

        float uiScale = Math.Max(1f, owner.DeviceDpi / 96f);
        float targetPixels = 120f * uiScale;
        double targetMeters = targetPixels * metersPerPixel;
        double distanceMeters = NiceFloor(targetMeters);
        if (!double.IsFinite(distanceMeters) || distanceMeters <= 0d)
            return;

        float barPixels = (float)(distanceMeters / metersPerPixel);
        if (!float.IsFinite(barPixels) || barPixels < 24f)
            return;

        barPixels = Math.Min(
            barPixels,
            Math.Max(40f * uiScale, owner.ClientSize.Width - 46f * uiScale));
        float left = 14f * uiScale;
        float baseline = owner.ClientSize.Height - 24f * uiScale;
        float tick = 5f * uiScale;

        using var font = new Font(
            owner.Font.FontFamily,
            Math.Max(7f, owner.Font.Size - 1f),
            FontStyle.Regular,
            GraphicsUnit.Point);

        string endLabel = FormatDistance(distanceMeters);
        Size endSize = TextRenderer.MeasureText(
            graphics,
            endLabel,
            font,
            Size.Empty,
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        var background = new RectangleF(
            left - 6f * uiScale,
            baseline - 22f * uiScale,
            Math.Min(
                owner.ClientSize.Width - left + 2f * uiScale,
                barPixels + endSize.Width + 18f * uiScale),
            34f * uiScale);

        using var backgroundBrush = new SolidBrush(SystemColors.Window);
        using var borderPen = new Pen(SystemColors.ControlLight);
        graphics.FillRectangle(backgroundBrush, background);
        graphics.DrawRectangle(
            borderPen,
            background.X,
            background.Y,
            background.Width,
            background.Height);

        using var pen = new Pen(SystemColors.ControlText, 1.4f * uiScale);
        graphics.DrawLine(pen, left, baseline, left + barPixels, baseline);
        graphics.DrawLine(pen, left, baseline - tick, left, baseline + tick);
        graphics.DrawLine(
            pen,
            left + barPixels / 2f,
            baseline - tick + 1f,
            left + barPixels / 2f,
            baseline + tick - 1f);
        graphics.DrawLine(
            pen,
            left + barPixels,
            baseline - tick,
            left + barPixels,
            baseline + tick);

        TextRenderer.DrawText(
            graphics,
            "0",
            font,
            new Point((int)left - (int)(2f * uiScale), (int)baseline - (int)(21f * uiScale)),
            SystemColors.ControlText,
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        if (barPixels >= 90f * uiScale)
        {
            string middleLabel = FormatDistance(distanceMeters / 2d);
            Size middleSize = TextRenderer.MeasureText(
                graphics,
                middleLabel,
                font,
                Size.Empty,
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            TextRenderer.DrawText(
                graphics,
                middleLabel,
                font,
                new Point(
                    Math.Max(0, (int)(left + barPixels / 2f - middleSize.Width / 2f)),
                    (int)baseline - (int)(21f * uiScale)),
                SystemColors.GrayText,
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        }

        TextRenderer.DrawText(
            graphics,
            endLabel,
            font,
            new Point(
                Math.Max(0, (int)(left + barPixels - endSize.Width / 2f)),
                (int)baseline - (int)(21f * uiScale)),
            SystemColors.ControlText,
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
    }

    private static double NiceFloor(double value)
    {
        if (!double.IsFinite(value) || value <= 0d)
            return 0d;

        double power = Math.Pow(10d, Math.Floor(Math.Log10(value)));
        double normalized = value / power;
        double factor = normalized >= 5d
            ? 5d
            : normalized >= 2d
                ? 2d
                : 1d;
        return factor * power;
    }

    private static string FormatDistance(double meters)
    {
        if (meters >= 1000d)
        {
            double km = meters / 1000d;
            return km >= 10d
                ? $"{km:0} km"
                : $"{km:0.#} km";
        }

        if (meters >= 1d)
            return $"{meters:0.#} m";

        return $"{meters * 100d:0.#} cm";
    }
}
