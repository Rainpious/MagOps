using System.Text.Json;
using MagOps.Core.Domain;
using MagOps.Infrastructure.Services;

namespace MagOps.SmokeTests;

internal static class LocalHitCandidateChecks
{
    public static void Run()
    {
        const double metre = 1 / 111319.49;
        var time = new DateTimeOffset(2026, 10, 11, 0, 0, 0, TimeSpan.Zero);
        GeoCoordinate P(double x, double y = 0) => new(x * metre, y * metre);
        var task = new PlannedSurveyLineTask(1, 0, "L1", "", "L1", P(0), P(800), 800)
            { SurveyTaskStableId = "line" };
        FlightLeg Leg(string id, double x, double y = 0) => new(id, "flight", time, time.AddSeconds(10), "UNRESOLVED", null, 11, "")
        {
            Samples = Enumerable.Range(0, 11).Select(i => new FlightLegSample(id + ".uav", i,
                time.AddSeconds(i), P(x + i * 8, y).Longitude, P(x, y).Latitude)).ToArray()
        };
        var unresolved = Leg("unmatched", 400);
        var matched = Leg("seed", 0) with { Classification = "SURVEY", SurveyTaskStableId = task.SurveyTaskStableId };
        var pass = WholeLegMatcherService.CreateManualAssignmentPass(matched, task)! with
            { PlannedCoverage = .5, MatchMethod = "SINGLE_ENDPOINT", ExecutionDirection = "FORWARD" };
        var execution = new SurveyExecutionResult("fixture", [], [], [matched, unresolved], [pass], []);
        var window = new MissionTimeWindow("flight", "", time, time.AddMinutes(2)) { TaskScopeSource = "AUTO" };
        var input = new TrackSegmentationInput { Enabled = true, AxisDegrees = 90 };
        SurveyExecutionResult Offer(SurveyExecutionResult e, MissionTimeWindow? w = null,
            PlannedSurveyLineTask? t = null) => LocalHitCandidateService.Annotate(e, [t ?? task], [w ?? window], input);
        void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
        var offered = Offer(execution);
        var candidateLeg = offered.Legs[1];
        Check(candidateLeg.LocalHitCandidates.Count == 1 && candidateLeg.CandidateTaskStableIds.SequenceEqual(["line"]),
            "a local endpoint hit proposes a line for a continuous unresolved run");
        Check(candidateLeg.SurveyTaskStableId is null && candidateLeg.Samples.SequenceEqual(unresolved.Samples) &&
            candidateLeg.Classification == unresolved.Classification && offered.Passes.SequenceEqual(execution.Passes),
            "candidate evidence cannot assign identity, approve quality, alter passes or remove samples");
        Check(JsonSerializer.Deserialize<FlightLeg>(JsonSerializer.Serialize(candidateLeg))!.LocalHitCandidates.Count == 1,
            "candidate evidence survives audit serialization");
        Check(Offer(offered).Legs[1].ContextEvidence.Count == candidateLeg.ContextEvidence.Count,
            "repeated annotation does not duplicate context");
        foreach (var method in new[] { "SINGLE_ENDPOINT", "BODY_TRACK" })
            Check(Offer(execution with { Passes = [pass with { PlannedCoverage = 1, MatchMethod = method }] }).Legs[1].LocalHitCandidates.Count == 1,
                "local hit method is retained even when its coverage rounds to full");
        Check(Offer(execution with { Passes = [pass with { PlannedCoverage = 1, MatchMethod = "ACTUAL_TRACK", MatchConfidence = .5 }] })
            .Legs[1].LocalHitCandidates.Count == 0, "a full forward actual fit is not a local seed, including low-confidence fits");
        Check(Offer(execution with { Passes = [pass with { PlannedCoverage = 1, MatchMethod = "ENDPOINT_PAIR" }] })
            .Legs[1].LocalHitCandidates.Count == 0, "full forward endpoint match is not a partial seed");
        Check(Offer(execution with { Passes = [pass with { MatchMethod = "USER_ASSIGNED", ExecutionDirection = "REVERSE" }] })
            .Legs[1].LocalHitCandidates.Count == 0, "manual identity does not silently propagate");
        var reverse = Offer(execution with { Passes = [pass with { PlannedCoverage = 1, MatchMethod = "ACTUAL_TRACK", ExecutionDirection = "REVERSE" }] });
        Check(reverse.Legs[1].LocalHitCandidates.Single().HasReverseSeed && !reverse.Passes[0].ReviewConfirmed &&
            reverse.Passes[0].QualityStatus == pass.QualityStatus, "reverse evidence remains review-only");
        Check(Offer(execution, window with { TaskScopeSource = "MANUAL", SurveyTaskStableIds = [] }).Legs[1].LocalHitCandidates.Count == 0,
            "manual window scope cannot be bypassed by candidates");
        foreach (string role in new[] { "TRANSIT", "CONNECTOR" })
            Check(Offer(execution with { Legs = [matched, unresolved with { RouteRole = role }] }).Legs[1].LocalHitCandidates.Count == 0,
                "explicit route purpose cannot be promoted to a local candidate");
        Check(Offer(execution with { Legs = [matched, unresolved with { MissionExecutionId = "outside" }] }).Legs[1].LocalHitCandidates.Count == 0,
            "candidates cannot cross selected flight windows");
        Check(Offer(execution with { Legs = [matched, Leg("far", 400, 50)] }).Legs[1].LocalHitCandidates.Count == 0,
            "a parallel line beyond the search corridor is not proposed");
        var across = unresolved with { Samples = unresolved.Samples.Select((s, i) => s with
            { Longitude = P(400).Longitude, Latitude = P(0, i * 8).Latitude }).ToArray() };
        Check(Offer(execution with { Legs = [matched, across] }).Legs[1].LocalHitCandidates.Count == 0,
            "a crossing track does not acquire a collinear identity");
        var gaps = unresolved with { Samples = unresolved.Samples.Select((s, i) => s with
            { TimestampUtc = time.AddSeconds(i / 2 * 10 + i % 2) }).ToArray() };
        Check(Offer(execution with { Legs = [matched, gaps] }).Legs[1].LocalHitCandidates.Count == 0,
            "isolated points separated by gaps cannot accumulate into continuous support");
        var sources = unresolved with { Samples = unresolved.Samples.Select((s, i) => s with { SourceFile = "source-" + i / 2 }).ToArray() };
        Check(Offer(execution with { Legs = [matched, sources] }).Legs[1].LocalHitCandidates.Count == 0,
            "continuity cannot bridge source files");
        var extension = execution with { Legs = [matched, Leg("extension", 800)] };
        Check(Offer(extension).Legs[1].LocalHitCandidates.Count == 0, "independent endpoints bound candidate extent");
        Check(Offer(extension, t: task with { GeometryOrigin = "ACTUAL_TRACK" }).Legs[1].LocalHitCandidates.Single().ExtendsActualFit,
            "actual-fit extensions are bounded and explicitly identified as proposals");
        var tasks = Enumerable.Range(0, 4).Select(i => task with { SurveyTaskStableId = "line-" + i,
            Start = P(0, i * 4), End = P(800, i * 4) }).ToArray();
        var many = LocalHitCandidateService.Annotate(execution with
            { Passes = tasks.Select(t => pass with { SurveyTaskStableId = t.SurveyTaskStableId }).ToArray() }, tasks, [window], input);
        Check(many.Legs[1].LocalHitCandidates.Count == 3 && many.Legs[1].LocalHitCandidates[0].TaskId == "line-0",
            "candidate list is limited to three and prefers the nearest equally supported identity");
    }
}
