namespace MagOps.Desktop;

/// <summary>Queries display geometry without depending on a control or editing review decisions.</summary>
internal static class TrackMapHitQuery
{
    internal static IReadOnlyList<string> Find(TrackMapGeometrySnapshot geometry, TrackMapViewTransform view,
        Point point, double tolerance, TrackMapEdgeIndex edges,
        Func<string, bool> visible)
    {
        double Distance(PointF a, PointF b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double length = dx * dx + dy * dy;
            double t = length == 0 ? 0 : Math.Clamp(((point.X - a.X) * dx + (point.Y - a.Y) * dy) / length, 0, 1);
            return Math.Pow(point.X - a.X - t * dx, 2) + Math.Pow(point.Y - a.Y - t * dy, 2);
        }
        var distances = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        void Add(string id, double distance)
        {
            if (id.Length > 0 && distance <= tolerance * tolerance &&
                (!distances.TryGetValue(id, out var prior) || distance < prior)) distances[id] = distance;
        }
        for (int s = 0; s < geometry.StreamCount; s++)
        {
            var samples = geometry.Samples(s);
            var projected = geometry.Projected(s);
            for (int i = 0; i < samples.Length; i++)
            {
                string id = edges.LegId(s, i);
                if (!visible(id)) continue;
                var b = view.ToScreen(projected[i]);
                Add(id, Distance(b, b));
                if (id.Length == 0 || !edges.IsContinuous(s, i)) continue;
                Add(id, Distance(view.ToScreen(projected[i - 1]), b));
            }
        }
        return distances.OrderBy(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Key).ToArray();
    }
}
