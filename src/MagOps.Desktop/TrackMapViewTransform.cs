namespace MagOps.Desktop;

/// <summary>Immutable display transform shared by painting, highlights and hit queries.</summary>
internal readonly record struct TrackMapViewTransform(
    Size Viewport, double CentreX, double CentreY, double Scale, double Zoom, PointF Offset)
{
    internal static TrackMapViewTransform Create(Size viewport, double minX, double maxX,
        double minY, double maxY, double zoom, PointF offset) => new(viewport,
            (minX + maxX) / 2, (minY + maxY) / 2,
            Math.Min(Math.Max(1, viewport.Width - 64) / Math.Max(1, maxX - minX),
                Math.Max(1, viewport.Height - 94) / Math.Max(1, maxY - minY)), zoom, offset);

    internal PointF ToScreen((double X, double Y) point)
    {
        // Preserve the existing PointF rounding at the navigation boundary.
        var fitted = new PointF(Viewport.Width / 2f + (float)((point.X - CentreX) * Scale),
            Viewport.Height / 2f + 15 - (float)((point.Y - CentreY) * Scale));
        return new((float)(fitted.X * Zoom + Offset.X), (float)(fitted.Y * Zoom + Offset.Y));
    }

    internal PointF ToRenderScreen((double X, double Y) point) => new(
        (float)((Viewport.Width / 2f + (point.X - CentreX) * Scale) * Zoom + Offset.X),
        (float)((Viewport.Height / 2f + 15 - (point.Y - CentreY) * Scale) * Zoom + Offset.Y));

    internal (double X, double Y) ToProjected(PointF screen) => (
        CentreX + ((screen.X - Offset.X) / Zoom - Viewport.Width / 2f) / Scale,
        CentreY - ((screen.Y - Offset.Y) / Zoom - Viewport.Height / 2f - 15) / Scale);
}
