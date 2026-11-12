using MagOps.Core.Domain;

namespace MagOps.Infrastructure.Services;

/// <summary>Refines continuous streams only. Geometry confirms a change; raw samples define the cut.</summary>
internal static class TrackShallowTurnRefiner
{
    private const int MaximumDepth = 6;
    private const double MinimumSideSpanM = 40, MinimumDirectionChangeDegrees = 3, MaximumResidualRatio = .35, MinimumConfirmationSeconds = 2;
    internal static object RuleSnapshot => new { MaximumDepth, MinimumSideSpanM, MinimumDirectionChangeDegrees,
        MaximumResidualRatio, MinimumConfirmationSeconds, Aggregation = "UTC_SECOND_COORDINATE_MEDIAN",
        StableRatio = "MinimumAlignedRatio within CoreM", Departure = "continuous beyond CorridorM AND directed local heading change",
        Cut = "nearest original coordinate to fitted-body intersection within ObservationSeconds (minimum 2s)" };
    internal readonly record struct Span(int First, int Last, int Confirmed, bool Refined);
    private readonly record struct Observation(int First, int Last, double X, double Y, DateTimeOffset Time);
    private readonly record struct Fit(double X, double Y, double Dx, double Dy, double Error)
    {
        public double Distance(double x, double y) => Math.Abs((x - X) * Dy - (y - Y) * Dx);
    }

    internal static IEnumerable<Span> Refine(List<SurveyTrackPoint> points, int first, int last, int confirmed,
        TrackRecognitionOptions options, WholeLegMatchOptions matching, int depth = 0, bool refined = false)
    {
        if (depth >= MaximumDepth || last - first < 6)
        { yield return new(first, last, confirmed, refined); yield break; }
        var origin = points[first].Coordinate;
        var observations = new List<Observation>();
        // Equal-weight seconds prevent a high-rate burst or duplicate UTC seconds from dominating the fit.
        for (int i = first; i <= last;)
        {
            int end = i;
            while (end < last && points[end + 1].TimestampUtc.ToUnixTimeSeconds() == points[i].TimestampUtc.ToUnixTimeSeconds()) end++;
            var coordinates = Enumerable.Range(i, end - i + 1).Select(j => EndpointLineRecognizer.Delta(origin, points[j].Coordinate)).ToArray();
            double Median(IEnumerable<double> values) { var ordered = values.Order().ToArray(); int m = ordered.Length / 2;
                return ordered.Length % 2 == 0 ? (ordered[m - 1] + ordered[m]) / 2 : ordered[m]; }
            observations.Add(new(i, end, Median(coordinates.Select(p => p.X)), Median(coordinates.Select(p => p.Y)), points[(i + end) / 2].TimestampUtc));
            i = end + 1;
        }
        int n = observations.Count;
        if (n < 8) { yield return new(first, last, confirmed, refined); yield break; }
        var sx = new double[n + 1]; var sy = new double[n + 1]; var xx = new double[n + 1];
        var yy = new double[n + 1]; var xy = new double[n + 1];
        for (int i = 0; i < n; i++)
        {
            var p = observations[i]; sx[i + 1] = sx[i] + p.X; sy[i + 1] = sy[i] + p.Y;
            xx[i + 1] = xx[i] + p.X * p.X; yy[i + 1] = yy[i] + p.Y * p.Y; xy[i + 1] = xy[i] + p.X * p.Y;
        }
        Fit Line(int a, int b)
        {
            int count = b - a; double x = (sx[b] - sx[a]) / count, y = (sy[b] - sy[a]) / count;
            double vx = Math.Max(0, xx[b] - xx[a] - count * x * x), vy = Math.Max(0, yy[b] - yy[a] - count * y * y);
            double cov = xy[b] - xy[a] - count * x * y;
            double angle = .5 * Math.Atan2(2 * cov, vx - vy), dx = Math.Cos(angle), dy = Math.Sin(angle);
            if (dx * (observations[b - 1].X - observations[a].X) + dy * (observations[b - 1].Y - observations[a].Y) < 0)
            { dx = -dx; dy = -dy; }
            double error = Math.Max(0, (vx + vy - Math.Sqrt((vx - vy) * (vx - vy) + 4 * cov * cov)) / 2);
            return new(x, y, dx, dy, error);
        }
        double Length(int a, int b) => Math.Sqrt(Math.Pow(observations[b - 1].X - observations[a].X, 2) + Math.Pow(observations[b - 1].Y - observations[a].Y, 2));
        double Duration(int a, int b) => (observations[b - 1].Time - observations[a].Time).TotalSeconds;
        var whole = Line(0, n);
        int best = -1; double bestError = whole.Error * MaximumResidualRatio;
        double confirmationSeconds = Math.Max(MinimumConfirmationSeconds, options.TurnConfirmationSeconds);
        double sideSpan = Math.Max(MinimumSideSpanM, options.MinimumSpanM / 2);
        for (int split = 2; split < n - 2; split++)
        {
            if (Duration(0, split) < confirmationSeconds || Duration(split, n) < confirmationSeconds ||
                Length(0, split) < sideSpan || Length(split, n) < sideSpan ||
                !(Length(0, split) >= options.MinimumSpanM && Duration(0, split) >= options.MinimumDurationSeconds ||
                  Length(split, n) >= options.MinimumSpanM && Duration(split, n) >= options.MinimumDurationSeconds)) continue;
            var left = Line(0, split); var right = Line(split, n);
            // Use directed headings: a reversal never becomes an apparent zero-degree change.
            double angle = Math.Acos(Math.Clamp(left.Dx * right.Dx + left.Dy * right.Dy, -1, 1)) * 180 / Math.PI;
            if (angle < Math.Max(MinimumDirectionChangeDegrees, options.RepeatAxisToleranceDegrees)) continue;
            double error = left.Error + right.Error;
            if (error < bestError) { best = split; bestError = error; }
        }
        if (best < 0) { yield return new(first, last, confirmed, refined); yield break; }
        var before = Line(0, best); var after = Line(best, n);
        double localSeconds = Math.Max(confirmationSeconds, matching.ObservationSeconds);
        int localFirst = best - 1, localLast = best + 1;
        while (localFirst > 0 && (observations[best - 1].Time - observations[localFirst - 1].Time).TotalSeconds <= localSeconds) localFirst--;
        while (localLast < n && (observations[localLast].Time - observations[best].Time).TotalSeconds <= localSeconds) localLast++;
        var localBefore = Line(localFirst, best); var localAfter = Line(best, localLast);
        double localAngle = Math.Acos(Math.Clamp(localBefore.Dx * localAfter.Dx + localBefore.Dy * localAfter.Dy, -1, 1)) * 180 / Math.PI;
        if (best - localFirst < 3 || localLast - best < 3 || localAngle < Math.Max(MinimumDirectionChangeDegrees, options.RepeatAxisToleranceDegrees))
        { yield return new(first, last, confirmed, refined); yield break; }
        bool Stable(Fit fit, int a, int b) => Enumerable.Range(a, b - a).Count(i => fit.Distance(observations[i].X, observations[i].Y) <= matching.CoreM) >= (b - a) * options.MinimumAlignedRatio;
        // Both sides must have a straight body. Gradual bows and noisy wind drift remain quality observations.
        bool departure = false;
        var body = Length(0, best) >= Length(best, n) ? before : after;
        int otherFirst = Length(0, best) >= Length(best, n) ? best : 0;
        int otherLast = otherFirst == 0 ? best : n;
        DateTimeOffset? outside = null;
        for (int i = otherFirst; i < otherLast; i++)
        {
            if (body.Distance(observations[i].X, observations[i].Y) > matching.CorridorM)
            {
                outside ??= observations[i].Time;
                if ((observations[i].Time - outside.Value).TotalSeconds >= confirmationSeconds) departure = true;
            }
            else outside = null;
        }
        if (!departure || !Stable(before, 0, best) || !Stable(after, best, n))
        { yield return new(first, last, confirmed, refined); yield break; }

        // Backtrack to the intersection of the two fitted bodies, then use the closest original position.
        double determinant = before.Dx * after.Dy - before.Dy * after.Dx;
        double t = ((after.X - before.X) * after.Dy - (after.Y - before.Y) * after.Dx) / determinant;
        double junctionX = before.X + t * before.Dx, junctionY = before.Y + t * before.Dy;
        int cut = observations[best].First; double distance = double.MaxValue;
        var candidateTime = observations[best].Time;
        double searchSeconds = Math.Max(MinimumConfirmationSeconds, matching.ObservationSeconds);
        int searchFirst = observations.FindIndex(p => p.Time >= candidateTime.AddSeconds(-searchSeconds));
        int searchLast = observations.FindLastIndex(p => p.Time <= candidateTime.AddSeconds(searchSeconds));
        for (int i = observations[searchFirst].First; i <= observations[searchLast].Last; i++)
        {
            if (i <= first || i >= last) continue;
            var (x, y) = EndpointLineRecognizer.Delta(origin, points[i].Coordinate);
            double d = Math.Pow(x - junctionX, 2) + Math.Pow(y - junctionY, 2);
            if (d < distance) { distance = d; cut = i; }
        }
        int confirmation = cut;
        while (confirmation < last && (points[confirmation].TimestampUtc - points[cut].TimestampUtc).TotalSeconds < confirmationSeconds) confirmation++;
        foreach (var span in Refine(points, first, cut - 1, confirmation, options, matching, depth + 1, true)) yield return span;
        foreach (var span in Refine(points, cut, last, confirmed, options, matching, depth + 1, true)) yield return span;
    }
}
