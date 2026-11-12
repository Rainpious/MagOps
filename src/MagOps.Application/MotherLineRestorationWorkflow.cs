using MagOps.Infrastructure.Services;
using MagOps.Core.Domain;

namespace MagOps.Application;

/// <summary>Group-level commands; history is persisted with the review, independent of grid selection.</summary>
public static class MotherLineRestorationWorkflow
{
    public static IReadOnlyList<FlightLeg> UnconfirmedIndependent(AuditProductionResult audit, MotherLineRestorationState state, string scope)
    {
        var passed = audit.Execution.Passes.Select(p => p.FlightLegId).ToHashSet();
        var selected = audit.SelectedSamples.Select(p => (p.SourceFile, p.SourceRecord)).ToHashSet();
        return audit.Execution.Legs.Where(l => !passed.Contains(l.FlightLegId) && l.Samples.Any(p => selected.Contains((p.SourceFile, p.SourceRecord))) &&
            (state.IndependentDecisionScope != scope || !state.RetainedIndependentLegIds.Contains(l.FlightLegId))).ToArray();
    }

    public static MotherLineLegDecision Snapshot(ReviewLegDecision d) => new(d.LegId, d.Confirmed, d.Included, d.QualityStatus, d.TaskId);
    private static ReviewLegDecision Decision(MotherLineLegDecision d) => new(d.LegId, d.Confirmed, d.Included, d.QualityStatus, d.TaskId);

    public static MotherLineRestorationState Protect(MotherLineRestorationState state, IEnumerable<string> ids) => state with
    { ProtectedLegIds = state.ProtectedLegIds.Concat(ids).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray(), CheckedKey = "" };

    public static MotherLineRestorationState Include(MotherLineRestorationState state, IEnumerable<string> ids, bool include)
    {
        var changed = ids.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return state with { ExcludedLegIds = include ? state.ExcludedLegIds.Where(id => !changed.Contains(id)).ToArray() :
            state.ExcludedLegIds.Concat(changed).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray(), CheckedKey = "" };
    }

    public static MotherLineRestorationState Refresh(MotherLineRestorationState state, IReadOnlyList<MotherLineRestoreGroup> candidates)
    {
        var history = state.Groups.Where(g => g.Status is "APPLIED_AUTO" or "APPLIED_MANUAL" or "UNDONE" or "REJECTED" or "EXCLUDED").ToArray();
        var known = history.Select(g => g.Id).Concat(state.RejectedGroupIds).ToHashSet();
        return state with { Groups = history.Concat(candidates.Where(g => !known.Contains(g.Id))).ToArray() };
    }

    public static MotherLineRestorationState Apply(MotherLineRestorationState state, IEnumerable<string> groupIds,
        ReviewDecisionState decisions, SurveyExecutionResult execution, bool automatic)
    {
        var chosen = groupIds.ToHashSet();
        var legs = execution.Legs.ToDictionary(l => l.FlightLegId);
        var groups = state.Groups.ToArray();
        string batch = Guid.NewGuid().ToString("N");
        var touched = new HashSet<string>();
        for (int i = 0; i < groups.Length; i++)
        {
            var g = groups[i];
            if (string.IsNullOrWhiteSpace(g.TargetId) || !chosen.Contains(g.Id) || !(g.Status == "HIGH" || !automatic && g.Status == "REVIEW") ||
                state.RejectedGroupIds.Contains(g.Id) || g.LegIds.Any(id => !legs.ContainsKey(id) || state.ProtectedLegIds.Contains(id) || state.ExcludedLegIds.Contains(id)) ||
                g.LegIds.Any(touched.Contains)) continue;
            var before = g.LegIds.Select(id => Snapshot(decisions.Get(id))).ToArray();
            bool identityOnly = !g.AllowShortInclusion;
            foreach (string id in g.LegIds)
            {
                var prior = decisions.Get(id);
                decisions.Set(identityOnly ? prior with { TaskId = g.TargetId } :
                    prior with { TaskId = g.TargetId, Included = prior.Accepted || g.AllowShortInclusion && g.ShortLegIds.Contains(id),
                    Confirmed = true, QualityStatus = PassQualityMarkers.Pending });
                touched.Add(id);
            }
            groups[i] = g with { Before = before, After = g.LegIds.Select(id => Snapshot(decisions.Get(id))).ToArray(),
                Status = automatic ? "APPLIED_AUTO" : "APPLIED_MANUAL", BatchId = batch,
                AddedRecords = g.AllowShortInclusion ? g.ShortLegIds.Where(id => !before.Single(b => b.LegId == id).Included).Sum(id => legs[id].Samples.Count) : 0 };
        }
        return state with { Groups = groups, CheckedKey = "" };
    }

    public static MotherLineRestorationState Undo(MotherLineRestorationState state, IEnumerable<string> groupIds, ReviewDecisionState decisions)
    {
        var ids = groupIds.ToHashSet(); var rejected = state.RejectedGroupIds.ToHashSet();
        // Reverse history order: a later operation may depend on an earlier restored identity.
        var groups = state.Groups.ToArray();
        for (int i = groups.Length - 1; i >= 0; i--)
        {
            var g = groups[i];
            if (!ids.Contains(g.Id) || g.Status is not ("APPLIED_AUTO" or "APPLIED_MANUAL") || g.After.Count == 0 ||
                g.LegIds.Any(id => g.ProtectedBefore is null && state.ProtectedLegIds.Contains(id) || state.ExcludedLegIds.Contains(id)) || g.After.Any(d => Snapshot(decisions.Get(d.LegId)) != d)) continue;
            foreach (var d in g.Before) decisions.Set(Decision(d));
            if (g.ProtectedBefore is not null) state = state with { ProtectedLegIds = state.ProtectedLegIds.Where(id => !g.LegIds.Contains(id)).Concat(g.ProtectedBefore).Distinct().ToArray() };
            rejected.Add(g.Id); groups[i] = g with { Status = "UNDONE" };
        }
        return state with { Groups = groups, RejectedGroupIds = rejected.ToArray(), CheckedKey = "" };
    }

    public static MotherLineRestorationState RecordManual(MotherLineRestorationState state, IReadOnlyList<ManualFlightLegAssignment> assignments,
        ReviewDecisionState decisions, SurveyExecutionResult execution, IReadOnlyList<PlannedSurveyLineTask>? tasks = null)
    {
        var legs = execution.Legs.ToDictionary(l => l.FlightLegId);
        if (assignments.Count == 0 || assignments.Select(a => a.FlightLegId).Distinct().Count() != assignments.Count ||
            assignments.Any(a => !legs.ContainsKey(a.FlightLegId))) throw new InvalidDataException("归并航段必须唯一且属于当前快照。");
        string batch = Guid.NewGuid().ToString("N"); var groups = state.Groups.ToList();
        foreach (var target in assignments.GroupBy(a => a.SurveyTaskStableId))
        {
            var ids = target.Select(a => a.FlightLegId).Order().ToArray();
            var before = ids.Select(id => Snapshot(decisions.Get(id))).ToArray();
            foreach (string id in ids) decisions.Set(decisions.Get(id) with { TaskId = target.Key });
            groups.Add(new("manual:" + Guid.NewGuid().ToString("N"), target.Key, target.Key, 0, ids, [], [], "APPLIED_MANUAL", "用户明确指定母线；保留纳入选择", 0, 0, 0)
                { Before = before, After = ids.Select(id => Snapshot(decisions.Get(id))).ToArray(), BatchId = batch,
                    OriginalTaskByLeg = ids.ToDictionary(id => id, id => legs[id].SurveyTaskStableId), AllowShortInclusion = false,
                    OriginalTasks = (tasks ?? []).Where(t => ids.Any(id => legs[id].SurveyTaskStableId == t.SurveyTaskStableId) || t.SurveyTaskStableId == target.Key).ToArray(),
                    OriginalNames = (tasks ?? []).Where(t => ids.Any(id => legs[id].SurveyTaskStableId == t.SurveyTaskStableId)).OrderBy(t => t.Order).Select(t => t.LineName).ToArray(),
                    ProtectedBefore = state.ProtectedLegIds.Where(ids.Contains).ToArray() });
        }
        return Protect(state with { Groups = groups, CheckedKey = "" }, assignments.Select(a => a.FlightLegId));
    }

    public static MotherLineRestorationState Reject(MotherLineRestorationState state, IEnumerable<string> groupIds)
    {
        var ids = groupIds.ToHashSet();
        var eligible = state.Groups.Where(g => ids.Contains(g.Id) && g.Status is "HIGH" or "REVIEW" or "CONFLICT" or "SUSPECT").Select(g => g.Id).ToHashSet();
        return state with { Groups = state.Groups.Select(g => eligible.Contains(g.Id) ? g with { Status = "REJECTED" } : g).ToArray(),
            RejectedGroupIds = state.RejectedGroupIds.Concat(eligible).Distinct().ToArray(), CheckedKey = "" };
    }

    public static MotherLineRestorationState Reopen(MotherLineRestorationState state, IEnumerable<string> groupIds)
    {
        var ids = state.Groups.Where(g => groupIds.Contains(g.Id) && g.Status is "UNDONE" or "REJECTED" or "EXCLUDED").Select(g => g.Id).ToHashSet();
        return state with { Groups = state.Groups.Where(g => !ids.Contains(g.Id)).ToArray(),
            RejectedGroupIds = state.RejectedGroupIds.Where(id => !ids.Contains(id)).ToArray(), CheckedKey = "" };
    }
}
