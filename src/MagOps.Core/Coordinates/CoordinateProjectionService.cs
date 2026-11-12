namespace MagOps.Core.Coordinates;

/// <summary>New modes use WGS84 geographic coordinates; EPSG:3168 includes EPSG:8659 datum shift.
/// Hotine variant A follows EPSG method 9812. No dependency on a locally installed GIS.</summary>
public static class CoordinateProjectionService
{
    private const double Rad = Math.PI / 180;
    public const string RsoWarning = "RSO使用EPSG:3168及EPSG:8659（-11,+851,+5 m）转换到WGS84，标称精度15 m；不是高精度控制转换，不转换高程。";
    public static ProjectionParameters Normalize(ProjectionParameters p) => p.Kind switch
    {
        CoordinateProjectionKind.GaussKruger => p,
        CoordinateProjectionKind.Utm when p.UtmZone is >= 1 and <= 60 => p with
        { Datum = "WGS84", CentralMeridian = p.UtmZone * 6 - 183, FalseEasting = 500000, FalseNorthing = p.SouthernHemisphere ? 10000000 : 0 },
        CoordinateProjectionKind.Utm => throw new ArgumentException("UTM带号必须为1～60。"),
        CoordinateProjectionKind.KertauRsoMalaya => p with
        { Datum = "Kertau (RSO)", CentralMeridian = 102.25, FalseEasting = 804670.24, FalseNorthing = 0 },
        _ => throw new ArgumentException("未知投影方式。")
    };
    public static string Label(ProjectionParameters p) => p.Kind switch
    {
        CoordinateProjectionKind.Utm => $"WGS84 / UTM {p.UtmZone}{(p.SouthernHemisphere ? "S" : "N")} (EPSG:{(p.SouthernHemisphere ? 32700 : 32600) + p.UtmZone})",
        CoordinateProjectionKind.KertauRsoMalaya => "Kertau (RSO) / RSO Malaya (m) (EPSG:3168)",
        _ => "高斯-克吕格三度带（横轴墨卡托）"
    };
    public static IReadOnlyList<(string Name, string Value)> DefinitionParameters(ProjectionParameters p) => p.Kind switch
    {
        CoordinateProjectionKind.Utm => new[] { ("EPSG", ((p.SouthernHemisphere ? 32700 : 32600) + p.UtmZone).ToString(System.Globalization.CultureInfo.InvariantCulture)), ("比例因子", "0.9996"), ("纬度原点_deg", "0") },
        CoordinateProjectionKind.KertauRsoMalaya => new[] { ("EPSG", "3168"), ("投影方法", "Hotine Oblique Mercator variant A / EPSG:9812"),
            ("椭球长半轴_m", "6377295.664"), ("椭球扁率倒数", "300.8017"), ("纬度原点_deg", "4"),
            ("方位角_deg", "323.0257905"), ("整饰角_deg", "323.130102361111"), ("比例因子", "0.99984"),
            ("基准转换操作", "EPSG:8659 (15896 + 1158)"), ("地心平移XYZ_m", "-11,+851,+5"), ("基准转换标称精度_m", "15") },
        _ => new[] { ("比例因子", "1"), ("纬度原点_deg", "0") }
    };
    public static (double Easting, double Northing) Forward(double lon, double lat, ProjectionParameters source)
    {
        var p = Normalize(source);
        if (!double.IsFinite(lon) || !double.IsFinite(lat) || lon is < -180 or > 180 || lat is < -90 or > 90)
            throw new ArgumentException("经纬度必须为有限值且在有效范围内。");
        if (p.Kind == CoordinateProjectionKind.KertauRsoMalaya)
        {
            CheckRsoArea(lon, lat);
            var local = ShiftDatum(lon, lat, inverse: true);
            return RsoForward(local.Longitude, local.Latitude);
        }
        double cm = p.CentralMeridian ?? CoordinateService.SuggestGk3CentralMeridian(lon);
        if (p.Kind == CoordinateProjectionKind.GaussKruger)
            return CoordinateService.GaussKrugerForward(lon, lat, cm, p.Datum, p.FalseEasting, p.FalseNorthing);
        double delta = Wrap(lon - cm);
        if (lat < -80 - 1e-8 || lat > 84 + 1e-8 || (lat < -1e-8 && !p.SouthernHemisphere) || (lat > 1e-8 && p.SouthernHemisphere))
            throw new ArgumentException("UTM纬度须在80°S～84°N，且与所选南北半球一致。");
        if (Math.Abs(delta) > 6.000001) throw new ArgumentException("UTM带号与输入位置不符（距中央经线超过6°），请核对带号。");
        // GK and UTM share the transverse Mercator series; apply UTM scale to both axes.
        return UtmSeries(cm + delta, lat, p);
    }
    public static (double Longitude, double Latitude) Inverse(double e, double n, ProjectionParameters source)
    {
        var p = Normalize(source);
        if (!double.IsFinite(e) || !double.IsFinite(n)) throw new ArgumentException("投影坐标必须为有限数值。");
        if (p.Kind == CoordinateProjectionKind.KertauRsoMalaya)
        {
            var local = RsoInverse(e, n);
            var wgs = ShiftDatum(local.Longitude, local.Latitude, inverse: false);
            CheckRsoArea(wgs.Longitude, wgs.Latitude);
            return wgs;
        }
        if (!p.CentralMeridian.HasValue) throw new ArgumentException("必须指定中央经线。");
        if (p.Kind == CoordinateProjectionKind.GaussKruger)
            return CoordinateService.GaussKrugerInverse(e, n, p.CentralMeridian.Value, p.Datum, p.FalseEasting, p.FalseNorthing);
        if (e is < 0 or > 1000000 || n is < 0 or > 10000000) throw new ArgumentException("UTM东距或北距超出有效范围，请核对轴序、带号及半球。");
        var initial = CoordinateService.GaussKrugerInverse((e - 500000) / .9996, (n - p.FalseNorthing) / .9996, 0, "WGS84", 0, 0);
        (double Longitude, double Latitude) ll = (Wrap(initial.Longitude + p.CentralMeridian.Value), initial.Latitude);
        // Remove the inverse series truncation residual using the same forward function.
        for (int i = 0; i < 3; i++)
        {
            double lon = Wrap(ll.Longitude);
            var xy = UtmSeries(lon, ll.Latitude, p);
            var dx = UtmSeries(Wrap(lon + 1e-6), ll.Latitude, p);
            var dy = UtmSeries(lon, ll.Latitude + (ll.Latitude >= 84 ? -1e-6 : 1e-6), p);
            double h = ll.Latitude >= 84 ? -1e-6 : 1e-6;
            double a = (dx.Easting - xy.Easting) / 1e-6, b = (dy.Easting - xy.Easting) / h;
            double c = (dx.Northing - xy.Northing) / 1e-6, d = (dy.Northing - xy.Northing) / h;
            double det = a * d - b * c;
            ll = (lon + ((e - xy.Easting) * d - (n - xy.Northing) * b) / det,
                ll.Latitude + ((n - xy.Northing) * a - (e - xy.Easting) * c) / det);
        }
        Forward(Wrap(ll.Longitude), ll.Latitude, p);
        return (Wrap(ll.Longitude), ll.Latitude);
    }
    private static (double Easting, double Northing) UtmSeries(double lon, double lat, ProjectionParameters p)
    {
        double cm = p.CentralMeridian!.Value;
        var xy = CoordinateService.GaussKrugerForward(Wrap(lon - cm), lat, 0, "WGS84", 0, 0);
        return (500000 + .9996 * xy.Easting, p.FalseNorthing + .9996 * xy.Northing);
    }
    private static double Wrap(double angle) => angle > 180 ? angle - 360 : angle < -180 ? angle + 360 : angle;
    private static void CheckRsoArea(double lon, double lat)
    {
        if (!double.IsFinite(lon) || !double.IsFinite(lat) || lon is < 99.59 or > 104.6 || lat is < 1.13 or > 6.72)
            throw new ArgumentException("RSO Malaya仅适用于西马来西亚/新加坡区域（99.59～104.6°E，1.13～6.72°N），不是婆罗洲RSO。");
    }
    private static double Ecc(double rf) { double f = 1 / rf; return Math.Sqrt(f * (2 - f)); }
    private static readonly double EccRso = Ecc(300.8017);
    private static double T(double phi) => Math.Tan(Math.PI / 4 - phi / 2) / Math.Pow((1 - EccRso * Math.Sin(phi)) / (1 + EccRso * Math.Sin(phi)), EccRso / 2);
    private static readonly (double B, double A, double H, double Gamma0, double Lambda0) Rso = SetupRso();
    private static (double, double, double, double, double) SetupRso()
    {
        double phi = 4 * Rad, es = EccRso * EccRso;
        double b = Math.Sqrt(1 + es * Math.Pow(Math.Cos(phi), 4) / (1 - es));
        double a = 6377295.664 * b * .99984 * Math.Sqrt(1 - es) / (1 - es * Math.Pow(Math.Sin(phi), 2));
        double d = b * Math.Sqrt(1 - es) / (Math.Cos(phi) * Math.Sqrt(1 - es * Math.Pow(Math.Sin(phi), 2)));
        double f = d + Math.Sqrt(d * d - 1), h = f * Math.Pow(T(phi), b), g = (f - 1 / f) / 2;
        double gamma = Math.Asin(Math.Sin(323.0257905 * Rad) / d);
        return (b, a, h, gamma, 102.25 * Rad - Math.Asin(g * Math.Tan(gamma)) / b);
    }
    /// <summary>EPSG:4751 native geographic to EPSG:3168. No datum shift here.</summary>
    public static (double Easting, double Northing) RsoForward(double lon, double lat)
    {
        CheckRsoArea(lon, lat);
        var (b, a, h, gamma0, lambda0) = Rso;
        double q = h / Math.Pow(T(lat * Rad), b), s = (q - 1 / q) / 2, t = (q + 1 / q) / 2;
        double v0 = Math.Sin(b * (lon * Rad - lambda0));
        double uu = (-v0 * Math.Cos(gamma0) + s * Math.Sin(gamma0)) / t;
        double v = a / (2 * b) * Math.Log((1 - uu) / (1 + uu));
        double u = a / b * Math.Atan2(s * Math.Cos(gamma0) + v0 * Math.Sin(gamma0), Math.Cos(b * (lon * Rad - lambda0)));
        double gamma = 323.130102361111 * Rad;
        return (804670.24 + v * Math.Cos(gamma) + u * Math.Sin(gamma), u * Math.Cos(gamma) - v * Math.Sin(gamma));
    }
    public static (double Longitude, double Latitude) RsoInverse(double e, double n)
    {
        var (b, a, h, gamma0, lambda0) = Rso;
        double gamma = 323.130102361111 * Rad;
        double v = (e - 804670.24) * Math.Cos(gamma) - n * Math.Sin(gamma);
        double u = n * Math.Cos(gamma) + (e - 804670.24) * Math.Sin(gamma);
        double q = Math.Exp(-b * v / a), s = (q - 1 / q) / 2, t = (q + 1 / q) / 2, vv = Math.Sin(b * u / a);
        double uu = (vv * Math.Cos(gamma0) + s * Math.Sin(gamma0)) / t;
        if (!double.IsFinite(uu) || Math.Abs(uu) >= 1) throw new ArgumentException("无效RSO坐标。");
        double ts = Math.Pow(h / Math.Sqrt((1 + uu) / (1 - uu)), 1 / b);
        double phi = Math.PI / 2 - 2 * Math.Atan(ts);
        for (int i = 0; i < 12; i++) phi = Math.PI / 2 - 2 * Math.Atan(ts * Math.Pow((1 - EccRso * Math.Sin(phi)) / (1 + EccRso * Math.Sin(phi)), EccRso / 2));
        double lon = lambda0 - Math.Atan2(s * Math.Cos(gamma0) - vv * Math.Sin(gamma0), Math.Cos(b * u / a)) / b;
        return (lon / Rad, phi / Rad);
    }
    private static (double Longitude, double Latitude) ShiftDatum(double lon, double lat, bool inverse)
    {
        // EPSG:8659 concatenates 15896 (zero shift) and 1158. PROJ reduces this
        // to Everest RSO -> geocentric translation -> WGS84, with zero input height.
        double fromA = inverse ? 6378137 : 6377295.664, toA = inverse ? 6377295.664 : 6378137;
        double fromE = inverse ? Ecc(298.257223563) : EccRso, toE = inverse ? EccRso : Ecc(298.257223563);
        double sin = Math.Sin(lat * Rad), cos = Math.Cos(lat * Rad), r = fromA / Math.Sqrt(1 - fromE * fromE * sin * sin);
        double sign = inverse ? -1 : 1;
        double x = r * cos * Math.Cos(lon * Rad) - sign * 11;
        double y = r * cos * Math.Sin(lon * Rad) + sign * 851;
        double z = r * (1 - fromE * fromE) * sin + sign * 5;
        double distance = Math.Sqrt(x * x + y * y), phi = Math.Atan2(z, distance * (1 - toE * toE));
        for (int i = 0; i < 12; i++)
        {
            r = toA / Math.Sqrt(1 - toE * toE * Math.Pow(Math.Sin(phi), 2));
            phi = Math.Atan2(z + toE * toE * r * Math.Sin(phi), distance);
        }
        return (Math.Atan2(y, x) / Rad, phi / Rad);
    }
}
