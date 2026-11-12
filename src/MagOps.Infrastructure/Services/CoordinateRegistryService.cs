using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MagOps.Core.Coordinates;
using MagOps.Core.Domain;
using MagOps.Infrastructure.Storage;

namespace MagOps.Infrastructure.Services;

public sealed record GeometryAuditResult(
    string Status,
    int GeometryCount,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings);

public sealed record CoordinateSummaryResult(
    int Count,
    IReadOnlyDictionary<string, int> ByCrs,
    IReadOnlyDictionary<string, int> ByHeightType,
    int MissingCrs);

public sealed class CoordinateRegistryService
{
    public static readonly string[] HeightTypes =
        ["terrain_elevation_m", "planned_agl_m", "planned_altitude_m"];

    private readonly JsonStore _store;
    private readonly WorkspaceService _workspace;

    public CoordinateRegistryService(JsonStore store, WorkspaceService workspace)
    {
        _store = store;
        _workspace = workspace;
    }

    public List<CoordinateRecord> List(string root)
    {
        _workspace.GetWorkspace(root);
        return _store.Read(ProjectPaths.Json(root, "coordinates.json"), new List<CoordinateRecord>());
    }

    public List<CoordinateRecord> Register(
        string root,
        IEnumerable<CoordinateRecord> coordinates,
        string sourceArtifact = "")
    {
        using var writeLock = WorkspaceFileLock.AcquireForRoot(root);
        var rows = List(root);
        var existing = rows.Select(x => x.CoordinateId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = new List<CoordinateRecord>();

        foreach (var coordinate in coordinates)
        {
            ValidateCoordinate(coordinate);
            if (!string.IsNullOrWhiteSpace(sourceArtifact) && string.IsNullOrWhiteSpace(coordinate.SourceArtifact))
                coordinate.SourceArtifact = sourceArtifact;
            if (string.IsNullOrWhiteSpace(coordinate.CoordinateId) || existing.Contains(coordinate.CoordinateId))
                throw new InvalidOperationException($"坐标 ID 为空或已存在: {coordinate.CoordinateId}");
            existing.Add(coordinate.CoordinateId);
            added.Add(coordinate);
        }

        rows.AddRange(added);
        _store.Write(ProjectPaths.Json(root, "coordinates.json"), rows);
        _store.AppendJsonLine(ProjectPaths.DataDirectory(root), "coordinates_registered",
            new { count = added.Count, source_artifact = sourceArtifact });
        return added;
    }

    public List<CrsDefinitionRecord> CrsList(string root)
    {
        _workspace.GetWorkspace(root);
        return _store.Read(ProjectPaths.Json(root, "crs_definitions.json"), new List<CrsDefinitionRecord>());
    }

    public CrsDefinitionRecord RegisterCrs(string root, CrsDefinitionRecord definition)
    {
        ValidateCrs(definition);
        using var writeLock = WorkspaceFileLock.AcquireForRoot(root);
        var rows = CrsList(root);
        rows.RemoveAll(x => x.Name == definition.Name);
        rows.Add(definition);
        _store.Write(ProjectPaths.Json(root, "crs_definitions.json"), rows);
        _store.AppendJsonLine(ProjectPaths.DataDirectory(root), "crs_registered",
            new { name = definition.Name, epsg = definition.Epsg });
        return definition;
    }

    public CoordinateSummaryResult Summary(string root)
    {
        var rows = List(root);
        var byCrs = rows.Where(x => !string.IsNullOrWhiteSpace(x.Crs))
            .GroupBy(x => x.Crs)
            .OrderBy(x => x.Key)
            .ToDictionary(x => x.Key, x => x.Count());
        var byHeight = HeightTypes.ToDictionary(
            h => h,
            h => rows.Count(x => x.ZType == h));
        return new CoordinateSummaryResult(
            rows.Count,
            byCrs,
            byHeight,
            rows.Count(x => string.IsNullOrWhiteSpace(x.Crs)));
    }

    public List<CoordinateRecord> ReadCoordinateCsv(
        string path,
        string sourceCrs = "",
        string zType = "",
        string sourceArtifact = "")
    {
        if (!string.IsNullOrWhiteSpace(zType) && !HeightTypes.Contains(zType))
            throw new ArgumentException($"无效高度类型: {zType}");

        var sourceDefinition = string.IsNullOrWhiteSpace(sourceCrs)
            ? new CrsDefinitionRecord { Name = "", Kind = "geographic" }
            : ParseKnownCrs(sourceCrs);

        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        string? headerLine = reader.ReadLine();
        if (headerLine is null)
            throw new InvalidDataException("CSV 缺少表头");

        var headers = ParseCsvLine(headerLine);
        if (headers.Count == 0)
            throw new InvalidDataException("CSV 缺少表头");

        var fields = headers
            .Select((name, index) => (Name: name.Trim().ToLowerInvariant(), Index: index))
            .Where(x => x.Name.Length > 0)
            .ToDictionary(x => x.Name, x => x.Index);

        var rows = new List<CoordinateRecord>();
        int lineNo = 1;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNo++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            var cells = ParseCsvLine(line);

            string Field(params string[] aliases)
            {
                foreach (string alias in aliases)
                    if (fields.TryGetValue(alias.ToLowerInvariant(), out int index) && index < cells.Count)
                        return cells[index];
                return "";
            }

            string lonRaw = Field("longitude", "lon", "lng", "经度");
            string latRaw = Field("latitude", "lat", "纬度");
            string xRaw = Field("x", "x_m", "easting", "east", "东坐标");
            string yRaw = Field("y", "y_m", "northing", "north", "北坐标");

            double? lon = null, lat = null;
            if (string.IsNullOrWhiteSpace(lonRaw) && string.IsNullOrWhiteSpace(latRaw))
            {
                if (!(sourceDefinition.Kind == "projected"
                    && !string.IsNullOrWhiteSpace(xRaw)
                    && !string.IsNullOrWhiteSpace(yRaw)))
                    continue;
            }
            else
            {
                lon = CoordinateService.ParseDms(lonRaw, "lon");
                lat = CoordinateService.ParseDms(latRaw, "lat");
            }

            string zRaw = Field("z", "z_m", "elevation", "高程", "height");
            var coordinate = new CoordinateRecord
            {
                CoordinateId = NonEmpty(Field("coordinate_id", "id", "point", "点号"),
                    $"{Path.GetFileNameWithoutExtension(path)}-{lineNo:0000}"),
                SourceValue = $"{NonEmpty(lonRaw, xRaw)},{NonEmpty(latRaw, yRaw)}",
                LongitudeDeg = lon,
                LatitudeDeg = lat,
                XM = NumberOrNull(xRaw),
                YM = NumberOrNull(yRaw),
                ZM = NumberOrNull(zRaw),
                ZType = zType,
                Crs = sourceCrs,
                SourceArtifact = string.IsNullOrWhiteSpace(sourceArtifact) ? path.Replace('\\', '/') : sourceArtifact,
                SourceRow = lineNo,
                AxisOrder = sourceDefinition.AxisOrder,
                Notes = Field("notes", "备注")
            };
            ValidateCoordinate(coordinate);
            rows.Add(coordinate);
        }
        return rows;
    }

    public string WriteCoordinateCsv(string path, IEnumerable<CoordinateRecord> coordinates)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        WriteCsv(writer, "coordinate_id", "source_value", "lon_deg", "lat_deg", "x_m", "y_m",
            "z_m", "z_type", "crs", "source_artifact", "source_row", "axis_order", "notes");
        foreach (var row in coordinates)
        {
            WriteCsv(writer,
                row.CoordinateId,
                row.SourceValue,
                F(row.LongitudeDeg),
                F(row.LatitudeDeg),
                F(row.XM),
                F(row.YM),
                F(row.ZM),
                row.ZType,
                row.Crs,
                row.SourceArtifact,
                row.SourceRow?.ToString(CultureInfo.InvariantCulture) ?? "",
                row.AxisOrder,
                row.Notes);
        }
        return path;
    }

    public string ExportConversionCsv(
        string destination,
        BatchConversionResult batch,
        CoordinateInputMode mode,
        ProjectionParameters parameters,
        int decimalPlaces = 8,
        bool includeZoneNumber = false)
    {
        if (decimalPlaces is < 0 or > 12)
            throw new ArgumentOutOfRangeException(nameof(decimalPlaces));
        parameters = CoordinateProjectionService.Normalize(parameters);
        if (includeZoneNumber && parameters.Kind != CoordinateProjectionKind.GaussKruger)
            throw new ArgumentException("UTM/RSO不使用高斯百万位带号。");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        using var writer = new StreamWriter(destination, false, new UTF8Encoding(true));

        string xHeader = parameters.AxisOrder == XyAxisOrder.EastingNorthing ? "X_东向_m" : "X_北向_m";
        string yHeader = parameters.AxisOrder == XyAxisOrder.EastingNorthing ? "Y_北向_m" : "Y_东向_m";
        WriteCsv(writer, "序号", "经度_十进制度", "纬度_十进制度", "经度_度分秒", "纬度_度分秒",
            xHeader, yHeader, "", "参数", "参数值");

        string modeLabel = mode switch
        {
            CoordinateInputMode.Dms => "度分秒",
            CoordinateInputMode.DecimalDegrees => "小数格式（十进制度）",
            _ => "XY 投影坐标"
        };
        string axis = parameters.AxisOrder == XyAxisOrder.EastingNorthing
            ? "GIS：X=Easting，Y=Northing"
            : "测绘：X=Northing，Y=Easting";
        int zoneNumber = CoordinateService.GetGk3ZoneNumber(batch.CentralMeridian);
        var parameterRows = new[]
        {
            ("原始坐标格式", modeLabel),
            ("坐标基准", parameters.Datum),
            ("中央经线", $"{batch.CentralMeridian:g}°"),
            ("三度带号", parameters.Kind == CoordinateProjectionKind.GaussKruger ? zoneNumber.ToString(CultureInfo.InvariantCulture) : "不适用"),
            ("添加带号", includeZoneNumber ? "是（仅东向坐标）" : "否"),
            ("投影方式", CoordinateProjectionService.Label(parameters)),
            ("坐标轴顺序", axis),
            ("假东", parameters.FalseEasting.ToString("0.###", CultureInfo.InvariantCulture) + " m"),
            ("假北", parameters.FalseNorthing.ToString("0.###", CultureInfo.InvariantCulture) + " m"),
            ("小数坐标保留位数", decimalPlaces.ToString(CultureInfo.InvariantCulture))
        }.Concat(new[]
        {
            ("投影代码", parameters.Kind.ToString()),
            ("UTM带号", parameters.Kind == CoordinateProjectionKind.Utm ? parameters.UtmZone.ToString(CultureInfo.InvariantCulture) : "不适用"),
            ("UTM半球", parameters.Kind == CoordinateProjectionKind.Utm ? (parameters.SouthernHemisphere ? "S" : "N") : "不适用"),
            ("经纬度基准", parameters.Kind == CoordinateProjectionKind.GaussKruger ? parameters.Datum : "WGS84 (EPSG:4326)"),
            ("基准转换", parameters.Kind == CoordinateProjectionKind.KertauRsoMalaya ? CoordinateProjectionService.RsoWarning : "不执行基准转换")
        }).Concat(CoordinateProjectionService.DefinitionParameters(parameters).Select(p => (p.Name, p.Value)))
          .Concat(MeasurementParameters(batch, parameters).Select(p => (p.Name, p.Value))).ToArray();

        for (int i = 0; i < batch.Results.Count; i++)
        {
            var r = batch.Results[i];
            string param = i < parameterRows.Length ? parameterRows[i].Item1 : "";
            string paramValue = i < parameterRows.Length ? parameterRows[i].Item2 : "";
            (double xOut, double yOut) = CoordinateOutputValues(
                r, parameters.AxisOrder, batch.CentralMeridian, includeZoneNumber);
            string sequenceOrPointId = string.IsNullOrWhiteSpace(r.PointId)
                ? (i + 1).ToString(CultureInfo.InvariantCulture)
                : r.PointId.Trim();
            WriteCsv(writer,
                sequenceOrPointId,
                r.Longitude.ToString($"F{decimalPlaces}", CultureInfo.InvariantCulture),
                r.Latitude.ToString($"F{decimalPlaces}", CultureInfo.InvariantCulture),
                r.LongitudeDms, r.LatitudeDms,
                xOut.ToString("0.###", CultureInfo.InvariantCulture),
                yOut.ToString("0.###", CultureInfo.InvariantCulture),
                "", param, paramValue);
        }
        // Metadata must survive even for a one-point or short boundary export.
        for (int i = batch.Results.Count; i < parameterRows.Length; i++)
            WriteCsv(writer, "", "", "", "", "", "", "", "", parameterRows[i].Item1, parameterRows[i].Item2);
        return destination;
    }

    public static IReadOnlyList<(string Name, string Value)> MeasurementParameters(BatchConversionResult batch, ProjectionParameters parameters)
    {
        if (batch.Results.Count == 0) return [];
        var p = CoordinateProjectionService.Normalize(parameters with { CentralMeridian = batch.CentralMeridian });
        var center = CoordinateGeometryMath.Centroid(batch.Results);
        var geo = CoordinateProjectionService.Inverse(center.Easting, center.Northing, p);
        double area = CoordinateGeometryMath.PolygonAreaSquareMeters(batch.Results);
        string F(double value, string format) => value.ToString(format, CultureInfo.InvariantCulture);
        return new[]
        {
            ("测算面积_m2", F(area, "0.###")),
            ("测算面积_km2", F(area / 1e6, "0.#########")),
            ("测算面积_ha", F(area / 1e4, "0.#######")),
            ("工区重心类型", center.IsAreaCentroid ? "按输入点序的多边形面积重心" : "不足三点或退化：顶点平均中心（非面积重心）"),
            ("工区重心_E_m", F(center.Easting, "0.###")),
            ("工区重心_N_m", F(center.Northing, "0.###")),
            ("工区重心经度_deg", F(geo.Longitude, "0.########")),
            ("工区重心纬度_deg", F(geo.Latitude, "0.########")),
            ("重心经纬度基准", p.Kind == CoordinateProjectionKind.GaussKruger ? p.Datum : "WGS84"),
            ("边界测算口径", "单环按输入点序闭合的投影平面测算；未验证完整边界，不支持孔洞/多区域；重心可能在凹面外。")
        };
    }

    public static (double X, double Y) CoordinateOutputValues(
        CoordinateConversionResult row,
        XyAxisOrder axisOrder,
        double centralMeridian,
        bool includeZoneNumber)
    {
        if (includeZoneNumber && row.ProjectionKind != CoordinateProjectionKind.GaussKruger)
            throw new ArgumentException("UTM/RSO不使用高斯百万位带号。");
        double easting = includeZoneNumber
            ? CoordinateService.AddGk3ZoneNumberToEasting(
                row.Easting, centralMeridian)
            : row.Easting;

        return axisOrder == XyAxisOrder.NorthingEasting
            ? (row.Northing, easting)
            : (easting, row.Northing);
    }

    public string ExportCleanXyPly(
        string destination,
        BatchConversionResult batch)
    {
        if (batch.Results.Count == 0)
            throw new InvalidOperationException("没有可导出的 XY 坐标。");

        string full = Path.GetFullPath(destination);
        Directory.CreateDirectory(
            Path.GetDirectoryName(full)
            ?? throw new InvalidOperationException("PLY 输出目录无效。"));

        using var writer = new StreamWriter(
            full,
            false,
            new UTF8Encoding(false));

        foreach (CoordinateConversionResult row in batch.Results)
        {
            if (!double.IsFinite(row.X) || !double.IsFinite(row.Y))
                throw new InvalidDataException("坐标结果包含非有限 X/Y，不能导出 PLY。");

            writer.Write(
                row.X.ToString(
                    "0.##########",
                    CultureInfo.InvariantCulture));
            writer.Write('\t');
            writer.WriteLine(
                row.Y.ToString(
                    "0.##########",
                    CultureInfo.InvariantCulture));
        }

        return full;
    }

    public GeometryAuditResult AuditSpatialFile(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        List<SpatialGeometry> geometries = ext switch
        {
            ".kml" => SpatialGeometryService.ParseKml(path),
            ".gpx" => SpatialGeometryService.ParseGpx(path),
            _ => throw new NotSupportedException($"当前仅支持 KML / GPX，文件类型为 {ext}")
        };

        var summary = SpatialGeometryService.AuditGeometries(geometries);
        var warnings = summary.Geometries
            .SelectMany((x, i) => x.Warnings.Select(w => $"#{i + 1} {x.Type}: {w}"))
            .ToList();
        var errors = summary.Errors.Concat(
            summary.Geometries.SelectMany((x, i) => x.Errors.Select(e => $"#{i + 1} {x.Type}: {e}")))
            .ToList();

        return new GeometryAuditResult(summary.Status, summary.Count, errors, warnings);
    }

    public SpatialRoundtripAudit ConvertSpatialFile(string source, string destination)
    {
        string sourceExt = Path.GetExtension(source).ToLowerInvariant();
        string destExt = Path.GetExtension(destination).ToLowerInvariant();

        List<SpatialGeometry> original = sourceExt switch
        {
            ".kml" => SpatialGeometryService.ParseKml(source),
            ".gpx" => SpatialGeometryService.ParseGpx(source),
            _ => throw new NotSupportedException("源文件必须是 KML 或 GPX")
        };

        if (destExt == ".kml") SpatialGeometryService.WriteKml(destination, original);
        else if (destExt == ".gpx") SpatialGeometryService.WriteGpx(destination, original);
        else throw new NotSupportedException("目标文件必须是 KML 或 GPX");

        List<SpatialGeometry> regenerated = destExt == ".kml"
            ? SpatialGeometryService.ParseKml(destination)
            : SpatialGeometryService.ParseGpx(destination);
        return SpatialGeometryService.AuditRoundtrip(original, regenerated);
    }

    public static CrsDefinitionRecord ParseKnownCrs(string value)
    {
        string text = (value ?? "").Trim();
        string upper = text.ToUpperInvariant();
        if (upper is "WGS84" or "EPSG:4326" or "CGCS2000" or "EPSG:4490")
        {
            return new CrsDefinitionRecord
            {
                Name = text,
                Epsg = upper,
                Datum = upper.Contains("CGCS") || upper.Contains("4490") ? "CGCS2000" : "WGS84"
            };
        }

        if (upper is "WEBMERCATOR" or "WEB MERCATOR" or "EPSG:3857")
        {
            return new CrsDefinitionRecord
            {
                Name = text,
                Kind = "projected",
                Epsg = "EPSG:3857",
                Datum = "WGS84",
                Unit = "metre",
                AxisOrder = "x,y"
            };
        }

        var match = Regex.Match(upper, @"(?:UTM|ZONE)\s*(6[0]|[1-5]\d|[1-9])");
        if (match.Success)
        {
            int zone = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            return new CrsDefinitionRecord
            {
                Name = text,
                Kind = "projected",
                Epsg = $"UTM:{zone}",
                Datum = "WGS84",
                CentralMeridian = zone * 6 - 183,
                FalseEastingM = 500000,
                Unit = "metre",
                AxisOrder = "x,y"
            };
        }

        throw new ArgumentException($"未登记或不支持的 CRS: {value}");
    }

    private static void ValidateCoordinate(CoordinateRecord row)
    {
        if (!string.IsNullOrWhiteSpace(row.ZType) && !HeightTypes.Contains(row.ZType))
            throw new ArgumentException($"无效高度类型: {row.ZType}");
        if (row.LongitudeDeg.HasValue && (row.LongitudeDeg < -180 || row.LongitudeDeg > 180))
            throw new FormatException($"经度超出范围: {row.LongitudeDeg}");
        if (row.LatitudeDeg.HasValue && (row.LatitudeDeg < -90 || row.LatitudeDeg > 90))
            throw new FormatException($"纬度超出范围: {row.LatitudeDeg}");
        foreach (var (label, value) in new[] { ("X", row.XM), ("Y", row.YM), ("Z", row.ZM) })
            if (value.HasValue && !double.IsFinite(value.Value))
                throw new FormatException($"{label} 坐标不能是无穷或 NaN");
    }

    private static void ValidateCrs(CrsDefinitionRecord row)
    {
        if (string.IsNullOrWhiteSpace(row.Name)) throw new ArgumentException("CRS 名称不能为空");
        if (row.Kind != "geographic" && row.Kind != "projected")
            throw new ArgumentException("CRS kind 必须是 geographic 或 projected");
        if (row.AxisOrder is not "lon,lat" and not "lat,lon" and not "x,y" and not "y,x")
            throw new ArgumentException("不支持的 CRS 轴顺序");
        if (row.Kind == "geographic" && row.Unit is not "degree" and not "degrees")
            throw new ArgumentException("地理坐标 CRS 单位必须为 degree");
    }

    private static List<string> ParseCsvLine(string line)
    {
        var result = new List<string>();
        var cell = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"')
                {
                    cell.Append('"');
                    i++;
                }
                else quoted = !quoted;
            }
            else if (c == ',' && !quoted)
            {
                result.Add(cell.ToString());
                cell.Clear();
            }
            else
            {
                cell.Append(c);
            }
        }
        result.Add(cell.ToString());
        return result;
    }

    private static double? NumberOrNull(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string cleaned = text.Trim().Replace(",", "");
        if (!double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            || !double.IsFinite(value))
            throw new FormatException($"不是有效数字: {text}");
        return value;
    }

    private static string NonEmpty(params string[] values) =>
        values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "";

    private static string F(double? value) =>
        value?.ToString("R", CultureInfo.InvariantCulture) ?? "";

    private static void WriteCsv(TextWriter writer, params string[] values) =>
        writer.WriteLine(string.Join(",", values.Select(Csv)));

    private static string Csv(string? value)
    {
        string text = value ?? "";
        if (text.Contains('"')) text = text.Replace("\"", "\"\"");
        return text.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? $"\"{text}\"" : text;
    }
}
