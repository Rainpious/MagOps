using System.Globalization;
using System.Reflection;
using System.Text;
using System.Xml.Linq;

namespace MagOps.Infrastructure.Services;

public sealed record KmlRangeTemplateInfo(
    string Key,
    string DisplayName,
    string ResourceName);

public sealed record KmlRangeExportMetadata(
    string ProjectName,
    string WorkareaName,
    string Source,
    string CoordinateStatus = "待核查",
    double? CentralMeridian = null);

public sealed record KmlRangeFormat
{
    public string LineColorHex { get; init; } = "#87CEEB";
    public double LineWidth { get; init; } = 3d;
    public string FillColorHex { get; init; } = "#87CEEB";
    public int FillOpacityPercent { get; init; } = 40;
    public bool Fill { get; init; } = true;
    public bool Outline { get; init; } = true;
    public bool Tessellate { get; init; } = true;
    public string AltitudeMode { get; init; } = "clampToGround";
    public int CoordinateDecimals { get; init; } = 8;
    public string PlacemarkNamePattern { get; init; } = "{workarea}作业范围";
}

public sealed class KmlRangeExportService
{
    public const string SkyBlueTemplateKey = "skyblue";
    public const string RedYellowTemplateKey = "red-yellow";

    private static readonly IReadOnlyList<KmlRangeTemplateInfo> BuiltIns =
    [
        new(
            SkyBlueTemplateKey,
            "天青边 / 天青填充",
            "MagOps.Infrastructure.Resources.KmlTemplates.边缘-天青_填充-天青.kml"),
        new(
            RedYellowTemplateKey,
            "红色边 / 黄色填充",
            "MagOps.Infrastructure.Resources.KmlTemplates.边缘-红色_填充-黄色.kml")
    ];

    public IReadOnlyList<KmlRangeTemplateInfo> GetBuiltInTemplates() => BuiltIns;

    public KmlRangeFormat LoadBuiltInFormat(string key)
    {
        KmlRangeTemplateInfo template = BuiltIns.FirstOrDefault(x =>
            x.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"未知 KML 内置模板：{key}");

        Assembly assembly = typeof(KmlRangeExportService).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(template.ResourceName)
            ?? throw new FileNotFoundException($"内置 KML 模板资源不可用：{template.DisplayName}");
        return ParseFormat(XDocument.Load(stream));
    }

    public KmlRangeFormat LoadFormatFromFile(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("KML 模板文件不存在。", path);
        return ParseFormat(XDocument.Load(path));
    }

    public string WriteRange(
        string path,
        IEnumerable<GeoCoordinate> sourcePoints,
        KmlRangeFormat format,
        KmlRangeExportMetadata metadata)
    {
        ValidateFormat(format);
        List<GeoCoordinate> points = NormalizeRing(sourcePoints);
        if (points.Count < 4)
            throw new InvalidDataException("导出 KML 范围至少需要 3 个不同的坐标点。");

        string projectName = SafeText(metadata.ProjectName, "临时");
        string workareaName = SafeText(metadata.WorkareaName, "未指定工区");
        string source = SafeText(metadata.Source, "坐标转换结果");
        string status = SafeText(metadata.CoordinateStatus, "待核查");

        string placemarkName = ExpandText(
            format.PlacemarkNamePattern,
            projectName,
            workareaName,
            source,
            status);

        string coordinateText = string.Join(
            Environment.NewLine,
            points.Select(p =>
                $"{FormatCoordinate(p.Longitude, format.CoordinateDecimals)}," +
                $"{FormatCoordinate(p.Latitude, format.CoordinateDecimals)}," +
                $"{FormatCoordinate(p.Elevation ?? 0d, Math.Min(format.CoordinateDecimals, 3))}"));

        var style = new XElement(
            "Style",
            new XElement(
                "LineStyle",
                new XElement("color", ToKmlColor(format.LineColorHex, 255)),
                new XElement(
                    "width",
                    format.LineWidth.ToString(
                        "0.###",
                        CultureInfo.InvariantCulture))),
            new XElement(
                "PolyStyle",
                new XElement(
                    "color",
                    ToKmlColor(
                        format.FillColorHex,
                        PercentToAlpha(format.FillOpacityPercent))),
                new XElement("fill", format.Fill ? "1" : "0"),
                new XElement("outline", format.Outline ? "1" : "0")));

        var polygon = new XElement(
            "Polygon",
            new XElement("tessellate", format.Tessellate ? "1" : "0"),
            new XElement("altitudeMode", format.AltitudeMode),
            new XElement(
                "outerBoundaryIs",
                new XElement(
                    "LinearRing",
                    new XElement(
                        "coordinates",
                        Environment.NewLine +
                        coordinateText +
                        Environment.NewLine))));

        var placemark = new XElement(
            "Placemark",
            new XElement("name", placemarkName),
            new XElement("visibility", "1"),
            style,
            polygon);

        var output = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XElement(
                "kml",
                new XElement(
                    "Document",
                    placemark)));

        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        using var writer = new StreamWriter(
            fullPath,
            false,
            new UTF8Encoding(false));
        output.Save(writer);
        return fullPath;
    }

    public static string FormatStyleSuffix(KmlRangeFormat format) =>
        $"边缘-{ColorName(format.LineColorHex)}_填充-{ColorName(format.FillColorHex)}";

    public static void ValidateFormat(KmlRangeFormat format)
    {
        _ = NormalizeHexColor(format.LineColorHex);
        _ = NormalizeHexColor(format.FillColorHex);
        if (format.LineWidth <= 0d || format.LineWidth > 50d)
            throw new InvalidDataException("KML 线宽必须大于 0 且不超过 50。");
        if (format.FillOpacityPercent is < 0 or > 100)
            throw new InvalidDataException("KML 填充透明度必须在 0–100% 之间。");
        if (format.CoordinateDecimals is < 0 or > 12)
            throw new InvalidDataException("KML 坐标小数位数必须在 0–12 之间。");
        if (format.AltitudeMode is not ("clampToGround" or "relativeToGround" or "absolute"))
            throw new InvalidDataException("KML altitudeMode 仅支持 clampToGround、relativeToGround 或 absolute。");
        if (string.IsNullOrWhiteSpace(format.PlacemarkNamePattern))
            throw new InvalidDataException("Placemark 名称不能为空。");
    }

    public static string NormalizeHexColor(string input)
    {
        string value = (input ?? "").Trim().TrimStart('#').ToUpperInvariant();
        if (value.Length != 6 || value.Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidDataException($"颜色必须是 #RRGGBB：{input}");
        return "#" + value;
    }

    private static KmlRangeFormat ParseFormat(XDocument document)
    {
        XElement? lineStyle = document
            .Descendants()
            .FirstOrDefault(x => x.Name.LocalName == "LineStyle");
        XElement? polyStyle = document
            .Descendants()
            .FirstOrDefault(x => x.Name.LocalName == "PolyStyle");
        XElement? polygon = document
            .Descendants()
            .FirstOrDefault(x => x.Name.LocalName == "Polygon");
        XElement? placemark = document
            .Descendants()
            .FirstOrDefault(x => x.Name.LocalName == "Placemark");

        string lineKmlColor = ChildValue(lineStyle, "color", "FFEBCE87");
        string fillKmlColor = ChildValue(polyStyle, "color", "66EBCE87");
        ParseKmlColor(lineKmlColor, out string lineHex, out _);
        ParseKmlColor(fillKmlColor, out string fillHex, out byte fillAlpha);

        double width = ParseDouble(ChildValue(lineStyle, "width", "3"), 3d);
        bool fill = ParseBool01(ChildValue(polyStyle, "fill", "1"), true);
        bool outline = ParseBool01(ChildValue(polyStyle, "outline", "1"), true);
        bool tessellate = ParseBool01(ChildValue(polygon, "tessellate", "1"), true);
        string altitudeMode = ChildValue(
            polygon,
            "altitudeMode",
            "clampToGround");
        if (altitudeMode is not ("clampToGround" or "relativeToGround" or "absolute"))
            altitudeMode = "clampToGround";

        string name = ChildValue(
                placemark,
                "name",
                "{workarea}作业范围")
            .Replace("【项目名称】", "{project}", StringComparison.Ordinal)
            .Replace("【工区名称】", "{workarea}", StringComparison.Ordinal)
            .Replace("【许可区编号】", "{workarea}", StringComparison.Ordinal);

        var format = new KmlRangeFormat
        {
            LineColorHex = lineHex,
            LineWidth = width,
            FillColorHex = fillHex,
            FillOpacityPercent = AlphaToPercent(fillAlpha),
            Fill = fill,
            Outline = outline,
            Tessellate = tessellate,
            AltitudeMode = altitudeMode,
            CoordinateDecimals = 8,
            PlacemarkNamePattern = name
        };
        ValidateFormat(format);
        return format;
    }

    private static List<GeoCoordinate> NormalizeRing(
        IEnumerable<GeoCoordinate> sourcePoints)
    {
        var points = sourcePoints
            .Where(x =>
                !double.IsNaN(x.Longitude) &&
                !double.IsNaN(x.Latitude))
            .ToList();
        if (points.Count < 3)
            return points;

        int distinct = points
            .Select(x => (
                Math.Round(x.Longitude, 12),
                Math.Round(x.Latitude, 12)))
            .Distinct()
            .Count();
        if (distinct < 3)
            throw new InvalidDataException("导出 KML 范围至少需要 3 个不同的坐标点。");

        GeoCoordinate first = points[0];
        GeoCoordinate last = points[^1];
        if (Math.Abs(first.Longitude - last.Longitude) > 1e-12 ||
            Math.Abs(first.Latitude - last.Latitude) > 1e-12)
        {
            points.Add(first);
        }

        return points;
    }


    private static string ChildValue(
        XElement? parent,
        string localName,
        string fallback)
    {
        string? value = parent?
            .Elements()
            .FirstOrDefault(x => x.Name.LocalName == localName)?
            .Value;
        return string.IsNullOrWhiteSpace(value)
            ? fallback
            : value.Trim();
    }

    private static double ParseDouble(string text, double fallback) =>
        double.TryParse(
            text,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out double value)
            ? value
            : fallback;

    private static bool ParseBool01(string text, bool fallback) =>
        text.Trim() switch
        {
            "1" => true,
            "0" => false,
            _ => fallback
        };

    private static string ExpandText(
        string template,
        string project,
        string workarea,
        string source,
        string status) =>
        (template ?? "")
            .Replace("{project}", project, StringComparison.OrdinalIgnoreCase)
            .Replace("{workarea}", workarea, StringComparison.OrdinalIgnoreCase)
            .Replace("{source}", source, StringComparison.OrdinalIgnoreCase)
            .Replace("{status}", status, StringComparison.OrdinalIgnoreCase);

    private static string SafeText(string value, string fallback) =>
        string.IsNullOrWhiteSpace(value)
            ? fallback
            : value.Trim();

    private static string FormatCoordinate(double value, int decimals) =>
        value.ToString(
            decimals == 0
                ? "0"
                : "0." + new string('#', decimals),
            CultureInfo.InvariantCulture);

    private static int PercentToAlpha(int percent) =>
        Math.Clamp(
            (int)Math.Round(
                percent / 100d * 255d,
                MidpointRounding.AwayFromZero),
            0,
            255);

    private static int AlphaToPercent(byte alpha) =>
        Math.Clamp(
            (int)Math.Round(
                alpha / 255d * 100d,
                MidpointRounding.AwayFromZero),
            0,
            100);

    private static string ToKmlColor(string hexRgb, int alpha)
    {
        string value = NormalizeHexColor(hexRgb).TrimStart('#');
        string rr = value[..2];
        string gg = value.Substring(2, 2);
        string bb = value.Substring(4, 2);
        return $"{Math.Clamp(alpha, 0, 255):X2}{bb}{gg}{rr}";
    }

    private static void ParseKmlColor(
        string input,
        out string hexRgb,
        out byte alpha)
    {
        string value = (input ?? "").Trim().TrimStart('#');
        if (value.Length != 8 || value.Any(c => !Uri.IsHexDigit(c)))
            value = "FFEBCE87";

        alpha = byte.Parse(
            value[..2],
            NumberStyles.HexNumber,
            CultureInfo.InvariantCulture);
        string bb = value.Substring(2, 2);
        string gg = value.Substring(4, 2);
        string rr = value.Substring(6, 2);
        hexRgb = $"#{rr}{gg}{bb}".ToUpperInvariant();
    }

    private static string ColorName(string hex)
    {
        string normalized = NormalizeHexColor(hex);
        return normalized switch
        {
            "#87CEEB" => "天青",
            "#FF0000" => "红色",
            "#FFFF00" => "黄色",
            "#000000" => "黑色",
            "#FFFFFF" => "白色",
            "#0000FF" => "蓝色",
            "#00FF00" => "绿色",
            _ => normalized.TrimStart('#')
        };
    }
}
