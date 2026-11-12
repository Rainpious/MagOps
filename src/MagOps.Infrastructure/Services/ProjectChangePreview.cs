using System.Text.Json;
using MagOps.Infrastructure.Storage;

namespace MagOps.Infrastructure.Services;

public sealed record ProjectPathMapping(string OldPath, string NewPath);
public sealed record ProjectChangeRequest(string Operation, List<ProjectPathMapping> Mappings, string NewRoot = "");
public sealed record ProjectFilePreview(string OldPath, string NewPath, long Length, string Sha256, string ArtifactId, string FileFamilyId, bool TargetExists);
public sealed record ProjectChangeResult(string ProjectId, string Root, string Operation, string NewRoot,
    IReadOnlyList<ProjectFilePreview> Files, IReadOnlyList<string> ReferenceFiles, IReadOnlyList<string> Blockers)
{
    public bool ReadOnly => true;
    public bool CanExecute => false;
    public string Scope => "文件清单和已识别文本引用预览；不代表私有工程内部引用已验证，不移动或删除文件。";
}

/// <summary>Read-only preparation; deliberately has no execution method.</summary>
public static class ProjectChangePreview
{
    public static IReadOnlyList<string> EnumerateSafeFiles(string root, CancellationToken token = default)
    {
        root = Path.GetFullPath(root); RejectLinks(root);
        var pending = new Stack<string>(); pending.Push(root); var files = new List<string>();
        while (pending.TryPop(out string? directory))
            foreach (string path in Directory.EnumerateFileSystemEntries(directory))
            {
                token.ThrowIfCancellationRequested(); RejectLinks(path);
                if (Directory.Exists(path)) pending.Push(path); else files.Add(path);
                if (files.Count > 100000) throw new InvalidDataException("文件超过十万个，请缩小检查范围。");
            }
        return files.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    public static void RejectLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("路径包含重解析点，不能按普通目录处理：" + current);
    }
    public static string Within(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':')) throw new ArgumentException("必须提供项目内相对路径。");
        string full = TerrainLibraryService.SafePath(root, relative); RejectLinks(full); return full;
    }
    public static ProjectChangeResult Inspect(string root, ProjectChangeRequest request, CancellationToken token = default)
    {
        root = Path.GetFullPath(root);
        var store = new JsonStore(); var workspace = new WorkspaceService(store).GetWorkspaceReadOnly(root);
        if (request.Operation is not ("directory-mapping" or "root-rename" or "deactivate" or "archive" or "delete")) throw new ArgumentException("不支持的生命周期预览操作。");
        var rows = store.Read(ProjectPaths.Json(root, "artifacts.json"), new List<MagOps.Core.Domain.ArtifactRecord>());
        var blockers = new List<string> { "本预览不授权物理执行；须核对外部工程引用、活动写入和可恢复备份。" };
        var maps = (request.Mappings ?? []).Select(m => (Old: Within(root, m.OldPath), New: Within(root, m.NewPath))).ToArray();
        if (request.Operation == "directory-mapping" && maps.Length == 0) throw new ArgumentException("目录预览需要显式旧新路径映射。");
        foreach (var left in maps)
        {
            if (!Directory.Exists(left.Old)) blockers.Add("源目录不存在：" + left.Old);
            if (SameOrUnder(left.Old, left.New) || SameOrUnder(left.New, left.Old)) blockers.Add("映射源目标重叠：" + left.Old);
            foreach (var right in maps.Where(r => r != left))
                if (SameOrUnder(left.Old, right.Old) || SameOrUnder(left.New, right.New) || SameOrUnder(left.New, right.Old)) blockers.Add("映射相互重叠：" + left.Old);
        }
        string newRoot = "";
        if (request.Operation == "root-rename")
        {
            if (!Path.IsPathFullyQualified(request.NewRoot)) throw new ArgumentException("根目录改名需要完整目标路径。");
            newRoot = Path.GetFullPath(request.NewRoot); RejectLinks(newRoot);
            if (!string.Equals(Path.GetDirectoryName(root), Path.GetDirectoryName(newRoot), StringComparison.OrdinalIgnoreCase)) blockers.Add("根目录改名只接受同父目录目标。");
            if (Directory.Exists(newRoot) || File.Exists(newRoot)) blockers.Add("目标根目录已存在。");
        }
        var files = new List<ProjectFilePreview>(); var references = new List<string>();
        foreach (string path in EnumerateSafeFiles(root, token))
        {
            token.ThrowIfCancellationRequested(); string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            var matches = maps.Where(m => SameOrUnder(path, m.Old)).ToArray();
            string target = request.Operation == "root-rename" ? Path.Combine(newRoot, relative) : matches.Length == 1 ? Path.Combine(matches[0].New, Path.GetRelativePath(matches[0].Old, path)) : path;
            var row = rows.FirstOrDefault(a => a.Path.Equals(relative, StringComparison.OrdinalIgnoreCase));
            var info = new FileInfo(path);
            if (request.Operation != "directory-mapping" || matches.Length > 0)
                files.Add(new(relative, target, info.Length, GeneratedArtifactIdentity.Hash(path), row?.ArtifactId ?? "", row?.VersionFamily ?? "", !target.Equals(path, StringComparison.OrdinalIgnoreCase) && (File.Exists(target) || Directory.Exists(target))));
            if (new[] { ".json", ".kml", ".xml", ".txt", ".csv", ".md" }.Contains(Path.GetExtension(path).ToLowerInvariant()))
            {
                if (info.Length > 16 * 1024 * 1024) { blockers.Add("文本引用扫描超出 16 MB：" + relative); continue; }
                string text = File.ReadAllText(path).Replace("\\\\", "/").Replace('\\', '/');
                if (text.Contains(root.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase) || maps.Any(m => text.Contains(Path.GetRelativePath(root, m.Old).Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))) references.Add(relative);
            }
            else if (new[] { ".gdb", ".om", ".grd", ".mxd", ".qgz" }.Contains(Path.GetExtension(path).ToLowerInvariant())) blockers.Add("内部引用未验证：" + relative);
        }
        blockers.AddRange(files.Where(f => f.TargetExists).Select(f => "目标冲突，禁止覆盖：" + f.NewPath));
        return new(workspace.ProjectId, root, request.Operation, newRoot, files, references, blockers.Distinct().ToArray());
    }
    private static bool SameOrUnder(string path, string root) => path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(root.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
