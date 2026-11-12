using System.Text.Json.Serialization;
using MagOps.Core.Analysis;
using MagOps.Infrastructure.Storage;

namespace MagOps.Infrastructure.Services;

public sealed class ConcaveLineRuleProfile
{
    [JsonPropertyName("schema_version")]
    public string SchemaVersion { get; set; } = ConcaveLineRuleProfileService.SchemaVersion;

    [JsonPropertyName("workarea_id")]
    public string WorkareaId { get; set; } = "";

    [JsonPropertyName("workarea_path")]
    public string WorkareaPath { get; set; } = "";

    [JsonPropertyName("partition_code")]
    public string PartitionCode { get; set; } = "";

    [JsonPropertyName("source_geometry_fingerprint")]
    public string SourceGeometryFingerprint { get; set; } = "";

    [JsonPropertyName("rules")]
    public List<ConcaveLineRule> Rules { get; set; } = [];

    [JsonPropertyName("updated_at")]
    public string UpdatedAt { get; set; } = "";
}

/// <summary>
/// Persists concave-line decisions only inside an exact geometry scope.
/// Legacy v1 profiles were keyed only by workarea path and L number; they are
/// intentionally ignored so a stale CONNECT decision cannot be replayed after
/// repartitioning or regenerating the source geometry.
/// </summary>
public sealed class ConcaveLineRuleProfileService
{
    public const string SchemaVersion = "concave-line-rules-2";
    public const string FileName = "concave_line_rules.json";

    private readonly JsonStore _store;

    public ConcaveLineRuleProfileService(JsonStore store) => _store = store;

    public ConcaveLineRuleProfile Load(
        string root,
        string workareaId,
        string workareaPath,
        string partitionCode,
        string sourceGeometryFingerprint)
    {
        string normalizedId = NormalizeToken(workareaId);
        string normalizedPath = NormalizePath(workareaPath);
        string normalizedPartition = NormalizePartition(partitionCode);
        string normalizedFingerprint = NormalizeFingerprint(sourceGeometryFingerprint);

        List<ConcaveLineRuleProfile> profiles = ReadAll(root);
        ConcaveLineRuleProfile? found = profiles
            .Where(profile => string.Equals(
                profile.SchemaVersion,
                SchemaVersion,
                StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault(profile =>
                SameWorkarea(profile, normalizedId, normalizedPath) &&
                NormalizePartition(profile.PartitionCode).Equals(
                    normalizedPartition,
                    StringComparison.OrdinalIgnoreCase) &&
                NormalizeFingerprint(profile.SourceGeometryFingerprint).Equals(
                    normalizedFingerprint,
                    StringComparison.OrdinalIgnoreCase));

        if (found is null)
        {
            return Empty(
                normalizedId,
                normalizedPath,
                normalizedPartition,
                normalizedFingerprint);
        }

        return new ConcaveLineRuleProfile
        {
            SchemaVersion = SchemaVersion,
            WorkareaId = normalizedId,
            WorkareaPath = normalizedPath,
            PartitionCode = normalizedPartition,
            SourceGeometryFingerprint = normalizedFingerprint,
            Rules = (found.Rules ?? []).Select(CloneRule).ToList(),
            UpdatedAt = found.UpdatedAt ?? ""
        };
    }

    public ConcaveLineRuleProfile Save(
        string root,
        string workareaId,
        string workareaPath,
        string partitionCode,
        string sourceGeometryFingerprint,
        IEnumerable<ConcaveLineRule> rules)
    {
        using var writeLock = WorkspaceFileLock.AcquireForRoot(root);
        string normalizedId = NormalizeToken(workareaId);
        string normalizedPath = NormalizePath(workareaPath);
        string normalizedPartition = NormalizePartition(partitionCode);
        string normalizedFingerprint = NormalizeFingerprint(sourceGeometryFingerprint);
        if (normalizedFingerprint.Length == 0)
        {
            throw new ArgumentException(
                "凹形断线规则必须绑定源几何指纹。",
                nameof(sourceGeometryFingerprint));
        }

        var profile = new ConcaveLineRuleProfile
        {
            SchemaVersion = SchemaVersion,
            WorkareaId = normalizedId,
            WorkareaPath = normalizedPath,
            PartitionCode = normalizedPartition,
            SourceGeometryFingerprint = normalizedFingerprint,
            Rules = rules.Select(CloneRule)
                .OrderBy(x => ParseSortKey(x.SourceLine))
                .ThenBy(x => x.SourceLine, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            UpdatedAt = DateTimeOffset.UtcNow.ToString("O")
        };

        List<ConcaveLineRuleProfile> profiles = ReadAll(root);
        int index = profiles.FindIndex(existing =>
            string.Equals(
                existing.SchemaVersion,
                SchemaVersion,
                StringComparison.OrdinalIgnoreCase) &&
            SameWorkarea(existing, normalizedId, normalizedPath) &&
            NormalizePartition(existing.PartitionCode).Equals(
                normalizedPartition,
                StringComparison.OrdinalIgnoreCase) &&
            NormalizeFingerprint(existing.SourceGeometryFingerprint).Equals(
                normalizedFingerprint,
                StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
            profiles[index] = profile;
        else
            profiles.Add(profile);

        profiles = profiles
            .OrderBy(x => NormalizeToken(x.WorkareaId), StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => NormalizePath(x.WorkareaPath), StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => NormalizePartition(x.PartitionCode), StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => NormalizeFingerprint(x.SourceGeometryFingerprint), StringComparer.OrdinalIgnoreCase)
            .ToList();
        _store.Write(ProjectPaths.Json(root, FileName), profiles);
        return profile;
    }

    private List<ConcaveLineRuleProfile> ReadAll(string root)
    {
        List<ConcaveLineRuleProfile> profiles = _store.Read(
            ProjectPaths.Json(root, FileName),
            new List<ConcaveLineRuleProfile>());
        return profiles ?? [];
    }

    private static ConcaveLineRuleProfile Empty(
        string workareaId,
        string workareaPath,
        string partitionCode,
        string sourceGeometryFingerprint) => new()
    {
        WorkareaId = workareaId,
        WorkareaPath = workareaPath,
        PartitionCode = partitionCode,
        SourceGeometryFingerprint = sourceGeometryFingerprint
    };

    private static bool SameWorkarea(
        ConcaveLineRuleProfile profile,
        string normalizedId,
        string normalizedPath)
    {
        string profileId = NormalizeToken(profile.WorkareaId);
        if (normalizedId.Length > 0 && profileId.Length > 0)
        {
            return profileId.Equals(
                normalizedId,
                StringComparison.OrdinalIgnoreCase);
        }
        return NormalizePath(profile.WorkareaPath).Equals(
            normalizedPath,
            StringComparison.OrdinalIgnoreCase);
    }

    private static ConcaveLineRule CloneRule(ConcaveLineRule input) => new()
    {
        SourceLine = input.SourceLine ?? "",
        Action = input.Action ?? "",
        SecondOutputLine = input.SecondOutputLine ?? "",
        Notes = input.Notes ?? "",
        GeometryFingerprint = input.GeometryFingerprint ?? ""
    };

    private static int ParseSortKey(string sourceLine)
    {
        try { return ConcaveBoundaryLineService.ParseExactLineNumber(sourceLine); }
        catch { return int.MaxValue; }
    }

    private static string NormalizeToken(string? value) =>
        (value ?? "").Trim();

    private static string NormalizePath(string? value) =>
        (value ?? "").Replace('\\', '/').Trim().Trim('/');

    private static string NormalizePartition(string? value) =>
        (value ?? "").Trim().ToUpperInvariant();

    private static string NormalizeFingerprint(string? value) =>
        (value ?? "").Trim().ToLowerInvariant();
}
