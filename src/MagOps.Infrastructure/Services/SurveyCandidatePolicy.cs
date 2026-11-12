using MagOps.Core.Domain;

namespace MagOps.Infrastructure.Services;

/// <summary>Shared default/batch suggestion boundary. Explicit human inclusions remain separate.</summary>
public static class SurveyCandidatePolicy
{
    public static bool CanSuggest(FlightLeg leg, SurveyLinePass? pass, bool axisOnlyDirection) => pass is not null &&
        leg.MotionKind.Length == 0 && (pass.ExecutionDirection != "REVERSE" || axisOnlyDirection) &&
        leg.RouteRole is not ("TRANSIT" or "CONNECTOR") && leg.TransitEvidence.Count == 0 &&
        !leg.ReviewReason.Contains("REVIEW", StringComparison.Ordinal);

    public static IReadOnlyList<string> SuggestedLegIds(AuditProductionResult audit)
    {
        var passes = audit.Execution.Passes.ToDictionary(p => p.FlightLegId);
        var actualIds = (audit.EffectiveTasks ?? []).Where(t => t.GeometryOrigin == "ACTUAL_TRACK")
            .Select(t => t.SurveyTaskStableId).ToHashSet();
        return audit.Execution.Legs.Where(l =>
        {
            passes.TryGetValue(l.FlightLegId, out var pass);
            return CanSuggest(l, pass, pass is not null && actualIds.Contains(pass.SurveyTaskStableId));
        }).Select(l => l.FlightLegId).ToArray();
    }
}
