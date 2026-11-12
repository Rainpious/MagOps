using MagOps.Application;
using MagOps.Core.Domain;
using MagOps.Infrastructure.Services;

internal static class ReviewDecisionChecks
{
    internal static void Run()
    {
        var seed = new AuditProductionRequest("decision-fixture", [], [], [], true)
        { DefaultMatchedSelectionsApplied = true, AnomalyDecisions = [new("anomaly-1", "EXCLUDE", "fixture")] };
        var state = new ReviewDecisionState();
        state.Move([("known", true), ("unknown", false)], true);
        if (state.Revision != 1 || state.AcceptedLegIds().Count != 2 ||
            state.Get("unknown").Confirmed || state.Get("known").QualityStatus != "PENDING_QC")
            throw new InvalidOperationException("Batch inclusion must keep identity and quality independent");
        long revision = state.Revision;
        state.Move([("known", true), ("unknown", false)], true);
        if (state.Revision != revision) throw new InvalidOperationException("No-op batch changes revision");
        state.Set(state.Get("known") with { TaskId = "task-1", QualityStatus = "FAIL" });
        var request = state.ApplyTo(seed, "fixture");
        if (request.ConfirmedPassIds.Single() != "known/pass" || request.ManualIncludedFlightLegIds.Count != 2 ||
            request.QualityConfirmations.Single().QualityStatus != "FAIL" || request.AnomalyDecisions != seed.AnomalyDecisions)
            throw new InvalidOperationException("Decision projection changes request semantics");
        var restored = new ReviewDecisionState();
        restored.RestoreLeg("known", request, new HashSet<string>(["task-1"]));
        restored.RestoreLeg("unknown", request, new HashSet<string>());
        if (restored.Get("known") != state.Get("known") || restored.Get("unknown") != state.Get("unknown"))
            throw new InvalidOperationException("Decision roundtrip loses manual or unmatched inclusion");
        restored.Move([("known", true), ("unknown", false)], false);
        var empty = restored.ApplyTo(seed, "fixture");
        if (restored.AcceptedLegIds().Count != 0 || empty.ManualIncludedFlightLegIds.Count != 0 ||
            empty.ConfirmedPassIds.Count != 0 || !empty.DefaultMatchedSelectionsApplied || empty.QualityConfirmations.Count != 1)
            throw new InvalidOperationException("Explicit empty selection must retain independent quality and default marker");
        restored.RestoreLeg("known", request, new HashSet<string>());
        if (restored.Get("known").TaskId is not null) throw new InvalidOperationException("Unavailable task assignment restored silently");

        // Future/foreign markers must not silently become approved work.
        var future = seed with { ConfirmedPassIds = ["future/pass"],
            QualityConfirmations = [new("future/pass", "FUTURE_QC", "fixture")] };
        restored.RestoreLeg("future", future, new HashSet<string>());
        if (restored.Get("future").QualityStatus != "FUTURE_QC" || restored.Get("future").Accepted ||
            PassQualityMarkers.Parse("pass") != PassQuality.Unknown ||
            RoutePurposeMarkers.Parse("FUTURE_ROLE") != RoutePurpose.Unknown ||
            ExecutionDirectionMarkers.Parse("REVERSE") != FlightExecutionDirection.Reverse)
            throw new InvalidOperationException("Unknown markers lost or promoted to approved quality");
        if (restored.ApplyTo(seed, "fixture").QualityConfirmations.Any(q => q.PassId == "future/pass"))
            throw new InvalidOperationException("Unknown quality must not produce an approval confirmation");

        var wireLeg = new FlightLeg("leg", "mission", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
            "SURVEY", "task", 0, "") { RouteRole = "FUTURE_ROLE" };
        var encoded = System.Text.Json.JsonSerializer.Serialize(wireLeg);
        var decoded = System.Text.Json.JsonSerializer.Deserialize<FlightLeg>(encoded)!;
        if (decoded.RouteRole != "FUTURE_ROLE" || encoded.Contains("RoutePurpose"))
            throw new InvalidOperationException("State mapping changed the persisted contract");
    }
}
