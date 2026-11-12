using MagOps.Core.Domain;

namespace MagOps.Infrastructure.Services;

public static partial class MotherLineGroupingService
{
    // Classify members before forming executable commands. Connector fragments
    // stay available for explicit assignment but never travel with a gap command.
    private static IReadOnlyList<MotherLineRestoreGroup> ClassifySuspects(MotherLineRestoreGroup group, AuditProductionResult audit, double maximumGapM)
    {
        var tasks = (audit.EffectiveTasks ?? []).ToDictionary(t => t.SurveyTaskStableId);
        var legs = audit.Execution.Legs.ToDictionary(l => l.FlightLegId);
        var anchors = group.AnchorLegIds.Where(legs.ContainsKey).Select(id => legs[id]).ToArray();
        string kind = "";
        var boundaries = new Dictionary<string, int>();
        var connectorIds = new HashSet<string>(StringComparer.Ordinal);
        var gapIds = new HashSet<string>(StringComparer.Ordinal);
        var terminalIds = new HashSet<string>(StringComparer.Ordinal);
        if (tasks.TryGetValue(group.TargetId, out var target))
        foreach (string id in group.ShortLegIds.Where(legs.ContainsKey))
        {
            var samples = legs[id].Samples;
            if (samples.Count < 3) continue;
            var p = samples.Select(s => Project(Point(s), target)).ToArray();
            double sign = p[^1].Along >= p[0].Along ? 1 : -1;
            int run = 0;
            for (int i = 1; i < p.Length; i++)
            {
                double along = (p[i].Along - p[i - 1].Along) * sign;
                double cross = Math.Abs(p[i].Cross - p[i - 1].Cross);
                bool turning = Math.Sqrt(along * along + cross * cross) >= .5 && (along < -.5 || cross > Math.Max(.5, Math.Abs(along) * Math.Tan(20 * Math.PI / 180)));
                run = turning ? run + 1 : 0;
                if (run < 3) continue;
                boundaries[id] = samples[i - run].SourceRecord;
                connectorIds.Add(id);
                break;
            }
            if (legs[id].RouteRole is "CONNECTOR" or "TRANSIT" || legs[id].MotionKind.Length > 0 || legs[id].TransitEvidence.Count > 0)
                connectorIds.Add(id);
            var range = (Min: p.Min(x => x.Along), Max: p.Max(x => x.Along));
            var motherRanges = anchors.Where(a => a.SurveyTaskStableId == group.TargetId && a.FlightLegId != id)
                .Select(a => { var q = a.Samples.Select(s => Project(Point(s), target).Along).ToArray(); return (Leg: a, Min: q.Min(), Max: q.Max()); })
                .OrderBy(a => a.Min).ToArray();
            bool internalGap = motherRanges.Zip(motherRanges.Skip(1)).Any(pair =>
                range.Min >= pair.First.Max - 10 && range.Max <= pair.Second.Min + 10 &&
                pair.Second.Min - pair.First.Max > 0 && range.Max >= pair.First.Max - maximumGapM && range.Min <= pair.Second.Min + maximumGapM);
            // Two distinct assigned pieces bounding a real internal gap remain
            // stronger identity evidence than a local geometric disturbance.
            if (internalGap && legs[id].RouteRole is not ("CONNECTOR" or "TRANSIT") && legs[id].MotionKind.Length == 0 && legs[id].TransitEvidence.Count == 0)
            { connectorIds.Remove(id); boundaries.Remove(id); gapIds.Add(id); }
            else if (connectorIds.Contains(id)) continue;
        }
        if (tasks.TryGetValue(group.TargetId, out var motherAxis))
        {
            var assignedPieces = group.LegIds.Concat(group.AnchorLegIds).Distinct().Where(id => legs.ContainsKey(id) && legs[id].SurveyTaskStableId is not null &&
                    legs[id].Samples.Count > 1 && legs[id].RouteRole is not ("CONNECTOR" or "TRANSIT") && legs[id].MotionKind.Length == 0 && legs[id].TransitEvidence.Count == 0)
                .Select(id => (Id: id, Identity: legs[id].SurveyTaskStableId, Range: legs[id].Samples.Select(p => Project(Point(p), motherAxis).Along).ToArray()))
                .Where(x => x.Range.Length > 1).OrderBy(x => x.Range.Min()).ToArray();
            foreach (var pair in assignedPieces.Zip(assignedPieces.Skip(1)))
            {
                if (pair.First.Identity == pair.Second.Identity) continue;
                double gap = pair.Second.Range.Min() - pair.First.Range.Max();
                double shorter = Math.Min(pair.First.Range.Max() - pair.First.Range.Min(), pair.Second.Range.Max() - pair.Second.Range.Min());
                if (gap >= -Math.Max(10, shorter * .1) && gap <= maximumGapM)
                { gapIds.Add(pair.First.Id); gapIds.Add(pair.Second.Id); }
            }
            // A fragment is a terminal extension only if its complete projected
            // interval lies outside all assigned coverage, with a nearby anchor
            // on that same side. Touching a mother endpoint alone is insufficient.
            if (assignedPieces.Length > 0)
            {
                double coverageMin = assignedPieces.Min(x => x.Range.Min());
                double coverageMax = assignedPieces.Max(x => x.Range.Max());
                foreach (string id in group.ShortLegIds.Where(legs.ContainsKey))
                {
                    if (connectorIds.Contains(id) || gapIds.Contains(id)) continue;
                    var projected = legs[id].Samples.Select(p => Project(Point(p), motherAxis).Along).ToArray();
                    if (projected.Length < 2) continue;
                    double fragmentMin = projected.Min(), fragmentMax = projected.Max();
                    bool leftExtension = fragmentMax < coverageMin && coverageMin - fragmentMax <= maximumGapM;
                    bool rightExtension = fragmentMin > coverageMax && fragmentMin - coverageMax <= maximumGapM;
                    if (leftExtension ^ rightExtension) terminalIds.Add(id);
                }
            }
        }
        // Distinguish overlapping flights from complementary pieces of a mother line.
        var members = group.LegIds.Where(legs.ContainsKey).Select(id => legs[id]).Where(l => l.SurveyTaskStableId is not null && l.Samples.Count >= 2).ToArray();
        if (tasks.TryGetValue(group.TargetId, out var axis))
        foreach (var first in members)
        foreach (var second in members.Where(l => l.SurveyTaskStableId != first.SurveyTaskStableId))
        {
            var a = first.Samples.Select(p => Project(Point(p), axis).Along).ToArray();
            var b = second.Samples.Select(p => Project(Point(p), axis).Along).ToArray();
            double shorter = Math.Min(a.Max() - a.Min(), b.Max() - b.Min());
            double overlap = Math.Max(0, Math.Min(a.Max(), b.Max()) - Math.Max(a.Min(), b.Min()));
            if (shorter >= 60 && overlap >= shorter * .6) kind = "整线重叠·疑似重复覆盖";
        }
        var output = new List<MotherLineRestoreGroup>();
        if (group.Status == "CONFLICT") kind = "竞争归属或用途冲突";
        var connectorLegs = group.LegIds.Where(connectorIds.Contains).ToArray();
        var normalLegs = group.LegIds.Where(id => !connectorIds.Contains(id)).ToArray();
        var normalShorts = group.ShortLegIds.Where(id => !connectorIds.Contains(id)).ToArray();
        bool executable = normalShorts.Length > 0 || normalLegs.Any(id => group.OriginalTaskByLeg.TryGetValue(id, out var old) && old != group.TargetId);
        if (executable)
        {
            string disposition = gapIds.Count > 0 ? "断开测线·母线缺口" : terminalIds.Count > 0 ? "测线端部延伸·单独核对" : "";
            var normal = group with { LegIds = normalLegs, ShortLegIds = normalShorts, SuspectKind = disposition,
                Id = group.Id + (connectorLegs.Length > 0 ? ":gap" : ""),
                OriginalTaskByLeg = group.OriginalTaskByLeg.Where(p => normalLegs.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value),
                Reason = group.Reason + (disposition.Length == 0 ? "" : $"；特征分组：{disposition}。连接处碎段已拆出，不随本组批量归并。") };
            if (kind == "整线重叠·疑似重复覆盖") normal = normal with { Status = "SUSPECT", SuspectKind = kind };
            else if (group.Status == "CONFLICT") normal = normal with { SuspectKind = "竞争归属或用途冲突" };
            else if (disposition.Length == 0) normal = normal with { SuspectKind = "断开测线·互补航段" };
            output.Add(normal);
        }
        foreach (string connectorId in connectorLegs)
        {
            string id = "connector:" + TrackSegmentationReferenceService.Hash(connectorId + "/" + group.TargetId);
            output.Add(group with { Id = id, LegIds = [connectorId], ShortLegIds = [connectorId], OriginalNames = ["连接处碎段"],
                Status = "SUSPECT", SuspectKind = "连接处碎段·保留候选", TurnBoundaryRecords = boundaries.Where(p => p.Key == connectorId).ToDictionary(p => p.Key, p => p.Value),
                OriginalTaskByLeg = new Dictionary<string, string?> { [connectorId] = group.OriginalTaskByLeg.GetValueOrDefault(connectorId) },
                Reason = "连接处/持续转弯空间特征；保留当前纳入和归属状态，不参与断开测线自动归并。若此处存在真实断口，可选中单段并明确指定母线。" });
        }
        if (output.Count == 0)
        {
            string suspect = group.Status == "CONFLICT" ? "竞争归属或用途冲突" : group.SuspectKind;
            output.Add(group with { SuspectKind = suspect, Status = suspect.Length > 0 ? "SUSPECT" : group.Status });
        }
        return output;
    }
}
