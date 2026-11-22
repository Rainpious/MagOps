using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MagOps.Infrastructure.Services;

public sealed record GeneratedArtifactLineage(
    IReadOnlyList<string> SourceFileStableIds,
    IReadOnlyList<string> GsmSessionStableIds,
    IReadOnlyList<string> SurveyTaskStableIds,
    IReadOnlyList<string> SurveyLineStableIds,
    IReadOnlyList<string> ExecutionSegmentStableIds,
    string PartitionCode = "",
    string PartitionManifestPath = "")
{
    public static GeneratedArtifactLineage Empty { get; } =
        new([], [], [], [], []);
}

public sealed record GeneratedArtifactMetadata(
    int SchemaVersion, string ArtifactId, string Producer, string Role, string ProcessingStage,
    string FileName, string ContentSha256, DateTimeOffset GeneratedAtUtc,
    string SourceFingerprint, IReadOnlyList<string> SourceFiles,
    string? ProjectId = null, string? WorkareaId = null,
    string ContextStatus = "UNAVAILABLE", string QualityStatus = "UNREVIEWED",
    GeneratedArtifactLineage? Lineage = null)
{
    public IReadOnlyList<ArtifactDependency>? Dependencies { get; init; }
    public System.Text.Json.JsonElement? ExportOptions { get; init; }
    public string DependencyContract { get; init; } = "";
    public string ExportTransactionId { get; init; } = "";
    public IReadOnlyList<ArtifactQualityEvidence>? PriorQualityEvidence { get; init; }
    public string ProductionRequestFingerprint { get; init; } = "";
    public string ProductionSnapshotFingerprint { get; init; } = "";
    public string ProductionAlgorithmVersion { get; init; } = "";
    public string ProjectSpatialReferenceFingerprint { get; init; } = "";
}

public sealed record GeneratedArtifactContext(
    string ProjectId,
    string WorkareaId)
{
    public string ContextStatus =>
        string.IsNullOrWhiteSpace(ProjectId)
            ? "UNAVAILABLE"
            : string.IsNullOrWhiteSpace(WorkareaId) ? "PROJECT" : "WORKAREA";

    public static GeneratedArtifactContext Unavailable { get; } = new("", "");
}

public sealed record ArtifactIdentityInspection(string Status, string MetadataPath,
    GeneratedArtifactMetadata? Metadata, string? Warning, bool IsHistoricalVersion = false)
{
    public bool IsProductionStale { get; init; }
    public bool IsProjectSpatialReferenceStale { get; init; }
}

/// <summary>Portable identity evidence; a matching hash is integrity, not production approval.</summary>
public static class GeneratedArtifactIdentity
{
    public const string Suffix = ".magops.json";
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip
    };

    public static string TaggedName(string fileName, string stage)
    {
        if (stage.Length == 0 || stage.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
            throw new ArgumentException("Invalid identity stage.", nameof(stage));
        string extension = fileName.EndsWith(".csv.gz", StringComparison.OrdinalIgnoreCase)
            ? ".csv.gz" : Path.GetExtension(fileName);
        return fileName[..^extension.Length] + "__MagOps_" + stage + extension;
    }

    public static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public static ArtifactIdentityInspection Inspect(string path)
    {
        if (path.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase))
            return new("METADATA_FILE", path, null, null);
        string sidecar = path + Suffix;
        if (!File.Exists(sidecar))
            return new(Path.GetFileName(path).Contains("__MagOps_", StringComparison.Ordinal)
                ? "MISSING_METADATA" : "UNTAGGED", sidecar, null,
                Path.GetFileName(path).Contains("__MagOps_", StringComparison.Ordinal)
                    ? "文件名有 MagOps 标签，但配套元数据缺失；不能仅凭文件名确认身份。" : null);
        try
        {
            var metadata = JsonSerializer.Deserialize<GeneratedArtifactMetadata>(File.ReadAllText(sidecar), Options);
            if (metadata is null || metadata.SchemaVersion is < 1 or > 3 || metadata.Producer != "MagOps" ||
                string.IsNullOrWhiteSpace(metadata.ArtifactId) || string.IsNullOrWhiteSpace(metadata.Role) ||
                string.IsNullOrWhiteSpace(metadata.ProcessingStage) || metadata.SourceFiles is null ||
                metadata.ContentSha256 is null || metadata.ContentSha256.Length != 64 ||
                !metadata.ContentSha256.All(Uri.IsHexDigit))
                return new("INVALID_METADATA", sidecar, null, "元数据格式或版本无效，身份需复核。");
            if (!Hash(path).Equals(metadata.ContentSha256, StringComparison.OrdinalIgnoreCase))
                return new("HASH_MISMATCH", sidecar, null, "成果内容与配套元数据哈希不一致，不能使用旧身份标签。");
            bool productionStale = ProductionSnapshotService.IsStale(path, metadata);
            bool spatialReferenceStale = ProductionSnapshotService.IsProjectSpatialReferenceStale(path, metadata);
            string? warning = productionStale
                ? "生产选择或异常区间版本已变化，旧成果需重新生成。"
                : spatialReferenceStale ? "空间基准的中央经线已变化，旧空间成果需重新计算或复核。" : null;
            return new("VERIFIED", sidecar, metadata, warning,
                IsPreviousVersion(path, metadata.ArtifactId))
            { IsProductionStale = productionStale, IsProjectSpatialReferenceStale = spatialReferenceStale };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new("INVALID_METADATA", sidecar, null, $"无法验证元数据：{ex.Message}");
        }
    }

    public static string InspectProjectSpatialReferenceStatus(string path)
    {
        if (!File.Exists(path)) return "UNVERIFIED";
        ArtifactIdentityInspection identity = Inspect(path);
        GeneratedArtifactMetadata? metadata = identity.Metadata;
        if (identity.Status != "VERIFIED" || metadata is null) return "UNVERIFIED";
        if (!ProductionSnapshotService.UsesProjectSpatialReference(metadata)) return "NOT_APPLICABLE";

        string savedFingerprint = metadata.ProjectSpatialReferenceFingerprint;
        if (string.IsNullOrWhiteSpace(metadata.ProjectId) || string.IsNullOrWhiteSpace(savedFingerprint) ||
            savedFingerprint == "UNAVAILABLE")
            return "UNVERIFIED";
        if (!TryReadProjectSpatialReference(path, metadata.WorkareaId, out string projectId, out string currentFingerprint))
            return "UNVERIFIED";
        if (!projectId.Equals(metadata.ProjectId, StringComparison.OrdinalIgnoreCase)) return "STALE";

        // An artifact created without a confirmed project meridian is not verified
        // while the project is also unconfirmed. Once a meridian is confirmed, that
        // artifact has a known stale basis and must be regenerated or reviewed.
        if (savedFingerprint == "UNCONFIRMED" && currentFingerprint == "UNCONFIRMED")
            return "UNVERIFIED";
        return savedFingerprint.Equals(currentFingerprint, StringComparison.Ordinal) ? "CURRENT" : "STALE";
    }

    private static bool TryReadProjectSpatialReference(string artifactPath, string? areaId, out string projectId,
        out string fingerprint)
    {
        projectId = "";
        fingerprint = "";
        string? directory = Path.GetDirectoryName(Path.GetFullPath(artifactPath));
        while (directory is not null && !MagOps.Infrastructure.Storage.ProjectPaths.HasProject(directory))
            directory = Directory.GetParent(directory)?.FullName;
        if (directory is null) return false;

        try
        {
            var workspace = new MagOps.Infrastructure.Storage.JsonStore().Read(
                MagOps.Infrastructure.Storage.ProjectPaths.ExistingProject(directory),
                new MagOps.Core.Domain.WorkspaceRecord());
            if (string.IsNullOrWhiteSpace(workspace.ProjectId)) return false;
            projectId = workspace.ProjectId;
            fingerprint = new WorkspaceService(new MagOps.Infrastructure.Storage.JsonStore()).SpatialFingerprint(directory, workspace, areaId);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    public static string InspectSourceFreshnessStatus(string path, IDictionary<string, string>? sourceHashCache = null)
    {
        ArtifactDependencyStatus independent = InspectIndependentFreshness(path, sourceHashCache);
        if (independent.Status != "NOT_APPLICABLE") return independent.Status;
        if (!File.Exists(path)) return "UNVERIFIED";
        ArtifactIdentityInspection identity = Inspect(path);
        GeneratedArtifactMetadata? metadata = identity.Metadata;
        if (identity.Status != "VERIFIED" || metadata is null) return "UNVERIFIED";
        bool analysisFingerprintBound = UsesFieldDataAnalysisFingerprint(metadata);
        bool sourceHashesBound = analysisFingerprintBound ||
                                 metadata.ProcessingStage.Equals("formal_survey_raw", StringComparison.OrdinalIgnoreCase);
        if (!sourceHashesBound)
        {
            string stage = metadata.ProcessingStage;
            return stage.StartsWith("fieldwork_", StringComparison.OrdinalIgnoreCase) ||
                stage is "field_daily_progress" or "field_project_progress" or "field_progress_preview" or
                    "field_takeoff_coordinates" or "field_takeoff_gpx" or "field_takeoff_connection_gpx"
                ? "UNVERIFIED" : "NOT_APPLICABLE";
        }
        if (string.IsNullOrWhiteSpace(metadata.ProjectId) || metadata.SourceFiles.Count == 0)
            return "UNVERIFIED";

        string? projectRoot = Path.GetDirectoryName(Path.GetFullPath(path));
        while (projectRoot is not null && !MagOps.Infrastructure.Storage.ProjectPaths.HasProject(projectRoot))
            projectRoot = Directory.GetParent(projectRoot)?.FullName;
        if (projectRoot is null) return "UNVERIFIED";

        try
        {
            var store = new MagOps.Infrastructure.Storage.JsonStore();
            var workspace = store.Read(MagOps.Infrastructure.Storage.ProjectPaths.ExistingProject(projectRoot),
                new MagOps.Core.Domain.WorkspaceRecord());
            if (string.IsNullOrWhiteSpace(workspace.ProjectId)) return "UNVERIFIED";
            if (!workspace.ProjectId.Equals(metadata.ProjectId, StringComparison.OrdinalIgnoreCase)) return "STALE";

            IReadOnlyList<FieldDataConsistencyReport> reports =
                new FieldDataImportService(store).ListConsistencyReports(projectRoot);
            if (metadata.SourceFiles.Any(source => string.IsNullOrWhiteSpace(source) || !Path.IsPathRooted(source)))
                return "UNVERIFIED";
            string[] sourcePaths = metadata.SourceFiles
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            FieldDataConsistencyReport? report = reports
                .Where(candidate => !string.IsNullOrWhiteSpace(candidate.SourceDirectory))
                .Where(candidate =>
                {
                    string directory = FieldDataImportService.NormalizeSourceDirectory(candidate.SourceDirectory);
                    string prefix = directory.EndsWith(Path.DirectorySeparatorChar) ||
                                    directory.EndsWith(Path.AltDirectorySeparatorChar)
                        ? directory : directory + Path.DirectorySeparatorChar;
                    return sourcePaths.All(source =>
                        source.Equals(directory, StringComparison.OrdinalIgnoreCase) ||
                        source.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
                })
                .OrderByDescending(candidate => Path.GetFullPath(candidate.SourceDirectory).Length)
                .FirstOrDefault();
            if (report is null) return "UNVERIFIED";
            Dictionary<string, string> knownHashes = report.SourceFileHashes
                .Where(x => !string.IsNullOrWhiteSpace(x.Key) && !string.IsNullOrWhiteSpace(x.Value))
                .GroupBy(x => Path.GetFullPath(x.Key), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.Last().Value, StringComparer.OrdinalIgnoreCase);
            foreach (string source in sourcePaths)
            {
                if (!knownHashes.TryGetValue(source, out string? expectedHash)) return "UNVERIFIED";
                if (!File.Exists(source)) return "STALE";
                string actualHash;
                if (sourceHashCache is not null)
                {
                    if (!sourceHashCache.TryGetValue(source, out actualHash!))
                        sourceHashCache[source] = actualHash = Hash(source);
                }
                else actualHash = Hash(source);
                if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase)) return "STALE";
            }
            if (report.RequiresFullReparse ||
                (analysisFingerprintBound &&
                !report.AnalysisFingerprint.Equals(metadata.SourceFingerprint, StringComparison.OrdinalIgnoreCase))
               )
                return "STALE";
            return "CURRENT";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException)
        {
            return "UNVERIFIED";
        }
    }

    public static ArtifactDependencyStatus InspectIndependentFreshness(string path, IDictionary<string, string>? cache = null)
    {
        if (!File.Exists(path)) return new("UNVERIFIED", "成果文件缺失。");
        var identity = Inspect(path);
        if (identity.Status == "HASH_MISMATCH") return new("STALE", "成果内容已改变，与身份文件不一致。");
        if (identity.Status != "VERIFIED" || identity.Metadata is null)
            return new("UNVERIFIED", identity.Warning ?? "缺少可验证的身份文件。");
        var metadata = identity.Metadata;
        if (metadata.SchemaVersion < 3) return new("NOT_APPLICABLE", "旧版来源合同。");
        if (metadata.DependencyContract != ArtifactDependencies.Contract)
            return new("UNVERIFIED", "不支持的来源合同版本。");
        string? root = ArtifactDependencies.FindRoot(path);
        if (root is null) return new("UNVERIFIED", "无法定位原项目。");
        try
        {
            var workspace = new MagOps.Infrastructure.Storage.JsonStore().Read(
                MagOps.Infrastructure.Storage.ProjectPaths.ExistingProject(root), new MagOps.Core.Domain.WorkspaceRecord());
            if (workspace.ProjectId != metadata.ProjectId) return new("STALE", "项目身份不匹配。");
            if (metadata.ContextStatus == "WORKAREA" &&
                !new WorkspaceService(new MagOps.Infrastructure.Storage.JsonStore()).GetWorkareas(root)
                    .Any(x => x.WorkareaId == metadata.WorkareaId))
                return new("STALE", "工区身份已缺失。");
            if (string.IsNullOrWhiteSpace(metadata.ExportTransactionId) ||
                IndependentArtifactTransaction.IsPending(root, metadata.ExportTransactionId))
                return new("UNVERIFIED", "成果提交尚待恢复或核对。");
            return ArtifactDependencies.Check(root, metadata.Dependencies, cache);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { return new("UNVERIFIED", "来源上下文不可读：" + ex.GetType().Name); }
    }

    private static bool UsesFieldDataAnalysisFingerprint(GeneratedArtifactMetadata metadata)
    {
        if (metadata.Lineage?.SourceFileStableIds?.Any(x => !string.IsNullOrWhiteSpace(x)) == true)
            return true;
        string stage = metadata.ProcessingStage;
        return stage.Equals("diurnal_only", StringComparison.OrdinalIgnoreCase) ||
               stage.StartsWith("diurnal_", StringComparison.OrdinalIgnoreCase) ||
               stage.StartsWith("prelevel_dtgz", StringComparison.OrdinalIgnoreCase) ||
               stage.StartsWith("audit_execution", StringComparison.OrdinalIgnoreCase) ||
               stage.StartsWith("production_", StringComparison.OrdinalIgnoreCase) ||
               stage.StartsWith("line_", StringComparison.OrdinalIgnoreCase) ||
               stage.StartsWith("repeat_", StringComparison.OrdinalIgnoreCase) ||
               stage.StartsWith("t0_", StringComparison.OrdinalIgnoreCase) ||
               stage.Equals("manual_review", StringComparison.OrdinalIgnoreCase) ||
               stage.Equals("audit_visualization", StringComparison.OrdinalIgnoreCase) ||
               stage.StartsWith("field_data_", StringComparison.OrdinalIgnoreCase) ||
               stage.Equals("field_gemlink_bridge", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPreviousVersion(string path, string artifactId)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        while (directory is not null)
        {
            if (directory.EndsWith(".previous", StringComparison.OrdinalIgnoreCase))
            {
                string current = Path.Combine(directory[..^".previous".Length], Path.GetRelativePath(directory, path));
                // Both content/metadata pairs must verify; a directory name alone is not identity evidence.
                if (File.Exists(current) && Inspect(current) is { Status: "VERIFIED", Metadata: not null } identity &&
                    identity.Metadata.ArtifactId == artifactId)
                    return true;
            }
            directory = Path.GetDirectoryName(directory);
        }
        return false;
    }

    public static bool IsHistoricalBundlePath(string path)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        while (directory is not null)
        {
            if (directory.EndsWith(".previous", StringComparison.OrdinalIgnoreCase) && Directory.Exists(directory))
            {
                string manifest = Path.Combine(directory, TaggedName("日变改正_manifest.json", "diurnal_manifest"));
                if (Inspect(manifest) is { IsHistoricalVersion: true, Metadata.ProcessingStage: "diurnal_manifest" })
                    return true;
            }
            directory = Path.GetDirectoryName(directory);
        }
        return false;
    }

    public static GeneratedArtifactMetadata Write(string path, string role, string stage,
        string sourceFingerprint, IReadOnlyList<string> sourceFiles, string? artifactId = null,
        string qualityStatus = "UNREVIEWED", GeneratedArtifactContext? context = null,
        GeneratedArtifactLineage? lineage = null,
        string productionRequestFingerprint = "", string productionSnapshotFingerprint = "",
        IReadOnlyList<ArtifactDependency>? dependencies = null, JsonElement? exportOptions = null,
        string exportTransactionId = "")
    {
        context ??= GeneratedArtifactContext.Unavailable;
        var metadata = new GeneratedArtifactMetadata(dependencies is null ? 2 : 3, artifactId ?? "artifact_" + Guid.NewGuid().ToString("N"),
            "MagOps", role, stage, Path.GetFileName(path), Hash(path), DateTimeOffset.UtcNow,
            sourceFingerprint, sourceFiles,
            ProjectId: string.IsNullOrWhiteSpace(context.ProjectId) ? null : context.ProjectId.Trim(),
            WorkareaId: string.IsNullOrWhiteSpace(context.WorkareaId) ? null : context.WorkareaId.Trim(),
            ContextStatus: context.ContextStatus,
            QualityStatus: qualityStatus,
            Lineage: lineage ?? GeneratedArtifactLineage.Empty)
        {
            Dependencies = dependencies,
            ExportOptions = exportOptions,
            DependencyContract = dependencies is null ? "" : ArtifactDependencies.Contract,
            ExportTransactionId = exportTransactionId,
            ProductionRequestFingerprint = productionRequestFingerprint,
            ProductionSnapshotFingerprint = productionSnapshotFingerprint,
            ProductionAlgorithmVersion = string.IsNullOrEmpty(productionRequestFingerprint) ? "" : ProductionAnomalyDetector.Version,
            ProjectSpatialReferenceFingerprint = (context.ContextStatus is "PROJECT" or "WORKAREA") &&
                                                 !string.IsNullOrWhiteSpace(context.ProjectId)
                ? ProjectSpatialReferenceFingerprintForArtifact(path, context.ProjectId, context.WorkareaId)
                : ""
        };
        if (dependencies is null) File.WriteAllText(path + Suffix, JsonSerializer.Serialize(metadata, Options));
        else AtomicFileWriter.Write(path + Suffix, stream => JsonSerializer.Serialize(stream, metadata, Options));
        return metadata;
    }

    private static string ProjectSpatialReferenceFingerprintForArtifact(string path, string projectId, string? areaId)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        while (directory is not null && !MagOps.Infrastructure.Storage.ProjectPaths.HasProject(directory))
            directory = Directory.GetParent(directory)?.FullName;
        if (directory is null) return "UNAVAILABLE";
        var workspace = new MagOps.Infrastructure.Storage.JsonStore().Read(
            MagOps.Infrastructure.Storage.ProjectPaths.ExistingProject(directory),
            new MagOps.Core.Domain.WorkspaceRecord());
        return workspace.ProjectId.Equals(projectId, StringComparison.OrdinalIgnoreCase)
            ? new WorkspaceService(new MagOps.Infrastructure.Storage.JsonStore()).SpatialFingerprint(directory, workspace, areaId)
            : "PROJECT_ID_MISMATCH";
    }
}
