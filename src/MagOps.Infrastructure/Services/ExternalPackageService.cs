using System.Text.Json;
using MagOps.Core.Domain;
using MagOps.Infrastructure.Storage;

namespace MagOps.Infrastructure.Services;

public sealed record ExternalPackageDeclaration(string Scope, string WorkareaId, string WorkDate, string ProcessingStage,
    string Tool, string ToolVersion, string MethodEvidence, string Crs, string HeightDatum, string CreatedBy,
    List<string> SourceArtifactIds, string ReviewEvidence = "");
public sealed record ExternalPackageFile(string RelativePath, long Length, string Sha256, string ArtifactId);
public sealed record ExternalPackageSource(string ArtifactId, string RelativePath, string Sha256);
public sealed record ExternalPackageManifest(string Schema, string PackageId, string ProjectId, string ProjectTimeZone,
    ExternalPackageDeclaration Declaration, DateTimeOffset RegisteredAt, IReadOnlyList<ExternalPackageFile> Files,
    IReadOnlyList<ExternalPackageSource> Sources)
{
    public string Status => "REVIEW_REQUIRED";
    public bool InternalReferencesVerified => false;
    public bool ProductionEligible => false;
}
public sealed record ExternalPackageInspection(string PackageId, string Status, IReadOnlyList<string> Issues)
{
    public bool InternalReferencesVerified => false;
    public bool ProductionEligible => false;
}

/// <summary>Archives a complete explicitly selected file family, without invoking external software.</summary>
public static class ExternalPackageService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
    public static ExternalPackageDeclaration ReadDeclaration(string path) => JsonSerializer.Deserialize<ExternalPackageDeclaration>(File.ReadAllText(path), Json) ?? throw new ArgumentException("外部成果声明为空。");
    public static ExternalPackageManifest Preview(string root, string input, ExternalPackageDeclaration declaration, CancellationToken token = default)
    {
        root = Path.GetFullPath(root); input = Path.GetFullPath(input); ProjectChangePreview.RejectLinks(root);
        var store = new JsonStore(); var workspace = new WorkspaceService(store).GetWorkspaceReadOnly(root);
        if (declaration.Scope is not ("project" or "workarea" or "day") || new[] { declaration.ProcessingStage, declaration.Tool, declaration.ToolVersion, declaration.MethodEvidence, declaration.Crs, declaration.HeightDatum, declaration.CreatedBy }.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("需声明范围、阶段、工具版本、方法依据、坐标/高程基准及登记人；未知基准请明确填“未确认”。");
        var workareas = new WorkspaceService(store).GetWorkareas(root);
        if (declaration.Scope == "project" ? !string.IsNullOrWhiteSpace(declaration.WorkareaId) : !workareas.Any(w => w.WorkareaId == declaration.WorkareaId))
            throw new ArgumentException("项目包不指定工区；工区/日包必须引用已有 WorkareaId。");
        if (declaration.Scope == "day" && !DateOnly.TryParseExact(declaration.WorkDate, "yyyy-MM-dd", out _)) throw new ArgumentException("日包需要明确作业日 yyyy-MM-dd；项目时区取项目登记。");
        var artifacts = new ArtifactService(store, new WorkspaceService(store)).List(root); var sources = new List<ExternalPackageSource>();
        foreach (string id in (declaration.SourceArtifactIds ?? []).Distinct())
        {
            var row = artifacts.SingleOrDefault(a => a.ArtifactId == id) ?? throw new ArgumentException("源成果身份不存在：" + id);
            if (declaration.Scope != "project" && row.WorkareaRef.Length > 0 && row.WorkareaRef != declaration.WorkareaId) throw new ArgumentException("源成果属于其他工区。");
            string path = ProjectChangePreview.Within(root, row.Path);
            sources.Add(new(id, row.Path, GeneratedArtifactIdentity.Hash(path)));
        }
        if (declaration.Scope == "project" && (sources.Count == 0 || sources.Any(s => string.IsNullOrWhiteSpace(artifacts.Single(a => a.ArtifactId == s.ArtifactId).WorkareaRef))))
            throw new ArgumentException("项目汇总包需显式引用具备工区身份的来源成果。");
        var files = ProjectChangePreview.EnumerateSafeFiles(input, token).Select(path =>
        {
            token.ThrowIfCancellationRequested(); return new ExternalPackageFile(Path.GetRelativePath(input, path).Replace('\\', '/'), new FileInfo(path).Length, GeneratedArtifactIdentity.Hash(path), "external-" + Guid.NewGuid().ToString("N"));
        }).ToArray();
        if (files.Length == 0) throw new ArgumentException("外部文件族目录为空。");
        return new("magops-external-package-1", "package-" + Guid.NewGuid().ToString("N"), workspace.ProjectId, workspace.TimeZoneId, declaration, DateTimeOffset.UtcNow, files, sources);
    }
    public static string Register(string root, string input, string output, ExternalPackageDeclaration declaration, CancellationToken token = default, ExternalPackageManifest? expected = null)
    {
        root = Path.GetFullPath(root); input = Path.GetFullPath(input); output = Path.GetFullPath(output);
        output = ProjectChangePreview.Within(root, Path.GetRelativePath(root, output));
        if (output.Equals(input, StringComparison.OrdinalIgnoreCase) || output.StartsWith(input.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("输出不可位于源文件族目录内。");
        if (Directory.Exists(output) || File.Exists(output)) throw new ArgumentException("输出目录必须不存在；不覆盖已有成果包。");
        using var guard = WorkspaceFileLock.AcquireForRoot(root);
        var manifest = Preview(root, input, declaration, token);
        if (expected is not null && (expected.ProjectId != manifest.ProjectId || JsonSerializer.Serialize(expected.Declaration, Json) != JsonSerializer.Serialize(declaration, Json) ||
            !expected.Files.Select(f => (f.RelativePath, f.Length, f.Sha256)).SequenceEqual(manifest.Files.Select(f => (f.RelativePath, f.Length, f.Sha256))) ||
            !expected.Sources.SequenceEqual(manifest.Sources))) throw new InvalidDataException("成果包或来源在预览后发生变化，请重新预览。");
        string staging = output + "." + Guid.NewGuid().ToString("N") + ".pending"; Directory.CreateDirectory(staging);
        bool moved = false, registered = false;
        try
        {
            foreach (var file in manifest.Files)
            {
                token.ThrowIfCancellationRequested(); string target = ProjectChangePreview.Within(Path.Combine(staging, "files"), file.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(ProjectChangePreview.Within(input, file.RelativePath), target);
                if (GeneratedArtifactIdentity.Hash(target) != file.Sha256) throw new InvalidDataException("复制期间外部成果发生变化。");
            }
            var finalNames = ProjectChangePreview.EnumerateSafeFiles(input, token).Select(p => Path.GetRelativePath(input, p).Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!finalNames.SetEquals(manifest.Files.Select(f => f.RelativePath)) || manifest.Files.Any(f => GeneratedArtifactIdentity.Hash(ProjectChangePreview.Within(input, f.RelativePath)) != f.Sha256) || manifest.Sources.Any(s => GeneratedArtifactIdentity.Hash(ProjectChangePreview.Within(root, s.RelativePath)) != s.Sha256)) throw new InvalidDataException("登记期间文件族或来源发生变化，未提交。");
            string name = "external-package.json"; File.WriteAllText(Path.Combine(staging, name), JsonSerializer.Serialize(manifest, Json));
            var store = new JsonStore(); var rows = store.Read(ProjectPaths.Json(root, "artifacts.json"), new List<ArtifactRecord>());
            string manifestRelative = Path.GetRelativePath(root, Path.Combine(output, name)).Replace('\\', '/');
            foreach (var file in manifest.Files.Append(new(name, new FileInfo(Path.Combine(staging, name)).Length, GeneratedArtifactIdentity.Hash(Path.Combine(staging, name)), manifest.PackageId)))
            {
                string relative = file.ArtifactId == manifest.PackageId ? manifestRelative : Path.GetRelativePath(root, Path.Combine(output, "files", file.RelativePath)).Replace('\\', '/');
                if (rows.Any(r => r.Path.Equals(relative, StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("输出路径已有登记，禁止复用。");
                rows.Add(new ArtifactRecord { ArtifactId = file.ArtifactId, Path = relative, Name = Path.GetFileName(relative), Role = file.ArtifactId == manifest.PackageId ? "registry" : "external_software", SourceKind = "external_package", SourceRef = manifestRelative,
                    Sha256 = file.Sha256, HashStatus = "COMPUTED", SizeBytes = file.Length, ProcessingStage = declaration.ProcessingStage,
                    VersionFamily = manifest.PackageId, QualityStatus = "REVIEW_REQUIRED", Selected = false, WorkareaRef = declaration.WorkareaId,
                    WorkareaAssignmentStatus = declaration.Scope == "project" ? "PROJECT" : "ASSIGNED", MetadataOrigin = "external_package",
                    DerivedFrom = manifest.Sources.Select(s => s.ArtifactId).ToList(), Notes = "完整文件族登记；内部引用未验证；哈希完整不代表质量通过。" });
            }
            token.ThrowIfCancellationRequested(); Directory.Move(staging, output); moved = true;
            store.Write(ProjectPaths.Json(root, "artifacts.json"), rows); registered = true;
            return Path.Combine(output, name);
        }
        finally
        {
            if (moved && !registered && Directory.Exists(output)) Directory.Move(output, staging);
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
    }
    public static ExternalPackageInspection Inspect(string root, string manifestPath)
    {
        root = Path.GetFullPath(root); manifestPath = ProjectChangePreview.Within(root, Path.GetRelativePath(root, manifestPath));
        var m = JsonSerializer.Deserialize<ExternalPackageManifest>(File.ReadAllText(manifestPath), Json) ?? throw new InvalidDataException("成果包清单为空。");
        if (m.Schema != "magops-external-package-1" || m.ProjectId != new WorkspaceService(new JsonStore()).GetWorkspaceReadOnly(root).ProjectId) throw new InvalidDataException("成果包版本或项目身份不符。");
        var ledger = new JsonStore().Read(ProjectPaths.Json(root, "artifacts.json"), new List<ArtifactRecord>());
        var registered = ledger.SingleOrDefault(a => a.ArtifactId == m.PackageId && a.Path.Equals(Path.GetRelativePath(root, manifestPath).Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));
        if (registered is null || GeneratedArtifactIdentity.Hash(manifestPath) != registered.Sha256) throw new InvalidDataException("成果包清单未登记或已被修改，不能据此确认文件完整性。");
        var issues = new List<string>(); bool missing = false, changed = false;
        foreach (var file in m.Files.Select(f => (Path: ProjectChangePreview.Within(Path.Combine(Path.GetDirectoryName(manifestPath)!, "files"), f.RelativePath), f.Sha256)).Concat(m.Sources.Select(s => (Path: ProjectChangePreview.Within(root, s.RelativePath), s.Sha256))))
        {
            if (!File.Exists(file.Path)) { missing = true; issues.Add("文件缺失：" + Path.GetRelativePath(root, file.Path)); }
            else if (GeneratedArtifactIdentity.Hash(file.Path) != file.Sha256) { changed = true; issues.Add("文件变化：" + Path.GetRelativePath(root, file.Path)); }
        }
        string familyRoot = Path.Combine(Path.GetDirectoryName(manifestPath)!, "files");
        if (Directory.Exists(familyRoot) && ProjectChangePreview.EnumerateSafeFiles(familyRoot).Select(p => Path.GetRelativePath(familyRoot, p).Replace('\\', '/')).Except(m.Files.Select(f => f.RelativePath), StringComparer.OrdinalIgnoreCase).Any())
        { changed = true; issues.Add("文件族新增了未登记文件。"); }
        issues.Add("内部引用未验证；外部质量尚需复核。");
        return new(m.PackageId, missing ? "MISSING" : changed ? "STALE" : "REVIEW_REQUIRED", issues);
    }
}
