namespace MagOps.Infrastructure.Services;

/// <summary>Geometry-only straight-flight proposals. Never creates a task ID or production pass.</summary>
internal static class AnonymousTrackRecognizer
{
    internal sealed record Span(int Start, int End, bool Straight);

    internal static IReadOnlyList<Span> Find(List<SurveyTrackPoint> points, WholeLegMatchOptions options)
    {
        if (points.Count < 3) return points.Count == 0 ? [] : [new(0, points.Count - 1, false)];
        var headings = new double?[points.Count];
        var observations = TrackMotionService.Measure(points).ToDictionary(o => (o.Source, o.Time.ToUnixTimeSeconds()));
        for (int i = 0, right = 0; i < points.Count; i++)
        {
            right = Math.Max(i, right);
            while (right + 1 < points.Count && EndpointLineRecognizer.Seconds(points[i], points[right + 1]) <= 1 &&
                EndpointLineRecognizer.Seconds(points[right], points[right + 1]) <= options.MaximumEvidenceGapSeconds) right++;
            int last = right == i ? Math.Min(i + 1, points.Count - 1) : right;
            headings[i] = SurveyDirection.Bearing(points[i].Coordinate, points[last].Coordinate);
            if (!observations.TryGetValue((points[i].SourceFile, points[i].TimestampUtc.ToUnixTimeSeconds()), out var motion) ||
                motion.SpeedMps is < 1.5 || motion.SpeedMps < options.VerticalMotionHorizontalSpeedMps &&
                motion.ClimbMps is { } climb && Math.Abs(climb) > options.VerticalSpeedReviewMps)
                headings[i] = null;
        }
        var output = new List<Span>();
        int start = 0, turn = -1;
        double? fixedHeading = null;
        for (int i = 0; i < points.Count; i++)
        {
            if (headings[i] is null) turn = -1;
            fixedHeading ??= headings[i];
            double gap = i == 0 ? 0 : EndpointLineRecognizer.Seconds(points[i - 1], points[i]);
            if (gap > options.MaximumTimeGapSeconds || gap < 0 && i > 0)
            { Add(start, i - 1); start = i; fixedHeading = headings[i]; turn = -1; continue; }
            if (gap > options.MaximumEvidenceGapSeconds) turn = -1;
            if (headings[i] is { } heading && fixedHeading is { } basis &&
                SurveyDirection.Difference(heading, basis) > options.DirectionBreakDegrees)
            {
                if (turn < 0) turn = i;
                if (EndpointLineRecognizer.Seconds(points[turn], points[i]) >= options.DirectionConfirmationSeconds)
                { Add(start, turn - 1); start = turn; fixedHeading = headings[i]; turn = -1; }
            }
            else turn = -1;
        }
        Add(start, points.Count - 1);
        return output;

        void Add(int first, int last)
        {
            if (last < first) return;
            var (x, y) = EndpointLineRecognizer.Delta(points[first].Coordinate, points[last].Coordinate);
            double distance = Math.Sqrt(x * x + y * y);
            double? direction = SurveyDirection.Bearing(points[first].Coordinate, points[last].Coordinate);
            int count = 0, aligned = 0;
            for (int j = first; j <= last; j++)
                if (headings[j] is { } h && direction is { } d)
                { count++; if (SurveyDirection.Difference(h, d) <= options.HeadingToleranceDegrees) aligned++; }
            bool straight = distance >= 15 && EndpointLineRecognizer.Seconds(points[first], points[last]) >= 2 &&
                count >= 3 && (double)aligned / count >= .80;
            output.Add(new(first, last, straight));
        }
    }
}

public static class SurveyDirection
{
    /// <summary>North=0, east=90. Negligible displacement provides no reliable direction.</summary>
    public static double? Bearing(GeoCoordinate start, GeoCoordinate end)
    {
        var (x, y) = EndpointLineRecognizer.Delta(start, end);
        return x * x + y * y < 1 ? null : (Math.Atan2(x, y) * 180 / Math.PI + 360) % 360;
    }

    public static double Difference(double a, double b)
    {
        double difference = Math.Abs((a - b) % 360);
        return Math.Min(difference, 360 - difference);
    }
}
