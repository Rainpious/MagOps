using System.Drawing.Drawing2D;
using MagOps.Core.Domain;
using MagOps.Infrastructure.Services;
using static MagOps.Desktop.TrackMapPalette;

namespace MagOps.Desktop;

internal static partial class TrackMapRenderer
{
    internal static void DrawOverlay(Graphics graphics, TrackMapGeometrySnapshot geometry,
        TrackMapViewTransform view, TrackMapDisplayState display, Size viewport, Font font,
        float legendHeight, IReadOnlyList<FlightLeg> selectedLegs, IReadOnlySet<string> acceptedLegIds,
        IReadOnlyDictionary<string, byte> legStyles,
        IReadOnlyDictionary<FlightLeg, (double X, double Y)[]> selectedGeometry,
        IReadOnlyList<PlannedSurveyLineTask> tasks, string? selectedTaskId, IReadOnlyDictionary<string, string>? highlightRoles = null, string? inspectedLegId = null)
    {
        int Width = viewport.Width, Height = viewport.Height;
        var Font = font;
        PointF ScreenLocal((double X, double Y) xy) => view.ToScreen(xy);
        PointF Screen(GeoCoordinate point) => ScreenLocal(geometry.Project(point));
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var endpointLabels = new List<RectangleF>();
        foreach (var selected in selectedLegs.Where(l => l.Samples.Count > 0).OrderBy(l => l.FlightLegId == inspectedLegId ? 3 : highlightRoles?.GetValueOrDefault(l.FlightLegId) == "源段" ? 2 : highlightRoles?.GetValueOrDefault(l.FlightLegId) == "目标母线" ? 1 : 0))
        {
            byte selectedStyle = acceptedLegIds.Contains(selected.FlightLegId) ? (byte)1 : legStyles.GetValueOrDefault(selected.FlightLegId, (byte)5);
            string? role = highlightRoles?.GetValueOrDefault(selected.FlightLegId);
            if (role is null && !display.VisibleStyle(selectedStyle)) continue;
            Color focusColor = role switch { "源段" => Color.OrangeRed, "目标母线" => Color.DarkCyan, "连接碎段" => Color.DarkOrange, "断开段" => Color.ForestGreen, "端部延伸" => Color.SteelBlue, "疑似重复" => Color.BlueViolet, "疑似冲突" => Color.Crimson, "其他候选" => Color.FromArgb(160, 160, 178), "附近查看" => Color.MediumVioletRed, _ => SelectedColor };
            bool inspected = selected.FlightLegId == inspectedLegId;
            if (inspected) focusColor = Color.MediumVioletRed;
            Color identityColor = selectedStyle switch { 1 => IncludedColor, 4 => CandidateColor, 2 or 3 => ReviewColor, _ => UnassignedColor };
            using var halo = new Pen(focusColor, 6.4f);
            using var separator = new Pen(Color.White, 4.2f);
            using var pen = new Pen(role is null ? identityColor : focusColor, selectedStyle == 1 ? IncludedWidth : CandidateWidth);
            using var selectedBrush = new SolidBrush(focusColor);
            if (role == "其他候选" && !inspected) { halo.DashStyle = DashStyle.Dash; pen.DashStyle = DashStyle.Dash; }
            var samples = selected.Samples;
            var projected = selectedGeometry[selected];
            PointF? lastHighlight = null;
            using var highlightPath = new GraphicsPath();
            for (int i = 1; i < samples.Count; i++)
            {
                if (!TrackMapEdgeIndex.IsContinuous(samples[i - 1], samples[i]))
                { lastHighlight = null; highlightPath.StartFigure(); continue; }
                var a = lastHighlight ?? ScreenLocal(projected[i - 1]);
                var b = ScreenLocal(projected[i]);
                if (i < samples.Count - 1 && i != samples.Count / 2 && Math.Abs(b.X - a.X) < .5f && Math.Abs(b.Y - a.Y) < .5f)
                { lastHighlight = a; continue; }
                highlightPath.AddLine(a,b);
                lastHighlight = b;
            }
            graphics.DrawPath(halo, highlightPath);
            graphics.DrawPath(separator, highlightPath);
            graphics.DrawPath(pen, highlightPath);
            int middleIndex = samples.Count / 2;
            if (middleIndex > 0 && TrackMapEdgeIndex.IsContinuous(samples[middleIndex - 1], samples[middleIndex]))
                TrackMapRenderer.DrawArrow(graphics, focusColor, ScreenLocal(projected[middleIndex - 1]), ScreenLocal(projected[middleIndex]));
            var first = Screen(new(samples[0].Longitude,samples[0].Latitude));
            var last = Screen(new(samples[^1].Longitude,samples[^1].Latitude));
            graphics.FillEllipse(selectedBrush,first.X-4,first.Y-4,8,8);
            graphics.FillRectangle(selectedBrush,last.X-4,last.Y-4,8,8);
            string lineName = tasks.FirstOrDefault(t => t.SurveyTaskStableId == selected.SurveyTaskStableId)?.LineName ?? "未归属";
            foreach (var endpoint in new[] { (Text: role is null ? "实飞起" : $"{(inspected ? "附近查看 · " : "")}{role} {lineName}", Point: first), (Text: role is null ? "实飞终" : "终", Point: last) })
            {
                if (endpoint.Point.X < 0 || endpoint.Point.X > Width || endpoint.Point.Y < 0 || endpoint.Point.Y > Height) continue;
                var rectangle = PlaceEndpointLabel(graphics, endpoint.Text, endpoint.Point, endpointLabels, font, viewport, legendHeight);
                if (rectangle.IsEmpty) continue;
                graphics.FillRectangle(Brushes.White, rectangle);
                graphics.DrawString(endpoint.Text, Font, selectedBrush, rectangle.Location);
            }
        }
        if (display.ShowDesignLines && !display.OnlyCandidates && display.ShowReferenceEndpoints)
        {
            var chosenTask = tasks.FirstOrDefault(task => task.SurveyTaskStableId.Equals(selectedTaskId, StringComparison.OrdinalIgnoreCase));
            if (chosenTask is not null)
            foreach (var endpoint in new[] { (Text: chosenTask.LineName + " 参考起", Point: Screen(chosenTask.Start)),
                (Text: "参考终", Point: Screen(chosenTask.End)) })
            {
                if (endpoint.Point.X < 0 || endpoint.Point.X > Width || endpoint.Point.Y < 0 || endpoint.Point.Y > Height) continue;
                var rectangle = PlaceEndpointLabel(graphics, endpoint.Text, endpoint.Point, endpointLabels, font, viewport, legendHeight);
                if (rectangle.IsEmpty) continue;
                graphics.FillRectangle(Brushes.White, rectangle);
                graphics.DrawString(endpoint.Text, Font, Brushes.SlateGray, rectangle.Location);
            }
        }
    }

    private static RectangleF PlaceEndpointLabel(Graphics graphics, string text, PointF point, List<RectangleF> occupied, Font font, Size viewport, float legendHeight)
    {
        var size = graphics.MeasureString(text, font);
        foreach (var offset in new[] { new PointF(7, 7), new PointF(7, -size.Height - 7),
            new PointF(-size.Width - 7, 7), new PointF(-size.Width - 7, -size.Height - 7) })
        {
            var rectangle = new RectangleF(Math.Clamp(point.X + offset.X, 2, Math.Max(2, viewport.Width - size.Width - 2)),
                Math.Clamp(point.Y + offset.Y, legendHeight + 2, Math.Max(legendHeight + 2, viewport.Height - size.Height - 2)), size.Width, size.Height);
            if (occupied.Any(r => r.IntersectsWith(rectangle))) continue;
            occupied.Add(rectangle); return rectangle;
        }
        return RectangleF.Empty; // Suppress colliding labels; endpoints and full row details remain available.
    }
}
