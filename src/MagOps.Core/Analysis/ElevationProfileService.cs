using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using MagOps.Core.Routes;

namespace MagOps.Core.Analysis;

public sealed record ElevationPoint(
    double DistanceM,
    double Longitude,
    double Latitude,
    double TerrainElevationM,
    int SourceRow);

public sealed class ElevationProfileService
{
    private static string Normalize(string? value) =>
        Regex.Replace((value ?? "").Trim().ToLowerInvariant(),
            @"[\s_\-\(\)（）\[\]【】/\\]+", "");

    public IReadOnlyList<ElevationPoint> Read(string path)
    {
        var parsed = DelimitedTextReader.ReadRows(path);
        List<string[]> rows = parsed.Rows;
        if (rows.Count == 0) throw new InvalidDataException("高程 CSV 没有数据。");

        var aliases = new Dictionary<string, HashSet<string>>
        {
            ["distance"] = NormalizeSet("distance", "dist", "里程", "距离", "距离m", "累计距离", "累计里程"),
            ["x"] = NormalizeSet("x", "lon", "longitude", "经度", "经度x"),
            ["y"] = NormalizeSet("y", "lat", "latitude", "纬度", "纬度y"),
            ["z"] = NormalizeSet("z", "elevation", "elev", "altitude", "height", "高程", "海拔", "高度", "高程z")
        };

        int headerRow = -1;
        var cols = new Dictionary<string, int>();
        for (int ri = 0; ri < Math.Min(12, rows.Count); ri++)
        {
            string[] normalized = rows[ri].Select(Normalize).ToArray();
            var found = new Dictionary<string, int>();
            foreach (var alias in aliases)
            {
                for (int ci = 0; ci < normalized.Length; ci++)
                {
                    if (!alias.Value.Contains(normalized[ci])) continue;
                    found[alias.Key] = ci;
                    break;
                }
            }
            if (found.ContainsKey("x") && found.ContainsKey("y") && found.ContainsKey("z"))
            {
                headerRow = ri;
                cols = found;
                break;
            }
        }

        var temp = new List<(double? Distance, double Lon, double Lat, double Z, int Row)>();
        if (headerRow >= 0)
        {
            int xi = cols["x"], yi = cols["y"], zi = cols["z"];
            int? di = cols.TryGetValue("distance", out int d) ? d : null;

            for (int i = headerRow + 1; i < rows.Count; i++)
            {
                string[] row = rows[i];
                if (Math.Max(xi, Math.Max(yi, zi)) >= row.Length) continue;
                if (!TryNumber(row[xi], out double lon) ||
                    !TryNumber(row[yi], out double lat) ||
                    !TryNumber(row[zi], out double z))
                    continue;

                double? distance = di.HasValue && di.Value < row.Length && TryNumber(row[di.Value], out double dv)
                    ? dv : null;
                temp.Add((distance, lon, lat, z, i + 1));
            }
        }
        else
        {
            for (int i = 0; i < rows.Count; i++)
            {
                string[] row = rows[i];
                if (row.Length >= 4 &&
                    TryNumber(row[0], out double dist) &&
                    TryNumber(row[1], out double lon) &&
                    TryNumber(row[2], out double lat) &&
                    TryNumber(row[3], out double z))
                    temp.Add((dist, lon, lat, z, i + 1));
                else if (row.Length >= 3 &&
                         TryNumber(row[0], out lon) &&
                         TryNumber(row[1], out lat) &&
                         TryNumber(row[2], out z))
                    temp.Add((null, lon, lat, z, i + 1));
            }
        }

        if (temp.Count < 2)
            throw new InvalidDataException(
                "未识别到足够的高程点。请确认 CSV 至少包含 X/Y/Z（经度/纬度/高程），推荐使用 Distance,X,Y,Z 格式。");

        foreach (var p in temp)
            if (p.Lon is < -180 or > 180 || p.Lat is < -90 or > 90)
                throw new InvalidDataException(
                    $"第 {p.Row} 行的 X/Y 不像 WGS84 经纬度：X={p.Lon}, Y={p.Lat}。当前高程工具按经纬度 CSV 设计。");

        var result = new List<ElevationPoint>(temp.Count);
        double cumulative = 0;
        for (int i = 0; i < temp.Count; i++)
        {
            if (i > 0)
                cumulative += RouteCoordinateMath.Haversine(
                    temp[i - 1].Lon, temp[i - 1].Lat, temp[i].Lon, temp[i].Lat);
            double distance = temp[i].Distance ?? cumulative;
            result.Add(new ElevationPoint(distance, temp[i].Lon, temp[i].Lat, temp[i].Z, temp[i].Row));
        }
        return result;
    }

    public void WriteKml(
        string path,
        IReadOnlyList<ElevationPoint> points,
        double heightOffsetM = 0,
        string name = "带高程航线",
        string sourceName = "")
    {
        if (points.Count < 2)
            throw new InvalidOperationException("至少需要 2 个高程点才能生成航线 KML。");

        const string ns = "http://www.opengis.net/kml/2.2";
        var doc = new XmlDocument();
        doc.AppendChild(doc.CreateXmlDeclaration("1.0", "utf-8", null));
        XmlElement kml = doc.CreateElement("kml", ns);
        doc.AppendChild(kml);
        XmlElement document = doc.CreateElement("Document", ns);
        kml.AppendChild(document);
        XmlElement pm = doc.CreateElement("Placemark", ns);
        document.AppendChild(pm);

        XmlElement title = doc.CreateElement("name", ns);
        title.InnerText = name;
        pm.AppendChild(title);

        var descLines = new List<string>();
        if (!string.IsNullOrWhiteSpace(sourceName))
            descLines.Add($"源文件：{sourceName}");
        descLines.Add($"高程点数：{points.Count}");
        descLines.Add($"Z 统一增加：{heightOffsetM:g} m");
        descLines.Add("高度模式：absolute（海拔高度）");
        XmlElement desc = doc.CreateElement("description", ns);
        desc.InnerText = string.Join("\n", descLines);
        pm.AppendChild(desc);

        XmlElement line = doc.CreateElement("LineString", ns);
        pm.AppendChild(line);
        XmlElement tess = doc.CreateElement("tessellate", ns);
        tess.InnerText = "1";
        line.AppendChild(tess);
        XmlElement alt = doc.CreateElement("altitudeMode", ns);
        alt.InnerText = "absolute";
        line.AppendChild(alt);

        XmlElement coords = doc.CreateElement("coordinates", ns);
        coords.InnerText = string.Join("\n", points.Select(p =>
            $"{p.Longitude.ToString("F10", CultureInfo.InvariantCulture)}," +
            $"{p.Latitude.ToString("F10", CultureInfo.InvariantCulture)}," +
            $"{(p.TerrainElevationM + heightOffsetM).ToString("F3", CultureInfo.InvariantCulture)}"));
        line.AppendChild(coords);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var settings = new XmlWriterSettings
        {
            Encoding = new System.Text.UTF8Encoding(false),
            OmitXmlDeclaration = false,
            Indent = false
        };
        using XmlWriter writer = XmlWriter.Create(path, settings);
        doc.Save(writer);
    }

    private static HashSet<string> NormalizeSet(params string[] values) =>
        values.Select(Normalize).ToHashSet();

    private static bool TryNumber(string? text, out double value) =>
        double.TryParse((text ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
        && double.IsFinite(value);
}
