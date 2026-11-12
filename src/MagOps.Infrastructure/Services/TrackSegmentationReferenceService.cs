using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using MagOps.Core.Coordinates;
using MagOps.Core.Routes;

namespace MagOps.Infrastructure.Services;

public sealed record SegmentationReferenceGeometry(string Id, string Name,
    GeoCoordinate Start, GeoCoordinate End, string Kind, string Evidence)
{
    public string SourcePath { get; init; } = "";
    public string SourceHash { get; init; } = "";
    public string PathId { get; init; } = "";
    public int PathOrder { get; init; }
    public int CoordinateCount { get; init; }
    public IReadOnlyList<ReferenceGeometryVersion> Versions { get; init; } = [];
    public IReadOnlyList<string> AdjacentSurveyIds { get; init; } = [];
}

public sealed record ReferenceGeometryVersion(string Id, string Name, GeoCoordinate Start,
    GeoCoordinate End, string SourcePath, string SourceHash, string PathId, int PathOrder, int CoordinateCount);

public sealed record TrackRecognitionOptions(
    double AxisToleranceDegrees = 12,
    double StraightHeadingToleranceDegrees = 12,
    double MinimumSpanM = 60,
    double MinimumDurationSeconds = 5,
    double MinimumAlignedRatio = .8,
    double RepeatAxisToleranceDegrees = 3,
    double RepeatCrossTrackToleranceM = 6,
    double TurnConfirmationSeconds = 1,
    double DirectionWindowSeconds = 1)
{
    /// <summary>Optional expected survey-line spacing in metres; 0 disables spacing-assisted evidence.</summary>
    public double ExpectedLineSpacingM { get; init; }
    public void Validate()
    {
        double[] positive = [AxisToleranceDegrees, StraightHeadingToleranceDegrees, MinimumSpanM, MinimumDurationSeconds,
            MinimumAlignedRatio, RepeatAxisToleranceDegrees, RepeatCrossTrackToleranceM, TurnConfirmationSeconds, DirectionWindowSeconds];
        if (positive.Any(v => !double.IsFinite(v) || v <= 0) || AxisToleranceDegrees >= 90 || StraightHeadingToleranceDegrees >= 90 ||
            RepeatAxisToleranceDegrees >= 90 || MinimumAlignedRatio > 1 || DirectionWindowSeconds > 8 ||
            !double.IsFinite(ExpectedLineSpacingM) || ExpectedLineSpacingM < 0 || ExpectedLineSpacingM > 5000)
            throw new InvalidDataException("方向容差须大于0且小于90°，比例须为(0,1]，方向窗口不超过8秒；其余门槛须为有限正数。");
    }
}

/// <summary>Embedded, immutable reference snapshot: replay never rereads a mutable external file.</summary>
public sealed record TrackSegmentationInput
{
    public bool Enabled { get; init; }
    public bool UseDesignTasks { get; init; }
    public double? AxisDegrees { get; init; }
    public TrackRecognitionOptions RecognitionOptions { get; init; } = new();
    public WholeLegMatchOptions MatchOptions { get; init; } = new();
    /// <summary>Named, reproducible threshold profile; values remain embedded in the snapshot.</summary>
    public string ParameterProfile { get; init; } = "自定义";
    public int ParameterProfileVersion { get; init; } = 1;
    public double? PlannedSpeedMps { get; init; }
    public IReadOnlyDictionary<string, double> WindowPlannedSpeeds { get; init; } = new Dictionary<string, double>();
    public IReadOnlyList<GeoCoordinate> KnownTakeoffPositions { get; init; } = [];
    public string ReferenceSource { get; init; } = "";
    public string ReferenceFileHash { get; init; } = "";
    /// <summary>
    /// KML supplied by the operator describes the mission geometry. In this
    /// mode it is the authoritative source for line identities; unmatched
    /// straight tracks remain visible review evidence instead of creating new
    /// line identities. CSV/hand-built references keep the older assisted
    /// behaviour unless explicitly marked authoritative.
    /// </summary>
    public string ReferenceAuthority { get; init; } = "AUTO";
    public IReadOnlyList<SegmentationReferenceGeometry> ReferenceGeometry { get; init; } = [];
}

public static class TrackSegmentationParameterProfiles
{
    public static readonly IReadOnlyList<string> Names = ["精准直线", "草原风扰", "通用保守", "自定义"];
    public static TrackSegmentationInput Apply(string name, TrackSegmentationInput input)
    {
        return name switch
        {
            "精准直线" => input with { ParameterProfile = name,
                RecognitionOptions = input.RecognitionOptions with { AxisToleranceDegrees = 3, StraightHeadingToleranceDegrees = 12, DirectionWindowSeconds = 1, TurnConfirmationSeconds = 1 } },
            "草原风扰" => input with { ParameterProfile = name,
                RecognitionOptions = input.RecognitionOptions with { AxisToleranceDegrees = 5, StraightHeadingToleranceDegrees = 12, DirectionWindowSeconds = 1, TurnConfirmationSeconds = 1 } },
            "通用保守" => input with { ParameterProfile = name,
                RecognitionOptions = input.RecognitionOptions with { AxisToleranceDegrees = 12, StraightHeadingToleranceDegrees = 12, DirectionWindowSeconds = 1, TurnConfirmationSeconds = 1 } },
            _ => input with { ParameterProfile = "自定义" }
        };
    }
    public static TrackSegmentationInput ApplySpeed(double speed, TrackSegmentationInput input) =>
        input with { PlannedSpeedMps = speed, MatchOptions = input.MatchOptions with
        { MinimumFlightSpeedMps = 4, MaximumFlightSpeedMps = speed == 12 ? 14 : speed == 8 ? 12 : input.MatchOptions.MaximumFlightSpeedMps } };
    public static WholeLegMatchOptions ForWindow(TrackSegmentationInput input, string mission) =>
        input.WindowPlannedSpeeds.TryGetValue(mission, out double speed)
            ? ApplySpeed(speed, input).MatchOptions : input.MatchOptions;
}

public static class TrackSegmentationReferenceService
{
    public static string Fingerprint(TrackSegmentationInput? input) => input?.Enabled == true
        ? Hash(JsonSerializer.Serialize(input)) : input is not null && (input.MatchOptions != new WholeLegMatchOptions() ||
            input.PlannedSpeedMps.HasValue || input.WindowPlannedSpeeds.Count > 0 || input.KnownTakeoffPositions.Count > 0)
            ? Hash(JsonSerializer.Serialize(input)) : "";
    internal static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public static double Distance(GeoCoordinate a, GeoCoordinate b)
    { var (x, y) = EndpointLineRecognizer.Delta(a, b); return Math.Sqrt(x * x + y * y); }
    public static double AxisDifference(double a, double b) => Math.Min(SurveyDirection.Difference(a, b), SurveyDirection.Difference(a, b + 180));
    public static bool IsAuthoritative(TrackSegmentationInput input) => input.ReferenceGeometry.Count > 0 &&
        (input.ReferenceAuthority == "KML_AUTHORITATIVE" || input.ReferenceAuthority == "AUTO" &&
            (input.ReferenceGeometry.Any(g => Path.GetExtension(g.SourcePath).Equals(".kml", StringComparison.OrdinalIgnoreCase)) ||
             input.ReferenceSource.Split(';').Any(p => p.Trim().EndsWith(".kml", StringComparison.OrdinalIgnoreCase))));

    public static TrackSegmentationInput Import(string path, double? centralMeridian = null)
    {
        var geometries = new List<SegmentationReferenceGeometry>();
        string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        int pathNumber = 0;
        string currentPathId = "";
        int pathOrder = 0, coordinateCount = 0;
        if (Path.GetExtension(path).Equals(".kml", StringComparison.OrdinalIgnoreCase))
        {
            using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            var doc = XDocument.Load(reader);
            foreach (var placemark in doc.Descendants().Where(e => e.Name.LocalName == "Placemark"))
            {
                string name = placemark.Elements().FirstOrDefault(e => e.Name.LocalName == "name")?.Value ?? "航线";
                string metadata = string.Join(" ", placemark.Descendants().Where(e => e.Name.LocalName is "Data" or "SimpleData")
                    .Select(e => (string?)e.Attribute("name") + "=" + e.Value));
                string declared = DeclaredKind(name + " " + metadata);
                foreach (var line in placemark.Descendants().Where(e => e.Name.LocalName == "LineString"))
                {
                    currentPathId = hash + "/path-" + (++pathNumber);
                    pathOrder = 0;
                    var coordinates = line.Descendants().FirstOrDefault(e => e.Name.LocalName == "coordinates")?.Value ?? "";
                    var points = coordinates.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(token =>
                    {
                        var parts = token.Split(',');
                        if (parts.Length < 2) throw new InvalidDataException("KML 坐标缺少经纬度。");
                        return new GeoCoordinate(double.Parse(parts[0], CultureInfo.InvariantCulture), double.Parse(parts[1], CultureInfo.InvariantCulture));
                    }).ToList();
                    if (points.Any(p => !WorkareaSpatialService.ValidCoordinate(p.Longitude, p.Latitude)))
                        throw new InvalidDataException("KML 包含无效经纬度。");
                    coordinateCount = points.Count;
                    // A placemark can contain a whole mowing route. Split directed turns before classification.
                    foreach (var pair in SplitGeometry(points)) Add(name, pair.Start, pair.End, declared, "KML 转角切分");
                }
            }
            var eligible = geometries.Where(g => g.Kind == "REVIEW" && Distance(g.Start, g.End) >= 60).ToArray();
            var axis = DominantAxis(eligible.Select(g => (SurveyDirection.Bearing(g.Start, g.End) ?? 0, Distance(g.Start, g.End))));
            geometries = geometries.Select(g => g.Kind != "REVIEW" ? g : g with
            {
                Kind = axis.HasValue && Distance(g.Start, g.End) >= 60 &&
                    AxisDifference(SurveyDirection.Bearing(g.Start, g.End) ?? 0, axis.Value) <= 5 &&
                    eligible.Any(h => h.Id != g.Id && ParallelNeighbour(g, h))
                    ? "SURVEY" : "REVIEW",
                Evidence = g.Evidence + "；长直段与平行组推断，允许人工更改"
            }).ToList();
        }
        else
        {
            var raw = DelimitedTextReader.ReadRawRouteCsv(path);
            bool geographic = raw.All(p => WorkareaSpatialService.ValidCoordinate(p.X, p.Y));
            if (!geographic && !centralMeridian.HasValue)
                throw new InvalidDataException("投影端点 CSV 需要已确认的中央经线；不会将 X/Y 猜成经纬度。");
            GeoCoordinate Convert(RawRoutePoint p)
            {
                if (geographic) return new(p.X, p.Y);
                var (lon, lat) = CoordinateService.GaussKrugerInverse(p.X, p.Y, centralMeridian!.Value, "CGCS2000");
                if (!WorkareaSpatialService.ValidCoordinate(lon, lat)) throw new InvalidDataException("CSV 投影转换得到无效坐标。");
                return new(lon, lat);
            }
            if (raw.Count % 2 != 0) throw new InvalidDataException("测线 CSV 需每条测线两个端点，记录数必须为偶数。");
            for (int i = 0; i < raw.Count; i += 2)
            {
                if (!string.IsNullOrWhiteSpace(raw[i].Label) && raw[i].Label != raw[i + 1].Label)
                    throw new InvalidDataException($"CSV 第 {i + 1} 对端点的测线名不同，请使用 Line,X,Y 格式。");
                Add(string.IsNullOrWhiteSpace(raw[i].Label) ? $"参考{i / 2 + 1:D3}" : raw[i].Label,
                    Convert(raw[i]), Convert(raw[i + 1]), "SURVEY", geographic ? "CSV 经纬度端点" : $"CSV CGCS2000 高斯投影；中央经线 {centralMeridian}", false);
            }
        }
        if (geometries.Count == 0) throw new InvalidDataException("未发现可用的线状航线；区域 Polygon 不能作为测线。");
        return new()
        {
            Enabled = true,
            ReferenceSource = Path.GetFullPath(path),
            ReferenceFileHash = hash,
            ReferenceAuthority = Path.GetExtension(path).Equals(".kml", StringComparison.OrdinalIgnoreCase)
                ? "KML_AUTHORITATIVE" : "ASSISTED",
            ReferenceGeometry = ReferenceMissionGeometryService.Consolidate(geometries)
        };

        void Add(string name, GeoCoordinate a, GeoCoordinate b, string kind, string evidence, bool appendIndex = true)
        {
            if (Distance(a, b) < 1) return;
            string id = "ref-" + Hash($"{hash}/{geometries.Count}/{a}/{b}")[..20];
            geometries.Add(new(id, appendIndex ? name + $"·{geometries.Count + 1:D3}" : name, a, b, kind, evidence)
            { SourcePath = Path.GetFullPath(path), SourceHash = hash, PathId = currentPathId,
                PathOrder = pathOrder++, CoordinateCount = coordinateCount });
        }
    }

    /// <summary>
    /// Consolidates the plain/height/revision copies normally supplied beside
    /// one another. Identity is spatial, not file-hash based: a copy keeps the
    /// first stable geometry and records every merged source in its evidence.
    /// Distinct extents or non-parallel lines remain separate reviewable
    /// variants.
    /// </summary>
    public static IReadOnlyList<SegmentationReferenceGeometry> Consolidate(
        IEnumerable<SegmentationReferenceGeometry> source)
    {
        return ReferenceMissionGeometryService.Consolidate(source);
    }

    private static string DeclaredKind(string value)
    {
        value = value.ToLowerInvariant();
        if (value.Contains("连接") || value.Contains("connector") || value.Contains("transit") || value.Contains("return")) return "CONNECTOR";
        if (value.Contains("联络") || value.Contains("tie")) return "TIE";
        if (value.Contains("测线") || value.Contains("survey")) return "SURVEY";
        return "REVIEW";
    }

    private static bool ParallelNeighbour(SegmentationReferenceGeometry a, SegmentationReferenceGeometry b)
    {
        if (AxisDifference(SurveyDirection.Bearing(a.Start, a.End) ?? 0, SurveyDirection.Bearing(b.Start, b.End) ?? 0) > 5) return false;
        var (dx, dy) = EndpointLineRecognizer.Delta(a.Start, a.End);
        double length = Math.Sqrt(dx * dx + dy * dy);
        var (x, y) = EndpointLineRecognizer.Delta(a.Start, b.Start);
        double separation = Math.Abs(x * dy - y * dx) / length;
        if (separation < 12 || separation > 500) return false;
        var (bx, by) = EndpointLineRecognizer.Delta(a.Start, b.End);
        double first = (x * dx + y * dy) / length, last = (bx * dx + by * dy) / length;
        double overlap = Math.Max(0, Math.Min(length, Math.Max(first, last)) - Math.Max(0, Math.Min(first, last)));
        return overlap / Math.Max(1, Math.Min(length, Math.Abs(last - first))) > .2;
    }

    internal static double? DominantAxis(IEnumerable<(double Heading, double Weight)> values)
    {
        var items = values.ToArray();
        if (items.Length == 0) return null;
        double best = Enumerable.Range(0, 180).OrderByDescending(a => items.Where(v => AxisDifference(v.Heading, a) <= 5).Sum(v => v.Weight)).First();
        var core = items.Where(v => AxisDifference(v.Heading, best) <= 5).ToArray();
        double x = core.Sum(v => Math.Cos(v.Heading * Math.PI / 90) * v.Weight);
        double y = core.Sum(v => Math.Sin(v.Heading * Math.PI / 90) * v.Weight);
        return (Math.Atan2(y, x) * 90 / Math.PI + 180) % 180;
    }

    internal static IReadOnlyList<(GeoCoordinate Start, GeoCoordinate End)> SplitGeometry(List<GeoCoordinate> points)
    {
        var result = new List<(GeoCoordinate, GeoCoordinate)>();
        int start = 0;
        double? basis = null;
        for (int i = 1; i < points.Count; i++)
        {
            double? heading = SurveyDirection.Bearing(points[i - 1], points[i]);
            if (heading is null) continue;
            basis ??= heading;
            if (SurveyDirection.Difference(heading.Value, basis.Value) > 8)
            { if (i - 1 > start) result.Add((points[start], points[i - 1])); start = i - 1; basis = heading; }
        }
        if (points.Count - 1 > start) result.Add((points[start], points[^1]));
        return result;
    }
}
