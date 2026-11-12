using MagOps.Core.Domain;
using MagOps.Infrastructure.Services;

namespace MagOps.SmokeTests;

internal static class StationaryPurposeChecks
{
    public static void Run()
    {
        const double metre = 1 / 111319.49;
        var time = new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.Zero);
        GeoCoordinate P(double x, double y = 0) => new(x * metre, y * metre);
        void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        UavMagCanonicalSample Canonical(SurveyTrackPoint p) => new(p.TimestampUtc, p.Coordinate.Longitude, p.Coordinate.Latitude,
            p.AltitudeM, null, 50000 + p.SourceRecord * .01, null, "A", 12, "T", p.SourceFile, p.SourceRecord, []);
        var window = new MissionTimeWindow("mission", "", time, time.AddSeconds(600), "flight") { TaskScopeSource = "AUTO" };
        var input = new TrackSegmentationInput { Enabled = true, AxisDegrees = 90 };
        var service = new TrackSegmentationService();
        var dwell = Enumerable.Range(0, 100).SelectMany(second => Enumerable.Range(0, 10).Select(j =>
            new SurveyTrackPoint(time.AddSeconds(second), P(.6 * Math.Cos(second * 1.7 + j), .6 * Math.Sin(second * 1.7 + j)),
                "hover.uav", second * 10 + j) { AltitudeM = 100 + Math.Clamp(second - 30, 0, 20) * 3 })).ToArray();
        var result = service.Recognize([], dwell, [window], input);
        Check(result.Execution.Legs.Count <= 3 && result.Execution.Passes.Count == 0,
            "one continuous metre-scale dwell/climb is not hundreds of flight turns or invented tasks");
        Check(result.Execution.Legs.Sum(l => l.Samples.Count) == dwell.Length &&
            result.Execution.Legs.SelectMany(l => l.Samples).Select(s => (s.SourceFile, s.SourceRecord)).Distinct().Count() == dwell.Length,
            "stationary consolidation preserves every magnetic source record");
        var annotated = TransitEvidenceService.Annotate(result.Execution, dwell.Select(Canonical).ToArray(),
            input with { KnownTakeoffPositions = [P(0)] }, null);
        Check(annotated.Legs.Any(l => l.MotionKind == "STATIONARY_VERTICAL") && annotated.Legs.All(l => l.TransitEvidence.Count == 0),
            "stationary climb at a known home is motion evidence, not a transit traversal");
        Check(annotated.Legs.Any(l => l.ContextEvidence.Count > 0), "neighbourhood evidence remains visible for review");

        var gap = dwell.Take(300).Concat(dwell.Skip(300).Take(300).Select(p => p with { TimestampUtc = p.TimestampUtc.AddSeconds(10) })).ToArray();
        var separated = service.Recognize([], gap, [window], input);
        Check(separated.Execution.Legs.Count >= 2 && separated.Execution.Legs.All(l =>
            l.Samples.Zip(l.Samples.Skip(1)).All(p => (p.Second.TimestampUtc - p.First.TimestampUtc).TotalSeconds <= 1)),
            "same-place dwell events cannot merge over an evidence gap");
        var sources = dwell.Take(300).Concat(dwell.Skip(300).Take(300).Select(p => p with { SourceFile = "other.uav" })).ToArray();
        var isolated = service.Recognize([], sources, [window], input);
        Check(isolated.Execution.Legs.All(l => l.Samples.Select(p => p.SourceFile).Distinct().Count() == 1),
            "same-place events do not merge across files");
        var repeatedSecond = dwell.Take(10).Select(p => p with { TimestampUtc = time }).ToArray();
        var sameTime = service.Recognize([], repeatedSecond, [window], input);
        Check(TransitEvidenceService.Annotate(sameTime.Execution, repeatedSecond.Select(Canonical).ToArray(), input, null)
            .Legs.All(l => l.MotionKind.Length == 0), "same-second records cannot manufacture five-second motion evidence");

        var line = new SegmentationReferenceGeometry("line", "K1", P(0), P(1200), "SURVEY", "KML");
        var reference = input with { ReferenceAuthority = "KML_AUTHORITATIVE", ReferenceGeometry = [line] };
        var workAndTurn = Enumerable.Range(0, 101).SelectMany(second => Enumerable.Range(0, 8).Select(j =>
            new SurveyTrackPoint(time.AddSeconds(second), P(second * 8 + j), "turn.uav", second * 8 + j) { AltitudeM = 100 }))
            .Concat(Enumerable.Range(101, 100).SelectMany(second => Enumerable.Range(0, 8).Select(j =>
                new SurveyTrackPoint(time.AddSeconds(second), P(807, (second - 100) * 8 + j), "turn.uav", second * 8 + j) { AltitudeM = 100 }))).ToArray();
        var turned = service.Recognize([], workAndTurn, [window], reference);
        Check(turned.Execution.Passes.Count > 0 && turned.Execution.Legs.Where(l => l.SurveyTaskStableId == line.Id)
            .SelectMany(l => l.Samples).All(s => Math.Abs(s.Latitude / metre) <= 8),
            "same-second telemetry confirms a real turn before the remote endpoint, without consuming its connector");
        Check(turned.Execution.Legs.Sum(l => l.PointCount) == workAndTurn.Length, "turn refinement preserves the residual connector");
        var shallow = workAndTurn.Select(p => p.SourceRecord < 808 ? p : p with
            { Coordinate = P(807 + ((p.SourceRecord - 800) / 8d) * 7, ((p.SourceRecord - 800) / 8d) * 4) }).ToArray();
        var shallowResult = service.Recognize([], shallow, [window], reference);
        Check(shallowResult.Execution.Passes.Count > 0 && shallowResult.Execution.Legs.Where(l => l.SurveyTaskStableId == line.Id)
            .SelectMany(l => l.Samples).All(s => Math.Abs(s.Latitude / metre) <= 8),
            "40m departure evidence uses real second-to-second travel rather than one eighth of the actual motion");
        var excluded = service.Recognize([], workAndTurn,
            [window with { TaskScopeSource = "MANUAL", SurveyTaskStableIds = [] }], reference);
        Check(excluded.Execution.Passes.Count == 0 && excluded.Tasks.Count == 1,
            "local KML retry cannot bypass a manual task restriction or invent an identity");

        var travel = Enumerable.Range(0, 101).Select(i => new SurveyTrackPoint(time.AddSeconds(i), P(i * 8), "return.uav", i)
            { AltitudeM = 100 }).ToArray();
        var unmatchedReference = reference with { ReferenceGeometry = [line with { Start = P(0, 100), End = P(1200, 100) }], KnownTakeoffPositions = [P(0)] };
        var returnResult = service.Recognize([], travel, [window], unmatchedReference);
        var returnAnnotated = TransitEvidenceService.Annotate(returnResult.Execution, travel.Select(Canonical).ToArray(), unmatchedReference, null);
        Check(returnAnnotated.Legs.Any(l => l.TransitEvidence.Count > 0) && returnResult.Execution.Passes.Count == 0,
            "a real long traversal from a known home retains its transit review restriction");
        var full = Enumerable.Range(0, 151).Select(i => new SurveyTrackPoint(time.AddSeconds(i), P(i * 8), "full.uav", i)
            { AltitudeM = 100 }).ToArray();
        var formalInput = reference with { KnownTakeoffPositions = [P(0)] };
        var formal = service.Recognize([], full, [window], formalInput);
        var formalAnnotated = TransitEvidenceService.Annotate(formal.Execution, full.Select(Canonical).ToArray(), formalInput, null);
        Check(formal.Execution.Passes.Count == 1 && formalAnnotated.Legs.Single(l => l.SurveyTaskStableId == line.Id).TransitEvidence.Count == 0,
            "complete independent KML work outranks takeoff proximity");

        var midpoint = travel.Select(p => p with { Coordinate = P(p.SourceRecord * 8, 10) }).ToArray();
        var competitionInput = reference with { ReferenceGeometry = [line, line with { Id = "neighbour", Start = P(0, 20), End = P(1200, 20) }] };
        var competition = service.Recognize([], midpoint, [window], competitionInput);
        Check(competition.Execution.Passes.Count == 0 && competition.Execution.Legs.Any(l => l.CandidateTaskStableIds.Count == 2),
            "refined KML retry retains 20m neighbour competition rather than selecting the closest task");
    }
}
