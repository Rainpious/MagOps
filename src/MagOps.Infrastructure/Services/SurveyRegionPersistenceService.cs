using System.Text.Json;
using MagOps.Core.SurveyPlanning;

namespace MagOps.Infrastructure.Services;

public sealed class SurveyRegionSnapshot
{
    public string Schema { get; set; } = "magops-survey-region-1";
    public List<SurveyRegionPolygonSnapshot> Polygons { get; set; } = [];
}

public sealed class SurveyRegionPolygonSnapshot
{
    public List<SurveyBoundaryPoint> OuterRing { get; set; } = [];
    public List<List<SurveyBoundaryPoint>> Holes { get; set; } = [];
}

public sealed class SurveyRegionPersistenceService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true
    };

    public string Save(string path, SurveyRegionGeometry region)
    {
        ArgumentNullException.ThrowIfNull(region);
        var snapshot = new SurveyRegionSnapshot
        {
            Polygons = region.Polygons.Select(p => new SurveyRegionPolygonSnapshot
            {
                OuterRing = p.OuterRing.ToList(),
                Holes = p.Holes.Select(h => h.ToList()).ToList()
            }).ToList()
        };
        string full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        string temp = full + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(snapshot, JsonOptions));
            File.Move(temp, full, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        return full;
    }

    public SurveyRegionGeometry Load(string path)
    {
        string full = Path.GetFullPath(path);
        if (!File.Exists(full))
            throw new FileNotFoundException("Survey Region snapshot not found.", full);
        SurveyRegionSnapshot snapshot = JsonSerializer.Deserialize<SurveyRegionSnapshot>(
            File.ReadAllText(full), JsonOptions)
            ?? throw new InvalidDataException("Survey Region snapshot is empty.");
        if (!snapshot.Schema.Equals("magops-survey-region-1",
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"Unsupported Survey Region schema: {snapshot.Schema}");
        return new SurveyRegionGeometry(snapshot.Polygons.Select(p =>
            new SurveyRegionPolygon(
                p.OuterRing,
                p.Holes.Select(h => (IReadOnlyList<SurveyBoundaryPoint>)h).ToArray())));
    }
}
