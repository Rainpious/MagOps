using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MagOps.Core.Domain;
using MagOps.Infrastructure.Storage;

namespace MagOps.Infrastructure.Services;

public sealed class RouteResultService
{
    public const string RouteResultSchemaVersion = "route-result-1";
    private static readonly HashSet<string> ResultStatuses =
        new(StringComparer.OrdinalIgnoreCase) { "PASSED", "REVIEW_REQUIRED", "BLOCKED", "ERROR" };
    private static readonly Regex HashRegex =
        new("^[0-9A-Fa-f]{64}$", RegexOptions.Compiled);

    private readonly JsonStore _store;
    private readonly WorkspaceService _workspace;
    private readonly ArtifactService _artifacts;

    public RouteResultService(JsonStore store, WorkspaceService workspace, ArtifactService artifacts)
    {
        _store = store;
        _workspace = workspace;
        _artifacts = artifacts;
    }

    public List<RouteResultRecord> List(string root) =>
        _store.Read(ProjectPaths.Json(root, "route_results.json"), new List<RouteResultRecord>());

    public RouteResultRecord Import(string root, string manifestPath, bool confirmGate = false, string confirmedBy = "")
    {
        using var writeLock = WorkspaceFileLock.AcquireForRoot(root);
        var workspace = _workspace.GetWorkspace(root);
        string rootPath = Path.GetFullPath(root);
        manifestPath = Path.GetFullPath(manifestPath);
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException("航线结果清单不存在", manifestPath);

        if (JsonNode.Parse(File.ReadAllText(manifestPath)) is not JsonObject payload)
            throw new InvalidDataException("航线结果必须是 JSON 对象");
        ValidatePayload(payload);
        string manifestProjectId = payload["project_id"]?.GetValue<string>()?.Trim() ?? "";
        if (!string.IsNullOrWhiteSpace(manifestProjectId) &&
            !manifestProjectId.Equals(workspace.ProjectId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"航线结果 ProjectId 与当前项目不一致：{manifestProjectId}。");
        }

        string manifestHash = ArtifactService.Sha256(manifestPath).ToUpperInvariant();
        string resultId = "route-result-" + manifestHash[..16].ToLowerInvariant();
        var verificationErrors = new List<string>();

        var sources = StringArray(payload["source_paths"]);
        var outputs = StringArray(payload["output_paths"]);
        var inputHashes = StringMap(payload["input_sha256"]);
        var outputHashes = StringMap(payload["output_sha256"]);

        foreach (var (rawPath, expected) in inputHashes)
        {
            string resolved = Resolve(rawPath, manifestPath);
            if (!File.Exists(resolved))
                verificationErrors.Add($"输入文件不存在: {resolved}");
            else if (!ArtifactService.Sha256(resolved).Equals(expected, StringComparison.OrdinalIgnoreCase))
                verificationErrors.Add($"输入文件哈希不一致: {resolved}");
        }

        foreach (string rawPath in outputs)
        {
            string resolved = Resolve(rawPath, manifestPath);
            if (!File.Exists(resolved))
                verificationErrors.Add($"输出文件不存在: {resolved}");
        }

        foreach (var (rawPath, expected) in outputHashes)
        {
            string resolved = Resolve(rawPath, manifestPath);
            if (File.Exists(resolved)
                && !ArtifactService.Sha256(resolved).Equals(expected, StringComparison.OrdinalIgnoreCase))
                verificationErrors.Add($"输出文件哈希不一致: {resolved}");
        }

        string payloadStatus = payload["status"]!.GetValue<string>().ToUpperInvariant();
        string importedStatus =
            verificationErrors.Count > 0 || payloadStatus is "BLOCKED" or "ERROR"
                ? "BLOCKED"
                : payloadStatus;

        string confirmer = string.IsNullOrWhiteSpace(confirmedBy)
            ? payload["confirmed_by"]?.GetValue<string>()?.Trim() ?? ""
            : confirmedBy.Trim();

        if (confirmGate && importedStatus != "PASSED")
            throw new InvalidOperationException($"只有 PASSED 航线结果可以确认门禁，当前状态: {importedStatus}");
        if (confirmGate && confirmer.Length == 0)
            throw new InvalidOperationException("确认航线门禁必须填写确认人");

        string workareaId = payload["workarea_id"]?.GetValue<string>()?.Trim() ?? "";
        string legacySubareaId = payload["subarea_id"]?.GetValue<string>()?.Trim() ?? "";
        bool legacyWorkareaFallback = workareaId.Length == 0 && legacySubareaId.Length > 0;
        if (legacyWorkareaFallback)
            workareaId = legacySubareaId;

        var record = new RouteResultRecord
        {
            ResultId = resultId,
            ManifestPath = manifestPath,
            ManifestSha256 = manifestHash,
            SchemaVersion = payload["schema_version"]!.GetValue<string>(),
            AlgorithmVersion = payload["algorithm_version"]!.GetValue<string>().Trim(),
            CreatedAt = payload["created_at"]?.GetValue<string>() ?? "",
            Command = payload["command"]!.GetValue<string>().Trim(),
            Status = importedStatus,
            ProjectId = NonEmpty(
                payload["project_id"]?.GetValue<string>(),
                workspace.ProjectId,
                workspace.WorkspaceId),
            WorkareaId = workareaId,
            SubareaId = workareaId,
            RouteId = payload["route_id"]?.GetValue<string>()?.Trim() ?? "",
            SortieId = payload["sortie_id"]?.GetValue<string>()?.Trim() ?? "",
            SourcePaths = sources,
            InputSha256 = inputHashes,
            Parameters = ElementMap(payload["parameters"]),
            Summary = ElementMap(payload["summary"]),
            Warnings = StringArray(payload["warnings"]),
            Errors = [.. StringArray(payload["errors"]), .. verificationErrors],
            OutputPaths = outputs,
            OutputSha256 = outputHashes,
            ConfirmedBy = confirmer,
            ConfirmedAt = payload["confirmed_at"]?.GetValue<string>() ?? "",
            ImportedAt = WorkspaceService.Now()
        };

        var routeRows = List(root);
        routeRows.RemoveAll(x => x.ResultId == resultId);
        routeRows.Add(record);

        var artifactRows = _artifacts.List(root);
        List<WorkareaRecord> workareas = _workspace.GetWorkareas(root);
        var byPath = artifactRows
            .Where(x => !string.IsNullOrWhiteSpace(x.Path))
            .ToDictionary(x => x.Path, StringComparer.OrdinalIgnoreCase);
        var registered = new List<string>();

        foreach (string rawPath in outputs)
        {
            string output = Resolve(rawPath, manifestPath);
            if (!File.Exists(output)) continue;

            if (!TryRelativeWithinRoot(rootPath, output, out string relative))
            {
                record.Warnings.Add($"输出位于项目根目录外，仅登记结果、不登记 Artifact: {output}");
                continue;
            }

            byPath.TryGetValue(relative, out var existing);
            ArtifactIdentityInspection outputIdentity = GeneratedArtifactIdentity.Inspect(output);
            if (outputIdentity is { Status: "VERIFIED", Metadata: not null })
            {
                GeneratedArtifactMetadata metadata = outputIdentity.Metadata;
                if (!string.IsNullOrWhiteSpace(metadata.ProjectId) &&
                    !metadata.ProjectId.Equals(workspace.ProjectId, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"航线成果 sidecar ProjectId 与当前项目不一致：{output}");
                if (!string.IsNullOrWhiteSpace(workareaId) &&
                    metadata.ContextStatus.Equals("WORKAREA", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(metadata.WorkareaId, workareaId, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"航线成果 sidecar WorkareaId 与结果清单不一致：{output}");
                if (metadata.ContextStatus.Equals("WORKAREA", StringComparison.OrdinalIgnoreCase) &&
                    (string.IsNullOrWhiteSpace(metadata.WorkareaId) ||
                     !workareas.Any(area =>
                         area.WorkareaId.Equals(metadata.WorkareaId, StringComparison.OrdinalIgnoreCase))))
                    throw new InvalidDataException($"航线成果 sidecar 引用未知 canonical WorkareaId：{output}");

                if (artifactRows.Any(x =>
                        x.ArtifactId.Equals(metadata.ArtifactId, StringComparison.OrdinalIgnoreCase) &&
                        !x.Path.Equals(relative, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException(
                        $"航线成果 ArtifactId 已登记在其他路径，禁止建立第二事实源：{metadata.ArtifactId}");

                var infoVerified = new FileInfo(output);
                GeneratedArtifactLineage lineage = metadata.Lineage ?? GeneratedArtifactLineage.Empty;
                var registeredIdentity = new ArtifactRecord
                {
                    ArtifactId = metadata.ArtifactId,
                    WorkareaRef = metadata.ContextStatus.Equals("WORKAREA", StringComparison.OrdinalIgnoreCase)
                        ? metadata.WorkareaId ?? "" : "",
                    WorkareaAssignmentStatus = metadata.ContextStatus.Equals("WORKAREA", StringComparison.OrdinalIgnoreCase)
                        ? "ASSIGNED" : "PROJECT",
                    PreviousPaths = existing?.PreviousPaths?.ToList() ?? [],
                    Path = relative,
                    Name = infoVerified.Name,
                    Role = metadata.Role,
                    SourceKind = "route_result",
                    SourceRef = resultId,
                    Sha256 = metadata.ContentSha256,
                    HashStatus = "COMPUTED",
                    SizeBytes = infoVerified.Length,
                    ModifiedAt = infoVerified.LastWriteTimeUtc.ToString("O"),
                    ProcessingStage = metadata.ProcessingStage,
                    VersionFamily = existing?.VersionFamily ?? "",
                    VersionLabel = existing?.VersionLabel ?? "original",
                    Selected = importedStatus == "PASSED",
                    QualityStatus = importedStatus,
                    DerivedFrom = sources.ToList(),
                    SourceFileStableIds = lineage.SourceFileStableIds.ToList(),
                    GsmSessionStableIds = lineage.GsmSessionStableIds.ToList(),
                    SurveyTaskStableIds = lineage.SurveyTaskStableIds.ToList(),
                    SurveyLineStableIds = lineage.SurveyLineStableIds.ToList(),
                    ExecutionSegmentStableIds = lineage.ExecutionSegmentStableIds.ToList(),
                    Notes = existing?.Notes ?? "由带验证 sidecar 的航线结果接口登记",
                    ExternalOrigin = existing?.ExternalOrigin ?? "",
                    LineageRelation = existing?.LineageRelation ?? "",
                    MetadataOrigin = "route_result_verified_sidecar",
                    ChangeStatus = existing is null ? "NEW" : "UNCHANGED",
                    ScanStatus = "REGISTERED",
                    LastScannedAt = WorkspaceService.Now(),
                    Extra = existing?.Extra
                };
                UpsertArtifact(artifactRows, registeredIdentity);
                byPath[relative] = registeredIdentity;
                registered.Add(relative);
                continue;
            }

            var (family, label) = ArtifactService.VersionInfo(Path.GetFileName(output));
            var info = new FileInfo(output);
            var assignment = ResolveArtifactWorkarea(
                relative,
                workareaId,
                legacyWorkareaFallback,
                workareas,
                existing);

            var generated = new ArtifactRecord
            {
                ArtifactId = !string.IsNullOrWhiteSpace(existing?.ArtifactId)
                    ? existing!.ArtifactId
                    : NewArtifactId(),
                WorkareaRef = assignment.WorkareaRef,
                WorkareaAssignmentStatus = assignment.Status,
                PreviousPaths = existing?.PreviousPaths?.ToList() ?? [],
                Path = relative,
                Name = info.Name,
                Role = existing?.Role is { Length: > 0 } ? existing.Role : "route_result",
                SourceKind = "route_result",
                SourceRef = resultId,
                Sha256 = ArtifactService.Sha256(output),
                HashStatus = "COMPUTED",
                SizeBytes = info.Length,
                ModifiedAt = info.LastWriteTimeUtc.ToString("O"),
                ProcessingStage = existing?.ProcessingStage is { Length: > 0 } ? existing.ProcessingStage : "route",
                VersionFamily = existing?.VersionFamily is { Length: > 0 } ? existing.VersionFamily : family,
                VersionLabel = existing?.VersionLabel is { Length: > 0 } ? existing.VersionLabel : label,
                Selected = existing?.Selected ?? importedStatus == "PASSED",
                QualityStatus = importedStatus,
                DerivedFrom = sources.ToList(),
                Notes = existing?.Notes is { Length: > 0 } ? existing.Notes : "由航线结果接口登记",
                ExternalOrigin = existing?.ExternalOrigin ?? "",
                LineageRelation = existing?.LineageRelation ?? "",
                MetadataOrigin = existing?.MetadataOrigin is { Length: > 0 } ? existing.MetadataOrigin : "route_result",
                ChangeStatus = existing is null ? "NEW" : "UNCHANGED",
                ScanStatus = "REGISTERED",
                LastScannedAt = WorkspaceService.Now(),
                Extra = existing?.Extra
            };

            if (existing is null)
                artifactRows.Add(generated);
            else
                artifactRows[artifactRows.IndexOf(existing)] = generated;
            byPath[relative] = generated;
            registered.Add(relative);
        }

        _store.Write(ProjectPaths.Json(root, "artifacts.json"), artifactRows.OrderBy(x => x.Path).ToList());
        _store.Write(ProjectPaths.Json(root, "route_results.json"), routeRows);

        if (confirmGate)
        {
            _workspace.SetGate(
                root,
                "route",
                "PASSED",
                $"导入并核验航线结果 {resultId}",
                [manifestPath, .. registered],
                confirmer);
        }

        _store.AppendJsonLine(ProjectPaths.DataDirectory(root), "route_result_imported",
            new
            {
                result_id = resultId,
                status = importedStatus,
                manifest_path = manifestPath,
                registered_artifacts = registered,
                gate_confirmed = confirmGate
            });

        return record;
    }

    private static void ValidatePayload(JsonObject payload)
    {
        string schema = payload["schema_version"]?.GetValue<string>() ?? "";
        if (schema != RouteResultSchemaVersion)
            throw new InvalidDataException($"不支持的航线结果 schema_version: {schema}");

        if (string.IsNullOrWhiteSpace(payload["algorithm_version"]?.GetValue<string>()))
            throw new InvalidDataException("航线结果缺少 algorithm_version");
        if (string.IsNullOrWhiteSpace(payload["command"]?.GetValue<string>()))
            throw new InvalidDataException("航线结果缺少 command");

        string status = payload["status"]?.GetValue<string>() ?? "";
        if (!ResultStatuses.Contains(status))
            throw new InvalidDataException($"无效航线结果状态: {status}");

        foreach (string name in new[] { "source_paths", "warnings", "errors", "output_paths" })
            if (payload[name] is not JsonArray)
                throw new InvalidDataException($"航线结果字段 {name} 必须是数组");

        foreach (string name in new[] { "input_sha256", "output_sha256", "parameters", "summary" })
            if (payload[name] is not JsonObject)
                throw new InvalidDataException($"航线结果字段 {name} 必须是对象");

        foreach (var pair in StringMap(payload["input_sha256"]).Concat(StringMap(payload["output_sha256"])))
            if (!HashRegex.IsMatch(pair.Value))
                throw new InvalidDataException($"输入/输出文件哈希无效: {pair.Key}");

        if (status == "PASSED" && StringArray(payload["errors"]).Count > 0)
            throw new InvalidDataException("PASSED 结果不能包含 errors");
    }

    private static string Resolve(string path, string manifestPath) =>
        Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(manifestPath)!, path));

    private static List<string> StringArray(JsonNode? node) =>
        node is JsonArray array
            ? array.Select(x => x?.GetValue<string>() ?? "").Where(x => x.Trim().Length > 0).Select(x => x.Trim()).ToList()
            : [];

    private static Dictionary<string, string> StringMap(JsonNode? node)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (node is not JsonObject obj) return result;
        foreach (var kv in obj)
            if (kv.Value is not null)
                result[kv.Key] = kv.Value.GetValue<string>().ToUpperInvariant();
        return result;
    }

    private static Dictionary<string, JsonElement> ElementMap(JsonNode? node)
    {
        var result = new Dictionary<string, JsonElement>();
        if (node is not JsonObject obj) return result;
        foreach (var kv in obj)
        {
            if (kv.Value is null) continue;
            using var doc = JsonDocument.Parse(kv.Value.ToJsonString());
            result[kv.Key] = doc.RootElement.Clone();
        }
        return result;
    }

    private static string NonEmpty(params string?[] values) =>
        values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim() ?? "";

    private static bool TryRelativeWithinRoot(string root, string path, out string relative)
    {
        string rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        string pathFull = Path.GetFullPath(path);
        if (!pathFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
        {
            relative = "";
            return false;
        }
        relative = Path.GetRelativePath(root, pathFull).Replace('\\', '/');
        return true;
    }

    private static (string WorkareaRef, string Status) ResolveArtifactWorkarea(
        string relativePath,
        string manifestWorkareaId,
        bool legacyWorkareaFallback,
        IReadOnlyList<WorkareaRecord> workareas,
        ArtifactRecord? existing)
    {
        if (!string.IsNullOrWhiteSpace(manifestWorkareaId))
        {
            WorkareaRecord? explicitArea = workareas.FirstOrDefault(x =>
                x.WorkareaId.Equals(manifestWorkareaId, StringComparison.OrdinalIgnoreCase) ||
                (legacyWorkareaFallback &&
                 x.SubareaId.Equals(manifestWorkareaId, StringComparison.OrdinalIgnoreCase)));
            if (explicitArea is not null)
                return (explicitArea.WorkareaId, "ASSIGNED");

            return ("", "REVIEW_REQUIRED");
        }

        if (existing is not null)
        {
            string status = (existing.WorkareaAssignmentStatus ?? "").Trim().ToUpperInvariant();
            if (status == "ASSIGNED" &&
                workareas.Any(x =>
                    x.WorkareaId.Equals(existing.WorkareaRef, StringComparison.OrdinalIgnoreCase)))
            {
                return (existing.WorkareaRef, "ASSIGNED");
            }
            if (status == "PROJECT")
                return ("", "PROJECT");
        }

        string normalized = relativePath.Replace('\\', '/').TrimStart('/');
        foreach (WorkareaRecord area in workareas
                     .Where(x =>
                         !string.IsNullOrWhiteSpace(x.WorkareaId) &&
                         !string.IsNullOrWhiteSpace(x.Path))
                     .OrderByDescending(x => x.Path.Length))
        {
            string prefix = area.Path.Replace('\\', '/').Trim('/');
            if (normalized.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
            {
                return (area.WorkareaId, "ASSIGNED");
            }
        }

        if (normalized.StartsWith("10_工区/", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("10_子工区/", StringComparison.OrdinalIgnoreCase))
        {
            return ("", "REVIEW_REQUIRED");
        }

        return ("", "PROJECT");
    }

    private static void UpsertArtifact(List<ArtifactRecord> rows, ArtifactRecord row)
    {
        int index = rows.FindIndex(x => x.Path.Equals(row.Path, StringComparison.OrdinalIgnoreCase));
        if (index < 0) rows.Add(row);
        else rows[index] = row;
    }

    private static string NewArtifactId() =>
        "art-" + Guid.NewGuid().ToString("N")[..20];
}
