using System.Xml;
using System.Xml.Linq;
using System.Globalization;
using MagOps.Core.Analysis;

namespace MagOps.Reporting;

public sealed record QualityRouteEntry(string Path, string Placemark, string Sha256);
public sealed record QualityRouteCatalogResult(QualityRouteEntry[] Entries, ReportSource[] Sources, string[] Warnings);
public sealed record QualitySpatialCandidate(string Identity, string KmlPath, string Placemark,
    double MedianDistanceM, double ComputablePointRatio, int Rank);

/// <summary>Deterministic discovery, no directory links and no invented spatial identity.</summary>
public static class QualityRouteCatalog
{
    /// <summary>Whole-line geometric suggestions only. Never applies production identity or mapping.</summary>
    public static QualitySpatialCandidate[] SuggestSpatial(QualityRouteCatalogResult catalog, TextTable table, QualityColumns columns)
    {
        if (columns.Longitude is not { } lon || columns.Latitude is not { } lat || lon < 0 || lat < 0 ||
            lon >= table.Headers.Length || lat >= table.Headers.Length) return [];
        var routes = new List<(QualityRouteEntry Entry, ReportRouteGeometry Geometry)>();
        foreach (var entry in catalog.Entries)
        {
            try { routes.Add((entry, ReportRouteGeometry.Read(entry.Path, entry.Placemark))); }
            catch (Exception ex) when (ex is InvalidDataException or FormatException or XmlException) { }
        }
        var result = new List<QualitySpatialCandidate>();
        foreach (var line in table.Rows.GroupBy(r => QualitySpatialIdentity.Key(r, table.Headers.Length, columns)).Where(g => g.Key.Length > 0))
        {
            var points = line.Where(r => r.Length == table.Headers.Length)
                .Select(r => (Lon: double.TryParse(r[lon], NumberStyles.Float, CultureInfo.InvariantCulture, out double x) ? x : double.NaN,
                    Lat: double.TryParse(r[lat], NumberStyles.Float, CultureInfo.InvariantCulture, out double y) ? y : double.NaN))
                .Where(p => double.IsFinite(p.Lon) && double.IsFinite(p.Lat) && p.Lon is >= -180 and <= 180 && p.Lat is >= -90 and <= 90).ToArray();
            if (points.Length == 0) continue;
            // Uniform record samples retain the complete line extent rather than only its first fragment.
            int count = Math.Min(256, points.Length);
            var sample = Enumerable.Range(0, count).Select(i => points[count == 1 ? 0 : (int)((long)i * (points.Length - 1) / (count - 1))]).ToArray();
            var candidates = new List<QualitySpatialCandidate>();
            foreach (var route in routes)
            {
                var distances = sample.Select(p => route.Geometry.EvaluateHorizontal(p.Lon, p.Lat).Offset)
                    .Where(d => d is not null).Select(d => d!.Value).Order().ToArray();
                if (distances.Length == 0) continue;
                double median = distances.Length % 2 == 1 ? distances[distances.Length / 2] :
                    (distances[distances.Length / 2 - 1] + distances[distances.Length / 2]) / 2;
                candidates.Add(new(line.Key, route.Entry.Path, route.Entry.Placemark, median, (double)distances.Length / sample.Length, 0));
            }
            // Low-coverage routes cannot win solely because a small intersecting fragment is nearby.
            result.AddRange(candidates.OrderByDescending(c => c.ComputablePointRatio >= .8)
                .ThenBy(c => c.MedianDistanceM).ThenByDescending(c => c.ComputablePointRatio)
                .ThenBy(c => c.KmlPath, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Placemark, StringComparer.Ordinal)
                .Take(3).Select((c, i) => c with { Rank = i + 1 }));
        }
        return result.ToArray();
    }

    public static QualityRouteCatalogResult Read(string input)
    {
        string root = System.IO.Path.GetFullPath(input);
        var files = new List<string>();
        void Visit(string directory)
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return;
            files.AddRange(Directory.EnumerateFiles(directory).Where(p => System.IO.Path.GetExtension(p).Equals(".kml", StringComparison.OrdinalIgnoreCase) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) == 0));
            foreach (string child in Directory.EnumerateDirectories(directory).Order(StringComparer.OrdinalIgnoreCase)) Visit(child);
        }
        if (Directory.Exists(root)) Visit(root);
        else if (File.Exists(root) && System.IO.Path.GetExtension(root).Equals(".kml", StringComparison.OrdinalIgnoreCase)) files.Add(root);
        else throw new InvalidDataException("设计来源应为KML文件或包含KML的文件夹。");
        var entries = new List<QualityRouteEntry>(); var sources = new List<ReportSource>(); var warnings = new List<string>();
        foreach (string file in files.Order(StringComparer.OrdinalIgnoreCase))
        {
            string hash = QualityCheckService.Hash(file);
            sources.Add(new("quality-design-kml", file, hash));
            try
            {
                using var reader = XmlReader.Create(file, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
                var doc = XDocument.Load(reader);
                foreach (var mark in doc.Descendants().Where(e => e.Name.LocalName == "Placemark"))
                {
                    string name = mark.Elements().FirstOrDefault(e => e.Name.LocalName == "name")?.Value ?? "";
                    int count = mark.Descendants().Count(e => e.Name.LocalName == "LineString");
                    if (count == 1) entries.Add(new(file, name, hash));
                    else if (count > 1) warnings.Add(System.IO.Path.GetFileName(file) + "：多几何测线未自动对应：" + name);
                }
            }
            catch (Exception ex) when (ex is XmlException or InvalidDataException)
            { warnings.Add(System.IO.Path.GetFileName(file) + "：跳过无法读取的KML：" + ex.Message); }
            if (QualityCheckService.Hash(file) != hash) throw new IOException("读取过程中KML变化。");
        }
        return new(entries.ToArray(), sources.ToArray(), warnings.ToArray());
    }

    public static QualityRouteMapping[] Suggest(QualityRouteCatalogResult catalog, IEnumerable<string> segments) =>
        segments.Where(s => s.Length > 0).Distinct(StringComparer.Ordinal).Select(segment =>
        {
            var hits = catalog.Entries.Where(e => e.Placemark.Equals(segment, StringComparison.Ordinal)).ToArray();
            if (segment == "FILE" && catalog.Entries.Length == 1) hits = catalog.Entries;
            return hits.Length == 1 ? new QualityRouteMapping(segment, hits[0].Placemark) { KmlPath = hits[0].Path } : new(segment, "");
        }).ToArray();
}
