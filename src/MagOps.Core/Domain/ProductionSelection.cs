namespace MagOps.Core.Domain;

public sealed record ProductionOverride(string SurveyTaskStableId,
    CoverageInterval Interval, string PassId, string ConfirmedBy);

public sealed record ProductionSelectionEntry(string SurveyTaskStableId,
    CoverageInterval Interval, string PassId, string SelectionReason,
    string QualityStatus, bool IsQualityRepeat);

public sealed record ProductionSelectionResult(string SelectionId,
    IReadOnlyList<ProductionSelectionEntry> Entries,
    IReadOnlyList<CoverageInterval> UnselectedIntervals,
    string Status);

public static class SurveyProductionSelection
{
    /// <summary>
    /// Each boundary splits an independent interval. Explicit overrides take
    /// priority; otherwise select the latest QC PASS. Pending/failed passes
    /// remain unselected. Quality repeats are never mixed with normal passes.
    /// </summary>
    public static ProductionSelectionResult Select(string taskId,
        IEnumerable<SurveyLinePass> source,
        IEnumerable<ProductionOverride>? userOverrides = null,
        bool qualityRepeat = false)
    {
        var passes = source.Where(p => p.SurveyTaskStableId == taskId &&
            p.IsQualityRepeat == qualityRepeat).ToList();
        var overrides = (userOverrides ?? []).Where(x => x.SurveyTaskStableId == taskId).ToList();
        var byId = passes.ToDictionary(x => x.PassId, StringComparer.Ordinal);
        foreach (ProductionOverride chosen in overrides)
        {
            if (!byId.TryGetValue(chosen.PassId, out SurveyLinePass? pass) ||
                !pass.QualityStatus.Equals("PASS", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(chosen.ConfirmedBy) ||
                chosen.Interval.Start < 0d || chosen.Interval.End > 1d ||
                chosen.Interval.End <= chosen.Interval.Start ||
                !SurveyCoverage.Union(pass.Coverage).Any(x =>
                    x.Start <= chosen.Interval.Start && x.End >= chosen.Interval.End))
                throw new InvalidDataException("人工选择必须指向本任务内 QC PASS 且覆盖该区间的 Pass。");
        }
        var boundaries = new SortedSet<double> { 0d, 1d };
        foreach (var coverage in passes.SelectMany(p => p.Coverage))
        {
            boundaries.Add(Math.Clamp(coverage.Start, 0d, 1d));
            boundaries.Add(Math.Clamp(coverage.End, 0d, 1d));
        }
        foreach (ProductionOverride chosen in overrides)
        {
            boundaries.Add(chosen.Interval.Start);
            boundaries.Add(chosen.Interval.End);
        }
        var selected = new List<ProductionSelectionEntry>();
        var missing = new List<CoverageInterval>();
        double[] edges = boundaries.ToArray();
        for (int i = 1; i < edges.Length; i++)
        {
            var interval = new CoverageInterval(edges[i - 1], edges[i]);
            double middle = (interval.Start + interval.End) / 2d;
            var explicitChoices = overrides.Where(x =>
                x.Interval.Start <= middle && x.Interval.End >= middle).ToList();
            if (explicitChoices.Select(x => x.PassId).Distinct(StringComparer.Ordinal).Count() > 1)
                throw new InvalidDataException("人工选择的重叠区间指向不同 Pass。");
            SurveyLinePass? choice = explicitChoices.Count > 0
                ? byId[explicitChoices[0].PassId]
                : passes.Where(p => p.QualityStatus.Equals("PASS", StringComparison.OrdinalIgnoreCase) &&
                    p.Coverage.Any(c => c.Start <= middle && c.End >= middle))
                    .OrderByDescending(p => p.EndUtc).ThenBy(p => p.PassId, StringComparer.Ordinal)
                    .FirstOrDefault();
            if (choice is null)
                missing.Add(interval);
            else
                selected.Add(new ProductionSelectionEntry(taskId, interval, choice.PassId,
                    explicitChoices.Count > 0 ? "USER_OVERRIDE" : "LATEST_QC_PASS",
                    choice.QualityStatus, qualityRepeat));
        }
        var normalized = MergeEntries(selected);
        return new ProductionSelectionResult("selection-" + Guid.NewGuid().ToString("N"),
            normalized, SurveyCoverage.Union(missing), missing.Count == 0 ? "READY" : "REVIEW_REQUIRED");
    }

    private static IReadOnlyList<ProductionSelectionEntry> MergeEntries(
        IReadOnlyList<ProductionSelectionEntry> entries)
    {
        var merged = new List<ProductionSelectionEntry>();
        foreach (ProductionSelectionEntry entry in entries)
        {
            if (merged.Count > 0 && merged[^1].PassId == entry.PassId &&
                merged[^1].SelectionReason == entry.SelectionReason &&
                Math.Abs(merged[^1].Interval.End - entry.Interval.Start) < 1e-9)
            {
                merged[^1] = merged[^1] with
                {
                    Interval = new CoverageInterval(merged[^1].Interval.Start, entry.Interval.End)
                };
            }
            else merged.Add(entry);
        }
        return merged;
    }
}
