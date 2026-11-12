namespace MagOps.Infrastructure.Services;

/// <summary>Legacy spatial-bucket discovery, with no point identity or production authority.</summary>
internal sealed class SurveyTaskSpatialIndex
{
    private const double CellM = 100;
    private readonly double _lon, _lat, _scale;
    private readonly Dictionary<(int X, int Y), HashSet<PlannedSurveyLineTask>> _cells = [];

    public SurveyTaskSpatialIndex(IReadOnlyList<PlannedSurveyLineTask> tasks, double searchM)
    {
        if (tasks.Count == 0) return;
        _lon = tasks.Average(t => (t.Start.Longitude + t.End.Longitude) / 2);
        _lat = tasks.Average(t => (t.Start.Latitude + t.End.Latitude) / 2);
        _scale = 111319.49 * Math.Cos(_lat * Math.PI / 180);
        int guard = Math.Max(1, (int)Math.Ceiling(searchM / CellM));
        foreach (var task in tasks)
        {
            var (dx, dy) = EndpointLineRecognizer.Delta(task.Start, task.End);
            int steps = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(dx * dx + dy * dy) / 50));
            for (int i = 0; i <= steps; i++)
            {
                double f = (double)i / steps;
                var cell = Cell(new(task.Start.Longitude + (task.End.Longitude - task.Start.Longitude) * f,
                    task.Start.Latitude + (task.End.Latitude - task.Start.Latitude) * f));
                for (int x = -guard; x <= guard; x++)
                for (int y = -guard; y <= guard; y++)
                {
                    var key = (cell.X + x, cell.Y + y);
                    if (!_cells.TryGetValue(key, out var bucket)) _cells[key] = bucket = [];
                    bucket.Add(task);
                }
            }
        }
    }

    public IReadOnlyList<PlannedSurveyLineTask> Nearby(IEnumerable<SurveyTrackPoint> points)
    {
        var found = new HashSet<PlannedSurveyLineTask>();
        foreach (var p in points)
            if (_cells.TryGetValue(Cell(p.Coordinate), out var bucket)) found.UnionWith(bucket);
        return found.OrderBy(t => t.Order).ThenBy(t => t.SurveyTaskStableId, StringComparer.Ordinal).ToArray();
    }

    private (int X, int Y) Cell(GeoCoordinate p) =>
        ((int)Math.Floor((p.Longitude - _lon) * _scale / CellM),
         (int)Math.Floor((p.Latitude - _lat) * 111319.49 / CellM));
}
