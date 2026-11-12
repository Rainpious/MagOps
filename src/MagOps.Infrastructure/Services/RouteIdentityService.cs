using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MagOps.Core.Routes;

namespace MagOps.Infrastructure.Services;

/// <summary>
/// Assigns opaque identities at route-project save time. A legacy label or
/// geometry hash is a migration lookup key, never the persistent identity.
/// Ambiguous legacy mappings require review instead of arbitrary selection.
/// </summary>
public static class RouteIdentityService
{
    public static void EnsureReferences(
        IReadOnlyList<RouteSortie> sorties,
        IReadOnlyList<RouteSortie>? previous = null)
    {
        RouteTaskReference[] previousReferences = (previous ?? [])
            .SelectMany(s => s.TaskReferences ?? [])
            .Where(r => !string.IsNullOrWhiteSpace(r.SurveyTaskStableId))
            .ToArray();
        var old = previousReferences
            .GroupBy(r => Key(r.LineName, r.GeometryFingerprint), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var oldByGeometry = previousReferences
            .Where(r => !string.IsNullOrWhiteSpace(r.GeometryFingerprint))
            .GroupBy(r => r.GeometryFingerprint, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var mothers = (previous ?? []).SelectMany(s => s.TaskReferences ?? [])
            .Where(r => !string.IsNullOrWhiteSpace(r.MotherLineStableId))
            .GroupBy(r => r.LineName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(r => r.MotherLineStableId)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList(), StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (RouteSortie sortie in sorties)
        {
            var refs = new List<RouteTaskReference>();
            if (sortie.Points.Count < sortie.SourceLines.Count * 2)
            {
                // Manually edited route: retain existing references for review,
                // but never invent an identity without matching geometry.
                if (sortie.SourceLines.Count > 0)
                    throw new InvalidDataException("航线端点不足，无法建立无歧义任务身份。");
                continue;
            }

            for (int i = 0; i < sortie.SourceLines.Count; i++)
            {
                string name = sortie.SourceLines[i].Trim();
                string fingerprint = Fingerprint(sortie.Points[2 * i], sortie.Points[2 * i + 1]);
                string key = Key(name, fingerprint);
                if (!seen.Add(key))
                    throw new InvalidDataException($"测线 {name} 的相同几何出现多次，需要人工确认 RepeatIndex。");

                // Stable IDs are authoritative. Name/fingerprint are migration evidence only:
                // reuse an existing identity when the evidence resolves to one stable task,
                // but never silently choose between multiple historical candidates.
                RouteTaskReference? currentSegmentReference = (sortie.TaskReferences ?? [])
                    .FirstOrDefault(r => r.ExecutionSegmentId.Equals(name, StringComparison.OrdinalIgnoreCase));
                string currentSegmentStableId =
                    currentSegmentReference?.ExecutionSegmentStableId ?? "";

                // Evidence is hierarchical, not additive. A stable segment ID is stronger than
                // display name/geometry and must not become "ambiguous" merely because a mother
                // line legitimately has several task IDs.
                List<RouteTaskReference> candidates;
                RouteTaskReference[] stableSegmentMatches = previousReferences.Where(r =>
                    !string.IsNullOrWhiteSpace(currentSegmentStableId) &&
                    r.ExecutionSegmentStableId.Equals(currentSegmentStableId, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (stableSegmentMatches.Length > 0)
                    candidates = [.. stableSegmentMatches];
                else if (old.TryGetValue(key, out var prior) && prior.Count > 0)
                    candidates = [.. prior];
                else if (oldByGeometry.TryGetValue(fingerprint, out var sameGeometry) && sameGeometry.Count > 0)
                    candidates = [.. sameGeometry];
                else
                    candidates = [.. (sortie.TaskReferences ?? []).Where(r =>
                        r.LineName.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                        r.ExecutionSegmentId.Equals(name, StringComparison.OrdinalIgnoreCase))];

                var distinct = candidates.Select(r => r.SurveyTaskStableId)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (distinct.Count > 1)
                    throw new InvalidDataException($"测线 {name} 的历史身份不唯一，需要人工确认迁移映射。");
                RouteTaskReference? selected = candidates.FirstOrDefault(r =>
                    r.SurveyTaskStableId == distinct.FirstOrDefault());
                selected ??= candidates.FirstOrDefault(r =>
                    r.ExecutionSegmentId.Equals(name, StringComparison.OrdinalIgnoreCase));

                bool executionSegmentReference =
                    currentSegmentReference?.ProtectedExecutionSegment == true ||
                    selected?.ProtectedExecutionSegment == true;
                string businessLineName = executionSegmentReference
                    ? currentSegmentReference?.LineName is { Length: > 0 } currentParent
                        ? currentParent
                        : selected?.LineName is { Length: > 0 } priorParent
                            ? priorParent
                            : name
                    : name;
                if (mothers.TryGetValue(businessLineName, out var motherIds) && motherIds.Count > 1)
                    throw new InvalidDataException($"母线 {businessLineName} 的历史身份不唯一，需要人工确认。");
                var boundMother = (sortie.TaskReferences ?? []).FirstOrDefault(r =>
                    r.LineName.Equals(businessLineName, StringComparison.OrdinalIgnoreCase) &&
                    r.GeometryFingerprint.Equals(fingerprint, StringComparison.OrdinalIgnoreCase))?.MotherLineStableId;
                if (!string.IsNullOrWhiteSpace(boundMother) && selected?.MotherLineStableId is { Length: > 0 } savedMother &&
                    !boundMother.Equals(savedMother, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"测线 {businessLineName} 的当前母线身份与历史版本不同，请核对设计版本并另存工程，不能静默继承旧身份。");
                string mother = selected?.MotherLineStableId is { Length: > 0 } id
                    ? id : motherIds?.FirstOrDefault() ?? NewId("mother");
                mothers[businessLineName] = [mother];
                refs.Add(new RouteTaskReference
                {
                    LineName = businessLineName,
                    GeometryFingerprint = fingerprint,
                    MotherLineStableId = mother,
                    SurveyLineStableId = selected?.SurveyLineStableId is { Length: > 0 } lineId
                        ? lineId : NewId("line"),
                    SurveyTaskStableId = selected?.SurveyTaskStableId is { Length: > 0 } taskId
                        ? taskId : NewId("task"),
                    SurveyPurpose = selected?.SurveyPurpose ?? "NORMAL",
                    // Stable task/line identity may come from history, but segment display
                    // identity belongs to the current canonical plan. Re-numbering an
                    // unaffected old S2 to current S3 must not write the historical S2
                    // label/index back into the saved Route Project.
                    SegmentIndex = currentSegmentReference?.SegmentIndex
                        ?? selected?.SegmentIndex ?? 0,
                    RepeatIndex = selected?.RepeatIndex ?? 0,
                    ExecutionSegmentId = currentSegmentReference?.ExecutionSegmentId
                        ?? selected?.ExecutionSegmentId ?? "",
                    ExecutionSegmentStableId = currentSegmentStableId.Length > 0
                        ? currentSegmentStableId
                        : selected?.ExecutionSegmentStableId ?? "",
                    ProtectedExecutionSegment = currentSegmentReference?.ProtectedExecutionSegment ??
                        selected?.ProtectedExecutionSegment ?? false
                });
            }
            sortie.TaskReferences = refs;
        }
    }

    public static string Fingerprint(RouteEndpoint a, RouteEndpoint b)
    {
        static string Point(RouteEndpoint p) =>
            p.X.ToString("R", CultureInfo.InvariantCulture) + "," +
            p.Y.ToString("R", CultureInfo.InvariantCulture);
        string[] ends = [Point(a), Point(b)];
        Array.Sort(ends, StringComparer.Ordinal);
        return Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(ends[0] + "|" + ends[1]))).ToLowerInvariant();
    }

    private static string Key(string name, string fingerprint) =>
        name.Trim().ToUpperInvariant() + "|" + fingerprint.ToLowerInvariant();

    private static string NewId(string kind) => kind + "-" + Guid.NewGuid().ToString("N");
}
