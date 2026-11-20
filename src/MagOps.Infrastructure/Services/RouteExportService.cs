using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using MagOps.Core.Routes;
using MagOps.Infrastructure.Storage;

namespace MagOps.Infrastructure.Services;

public sealed record RouteExportDirectoryLayout(
    string BaseDirectory,
    string FolderPrefix,
    string KmlDirectory,
    string CsvDirectory,
    string KmzDirectory);

public sealed class RouteExportService
{
    public const string EmbeddedTemplateName =
        "MagOps.Infrastructure.Resources.DJI_M400_实时仿地_KMZ模板_v25.kmz";

    public const string EmbeddedRouteKmlTemplateName =
        "MagOps.Infrastructure.Resources.RouteKmlTemplates.航线-天青_DJI极简.kml";

    public const double DjiRouteKmlMaxSpacingM = 10.0;

    public string WriteSortieKml(
        string path,
        RouteSortie sortie,
        int sortieIndex,
        string mode,
        double centralMeridian,
        string terrainGeoTiffPath,
        double aglM)
    {
        var dem = new TerrainSetSampler(terrainGeoTiffPath);
        return WriteSortieKml(
            path,
            sortie,
            sortieIndex,
            mode,
            centralMeridian,
            dem,
            aglM);
    }

    public string WriteSortieKmlWithoutElevation(
        string path,
        RouteSortie sortie,
        int sortieIndex,
        string mode,
        double centralMeridian)
    {
        if (sortie.Points.Count < 2)
            throw new InvalidOperationException("架次至少需要 2 个航点。");

        IReadOnlyList<(double Lon, double Lat)> dense =
            BuildDjiRouteKmlPoints(
                sortie,
                mode,
                centralMeridian);

        string[] coordinateLines = dense
            .Select(point =>
                $"{point.Lon.ToString("F6", CultureInfo.InvariantCulture)}," +
                point.Lat.ToString("F6", CultureInfo.InvariantCulture))
            .ToArray();

        string template = Encoding.UTF8.GetString(
            LoadEmbeddedRouteKmlTemplate());
        template = template
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal);

        const string requiredDeclaration =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>";
        if (!template.StartsWith(requiredDeclaration, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "航线 KML 母版必须以 UTF-8 大写编码声明开头。");
        }

        template = Regex.Replace(
            template,
            @"<name>.*?</name>",
            $"<name>{GetSortieFileStem(sortie, sortieIndex)}</name>",
            RegexOptions.Singleline);

        template = Regex.Replace(
            template,
            @"\s*<altitudeMode>.*?</altitudeMode>",
            "",
            RegexOptions.Singleline);

        template = Regex.Replace(
            template,
            @"(<coordinates>).*?(</coordinates>)",
            match =>
                match.Groups[1].Value +
                "\n" +
                string.Join(
                    "\n",
                    coordinateLines.Select(line => "          " + line)) +
                "\n        " +
                match.Groups[2].Value,
            RegexOptions.Singleline);

        EnsureParent(path);
        File.WriteAllText(
            path,
            template,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return Path.GetFullPath(path);
    }

    private string WriteSortieKml(
        string path,
        RouteSortie sortie,
        int sortieIndex,
        string mode,
        double centralMeridian,
        TerrainSetSampler dem,
        double aglM)
    {
        if (sortie.Points.Count < 2)
            throw new InvalidOperationException("架次至少需要 2 个航点。");
        if (!double.IsFinite(aglM) || aglM < 0)
            throw new InvalidOperationException("目标 AGL 必须是大于或等于 0 的有限数字。");

        IReadOnlyList<(double Lon, double Lat)> dense =
            BuildDjiRouteKmlPoints(
                sortie,
                mode,
                centralMeridian);

        string[] coordinateLines = dense
            .Select(point =>
            {
                double terrainM = dem.SampleBilinear(
                    point.Lon,
                    point.Lat);
                double absoluteM = terrainM + aglM;
                return
                    $"{point.Lon.ToString("F6", CultureInfo.InvariantCulture)}," +
                    $"{point.Lat.ToString("F6", CultureInfo.InvariantCulture)}," +
                    absoluteM.ToString("F2", CultureInfo.InvariantCulture);
            })
            .ToArray();

        string template = Encoding.UTF8.GetString(
            LoadEmbeddedRouteKmlTemplate());
        template = template
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal);

        const string requiredDeclaration =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>";
        if (!template.StartsWith(requiredDeclaration, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "航线 KML 母版必须以 UTF-8 大写编码声明开头。");
        }

        template = Regex.Replace(
            template,
            @"<name>.*?</name>",
            $"<name>{GetSortieFileStem(sortie, sortieIndex)}</name>",
            RegexOptions.Singleline);

        template = Regex.Replace(
            template,
            @"(<coordinates>).*?(</coordinates>)",
            match =>
                match.Groups[1].Value +
                "\n" +
                string.Join(
                    "\n",
                    coordinateLines.Select(line => "          " + line)) +
                "\n        " +
                match.Groups[2].Value,
            RegexOptions.Singleline);

        EnsureParent(path);
        File.WriteAllText(
            path,
            template,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return Path.GetFullPath(path);
    }

    private static IReadOnlyList<(double Lon, double Lat)> BuildDjiRouteKmlPoints(
        RouteSortie sortie,
        string mode,
        double centralMeridian)
    {
        var source = sortie.Points
            .Select(point => RouteCoordinateMath.ToLonLat(
                point,
                mode,
                centralMeridian))
            .ToArray();
        if (source.Length < 2)
            throw new InvalidOperationException("架次至少需要 2 个航点。");

        var result = new List<(double Lon, double Lat)>
        {
            (source[0].Lon, source[0].Lat)
        };

        for (int i = 0; i < source.Length - 1; i++)
        {
            var from = source[i];
            var to = source[i + 1];
            double distanceM = RouteCoordinateMath.Haversine(
                from.Lon,
                from.Lat,
                to.Lon,
                to.Lat);
            int steps = Math.Max(
                1,
                (int)Math.Ceiling(distanceM / DjiRouteKmlMaxSpacingM));

            double lonDelta = to.Lon - from.Lon;
            if (lonDelta > 180.0)
                lonDelta -= 360.0;
            else if (lonDelta < -180.0)
                lonDelta += 360.0;

            for (int step = 1; step <= steps; step++)
            {
                double t = (double)step / steps;
                double lon = from.Lon + lonDelta * t;
                if (lon > 180.0)
                    lon -= 360.0;
                else if (lon < -180.0)
                    lon += 360.0;

                double lat = from.Lat + (to.Lat - from.Lat) * t;
                result.Add((lon, lat));
            }
        }

        return result;
    }

    public string WriteSortieCsv(
        string path,
        RouteSortie sortie,
        int sortieIndex,
        string mode,
        double centralMeridian)
    {
        int missing = sortie.Points.Count(p => p.Label.Trim().Length == 0);
        if (missing > 0)
            throw new InvalidOperationException(
                $"{GetSortieFileStem(sortie, sortieIndex)} 中有 {missing} 个点缺少原始点名，无法按原程序格式导出 CSV。");

        EnsureParent(path);
        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        foreach (RouteEndpoint p in sortie.Points)
        {
            var ll = RouteCoordinateMath.ToLonLat(p, mode, centralMeridian);
            string row = string.Join(",",
                EscapeCsv(p.Label.Trim()),
                ll.Lat.ToString("0.##########", CultureInfo.InvariantCulture),
                ll.Lon.ToString("0.##########", CultureInfo.InvariantCulture));
            writer.WriteLine(row);
        }
        return Path.GetFullPath(path);
    }

    public string WriteDjiKmz(
        string outPath,
        RouteSortie sortie,
        string mode,
        double centralMeridian,
        double aglM,
        double speedMps)
    {
        if (speedMps <= 0)
            throw new ArgumentOutOfRangeException(nameof(speedMps), "飞行速度必须大于 0。");

        byte[] templateBytes = LoadEmbeddedDjiTemplate();
        string temp = Path.Combine(Path.GetTempPath(), "RouteKmlKmz_" + Guid.NewGuid().ToString("N"));
        string zip = Path.Combine(temp, "template.kmz");
        string work = Path.Combine(temp, "work");
        Directory.CreateDirectory(temp);
        Directory.CreateDirectory(work);
        File.WriteAllBytes(zip, templateBytes);

        try
        {
            ZipFile.ExtractToDirectory(zip, work);
            string templateKml = Path.Combine(work, "wpmz", "template.kml");
            string waylines = Path.Combine(work, "wpmz", "waylines.wpml");
            if (!File.Exists(templateKml) || !File.Exists(waylines))
                throw new InvalidDataException("KMZ 母版必须包含 wpmz\\template.kml 和 wpmz\\waylines.wpml。");

            var pts = sortie.Points
                .Select(p => RouteCoordinateMath.ToLonLat(p, mode, centralMeridian))
                .ToArray();
            if (pts.Length < 2)
                throw new InvalidOperationException("架次至少需要 2 个航点。");

            var tkml = new XmlDocument { PreserveWhitespace = true };
            tkml.Load(templateKml);
            var tns = new XmlNamespaceManager(tkml.NameTable);
            tns.AddNamespace("k", "http://www.opengis.net/kml/2.2");
            XmlNode? coord = tkml.SelectSingleNode("//k:coordinates", tns);
            if (coord is not null)
            {
                string lines = string.Join("\r\n", pts.Select(p =>
                    $"{p.Lon.ToString("F10", CultureInfo.InvariantCulture)},{p.Lat.ToString("F10", CultureInfo.InvariantCulture)},0"));
                coord.InnerText = "\r\n" + lines + "\r\n            ";
            }
            SaveXmlUtf8(tkml, templateKml);

            var w = new XmlDocument { PreserveWhitespace = true };
            w.Load(waylines);
            var ns = new XmlNamespaceManager(w.NameTable);
            ns.AddNamespace("k", "http://www.opengis.net/kml/2.2");
            ns.AddNamespace("w", "http://www.dji.com/wpmz/1.0.6");

            XmlNodeList? folders = w.SelectNodes("//k:Folder", ns);
            if (folders is null || folders.Count == 0)
                throw new InvalidDataException("waylines.wpml 中未找到航线 Folder。");
            XmlNode folder = folders[0]!;
            XmlNodeList? marks = folder.SelectNodes("k:Placemark", ns);
            if (marks is null || marks.Count == 0)
                throw new InvalidDataException("waylines.wpml 中未找到航点。");

            XmlNode prototype = marks[0]!.CloneNode(true);
            foreach (XmlNode mark in marks.Cast<XmlNode>().ToArray())
                folder.RemoveChild(mark);

            string aglText = aglM.ToString("0.############", CultureInfo.InvariantCulture);
            string speedText = speedMps.ToString("0.############", CultureInfo.InvariantCulture);

            for (int i = 0; i < pts.Length; i++)
            {
                XmlNode p = prototype.CloneNode(true);
                XmlNode? pc = p.SelectSingleNode("./k:Point/k:coordinates", ns);
                if (pc is not null)
                    pc.InnerText = "\r\n            "
                                 + pts[i].Lon.ToString("F10", CultureInfo.InvariantCulture)
                                 + ","
                                 + pts[i].Lat.ToString("F10", CultureInfo.InvariantCulture)
                                 + "\r\n          ";

                SetNodeText(p.SelectSingleNode("w:index", ns), i.ToString(CultureInfo.InvariantCulture));
                SetNodeText(p.SelectSingleNode("w:executeHeight", ns), aglText);
                SetNodeText(p.SelectSingleNode("w:waypointSpeed", ns), speedText);
                folder.AppendChild(p);
            }

            int last = Math.Max(0, pts.Length - 1);
            foreach (XmlNode n in w.SelectNodes("//w:actionGroupEndIndex", ns)!.Cast<XmlNode>())
                n.InnerText = last.ToString(CultureInfo.InvariantCulture);

            double distanceM = RouteCoordinateMath.RouteLength(
                sortie.Points, mode, centralMeridian);
            foreach (XmlNode n in w.SelectNodes("//w:distance", ns)!.Cast<XmlNode>())
                n.InnerText = distanceM.ToString("F6", CultureInfo.InvariantCulture);
            foreach (XmlNode n in w.SelectNodes("//w:duration", ns)!.Cast<XmlNode>())
                n.InnerText = (distanceM / speedMps).ToString("F6", CultureInfo.InvariantCulture);

            SaveXmlUtf8(w, waylines);

            EnsureParent(outPath);
            if (File.Exists(outPath)) File.Delete(outPath);
            ZipFile.CreateFromDirectory(work, outPath, CompressionLevel.Optimal, includeBaseDirectory: false);
            return Path.GetFullPath(outPath);
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { }
        }
    }

    public static string GetSortieFileStem(
        RouteSortie sortie,
        int oneBasedIndex)
    {
        string value = string.IsNullOrWhiteSpace(sortie.Name)
            ? $"A{oneBasedIndex:D2}"
            : sortie.Name.Trim();
        foreach (char invalid in Path.GetInvalidFileNameChars())
            value = value.Replace(invalid, '_');
        value = value.Trim(' ', '.', '_');
        return value.Length == 0 ? $"A{oneBasedIndex:D2}" : value;
    }

    public RouteExportDirectoryLayout GetSeparatedOutputLayout(
        string baseDirectory,
        string workareaName)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory))
            throw new InvalidOperationException("请选择统一输出根目录。");

        string fullBase = Path.GetFullPath(baseDirectory);
        string prefix = SafeFolderStem(workareaName);

        var preferences = new GlobalWorkspaceService().Load();
        if (preferences.Root.Length > 0 && preferences.AutomaticOutput)
            return new RouteExportDirectoryLayout(fullBase, prefix, fullBase, fullBase, fullBase);

        return new RouteExportDirectoryLayout(
            fullBase,
            prefix,
            Path.Combine(fullBase, "01_KML"),
            Path.Combine(fullBase, "02_CSV"),
            Path.Combine(fullBase, "03_KMZ_冻结"));
    }

    public IReadOnlyList<string> GetSeparatedOutputPaths(
        RoutePlanResult plan,
        string baseDirectory,
        string workareaName,
        bool exportKml = true,
        bool exportCsv = true,
        bool exportKmz = true)
    {
        if (plan.Sorties.Count == 0)
            throw new InvalidOperationException("尚未生成架次航线。");

        RouteExportDirectoryLayout layout =
            GetSeparatedOutputLayout(
                baseDirectory,
                workareaName);

        var outputs = new List<string>();
        for (int i = 0; i < plan.Sorties.Count; i++)
        {
            int n = i + 1;
            string stem = GetSortieFileStem(plan.Sorties[i], n);
            if (exportKml)
            {
                outputs.Add(Path.Combine(
                    layout.KmlDirectory,
                    stem + ".kml"));
            }

            if (exportCsv)
            {
                outputs.Add(Path.Combine(
                    layout.CsvDirectory,
                    stem + ".csv"));
            }

            if (exportKmz)
            {
                outputs.Add(Path.Combine(
                    layout.KmzDirectory,
                    stem + "_实时仿地.kmz"));
            }
        }

        return outputs;
    }

    public RouteExportDirectoryLayout PrepareSeparatedOutputDirectories(
        string baseDirectory,
        string workareaName,
        bool createKml,
        bool createCsv,
        bool createKmz)
    {
        RouteExportDirectoryLayout layout =
            GetSeparatedOutputLayout(
                baseDirectory,
                workareaName);

        Directory.CreateDirectory(layout.BaseDirectory);

        if (createKml)
            Directory.CreateDirectory(layout.KmlDirectory);
        if (createCsv)
            Directory.CreateDirectory(layout.CsvDirectory);
        if (createKmz)
            Directory.CreateDirectory(layout.KmzDirectory);

        return layout;
    }

    public RouteExportBundle ExportAllSeparated(
        RoutePlanResult plan,
        string baseDirectory,
        string workareaName,
        bool exportKml = true,
        bool exportCsv = true,
        bool exportKmz = true,
        string? kmlTerrainGeoTiffPath = null,
        double kmlAglM = 50.0,
        bool kmlIncludeElevation = true,
        Action<IReadOnlyList<string>>? stageFinalizer = null)
    {
        if (plan.Sorties.Count == 0)
            throw new InvalidOperationException("尚未生成架次航线。");

        RouteExportDirectoryLayout finalLayout =
            GetSeparatedOutputLayout(baseDirectory, workareaName);

        RouteExportBundle staged = AtomicDirectoryBundle.Execute(
            finalLayout.BaseDirectory,
            stage =>
            {
                RouteExportDirectoryLayout layout =
                    PrepareSeparatedOutputDirectories(
                        stage,
                        "",
                        exportKml,
                        exportCsv,
                        exportKmz);

                TerrainSetSampler? kmlDem = exportKml && kmlIncludeElevation
                    ? new TerrainSetSampler(
                        kmlTerrainGeoTiffPath ??
                        throw new InvalidOperationException(
                            "导出带地形高程 KML 前请选择 WGS84 / EPSG:4326 地形 GeoTIFF。"))
                    : null;

                var relativeOutputs = new List<string>();
                for (int i = 0; i < plan.Sorties.Count; i++)
                {
                    int n = i + 1;
                    RouteSortie sortie = plan.Sorties[i];
                    string stem = GetSortieFileStem(sortie, n);

                    if (exportKml)
                    {
                        string kmlPath = Path.Combine(layout.KmlDirectory, stem + ".kml");
                        string written = kmlIncludeElevation
                            ? WriteSortieKml(kmlPath, sortie, n, plan.Mode, plan.CentralMeridian, kmlDem!, kmlAglM)
                            : WriteSortieKmlWithoutElevation(kmlPath, sortie, n, plan.Mode, plan.CentralMeridian);
                        relativeOutputs.Add(Path.GetRelativePath(stage, written));
                    }

                    if (exportCsv)
                    {
                        string written = WriteSortieCsv(
                            Path.Combine(layout.CsvDirectory, stem + ".csv"),
                            sortie, n, plan.Mode, plan.CentralMeridian);
                        relativeOutputs.Add(Path.GetRelativePath(stage, written));
                    }

                    if (exportKmz)
                    {
                        string written = WriteDjiKmz(
                            Path.Combine(layout.KmzDirectory, stem + "_实时仿地.kmz"),
                            sortie, plan.Mode, plan.CentralMeridian,
                            plan.Options.KmzAglM, plan.Options.KmzSpeedMps);
                        relativeOutputs.Add(Path.GetRelativePath(stage, written));
                    }
                }

                stageFinalizer?.Invoke(relativeOutputs
                    .Select(relative => Path.GetFullPath(Path.Combine(stage, relative)))
                    .ToArray());
                return new RouteExportBundle(stage, relativeOutputs);
            },
            (stage, bundle) =>
            {
                if (bundle.OutputPaths.Count == 0)
                    throw new InvalidDataException("航线导出没有生成任何成果。");
                foreach (string relative in bundle.OutputPaths)
                {
                    string full = Path.GetFullPath(Path.Combine(stage, relative));
                    string prefix = Path.GetFullPath(stage)
                        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                        + Path.DirectorySeparatorChar;
                    if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
                        throw new InvalidDataException($"航线导出成果缺失或越界：{relative}");
                    if (new FileInfo(full).Length == 0)
                        throw new InvalidDataException($"航线导出成果为空：{relative}");
                }
            });

        return new RouteExportBundle(
            finalLayout.BaseDirectory,
            staged.OutputPaths
                .Select(relative => Path.GetFullPath(Path.Combine(finalLayout.BaseDirectory, relative)))
                .ToList());
    }

    public RouteExportBundle ExportAll(
        RoutePlanResult plan,
        string outputDirectory,
        bool exportKml = true,
        bool exportCsv = true,
        bool exportKmz = true,
        string? kmlTerrainGeoTiffPath = null,
        double kmlAglM = 50.0,
        bool kmlIncludeElevation = true)
    {
        if (plan.Sorties.Count == 0)
            throw new InvalidOperationException("尚未生成架次航线。");
        if (string.IsNullOrWhiteSpace(outputDirectory))
            throw new InvalidOperationException("请选择统一输出目录。");

        Directory.CreateDirectory(outputDirectory);
        TerrainSetSampler? kmlDem = exportKml && kmlIncludeElevation
            ? new TerrainSetSampler(
                kmlTerrainGeoTiffPath ??
                throw new InvalidOperationException(
                    "导出带地形高程 KML 前请选择 WGS84 / EPSG:4326 地形 GeoTIFF。"))
            : null;
        var outputs = new List<string>();

        for (int i = 0; i < plan.Sorties.Count; i++)
        {
            int n = i + 1;
            RouteSortie sortie = plan.Sorties[i];
            string stem = GetSortieFileStem(sortie, n);
            if (exportKml)
            {
                string kmlPath = Path.Combine(outputDirectory, stem + ".kml");
                outputs.Add(kmlIncludeElevation
                    ? WriteSortieKml(
                        kmlPath,
                        sortie, n, plan.Mode, plan.CentralMeridian,
                        kmlDem!, kmlAglM)
                    : WriteSortieKmlWithoutElevation(
                        kmlPath,
                        sortie, n, plan.Mode, plan.CentralMeridian));
            }
            if (exportCsv)
                outputs.Add(WriteSortieCsv(
                    Path.Combine(outputDirectory, stem + ".csv"),
                    sortie, n, plan.Mode, plan.CentralMeridian));
            if (exportKmz)
                outputs.Add(WriteDjiKmz(
                    Path.Combine(outputDirectory, stem + "_实时仿地.kmz"),
                    sortie, plan.Mode, plan.CentralMeridian,
                    plan.Options.KmzAglM, plan.Options.KmzSpeedMps));
        }

        return new RouteExportBundle(Path.GetFullPath(outputDirectory), outputs);
    }

    public static byte[] LoadEmbeddedDjiTemplate()
    {
        throw new NotSupportedException(
            "DJI M400 实时仿地 KMZ 当前冻结；其模板不再作为 MagOps 主程序编译资源。");
    }

    public static byte[] LoadEmbeddedRouteKmlTemplate()
    {
        using Stream stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream(EmbeddedRouteKmlTemplateName)
            ?? throw new InvalidOperationException(
                $"未找到嵌入式航线 KML 极简母版：{EmbeddedRouteKmlTemplateName}");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    private static string SafeFolderStem(string? value)
    {
        string text = (value ?? "").Trim();
        if (text.Length == 0)
            text = "工区";

        char[] invalid = Path.GetInvalidFileNameChars();
        string cleaned = string.Concat(
            text.Select(ch =>
                invalid.Contains(ch) ||
                ch is '/' or '\\'
                    ? '-'
                    : ch));

        cleaned = cleaned.Trim().TrimEnd('.');
        return cleaned.Length > 0
            ? cleaned
            : "工区";
    }

    private static string EscapeCsv(string value)
    {
        string s = value.Replace("\"", "\"\"");
        return s.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + s + "\"" : s;
    }

    private static void SetNodeText(XmlNode? node, string text)
    {
        if (node is not null) node.InnerText = text;
    }

    private static void EnsureParent(string path)
    {
        string? parent = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrWhiteSpace(parent))
            Directory.CreateDirectory(parent);
    }

    private static void SaveXmlUtf8(XmlDocument doc, string path)
    {
        var settings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            Indent = false,
            OmitXmlDeclaration = false
        };
        using XmlWriter writer = XmlWriter.Create(path, settings);
        doc.Save(writer);
    }
}

public sealed record RouteExportBundle(string OutputDirectory, List<string> OutputPaths);
