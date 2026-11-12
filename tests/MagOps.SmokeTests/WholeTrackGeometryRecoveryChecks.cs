using System.Text.Json;
using MagOps.Core.Domain;
using MagOps.Infrastructure.Services;

namespace MagOps.SmokeTests;

internal static class WholeTrackGeometryRecoveryChecks
{
    public static void Run()
    {
        const double unit = 1 / 111319.49;
        var time = new DateTimeOffset(2026, 10, 30, 0, 0, 0, TimeSpan.Zero);
        GeoCoordinate P(double x, double y) => new(x * unit, y * unit);
        PlannedSurveyLineTask Task(string id, double y) => new(1, 0, id, "", id, P(0, y), P(1000, y), 1000)
        { SurveyTaskStableId = id, GeometryOrigin = "IMPORTED_REFERENCE" };
        var a = Task("A", 0); var b = Task("B", 65);
        SurveyTrackPoint[] Points(double y, bool reverse = false) => Enumerable.Range(0, 81)
            .Select(i => new SurveyTrackPoint(time.AddSeconds(i), P(reverse ? 900 - i * 10 : 100 + i * 10, y), "fixture.uav", i + 1)).ToArray();
        var window = new MissionTimeWindow("mission", "", time.AddSeconds(-1), time.AddMinutes(3))
        { SurveyTaskStableIds = ["A", "B"], SourceFiles = ["fixture.uav"], TaskScopeSource = "MANUAL" };
        var options = new WholeLegMatchOptions();
        FlightLeg Leg(SurveyTrackPoint[] points) => new("leg", "mission", points[0].TimestampUtc, points[^1].TimestampUtc,
            "SURVEY_CANDIDATE", null, points.Length, "ANONYMOUS_STRAIGHT_TRACK")
        { Samples = points.Select(p => new FlightLegSample(p.SourceFile, p.SourceRecord, p.TimestampUtc,
            p.Coordinate.Longitude, p.Coordinate.Latitude)).ToArray() };
        SurveyExecutionResult Execution(FlightLeg leg) => new("fixture", [], [], [leg], [], []);
        SurveyExecutionResult Apply(FlightLeg leg, PlannedSurveyLineTask[]? tasks = null, MissionTimeWindow? scope = null,
            WholeLegMatchOptions? settings = null) => WholeTrackGeometryRecoveryService.Apply(Execution(leg), tasks ?? [a], [scope ?? window], settings ?? options);
        void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        var points = Points(30); var leg = Leg(points);
        var result = Apply(leg);
        Check(result.Passes.Single().MatchMethod == "WHOLE_TRACK_GEOMETRY" && result.Legs.Single().SurveyTaskStableId == "A",
            "wide-offset unassigned line is recovered without endpoint or seed hit");
        Check(result.Legs.Single().Samples.SequenceEqual(leg.Samples) && result.Passes.Single().Samples.Count == points.Length,
            "recovery preserves all source observations and their order");
        Check(result.Passes.Single().QualityStatus == "PENDING_QC" && !result.Passes.Single().ReviewConfirmed &&
            result.Passes.Single().FlightQuality!.OutsideCorridorPoints == points.Length,
            "identity recovery does not mark outside-corridor quality as passed");
        Check(result.Legs.Single().GeometryCandidates.Single().MedianDistanceM is > 29.99 and < 30.01,
            "persisted geometric evidence matches known perpendicular distance");
        Check(Apply(leg, settings: options with { EnableGeometryRecovery = false }).Passes.Count == 0, "switch restores legacy unmatched behavior");
        Check(Apply(leg, [a, Task("near", 60)]).Passes.Count == 1,
            "unauthorized nearby task is not a mission candidate");
        var competition = Apply(leg, [a, b with { Start = P(0, 60), End = P(1000, 60) }]);
        Check(competition.Passes.Count == 0 && competition.Legs.Single().GeometryCandidates.Count == 2 &&
            competition.Legs.Single().CandidateTaskStableIds.Count == 2, "parallel tie keeps ranked competition instead of nearest identity");
        Check(Apply(leg, [a, b]).Passes.Count == 0, "small neighbour advantage does not force winner");
        Check(Apply(Leg(Points(25)), [a, Task("B", 100)]).Passes.Count == 1, "clear winner is assigned inside authorized scope");
        Check(Apply(leg, scope: window with { SurveyTaskStableIds = [] }).Passes.Count == 0, "empty manual scope does not invent task membership");
        Check(Apply(leg, scope: window with { SourceFiles = ["other"] }).Passes.Count == 0, "source-window restriction remains authoritative");
        Check(Apply(leg with { RouteRole = "CONNECTOR" }).Passes.Count == 0 &&
            Apply(leg with { ReviewReason = "EXPLICIT_AXIS_MISMATCH_REVIEW" }).Passes.Count == 0, "connector and axis veto remain intact");
        Check(Apply(leg with { CandidateTaskStableIds = ["A", "B"] }).Passes.Count == 0, "legacy ambiguity is not erased by recovery");
        Check(Apply(leg with { SurveyTaskStableId = "original" }).Legs.Single().SurveyTaskStableId == "original", "existing identity remains intact");
        Check(Apply(leg, [a with { GeometryOrigin = "ACTUAL_TRACK" }]).Passes.Count == 0, "actual fit is not promoted to design authority");
        var reversed = Apply(Leg(Points(30, true)));
        Check(reversed.Passes.Single().ExecutionDirection == "REVERSE" &&
            Math.Abs(reversed.Legs.Single().GeometryCandidates[0].MedianDistanceM - 30) < .01, "reverse traversal retains offset and direction");
        Check(Apply(Leg(Points(80))).Passes.Count == 0, "far parallel transit is not recovered");
        var broken = points.ToArray(); broken[40] = broken[40] with { SourceRecord = 9000 };
        Check(Apply(Leg(broken)).Passes.Count == 0, "record gap cannot be hidden by whole-line sampling");
        var outlier = points.ToArray(); outlier[37] = outlier[37] with { Coordinate = P(470, 400) };
        Check(Apply(Leg(outlier)).Passes.Count == 0, "full-point checks catch unsampled detour");
        var frozen = points.Select(p => p with { Coordinate = P(500, 30) }).ToArray();
        Check(Apply(Leg(frozen)).Passes.Count == 0, "stationary cluster cannot become a line");
        var endpointOutside = points.Select(p => p with { Coordinate = P(p.SourceRecord * 10 - 100, 30) }).ToArray();
        Check(Apply(Leg(endpointOutside)).Passes.Count == 0, "reference endpoints are not extrapolated to capture transit");
        Check(JsonSerializer.Serialize(result.Legs.Single()).Contains("MedianDistanceM"), "candidate evidence survives persisted summary");
        var legacy = new WholeLegMatcherService().Match([a], points, [window with { SurveyTaskStableIds = ["A"] }], options with { EnableGeometryRecovery = false });
        var enabled = new WholeLegMatcherService().Match([a], points, [window with { SurveyTaskStableIds = ["A"] }], options);
        Check(legacy.Passes.Count == 0 && enabled.Passes.Count == 1, "main matcher automatically recovers wide-offset straight run");
        var input = new TrackSegmentationInput { Enabled = true, AxisDegrees = 90, ReferenceAuthority = "KML_AUTHORITATIVE",
            ReferenceGeometry = [new("A", "A", a.Start, a.End, "SURVEY", "fixture")] };
        var track = new TrackSegmentationService().Recognize([], points, [window with { SurveyTaskStableIds = ["A"] }], input);
        Check(track.Execution.Passes.Any(p => p.MatchMethod == "WHOLE_TRACK_GEOMETRY"), "reference track pipeline applies recovery after scope and connector checks");
        try { WholeTrackGeometryRecoveryService.Validate(options with { GeometryRecoverySearchM = double.NaN }); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("invalid recovery settings were accepted");
    }
}
