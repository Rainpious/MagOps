using MagOps.Core.Domain;

namespace MagOps.Infrastructure.Services;

public enum WorkareaIdentityMatchKind
{
    NotFound,
    CanonicalId,
    LegacySubareaId,
    DisplayName,
    Ambiguous
}

public sealed record WorkareaIdentityResolution(
    WorkareaIdentityMatchKind MatchKind,
    WorkareaRecord? Workarea,
    string Selector,
    IReadOnlyList<string> CandidateWorkareaIds)
{
    public bool Found => Workarea is not null;
    public bool UsedCompatibilityAlias =>
        MatchKind is WorkareaIdentityMatchKind.LegacySubareaId or WorkareaIdentityMatchKind.DisplayName;
}

/// <summary>
/// Resolves external workarea selectors without allowing display/path aliases to become
/// authoritative identity. WorkareaId is canonical. SubareaId and DisplayName are accepted
/// only as compatibility aliases and ambiguous aliases are never selected implicitly.
/// </summary>
public static class WorkareaIdentityResolver
{
    public static WorkareaIdentityResolution Resolve(
        IReadOnlyList<WorkareaRecord> workareas,
        string? selector)
    {
        ArgumentNullException.ThrowIfNull(workareas);
        string key = (selector ?? "").Trim();
        if (key.Length == 0)
            return new(WorkareaIdentityMatchKind.NotFound, null, "", []);

        WorkareaIdentityResolution? canonical = ResolveUnique(
            workareas.Where(x =>
                !string.IsNullOrWhiteSpace(x.WorkareaId) &&
                x.WorkareaId.Equals(key, StringComparison.OrdinalIgnoreCase)),
            WorkareaIdentityMatchKind.CanonicalId,
            key);
        if (canonical is not null)
            return canonical;

        WorkareaIdentityResolution? legacy = ResolveUnique(
            workareas.Where(x =>
                !string.IsNullOrWhiteSpace(x.SubareaId) &&
                x.SubareaId.Equals(key, StringComparison.OrdinalIgnoreCase)),
            WorkareaIdentityMatchKind.LegacySubareaId,
            key);
        if (legacy is not null)
            return legacy;

        WorkareaIdentityResolution? display = ResolveUnique(
            workareas.Where(x =>
                (!string.IsNullOrWhiteSpace(x.DisplayName) &&
                 x.DisplayName.Equals(key, StringComparison.OrdinalIgnoreCase)) ||
                x.PreviousDisplayNames.Contains(key, StringComparer.OrdinalIgnoreCase)),
            WorkareaIdentityMatchKind.DisplayName,
            key);
        if (display is not null)
            return display;

        return new(WorkareaIdentityMatchKind.NotFound, null, key, []);
    }

    private static WorkareaIdentityResolution? ResolveUnique(
        IEnumerable<WorkareaRecord> candidates,
        WorkareaIdentityMatchKind matchKind,
        string selector)
    {
        WorkareaRecord[] matches = candidates.ToArray();
        if (matches.Length == 0)
            return null;
        if (matches.Length == 1)
            return new(matchKind, matches[0], selector, [matches[0].WorkareaId]);

        return new(
            WorkareaIdentityMatchKind.Ambiguous,
            null,
            selector,
            matches.Select(x => x.WorkareaId)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray());
    }
}
