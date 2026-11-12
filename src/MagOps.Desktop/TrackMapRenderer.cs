using System.Drawing.Drawing2D;
using MagOps.Core.Domain;
using MagOps.Infrastructure.Services;
using static MagOps.Desktop.TrackMapPalette;

namespace MagOps.Desktop;

/// <summary>Draws base geometry from explicit read-only inputs; owns no controls or caches.</summary>
internal static partial class TrackMapRenderer
{
    internal static void DrawBase(Graphics graphics, TrackMapGeometrySnapshot geometry,
        TrackMapViewTransform view, Size renderSize, float detailPixels,
        TrackMapDisplayState display, IReadOnlyList<PlannedSurveyLineTask> tasks,
        bool hasExecution, IReadOnlyDictionary<string, IReadOnlyList<CoverageInterval>> coverage,
        IReadOnlyList<byte[]> segmentStyles, Font font)
    {
        int renderWidth = renderSize.Width, renderHeight = renderSize.Height;
        var Font = font;
        PointF RenderLocal((double X, double Y) xy) => view.ToRenderScreen(xy);
        PointF Render(GeoCoordinate point) => RenderLocal(geometry.Project(point));

        if (display.ShowRawTrack && !display.OnlyCandidates)
        {
            using var rawPen = new Pen(Color.FromArgb(215, 220, 224), .7f);
            using var rawPath = new GraphicsPath();
            for (int k = 0; k < geometry.StreamCount; k++)
            {
                var xy = geometry.Projected(k);
                for (int j = 1; j < xy.Length; j++)
                {
                    if (!geometry.IsContinuousEdge(k, j)) continue;
                    var a = RenderLocal(xy[j - 1]); var b = RenderLocal(xy[j]);
                    if (a.X < -8 && b.X < -8 || a.X > renderWidth + 8 && b.X > renderWidth + 8 ||
                        a.Y < -8 && b.Y < -8 || a.Y > renderHeight + 8 && b.Y > renderHeight + 8) continue;
                    if (rawPath.PointCount == 0 || rawPath.GetLastPoint() != a) rawPath.StartFigure();
                    rawPath.AddLine(a, b);
                }
            }
            graphics.DrawPath(rawPen, rawPath);
        }
        if (display.ShowDesignLines && !display.OnlyCandidates)
        foreach (var task in tasks)
        {
            bool chosen = false;
            using var pen = new Pen(chosen ? Color.FromArgb(180, 160, 120) : ReferenceColor, chosen ? 1.2f : .6f)
            { DashStyle = DashStyle.Dash };
            var a = Render(task.Start); var b = Render(task.End);
            graphics.DrawLine(pen, a, b);
            // Keep covered reference geometry faint under real samples. The
            // unmatched part is independent KML/design evidence, so draw it
            // distinctly without covering the trajectory that must be checked.
            if (hasExecution && task.GeometryOrigin != "ACTUAL_TRACK")
            {
                var covered = coverage.GetValueOrDefault(task.SurveyTaskStableId) ?? [];
                double cursor = 0;
                using var missing = new Pen(MissingColor, chosen ? 2.4f : 1.6f) { DashStyle = DashStyle.Dash };
                void Gap(double from, double to)
                {
                    if (to <= from) return;
                    graphics.DrawLine(missing, new PointF(a.X + (b.X - a.X) * (float)from, a.Y + (b.Y - a.Y) * (float)from),
                        new PointF(a.X + (b.X - a.X) * (float)to, a.Y + (b.Y - a.Y) * (float)to));
                }
                foreach (var interval in covered) { Gap(cursor, interval.Start); cursor = interval.End; }
                Gap(cursor, 1);
            }
            if (chosen)
            {
                DrawArrow(graphics, pen.Color, a, b);
                graphics.DrawString(task.LineName + " 起", Font, Brushes.SlateGray, a.X, a.Y - Font.Height - 4);
                graphics.DrawString("终", Font, Brushes.SlateGray, b.X, b.Y - Font.Height - 4);
            }
        }
        using var matched = new Pen(CandidateColor, CandidateWidth);
        using var includedPen = new Pen(IncludedColor, IncludedWidth);
        using var unmatched = new Pen(Color.FromArgb(175, 180, 185), AuxiliaryWidth);
        using var conflict = new Pen(Color.FromArgb(195, 168, 186), AuxiliaryWidth) { DashStyle = DashStyle.Dot };
        using var review = new Pen(Color.FromArgb(195, 168, 186), AuxiliaryWidth) { DashStyle = DashStyle.Dash };
        using var connector = new Pen(Color.FromArgb(174, 137, 91), AuxiliaryWidth) { DashStyle = DashStyle.DashDot };
        using var connectorPath = new GraphicsPath();
        using var includedPath = new GraphicsPath();
        using var candidatePath = new GraphicsPath();
        using var unassignedPath = new GraphicsPath();
        using var conflictPath = new GraphicsPath();
        using var reviewPath = new GraphicsPath();
        var includedIndex = new IncludedScreenIndex(renderWidth, renderHeight);
        var candidateSegments = new List<(PointF A, PointF B)>();
        for (int streamIndex = 0; streamIndex < geometry.StreamCount; streamIndex++)
        {
            var stream = geometry.Samples(streamIndex);
            var coordinates = geometry.Projected(streamIndex);
            PointF? lastDrawn = null;
            Pen? lastPen = null;
            for (int i = 1; i < stream.Length; i++)
            {
                // Source UAV logs can have second-resolution UTC with several
                // spatial samples sharing the same timestamp. Equal timestamps
                // are valid adjacent observations; only reverse time or a long
                // gap is a display break.
                byte style = segmentStyles[streamIndex][i];
                if (style == 0 || !display.VisibleStyle(style)) { lastDrawn = null; lastPen = null; continue; }
                Pen pen = style switch { 1 => includedPen, 2 => conflict, 3 => review, 4 => matched, 6 => connector, _ => unmatched };
                var a = RenderLocal(coordinates[i-1]);
                var b = RenderLocal(coordinates[i]);
                bool newFigure = !ReferenceEquals(lastPen, pen) || !lastDrawn.HasValue;
                if (!ReferenceEquals(lastPen, pen)) lastDrawn = a;
                a = lastDrawn ?? a;
                // Display-only subpixel coalescing. Keep time breaks and category changes;
                // recognition and exports always use every original observation.
                if (i < stream.Length - 1 && Math.Abs(b.X-a.X) < detailPixels && Math.Abs(b.Y-a.Y) < detailPixels)
                { lastPen = pen; continue; }
                bool outside = a.X < -8 && b.X < -8 || a.X > renderWidth+8 && b.X > renderWidth+8 ||
                    a.Y < -8 && b.Y < -8 || a.Y > renderHeight+8 && b.Y > renderHeight+8;
                if (!outside)
                {
                    var path = style switch { 1 => includedPath, 2 => conflictPath, 3 => reviewPath, 4 => candidatePath, 6 => connectorPath, _ => unassignedPath };
                    if (newFigure || path.PointCount == 0 || path.GetLastPoint() != a) path.StartFigure();
                    path.AddLine(a, b);
                    if (style == 1) includedIndex.Add(a, b);
                    else if (style == 4) candidateSegments.Add((a, b));
                }
                lastDrawn = b; lastPen = pen;
            }
        }
        graphics.DrawPath(connector, connectorPath);
        graphics.DrawPath(unmatched, unassignedPath);
        graphics.DrawPath(review, reviewPath);
        graphics.DrawPath(conflict, conflictPath);
        graphics.DrawPath(matched, candidatePath);
        graphics.DrawPath(includedPen, includedPath);
        using var overlapPath = new GraphicsPath();
        foreach (var segment in candidateSegments)
        {
            float dx = segment.B.X - segment.A.X, dy = segment.B.Y - segment.A.Y;
            float from = 0, to = 1;
            // Work only within the cached image; long offscreen lines cannot create huge loops.
            bool Clip(float p, float q)
            {
                if (p == 0) return q >= 0;
                float r = q / p;
                if (p < 0) { if (r > to) return false; from = Math.Max(from, r); }
                else { if (r < from) return false; to = Math.Min(to, r); }
                return true;
            }
            if (!Clip(-dx, segment.A.X + 8) || !Clip(dx, renderWidth + 8 - segment.A.X) ||
                !Clip(-dy, segment.A.Y + 8) || !Clip(dy, renderHeight + 8 - segment.A.Y)) continue;
            var start = new PointF(segment.A.X + dx * from, segment.A.Y + dy * from);
            var end = new PointF(segment.A.X + dx * to, segment.A.Y + dy * to);
            int steps = Math.Max(1, (int)Math.Ceiling(Math.Max(Math.Abs(end.X - start.X), Math.Abs(end.Y - start.Y)) / 6));
            for (int i = 0; i < steps; i++)
            {
                var a = new PointF(start.X + (end.X - start.X) * i / steps, start.Y + (end.Y - start.Y) * i / steps);
                var b = new PointF(start.X + (end.X - start.X) * (i + 1) / steps, start.Y + (end.Y - start.Y) * (i + 1) / steps);
                if (!includedIndex.Overlaps(a, b, 1.5f)) continue;
                if (overlapPath.PointCount == 0 || overlapPath.GetLastPoint() != a) overlapPath.StartFigure();
                overlapPath.AddLine(a, b);
            }
        }
        using var overlapPen = new Pen(CandidateColor, 1.5f) { DashStyle = DashStyle.Dash };
        graphics.DrawPath(overlapPen, overlapPath);
            }

    // Screen-space index is rebuilt only with a raster. It never changes recognition geometry.
    private sealed class IncludedScreenIndex
    {
        private const float Cell = 24;
        private readonly Dictionary<(int X, int Y), List<(PointF A, PointF B)>> _cells = [];
        private readonly int _width, _height;
        internal IncludedScreenIndex(int width, int height) { _width = width; _height = height; }
        internal void Add(PointF a, PointF b)
        {
            // Clip before indexing long lines so distant offscreen coordinates cannot grow the index.
            double from = 0, to = 1, dx = b.X - a.X, dy = b.Y - a.Y;
            bool Clip(double p, double q)
            {
                if (p == 0) return q >= 0;
                double r = q / p;
                if (p < 0) { if (r > to) return false; from = Math.Max(from, r); }
                else { if (r < from) return false; to = Math.Min(to, r); }
                return true;
            }
            if (!Clip(-dx, a.X + Cell) || !Clip(dx, _width + Cell - a.X) ||
                !Clip(-dy, a.Y + Cell) || !Clip(dy, _height + Cell - a.Y)) return;
            var start = new PointF((float)(a.X + dx * from), (float)(a.Y + dy * from));
            var end = new PointF((float)(a.X + dx * to), (float)(a.Y + dy * to));
            int steps = Math.Max(1, (int)Math.Ceiling(Math.Max(Math.Abs(end.X - start.X), Math.Abs(end.Y - start.Y)) / (Cell / 2)));
            (int, int)? prior = null;
            for (int i = 0; i <= steps; i++)
            {
                var key = ((int)Math.Floor((start.X + (end.X - start.X) * i / steps) / Cell),
                    (int)Math.Floor((start.Y + (end.Y - start.Y) * i / steps) / Cell));
                if (key == prior) continue;
                if (!_cells.TryGetValue(key, out var lines)) _cells[key] = lines = [];
                lines.Add((a, b)); prior = key;
            }
        }
        internal bool Overlaps(PointF a, PointF b, float tolerance)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y, length = dx * dx + dy * dy;
            if (length < .01) return false;
            var middle = new PointF((a.X + b.X) / 2, (a.Y + b.Y) / 2);
            int x = (int)Math.Floor(middle.X / Cell), y = (int)Math.Floor(middle.Y / Cell);
            for (int ox = -1; ox <= 1; ox++)
            for (int oy = -1; oy <= 1; oy++)
            {
                if (!_cells.TryGetValue((x + ox, y + oy), out var lines)) continue;
                foreach (var line in lines)
                {
                    double lx = line.B.X - line.A.X, ly = line.B.Y - line.A.Y, ll = lx * lx + ly * ly;
                    if (ll < .01 || Math.Pow(dx * lx + dy * ly, 2) < .97 * length * ll) continue;
                    double t = Math.Clamp(((middle.X - line.A.X) * lx + (middle.Y - line.A.Y) * ly) / ll, 0, 1);
                    double ex = middle.X - line.A.X - t * lx, ey = middle.Y - line.A.Y - t * ly;
                    if (ex * ex + ey * ey <= tolerance * tolerance) return true;
                }
            }
            return false;
        }
    }

    internal static void DrawArrow(Graphics graphics, Color color, PointF a, PointF b)
    {
        float dx=b.X-a.X, dy=b.Y-a.Y;
        float length=MathF.Sqrt(dx*dx+dy*dy);
        if (length < .01f) return;
        dx/=length; dy/=length;
        float x=(a.X+b.X)/2, y=(a.Y+b.Y)/2;
        using var brush=new SolidBrush(color);
        graphics.FillPolygon(brush,new PointF[] { new(x+dx*6,y+dy*6),new(x-dx*5-dy*4,y-dy*5+dx*4),new(x-dx*5+dy*4,y-dy*5-dx*4) });
    }
}
