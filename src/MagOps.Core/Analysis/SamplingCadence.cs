namespace MagOps.Core.Analysis;

/// <summary>Evidence from adjacent acquisition records; never sorts or fills missing observations.</summary>
public sealed record CadenceEvidence(double? IntervalSeconds, int Pairs, int MatchingPairs,
    int DuplicatePairs, int ReversePairs, int InvalidPairs, string Note);

public static class SamplingCadence
{
    public static bool Matches(double? a, double? b, double interval, bool reverse = false) =>
        a is { } x && b is { } y && double.IsFinite(x) && double.IsFinite(y) &&
        double.IsFinite(interval) && interval > 0 && (reverse ? y < x : y > x) &&
        Math.Abs((reverse ? Math.Abs(y - x) : y - x) - interval) <= Math.Max(1e-8, interval * .1);

    public static CadenceEvidence Analyze(IReadOnlyList<double?> times, IReadOnlyList<string>? segments = null)
    {
        if (segments is not null && segments.Count != times.Count) throw new ArgumentException("时间与采集段数量不一致。");
        var steps = new List<double>();
        int pairs = 0, duplicate = 0, reversed = 0, invalid = 0;
        for (int i = 1; i < times.Count; i++)
        {
            if (segments is not null && (segments[i].Length == 0 || segments[i] != segments[i - 1])) continue;
            pairs++;
            if (times[i] is not { } b || times[i - 1] is not { } a || !double.IsFinite(a) || !double.IsFinite(b)) { invalid++; continue; }
            double d = b - a;
            if (d == 0) duplicate++;
            else if (d < 0) reversed++;
            else steps.Add(d);
        }
        if (steps.Count < 4) return new(null, pairs, 0, duplicate, reversed, invalid, "有效相邻时间步长不足，不能自动确定采样间隔。");
        // Rounded modal seed avoids a single large gap corrupting the candidate. Refine with its median.
        double seed = steps.GroupBy(d => Math.Round(d, 6)).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).First().First();
        var cluster = steps.Where(d => Math.Abs(d - seed) <= Math.Max(1e-8, seed * .1)).Order().ToArray();
        double interval = cluster[cluster.Length / 2];
        int matching = steps.Count(d => Math.Abs(d - interval) <= Math.Max(1e-8, interval * .1));
        bool supported = interval > 0 && matching >= 4 && matching >= pairs * .8;
        return new(supported ? interval : null, pairs, matching, duplicate, reversed, invalid,
            $"相邻时间对{pairs}；主步长{interval:G8}秒，符合{matching}；重复{duplicate}、倒序{reversed}、无效{invalid}。" +
            (supported ? "主步长可用于参考计算；逐窗口仍检查断口，不证明没有整段漏采。" : "主步长证据不足；不自动采用采样间隔。"));
    }
}
