using MagOps.Core.Domain;
using MagOps.Infrastructure.Services;

internal static class EndpointRecognitionChecks
{
    private const double Metre = 1 / 111319.49;
    private static readonly DateTimeOffset Start = new(2026, 10, 8, 0, 0, 0, TimeSpan.Zero);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static PlannedSurveyLineTask Task(string id, double start = 0, double end = 200, double y = 0) =>
        new(1, 1, id, "mission", id, new(start * Metre, y * Metre), new(end * Metre, y * Metre), end - start)
        { SurveyTaskStableId = id };
    private static SurveyTrackPoint Point(int i, double x, double y = 0, double? height = null) =>
        new(Start.AddSeconds(i * .2), new(x * Metre, y * Metre), "track", i) { AltitudeM = height };
    private static SurveyExecutionResult Match(PlannedSurveyLineTask[] tasks, SurveyTrackPoint[] points) =>
        new WholeLegMatcherService().Match(tasks, points,
            [new("mission", "", points[0].TimestampUtc, points[^1].TimestampUtc, "flight")
                { SurveyTaskStableIds = tasks.Select(t => t.SurveyTaskStableId).ToArray() }]);
    private static void Preserved(SurveyExecutionResult result, SurveyTrackPoint[] points)
    {
        var samples = result.Legs.SelectMany(l => l.Samples).ToArray();
        Check(samples.Length == points.Length, "recognition must retain every input sample once");
        Check(samples.Select(p => (p.SourceFile, p.SourceRecord)).Distinct().Count() == points.Length,
            "samples must not be duplicated between adjacent tasks");
        Check(result.Passes.All(p => p.QualityStatus == "PENDING_QC" && !p.ReviewConfirmed), "recognition cannot approve QC or production");
    }

    public static void Run()
    {
        TerminalStopChecks.Run();
        var complete = Enumerable.Range(0, 126).Select(i => Point(i, i * 1.6)).ToArray();
        var result = Match([Task("A")], complete);
        Check(result.Passes.Count == 1 && result.Passes[0].MatchMethod == "ENDPOINT_PAIR", "a continuous full line should form one endpoint pass");
        Preserved(result, complete);

        var reverse = Enumerable.Range(0, 126).Select(i => Point(i, 200 - i * 1.6)).ToArray();
        result = Match([Task("A")], reverse);
        Check(result.Passes.Single().ExecutionDirection == "REVERSE", "reverse execution must be accepted");
        Preserved(result, reverse);

        // Local errors including gross cross-track outliers are QC evidence;
        // endpoints and sustained progress still establish the task identity.
        var deviations = complete.Select((p, i) => p with { Coordinate =
            new(p.Coordinate.Longitude, (i >= 30 && i <= 65 ? 12 : 0) * Metre) }).ToArray();
        result = Match([Task("A")], deviations);
        Check(result.Passes.Count == 1 && result.Passes[0].FlightQuality!.OutsideCorridorPoints > 0,
            "sustained parallel cross-track errors must not discard an endpoint-supported identity");
        Preserved(result, deviations);

        var briefDeviation = complete.Select((p, i) => p with { Coordinate =
            new(p.Coordinate.Longitude, (i >= 40 && i < 45 ? 14 : 0) * Metre) }).ToArray();
        result = Match([Task("A")], briefDeviation);
        Check(result.Passes.Count == 1 && result.Passes.Single().FlightQuality!.OutsideCorridorPoints == 5,
            "five-point deviation returning inside 8 m stays in one execution and keeps every anomaly");
        Preserved(result, briefDeviation);

        var curved = Enumerable.Range(0, 119).Select(i => Point(i, i * 1.6))
            .Concat(Enumerable.Range(119, 24).Select(i => Point(i, 190 + (i - 119) * .2, (i - 119) * 1.6))).ToArray();
        result = Match([Task("A")], curved);
        Check(result.Passes.Count == 1 && result.Passes.Single().MatchMethod == "SINGLE_ENDPOINT" &&
            result.Passes.Single().EndBoundary!.Kind is "DIRECTION_BREAK" or "TERMINAL_TURN" &&
            Math.Abs(result.Passes.Single().Samples.Last().SourceRecord - 118) <= 2,
            "a curved departure missing the far endpoint preserves single-endpoint identity and cuts at the turn");
        Preserved(result, curved);

        var collinear = Enumerable.Range(0, 206).Select(i => Point(i, i * 2)).ToArray();
        result = Match([Task("A"), Task("B", 210, 410)], collinear);
        Check(result.Passes.Count == 2 && result.Passes.All(p => p.MatchMethod == "ENDPOINT_PAIR"), "continuous collinear tasks need separate endpoint passes");
        Check(result.Passes[0].Samples.Last().SourceRecord == 100 && result.Passes[1].Samples.First().SourceRecord == 105,
            "task boundaries must use the closest observed endpoints");
        Preserved(result, collinear);

        var turning = Enumerable.Range(0, 51).Select(i => Point(i, i * 1.6))
            .Concat(Enumerable.Range(1, 51).Select(i => Point(50 + i, 80 - i * 1.6))).ToArray();
        result = Match([Task("A")], turning);
        Check(result.Passes.Count == 2, "a 180 degree reversal must create two executions on the same geometric line");
        var cut = result.Passes[0].EndBoundary!;
        Check(cut.Kind == "DIRECTION_BREAK" && cut.ConfirmedUtc > cut.CutUtc,
            "confirmation time and backtracked cut time must remain separate");
        Check(Math.Abs((cut.CutUtc - Start).TotalSeconds - 10) <= 1, "turn cut must return to the actual reversal neighborhood");
        Preserved(result, turning);

        var partial = Enumerable.Range(0, 51).Select(i => Point(i, 50 + i * 1.6)).ToArray();
        result = Match([Task("A")], partial);
        Check(result.Passes.Single().MatchMethod == "BODY_TRACK", "unanchored but clear partial execution must survive");
        Preserved(result, partial);

        var stationary = Enumerable.Range(0, 80).Select(i => Point(i, 100 + Math.Sin(i) * .1)).ToArray();
        result = Match([Task("A")], stationary);
        Check(result.Passes.Count == 0, "stationary jitter must not create a survey task");
        Preserved(result, stationary);

        result = Match([Task("A"), Task("B")], complete);
        Check(result.Passes.Count == 0 && result.Legs.Any(l => l.ReviewReason == "AMBIGUOUS_LINE"),
            "identical tasks cannot be assigned silently by list order");
        Preserved(result, complete);

        // Fixed speed limits are auxiliary; fast but otherwise certain passes survive.
        var fast = Enumerable.Range(0, 51).Select(i => Point(i, i * 4)).ToArray();
        result = Match([Task("A")], fast);
        Check(result.Passes.Single().FlightQuality!.SpeedOutsideRatio > .9, "speed anomalies need QC evidence, not identity removal");
        Preserved(result, fast);

        var gap = complete.Select((p, i) => i >= 63 ? p with { TimestampUtc = p.TimestampUtc.AddSeconds(20) } : p).ToArray();
        result = Match([Task("A")], gap);
        Check(result.Passes.Count == 2 && result.Passes.All(p => p.MatchMethod != "ENDPOINT_PAIR"), "sampling gaps cannot be joined into an endpoint pair");
        Preserved(result, gap);

        var crossing = Enumerable.Range(0, 126).Select(i => Point(i, 100, -100 + i * 1.6)).ToArray();
        result = Match([Task("A")], crossing);
        Check(result.Passes.Count == 0, "a crossing transit must not become a task by proximity");
        Preserved(result, crossing);

        var offset = complete.Select(p => p with { Coordinate = new(p.Coordinate.Longitude, 10 * Metre) }).ToArray();
        result = Match([Task("A")], offset);
        Check(result.Passes.Count == 1 && result.Passes[0].MatchMethod == "BODY_TRACK" &&
            result.Passes[0].FlightQuality!.CorridorPointRatio == 0 &&
            result.Passes[0].FlightQuality!.Warnings.Contains("SYSTEMATIC_LATERAL_OFFSET_REVIEW"),
            "stable offset bodies can propose identity while retaining directional lateral-offset quality evidence");
        Preserved(result, offset);

        result = Match([Task("A"), Task("B", y: 20)], offset);
        Check(result.Passes.Count == 0 && result.Legs.Any(l => l.CandidateTaskStableIds.Count == 2),
            "a broad search must retain competing parallel tasks instead of choosing the nearest line");
        Preserved(result, offset);

        var windOffset = complete.Select(p => p with { Coordinate = new(p.Coordinate.Longitude, 12 * Metre) }).ToArray();
        result = Match([Task("A"), Task("B", y: 20)], windOffset);
        Check(result.Passes.Count == 0 && result.Legs.Any(l => l.CandidateTaskStableIds.Count == 2),
            "a 20 m line-spacing midpoint offset must remain competing even when one line is nearer");
        Preserved(result, windOffset);

        var shallow = Enumerable.Range(0, 126).Select(i => Point(i, 100 + (i - 63) * 1.6,
            (i - 63) * 1.6 * Math.Tan(20 * Math.PI / 180))).ToArray();
        result = Match([Task("A")], shallow);
        Check(result.Passes.Count == 0, "a shallow crossing connector must not be identified by corridor proximity");
        Preserved(result, shallow);

        var crossingThenSurvey = Enumerable.Range(0, 31).Select(i => Point(i, 40 + i * 1.6,
                (i - 15) * 1.6 * Math.Tan(20 * Math.PI / 180)))
            .Concat(Enumerable.Range(0, 126).Select(i => Point(31 + i, i * 1.6))).ToArray();
        result = Match([Task("A")], crossingThenSurvey);
        Check(result.Passes.Any(p => p.Samples.Count >= 100), "a rejected early crossing cannot consume a later clear survey execution");
        Preserved(result, crossingThenSurvey);

        foreach (double speed in new[] { 4d, 12d })
        {
            var limit = Enumerable.Range(0, 101).Select(i => new SurveyTrackPoint(Start.AddSeconds(i),
                new(i * speed * Metre, 0), "track", i)).ToArray();
            result = Match([Task("A", end: speed * 100)], limit);
            Check(result.Passes.Single().FlightQuality!.SpeedOutsideRatio == 0,
                "exactly 4 and 12 m/s are inside the auxiliary speed range");
            Preserved(result, limit);
        }

        var noRoute = new WholeLegMatcherService().Match([], complete,
            [new("mission", "", complete[0].TimestampUtc, complete[^1].TimestampUtc)]);
        Check(noRoute.Passes.Count == 0 && noRoute.Legs.Any(l => l.Classification == "SURVEY_CANDIDATE") &&
            noRoute.Legs.All(l => l.SurveyTaskStableId is null), "no-design recognition must propose anonymous segments without inventing identities");
        Preserved(noRoute, complete);

        noRoute = new WholeLegMatcherService().Match([], turning,
            [new("mission", "", turning[0].TimestampUtc, turning[^1].TimestampUtc)]);
        Check(noRoute.Legs.Count(l => l.Classification == "SURVEY_CANDIDATE") == 2,
            "anonymous recognition also splits a directed 180 degree reversal");
        Preserved(noRoute, turning);

        Check(SurveyDirection.Difference(90, 270) == 180 && SurveyDirection.Bearing(new(0,0), new(0,0)) is null,
            "direction display must preserve reversal and cannot infer direction from a stationary point");

        TerminalRefinement();
    }

    private static void TerminalRefinement()
    {
        foreach (double degrees in new[] { 30d, 59.9d })
        foreach (int direction in new[] { 1, -1 })
        {
            double radians = degrees * Math.PI / 180;
            double x0 = direction > 0 ? 0 : 200;
            // Offset straight flight turns toward the design endpoint: the nearest
            // endpoint is already inside the connector and must not extend the cut.
            var track = Enumerable.Range(0, 119).Select(i => Point(i, x0 + direction * i * 1.6, -12))
                .Concat(Enumerable.Range(1, 30).Select(i => Point(118 + i,
                    x0 + direction * (188.8 + i * 1.6 * Math.Cos(radians)),
                    -12 + i * 1.6 * Math.Sin(radians)))).ToArray();
            var result = Match([Task("tail")], track);
            var pass = result.Passes.First(p => p.ExecutionDirection == (direction > 0 ? "FORWARD" : "REVERSE"));
            Check(pass.EndBoundary!.Kind == "TERMINAL_TURN", "shallow terminal connector must be refined independently of global 60 degree rule");
            Check(Math.Abs(pass.Samples.Last().SourceRecord - 118) <= 1, "terminal cut must return to the physical turn rather than its endpoint crossing");
            Check(pass.EndBoundary.ConfirmedUtc > pass.EndBoundary.CutUtc, "terminal confirmation must remain later than cut");
            Preserved(result, track);
        }

        var overshoot = Enumerable.Range(0, 141).Select(i => Point(i, i * 1.6, 12)).ToArray();
        var straightResult = Match([Task("tail")], overshoot);
        Check(straightResult.Passes.Single().Samples.Last().SourceRecord == 125,
            "unhit endpoint must cut at its projection instead of keeping the 5 m search extension");
        Preserved(straightResult, overshoot);

        var recovery = Enumerable.Range(0, 151).Select(i => Point(i, i * 1.6,
            i >= 108 && i <= 111 ? (i - 107) * 1.6 : i >= 112 && i <= 115 ? (116 - i) * 1.6 : 0)).ToArray();
        var recovered = Match([Task("tail")], recovery);
        Check(recovered.Passes.Single().Samples.Last().SourceRecord == 125 &&
            recovered.Passes.Single().Samples.Any(p => p.SourceRecord == 111),
            $"brief terminal excursion returning to the stable body must remain in the pass; kind={recovered.Passes.Single().EndBoundary?.Kind} end={recovered.Passes.Single().Samples.Last().SourceRecord} range={string.Join(',', recovered.Passes.SelectMany(p => p.Samples).Select(s => s.SourceRecord).Distinct().Order())}");
        Preserved(recovered, recovery);

        var shortData = Enumerable.Range(0, 116).Select(i => Point(i, i * 1.6))
            .Concat(Enumerable.Range(1, 4).Select(i => Point(115 + i, 184 + i, i))).ToArray();
        var shortResult = Match([Task("tail")], shortData);
        Check(shortResult.Passes.Single().EndBoundary!.Kind == "DATA_END_REVIEW" &&
            shortResult.Passes.Single().Samples.Last().SourceRecord == 119,
            "insufficient following observations must not manufacture a confirmed terminal turn");
        Preserved(shortResult, shortData);
    }
}
