using System.Text.Json;
using MagOps.Core.Domain;
using MagOps.Infrastructure.Storage;

namespace MagOps.Infrastructure.Services;

public sealed record ArtifactQualityEvidence(string ContentSha256, string QualityStatus);

public sealed record IndependentExportJournal(string Id, string Destination, string StageDirectory,
    bool HadFile, bool HadMetadata, string OldHash, string OldMetadataHash,
    string NewHash, string NewMetadataHash, ArtifactRecord? Previous, bool Committed = false);

/// <summary>Recoverable per-artifact transaction; does not replace a mixed output directory.</summary>
public static class IndependentArtifactTransaction
{
    private static readonly JsonSerializerOptions MetadataOptions = new()
    { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true };
    private static string JournalPath(string root, string id)
    {
        if (!id.StartsWith("export_", StringComparison.Ordinal) || id.Length != 39 ||
            !id[7..].All(Uri.IsHexDigit)) throw new InvalidDataException("导出恢复ID无效。");
        return Path.Combine(ProjectPaths.DataDirectory(root), "artifact_exports", id + ".json");
    }

    public static string ManagedPath(string root, string path)
    {
        string full = Path.GetFullPath(path);
        string prefix = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("独立成果提交必须位于项目内。");
        for (string? dir = Path.GetDirectoryName(full); dir is not null && dir.Length >= prefix.Length; dir = Path.GetDirectoryName(dir))
            if (Directory.Exists(dir) && (File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("成果路径不能经过目录链接。");
        return full;
    }

    public static bool IsPending(string root, string id) => File.Exists(JournalPath(root, id));
    public static GeneratedArtifactMetadata ReadMetadata(string path) =>
        JsonSerializer.Deserialize<GeneratedArtifactMetadata>(File.ReadAllText(path + GeneratedArtifactIdentity.Suffix), MetadataOptions)
        ?? throw new InvalidDataException("成果身份无效。");
    public static void WriteMetadata(string path, GeneratedArtifactMetadata metadata) =>
        AtomicFileWriter.Write(path + GeneratedArtifactIdentity.Suffix, stream => JsonSerializer.Serialize(stream, metadata, MetadataOptions));

    public static IndependentExportJournal Prepare(string root, string destination, string staged,
        string id, ArtifactRecord? previous, JsonStore store)
    {
        destination = ManagedPath(root, destination);
        string directory = Path.GetDirectoryName(staged)!;
        bool hadFile = File.Exists(destination), hadMetadata = File.Exists(destination + GeneratedArtifactIdentity.Suffix);
        if (hadFile) File.Copy(destination, Path.Combine(directory, "old.body"));
        if (hadMetadata) File.Copy(destination + GeneratedArtifactIdentity.Suffix, Path.Combine(directory, "old.metadata"));
        var journal = new IndependentExportJournal(id, ArtifactDependencies.Relative(root, destination),
            ArtifactDependencies.Relative(root, directory), hadFile, hadMetadata,
            hadFile ? GeneratedArtifactIdentity.Hash(destination) : "",
            hadMetadata ? GeneratedArtifactIdentity.Hash(destination + GeneratedArtifactIdentity.Suffix) : "",
            GeneratedArtifactIdentity.Hash(staged), GeneratedArtifactIdentity.Hash(staged + GeneratedArtifactIdentity.Suffix), previous);
        Save(root, journal, store);
        return journal;
    }

    private static void Save(string root, IndependentExportJournal journal, JsonStore store)
    {
        string path = JournalPath(root, journal.Id);
        AtomicFileWriter.Write(path, stream => JsonSerializer.Serialize(stream, journal, store.Options));
    }

    public static void Install(string root, IndependentExportJournal journal)
    {
        var (destination, directory) = Validate(root, journal);
        string staged = Path.Combine(directory, Path.GetFileName(destination));
        CopyAtomic(staged, destination);
        CopyAtomic(staged + GeneratedArtifactIdentity.Suffix, destination + GeneratedArtifactIdentity.Suffix);
    }

    public static void Commit(string root, IndependentExportJournal journal, JsonStore store)
    {
        Save(root, journal with { Committed = true }, store);
        Cleanup(root, journal);
    }

    public static void Recover(string root, JsonStore store)
    {
        using var writeLock = WorkspaceFileLock.AcquireForRoot(root);
        string folder = Path.Combine(ProjectPaths.DataDirectory(root), "artifact_exports");
        if (!Directory.Exists(folder)) return;
        foreach (string path in Directory.GetFiles(folder, "export_*.json"))
        {
            var journal = store.Read<IndependentExportJournal?>(path, null)
                ?? throw new InvalidDataException("恢复记录损坏；保留待恢复状态。");
            if (Path.GetFullPath(path) != Path.GetFullPath(JournalPath(root, journal.Id)))
                throw new InvalidDataException("恢复文件与ID不一致。");
            var (destination, directory) = Validate(root, journal);
            if (!journal.Committed)
            {
                // Check all conflicts and backups before changing either member.
                ValidateRestore(destination, journal.HadFile, journal.OldHash, journal.NewHash, Path.Combine(directory, "old.body"));
                ValidateRestore(destination + GeneratedArtifactIdentity.Suffix, journal.HadMetadata,
                    journal.OldMetadataHash, journal.NewMetadataHash, Path.Combine(directory, "old.metadata"));
                var rows = store.Read(ProjectPaths.Json(root, "artifacts.json"), new List<ArtifactRecord>());
                ArtifactRecord? current = rows.SingleOrDefault(x => x.Path.Equals(journal.Destination, StringComparison.OrdinalIgnoreCase));
                if (current is not null && current.Sha256 != journal.NewHash &&
                    JsonSerializer.Serialize(current, store.Options) != JsonSerializer.Serialize(journal.Previous, store.Options))
                    throw new InvalidDataException("台账行已被其他操作修改，需人工恢复。");
                Restore(destination, journal.HadFile, Path.Combine(directory, "old.body"));
                Restore(destination + GeneratedArtifactIdentity.Suffix, journal.HadMetadata, Path.Combine(directory, "old.metadata"));
                rows.RemoveAll(x => x.Path.Equals(journal.Destination, StringComparison.OrdinalIgnoreCase));
                if (journal.Previous is not null) rows.Add(journal.Previous);
                store.Write(ProjectPaths.Json(root, "artifacts.json"), rows.OrderBy(x => x.Path).ToList());
            }
            Cleanup(root, journal);
        }
    }

    private static (string Destination, string Directory) Validate(string root, IndependentExportJournal j)
    {
        string target = ManagedPath(root, ArtifactDependencies.Resolve(root, j.Destination));
        string directory = ManagedPath(root, ArtifactDependencies.Resolve(root, j.StageDirectory) + Path.DirectorySeparatorChar + "sentinel");
        directory = Path.GetDirectoryName(directory)!;
        if (directory != Path.Combine(Path.GetDirectoryName(target)!, ".magops-" + j.Id))
            throw new InvalidDataException("恢复备份位置无效。");
        if (j.Previous is not null && !j.Previous.Path.Equals(j.Destination, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("恢复台账路径不一致。");
        return (target, directory);
    }

    private static void ValidateRestore(string path, bool existed, string oldHash, string newHash, string backup)
    {
        if (existed && (!File.Exists(backup) || GeneratedArtifactIdentity.Hash(backup) != oldHash))
            throw new InvalidDataException("恢复备份缺失或改变，停止恢复。");
        if (File.Exists(path))
        {
            string hash = GeneratedArtifactIdentity.Hash(path);
            if (hash != oldHash && hash != newHash) throw new InvalidDataException("成果已被外部修改，停止自动恢复。");
        }
    }
    private static void Restore(string path, bool existed, string backup)
    {
        if (existed) CopyAtomic(backup, path);
        else if (File.Exists(path)) File.Delete(path);
    }
    private static void CopyAtomic(string source, string target) => AtomicFileWriter.Write(target, stream =>
    { using var input = File.OpenRead(source); input.CopyTo(stream); });
    private static void Cleanup(string root, IndependentExportJournal journal)
    {
        var (_, directory) = Validate(root, journal);
        // Remove the marker first only after rollback/commit is complete. Backups left by
        // a cleanup failure are harmless; a pending marker with deleted backups is not.
        File.Delete(JournalPath(root, journal.Id));
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
