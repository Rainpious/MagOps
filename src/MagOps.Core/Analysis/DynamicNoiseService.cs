using System.Globalization;
using MagOps.Core.Routes;

namespace MagOps.Core.Analysis;

public sealed record DynamicNoiseLineResult(
    string Group,
    int RawN,
    int ValidTN,
    int InvalidTN,
    int HighGradientN,
    int GradientUnavailableN,
    int BN,
    double BMean,
    double BStd,
    double Sv,
    string CoordMode,
    double MaxGradient,
    double MeanGradient);

public sealed record DynamicNoiseSummary(
    int LineCount,
    int ValidLineCount,
    double SvMax,
    string SvMaxGroup,
    double SvMin,
    string SvMinGroup,
    double SvMean,
    double SvMedian,
    int TotalSourceRows,
    int TotalInvalidT,
    int TotalHighGradient,
    int TotalGradientUnavailable);

public sealed class DynamicNoiseResult
{
    public List<DynamicNoiseLineResult> Lines { get; init; } = [];
    public DynamicNoiseSummary Summary { get; init; } =
        new(0, 0, double.NaN, "", double.NaN, "", double.NaN, double.NaN, 0, 0, 0, 0);
}

public sealed class DynamicNoiseService
{
    private readonly TextTableAnalysisService _tables = new();

    public DynamicNoiseResult Compute(
        string sourcePath,
        int tColumnIndex,
        int? groupColumnIndex = null,
        int? xColumnIndex = null,
        int? yColumnIndex = null,
        string coordModeRequested = "自动判断",
        bool useGradientFilter = true,
        double gradientLimitNtKm = 600.0)
    {
        TextTable table = _tables.Read(sourcePath);
        if (table.Rows.Count == 0)
            throw new InvalidDataException("动态噪声源表只有表头，没有数据。");

        ValidateIndex(table.Headers.Length, tColumnIndex, "磁测 T / 磁异常列");
        ValidateOptionalIndex(table.Headers.Length, groupColumnIndex, "测线/分组列");
        ValidateOptionalIndex(table.Headers.Length, xColumnIndex, "X / 经度列");
        ValidateOptionalIndex(table.Headers.Length, yColumnIndex, "Y / 纬度列");

        if (useGradientFilter)
        {
            if (!double.IsFinite(gradientLimitNtKm) || gradientLimitNtKm <= 0)
                throw new ArgumentOutOfRangeException(nameof(gradientLimitNtKm), "水平梯度阈值必须大于 0。");
            if (!xColumnIndex.HasValue || !yColumnIndex.HasValue)
                throw new InvalidOperationException("已启用“剔除水平梯度超限点”，必须选择 X/Y 或经度/纬度列。");
        }

        var groups = new Dictionary<string, List<string[]>>();
        if (!groupColumnIndex.HasValue)
            groups["ALL"] = table.Rows.ToList();
        else
        {
            foreach (string[] row in table.Rows)
            {
                string group = groupColumnIndex.Value < row.Length ? row[groupColumnIndex.Value].Trim() : "";
                if (!groups.TryGetValue(group, out var list))
                    groups[group] = list = [];
                list.Add(row);
            }
        }

        var results = new List<DynamicNoiseLineResult>();
        int allHigh = 0, allInvalidT = 0, allGradientUnavailable = 0;

        foreach (var pair in groups.OrderBy(x => x.Key, GroupValueComparer.Instance))
        {
            string group = pair.Key;
            List<string[]> rows = pair.Value;
            double[] t = rows.Select(r => OptionalFloat(r, tColumnIndex)).ToArray();
            int rawN = t.Length;
            bool[] tValid = t.Select(double.IsFinite).ToArray();
            int invalidT = tValid.Count(v => !v);
            allInvalidT += invalidT;

            double[]? gradients = null;
            string coordMode = "";
            bool[] pointOk = tValid.ToArray();
            int highGradient = 0, gradientUnavailable = 0;
            double maxGradient = double.NaN, meanGradient = double.NaN;

            if (useGradientFilter)
            {
                double[] xs = rows.Select(r => OptionalFloat(r, xColumnIndex!.Value)).ToArray();
                double[] ys = rows.Select(r => OptionalFloat(r, yColumnIndex!.Value)).ToArray();
                coordMode = CoordMode(xs, ys, coordModeRequested);
                gradients = PointGradients(t, xs, ys, coordMode);

                bool[] gradAvailable = gradients.Select(double.IsFinite).ToArray();
                bool[] highMask = gradients.Select((g, i) => gradAvailable[i] && g > gradientLimitNtKm).ToArray();
                highGradient = highMask.Count(v => v);
                gradientUnavailable = Enumerable.Range(0, rawN).Count(i => tValid[i] && !gradAvailable[i]);
                allHigh += highGradient;
                allGradientUnavailable += gradientUnavailable;

                pointOk = Enumerable.Range(0, rawN)
                    .Select(i => tValid[i] && gradAvailable[i] && !highMask[i]).ToArray();

                double[] available = gradients.Where(double.IsFinite).ToArray();
                if (available.Length > 0)
                {
                    maxGradient = available.Max();
                    meanGradient = available.Average();
                }
            }

            var bValid = new List<double>();
            if (rawN >= 5)
            {
                for (int i = 0; i <= rawN - 5; i++)
                {
                    double b = MagneticNoiseMath.FourthDifference(t[i], t[i + 1], t[i + 2], t[i + 3], t[i + 4]);
                    bool windowOk = pointOk[i] && pointOk[i + 1] && pointOk[i + 2] &&
                                    pointOk[i + 3] && pointOk[i + 4] && double.IsFinite(b);
                    if (windowOk) bValid.Add(b);
                }
            }

            int bN = bValid.Count;
            double bMean = double.NaN, bStd = double.NaN, sv = double.NaN;
            if (bN >= 2)
            {
                bMean = bValid.Average();
                bStd = MagneticNoiseMath.SampleStandardDeviation(bValid)!.Value;
                sv = bStd / Math.Sqrt(70.0);
            }

            results.Add(new DynamicNoiseLineResult(
                group.Length > 0 ? group : "(blank)",
                rawN,
                tValid.Count(v => v),
                invalidT,
                highGradient,
                gradientUnavailable,
                bN,
                bMean,
                bStd,
                sv,
                coordMode,
                maxGradient,
                meanGradient));
        }

        DynamicNoiseLineResult[] finite = results.Where(x => double.IsFinite(x.Sv)).ToArray();
        if (finite.Length == 0)
            throw new InvalidOperationException(
                "没有任何测线得到至少 2 个有效四阶差分值 B_i。请检查磁测列、分组列、坐标列和梯度阈值。");

        double[] svs = finite.Select(x => x.Sv).ToArray();
        DynamicNoiseLineResult max = finite.MaxBy(x => x.Sv)!;
        DynamicNoiseLineResult min = finite.MinBy(x => x.Sv)!;

        return new DynamicNoiseResult
        {
            Lines = results,
            Summary = new DynamicNoiseSummary(
                results.Count,
                finite.Length,
                svs.Max(),
                max.Group,
                svs.Min(),
                min.Group,
                svs.Average(),
                Median(svs),
                table.Rows.Count,
                allInvalidT,
                allHigh,
                allGradientUnavailable)
        };
    }

    public static double[] PointGradients(
        IReadOnlyList<double> t,
        IReadOnlyList<double> xs,
        IReadOnlyList<double> ys,
        string coordMode)
    {
        int n = t.Count;
        var gradients = Enumerable.Repeat(double.NaN, n).ToArray();
        if (n < 2) return gradients;

        bool[] coordOk = Enumerable.Range(0, n)
            .Select(i => double.IsFinite(xs[i]) && double.IsFinite(ys[i])).ToArray();
        const double epsM = 1e-6;

        var prevDistinct = Enumerable.Repeat(-1, n).ToArray();
        int last = -1;
        for (int i = 0; i < n; i++)
        {
            if (i > 0 && coordOk[i] && coordOk[i - 1])
            {
                double d = DistanceM(xs[i], ys[i], xs[i - 1], ys[i - 1], coordMode);
                if (double.IsFinite(d) && d > epsM) last = i - 1;
            }
            prevDistinct[i] = last;
        }

        var nextDistinct = Enumerable.Repeat(-1, n).ToArray();
        last = -1;
        for (int i = n - 1; i >= 0; i--)
        {
            if (i < n - 1 && coordOk[i] && coordOk[i + 1])
            {
                double d = DistanceM(xs[i], ys[i], xs[i + 1], ys[i + 1], coordMode);
                if (double.IsFinite(d) && d > epsM) last = i + 1;
            }
            nextDistinct[i] = last;
        }

        for (int i = 0; i < n; i++)
        {
            if (!double.IsFinite(t[i]) || !coordOk[i]) continue;
            int left = prevDistinct[i];
            int right = nextDistinct[i];

            if (left >= 0 && right >= 0 && double.IsFinite(t[left]) && double.IsFinite(t[right]))
            {
                double d = DistanceM(xs[left], ys[left], xs[right], ys[right], coordMode);
                if (double.IsFinite(d) && d > epsM)
                {
                    gradients[i] = Math.Abs(t[right] - t[left]) / d * 1000.0;
                    continue;
                }
            }

            if (left >= 0 && double.IsFinite(t[left]))
            {
                double d = DistanceM(xs[left], ys[left], xs[i], ys[i], coordMode);
                if (double.IsFinite(d) && d > epsM)
                {
                    gradients[i] = Math.Abs(t[i] - t[left]) / d * 1000.0;
                    continue;
                }
            }

            if (right >= 0 && double.IsFinite(t[right]))
            {
                double d = DistanceM(xs[i], ys[i], xs[right], ys[right], coordMode);
                if (double.IsFinite(d) && d > epsM)
                    gradients[i] = Math.Abs(t[right] - t[i]) / d * 1000.0;
            }
        }

        return gradients;
    }

    private static string CoordMode(double[] xs, double[] ys, string requested)
    {
        if (requested == "经纬度（WGS84）") return "geographic";
        if (requested == "平面坐标（米制）") return "projected";

        var valid = Enumerable.Range(0, xs.Length)
            .Where(i => double.IsFinite(xs[i]) && double.IsFinite(ys[i]))
            .Take(1000)
            .ToArray();
        if (valid.Length == 0) return "projected";
        return valid.All(i => xs[i] is >= -180 and <= 180 && ys[i] is >= -90 and <= 90)
            ? "geographic" : "projected";
    }

    private static double DistanceM(double x1, double y1, double x2, double y2, string mode)
    {
        if (!(double.IsFinite(x1) && double.IsFinite(y1) && double.IsFinite(x2) && double.IsFinite(y2)))
            return double.NaN;
        return mode == "geographic"
            ? RouteCoordinateMath.Haversine(x1, y1, x2, y2)
            : Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1));
    }

    private static double OptionalFloat(string[] row, int idx)
    {
        if (idx < 0 || idx >= row.Length) return double.NaN;
        string text = row[idx].Trim();
        return double.TryParse(text.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
               && double.IsFinite(v)
            ? v : double.NaN;
    }

    private static double SampleStd(IReadOnlyList<double> values)
    {
        if (values.Count < 2) return double.NaN;
        double mean = values.Average();
        double sumSq = values.Sum(x => (x - mean) * (x - mean));
        return Math.Sqrt(sumSq / (values.Count - 1));
    }

    private static double Median(IEnumerable<double> values) =>
        RouteCoordinateMath.Median(values);

    private static void ValidateIndex(int count, int index, string name)
    {
        if (index < 0 || index >= count)
            throw new ArgumentOutOfRangeException(nameof(index), $"{name}超出表格列范围。");
    }

    private static void ValidateOptionalIndex(int count, int? index, string name)
    {
        if (index.HasValue) ValidateIndex(count, index.Value, name);
    }
}
