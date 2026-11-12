using System.Security.Cryptography;
using System.Text;
using MagOps.Core.Routes;

namespace MagOps.Infrastructure.Services;

public static class RouteProjectMigration
{
    public static void MigrateV1ExecutionSegmentIdentity(RouteProjectFile project)
    {
        ArgumentNullException.ThrowIfNull(project);
        foreach (RouteExecutionSegment segment in project.ExecutionSegments ?? [])
        {
            if (string.IsNullOrWhiteSpace(segment.SegmentStableId))
                segment.SegmentStableId = LegacyStableId(segment);
        }

        var byDisplay = (project.ExecutionSegments ?? [])
            .Where(x => !string.IsNullOrWhiteSpace(x.SegmentId))
            .GroupBy(x => x.SegmentId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);
        foreach (RouteTaskReference reference in project.Sorties.SelectMany(x => x.TaskReferences ?? []))
        {
            if (!string.IsNullOrWhiteSpace(reference.ExecutionSegmentStableId) ||
                string.IsNullOrWhiteSpace(reference.ExecutionSegmentId))
                continue;
            if (!byDisplay.TryGetValue(reference.ExecutionSegmentId, out RouteExecutionSegment[]? candidates) ||
                candidates.Length != 1)
                throw new InvalidDataException(
                    $"旧航线工程执行段 {reference.ExecutionSegmentId} 无法唯一迁移，需要人工复核。");
            reference.ExecutionSegmentStableId = candidates[0].SegmentStableId;
        }
        project.SchemaVersion = "magops-route-project-2";
    }

    public static void ValidateExecutionSegmentIdentity(RouteProjectFile project)
    {
        RouteExecutionSegment[] segments = (project.ExecutionSegments ?? []).ToArray();
        if (segments.Any(x => string.IsNullOrWhiteSpace(x.SegmentStableId)))
            throw new InvalidDataException("执行段缺少 Stable ID。");
        if (segments.GroupBy(x => x.SegmentStableId, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
            throw new InvalidDataException("执行段 Stable ID 重复。");
        var known = segments.Select(x => x.SegmentStableId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (RouteTaskReference reference in project.Sorties.SelectMany(x => x.TaskReferences ?? [])
                     .Where(x => x.ProtectedExecutionSegment))
            if (string.IsNullOrWhiteSpace(reference.ExecutionSegmentStableId) ||
                !known.Contains(reference.ExecutionSegmentStableId))
                throw new InvalidDataException(
                    $"任务引用的执行段身份无效：{reference.ExecutionSegmentId}。");
    }

    private static string LegacyStableId(RouteExecutionSegment segment)
    {
        // Deterministic only for migration of an already persisted v1 object.
        string evidence = string.Join("|",
            segment.MotherLine.Trim().ToUpperInvariant(),
            segment.SegmentId.Trim().ToUpperInvariant(),
            segment.CutOperationId.Trim().ToUpperInvariant(),
            segment.Start.X.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            segment.Start.Y.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            segment.End.X.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            segment.End.Y.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(evidence)))
            .ToLowerInvariant();
        return "segment-" + hash[..32];
    }
}
