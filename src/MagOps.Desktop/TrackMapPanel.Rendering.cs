using static MagOps.Desktop.TrackMapPalette;
using System.Drawing.Drawing2D;
using MagOps.Core.Domain;
using MagOps.Infrastructure.Services;

namespace MagOps.Desktop;

internal sealed partial class TrackMapPanel
{
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var focusCue = _navigation.BeginPaint(e.Graphics);
        if (!_geometry.HasGeometry || Width <= 0 || Height <= 0) return;
        var view = CurrentViewTransform();
        double renderZoom = _navigation.Zoom;
        PointF renderOffset = _navigation.Apply(PointF.Empty);
        int renderWidth = Width, renderHeight = Height;
        float detailPixels = .5f;
        var overviewKey = (_mapRevision, ClientSize);
        if (_imageCache.Overview is null || _imageCache.OverviewKey != overviewKey)
        {
            renderZoom = 1; renderOffset = PointF.Empty; detailPixels = 2;
            _imageCache.BuildOverview(overviewKey, BackColor, DrawBase);
            OverviewImageBuilds++;
        }
        e.Graphics.InterpolationMode = InterpolationMode.Low;
        var offset = _navigation.Apply(PointF.Empty);
        e.Graphics.DrawImage(_imageCache.Overview!, new RectangleF(offset.X, offset.Y,
            (float)(Width * _navigation.Zoom), (float)(Height * _navigation.Zoom)));
        var paintKey = (_mapRevision, ClientSize, _navigation.Zoom, offset);
        bool reuseInteraction = _navigation.IsInteracting && _imageCache.Detail is not null &&
            _imageCache.DetailKey is { } cached && cached.Revision == _mapRevision && cached.Size == ClientSize;
        if (!reuseInteraction && (_imageCache.Detail is null || _imageCache.DetailKey != paintKey))
        {
            // Limit the expanded bitmap to 16 million pixels on very large/high-DPI displays.
            double expansion = Math.Min(2, Math.Max(1, Math.Sqrt(16_000_000d / ((double)Width * Height))));
            renderWidth = (int)Math.Ceiling(Width * expansion);
            renderHeight = (int)Math.Ceiling(Height * expansion);
            var padding = new PointF((renderWidth - Width) / 2f, (renderHeight - Height) / 2f);
            renderZoom = _navigation.Zoom;
            renderOffset = new PointF(offset.X + padding.X, offset.Y + padding.Y);
            detailPixels = .5f;
            _imageCache.BuildDetail(paintKey, new Size(renderWidth, renderHeight), padding, DrawBase);
            BaseImageBuilds++;
        }
        if (_imageCache.DetailKey is { } prior)
        {
            float ratio = (float)(_navigation.Zoom / prior.Zoom);
            var destination = new RectangleF(
                offset.X - (prior.Offset.X + _imageCache.Padding.X) * ratio,
                offset.Y - (prior.Offset.Y + _imageCache.Padding.Y) * ratio,
                _imageCache.Detail!.Width * ratio, _imageCache.Detail.Height * ratio);
            using var background = new SolidBrush(BackColor);
            e.Graphics.FillRectangle(background, destination);
            e.Graphics.DrawImage(_imageCache.Detail, destination);
            if (reuseInteraction) InteractionImageReuses++;
        }
        void DrawBase(Graphics graphics) => TrackMapRenderer.DrawBase(graphics, _geometry,
            view with { Zoom = renderZoom, Offset = renderOffset }, new Size(renderWidth, renderHeight),
            detailPixels, _displayState, _tasks, _execution is not null, _coverage, _segmentStyles, Font);
        DrawCorridor(e.Graphics);
        foreach (var selected in _selectedLegs.Where(l => l.Samples.Count > 0))
            if (!_selectedGeometry.ContainsKey(selected))
                _selectedGeometry[selected] = selected.Samples.Select(p => _geometry.Project(new(p.Longitude, p.Latitude))).ToArray();
        TrackMapRenderer.DrawOverlay(e.Graphics, _geometry, view, _displayState, Size, Font, _legendHeight,
            _selectedLegs, _acceptedLegIds, _legStyles, _selectedGeometry, _tasks, _selectedTaskId, _highlightRoles, _inspectedLegId);
        DrawLegend(e.Graphics);
    }

}
