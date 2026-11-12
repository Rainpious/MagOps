using MagOps.Core.Coordinates;
using MagOps.Core.Routes;

namespace MagOps.Infrastructure.Services;

/// <summary>Spatially consolidates duplicate plain/height/revision references.</summary>
internal static class ReferenceMissionGeometryService
{
    public const string Version = "kml-spatial-versions-topology-1";
    public static object RuleSnapshot => new { Version, AxisDegrees = 1, CrossM = 6,
        MinimumOverlapRatio = .8, TopologyEndpointM = 8, Primary = "PLAIN_THEN_LONGEST_THEN_SOURCE" };
    public static IReadOnlyList<SegmentationReferenceGeometry> Consolidate(
        IEnumerable<SegmentationReferenceGeometry> source)
    {
        var result = new List<SegmentationReferenceGeometry>();
        var raw = source.GroupBy(g => g.Id, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToArray();
        var eligible = raw.Where(g => g.Kind is "SURVEY" or "REVIEW" && TrackSegmentationReferenceService.Distance(g.Start, g.End) >= 60).ToArray();
        var axis = TrackSegmentationReferenceService.DominantAxis(eligible.Select(g =>
            (SurveyDirection.Bearing(g.Start, g.End) ?? 0, TrackSegmentationReferenceService.Distance(g.Start, g.End))));
        var classified = raw.Select(g => g.Kind == "REVIEW" && axis is { } h &&
            TrackSegmentationReferenceService.Distance(g.Start, g.End) >= 60 &&
            TrackSegmentationReferenceService.AxisDifference(SurveyDirection.Bearing(g.Start, g.End) ?? 0, h) <= 5 &&
            eligible.Any(other => other.Id != g.Id && ParallelNeighbour(g, other))
            ? g with { Kind = "SURVEY", Evidence = g.Evidence + "；跨文件平行作业组" } : g);
        foreach (var geometry in classified.OrderBy(g => g.CoordinateCount > 16 ? 1 : 0)
            .ThenByDescending(g => TrackSegmentationReferenceService.Distance(g.Start, g.End))
            .ThenBy(g => g.SourcePath, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Id, StringComparer.Ordinal))
        {
            int match = -1;
            for (int i = 0; i < result.Count; i++)
            {
                var existing = result[i];
                if (existing.Kind != geometry.Kind || existing.Kind is not ("SURVEY" or "TIE" or "CONNECTOR") ||
                    TrackSegmentationReferenceService.AxisDifference(
                        SurveyDirection.Bearing(existing.Start, existing.End) ?? 0,
                        SurveyDirection.Bearing(geometry.Start, geometry.End) ?? 0) > 1 ||
                    !NearLine(existing, geometry) || Overlap(existing, geometry) < .8)
                    continue;
                match = i;
                break;
            }
            if (match < 0)
            {
                result.Add(geometry with
                {
                    Versions = geometry.Versions.Count == 0 ? [VersionOf(geometry)] : geometry.Versions
                });
                continue;
            }

            var prior = result[match];
            var versions = prior.Versions.Concat(geometry.Versions.Count > 0 ? geometry.Versions : [VersionOf(geometry)])
                .GroupBy(v => v.Id, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToArray();
            string evidence = prior.Evidence.Contains(geometry.Id, StringComparison.Ordinal)
                ? prior.Evidence
                : prior.Evidence + $"；空间近重复来源 {geometry.Id}（{geometry.SourcePath}）";
            result[match] = prior with { Evidence = evidence, Versions = versions };
        }
        return AnnotateTopology(result);
    }

    private static IReadOnlyList<SegmentationReferenceGeometry> AnnotateTopology(List<SegmentationReferenceGeometry> geometry)
    {
        var alias = geometry.SelectMany(g => g.Versions.Select(v => (v.Id, Primary: g.Id)))
            .GroupBy(v => v.Id).ToDictionary(g => g.Key, g => g.First().Primary);
        var links = new Dictionary<string, HashSet<string>>();
        foreach (var path in geometry.SelectMany(g => g.Versions.Select(v => (Geometry: g, Version: v)))
            .Where(g => g.Version.PathId.Length > 0).GroupBy(g => g.Version.PathId))
        {
            var ordered = path.OrderBy(g => g.Version.PathOrder).ToArray();
            for (int i = 1; i + 1 < ordered.Length; i++)
            {
                var left = ordered[i - 1]; var middle = ordered[i]; var right = ordered[i + 1];
                if (middle.Geometry.Kind is not ("REVIEW" or "CONNECTOR") || left.Geometry.Kind is not ("SURVEY" or "TIE") ||
                    right.Geometry.Kind is not ("SURVEY" or "TIE") || left.Geometry.Id == right.Geometry.Id ||
                    TrackSegmentationReferenceService.Distance(left.Version.End, middle.Version.Start) > 8 ||
                    TrackSegmentationReferenceService.Distance(middle.Version.End, right.Version.Start) > 8) continue;
                if (!links.TryGetValue(middle.Geometry.Id, out var adjacent)) links[middle.Geometry.Id] = adjacent = [];
                adjacent.Add(alias.GetValueOrDefault(left.Version.Id, left.Geometry.Id));
                adjacent.Add(alias.GetValueOrDefault(right.Version.Id, right.Geometry.Id));
            }
        }
        return geometry.Select(g => links.TryGetValue(g.Id, out var adjacent) ? g with
        { Kind = "CONNECTOR", AdjacentSurveyIds = g.AdjacentSurveyIds.Concat(adjacent).Distinct().ToArray(),
            Evidence = g.Evidence + "；KML点序连接相邻作业测线" } : g).ToArray();
    }

    private static ReferenceGeometryVersion VersionOf(SegmentationReferenceGeometry g) =>
        new(g.Id, g.Name, g.Start, g.End, g.SourcePath, g.SourceHash, g.PathId, g.PathOrder, g.CoordinateCount);

    private static bool NearLine(SegmentationReferenceGeometry a, SegmentationReferenceGeometry b)
    {
        return Cross(a, b.Start) <= 6 && Cross(a, b.End) <= 6 && Cross(b, a.Start) <= 6 && Cross(b, a.End) <= 6;
    }

    private static double Cross(SegmentationReferenceGeometry a, GeoCoordinate point)
    {
        var (dx, dy) = EndpointLineRecognizer.Delta(a.Start, a.End);
        var (x, y) = EndpointLineRecognizer.Delta(a.Start, point);
        return Math.Abs(x * dy - y * dx) / Math.Max(.5, Math.Sqrt(dx * dx + dy * dy));
    }

    private static bool ParallelNeighbour(SegmentationReferenceGeometry a, SegmentationReferenceGeometry b) =>
        TrackSegmentationReferenceService.AxisDifference(SurveyDirection.Bearing(a.Start, a.End) ?? 0,
            SurveyDirection.Bearing(b.Start, b.End) ?? 0) <= 5 && Cross(a, b.Start) is >= 12 and <= 500 &&
        Cross(a, b.End) is >= 12 and <= 500 && Overlap(a, b) > .2;

    private static double Overlap(SegmentationReferenceGeometry a, SegmentationReferenceGeometry b)
    {
        var (dx, dy) = EndpointLineRecognizer.Delta(a.Start, a.End);
        double length = Math.Sqrt(dx * dx + dy * dy);
        if (length < .5) return 0;
        var (sx, sy) = EndpointLineRecognizer.Delta(a.Start, b.Start);
        var (ex, ey) = EndpointLineRecognizer.Delta(a.Start, b.End);
        double first = (sx * dx + sy * dy) / length;
        double last = (ex * dx + ey * dy) / length;
        double overlap = Math.Max(0, Math.Min(length, Math.Max(first, last)) - Math.Max(0, Math.Min(first, last)));
        return overlap / Math.Max(.5, Math.Max(length, Math.Abs(last - first)));
    }
}
