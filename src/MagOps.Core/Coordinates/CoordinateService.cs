using System.Globalization;
using System.Text.RegularExpressions;

namespace MagOps.Core.Coordinates;

public sealed class CoordinateService
{
    private static readonly Regex NumberRegex = new(@"[-+]?\d+(?:\.\d+)?", RegexOptions.Compiled);
    private static readonly Regex CompactDmsRegex =
        new(@"^\s*([+-]?)(\d+)(?:\.(\d+))?\s*[NSEW]?\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static double ParseDms(string? value, string? axis = null, string? separators = null)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new FormatException("坐标为空");

        string raw = value.Trim();
        string upper = raw.ToUpperInvariant();
        var separatorChars = NormalizeSeparators(separators).Select(char.ToUpperInvariant).ToHashSet();

        var edgeMarks = new List<char>();
        string trimmed = upper.Trim();
        if (trimmed.Length > 0 && "NSEW".Contains(trimmed[0]) && !separatorChars.Contains(trimmed[0]))
            edgeMarks.Add(trimmed[0]);
        if (trimmed.Length > 0 && "NSEW".Contains(trimmed[^1]) && !separatorChars.Contains(trimmed[^1])
            && (edgeMarks.Count == 0 || trimmed[^1] != edgeMarks[0]))
            edgeMarks.Add(trimmed[^1]);

        if (edgeMarks.Count > 1)
            throw new FormatException($"坐标包含多个半球标记: {value}");

        char? hemisphere = edgeMarks.Count == 1 ? edgeMarks[0] : null;
        bool axisLon = IsLongitudeAxis(axis);
        bool axisLat = IsLatitudeAxis(axis);

        if (axisLon && hemisphere is 'N' or 'S')
            throw new FormatException($"经度不能使用 {hemisphere} 半球标记: {value}");
        if (axisLat && hemisphere is 'E' or 'W')
            throw new FormatException($"纬度不能使用 {hemisphere} 半球标记: {value}");

        var compact = CompactDmsRegex.Match(upper);
        if (compact.Success && (axisLon || axisLat))
        {
            string signText = compact.Groups[1].Value;
            string integer = compact.Groups[2].Value;
            string fraction = compact.Groups[3].Success ? compact.Groups[3].Value : "";
            double numeric = double.Parse(
                signText + integer + (fraction.Length > 0 ? "." + fraction : ""),
                CultureInfo.InvariantCulture);
            double limit = axisLon ? 180d : 90d;

            if (Math.Abs(numeric) > limit)
            {
                int degreeDigits = axisLon ? 3 : 2;
                if (integer.Length < degreeDigits + 2)
                    throw new FormatException($"压缩度分秒位数不足: {value}");

                double degrees = double.Parse(integer[..degreeDigits], CultureInfo.InvariantCulture);
                double minutes = double.Parse(integer.Substring(degreeDigits, 2), CultureInfo.InvariantCulture);
                string secondInteger = integer[(degreeDigits + 2)..];
                if (secondInteger.Length == 0) secondInteger = "0";
                double seconds = double.Parse(
                    secondInteger + (fraction.Length > 0 ? "." + fraction : ""),
                    CultureInfo.InvariantCulture);

                ValidateMinutesSeconds(minutes, seconds, value);
                double result = degrees + minutes / 60d + seconds / 3600d;
                if (signText == "-") result = -result;
                result = ApplyHemisphere(result, hemisphere, value);
                ValidateAxisRange(result, axisLon, axisLat, value);
                return result;
            }
        }

        string text = Regex.Replace(upper, "[NSEW]", " ");
        foreach (char c in NormalizeSeparators(separators))
            text = text.Replace(c, ' ');

        foreach (char c in "度分秒°′″'\":")
            text = text.Replace(c, ' ');

        text = Regex.Replace(text, @"(?<=\d)-(?=\d)", " ");
        var matches = NumberRegex.Matches(text);
        if (matches.Count == 0)
            throw new FormatException($"无法解析坐标: {value}");
        if (matches.Count > 3)
            throw new FormatException($"坐标分量超过三段: {value}");

        var parts = matches.Select(m => double.Parse(m.Value, CultureInfo.InvariantCulture)).ToArray();
        if (hemisphere.HasValue && parts[0] < 0)
            throw new FormatException($"负号与半球标记不能同时使用，避免符号歧义: {value}");

        double sign = parts[0] < 0 ? -1d : 1d;
        double degrees2 = Math.Abs(parts[0]);
        double minutes2 = parts.Length > 1 ? parts[1] : 0d;
        double seconds2 = parts.Length > 2 ? parts[2] : 0d;
        ValidateMinutesSeconds(minutes2, seconds2, value);

        double result2 = sign * (degrees2 + minutes2 / 60d + seconds2 / 3600d);
        result2 = ApplyHemisphere(result2, hemisphere, value);
        ValidateAxisRange(result2, axisLon, axisLat, value);
        return result2;
    }

    public static (string Longitude, string Latitude)? SplitDmsPairFields(string? value, string? separators = null)
    {
        string raw = (value ?? "").Trim();
        if (raw.Length == 0) return null;

        string separatorText = NormalizeSeparators(separators);
        var separatorChars = separatorText.Select(char.ToUpperInvariant).ToHashSet();
        var unitChars = "°′″'\":度分秒".ToHashSet();
        var delimiters = new List<char> { '\t', ',', ';', '，', '；', '|', '/' };

        foreach (char c in separatorText)
        {
            if (!char.IsWhiteSpace(c) && !char.IsLetterOrDigit(c) && !unitChars.Contains(c) && !delimiters.Contains(c))
                delimiters.Add(c);
        }

        (string, string)? Assemble(IEnumerable<string> pieces)
        {
            string[] cells = pieces.Select(x => x.Trim()).ToArray();
            if (cells.Length != 6 || cells.Any(string.IsNullOrWhiteSpace))
                return null;
            if (cells.Any(c => NumberRegex.Matches(c).Count != 1))
                return null;

            string Group(IEnumerable<string> group)
            {
                string t = string.Join(" ", group);
                var marks = Regex.Matches(t, "[NSEW]", RegexOptions.IgnoreCase)
                    .Select(m => char.ToUpperInvariant(m.Value[0]))
                    .Where(m => !separatorChars.Contains(m))
                    .Distinct()
                    .ToArray();

                if (marks.Length == 1)
                {
                    char mark = marks[0];
                    t = Regex.Replace(t, mark.ToString(), "", RegexOptions.IgnoreCase).Trim();
                    return $"{mark}{t}";
                }
                return t;
            }

            return (Group(cells[..3]), Group(cells[3..]));
        }

        foreach (char delimiter in delimiters)
        {
            if (raw.Contains(delimiter))
            {
                var assembled = Assemble(raw.Split(delimiter));
                if (assembled.HasValue) return assembled;
            }
        }

        return Assemble(Regex.Split(raw, @"\s+"));
    }

    public static string FormatDms(double value, string axis, int secondsPrecision = 3)
    {
        bool lon = IsLongitudeAxis(axis);
        if (lon && (value < -180 || value > 180))
            throw new FormatException($"经度超出范围: {value}");
        if (!lon && (value < -90 || value > 90))
            throw new FormatException($"纬度超出范围: {value}");

        double absolute = Math.Abs(value);
        int degrees = (int)absolute;
        double minutesFull = (absolute - degrees) * 60d;
        int minutes = (int)minutesFull;
        double seconds = (minutesFull - minutes) * 60d;

        if (Math.Round(seconds, secondsPrecision) >= 60d)
        {
            seconds = 0d;
            minutes++;
        }
        if (minutes >= 60)
        {
            minutes = 0;
            degrees++;
        }

        string sign = value < 0 ? "-" : "";
        string secondsText = seconds.ToString(
            $"00.{new string('0', secondsPrecision)}",
            CultureInfo.InvariantCulture);
        return $"{sign}{degrees}°{minutes:00}′{secondsText}″";
    }

    public static double SuggestGk3CentralMeridian(double longitude)
    {
        if (longitude < -180 || longitude > 180)
            throw new FormatException($"经度超出范围: {longitude}");
        return Math.Floor((longitude + 1.5d) / 3d) * 3d;
    }

    public static (double Easting, double Northing) GaussKrugerForward(
        double longitude,
        double latitude,
        double centralMeridian,
        string datum = "CGCS2000",
        double falseEasting = 500000d,
        double falseNorthing = 0d)
    {
        ValidateLonLat(longitude, latitude);
        var (a, eccSq) = Ellipsoid(datum);
        double eccPrimeSq = eccSq / (1d - eccSq);
        double phi = DegToRad(latitude);
        double lambda = DegToRad(longitude);
        double central = DegToRad(centralMeridian);
        double sin = Math.Sin(phi);
        double cos = Math.Cos(phi);
        double tan = Math.Tan(phi);

        double n = a / Math.Sqrt(1d - eccSq * sin * sin);
        double t = tan * tan;
        double c = eccPrimeSq * cos * cos;
        double aa = cos * (lambda - central);
        double m = a * (
            (1 - eccSq / 4 - 3 * Math.Pow(eccSq, 2) / 64 - 5 * Math.Pow(eccSq, 3) / 256) * phi
            - (3 * eccSq / 8 + 3 * Math.Pow(eccSq, 2) / 32 + 45 * Math.Pow(eccSq, 3) / 1024) * Math.Sin(2 * phi)
            + (15 * Math.Pow(eccSq, 2) / 256 + 45 * Math.Pow(eccSq, 3) / 1024) * Math.Sin(4 * phi)
            - (35 * Math.Pow(eccSq, 3) / 3072) * Math.Sin(6 * phi));

        double easting = falseEasting + n * (
            aa
            + (1 - t + c) * Math.Pow(aa, 3) / 6
            + (5 - 18 * t + t * t + 72 * c - 58 * eccPrimeSq) * Math.Pow(aa, 5) / 120);

        double northing = falseNorthing + (
            m + n * tan * (
                aa * aa / 2
                + (5 - t + 9 * c + 4 * c * c) * Math.Pow(aa, 4) / 24
                + (61 - 58 * t + t * t + 600 * c - 330 * eccPrimeSq) * Math.Pow(aa, 6) / 720));

        return (easting, northing);
    }

    public static (double Longitude, double Latitude) GaussKrugerInverse(
        double easting,
        double northing,
        double centralMeridian,
        string datum = "CGCS2000",
        double falseEasting = 500000d,
        double falseNorthing = 0d)
    {
        var (a, eccSq) = Ellipsoid(datum);
        double eccPrimeSq = eccSq / (1d - eccSq);
        double m = northing - falseNorthing;
        double mu = m / (a * (1 - eccSq / 4 - 3 * Math.Pow(eccSq, 2) / 64 - 5 * Math.Pow(eccSq, 3) / 256));
        double e1 = (1 - Math.Sqrt(1 - eccSq)) / (1 + Math.Sqrt(1 - eccSq));

        double phi1 =
            mu
            + (3 * e1 / 2 - 27 * Math.Pow(e1, 3) / 32) * Math.Sin(2 * mu)
            + (21 * e1 * e1 / 16 - 55 * Math.Pow(e1, 4) / 32) * Math.Sin(4 * mu)
            + (151 * Math.Pow(e1, 3) / 96) * Math.Sin(6 * mu)
            + (1097 * Math.Pow(e1, 4) / 512) * Math.Sin(8 * mu);

        double sinPhi = Math.Sin(phi1);
        double cosPhi = Math.Cos(phi1);
        double tanPhi = Math.Tan(phi1);
        double n1 = a / Math.Sqrt(1 - eccSq * sinPhi * sinPhi);
        double r1 = a * (1 - eccSq) / Math.Pow(1 - eccSq * sinPhi * sinPhi, 1.5);
        double t1 = tanPhi * tanPhi;
        double c1 = eccPrimeSq * cosPhi * cosPhi;
        double d = (easting - falseEasting) / n1;

        double lat = phi1 - (n1 * tanPhi / r1) * (
            d * d / 2
            - (5 + 3 * t1 + 10 * c1 - 4 * c1 * c1 - 9 * eccPrimeSq) * Math.Pow(d, 4) / 24
            + (61 + 90 * t1 + 298 * c1 + 45 * t1 * t1 - 252 * eccPrimeSq - 3 * c1 * c1) * Math.Pow(d, 6) / 720);

        double lon = DegToRad(centralMeridian) + (
            d
            - (1 + 2 * t1 + c1) * Math.Pow(d, 3) / 6
            + (5 - 2 * c1 + 28 * t1 - 3 * c1 * c1 + 8 * eccPrimeSq + 24 * t1 * t1) * Math.Pow(d, 5) / 120
        ) / cosPhi;

        double lonDeg = RadToDeg(lon);
        double latDeg = RadToDeg(lat);
        ValidateLonLat(lonDeg, latDeg);
        return (lonDeg, latDeg);
    }

    public static CoordinateConversionResult Convert(
        string first,
        string second,
        CoordinateInputMode mode,
        ProjectionParameters parameters,
        string? dmsSeparators = null,
        int decimalPlaces = 8)
    {
        ValidateDecimalPlaces(decimalPlaces);
        parameters = CoordinateProjectionService.Normalize(parameters);
        double lon;
        double lat;

        if (mode == CoordinateInputMode.Dms)
        {
            string? firstHint = HemisphereAxisHint(first, dmsSeparators);
            string? secondHint = HemisphereAxisHint(second, dmsSeparators);
            if (firstHint == "lat" || secondHint == "lon")
            {
                lon = ParseDms(second, "lon", dmsSeparators);
                lat = ParseDms(first, "lat", dmsSeparators);
            }
            else
            {
                lon = ParseDms(first, "lon", dmsSeparators);
                lat = ParseDms(second, "lat", dmsSeparators);
            }
        }
        else if (mode == CoordinateInputMode.DecimalDegrees)
        {
            string? firstHint = HemisphereAxisHint(first, null);
            string? secondHint = HemisphereAxisHint(second, null);
            if (firstHint == "lat" || secondHint == "lon")
            {
                lon = ParseDms(second, "lon");
                lat = ParseDms(first, "lat");
            }
            else
            {
                lon = ParseDms(first, "lon");
                lat = ParseDms(second, "lat");
            }
        }
        else
        {
            if (!parameters.CentralMeridian.HasValue)
                throw new InvalidOperationException("XY 坐标必须先确认中央子午线");

            double x = CleanNumber(first);
            double y = CleanNumber(second);
            double easting;
            double northing;
            if (parameters.AxisOrder == XyAxisOrder.NorthingEasting)
            {
                northing = x;
                easting = y;
            }
            else
            {
                easting = x;
                northing = y;
            }

            (lon, lat) = CoordinateProjectionService.Inverse(easting, northing, parameters);
        }

        double cm = parameters.CentralMeridian ?? SuggestGk3CentralMeridian(lon);
        var projected = CoordinateProjectionService.Forward(lon, lat, parameters with { CentralMeridian = cm });

        double xOut = parameters.AxisOrder == XyAxisOrder.NorthingEasting ? projected.Northing : projected.Easting;
        double yOut = parameters.AxisOrder == XyAxisOrder.NorthingEasting ? projected.Easting : projected.Northing;
        string axisLabel = parameters.AxisOrder == XyAxisOrder.NorthingEasting
            ? "X=Northing, Y=Easting"
            : "X=Easting, Y=Northing";

        return new CoordinateConversionResult(
            lon, lat,
            lon.ToString($"F{decimalPlaces}", CultureInfo.InvariantCulture),
            lat.ToString($"F{decimalPlaces}", CultureInfo.InvariantCulture),
            FormatDms(lon, "lon"),
            FormatDms(lat, "lat"),
            xOut, yOut,
            projected.Easting, projected.Northing,
            axisLabel, cm, parameters.Datum, ProjectionKind: parameters.Kind);
    }

    public static BatchConversionResult ConvertBatch(
        string rawInput,
        CoordinateInputMode mode,
        ProjectionParameters parameters,
        bool autoCentralMeridian,
        string? dmsSeparators = null,
        int decimalPlaces = 8)
    {
        ValidateDecimalPlaces(decimalPlaces);
        mode = CoordinateBatchParser.ResolveInputMode(
            rawInput,
            mode,
            dmsSeparators);
        var pairs = CoordinateBatchParser.ParseRows(rawInput, mode, dmsSeparators);
        if (pairs.Count == 0)
            throw new FormatException("未识别到坐标");

        if (parameters.Kind != CoordinateProjectionKind.GaussKruger)
        {
            parameters = CoordinateProjectionService.Normalize(parameters);
            var projectedResults = pairs.Select(p => Convert(p.First, p.Second, mode, parameters, dmsSeparators, decimalPlaces)
                with { PointId = p.PointId }).ToList();
            string message = parameters.Kind == CoordinateProjectionKind.KertauRsoMalaya
                ? CoordinateProjectionService.RsoWarning : "UTM采用整批固定带号及半球；经纬度为WGS84，不添加高斯百万位带号。";
            if (parameters.Kind == CoordinateProjectionKind.Utm && projectedResults.Any(r => Math.Abs(r.Longitude - parameters.CentralMeridian!.Value) > 3))
                message += " 存在正常六度带外的点；仅在明确采用扩展带时使用，请核对。";
            return new(projectedResults, parameters.CentralMeridian!.Value, [parameters.CentralMeridian.Value], message);
        }

        if (mode == CoordinateInputMode.ProjectedXY)
        {
            if (!parameters.CentralMeridian.HasValue)
                throw new InvalidOperationException("XY 输入必须先确认中央子午线");
            var xyResults = pairs.Select(p => Convert(
                p.First, p.Second, mode, parameters, dmsSeparators, decimalPlaces)
                with { PointId = p.PointId }).ToList();
            return new BatchConversionResult(xyResults, parameters.CentralMeridian.Value, [parameters.CentralMeridian.Value], "");
        }

        double cm;
        IReadOnlyList<double> pointMeridians;
        string warning = "";

        if (autoCentralMeridian)
        {
            var previewParams = parameters with { CentralMeridian = null };
            var previews = pairs.Select(p => Convert(
                p.First, p.Second, mode, previewParams, dmsSeparators, decimalPlaces)
                with { PointId = p.PointId }).ToList();
            (cm, pointMeridians) = SuggestBatchGk3CentralMeridian(previews);
            if (pointMeridians.Count > 1)
            {
                warning = $"注意：整批坐标跨越多个三度带建议中央子午线 {string.Join("、", pointMeridians.Select(x => $"{x:g}°"))}；当前按整批经度中位数统一采用 {cm:g}°，请人工确认。";
            }
        }
        else
        {
            if (!parameters.CentralMeridian.HasValue)
                throw new InvalidOperationException("已关闭自动建议，请填写中央子午线");
            cm = parameters.CentralMeridian.Value;
            pointMeridians = [cm];
        }

        var finalParams = parameters with { CentralMeridian = cm };
        var results = pairs.Select(p => Convert(
            p.First, p.Second, mode, finalParams, dmsSeparators, decimalPlaces)
            with { PointId = p.PointId }).ToList();
        return new BatchConversionResult(results, cm, pointMeridians, warning);
    }

    // Compare geographic evidence even when conversion uses a confirmed project CM.
    // Projected XY cannot independently validate the CM used to invert those coordinates.
    public static (double CentralMeridian, IReadOnlyList<double> PointMeridians)
        SuggestBatchGk3CentralMeridian(IReadOnlyList<CoordinateConversionResult> results)
    {
        if (results.Count == 0)
            throw new ArgumentException("没有可用于中央经线建议的坐标。", nameof(results));
        var longitudes = results.Select(r => r.Longitude).OrderBy(x => x).ToArray();
        double median = longitudes.Length % 2 == 1
            ? longitudes[longitudes.Length / 2]
            : (longitudes[longitudes.Length / 2 - 1] + longitudes[longitudes.Length / 2]) / 2d;
        return (SuggestGk3CentralMeridian(median),
            longitudes.Select(SuggestGk3CentralMeridian).Distinct().OrderBy(x => x).ToArray());
    }

    public static int GetGk3ZoneNumber(double centralMeridian)
    {
        if (!double.IsFinite(centralMeridian))
            throw new ArgumentOutOfRangeException(nameof(centralMeridian));

        return checked((int)Math.Round(
            centralMeridian / 3.0,
            MidpointRounding.AwayFromZero));
    }

    public static double AddGk3ZoneNumberToEasting(
        double easting,
        double centralMeridian)
    {
        if (!double.IsFinite(easting))
            throw new ArgumentOutOfRangeException(nameof(easting));

        int zone = GetGk3ZoneNumber(centralMeridian);
        return checked(zone * 1_000_000d + easting);
    }

    private static void ValidateDecimalPlaces(int decimalPlaces)
    {
        if (decimalPlaces is < 0 or > 12)
            throw new ArgumentOutOfRangeException(
                nameof(decimalPlaces),
                "小数坐标保留位数必须在 0～12 之间。");
    }

    public static (double Easting, double Northing) UtmForward(double longitude, double latitude, int zone)
        => CoordinateProjectionService.Forward(longitude, latitude,
            new ProjectionParameters(Kind: CoordinateProjectionKind.Utm, UtmZone: zone, SouthernHemisphere: latitude < 0));

    private static string? HemisphereAxisHint(string? value, string? separators)
    {
        string text = (value ?? "").Trim().ToUpperInvariant();
        if (text.Length == 0) return null;
        var separatorChars = NormalizeSeparators(separators).Select(char.ToUpperInvariant).ToHashSet();
        var marks = new List<char>();
        if ("NSEW".Contains(text[0]) && !separatorChars.Contains(text[0])) marks.Add(text[0]);
        if ("NSEW".Contains(text[^1]) && !separatorChars.Contains(text[^1])) marks.Add(text[^1]);
        bool ns = marks.Any(x => x is 'N' or 'S');
        bool ew = marks.Any(x => x is 'E' or 'W');
        if (ns && !ew) return "lat";
        if (ew && !ns) return "lon";
        return null;
    }

    private static string NormalizeSeparators(string? separators) =>
        (separators ?? "").Replace("\\t", "\t", StringComparison.Ordinal);

    private static bool IsLongitudeAxis(string? axis) =>
        new[] { "lon", "longitude", "经度", "x" }.Contains((axis ?? "").Trim(), StringComparer.OrdinalIgnoreCase);

    private static bool IsLatitudeAxis(string? axis) =>
        new[] { "lat", "latitude", "纬度", "y" }.Contains((axis ?? "").Trim(), StringComparer.OrdinalIgnoreCase);

    private static void ValidateMinutesSeconds(double minutes, double seconds, string value)
    {
        if (minutes < 0 || minutes >= 60 || seconds < 0 || seconds >= 60)
            throw new FormatException($"分或秒超出范围: {value}");
    }

    private static double ApplyHemisphere(double result, char? hemisphere, string value)
    {
        if (hemisphere is 'S' or 'W') return -Math.Abs(result);
        if (hemisphere is 'N' or 'E') return Math.Abs(result);
        return result;
    }

    private static void ValidateAxisRange(double result, bool axisLon, bool axisLat, string value)
    {
        if (axisLon && (result < -180 || result > 180))
            throw new FormatException($"经度超出范围: {value}");
        if (axisLat && (result < -90 || result > 90))
            throw new FormatException($"纬度超出范围: {value}");
    }

    private static void ValidateLonLat(double longitude, double latitude)
    {
        if (!double.IsFinite(longitude) || longitude < -180 || longitude > 180)
            throw new FormatException($"经度超出范围: {longitude}");
        if (!double.IsFinite(latitude) || latitude < -90 || latitude > 90)
            throw new FormatException($"纬度超出范围: {latitude}");
    }

    private static double CleanNumber(string value)
    {
        if (!double.TryParse(value.Trim().Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out double number)
            || !double.IsFinite(number))
            throw new FormatException($"不是有效数字: {value}");
        return number;
    }

    private static (double A, double EccSq) Ellipsoid(string datum)
    {
        string name = (datum ?? "CGCS2000").Trim().ToUpperInvariant().Replace("-", "");
        double inverseFlattening = name switch
        {
            "CGCS2000" or "CGCS 2000" or "EPSG:4490" => 298.257222101d,
            "WGS84" or "WGS 84" or "EPSG:4326" => 298.257223563d,
            _ => throw new ArgumentException("当前坐标工作台仅支持 CGCS2000 或 WGS84 椭球")
        };
        const double a = 6378137d;
        double f = 1d / inverseFlattening;
        return (a, f * (2d - f));
    }

    private static double DegToRad(double value) => value * Math.PI / 180d;
    private static double RadToDeg(double value) => value * 180d / Math.PI;
}

public static class CoordinateBatchParser
{
    private static readonly HashSet<string> PointHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "点号", "点名", "点编号", "点位", "序号", "编号",
        "point", "pointid", "pointno", "pointnumber", "id", "name"
    };

    private static readonly HashSet<string> LongitudeHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "经度", "longitude", "lon", "lng", "经度x"
    };

    private static readonly HashSet<string> LatitudeHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "纬度", "latitude", "lat", "纬度y"
    };

    public static CoordinateInputMode ResolveInputMode(
        string rawInput,
        CoordinateInputMode requestedMode,
        string? dmsSeparators = null)
    {
        if (requestedMode == CoordinateInputMode.Dms)
            return requestedMode;

        string text = (rawInput ?? "").Trim();
        if (text.Length == 0)
            return requestedMode;

        // Explicit DMS notation is unambiguous enough to override a stale
        // UI mode restored from a previous decimal-coordinate project result.
        // Do not use custom separators here because characters such as '-'
        // can also be valid decimal/XY signs.
        if (Regex.IsMatch(
                text,
                @"[°º′’'″”""NSEW]",
                RegexOptions.IgnoreCase))
        {
            return CoordinateInputMode.Dms;
        }

        return requestedMode;
    }

    public static IReadOnlyList<CoordinateInputPair> ParseRows(
        string rawInput,
        CoordinateInputMode mode,
        string? dmsSeparators = null)
    {
        string text = (rawInput ?? "").Trim();
        if (text.Length == 0) return [];

        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => x.Length > 0)
            .ToArray();

        if (lines.Length == 0) return [];

        if (mode == CoordinateInputMode.Dms)
        {
            var structured = TryParseDmsTable(lines, dmsSeparators);
            if (structured is { Count: > 0 })
                return structured;
        }

        if (lines.Length == 1)
        {
            var pair = SplitInlinePair(lines[0], mode, dmsSeparators);
            if (pair is null)
                throw new FormatException(
                    "尚未识别为一对坐标；请使用 Tab、逗号、分号分隔两个完整坐标，" +
                    "直接粘贴“点号/经度/纬度”表格，或粘贴六列“经度度/分/秒 + 纬度度/分/秒”。");
            return [pair];
        }

        var rows = new List<CoordinateInputPair>();
        var failed = new List<(int line, string value)>();
        for (int i = 0; i < lines.Length; i++)
        {
            var pair = SplitInlinePair(lines[i], mode, dmsSeparators);
            if (pair is null) failed.Add((i + 1, lines[i]));
            else rows.Add(pair);
        }

        if (failed.Count == 0) return rows;
        if (lines.Length == 2 && failed.Count == 2)
            return [new CoordinateInputPair(lines[0], lines[1])];

        var first = failed[0];
        throw new FormatException(
            $"第 {first.line} 行无法拆分成一对坐标：{first.value}。" +
            "多行输入时请确保每一行包含两个完整坐标、点号/经度/纬度三列，或六列 D/M/S。");
    }

    private static IReadOnlyList<CoordinateInputPair>? TryParseDmsTable(
        string[] lines,
        string? dmsSeparators)
    {
        var vertical = TryParseVerticalDmsTable(lines, dmsSeparators);
        if (vertical is { Count: > 0 })
            return vertical;

        foreach (char delimiter in new[] { '\t', ',', ';', '，', '；', '|' })
        {
            var parsed = TryParseDelimitedDmsTable(lines, delimiter, dmsSeparators);
            if (parsed is { Count: > 0 })
                return parsed;
        }

        return null;
    }

    private static IReadOnlyList<CoordinateInputPair>? TryParseVerticalDmsTable(
        string[] lines,
        string? dmsSeparators)
    {
        if (lines.Length < 3 || lines.Length % 3 != 0)
            return null;

        string[] firstTriple = lines[..3];
        int pointIndex = FindHeaderIndex(firstTriple, PointHeaders);
        int lonIndex = FindHeaderIndex(firstTriple, LongitudeHeaders);
        int latIndex = FindHeaderIndex(firstTriple, LatitudeHeaders);

        bool hasHeader =
            pointIndex >= 0 &&
            lonIndex >= 0 &&
            latIndex >= 0 &&
            new[] { pointIndex, lonIndex, latIndex }.Distinct().Count() == 3;

        if (hasHeader)
        {
            if (lines.Length < 6)
                return null;

            var rows = new List<CoordinateInputPair>();
            for (int offset = 3; offset < lines.Length; offset += 3)
            {
                string[] cells = lines[offset..(offset + 3)];
                rows.Add(CreateValidatedDmsPair(
                    cells[lonIndex],
                    cells[latIndex],
                    cells[pointIndex],
                    dmsSeparators,
                    rows.Count + 1));
            }

            return rows;
        }

        // Headerless vertical paste:
        // 1
        // 111° 14' 1.93"
        // 47° 45' 41.76"
        //
        // Require recognizable DMS notation in the longitude/latitude lines.
        // This avoids treating a vertically pasted six-column numeric D/M/S
        // sequence as point-id / longitude / latitude triples.
        var headerlessRows = new List<CoordinateInputPair>();
        for (int offset = 0; offset < lines.Length; offset += 3)
        {
            string pointId = lines[offset];
            string longitude = lines[offset + 1];
            string latitude = lines[offset + 2];

            if (!LooksLikeExplicitDms(longitude, dmsSeparators) ||
                !LooksLikeExplicitDms(latitude, dmsSeparators))
            {
                return null;
            }

            headerlessRows.Add(CreateValidatedDmsPair(
                longitude,
                latitude,
                pointId,
                dmsSeparators,
                headerlessRows.Count + 1));
        }

        return headerlessRows;
    }

    private static bool LooksLikeExplicitDms(
        string value,
        string? dmsSeparators)
    {
        string text = (value ?? "").Trim();
        if (text.Length == 0)
            return false;

        if (Regex.IsMatch(
                text,
                @"[°º′’'″”""NSEW]",
                RegexOptions.IgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(dmsSeparators))
        {
            foreach (char separator in dmsSeparators)
            {
                if (!char.IsWhiteSpace(separator) &&
                    text.Contains(separator))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static IReadOnlyList<CoordinateInputPair>? TryParseDelimitedDmsTable(
        string[] lines,
        char delimiter,
        string? dmsSeparators)
    {
        if (!lines.Any(x => x.Contains(delimiter)))
            return null;

        string[][] table = lines
            .Select(x => x.Split(delimiter).Select(c => c.Trim()).ToArray())
            .ToArray();

        if (table.Length == 0)
            return null;

        int lonIndex = FindHeaderIndex(table[0], LongitudeHeaders);
        int latIndex = FindHeaderIndex(table[0], LatitudeHeaders);
        int pointIndex = FindHeaderIndex(table[0], PointHeaders);

        if (lonIndex >= 0 && latIndex >= 0)
        {
            var rows = new List<CoordinateInputPair>();
            int requiredIndex = Math.Max(lonIndex, latIndex);

            for (int i = 1; i < table.Length; i++)
            {
                string[] cells = table[i];
                if (cells.All(string.IsNullOrWhiteSpace))
                    continue;
                if (cells.Length <= requiredIndex)
                    throw new FormatException(
                        $"坐标表第 {i + 1} 行列数不足；需要读取经度和纬度列。");

                string? pointId = pointIndex >= 0 && pointIndex < cells.Length
                    ? cells[pointIndex]
                    : null;

                rows.Add(CreateValidatedDmsPair(
                    cells[lonIndex],
                    cells[latIndex],
                    pointId,
                    dmsSeparators,
                    i));
            }

            if (rows.Count == 0)
                throw new FormatException("已识别坐标表表头，但没有有效的数据行。");
            return rows;
        }

        if (table.All(x => x.Length == 3))
        {
            var rows = new List<CoordinateInputPair>();
            for (int i = 0; i < table.Length; i++)
            {
                try
                {
                    rows.Add(CreateValidatedDmsPair(
                        table[i][1],
                        table[i][2],
                        table[i][0],
                        dmsSeparators,
                        i + 1));
                }
                catch (FormatException)
                {
                    return null;
                }
            }

            return rows.Count > 0 ? rows : null;
        }

        return null;
    }

    private static CoordinateInputPair CreateValidatedDmsPair(
        string longitude,
        string latitude,
        string? pointId,
        string? dmsSeparators,
        int rowNumber)
    {
        string lon = longitude.Trim();
        string lat = latitude.Trim();
        if (lon.Length == 0 || lat.Length == 0)
            throw new FormatException($"坐标表第 {rowNumber} 行经纬度为空。");

        try
        {
            CoordinateService.ParseDms(lon, "lon", dmsSeparators);
            CoordinateService.ParseDms(lat, "lat", dmsSeparators);
        }
        catch (FormatException ex)
        {
            throw new FormatException(
                $"坐标表第 {rowNumber} 行无法解析：{ex.Message}",
                ex);
        }

        string? normalizedPointId = string.IsNullOrWhiteSpace(pointId)
            ? null
            : pointId.Trim();

        return new CoordinateInputPair(lon, lat, normalizedPointId);
    }

    private static int FindHeaderIndex(
        IReadOnlyList<string> cells,
        HashSet<string> aliases)
    {
        for (int i = 0; i < cells.Count; i++)
        {
            if (aliases.Contains(NormalizeHeader(cells[i])))
                return i;
        }

        return -1;
    }

    private static string NormalizeHeader(string value)
    {
        string text = (value ?? "")
            .Trim()
            .Trim('\uFEFF')
            .ToLowerInvariant();

        return Regex.Replace(
            text,
            @"[\s_\-./\\（）()\[\]【】]+",
            "");
    }

    private static CoordinateInputPair? SplitInlinePair(
        string text,
        CoordinateInputMode mode,
        string? dmsSeparators)
    {
        string value = text.Trim();
        if (value.Length == 0) return null;

        if (mode == CoordinateInputMode.Dms)
        {
            var six = CoordinateService.SplitDmsPairFields(value, dmsSeparators);
            if (six.HasValue)
                return new CoordinateInputPair(six.Value.Longitude, six.Value.Latitude);
        }

        foreach (char separator in new[] { '\t', ',', ';', '，', '；' })
        {
            int pos = value.IndexOf(separator);
            if (pos > 0 && pos < value.Length - 1)
            {
                string left = value[..pos].Trim();
                string right = value[(pos + 1)..].Trim();
                if (left.Length > 0 && right.Length > 0)
                    return new CoordinateInputPair(left, right);
            }
        }

        if (mode == CoordinateInputMode.Dms)
        {
            foreach (string pattern in new[]
            {
                @"^\s*(.+?[NSEW])\s+([NSEW]?.+?)\s*$",
                @"^\s*([NSEW].+?)\s+([NSEW].+?)\s*$"
            })
            {
                var m = Regex.Match(value, pattern, RegexOptions.IgnoreCase);
                if (m.Success)
                    return new CoordinateInputPair(m.Groups[1].Value.Trim(), m.Groups[2].Value.Trim());
            }
        }

        if (mode is CoordinateInputMode.DecimalDegrees or CoordinateInputMode.ProjectedXY)
        {
            string[] parts = Regex.Split(value, @"\s+");
            if (parts.Length == 2)
                return new CoordinateInputPair(parts[0], parts[1]);
        }

        return null;
    }
}
