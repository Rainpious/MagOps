using System.Text.Json;
using System.Security.Cryptography;
using MagOps.Core.Domain;
using MagOps.Infrastructure.Storage;

namespace MagOps.Infrastructure.Services;

/// <summary>Fingerprint of production selection and its evidence inputs, independent of generated IDs/time.</summary>
public static class ProductionSnapshotService
{
    public static string RequestFingerprint(AuditProductionRequest? request) =>
        TrackSegmentationReferenceService.Hash(JsonSerializer.Serialize(request));
    public static bool IsStale(string artifactPath, GeneratedArtifactMetadata metadata)
    {
        if (string.IsNullOrEmpty(metadata.ProductionRequestFingerprint) &&
            !metadata.ProcessingStage.StartsWith("prelevel_dtgz", StringComparison.Ordinal) &&
            !metadata.ProcessingStage.StartsWith("audit_execution", StringComparison.Ordinal) &&
            !metadata.ProcessingStage.StartsWith("production_", StringComparison.Ordinal) &&
            !metadata.ProcessingStage.StartsWith("diurnal_manifest", StringComparison.Ordinal) &&
            !metadata.ProcessingStage.StartsWith("line_", StringComparison.Ordinal) &&
            !metadata.ProcessingStage.StartsWith("t0_", StringComparison.Ordinal)) return false;
        if (!string.IsNullOrEmpty(metadata.ProductionRequestFingerprint) && metadata.ProductionAlgorithmVersion != ProductionAnomalyDetector.Version) return true;
        string? directory = Path.GetDirectoryName(Path.GetFullPath(artifactPath));
        if (directory is null) return false;
        if (directory.EndsWith(".review-draft", StringComparison.OrdinalIgnoreCase)) directory = directory[..^13];
        if (directory.Contains(".staging-", StringComparison.OrdinalIgnoreCase) || directory.EndsWith(".previous", StringComparison.OrdinalIgnoreCase)) return false;
        string stateDirectory = directory;
        string? state = new[] { SegmentationReviewStore.SavedInputName, SegmentationReviewStore.AutomaticStateName }
            .Select(name => Path.Combine(stateDirectory, name)).Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        if (state is null) return false;
        try
        {
            var request = JsonSerializer.Deserialize<AuditProductionRequest>(File.ReadAllText(state), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return string.IsNullOrEmpty(metadata.ProductionRequestFingerprint) ||
                RequestFingerprint(request) != metadata.ProductionRequestFingerprint;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { return true; }
    }

    public static bool IsProjectSpatialReferenceStale(string artifactPath, GeneratedArtifactMetadata metadata)
    {
        if (metadata.ContextStatus is not ("PROJECT" or "WORKAREA") ||
            string.IsNullOrWhiteSpace(metadata.ProjectId) ||
            !UsesProjectSpatialReference(metadata) ||
            !File.Exists(artifactPath))
            return false;

        string? directory = Path.GetDirectoryName(Path.GetFullPath(artifactPath));
        while (directory is not null && !ProjectPaths.HasProject(directory))
            directory = Directory.GetParent(directory)?.FullName;
        if (directory is null)
            return false;

        try
        {
            var store = new JsonStore();
            var workspace = store.Read(ProjectPaths.ExistingProject(directory), new WorkspaceRecord());
            if (!workspace.ProjectId.Equals(metadata.ProjectId, StringComparison.OrdinalIgnoreCase))
                return true;
            string current = new WorkspaceService(store).SpatialFingerprint(directory, workspace, metadata.WorkareaId);
            string artifact = metadata.ProjectSpatialReferenceFingerprint;
            if (artifact.Equals("UNAVAILABLE", StringComparison.Ordinal))
                return false;
            return string.IsNullOrWhiteSpace(artifact) || !artifact.Equals(current, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return true;
        }
    }

    public static string ProjectSpatialReferenceFingerprint(double? centralMeridian) =>
        centralMeridian?.ToString("R", System.Globalization.CultureInfo.InvariantCulture) ?? "UNCONFIRMED";

    public static bool UsesProjectSpatialReference(GeneratedArtifactMetadata metadata)
    {
        string stage = metadata.ProcessingStage;
        return stage.StartsWith("coordinate", StringComparison.OrdinalIgnoreCase) ||
               stage.StartsWith("route", StringComparison.OrdinalIgnoreCase) ||
               stage.StartsWith("prelevel_dtgz", StringComparison.OrdinalIgnoreCase) ||
               stage.StartsWith("diurnal_", StringComparison.OrdinalIgnoreCase) ||
               stage.StartsWith("audit_execution", StringComparison.OrdinalIgnoreCase) ||
               stage.StartsWith("production_", StringComparison.OrdinalIgnoreCase) ||
               stage.StartsWith("line_", StringComparison.OrdinalIgnoreCase) ||
               stage.StartsWith("t0_", StringComparison.OrdinalIgnoreCase) ||
               stage.StartsWith("mother_line", StringComparison.OrdinalIgnoreCase) ||
               stage.StartsWith("report_", StringComparison.OrdinalIgnoreCase) ||
               stage.Equals("generated_report", StringComparison.OrdinalIgnoreCase);
    }
    public static string Fingerprint(string source, string route, AuditProductionRequest? request,
        IEnumerable<ExecutionSampleReference> selected)
    {
        // Preserve the exact JSON/SHA256 contract without retaining a giant
        // UTF-16 string plus its UTF-8 copy for hundreds of thousands of keys.
        using var sha = SHA256.Create();
        using var stream = new CryptoStream(Stream.Null, sha, CryptoStreamMode.Write);
        JsonSerializer.Serialize(stream, new
        {
            Version = ProductionAnomalyDetector.Version,
            Source = source, Route = route, Request = request,
            Selected = selected.Select(x => new { x.SourceFile, x.SourceRecord })
                .OrderBy(x => x.SourceFile, StringComparer.Ordinal).ThenBy(x => x.SourceRecord)
        });
        stream.FlushFinalBlock();
        return Convert.ToHexString(sha.Hash!);
    }
}
