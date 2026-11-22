using MagOps.Infrastructure.Services;
using MagOps.Core.Domain;

namespace MagOps.Application;

/// <summary>Session-local review decisions. No table, display filter or row order owns these facts.</summary>
public sealed class ReviewDecisionState
{
    private readonly Dictionary<string, ReviewLegDecision> _legs = new(StringComparer.OrdinalIgnoreCase);
    private long _revision;
    public long Revision => Interlocked.Read(ref _revision);
    public int Count => _legs.Count;
    public ReviewLegDecision Get(string legId) => _legs.GetValueOrDefault(legId) ?? new(legId);
    public IReadOnlySet<string> AcceptedLegIds() => _legs.Values.Where(d => d.Accepted)
        .Select(d => d.LegId).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public void Reset()
    {
        if (_legs.Count == 0) return;
        _legs.Clear(); Interlocked.Increment(ref _revision);
    }

    public void Set(ReviewLegDecision decision)
    {
        if (_legs.TryGetValue(decision.LegId, out var prior) && prior == decision) return;
        _legs[decision.LegId] = decision; Interlocked.Increment(ref _revision);
    }

    public void Move(IEnumerable<(string LegId, bool HasIdentity)> legs, bool include)
    {
        bool changed = false;
        foreach (var (id, hasIdentity) in legs)
        {
            var prior = Get(id);
            var next = prior with { Included = include, Confirmed = include && hasIdentity };
            if (_legs.TryGetValue(id, out var existing) && existing == next) continue;
            _legs[id] = next; changed = true;
        }
        if (changed) Interlocked.Increment(ref _revision);
    }

    public void RestoreLeg(string legId, AuditProductionRequest request, IReadOnlySet<string> allowedTaskIds)
    {
        string passId = legId + "/pass";
        var assignment = request.ManualLegAssignments.FirstOrDefault(a => a.FlightLegId.Equals(legId, StringComparison.OrdinalIgnoreCase));
        var quality = request.QualityConfirmations.FirstOrDefault(q => q.PassId.Equals(passId, StringComparison.OrdinalIgnoreCase));
        Set(new(legId, request.ConfirmedPassIds.Contains(passId, StringComparer.OrdinalIgnoreCase),
            request.ManualIncludedFlightLegIds.Contains(legId, StringComparer.OrdinalIgnoreCase),
            quality?.QualityStatus ?? PassQualityMarkers.Pending,
            assignment is not null ? allowedTaskIds.FirstOrDefault(id =>
                id.Equals(assignment.SurveyTaskStableId, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(id)) : null,
            request.ActualRepeatLegIds.Contains(legId, StringComparer.OrdinalIgnoreCase)));
    }

    public AuditProductionRequest ApplyTo(AuditProductionRequest seed, string reviewer)
    {
        var confirmed = _legs.Values.Where(d => d.Confirmed).Select(d => d.LegId + "/pass").ToArray();
        return seed with
        {
            ActualRepeatLegIds = _legs.Values.Where(d => d.ActualRepeat).Select(d => d.LegId).ToArray(),
            ConfirmedPassIds = confirmed,
            ManualIncludedFlightLegIds = _legs.Values.Where(d => d.Included).Select(d => d.LegId).ToArray(),
            ManualLegAssignments = _legs.Values.Where(d => !string.IsNullOrWhiteSpace(d.TaskId)).Select(d =>
                new ManualFlightLegAssignment(d.LegId, d.TaskId!,
                    seed.MotherLineRestoration is { } restore && !restore.ProtectedLegIds.Contains(d.LegId) &&
                    restore.Groups.Any(g => g.Status is "APPLIED_AUTO" or "APPLIED_MANUAL" && g.TargetId == d.TaskId && g.LegIds.Contains(d.LegId))
                    ? "母测线组合恢复；来源及应用前后决定见 MotherLineRestoration" : "用户在图形核验界面指定所属测线")).ToArray(),
            QualityConfirmations = _legs.Values.Where(d => PassQualityMarkers.Parse(d.QualityStatus) is PassQuality.Passed or PassQuality.Failed).Select(d =>
                new PassQualityConfirmation(d.LegId + "/pass", d.QualityStatus, reviewer)).ToArray(),
            Overrides = seed.Overrides.Where(o => confirmed.Contains(o.PassId, StringComparer.OrdinalIgnoreCase)).ToArray()
        };
    }
}

public sealed record ReviewLegDecision(string LegId, bool Confirmed = false, bool Included = false,
    string QualityStatus = PassQualityMarkers.Pending, string? TaskId = null, bool ActualRepeat = false)
{
    // Explicit preprocessing inclusion and legacy confirmed/QC-approved selection
    // remain independent routes; including a leg does not approve its quality.
    public bool Accepted => Included || Confirmed && PassQualityMarkers.Parse(QualityStatus) == PassQuality.Passed;
}
