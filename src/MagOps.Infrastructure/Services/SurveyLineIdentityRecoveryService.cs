using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MagOps.Infrastructure.Services;

public sealed record SurveyLineIdentityRecoveryResult(
    FieldRouteSnapshot Route,
    bool Recovered,
    string Mode,
    string Summary,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Conservative fallback for route sources that are rich enough to draw task
/// progress but do not carry formal RouteSortie.SourceLines identity (for
/// example an externally imported KML batch). It never uses UAV magnetic data
/// to invent design geometry. Instead it recovers straight, dominant-axis
/// survey legs from the design-route polylines already feeding the field task
/// progress page. If the geometry is ambiguous, recovery is rejected and the
/// caller remains in compatibility mode.
/// </summary>
public static class SurveyLineIdentityRecoveryService
{
    public const string AlgorithmVersion = "survey-line-identity-recovery-2";

    private const double DuplicatePointToleranceM = 0.5d;
    private const double MinimumStraightLineLengthM = 30d;
    private const double StraightPathRatio = 0.965d;
    private const double StraightAxisToleranceDeg = 12d;
    private const double DominantAxisToleranceDeg = 18d;
    private const double MinimumDominantAxisConcentration = 0.55d;

    // Route-only identity recovery must distinguish long survey legs from
    // shallow/oblique transit connectors. A fixed 18-degree axis gate is too
    // broad: a connector that moves 100 m sideways over several kilometres can
    // still look "parallel" and merge adjacent survey legs into one bent run.
    //
    // The recovery therefore finds the strongest narrow axis family first and
    // derives a tight tolerance from that family. This is intentionally used
    // only by the compatibility recovery path; formal RouteSortie.SourceLines
    // remain authoritative and are never re-inferred here.
    private const double StableSurveyAxisBinWidthDeg = 0.25d;
    private const double StableSurveyAxisCoreHalfWindowDeg = 1.00d;
    private const double MinimumStableSurveyAxisSupport = 0.45d;
    private const double StableSurveyAxisTolerancePaddingDeg = 0.50d;
    private const double MinimumStableSurveyAxisToleranceDeg = 0.80d;
    private const double MaximumStableSurveyAxisToleranceDeg = 2.00d;

    private const double MinimumRecoveredSortieRatio = 0.90d;

    public static SurveyLineIdentityRecoveryResult Recover(FieldRouteSnapshot route)
    {
        if (route.HasSurveyLineIdentity)
        {
            return new SurveyLineIdentityRecoveryResult(
                route,
                false,
                "FORMAL",
                $"已存在正式测线身份：{route.SurveyLines.Count} 条。",
                []);
        }

        var warnings = new List<string>();
        var recovered = new List<PlannedSurveyLineTask>();
        int order = 1;
        int eligibleSorties = 0;
        int recoveredSorties = 0;

        foreach (PlannedFieldSortie sortie in route.Sorties.OrderBy(x => x.Order))
        {
            List<GeoCoordinate> points = Normalize(sortie.Coordinates);
            if (points.Count < 2)
                continue;

            double totalLengthM = PolylineLength(points);
            if (!double.IsFinite(totalLengthM) || totalLengthM < MinimumStraightLineLengthM)
                continue;

            eligibleSorties++;
            IReadOnlyList<PlannedSurveyLineTask> lines =
                RecoverFromSortie(sortie, points, ref order);
            if (lines.Count == 0)
            {
                warnings.Add($"{sortie.Name}：路线几何不足以可靠恢复测线。 ");
                continue;
            }

            recoveredSorties++;
            recovered.AddRange(lines);
        }

        if (eligibleSorties == 0 || recovered.Count == 0)
        {
            return new SurveyLineIdentityRecoveryResult(
                route,
                false,
                "UNAVAILABLE",
                "任务进度/导入航线只有架次轨迹，未发现可安全恢复的稳定直线测线。",
                warnings);
        }

        double recoveredSortieRatio = recoveredSorties / (double)eligibleSorties;
        if (recoveredSortieRatio < MinimumRecoveredSortieRatio)
        {
            warnings.Add(
                $"仅 {recoveredSorties}/{eligibleSorties} 个有效架次可恢复稳定测线，低于 {MinimumRecoveredSortieRatio:P0} 安全阈值；为避免误分线，未启用推断结果。 ");
            return new SurveyLineIdentityRecoveryResult(
                route,
                false,
                "LOW_CONFIDENCE",
                "任务进度路线可见，但测线几何恢复覆盖率不足，不能安全用于生产分线。",
                warnings);
        }

        string identityHash = StableIdentityHash(recovered);
        FieldRouteSnapshot enriched = route with
        {
            SourceLabel = route.SourceLabel + "（任务进度几何恢复测线）",
            Fingerprint = route.Fingerprint + "|SLREC|" + identityHash,
            SurveyLines = recovered
        };

        string summary =
            $"从任务进度/设计路线几何恢复 {recovered.Count} 条测线，覆盖 {recoveredSorties}/{eligibleSorties} 个架次；" +
            "该身份为兼容恢复，不等同于保存的 RouteSortie.SourceLines。";

        return new SurveyLineIdentityRecoveryResult(
            enriched,
            true,
            "GEOMETRY_RECOVERED",
            summary,
            warnings);
    }

    private static IReadOnlyList<PlannedSurveyLineTask> RecoverFromSortie(
        PlannedFieldSortie sortie,
        IReadOnlyList<GeoCoordinate> points,
        ref int order)
    {
        double totalLengthM = PolylineLength(points);
        double directLengthM = Distance(points[0], points[^1]);
        if (totalLengthM <= 0d || directLengthM <= 0d)
            return [];

        List<PrimitiveSegment> segments = BuildSegments(points);
        if (segments.Count == 0)
            return [];

        double directAxis = AxisAzimuth(points[0], points[^1]);
        double alignedLength = segments
            .Where(x => AxisDifference(x.AxisDeg, directAxis) <= StraightAxisToleranceDeg)
            .Sum(x => x.LengthM);
        double maxCrossTrack = MaxCrossTrack(points, points[0], points[^1]);
        double straightCrossTrackLimit = Math.Max(6d, Math.Min(25d, directLengthM * 0.003d));

        if (directLengthM >= MinimumStraightLineLengthM &&
            directLengthM / totalLengthM >= StraightPathRatio &&
            alignedLength / totalLengthM >= 0.90d &&
            maxCrossTrack <= straightCrossTrackLimit)
        {
            string lineName = PreferredLineName(sortie, 1, oneLine: true);
            return
            [
                CreateTask(
                    ref order,
                    sortie,
                    lineName,
                    points[0],
                    points[^1],
                    directLengthM)
            ];
        }

        (double dominantAxis, double concentration) = DominantAxis(segments);
        if (!double.IsFinite(dominantAxis) ||
            concentration < MinimumDominantAxisConcentration)
        {
            return [];
        }

        StableSurveyAxis stableAxis = ResolveStableSurveyAxis(segments);
        if (!stableAxis.IsValid ||
            stableAxis.SupportRatio < MinimumStableSurveyAxisSupport)
        {
            return [];
        }

        bool[] candidate = segments
            .Select(x =>
                AxisDifference(x.AxisDeg, stableAxis.AxisDeg) <=
                Math.Min(DominantAxisToleranceDeg, stableAxis.ToleranceDeg))
            .ToArray();

        List<Run> runs = BuildCandidateRuns(points, segments, candidate);
        if (runs.Count == 0)
            return [];

        double longestRun = runs.Max(x => x.PathLengthM);
        List<double> connectorLengths = segments
            .Where((_, index) => !candidate[index])
            .Select(x => x.LengthM)
            .Where(x => x >= 1d && double.IsFinite(x))
            .OrderBy(x => x)
            .ToList();
        double connectorScale = connectorLengths.Count == 0
            ? 20d
            : Median(connectorLengths);
        double minimumRunLength = Math.Max(30d, Math.Min(150d, connectorScale * 2.5d));
        minimumRunLength = Math.Min(minimumRunLength, Math.Max(30d, longestRun * 0.45d));

        List<Run> accepted = runs
            .Where(run =>
                run.PathLengthM >= minimumRunLength &&
                run.DirectLengthM >= MinimumStraightLineLengthM &&
                run.DirectLengthM / Math.Max(1d, run.PathLengthM) >= 0.90d &&
                run.MaxCrossTrackM <= Math.Max(8d, Math.Min(30d, run.DirectLengthM * 0.005d)))
            .ToList();

        // A bent route with only one accepted run is too ambiguous to call a
        // multi-leg survey sortie. Pure one-line routes were already handled by
        // the straight-path branch above.
        if (accepted.Count < 2)
            return [];

        var result = new List<PlannedSurveyLineTask>(accepted.Count);
        for (int i = 0; i < accepted.Count; i++)
        {
            Run run = accepted[i];
            string lineName = PreferredLineName(sortie, i + 1, oneLine: false);
            result.Add(CreateTask(
                ref order,
                sortie,
                lineName,
                run.Start,
                run.End,
                run.DirectLengthM));
        }

        return result;
    }

    private static PlannedSurveyLineTask CreateTask(
        ref int order,
        PlannedFieldSortie sortie,
        string lineName,
        GeoCoordinate start,
        GeoCoordinate end,
        double lengthM)
    {
        string taskId = StableTaskId(lineName, start, end);
        return new PlannedSurveyLineTask(
            order++,
            sortie.Order,
            taskId,
            sortie.Name,
            lineName,
            start,
            end,
            lengthM);
    }

    private static string PreferredLineName(
        PlannedFieldSortie sortie,
        int index,
        bool oneLine)
    {
        string baseName = string.IsNullOrWhiteSpace(sortie.Name)
            ? $"架次{sortie.Order:D2}"
            : sortie.Name.Trim();
        return oneLine
            ? baseName
            : $"{baseName}_L{index:D3}";
    }

    private static List<GeoCoordinate> Normalize(IReadOnlyList<GeoCoordinate> source)
    {
        var result = new List<GeoCoordinate>(source.Count);
        foreach (GeoCoordinate point in source)
        {
            if (!double.IsFinite(point.Longitude) || !double.IsFinite(point.Latitude))
                continue;
            if (result.Count > 0 && Distance(result[^1], point) < DuplicatePointToleranceM)
                continue;
            result.Add(point);
        }
        return result;
    }

    private static List<PrimitiveSegment> BuildSegments(IReadOnlyList<GeoCoordinate> points)
    {
        var result = new List<PrimitiveSegment>(Math.Max(0, points.Count - 1));
        for (int i = 0; i < points.Count - 1; i++)
        {
            GeoCoordinate start = points[i];
            GeoCoordinate end = points[i + 1];
            double length = Distance(start, end);
            if (!double.IsFinite(length) || length < 0.75d)
                continue;
            result.Add(new PrimitiveSegment(
                i,
                i + 1,
                start,
                end,
                length,
                AxisAzimuth(start, end)));
        }
        return result;
    }

    private static List<Run> BuildCandidateRuns(
        IReadOnlyList<GeoCoordinate> points,
        IReadOnlyList<PrimitiveSegment> segments,
        IReadOnlyList<bool> candidate)
    {
        var result = new List<Run>();
        int startSegment = -1;
        int lastSegment = -1;
        double pathLength = 0d;

        void Flush()
        {
            if (startSegment < 0 || lastSegment < startSegment)
                return;
            PrimitiveSegment first = segments[startSegment];
            PrimitiveSegment last = segments[lastSegment];
            GeoCoordinate start = first.Start;
            GeoCoordinate end = last.End;
            double direct = Distance(start, end);
            var runPoints = new List<GeoCoordinate>();
            runPoints.Add(start);
            for (int segmentIndex = startSegment; segmentIndex <= lastSegment; segmentIndex++)
                runPoints.Add(segments[segmentIndex].End);
            result.Add(new Run(
                start,
                end,
                pathLength,
                direct,
                MaxCrossTrack(runPoints, start, end)));
            startSegment = -1;
            lastSegment = -1;
            pathLength = 0d;
        }

        for (int i = 0; i < segments.Count; i++)
        {
            if (!candidate[i])
            {
                Flush();
                continue;
            }

            if (startSegment < 0)
                startSegment = i;
            lastSegment = i;
            pathLength += segments[i].LengthM;
        }
        Flush();
        return result;
    }

    private static StableSurveyAxis ResolveStableSurveyAxis(
        IReadOnlyList<PrimitiveSegment> segments)
    {
        PrimitiveSegment[] eligible = segments
            .Where(x =>
                x.LengthM >= 2d &&
                double.IsFinite(x.LengthM) &&
                double.IsFinite(x.AxisDeg))
            .ToArray();
        if (eligible.Length == 0)
            return default;

        int binCount =
            (int)Math.Round(
                180d / StableSurveyAxisBinWidthDeg,
                MidpointRounding.AwayFromZero);
        if (binCount <= 0)
            return default;

        var binWeights = new double[binCount];
        double totalLength = 0d;
        foreach (PrimitiveSegment segment in eligible)
        {
            double axis = NormalizeAxis(segment.AxisDeg);
            int bin = Math.Clamp(
                (int)Math.Floor(axis / StableSurveyAxisBinWidthDeg),
                0,
                binCount - 1);
            binWeights[bin] += segment.LengthM;
            totalLength += segment.LengthM;
        }
        if (totalLength <= 0d)
            return default;

        int peakBin = -1;
        double peakSupport = double.NegativeInfinity;
        double peakBinWeight = double.NegativeInfinity;
        for (int candidateBin = 0; candidateBin < binWeights.Length; candidateBin++)
        {
            if (binWeights[candidateBin] <= 0d)
                continue;

            double center =
                (candidateBin + 0.5d) *
                StableSurveyAxisBinWidthDeg;
            double support = 0d;
            for (int otherBin = 0; otherBin < binWeights.Length; otherBin++)
            {
                if (binWeights[otherBin] <= 0d)
                    continue;
                double otherCenter =
                    (otherBin + 0.5d) *
                    StableSurveyAxisBinWidthDeg;
                if (AxisDifference(center, otherCenter) <=
                    StableSurveyAxisCoreHalfWindowDeg)
                {
                    support += binWeights[otherBin];
                }
            }

            if (support > peakSupport + 1e-9d ||
                (Math.Abs(support - peakSupport) <= 1e-9d &&
                 binWeights[candidateBin] > peakBinWeight))
            {
                peakBin = candidateBin;
                peakSupport = support;
                peakBinWeight = binWeights[candidateBin];
            }
        }

        if (peakBin < 0)
            return default;

        double peakCenter =
            (peakBin + 0.5d) *
            StableSurveyAxisBinWidthDeg;
        PrimitiveSegment[] core = eligible
            .Where(x =>
                AxisDifference(
                    NormalizeAxis(x.AxisDeg),
                    peakCenter) <=
                StableSurveyAxisCoreHalfWindowDeg)
            .ToArray();
        if (core.Length == 0)
            return default;

        double sumCos = 0d;
        double sumSin = 0d;
        double coreLength = 0d;
        foreach (PrimitiveSegment segment in core)
        {
            double angle =
                NormalizeAxis(segment.AxisDeg) *
                Math.PI / 180d * 2d;
            sumCos +=
                Math.Cos(angle) *
                segment.LengthM;
            sumSin +=
                Math.Sin(angle) *
                segment.LengthM;
            coreLength +=
                segment.LengthM;
        }
        if (coreLength <= 0d)
            return default;

        double axis2 = Math.Atan2(sumSin, sumCos);
        double stableAxis =
            axis2 * 0.5d * 180d / Math.PI;
        stableAxis =
            NormalizeAxis(stableAxis);

        List<WeightedDeviation> deviations = core
            .Select(x =>
                new WeightedDeviation(
                    AxisDifference(
                        NormalizeAxis(x.AxisDeg),
                        stableAxis),
                    x.LengthM))
            .OrderBy(x => x.DeviationDeg)
            .ToList();

        double weightedMedian =
            WeightedMedianDeviation(
                deviations,
                coreLength);
        double tolerance =
            Math.Clamp(
                weightedMedian +
                StableSurveyAxisTolerancePaddingDeg,
                MinimumStableSurveyAxisToleranceDeg,
                MaximumStableSurveyAxisToleranceDeg);

        return new StableSurveyAxis(
            stableAxis,
            tolerance,
            coreLength / totalLength,
            true);
    }

    private static double WeightedMedianDeviation(
        IReadOnlyList<WeightedDeviation> values,
        double totalWeight)
    {
        if (values.Count == 0 ||
            totalWeight <= 0d)
        {
            return double.NaN;
        }

        double target =
            totalWeight * 0.5d;
        double cumulative = 0d;
        foreach (WeightedDeviation value in values)
        {
            cumulative += value.Weight;
            if (cumulative >= target)
                return value.DeviationDeg;
        }

        return values[^1].DeviationDeg;
    }

    private static double NormalizeAxis(
        double axisDeg)
    {
        double axis =
            axisDeg % 180d;
        if (axis < 0d)
            axis += 180d;
        return axis;
    }

    private static (double AxisDeg, double Concentration) DominantAxis(
        IReadOnlyList<PrimitiveSegment> segments)
    {
        double sumCos = 0d;
        double sumSin = 0d;
        double total = 0d;
        foreach (PrimitiveSegment segment in segments)
        {
            if (segment.LengthM < 2d)
                continue;
            double angle = segment.AxisDeg * Math.PI / 180d * 2d;
            sumCos += Math.Cos(angle) * segment.LengthM;
            sumSin += Math.Sin(angle) * segment.LengthM;
            total += segment.LengthM;
        }
        if (total <= 0d)
            return (double.NaN, 0d);
        double angle2 = Math.Atan2(sumSin, sumCos);
        double axis = angle2 * 0.5d * 180d / Math.PI;
        if (axis < 0d)
            axis += 180d;
        double concentration = Math.Sqrt(sumCos * sumCos + sumSin * sumSin) / total;
        return (axis, concentration);
    }

    private static double AxisAzimuth(GeoCoordinate start, GeoCoordinate end)
    {
        (double east, double north) = DeltaMeters(start, end);
        double azimuth = Math.Atan2(east, north) * 180d / Math.PI;
        if (azimuth < 0d)
            azimuth += 360d;
        if (azimuth >= 180d)
            azimuth -= 180d;
        return azimuth;
    }

    private static double AxisDifference(double first, double second)
    {
        double delta = Math.Abs(first - second) % 180d;
        return delta > 90d ? 180d - delta : delta;
    }

    private static double MaxCrossTrack(
        IReadOnlyList<GeoCoordinate> points,
        GeoCoordinate start,
        GeoCoordinate end)
    {
        (double lineEast, double lineNorth) = DeltaMeters(start, end);
        double length = Math.Sqrt(lineEast * lineEast + lineNorth * lineNorth);
        if (length <= 0.001d)
            return double.PositiveInfinity;
        double unitEast = lineEast / length;
        double unitNorth = lineNorth / length;
        double max = 0d;
        foreach (GeoCoordinate point in points)
        {
            (double east, double north) = DeltaMeters(start, point);
            double cross = Math.Abs(east * unitNorth - north * unitEast);
            if (cross > max)
                max = cross;
        }
        return max;
    }

    private static double PolylineLength(IReadOnlyList<GeoCoordinate> points)
    {
        double total = 0d;
        for (int i = 1; i < points.Count; i++)
            total += Distance(points[i - 1], points[i]);
        return total;
    }

    private static double Distance(GeoCoordinate a, GeoCoordinate b)
    {
        const double earthRadiusM = 6371008.8d;
        double lat1 = a.Latitude * Math.PI / 180d;
        double lat2 = b.Latitude * Math.PI / 180d;
        double dLat = (b.Latitude - a.Latitude) * Math.PI / 180d;
        double dLon = (b.Longitude - a.Longitude) * Math.PI / 180d;
        double sinLat = Math.Sin(dLat / 2d);
        double sinLon = Math.Sin(dLon / 2d);
        double h = sinLat * sinLat + Math.Cos(lat1) * Math.Cos(lat2) * sinLon * sinLon;
        return earthRadiusM * 2d * Math.Asin(Math.Min(1d, Math.Sqrt(Math.Max(0d, h))));
    }

    private static (double East, double North) DeltaMeters(
        GeoCoordinate origin,
        GeoCoordinate point)
    {
        const double earthRadiusM = 6371008.8d;
        double lat0 = origin.Latitude * Math.PI / 180d;
        double lat1 = point.Latitude * Math.PI / 180d;
        double meanLat = (lat0 + lat1) * 0.5d;
        double east = (point.Longitude - origin.Longitude) * Math.PI / 180d * earthRadiusM * Math.Cos(meanLat);
        double north = (point.Latitude - origin.Latitude) * Math.PI / 180d * earthRadiusM;
        return (east, north);
    }

    private static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
            return double.NaN;
        int mid = values.Count / 2;
        return values.Count % 2 == 1
            ? values[mid]
            : (values[mid - 1] + values[mid]) * 0.5d;
    }

    private static string StableTaskId(
        string lineName,
        GeoCoordinate start,
        GeoCoordinate end)
    {
        string first = string.Format(
            CultureInfo.InvariantCulture,
            "{0:F7},{1:F7}",
            start.Longitude,
            start.Latitude);
        string second = string.Format(
            CultureInfo.InvariantCulture,
            "{0:F7},{1:F7}",
            end.Longitude,
            end.Latitude);
        if (string.Compare(first, second, StringComparison.Ordinal) > 0)
            (first, second) = (second, first);
        string canonical =
            "RECOVERED|" + lineName.Trim().ToUpperInvariant() + "|" + first + "|" + second;
        string hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
        return "SLR-" + hash[..16];
    }

    private static string StableIdentityHash(IReadOnlyList<PlannedSurveyLineTask> lines)
    {
        string canonical = string.Join(
            "\n",
            lines.OrderBy(x => x.Order).Select(x =>
                x.TaskId + "|" +
                x.LineName + "|" +
                x.Start.Longitude.ToString("F7", CultureInfo.InvariantCulture) + "," +
                x.Start.Latitude.ToString("F7", CultureInfo.InvariantCulture) + "|" +
                x.End.Longitude.ToString("F7", CultureInfo.InvariantCulture) + "," +
                x.End.Latitude.ToString("F7", CultureInfo.InvariantCulture)));
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant()[..16];
    }

    private readonly record struct StableSurveyAxis(
        double AxisDeg,
        double ToleranceDeg,
        double SupportRatio,
        bool IsValid);

    private readonly record struct WeightedDeviation(
        double DeviationDeg,
        double Weight);

    private sealed record PrimitiveSegment(
        int StartIndex,
        int EndIndex,
        GeoCoordinate Start,
        GeoCoordinate End,
        double LengthM,
        double AxisDeg);

    private sealed record Run(
        GeoCoordinate Start,
        GeoCoordinate End,
        double PathLengthM,
        double DirectLengthM,
        double MaxCrossTrackM);
}
