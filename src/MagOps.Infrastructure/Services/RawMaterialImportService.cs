using System.Security.Cryptography;
using System.Text.Json;

namespace MagOps.Infrastructure.Services;

public static class RawImportStatuses
{
    public const string Archived = "ARCHIVED";
    public const string SkippedDuplicate = "SKIPPED_DUPLICATE";
    public const string Failed = "FAILED";
}

public sealed record RawImportItem(
    string SourcePath,
    string ArchivedRelativePath,
    long SizeBytes,
    string Sha256,
    string SourceModifiedUtc,
    string Status = RawImportStatuses.Archived,
    string Note = "");

public sealed record RawImportBatchResult(
    string Status,
    string WorkareaRelativePath,
    string DestinationDirectory,
    string ManifestPath,
    IReadOnlyList<RawImportItem> Items)
{
    public int ArchivedCount => Items.Count(x => x.Status == RawImportStatuses.Archived);
    public int SkippedDuplicateCount => Items.Count(x => x.Status == RawImportStatuses.SkippedDuplicate);
    public int FailedCount => Items.Count(x => x.Status == RawImportStatuses.Failed);
}

public sealed record RawImportProgress(
    string Phase,
    int CurrentIndex,
    int TotalFiles,
    string CurrentFile,
    long CurrentBytes,
    long CurrentTotalBytes,
    string Message);

/// <summary>
/// Copies incoming raw files into a timestamped archive batch.
/// Source files are never moved/deleted. The service supports cancellation,
/// duplicate SHA-256 detection, per-file progress, partial-file cleanup and
/// a persistent batch manifest.
/// </summary>
public sealed class RawMaterialImportService
{
    private readonly FolderLayoutService _folders;
    private const int BufferSize = 1024 * 1024;

    public RawMaterialImportService(FolderLayoutService folders)
    {
        _folders = folders;
    }

    public async Task<RawImportBatchResult> ImportAsync(
        string projectRoot,
        string workareaRelativePath,
        IEnumerable<string> sourcePaths,
        IReadOnlyDictionary<string, string>? existingHashToArchivedPath = null,
        IProgress<RawImportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        string root = Path.GetFullPath(projectRoot);
        string workarea = (workareaRelativePath ?? "").Trim();
        if (workarea.Length == 0)
            throw new InvalidOperationException("原始资料必须归档到明确的工区。");

        string[] inputs = sourcePaths
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (inputs.Length == 0)
            throw new InvalidOperationException("没有可导入的原始文件。");

        progress?.Report(new RawImportProgress(
            "PREPARING", 0, 0, "", 0, 0, "正在枚举原始资料…"));

        List<(string Source, string DesiredRelative)> expanded = ExpandInputFiles(inputs).ToList();
        if (expanded.Count == 0)
            throw new InvalidOperationException("选择的路径中没有可归档文件。");

        DateTimeOffset importedAt = DateTimeOffset.Now;
        string destination = _folders.EnsureOperationDirectory(
            root, "raw_import", workarea, importedAt);

        var knownHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (existingHashToArchivedPath is not null)
        {
            foreach (var pair in existingHashToArchivedPath)
            {
                if (!string.IsNullOrWhiteSpace(pair.Key))
                    knownHashes[pair.Key.ToLowerInvariant()] = pair.Value;
            }
        }

        var items = new List<RawImportItem>();
        string batchStatus = "COMPLETED";

        for (int index = 0; index < expanded.Count; index++)
        {
            var (source, desiredRelative) = expanded[index];
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                var info = new FileInfo(source);
                progress?.Report(new RawImportProgress(
                    "HASHING",
                    index + 1,
                    expanded.Count,
                    source,
                    0,
                    info.Length,
                    $"正在计算 SHA-256：{Path.GetFileName(source)}"));

                string sourceHash = await ComputeSha256Async(
                    source,
                    index + 1,
                    expanded.Count,
                    progress,
                    cancellationToken).ConfigureAwait(false);

                if (knownHashes.TryGetValue(sourceHash, out string? existingPath))
                {
                    items.Add(new RawImportItem(
                        source,
                        existingPath,
                        info.Length,
                        sourceHash,
                        info.LastWriteTimeUtc.ToString("O"),
                        RawImportStatuses.SkippedDuplicate,
                        "目标工区已有相同 SHA-256 文件，未重复复制。"));
                    progress?.Report(new RawImportProgress(
                        "SKIPPED_DUPLICATE",
                        index + 1,
                        expanded.Count,
                        source,
                        info.Length,
                        info.Length,
                        $"已跳过重复文件：{Path.GetFileName(source)}"));
                    continue;
                }

                string target = UniqueTarget(destination, desiredRelative);
                string parent = Path.GetDirectoryName(target)
                    ?? throw new InvalidOperationException("无法确定归档目标目录。");
                Directory.CreateDirectory(parent);

                string temp = target + ".magops-partial";
                if (File.Exists(temp))
                    File.Delete(temp);

                progress?.Report(new RawImportProgress(
                    "COPYING",
                    index + 1,
                    expanded.Count,
                    source,
                    0,
                    info.Length,
                    $"正在复制：{Path.GetFileName(source)}"));

                string copiedHash;
                try
                {
                    copiedHash = await CopyAndHashAsync(
                        source,
                        temp,
                        index + 1,
                        expanded.Count,
                        progress,
                        cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    if (File.Exists(temp))
                        File.Delete(temp);
                    throw;
                }

                if (!sourceHash.Equals(copiedHash, StringComparison.OrdinalIgnoreCase))
                {
                    if (File.Exists(temp))
                        File.Delete(temp);
                    throw new IOException($"原始资料复制后 SHA-256 不一致：{source}");
                }

                File.Move(temp, target);
                File.SetLastWriteTimeUtc(target, info.LastWriteTimeUtc);

                string archivedRelative = Path.GetRelativePath(root, target).Replace('\\', '/');
                items.Add(new RawImportItem(
                    source,
                    archivedRelative,
                    info.Length,
                    sourceHash,
                    info.LastWriteTimeUtc.ToString("O"),
                    RawImportStatuses.Archived,
                    ""));
                knownHashes[sourceHash] = archivedRelative;

                progress?.Report(new RawImportProgress(
                    "VERIFIED",
                    index + 1,
                    expanded.Count,
                    source,
                    info.Length,
                    info.Length,
                    $"复制并校验完成：{Path.GetFileName(source)}"));
            }
            catch (OperationCanceledException)
            {
                batchStatus = "CANCELLED";
                break;
            }
            catch (Exception ex)
            {
                batchStatus = "PARTIAL";
                var info = File.Exists(source) ? new FileInfo(source) : null;
                items.Add(new RawImportItem(
                    source,
                    "",
                    info?.Length ?? 0,
                    "",
                    info?.LastWriteTimeUtc.ToString("O") ?? "",
                    RawImportStatuses.Failed,
                    ex.Message));
                progress?.Report(new RawImportProgress(
                    "FAILED",
                    index + 1,
                    expanded.Count,
                    source,
                    0,
                    info?.Length ?? 0,
                    $"导入失败：{Path.GetFileName(source)} — {ex.Message}"));
            }
        }

        if (batchStatus != "CANCELLED")
        {
            if (items.Count > 0 && items.All(x => x.Status == RawImportStatuses.Failed))
                batchStatus = "FAILED";
            else if (items.Any(x => x.Status == RawImportStatuses.Failed))
                batchStatus = "PARTIAL";
            else
                batchStatus = "COMPLETED";
        }

        string manifest = Path.Combine(destination, "导入清单.json");
        await WriteManifestAsync(
            manifest,
            root,
            workarea,
            importedAt,
            batchStatus,
            expanded.Count,
            items,
            cancellationToken: CancellationToken.None).ConfigureAwait(false);

        string sanitizedManifest = Path.Combine(destination, "导入清单_脱敏.json");
        await WriteSanitizedManifestAsync(
            sanitizedManifest,
            workarea,
            importedAt,
            batchStatus,
            expanded.Count,
            items,
            cancellationToken: CancellationToken.None).ConfigureAwait(false);

        progress?.Report(new RawImportProgress(
            batchStatus,
            Math.Min(items.Count, expanded.Count),
            expanded.Count,
            "",
            0,
            0,
            $"批次完成：归档 {items.Count(x => x.Status == RawImportStatuses.Archived)}，" +
            $"重复跳过 {items.Count(x => x.Status == RawImportStatuses.SkippedDuplicate)}，" +
            $"失败 {items.Count(x => x.Status == RawImportStatuses.Failed)}"));

        return new RawImportBatchResult(
            batchStatus,
            workarea,
            destination,
            manifest,
            items);
    }

    public IReadOnlyList<string> FindPartialFiles(string projectRoot, int maxResults = 200)
    {
        string root = Path.GetFullPath(projectRoot);
        string workareas = Path.Combine(root, "10_工区");
        if (!Directory.Exists(workareas))
            return [];

        var result = new List<string>();
        foreach (string file in Directory.EnumerateFiles(
                     workareas,
                     "*.magops-partial",
                     SearchOption.AllDirectories))
        {
            result.Add(file);
            if (result.Count >= maxResults)
                break;
        }
        return result;
    }

    public int CleanupPartialFiles(IEnumerable<string> paths)
    {
        int count = 0;
        foreach (string path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(path) ||
                !path.EndsWith(".magops-partial", StringComparison.OrdinalIgnoreCase))
                continue;
            File.Delete(path);
            count++;
        }
        return count;
    }

    private static async Task<string> ComputeSha256Async(
        string path,
        int index,
        int total,
        IProgress<RawImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var input = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        byte[] buffer = new byte[BufferSize];
        long done = 0;
        while (true)
        {
            int read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
                break;

            hash.AppendData(buffer, 0, read);
            done += read;
            progress?.Report(new RawImportProgress(
                "HASHING", index, total, path, done, input.Length,
                $"正在计算 SHA-256：{Path.GetFileName(path)}"));
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static async Task<string> CopyAndHashAsync(
        string source,
        string destinationTemp,
        int index,
        int total,
        IProgress<RawImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var input = new FileStream(
            source,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(
            destinationTemp,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        byte[] buffer = new byte[BufferSize];
        long done = 0;
        while (true)
        {
            int read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
                break;

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                .ConfigureAwait(false);
            hash.AppendData(buffer, 0, read);
            done += read;

            progress?.Report(new RawImportProgress(
                "COPYING", index, total, source, done, input.Length,
                $"正在复制：{Path.GetFileName(source)}"));
        }

        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        output.Flush(flushToDisk: true);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static async Task WriteManifestAsync(
        string manifest,
        string projectRoot,
        string workarea,
        DateTimeOffset importedAt,
        string batchStatus,
        int plannedCount,
        IReadOnlyList<RawImportItem> items,
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            schema_version = "raw-import-2",
            status = batchStatus,
            imported_at = importedAt.ToString("O"),
            project_root = projectRoot,
            workarea_relative_path = workarea.Replace('\\', '/'),
            source_policy = "copy_keep_source",
            source_path_scope = "internal_only_may_contain_local_user_or_drive_information",
            verification = "sha256",
            planned_item_count = plannedCount,
            archived_count = items.Count(x => x.Status == RawImportStatuses.Archived),
            skipped_duplicate_count = items.Count(x => x.Status == RawImportStatuses.SkippedDuplicate),
            failed_count = items.Count(x => x.Status == RawImportStatuses.Failed),
            items = items.Select(x => new
            {
                status = x.Status,
                source_path = x.SourcePath,
                archived_relative_path = x.ArchivedRelativePath,
                size_bytes = x.SizeBytes,
                sha256 = x.Sha256,
                source_modified_utc = x.SourceModifiedUtc,
                note = x.Note
            }).ToArray()
        };

        await File.WriteAllTextAsync(
            manifest,
            JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true })
            + Environment.NewLine,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteSanitizedManifestAsync(
        string manifest,
        string workarea,
        DateTimeOffset importedAt,
        string batchStatus,
        int plannedCount,
        IReadOnlyList<RawImportItem> items,
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            schema_version = "raw-import-public-1",
            status = batchStatus,
            imported_at = importedAt.ToString("O"),
            workarea_relative_path = workarea.Replace('\\', '/'),
            source_policy = "copy_keep_source",
            verification = "sha256",
            planned_item_count = plannedCount,
            archived_count = items.Count(x => x.Status == RawImportStatuses.Archived),
            skipped_duplicate_count = items.Count(x => x.Status == RawImportStatuses.SkippedDuplicate),
            failed_count = items.Count(x => x.Status == RawImportStatuses.Failed),
            items = items.Select(x => new
            {
                status = x.Status,
                archived_relative_path = x.ArchivedRelativePath,
                size_bytes = x.SizeBytes,
                sha256 = x.Sha256,
                source_modified_utc = x.SourceModifiedUtc,
                note = x.Note
            }).ToArray()
        };

        await File.WriteAllTextAsync(
            manifest,
            JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true })
            + Environment.NewLine,
            cancellationToken).ConfigureAwait(false);
    }

    private static IEnumerable<(string Source, string DesiredRelative)> ExpandInputFiles(
        IEnumerable<string> inputs)
    {
        foreach (string input in inputs)
        {
            if (File.Exists(input))
            {
                yield return (input, Path.GetFileName(input));
                continue;
            }

            if (!Directory.Exists(input))
                throw new FileNotFoundException($"拖入/选择的路径不存在：{input}", input);

            string folderName = new DirectoryInfo(input).Name;
            foreach (string file in Directory.EnumerateFiles(
                         input, "*", SearchOption.AllDirectories)
                     .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                string child = Path.GetRelativePath(input, file);
                yield return (file, Path.Combine(folderName, child));
            }
        }
    }

    private static string UniqueTarget(string destinationRoot, string desiredRelative)
    {
        string relative = desiredRelative.Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(relative) ||
            relative.Split(Path.DirectorySeparatorChar).Any(x => x == ".."))
            throw new InvalidDataException($"非法归档相对路径：{desiredRelative}");

        string full = Path.GetFullPath(Path.Combine(destinationRoot, relative));
        string prefix = Path.GetFullPath(destinationRoot)
                            .TrimEnd(Path.DirectorySeparatorChar)
                        + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"归档路径越界：{desiredRelative}");

        if (!File.Exists(full))
            return full;

        string? dir = Path.GetDirectoryName(full);
        string stem = Path.GetFileNameWithoutExtension(full);
        string ext = Path.GetExtension(full);
        for (int i = 2; i < 10000; i++)
        {
            string candidate = Path.Combine(dir!, $"{stem}__{i}{ext}");
            if (!File.Exists(candidate))
                return candidate;
        }
        throw new IOException($"目标目录中同名文件过多：{desiredRelative}");
    }
}
