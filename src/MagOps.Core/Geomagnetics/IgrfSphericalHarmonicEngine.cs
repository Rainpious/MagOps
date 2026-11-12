namespace MagOps.Core.Geomagnetics;

/// <summary>
/// Independent Schmidt quasi-normalized spherical-harmonic synthesis for IGRF.
/// Coefficients are supplied by IgrfDatParser; no Oasis/Geosoft runtime is required.
/// </summary>
public static class IgrfSphericalHarmonicEngine
{
    // IGRF reference radius, km.
    private const double ReferenceRadiusKm = 6371.2d;

    // WGS84 semi-major and semi-minor axes, km.
    private const double Wgs84A = 6378.137d;
    private const double Wgs84B = 6356.752314245d;
    private const double Wgs84A2 = Wgs84A * Wgs84A;
    private const double Wgs84B2 = Wgs84B * Wgs84B;
    private const double DegreesToRadians = Math.PI / 180d;
    private const double RadiansToDegrees = 180d / Math.PI;

    public static GeomagneticFieldResult Evaluate(
        IgrfModelSet models,
        double latitudeDeg,
        double longitudeDeg,
        double heightMeters,
        DateTimeOffset timestampUtc,
        int? modelEpochOverride = null,
        bool allowBeyondFiveYearWindow = false)
    {
        double decimalYear = IgrfDate.ToDecimalYear(timestampUtc);
        return Evaluate(
            models,
            latitudeDeg,
            longitudeDeg,
            heightMeters,
            decimalYear,
            modelEpochOverride,
            allowBeyondFiveYearWindow);
    }

    public static GeomagneticFieldResult Evaluate(
        IgrfModelSet models,
        double latitudeDeg,
        double longitudeDeg,
        double heightMeters,
        DateOnly dateUtc,
        int? modelEpochOverride = null,
        bool allowBeyondFiveYearWindow = false)
    {
        double decimalYear = IgrfDate.ToDecimalYear(dateUtc);
        return Evaluate(
            models,
            latitudeDeg,
            longitudeDeg,
            heightMeters,
            decimalYear,
            modelEpochOverride,
            allowBeyondFiveYearWindow);
    }

    public static GeomagneticFieldResult Evaluate(
        IgrfModelSet models,
        double latitudeDeg,
        double longitudeDeg,
        double heightMeters,
        double decimalYear,
        int? modelEpochOverride = null,
        bool allowBeyondFiveYearWindow = false)
    {
        ArgumentNullException.ThrowIfNull(models);
        ValidateInput(latitudeDeg, longitudeDeg, heightMeters, decimalYear);

        IgrfEpochModel epoch = modelEpochOverride.HasValue
            ? models.GetEpoch(modelEpochOverride.Value)
            : models.SelectEpoch(decimalYear);

        double deltaYears = decimalYear - epoch.EpochYear;
        if (deltaYears < -1e-12)
        {
            throw new ArgumentOutOfRangeException(
                nameof(decimalYear),
                decimalYear,
                $"Requested date precedes forced IGRF model epoch {epoch.EpochYear}.");
        }

        if (!allowBeyondFiveYearWindow && deltaYears > 5d + 1e-12)
        {
            throw new ArgumentOutOfRangeException(
                nameof(decimalYear),
                decimalYear,
                $"IGRF model epoch {epoch.EpochYear} is being used more than five years forward. " +
                "Pass allowBeyondFiveYearWindow=true only for deliberate extrapolation.");
        }

        int nMax = epoch.MaxDegree;
        var g = new double[nMax + 1, nMax + 1];
        var h = new double[nMax + 1, nMax + 1];
        for (int n = 1; n <= nMax; n++)
        {
            for (int m = 0; m <= n; m++)
            {
                g[n, m] = epoch.G(n, m) + epoch.GDot(n, m) * deltaYears;
                h[n, m] = epoch.H(n, m) + epoch.HDot(n, m) * deltaYears;
            }
        }

        double latitude = latitudeDeg * DegreesToRadians;
        double longitude = NormalizeLongitude(longitudeDeg) * DegreesToRadians;
        double heightKm = heightMeters / 1000d;
        double sinLat = Math.Sin(latitude);
        double cosLat = Math.Cos(latitude);

        // Geodetic -> geocentric reduction and meridional rotation terms.
        double one = Wgs84A2 * cosLat * cosLat;
        double two = Wgs84B2 * sinLat * sinLat;
        double three = one + two;
        double rho = Math.Sqrt(three);
        double rKm = Math.Sqrt(
            heightKm * (heightKm + 2d * rho) +
            (Wgs84A2 * one + Wgs84B2 * two) / three);
        double cd = (heightKm + rho) / rKm;
        double sd = (Wgs84A2 - Wgs84B2) / rho * sinLat * cosLat / rKm;
        double sinGeocentricLat = sinLat * cd - cosLat * sd;
        double cosGeocentricLat = cosLat * cd + sinLat * sd;
        double theta = Math.Atan2(cosGeocentricLat, sinGeocentricLat); // colatitude

        (double[,] p, double[,] dP) = ComputeSchmidtLegendre(nMax, theta);
        double sinTheta = Math.Sin(theta);
        if (Math.Abs(sinTheta) < 1e-12)
        {
            throw new InvalidOperationException(
                "IGRF east component is singular exactly at a geographic pole; " +
                "evaluate a nearby latitude or use a dedicated polar formulation.");
        }

        double ar = ReferenceRadiusKm / rKm;
        double br = 0d;
        double bThetaAccumulator = 0d;
        double bPhiAccumulator = 0d;

        for (int n = 1; n <= nMax; n++)
        {
            double radialFactor = Math.Pow(ar, n + 2);
            for (int m = 0; m <= n; m++)
            {
                double angle = m * longitude;
                double sinMLon = Math.Sin(angle);
                double cosMLon = Math.Cos(angle);
                double gh = g[n, m] * cosMLon + h[n, m] * sinMLon;
                double dGh = -g[n, m] * sinMLon + h[n, m] * cosMLon;

                br += radialFactor * (n + 1d) * gh * p[n, m];
                bThetaAccumulator += radialFactor * gh * dP[n, m];
                bPhiAccumulator += radialFactor * m * dGh * p[n, m];
            }
        }

        // Components in geocentric spherical coordinates.
        double bTheta = -bThetaAccumulator;
        double bPhi = -bPhiAccumulator / sinTheta;

        // Convert to local geocentric N/E/Down, then rotate meridional components
        // back to the geodetic normal.
        double xGeocentric = -bTheta;
        double yEast = bPhi;
        double zGeocentricDown = -br;
        double xNorth = xGeocentric * cd + zGeocentricDown * sd;
        double zDown = zGeocentricDown * cd - xGeocentric * sd;

        double horizontal = Math.Sqrt(xNorth * xNorth + yEast * yEast);
        double total = Math.Sqrt(horizontal * horizontal + zDown * zDown);
        double declination = Math.Atan2(yEast, xNorth) * RadiansToDegrees;
        double inclination = Math.Atan2(zDown, horizontal) * RadiansToDegrees;

        return new GeomagneticFieldResult(
            xNorth,
            yEast,
            zDown,
            horizontal,
            total,
            declination,
            inclination,
            decimalYear,
            epoch.EpochYear,
            nMax,
            models.SourceSha256);
    }

    private static (double[,] P, double[,] DP) ComputeSchmidtLegendre(int nMax, double theta)
    {
        double sinTheta = Math.Sin(theta);
        double cosTheta = Math.Cos(theta);
        var p = new double[nMax + 1, nMax + 1];
        var dP = new double[nMax + 1, nMax + 1];
        p[0, 0] = 1d;

        // Gauss-normalized associated Legendre recursion.
        for (int n = 1; n <= nMax; n++)
        {
            for (int m = 0; m <= n; m++)
            {
                if (n == m)
                {
                    p[n, n] = sinTheta * p[n - 1, n - 1];
                    dP[n, n] =
                        sinTheta * dP[n - 1, n - 1] +
                        cosTheta * p[n - 1, n - 1];
                }
                else if (n == 1)
                {
                    p[n, m] = cosTheta * p[n - 1, m];
                    dP[n, m] =
                        cosTheta * dP[n - 1, m] -
                        sinTheta * p[n - 1, m];
                }
                else
                {
                    double numerator = (n - 1d) * (n - 1d) - m * m;
                    double denominator = (2d * n - 1d) * (2d * n - 3d);
                    double k = numerator / denominator;
                    p[n, m] = cosTheta * p[n - 1, m] - k * p[n - 2, m];
                    dP[n, m] =
                        cosTheta * dP[n - 1, m] -
                        sinTheta * p[n - 1, m] -
                        k * dP[n - 2, m];
                }
            }
        }

        // Schmidt quasi-normalization factors.
        var schmidt = new double[nMax + 1, nMax + 1];
        schmidt[0, 0] = 1d;
        for (int n = 1; n <= nMax; n++)
        {
            schmidt[n, 0] = schmidt[n - 1, 0] * (2d * n - 1d) / n;
            for (int m = 1; m <= n; m++)
            {
                double factor =
                    (n - m + 1d) * (m == 1 ? 2d : 1d) /
                    (n + m);
                schmidt[n, m] = schmidt[n, m - 1] * Math.Sqrt(factor);
            }
        }

        for (int n = 0; n <= nMax; n++)
        {
            for (int m = 0; m <= n; m++)
            {
                p[n, m] *= schmidt[n, m];
                dP[n, m] *= schmidt[n, m];
            }
        }

        return (p, dP);
    }

    private static void ValidateInput(
        double latitudeDeg,
        double longitudeDeg,
        double heightMeters,
        double decimalYear)
    {
        if (!double.IsFinite(latitudeDeg) || latitudeDeg < -90d || latitudeDeg > 90d)
            throw new ArgumentOutOfRangeException(nameof(latitudeDeg));
        if (!double.IsFinite(longitudeDeg))
            throw new ArgumentOutOfRangeException(nameof(longitudeDeg));
        if (!double.IsFinite(heightMeters))
            throw new ArgumentOutOfRangeException(nameof(heightMeters));
        if (!double.IsFinite(decimalYear))
            throw new ArgumentOutOfRangeException(nameof(decimalYear));
    }

    private static double NormalizeLongitude(double longitudeDeg)
    {
        double x = longitudeDeg % 360d;
        if (x > 180d)
            x -= 360d;
        else if (x < -180d)
            x += 360d;
        return x;
    }
}
