namespace MagOps.Core.Routes;

public static class RouteSiteSuggestions
{
    public sealed record Task(AccessPoint First, AccessPoint Last, double BudgetM);

    public static List<LaunchSite> Recommend(IReadOnlyList<Task> tasks, AccessPlanningData data, CancellationToken token, IReadOnlyList<Task>? preserveLegacy = null)
    {
        var result = new List<LaunchSite>();
        if (tasks.Count == 0 || data.MaxSuggestedSites == 0) return result;
        var candidates = new List<LaunchSite>();
        var seen = new HashSet<AccessPoint>();
        foreach (var road in data.Roads.Where(r => r.UseForRecommendations).OrderBy(r => r.Id, StringComparer.Ordinal))
        {
            void Add(int segment, int edge, double fraction, AccessPoint point)
            {
                if (!seen.Add(point)) return;
                candidates.Add(new($"auto-{road.Id}-{segment}-{edge}-{BitConverter.DoubleToInt64Bits(fraction):X}", "",
                    point.Longitude, point.Latitude, RoadId: road.Id, SegmentIndex: segment, EdgeIndex: edge, EdgeFraction: fraction, SnapDistanceM: 0));
            }
            for (int si = 0; si < road.Segments.Count; si++)
            for (int ei = 0; ei + 1 < road.Segments[si].Count; ei++)
            {
                token.ThrowIfCancellationRequested();
                var a = road.Segments[si][ei]; var b = road.Segments[si][ei+1];
                int steps = Math.Clamp((int)Math.Ceiling(RouteAccessPlanner.Distance(a,b) / 100), 1, 1024);
                for (int i = 0; i <= steps; i++)
                {
                    double t = (double)i / steps;
                    Add(si, ei, t, new(Math.IEEERemainder(a.Longitude + t * Math.IEEERemainder(b.Longitude-a.Longitude,360),360),
                        a.Latitude + t * (b.Latitude-a.Latitude)));
                }
            }
            foreach (var endpoint in tasks.SelectMany(x => new[] { x.First, x.Last }).Distinct())
            {
                token.ThrowIfCancellationRequested();
                if (RouteAccessPlanner.Snap(endpoint, [road], double.MaxValue) is { } snap)
                    Add(snap.SegmentIndex, snap.EdgeIndex, snap.Fraction, snap.Point);
            }
        }
        var existingIds = data.Sites.Select(s => s.Id).ToHashSet();
        var pending = tasks.ToList();
        while (pending.Count > 0 && (data.MaxSuggestedSites == AccessPlanningData.AutomaticSiteCount || result.Count < data.MaxSuggestedSites))
        {
            LaunchSite? best = null;
            List<Task> covered = [];
            double bestCost = double.PositiveInfinity;
            foreach (var c in candidates)
            {
                token.ThrowIfCancellationRequested();
                if (existingIds.Contains(c.Id)) continue;
                var point = new AccessPoint(c.Longitude,c.Latitude);
                if (result.Count == 0 && preserveLegacy != null && preserveLegacy.Any(t =>
                    RouteAccessPlanner.Distance(point,t.First) + RouteAccessPlanner.Distance(point,t.Last) > t.BudgetM)) continue;
                int count = 0; double sum = 0;
                foreach (var task in pending)
                {
                    double cost = RouteAccessPlanner.Distance(point,task.First) + RouteAccessPlanner.Distance(point,task.Last);
                    if (cost <= task.BudgetM) { count++; sum += cost; }
                }
                if (count == 0 || count < covered.Count || (count == covered.Count && sum >= bestCost)) continue;
                best = c; bestCost = sum;
                covered = pending.Where(t => RouteAccessPlanner.Distance(point,t.First) + RouteAccessPlanner.Distance(point,t.Last) <= t.BudgetM).ToList();
            }
            if (best is null) break;
            existingIds.Add(best.Id);
            int number = result.Count + 1;
            while (data.Sites.Concat(result).Any(site => site.Name == $"建议点{number}")) number++;
            result.Add(best with { Name = $"建议点{number}" });
            var completed = covered.ToHashSet();
            pending.RemoveAll(completed.Contains);
        }
        return result;
    }
}
