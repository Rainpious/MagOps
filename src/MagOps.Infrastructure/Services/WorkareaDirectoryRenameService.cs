using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MagOps.Infrastructure.Storage;

namespace MagOps.Infrastructure.Services;

public sealed class WorkareaDirectoryRenamePlan
{
    public string ProjectRoot { get; internal init; } = "";
    public string WorkareaId { get; internal init; } = "";
    public string SourcePath { get; internal init; } = "";
    public string TargetPath { get; internal init; } = "";
    public string SourceRelativePath { get; internal init; } = "";
    public string TargetRelativePath { get; internal init; } = "";
    public IReadOnlyList<string> ChangedFiles { get; internal init; } = [];
    public IReadOnlyList<string> Blockers { get; internal init; } = [];
    public bool CanApply => Blockers.Count == 0;
    internal Dictionary<string, (byte[] Before, byte[] After)> Changes { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    internal string Inventory { get; init; } = "";
    internal string? UserContextPath { get; init; }
}

/// <summary>Same-parent workarea directory rename with preview, identity-preserving JSON rebinding and rollback.</summary>
public sealed class WorkareaDirectoryRenameService
{
    // Deterministic failure injection for transaction regression checks; unused by the application.
    internal Action<int>? BeforeWrite { get; set; }
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly HashSet<string> PathFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "path", "paths", "root", "projectroot", "workareapath", "currentworkareapath", "workarearelativepath",
        "csvpath", "kmlpath", "motherkmlpath", "endpointcsvpath", "routesourcepath", "sourcepath", "sourcefile",
        "sourcefiles", "outputpath", "outputpaths", "outputdirectory", "directory", "filepath", "files",
        "manifestpath", "partitionmanifestpath", "flighttrackpath", "sourcekml", "endpointcsv", "kml", "csv",
        "inputpath", "inputpaths", "inputfiles", "outputfile", "outputfiles", "outputdir", "previouspaths", "sourcepaths",
        "lastrouteprojectpath", "lastpartitionmanifestpath"
    };

    public WorkareaDirectoryRenamePlan Preview(string root, string workareaId, string newFolderName, string? userContextPath = null)
    {
        root = Path.GetFullPath(root);
        var workspace = new WorkspaceService(new JsonStore());
        _ = workspace.GetWorkspaceReadOnly(root);
        var row = workspace.GetWorkareas(root).SingleOrDefault(w => w.WorkareaId.Equals(workareaId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("请先选择已登记工区。");
        ValidateLeaf(newFolderName);
        string source = Path.GetFullPath(Path.Combine(root, row.Path));
        if (!IsWithin(root, source)) throw new InvalidDataException("工区目录必须位于项目根目录以内。");
        if (SamePath(source, ProjectPaths.DataDirectory(root)) || IsWithin(source, ProjectPaths.DataDirectory(root)) ||
            IsWithin(ProjectPaths.DataDirectory(root), source))
            throw new InvalidDataException("工区不能包含项目管理目录。");
        for (string? ancestor = root; ancestor is not null; ancestor = Path.GetDirectoryName(ancestor))
            if ((File.GetAttributes(ancestor) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("项目路径包含目录链接，不支持自动更名。");
        string target = Path.Combine(Path.GetDirectoryName(source)!, newFolderName);
        var blockers = new List<string>();
        foreach (string pending in PendingTransactions(root))
            blockers.Add($"存在待恢复的更名事务，请先恢复：{Path.GetFileName(pending)}");
        if (source.Equals(target, StringComparison.OrdinalIgnoreCase)) blockers.Add("新旧目录名相同（含仅大小写变化）。");
        if (!Directory.Exists(source)) blockers.Add("工区目录不存在；请先核对工区路径。");
        if (Directory.Exists(target) || File.Exists(target)) blockers.Add("目标路径已经存在，不能覆盖或合并目录。");
        string oldRelative = Path.GetRelativePath(root, source).Replace('\\', '/');
        string newRelative = Path.GetRelativePath(root, target).Replace('\\', '/');
        if (workspace.GetWorkareas(root).Any(w => w.WorkareaId != row.WorkareaId &&
            (SamePath(w.Path, newRelative) || IsWithin(source, Path.GetFullPath(Path.Combine(root, w.Path))) ||
             IsWithin(Path.GetFullPath(Path.Combine(root, w.Path)), source))))
            blockers.Add("存在路径重叠的其他工区，请先整理登记。");
        var changes = new Dictionary<string, (byte[] Before, byte[] After)>(StringComparer.OrdinalIgnoreCase);
        userContextPath = string.IsNullOrWhiteSpace(userContextPath) ? null : Path.GetFullPath(userContextPath);
        bool userContextIsLinked = userContextPath is not null && HasLinkedAncestor(userContextPath);
        if (userContextIsLinked) blockers.Add("用户偏好路径包含链接，不支持自动迁移。");
        var directories = new List<string>();
        var files = EnumerateProjectFiles(root, blockers, directories)
            .Concat(!userContextIsLinked && userContextPath is not null && File.Exists(userContextPath) ? [userContextPath] : Array.Empty<string>())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        string management = ProjectPaths.DataDirectory(root);
        foreach (string file in files.Where(f => f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
        {
            if (new FileInfo(file).Length > 16 * 1024 * 1024)
            {
                blockers.Add($"JSON 超过当前迁移上限，需专项处理：{Path.GetRelativePath(root, file)}");
                continue;
            }
            byte[] before = File.ReadAllBytes(file);
            JsonNode? node;
            try { node = JsonNode.Parse(Utf8.GetString(before).TrimStart('\uFEFF')); }
            catch (Exception ex) when (ex is JsonException or DecoderFallbackException)
            {
                blockers.Add($"JSON 无法解析：{Path.GetRelativePath(root, file)}");
                continue;
            }
            bool managed = IsWithin(management, file) || file.Equals(userContextPath, StringComparison.OrdinalIgnoreCase);
            bool route = node is JsonObject obj && obj["schema_version"]?.ToString().StartsWith("magops-route-project-", StringComparison.Ordinal) == true;
            bool partition = node is JsonObject subdivision && subdivision.ContainsKey("source_kml") && subdivision.ContainsKey("children");
            bool sidecar = file.EndsWith(GeneratedArtifactIdentity.Suffix, StringComparison.OrdinalIgnoreCase);
            if (!managed && !route && !partition && !sidecar)
            {
                if (NodeReferences(node, source, oldRelative))
                    blockers.Add($"尚不支持此 JSON 的路径迁移：{Path.GetRelativePath(root, file)}");
                continue;
            }
            bool changed = false;
            if (file.Equals(userContextPath, StringComparison.OrdinalIgnoreCase))
            {
                if (node is not JsonObject preferences || preferences["projects"] is not JsonObject projects)
                    blockers.Add("用户偏好文件结构不受支持，未迁移。 ");
                else
                    foreach (var state in projects.Select(p => p.Value).OfType<JsonObject>())
                        if (state["project_root"] is JsonValue projectRoot && projectRoot.TryGetValue<string>(out var savedRoot) &&
                            SamePath(savedRoot, root)) Rewrite(state, "", file, true);
            }
            else Rewrite(node, "", file, managed);
            if (changed && sidecar)
                blockers.Add($"成果身份侧车包含待迁移引用，需先支持来源指纹迁移：{Path.GetRelativePath(root, file)}");
            if (changed && node is not null) changes[file] = (before, Utf8.GetBytes(node.ToJsonString(Options)));

            void Rewrite(JsonNode? current, string field, string owner, bool managementFile)
            {
                if (current is JsonObject map)
                {
                    foreach (var pair in map.ToArray())
                    {
                        if (Rebind(pair.Key, source, target, oldRelative, newRelative) != pair.Key)
                            blockers.Add($"字典键包含路径，尚需专项迁移：{Path.GetRelativePath(root, owner)}");
                        string key = Normalize(pair.Key);
                        if (pair.Value is JsonValue value && value.TryGetValue<string>(out var text))
                        {
                            if (key == "manifestjson")
                            {
                                try
                                {
                                    var embedded = JsonNode.Parse(text);
                                    bool prior = changed; changed = false;
                                    Rewrite(embedded, "", owner, false);
                                    bool embeddedChanged = changed; changed |= prior;
                                    if (embeddedChanged) map[pair.Key] = embedded!.ToJsonString(Options);
                                }
                                catch (JsonException) { blockers.Add($"内嵌分区清单无效：{owner}"); }
                                continue;
                            }
                            string replacement = Rebind(text, source, target, oldRelative, newRelative);
                            if (replacement != text)
                            {
                                if (PathFields.Contains(key)) { map[pair.Key] = replacement; changed = true; }
                                else if (key is not "notes" and not "note" and not "description")
                                    blockers.Add($"未明确的引用字段 {pair.Key}：{Path.GetRelativePath(root, owner)}");
                            }
                            else if (ContainsReference(text, source, oldRelative) &&
                                key is not "notes" and not "note" and not "description")
                                blockers.Add($"字段 {pair.Key} 包含复合路径引用：{Path.GetRelativePath(root, owner)}");
                        }
                        else if (pair.Value is JsonArray array)
                        {
                            for (int i = 0; i < array.Count; i++)
                            {
                                if (array[i] is JsonValue v && v.TryGetValue<string>(out var itemText))
                                {
                                    string replacement = Rebind(itemText, source, target, oldRelative, newRelative);
                                    if (replacement != itemText)
                                    {
                                        if (PathFields.Contains(key)) { array[i] = replacement; changed = true; }
                                        else if (key is not "notes") blockers.Add($"未明确的引用数组 {pair.Key}：{Path.GetRelativePath(root, owner)}");
                                    }
                                    else if (ContainsReference(itemText, source, oldRelative) && key is not "notes")
                                        blockers.Add($"数组 {pair.Key} 包含复合路径引用：{Path.GetRelativePath(root, owner)}");
                                }
                                else Rewrite(array[i], pair.Key, owner, managementFile);
                            }
                        }
                        else Rewrite(pair.Value, pair.Key, owner, managementFile);
                    }
                }
                else if (current is JsonArray list)
                    foreach (var item in list) Rewrite(item, field, owner, managementFile);
            }
        }
        // Rewriting a generated JSON changes its content hash. Until all dependent
        // production fingerprints can be migrated, refuse rather than certify old evidence.
        foreach (var file in changes.Keys.Where(f => !IsWithin(management, f)))
            if (File.Exists(file + GeneratedArtifactIdentity.Suffix))
                blockers.Add($"待迁移 JSON 有成果身份侧车，需先支持关联哈希迁移：{Path.GetRelativePath(root, file)}");
        var workareas = workspace.GetWorkareas(root); // Registration identity must remain unique after a directory scan.
        if (workareas.Any(w => w.WorkareaId != row.WorkareaId &&
            (w.WorkareaId.Equals(WorkspaceService.Slug(newFolderName), StringComparison.OrdinalIgnoreCase) ||
             w.SubareaId.Equals(WorkspaceService.Slug(newFolderName), StringComparison.OrdinalIgnoreCase))))
            blockers.Add("新目录名与另一工区的稳定身份冲突。");
        foreach (string file in files.Where(f => new[] { ".xml", ".kml", ".txt", ".csv", ".tsv", ".yaml", ".yml" }
                     .Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)))
            if (TextFileReferences(file, source, oldRelative))
                blockers.Add($"文本文件含旧目录引用，尚不能自动迁移：{Path.GetRelativePath(root, file)}");
        string artifactRegistry = ProjectPaths.Json(root, "artifacts.json");
        if (File.Exists(artifactRegistry) && JsonNode.Parse(File.ReadAllText(artifactRegistry)) is JsonArray registered)
            foreach (var artifact in registered.OfType<JsonObject>())
                if (artifact["sha256"]?.GetValue<string>() is { Length: > 0 } && artifact["path"]?.GetValue<string>() is { } path &&
                    changes.ContainsKey(Path.GetFullPath(Path.Combine(root, path))))
                    blockers.Add($"待迁移 JSON 已登记内容哈希，需先处理成果完整性关系：{path}");
        return new()
        {
            ProjectRoot = root, WorkareaId = row.WorkareaId, SourcePath = source, TargetPath = target,
            SourceRelativePath = oldRelative, TargetRelativePath = newRelative,
            ChangedFiles = changes.Keys.Select(f => Path.GetRelativePath(root, f)).ToArray(),
            Blockers = blockers.Distinct().ToArray(), Changes = changes, Inventory = Inventory(files, directories, root), UserContextPath = userContextPath
        };
    }

    public void Apply(WorkareaDirectoryRenamePlan plan)
    {
        lock (UserContextService.LocalWriteLock) ApplyCore(plan);
    }

    private void ApplyCore(WorkareaDirectoryRenamePlan plan)
    {
        if (!plan.CanApply) throw new InvalidOperationException("预览存在阻断项，未更改目录。");
        using var writeLock = WorkspaceFileLock.AcquireForRoot(plan.ProjectRoot);
        var current = Preview(plan.ProjectRoot, plan.WorkareaId, Path.GetFileName(plan.TargetPath), plan.UserContextPath);
        if (!current.CanApply || current.SourcePath != plan.SourcePath || current.Inventory != plan.Inventory ||
            current.Changes.Count != plan.Changes.Count || current.Changes.Any(p => !plan.Changes.TryGetValue(p.Key, out var old) ||
                !p.Value.Before.SequenceEqual(old.Before) || !p.Value.After.SequenceEqual(old.After)))
            throw new InvalidOperationException("目录或引用已变化，请重新预览后再确认。");
        string journal = Path.Combine(ProjectPaths.DataDirectory(plan.ProjectRoot), "directory-renames", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(journal);
        var manifest = new RenameJournal(plan.WorkareaId, plan.SourcePath, plan.TargetPath,
            plan.Changes.Select(p => new RenameJournalFile(p.Key, Hash(p.Value.Before), Hash(p.Value.After))).ToArray());
        int index = 0;
        foreach (var change in plan.Changes) File.WriteAllBytes(Path.Combine(journal, $"{index++:D4}.before"), change.Value.Before);
        AtomicWrite(Path.Combine(journal, "transaction.json"), Utf8.GetBytes(JsonSerializer.Serialize(manifest, Options)));
        bool moved = false;
        var written = new List<string>();
        try
        {
            Directory.Move(plan.SourcePath, plan.TargetPath);
            moved = true;
            foreach (var change in plan.Changes)
            {
                BeforeWrite?.Invoke(written.Count);
                string destination = Rebind(change.Key, plan.SourcePath, plan.TargetPath, "", "");
                written.Add(change.Key);
                AtomicWrite(destination, change.Value.After);
            }
            File.WriteAllText(Path.Combine(journal, "state.txt"), "committed");
        }
        catch (Exception failure)
        {
            var rollbackErrors = new List<Exception>();
            foreach (string file in written.AsEnumerable().Reverse())
            {
                try { AtomicWrite(Rebind(file, plan.SourcePath, plan.TargetPath, "", ""), plan.Changes[file].Before); }
                catch (Exception ex) { rollbackErrors.Add(ex); }
            }
            if (moved)
            {
                try { Directory.Move(plan.TargetPath, plan.SourcePath); }
                catch (Exception ex) { rollbackErrors.Add(ex); }
            }
            File.WriteAllText(Path.Combine(journal, "state.txt"), rollbackErrors.Count == 0 ? "rolled-back" : "recovery-required");
            if (rollbackErrors.Count > 0) throw new AggregateException($"更名失败且需恢复，请保留事务目录：{journal}", [failure, .. rollbackErrors]);
            throw;
        }
    }

    private static void AtomicWrite(string path, byte[] data)
    {
        string staged = path + ".rename-" + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(staged, data); File.Move(staged, path, true); }
        finally { if (File.Exists(staged)) File.Delete(staged); }
    }

    private sealed record RenameJournalFile(string Path, string BeforeHash, string AfterHash);
    private sealed record RenameJournal(string WorkareaId, string Source, string Target, RenameJournalFile[] Files);
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    public static IReadOnlyList<string> PendingTransactions(string root)
    {
        string directory = Path.Combine(ProjectPaths.DataDirectory(root), "directory-renames");
        if (!Directory.Exists(directory)) return [];
        return Directory.EnumerateDirectories(directory).Where(d =>
            File.Exists(Path.Combine(d, "transaction.json")) &&
            (!File.Exists(Path.Combine(d, "state.txt")) ||
             File.ReadAllText(Path.Combine(d, "state.txt")) is not "committed" and not "rolled-back")).ToArray();
    }

    public void Recover(string root, string transactionDirectory, string? userContextPath = null)
    {
        lock (UserContextService.LocalWriteLock) RecoverCore(root, transactionDirectory, userContextPath);
    }

    private void RecoverCore(string root, string transactionDirectory, string? userContextPath)
    {
        root = Path.GetFullPath(root);
        transactionDirectory = Path.GetFullPath(transactionDirectory);
        string transactionRoot = Path.Combine(ProjectPaths.DataDirectory(root), "directory-renames");
        if (!SamePath(Path.GetDirectoryName(transactionDirectory)!, transactionRoot) ||
            !PendingTransactions(root).Contains(transactionDirectory, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("请选择此项目尚未完成的更名事务。");
        using var writeLock = WorkspaceFileLock.AcquireForRoot(root);
        var blockers = new List<string>();
        _ = EnumerateProjectFiles(root, blockers).ToArray();
        if (blockers.Count > 0) throw new InvalidDataException(string.Join(Environment.NewLine, blockers));
        for (string? ancestor = transactionDirectory; ancestor is not null; ancestor = Path.GetDirectoryName(ancestor))
            if ((File.GetAttributes(ancestor) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("恢复路径包含目录链接。");
        var journal = JsonSerializer.Deserialize<RenameJournal>(File.ReadAllText(Path.Combine(transactionDirectory, "transaction.json")))
            ?? throw new InvalidDataException("事务清单无效。");
        string source = Path.GetFullPath(journal.Source), target = Path.GetFullPath(journal.Target);
        var registered = new WorkspaceService(new JsonStore()).GetWorkareas(root)
            .SingleOrDefault(w => w.WorkareaId == journal.WorkareaId);
        if (registered is null || !(SamePath(Path.GetFullPath(Path.Combine(root, registered.Path)), source) ||
                                  SamePath(Path.GetFullPath(Path.Combine(root, registered.Path)), target)))
            throw new InvalidDataException("事务工区身份与当前登记不一致，未修改文件。");
        if (!IsWithin(root, source) || !IsWithin(root, target) ||
            !SamePath(Path.GetDirectoryName(source)!, Path.GetDirectoryName(target)!) || SamePath(source, target) ||
            SamePath(source, ProjectPaths.DataDirectory(root)) || IsWithin(source, ProjectPaths.DataDirectory(root)) ||
            IsWithin(ProjectPaths.DataDirectory(root), source) ||
            Directory.Exists(source) == Directory.Exists(target) || File.Exists(source) || File.Exists(target))
            throw new InvalidDataException("恢复源/目标边界或当前目录状态不一致，未修改文件。");
        bool moved = Directory.Exists(target);
        var restores = new List<(string Path, byte[] Bytes)>();
        for (int i = 0; i < journal.Files.Length; i++)
        {
            var file = journal.Files[i];
            string original = Path.GetFullPath(file.Path);
            if (!IsWithin(root, original) &&
                (userContextPath is null || !SamePath(original, Path.GetFullPath(userContextPath))))
                throw new InvalidDataException("事务含范围外的恢复文件。");
            string current = moved ? Rebind(original, source, target, "", "") : original;
            if (HasLinkedAncestor(current)) throw new InvalidDataException("恢复文件路径包含链接，未覆盖文件。");
            byte[] backup = File.ReadAllBytes(Path.Combine(transactionDirectory, $"{i:D4}.before"));
            if (Hash(backup) != file.BeforeHash || !File.Exists(current))
                throw new InvalidDataException("恢复备份或当前文件缺失/失效，未覆盖文件。");
            string currentHash = GeneratedArtifactIdentity.Hash(current);
            if (currentHash != file.BeforeHash && currentHash != file.AfterHash)
                throw new InvalidDataException($"事务之后文件已有其他修改，请先人工核对：{current}");
            restores.Add((current, backup));
        }
        foreach (var restore in restores) AtomicWrite(restore.Path, restore.Bytes);
        if (moved) Directory.Move(target, source);
        File.WriteAllText(Path.Combine(transactionDirectory, "state.txt"), "rolled-back");
    }

    private static bool NodeReferences(JsonNode? node, string absolute, string relative) => node switch
    {
        JsonObject obj => obj.Any(p => ContainsReference(p.Key, absolute, relative) || NodeReferences(p.Value, absolute, relative)),
        JsonArray array => array.Any(n => NodeReferences(n, absolute, relative)),
        JsonValue value when value.TryGetValue<string>(out var text) => ContainsReference(text, absolute, relative),
        _ => false
    };

    private static bool TextFileReferences(string file, string absolute, string relative)
    {
        try
        {
            using var reader = new StreamReader(file, Utf8, detectEncodingFromByteOrderMarks: true);
            char[] buffer = new char[16384];
            string tail = "";
            int count;
            while ((count = reader.Read(buffer, 0, buffer.Length)) > 0)
            {
                string text = tail + new string(buffer, 0, count);
                if (ContainsReference(text, absolute, relative)) return true;
                tail = text[^Math.Min(text.Length, Math.Max(absolute.Length, relative.Length) * 4)..];
            }
            return false;
        }
        catch (DecoderFallbackException) { return true; }
    }

    private static IEnumerable<string> EnumerateProjectFiles(string root, List<string> blockers, List<string>? directories = null)
    {
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.TryPop(out string? directory))
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                { blockers.Add($"目录中存在链接，需先人工确认：{Path.GetRelativePath(root, entry)}"); continue; }
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (IsWithin(ProjectPaths.DataDirectory(root), entry) &&
                        Path.GetFileName(entry) is "snapshots" or "directory-renames") continue;
                    if (entry != root && ProjectPaths.HasProject(entry))
                    { blockers.Add($"存在嵌套项目：{Path.GetRelativePath(root, entry)}"); continue; }
                    directories?.Add(entry);
                    pending.Push(entry);
                }
                else if (Path.GetFileName(entry) != ".write.lock" && !entry.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                    yield return entry;
            }
        }
    }

    private static string Inventory(IEnumerable<string> files, IEnumerable<string> directories, string root) => Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(
        string.Join('\n', files.Order(StringComparer.OrdinalIgnoreCase).Select(f =>
        { var info = new FileInfo(f); return $"{Path.GetRelativePath(root, f)}|{info.Length}|{info.LastWriteTimeUtc.Ticks}"; })) +
        string.Join('\n', directories.Order(StringComparer.OrdinalIgnoreCase).Select(d => "DIR|" + Path.GetRelativePath(root, d))))));

    private static string Normalize(string field) => field.Replace("_", "").ToLowerInvariant();
    private static bool HasLinkedAncestor(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                return true;
        return false;
    }
    private static bool SamePath(string first, string second) => first.Replace('\\', '/').TrimEnd('/').Equals(second.Replace('\\', '/').TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
    private static bool IsWithin(string parent, string path) => path.StartsWith(parent.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static bool ContainsReference(string text, string absolute, string relative) =>
        text.Replace("\\\\", "/").Replace('\\', '/').Contains(absolute.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase) ||
        text.Replace('\\', '/').Contains(relative, StringComparison.OrdinalIgnoreCase);
    private static string Rebind(string value, string source, string target, string oldRelative, string newRelative)
    {
        string path = value.Replace('\\', '/');
        foreach (var mapping in new[] { (source.Replace('\\', '/'), target.Replace('\\', '/')), (oldRelative, newRelative) })
            if (mapping.Item1.Length > 0 && (SamePath(path, mapping.Item1) || path.StartsWith(mapping.Item1 + "/", StringComparison.OrdinalIgnoreCase)))
                return mapping.Item2 + path[mapping.Item1.Length..];
        return value;
    }
    private static void ValidateLeaf(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name != name.Trim() || name.EndsWith('.') || name.Length > 100 ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is "." or ".." ||
            new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
                "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(name.Split('.')[0], StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("请输入有效的新目录名（1～100 字，无路径分隔符、尾点、保留设备名）。");
    }
}
