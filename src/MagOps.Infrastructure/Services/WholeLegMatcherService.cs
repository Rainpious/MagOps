using MagOps.Core.Domain;

namespace MagOps.Infrastructure.Services;

public sealed record MissionTimeWindow(string MissionExecutionId, string PlannedSortieName,
    DateTimeOffset StartUtc, DateTimeOffset EndUtc, string PhysicalFlightId = "")
{
    /// <summary>Machine-authoritative task membership. Sortie name is display/legacy compatibility only.</summary>
    public IReadOnlyList<string> SurveyTaskStableIds { get; init; } = [];
    public IReadOnlyList<string> SourceFiles { get; init; } = [];
    public string TaskScopeSource { get; init; } = "MANUAL";
}


public sealed record WholeLegMatchOptions(double MaximumTimeGapSeconds = 8d,
    // Legacy serialized field: retained for compatibility; current recognizer does not read TurnAngleDegrees.
    double TurnAngleDegrees = 45d, double HeadingToleranceDegrees = 25d,
    double MinimumMonotonicity = 0.80d, double MinimumLegCoverage = 0.02d)
{
    public bool EnableGeometryRecovery { get; init; } = true;
    public double GeometryRecoverySearchM { get; init; } = 50;
    public double GeometryRecoveryMinimumSpanM { get; init; } = 60;
    public double GeometryRecoveryMaximumAxisDegrees { get; init; } = 10;
    public double GeometryRecoveryMinimumFiniteRatio { get; init; } = .9;
    public double GeometryRecoveryCompetitionGapM { get; init; } = 6;
    public double EndpointSearchM { get; init; } = 5;
    public double CoreM { get; init; } = 3;
    public double CorridorM { get; init; } = 8;
    /// <summary>Candidate discovery only; this does not relax flight-quality or production rules.</summary>
    /// <summary>Broad candidate discovery only; identity and QC tolerances remain 5/3/8 m.</summary>
    public double CandidateSearchM { get; init; } = 16;
    public double BodyHeadingToleranceDegrees { get; init; } = 10;
    public double MinimumBodySpanM { get; init; } = 60;
    public double VerticalSpeedReviewMps { get; init; } = 2;
    public double DepartureDirectionChangeDegrees { get; init; } = 10;
    public double DirectionBreakDegrees { get; init; } = 60;
    public double DirectionConfirmationSeconds { get; init; } = 2;
    public double ObservationSeconds { get; init; } = 5;
    public double ReturnSeconds { get; init; } = 1;
    public double DepartureTravelM { get; init; } = 40;
    public double MaximumEvidenceGapSeconds { get; init; } = 1;
    public double MinimumFlightSpeedMps { get; init; } = 4;
    public double MaximumFlightSpeedMps { get; init; } = 12;
    /// <summary>Recognition scales are independent of quality speed bounds.</summary>
    public double VerticalMotionHorizontalSpeedMps { get; init; } = 4;
    public double TerminalSearchSpeedMps { get; init; } = 12;
    public double DepartureAuxiliaryMinimumSpeedMps { get; init; } = 4;
    public double DepartureAuxiliaryMaximumSpeedMps { get; init; } = 12;
    // Local terminal refinement only; the global 60-degree break rule is unchanged.
    public double TerminalTurnDegrees { get; init; } = 15;
    public double TerminalStableSeconds { get; init; } = 2;
    public double TerminalStationaryRadiusM { get; init; } = 2;
    // Default confirmed by the regression cases: use a five-second
    // minimum before treating a terminal stationary cluster as evidence. The
    // radius remains independent from the 8 m survey corridor.
    public double TerminalStationarySeconds { get; init; } = 5;
    public double TerminalVerticalChangeM { get; init; } = 5;
}

/// <summary>
/// Mission windows are explicit inputs. A source file is provenance, not a
/// mission boundary. This engine has no production authority until reviewed.
/// </summary>
public sealed class WholeLegMatcherService
{
    public const string AlgorithmVersion = "spatial-body-endpoint-13-whole-track-recovery";

    public SurveyExecutionResult Match(IReadOnlyList<PlannedSurveyLineTask> planned,
        IEnumerable<SurveyTrackPoint> source, IReadOnlyList<MissionTimeWindow> windows,
        WholeLegMatchOptions? options = null)
    {
        options ??= new WholeLegMatchOptions();
        var orderedWindows = windows.OrderBy(x => x.StartUtc).ToList();
        var knownTaskIds = planned.Where(t => !string.IsNullOrWhiteSpace(t.SurveyTaskStableId))
            .Select(t => t.SurveyTaskStableId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < orderedWindows.Count; i++)
        {
            if (orderedWindows[i].EndUtc <= orderedWindows[i].StartUtc)
                throw new ArgumentException("Mission time window must have a positive duration.");
            if (orderedWindows.Take(i).Any(previous => orderedWindows[i].StartUtc <= previous.EndUtc &&
                (previous.SourceFiles.Count == 0 || orderedWindows[i].SourceFiles.Count == 0 ||
                 previous.SourceFiles.Intersect(orderedWindows[i].SourceFiles, StringComparer.OrdinalIgnoreCase).Any())))
                throw new ArgumentException("同一数据来源的飞行时间窗不能重叠。");
            string[] taskIds = orderedWindows[i].SurveyTaskStableIds
                .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            string[] unknown = taskIds.Where(x => !knownTaskIds.Contains(x)).ToArray();
            if (unknown.Length > 0)
                throw new InvalidDataException($"Mission {orderedWindows[i].MissionExecutionId} 引用未知 SurveyTaskStableId：{string.Join(", ", unknown)}。");
        }
        var points = source.Where(x => double.IsFinite(x.Coordinate.Longitude) &&
            double.IsFinite(x.Coordinate.Latitude)).OrderBy(x => x.TimestampUtc)
            .ThenBy(x => x.SourceFile, StringComparer.Ordinal)
            .ThenBy(x => x.SourceRecord).ToList();
        var legs = new List<FlightLeg>();
        var passes = new List<SurveyLinePass>();
        var warnings = new List<string>();
        var missions = orderedWindows.Select(x => new MissionExecution(
            x.MissionExecutionId, x.PlannedSortieName, x.PhysicalFlightId,
            x.StartUtc, x.EndUtc,
            x.PhysicalFlightId.Length == 0 ? "REVIEW_REQUIRED" : "CONFIRMED")
        { SurveyTaskStableIds = x.SurveyTaskStableIds.ToArray() }).ToList();
        var flights = orderedWindows.Where(x => x.PhysicalFlightId.Length > 0)
            .GroupBy(x => x.PhysicalFlightId, StringComparer.Ordinal)
            .Select(g => new PhysicalFlight(g.Key, g.Min(x => x.StartUtc), g.Max(x => x.EndUtc),
                "CONFIRMED", points.Where(p => g.Any(w => p.TimestampUtc >= w.StartUtc &&
                    p.TimestampUtc <= w.EndUtc)).Select(p => p.SourceFile)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray())).ToList();
        if (orderedWindows.Count == 0 && points.Count > 0)
            warnings.Add("没有确认的 Mission 时间窗；轨迹保留为 UNRESOLVED。");
        var assigned = new HashSet<SurveyTrackPoint>();
        foreach (MissionTimeWindow window in orderedWindows)
        {
            var missionPoints = points.Where(p => p.TimestampUtc >= window.StartUtc &&
                p.TimestampUtc <= window.EndUtc && (window.SourceFiles.Count == 0 ||
                    window.SourceFiles.Contains(p.SourceFile, StringComparer.OrdinalIgnoreCase))).ToList();
            foreach (SurveyTrackPoint point in missionPoints) assigned.Add(point);
            var authoritativeTaskIds = window.SurveyTaskStableIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var candidates = planned.Where(t => authoritativeTaskIds.Contains(t.SurveyTaskStableId)).ToList();
            int index = 0;
            foreach (var continuous in Split(missionPoints, options))
            {
                var proposed = EndpointLineRecognizer.Find(continuous, candidates, options);
                var ambiguous = new List<EndpointLineRecognizer.Candidate>();
                for (int a = 0; a < proposed.Count; a++)
                for (int b = a + 1; b < proposed.Count; b++)
                {
                    var first = proposed[a]; var second = proposed[b];
                    int overlap = Math.Min(first.End, second.End) - Math.Max(first.Start, second.Start) + 1;
                    int shorter = Math.Min(first.End - first.Start + 1, second.End - second.Start + 1);
                    if (first.Task.SurveyTaskStableId != second.Task.SurveyTaskStableId &&
                        overlap >= Math.Max(3, shorter * 0.5) &&
                        (Math.Abs(first.Confidence - second.Confidence) < 0.08 ||
                         AdjacentParallelCompetition(first, second, options)))
                    {
                        int from = Math.Max(first.Start, second.Start), to = Math.Min(first.End, second.End);
                        ambiguous.Add(first with { Start = from, End = to });
                        ambiguous.Add(second with { Start = from, End = to });
                    }
                }
                var selected = new List<EndpointLineRecognizer.Candidate>();
                foreach (var candidate in proposed
                    .OrderByDescending(p => p.Confidence).ThenBy(p => p.Task.Order))
                {
                    // Remove only actual conflicting/occupied intervals. A short
                    // co-linear endpoint overlap cannot invalidate a kilometre of
                    // independently supported non-overlapping survey body.
                    int next = candidate.Start;
                    var blockedIntervals = ambiguous.Concat(selected).Where(p => p.Start <= candidate.End && p.End >= candidate.Start)
                        .OrderBy(p => p.Start).ThenBy(p => p.End).ToArray();
                    foreach (var blockedInterval in blockedIntervals)
                    {
                        AddAvailable(next, Math.Min(candidate.End, blockedInterval.Start - 1));
                        next = Math.Max(next, blockedInterval.End + 1);
                    }
                    AddAvailable(next, candidate.End);

                    void AddAvailable(int from, int to)
                    {
                        if (to - from < 2) return;
                        var clipped = EndpointLineRecognizer.Clip(candidate, continuous, from, to, options);
                        if (clipped is not null) selected.Add(clipped);
                    }
                }
                int cursor = 0;
                foreach (var candidate in selected.OrderBy(p => p.Start).ThenBy(p => p.End))
                {
                    int start = Math.Max(cursor, candidate.Start);
                    if (candidate.End - start < 2) continue;
                    AddRemainder(cursor, start - 1);
                    var segment = continuous.GetRange(start, candidate.End - start + 1);
                    string legId = window.MissionExecutionId + "/leg-" + (++index).ToString("D4");
                    string review = window.PhysicalFlightId.Length == 0 ? "PHYSICAL_FLIGHT_BOUNDARY_UNRESOLVED" : "";
                    var leg = new FlightLeg(legId, window.MissionExecutionId,
                        segment[0].TimestampUtc, segment[^1].TimestampUtc, "SURVEY",
                        candidate.Task.SurveyTaskStableId, segment.Count, review)
                    { Samples = ToLegSamples(segment) };
                    var pass = CreateManualAssignmentPass(leg, candidate.Task, options);
                    if (pass is null) continue;
                    legs.Add(leg);
                    passes.Add(pass with
                    {
                        MatchConfidence = candidate.Confidence,
                        MatchMethod = candidate.Method,
                        ExecutionDirection = candidate.Direction > 0 ? "FORWARD" : "REVERSE",
                        StartBoundary = Boundary(candidate.StartKind, start, start),
                        EndBoundary = Boundary(candidate.EndKind, candidate.End, candidate.Confirmed),
                        FlightQuality = candidate.Quality
                    });
                    cursor = candidate.End + 1;
                }
                AddRemainder(cursor, continuous.Count - 1);

                ExecutionBoundaryEvidence Boundary(string kind, int cut, int confirmed) =>
                    new(kind, continuous[cut].TimestampUtc, continuous[confirmed].TimestampUtc,
                        continuous[cut].SourceFile, continuous[cut].SourceRecord);

                void AddRemainder(int start, int end)
                {
                    if (end < start) return;
                    var segment = continuous.GetRange(start, end - start + 1);
                    foreach (var span in AnonymousTrackRecognizer.Find(segment, options))
                    {
                        int first = start + span.Start, last = start + span.End;
                        // Preserve competition only where it exists, rather than
                        // tagging an entire straight flight by a short overlap.
                        var cuts = new[] { first, last + 1 }.Concat(ambiguous
                            .Where(p => p.Start <= last && p.End >= first)
                            .SelectMany(p => new[] { Math.Max(first, p.Start), Math.Min(last + 1, p.End + 1) }))
                            .Distinct().Order().ToArray();
                        for (int c = 1; c < cuts.Length; c++)
                        {
                            int from = cuts[c - 1], to = cuts[c] - 1;
                            var conflicts = ambiguous.Where(p => p.Start <= to && p.End >= from)
                                .Select(p => p.Task.SurveyTaskStableId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                            var samples = continuous.GetRange(from, to - from + 1);
                            legs.Add(new FlightLeg(window.MissionExecutionId + "/leg-" + (++index).ToString("D4"),
                                window.MissionExecutionId, samples[0].TimestampUtc, samples[^1].TimestampUtc,
                                conflicts.Length > 0 ? "UNRESOLVED" : span.Straight ? "SURVEY_CANDIDATE" : "UNRESOLVED",
                                null, samples.Count, conflicts.Length > 0 ? "AMBIGUOUS_LINE" :
                                    span.Straight ? "ANONYMOUS_STRAIGHT_TRACK" : "NO_ENDPOINT_OR_TRACK_MATCH")
                            { Samples = ToLegSamples(samples), CandidateTaskStableIds = conflicts });
                        }
                    }
                }
            }
        }
        int unresolvedIndex = 0;
        foreach (var segment in Split(points.Where(p => !assigned.Contains(p)).ToList(), options))
            legs.Add(new FlightLeg("unresolved/leg-" + (++unresolvedIndex).ToString("D4"),
                "", segment[0].TimestampUtc, segment[^1].TimestampUtc,
                "UNRESOLVED", null, segment.Count, "NO_MISSION_WINDOW")
            { Samples = ToLegSamples(segment) });
        return WholeTrackGeometryRecoveryService.Apply(
            new SurveyExecutionResult(AlgorithmVersion, flights, missions, legs, passes, warnings), planned, orderedWindows, options);
    }

    /// <summary>
    /// Treat a stable track near the midpoint of two parallel planned lines as
    /// a competition instead of selecting the nearest line. This is important
    /// for a 20 m design spacing: a 10–12 m wind offset can be closer to the
    /// neighbouring line while still belonging to the original task. Endpoint
    /// pairs remain the stronger anchor and are allowed to win over a weak body
    /// proposal; two close endpoint proposals are still handled by confidence
    /// and the normal ambiguity path.
    /// </summary>
    private static bool AdjacentParallelCompetition(
        EndpointLineRecognizer.Candidate first,
        EndpointLineRecognizer.Candidate second,
        WholeLegMatchOptions options)
    {
        if (first.Method == "ENDPOINT_PAIR" || second.Method == "ENDPOINT_PAIR") return false;
        // A genuine endpoint anchor outranks a body-only neighbour. Two
        // single-endpoint proposals still compete and are handled below.
        if (first.Method == "SINGLE_ENDPOINT" && second.Method == "BODY_TRACK" ||
            second.Method == "SINGLE_ENDPOINT" && first.Method == "BODY_TRACK") return false;
        var (ax, ay) = EndpointLineRecognizer.Delta(first.Task.Start, first.Task.End);
        var (bx, by) = EndpointLineRecognizer.Delta(second.Task.Start, second.Task.End);
        double aLength = Math.Sqrt(ax * ax + ay * ay);
        double bLength = Math.Sqrt(bx * bx + by * by);
        if (aLength < 0.5 || bLength < 0.5) return false;
        double axisAlignment = Math.Abs((ax * bx + ay * by) / (aLength * bLength));
        double axisAngle = Math.Acos(Math.Clamp(axisAlignment, -1d, 1d)) * 180d / Math.PI;
        if (axisAngle > 20d) return false;

        double separation = Math.Abs(SignedCross(first.Task.Start, second.Task.Start, ax, ay, aLength));
        if (separation < 0.5 || separation > 2d * (options.CandidateSearchM + options.CorridorM)) return false;

        double firstOffset = Math.Abs(first.Quality.MedianSignedCrossTrackM);
        double secondOffset = Math.Abs(second.Quality.MedianSignedCrossTrackM);
        double residualLimit = options.CandidateSearchM + options.CorridorM;
        if (firstOffset > residualLimit || secondOffset > residualLimit) return false;

        // In the midpoint/near-midpoint band, distances to the two parallel
        // tasks add up to their separation. This uses line spacing geometry,
        // not a nearest-line winner, and remains tolerant of a few metres of
        // wind variation and endpoint quantisation.
        double midpointError = Math.Abs(firstOffset + secondOffset - separation);
        return midpointError <= Math.Max(2d, separation * 0.30d);
    }

    private static double SignedCross(
        GeoCoordinate origin, GeoCoordinate point,
        double axisX, double axisY, double axisLength) 
    {
        var (x, y) = EndpointLineRecognizer.Delta(origin, point);
        return (x * axisY - y * axisX) / axisLength;
    }

    /// <summary>Builds measurable along-line evidence for an operator-assigned leg.</summary>
    public static SurveyLinePass? CreateManualAssignmentPass(FlightLeg leg, PlannedSurveyLineTask task,
        WholeLegMatchOptions? options = null)
    {
        options ??= new WholeLegMatchOptions();
        FlightLegSample[] samples = leg.Samples.Where(p =>
            double.IsFinite(p.Longitude) && double.IsFinite(p.Latitude)).ToArray();
        if (samples.Length < 2 || task.LengthM <= 0) return null;
        var (dx, dy) = Difference(task.Start, task.End);
        double length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 0.5d) return null;
        double ux = dx / length, uy = dy / length;
        var along = new List<double>(samples.Length);
        var cross = new List<double>(samples.Length);
        foreach (FlightLegSample sample in samples)
        {
            var (x, y) = Difference(task.Start, new GeoCoordinate(sample.Longitude, sample.Latitude));
            along.Add(x * ux + y * uy);
            cross.Add(Math.Abs(x * uy - y * ux));
        }
        var (legX, legY) = Difference(new GeoCoordinate(samples[0].Longitude, samples[0].Latitude),
            new GeoCoordinate(samples[^1].Longitude, samples[^1].Latitude));
        double legLength = Math.Sqrt(legX * legX + legY * legY);
        double heading = legLength < 0.5d ? 180d :
            Math.Acos(Math.Clamp(Math.Abs((legX * ux + legY * uy) / legLength), -1d, 1d)) * 180d / Math.PI;
        double sign = along[^1] >= along[0] ? 1d : -1d;
        int moving = 0, monotone = 0;
        for (int i = 1; i < along.Count; i++)
        {
            double delta = along[i] - along[i - 1];
            if (Math.Abs(delta) < 0.5d) continue;
            moving++;
            if (delta * sign > 0) monotone++;
        }
        cross.Sort();
        var coverage = ObservedTrackCoverageService.Calculate(samples, task, options);
        double first = Math.Clamp(along[0], 0d, length), last = Math.Clamp(along[^1], 0d, length);
        return new SurveyLinePass(leg.FlightLegId + "/pass", leg.FlightLegId,
            leg.MissionExecutionId, task.SurveyTaskStableId, coverage,
            cross[cross.Count / 2], cross[(int)Math.Ceiling(cross.Count * 0.95d) - 1],
            moving == 0 ? 0d : (double)monotone / moving,
            coverage.Sum(x => x.End - x.Start),
            sign > 0 ? first : length - first,
            sign > 0 ? length - last : last,
            heading, (leg.EndUtc - leg.StartUtc).TotalSeconds, 0d,
            "PENDING_QC", task.IsQualityRepeat, leg.EndUtc,
            samples.Select(x => x.SourceFile).Distinct(StringComparer.OrdinalIgnoreCase).ToArray())
        {
            Samples = samples.Select((p, i) => new ExecutionSampleReference(
                p.SourceFile, p.SourceRecord, Math.Clamp(along[i] / length, 0d, 1d))).ToArray()
        };
    }

    internal static IReadOnlyList<List<SurveyTrackPoint>> Split(
        List<SurveyTrackPoint> points, WholeLegMatchOptions options)
    {
        // Independent sources must not form one trajectory. Equal timestamps within a
        // source can be genuine magnetic samples recorded with second-resolution UTC.
        return points.GroupBy(p => p.SourceFile, StringComparer.OrdinalIgnoreCase)
            .SelectMany(stream => SplitSource(stream.ToList(), options)).ToArray();
    }

    private static IReadOnlyList<List<SurveyTrackPoint>> SplitSource(
        List<SurveyTrackPoint> points, WholeLegMatchOptions options)
    {
        var result = new List<List<SurveyTrackPoint>>();
        if (points.Count == 0) return result;
        var current = new List<SurveyTrackPoint> { points[0] };
        for (int i = 1; i < points.Count; i++)
        {
            double gap = (points[i].TimestampUtc - points[i - 1].TimestampUtc).TotalSeconds;
            if (gap > options.MaximumTimeGapSeconds || gap < 0)
            {
                result.Add(current);
                current = new List<SurveyTrackPoint>();
            }
            current.Add(points[i]);
        }
        result.Add(current);
        return result;
    }

    private static (double X, double Y) Difference(GeoCoordinate a, GeoCoordinate b)
    {
        double meanLat = (a.Latitude + b.Latitude) * Math.PI / 360d;
        return ((b.Longitude - a.Longitude) * 111_319.49d * Math.Cos(meanLat),
            (b.Latitude - a.Latitude) * 111_319.49d);
    }

    private static IReadOnlyList<FlightLegSample> ToLegSamples(IEnumerable<SurveyTrackPoint> points) =>
        points.Select(p => new FlightLegSample(p.SourceFile, p.SourceRecord, p.TimestampUtc,
            p.Coordinate.Longitude, p.Coordinate.Latitude, p.AltitudeM)).ToArray();

}
