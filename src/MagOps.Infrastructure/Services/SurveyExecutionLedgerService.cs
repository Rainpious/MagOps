using MagOps.Core.Domain;
using MagOps.Infrastructure.Storage;

namespace MagOps.Infrastructure.Services;

public sealed record SurveyExecutionDifference(
    string SurveyTaskStableId, string LineName,
    double LegacyCoverage, double WholeLegCoverage, string WholeLegStatus);

public sealed record SurveyExecutionComparison(
    string Status, string LegacyAlgorithm, string ShadowAlgorithm,
    int UnresolvedLegCount, IReadOnlyList<SurveyExecutionDifference> Differences,
    SurveyExecutionResult Execution);

/// <summary>
/// Separate shadow ledger: it never writes a production result or changes
/// the existing point-based progress and magnetic segmentation outputs.
/// </summary>
public sealed class SurveyExecutionLedgerService
{
    private readonly WholeLegMatcherService _matcher = new();

    public SurveyExecutionComparison Compare(
        IReadOnlyList<PlannedSurveyLineTask> tasks,
        IReadOnlyList<SurveyTrackPoint> points,
        IReadOnlyList<MissionTimeWindow> windows)
    {
        SurveyExecutionResult execution = _matcher.Match(tasks, points, windows);

        var differences = tasks.Select(task =>
        {
            string id = task.SurveyTaskStableId.Length > 0
                ? task.SurveyTaskStableId : task.TaskId;
            SurveyCompletionResult cumulative = SurveyCoverage.Calculate(id,
                task.LengthM, execution.Passes.Where(p =>
                    p.SurveyTaskStableId == id && !p.IsQualityRepeat)
                    .SelectMany(p => p.Coverage));
            return new SurveyExecutionDifference(id, task.LineName,
                0d,
                cumulative.CoverageRatio, cumulative.Status);
        }).ToList();
        bool unresolved = execution.Legs.Any(x => x.Classification == "UNRESOLVED" ||
            x.ReviewReason.Length > 0);
        return new SurveyExecutionComparison(unresolved ? "REVIEW_REQUIRED" : "SHADOW_ONLY",
            "REMOVED_LEGACY_NO_COMPARISON",
            WholeLegMatcherService.AlgorithmVersion,
            execution.Legs.Count(x => x.Classification == "UNRESOLVED"),
            differences, execution);
    }

    public void SaveShadowReport(string projectRoot, string path,
        SurveyExecutionComparison report)
    {
        using var writeLock = WorkspaceFileLock.AcquireForRoot(projectRoot);
        string fullRoot = Path.GetFullPath(projectRoot);
        string fullPath = Path.GetFullPath(path);
        if (!Path.IsPathFullyQualified(fullPath) ||
            !fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("执行对比报告必须保存在项目目录内。", nameof(path));
        new JsonStore().Write(fullPath, report);
    }
}
