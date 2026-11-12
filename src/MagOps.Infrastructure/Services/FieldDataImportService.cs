using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using MagOps.Core.Domain;
using MagOps.Infrastructure.Storage;

namespace MagOps.Infrastructure.Services;

public sealed record FieldDataImportItemResult(
    string SourcePath,
    string DestinationPath,
    string Sha256,
    bool Imported,
    bool Duplicate,
    string Message);

public sealed record FieldDataImportBatchResult(
    string BatchId,
    string DestinationDirectory,
    IReadOnlyList<FieldDataImportItemResult> Items)
{
    public int ImportedCount => Items.Count(x => x.Imported);
    public int DuplicateCount => Items.Count(x => x.Duplicate);
}

public sealed record FieldDataConsistencyIssue(string RelativePath, string Status);
public sealed record FieldDataConsistencyReport(string SourceDirectory, string AnalysisFingerprint,
    int CheckedFiles, IReadOnlyList<FieldDataConsistencyIssue> Issues)
{
    public IReadOnlyDictionary<string, string> ObservedHashes { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> SourceFileHashes { get; init; } = new Dictionary<string, string>();
    public bool RequiresFullReparse { get; init; }
    public IReadOnlyList<string> SourceFiles { get; init; } = [];
    public IReadOnlyList<string> SourceFileStableIds { get; init; } = [];
}

public sealed class FieldDataConsistencyLedger
{
    [JsonPropertyName("schema_version")] public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("reports")] public List<FieldDataConsistencyReport> Reports { get; set; } = [];
}

public sealed class FieldDataImportService
{
    private readonly JsonStore _store;

    public FieldDataImportService(JsonStore store)
    {
        _store = store;
    }

    public List<FieldDataCatalogRecord> List(string root) =>
        _store.Read(
            ProjectPaths.Json(root, "field_data_catalog.json"),
            new List<FieldDataCatalogRecord>());

    public IReadOnlyList<FieldDataConsistencyReport> ListConsistencyReports(string root) =>
        ReadConsistencyReports(ProjectPaths.Json(root, "field_data_consistency.json"));

    public IReadOnlyList<string> DiscoverSourceFiles(
        string sourceDirectory,
        bool recursive)
    {
        if (string.IsNullOrWhiteSpace(sourceDirectory))
            throw new ArgumentException(
                "外业数据源文件夹不能为空。",
                nameof(sourceDirectory));

        string sourceRoot =
            Path.GetFullPath(sourceDirectory);
        if (!Directory.Exists(sourceRoot))
            throw new DirectoryNotFoundException(
                $"外业数据源文件夹不存在：{sourceRoot}");

        var options =
            new EnumerationOptions
            {
                RecurseSubdirectories = recursive,
                IgnoreInaccessible = false,
                ReturnSpecialDirectories = false,
                AttributesToSkip = FileAttributes.ReparsePoint
            };

        return Directory
            .EnumerateFiles(sourceRoot, "*", options)
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public FieldDataImportBatchResult ImportFiles(
        string root,
        string workareaPath,
        string destinationRoot,
        IEnumerable<string> sourceFiles)
    {
        string projectRoot = Path.GetFullPath(root);
        string workarea = (workareaPath ?? "")
            .Replace('\\', '/')
            .Trim('/');
        if (workarea.Length == 0)
            throw new ArgumentException("导入外业数据前必须选择当前工区。");

        string targetRoot = Path.GetFullPath(destinationRoot);
        Directory.CreateDirectory(targetRoot);

        List<string> sources = sourceFiles
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(Path.GetFullPath)
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (sources.Count == 0)
            throw new InvalidOperationException("没有可导入的文件。");

        string batchId =
            DateTimeOffset.Now.ToString(
                "yyyyMMdd_HHmmssfff",
                CultureInfo.InvariantCulture);
        string finalDirectory = Path.Combine(
            targetRoot,
            "01_导入原始数据",
            batchId);
        string stagingDirectory = Path.Combine(
            ProjectPaths.DataDirectory(projectRoot),
            "staging",
            "field",
            batchId);
        Directory.CreateDirectory(stagingDirectory);

        List<FieldDataCatalogRecord> catalog = List(projectRoot);
        ReconcileMissingFiles(projectRoot, catalog.Where(x =>
            x.WorkareaPath.Equals(workarea, StringComparison.OrdinalIgnoreCase)));
        var results = new List<FieldDataImportItemResult>();

        try
        {
            foreach (string source in sources)
            {
                string sourceSha = Sha256(source);
                FileInfo sourceInfo = new(source);

                FieldDataCatalogRecord? duplicate = catalog.FirstOrDefault(x =>
                    x.WorkareaPath.Equals(
                        workarea,
                        StringComparison.OrdinalIgnoreCase) &&
                    x.Sha256.Equals(
                        sourceSha,
                        StringComparison.OrdinalIgnoreCase) &&
                    x.Status.Equals(
                        "IMPORTED",
                        StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(ResolveCatalogPath(projectRoot, x.RelativePath)));
                if (duplicate is not null)
                {
                    results.Add(new FieldDataImportItemResult(
                        source,
                        ResolveCatalogPath(projectRoot, duplicate.RelativePath),
                        sourceSha,
                        Imported: false,
                        Duplicate: true,
                        "内容 SHA-256 已存在，未重复入库"));
                    continue;
                }

                Directory.CreateDirectory(finalDirectory);
                string staging = UniquePath(
                    stagingDirectory,
                    Path.GetFileName(source),
                    sourceSha);
                File.Copy(source, staging, overwrite: false);
                if (!Sha256(staging).Equals(
                        sourceSha,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException(
                        $"导入暂存校验失败：{Path.GetFileName(source)}");
                }

                string finalPath = UniquePath(
                    finalDirectory,
                    Path.GetFileName(source),
                    sourceSha);
                File.Copy(staging, finalPath, overwrite: false);
                string finalSha = Sha256(finalPath);
                if (!finalSha.Equals(
                        sourceSha,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException(
                        $"项目原始资料写入校验失败：{Path.GetFileName(source)}");
                }

                var row = new FieldDataCatalogRecord
                {
                    FileId = "field_" + Guid.NewGuid().ToString("N"),
                    WorkareaPath = workarea,
                    RelativePath = ProjectRelativeOrAbsolute(
                        projectRoot,
                        finalPath),
                    OriginalFileName = Path.GetFileName(source),
                    SourcePathHint = source,
                    Sha256 = finalSha,
                    SizeBytes = sourceInfo.Length,
                    SourceLastWriteUtc =
                        sourceInfo.LastWriteTimeUtc.ToString(
                            "O",
                            CultureInfo.InvariantCulture),
                    ImportBatchId = batchId,
                    ImportedAt = DateTimeOffset.UtcNow.ToString("O"),
                    ParserVersion =
                        FieldMagDataService.IncrementalCacheParserVersion,
                    Status = "IMPORTED"
                };
                catalog.Add(row);

                results.Add(new FieldDataImportItemResult(
                    source,
                    finalPath,
                    finalSha,
                    Imported: true,
                    Duplicate: false,
                    "已复制并完成 SHA-256 校验"));
            }

            _store.Write(
                ProjectPaths.Json(
                    projectRoot,
                    "field_data_catalog.json"),
                catalog);
            _store.AppendJsonLine(
                ProjectPaths.DataDirectory(projectRoot),
                "field_data_imported",
                new
                {
                    batch_id = batchId,
                    workarea_path = workarea,
                    imported = results.Count(x => x.Imported),
                    duplicates = results.Count(x => x.Duplicate)
                });

            return new FieldDataImportBatchResult(
                batchId,
                finalDirectory,
                results);
        }
        finally
        {
            if (Directory.Exists(stagingDirectory))
            {
                try
                {
                    Directory.Delete(
                        stagingDirectory,
                        recursive: true);
                }
                catch
                {
                }
            }
        }
    }

    public FieldDataConsistencyReport EnrichFromAnalysis(
        string root,
        FieldMagFolderAnalysis analysis,
        bool fullyParsed = false)
    {
        string projectRoot = Path.GetFullPath(root);
        List<FieldDataCatalogRecord> rows = List(projectRoot);
        string scope = string.IsNullOrWhiteSpace(analysis.SourceDirectory) ? "" :
            Path.GetFullPath(analysis.SourceDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var scoped = rows.Where(r => scope.Length == 0 || ResolveCatalogPath(projectRoot, r.RelativePath)
            .StartsWith(scope, StringComparison.OrdinalIgnoreCase)).ToArray();

        Dictionary<string, FieldMagFileScan> scans = analysis.Files
            .GroupBy(
                x => Path.GetFullPath(x.Path),
                StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Last())
            .ToDictionary(
                x => Path.GetFullPath(x.Path),
                StringComparer.OrdinalIgnoreCase);

        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool changed = ReconcileMissingFiles(projectRoot, scoped, hashes);
        foreach (FieldDataCatalogRecord row in scoped)
        {
            if (row.Status != "IMPORTED") continue;
            string path = ResolveCatalogPath(
                projectRoot,
                row.RelativePath);
            if (!scans.TryGetValue(
                    path,
                    out FieldMagFileScan? scan))
            {
                continue;
            }

            row.SensorFamily = scan.SensorFamily.ToString();
            row.DataRole = scan.DataRole.ToString();
            row.RecordCount = scan.RecordCount;
            row.FirstTimeUtc =
                scan.FirstTimeUtc?.ToString("O") ?? "";
            row.LastTimeUtc =
                scan.LastTimeUtc?.ToString("O") ?? "";
            row.BeijingDates = analysis.UavSamples
                .Where(x =>
                    x.SourceFile.Equals(
                        scan.Path,
                        StringComparison.OrdinalIgnoreCase))
                .Select(x =>
                    FieldMagDataService.BeijingDate(
                        x.TimestampUtc))
                .Concat(
                    analysis.BaseSamples
                        .Where(x =>
                            x.SourceFile.Equals(
                                scan.Path,
                                StringComparison.OrdinalIgnoreCase))
                        .Select(x =>
                            FieldMagDataService.BeijingDate(
                                x.TimestampUtc)))
                .Distinct()
                .OrderBy(x => x)
                .Select(x =>
                    x.ToString(
                        "yyyy-MM-dd",
                        CultureInfo.InvariantCulture))
                .ToList();
            row.ParserVersion =
                FieldMagDataService.IncrementalCacheParserVersion;
            changed = true;
        }

        if (changed)
        {
            _store.Write(
                ProjectPaths.Json(
                    projectRoot,
                    "field_data_catalog.json"),
                rows);
        }
        string reportPath = ProjectPaths.Json(projectRoot, "field_data_consistency.json");
        List<FieldDataConsistencyReport> reports = ReadConsistencyReports(reportPath).ToList();
        string sourceDirectory = NormalizeSourceDirectory(analysis.SourceDirectory);
        int previousIndex = reports.FindLastIndex(x =>
            NormalizeSourceDirectory(x.SourceDirectory).Equals(sourceDirectory, StringComparison.OrdinalIgnoreCase));
        FieldDataConsistencyReport? previous = previousIndex >= 0 ? reports[previousIndex] : null;
        bool reparse = !fullyParsed && scoped.Any(r => r.Status == "FILE_CHANGED" && hashes.TryGetValue(r.RelativePath, out var hash) &&
            (previous is null || !NormalizeSourceDirectory(previous.SourceDirectory).Equals(sourceDirectory, StringComparison.OrdinalIgnoreCase) ||
             previous.AnalysisFingerprint != analysis.Fingerprint ||
             previous.RequiresFullReparse || previous.ObservedHashes?.GetValueOrDefault(r.RelativePath) != hash));
        var report = new FieldDataConsistencyReport(analysis.SourceDirectory, analysis.Fingerprint, scoped.Length,
            scoped.Where(r => r.Status is "MISSING_FILE" or "FILE_CHANGED" or "FILE_UNREADABLE")
                .Select(r => new FieldDataConsistencyIssue(r.RelativePath, r.Status)).ToArray())
        {
            ObservedHashes = hashes,
            RequiresFullReparse = reparse,
            SourceFiles = analysis.Files.Select(x => Path.GetFullPath(x.Path))
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray(),
            SourceFileHashes = analysis.Files
                .Where(x => !string.IsNullOrWhiteSpace(x.SourceContentSha256))
                .GroupBy(x => Path.GetFullPath(x.Path), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.Last().SourceContentSha256, StringComparer.OrdinalIgnoreCase),
            SourceFileStableIds = analysis.Files.Select(x => x.SourceFileStableId)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray()
        };
        if (previousIndex >= 0) reports[previousIndex] = report;
        else reports.Add(report);
        _store.Write(reportPath, new FieldDataConsistencyLedger { Reports = reports });
        return report;
    }

    private IReadOnlyList<FieldDataConsistencyReport> ReadConsistencyReports(string path)
    {
        if (!File.Exists(path)) return [];
        string json = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(json)) return [];
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind == JsonValueKind.Array)
            return JsonSerializer.Deserialize<List<FieldDataConsistencyReport>>(json, _store.Options) ?? [];
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("外业数据一致性记录格式无效，保留原文件。");
        if (document.RootElement.TryGetProperty("reports", out _))
            return (_store.Read(path, new FieldDataConsistencyLedger())).Reports;

        // Migrate the previous single-report object on the next successful analysis.
        FieldDataConsistencyReport? legacy =
            JsonSerializer.Deserialize<FieldDataConsistencyReport>(json, _store.Options);
        return legacy is null ? [] : [legacy];
    }

    internal static string NormalizeSourceDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return "";
        string full = Path.GetFullPath(directory);
        string root = Path.GetPathRoot(full) ?? "";
        string normalized = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return normalized.Length == 0 ? root : normalized;
    }

    private static bool ReconcileMissingFiles(
        string projectRoot,
        IEnumerable<FieldDataCatalogRecord> rows,
        Dictionary<string, string>? hashes = null)
    {
        bool changed = false;
        foreach (FieldDataCatalogRecord row in rows)
        {
            string path = ResolveCatalogPath(projectRoot, row.RelativePath);
            if (row.Status is not ("IMPORTED" or "MISSING_FILE" or "FILE_CHANGED" or "FILE_UNREADABLE")) continue;
            string status;
            try
            {
                // Open directly so access denied is not misreported as missing.
                string hash = Sha256(path);
                if (hashes is not null) hashes[row.RelativePath] = hash;
                status = hash.Equals(row.Sha256, StringComparison.OrdinalIgnoreCase) ? "IMPORTED" : "FILE_CHANGED";
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { status = "MISSING_FILE"; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { status = "FILE_UNREADABLE"; }
            if (row.Status != status)
            {
                row.Status = status;
                changed = true;
            }
        }
        return changed;
    }

    public static string ResolveCatalogPath(
        string projectRoot,
        string storedPath)
    {
        if (storedPath.StartsWith(
            "project:",
            StringComparison.OrdinalIgnoreCase))
        {
            string relative =
                storedPath["project:".Length..]
                    .Replace(
                        '/',
                        Path.DirectorySeparatorChar);
            return Path.GetFullPath(
                Path.Combine(
                    projectRoot,
                    relative));
        }

        if (storedPath.StartsWith(
            "absolute:",
            StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFullPath(
                storedPath["absolute:".Length..]);
        }

        return Path.GetFullPath(storedPath);
    }

    private static string ProjectRelativeOrAbsolute(
        string projectRoot,
        string path)
    {
        string root = Path.GetFullPath(projectRoot)
            .TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
        string full = Path.GetFullPath(path);

        if (full.Equals(
            root,
            StringComparison.OrdinalIgnoreCase))
        {
            return "project:.";
        }

        string prefix =
            root +
            Path.DirectorySeparatorChar;
        if (full.StartsWith(
            prefix,
            StringComparison.OrdinalIgnoreCase))
        {
            return
                "project:" +
                Path.GetRelativePath(
                    root,
                    full)
                .Replace('\\', '/');
        }

        return "absolute:" + full;
    }

    private static string UniquePath(
        string directory,
        string fileName,
        string sha256)
    {
        string safeName =
            Path.GetFileName(fileName);
        string candidate =
            Path.Combine(
                directory,
                safeName);
        if (!File.Exists(candidate))
            return candidate;

        string stem =
            Path.GetFileNameWithoutExtension(
                safeName);
        string ext =
            Path.GetExtension(safeName);
        for (int i = 0; i < 1000; i++)
        {
            string suffix = i == 0
                ? sha256[..8]
                : $"{sha256[..8]}_{i:D2}";
            candidate = Path.Combine(
                directory,
                $"{stem}__{suffix}{ext}");
            if (!File.Exists(candidate))
                return candidate;
        }

        return Path.Combine(
            directory,
            $"{stem}__{Guid.NewGuid():N}{ext}");
    }

    private static string Sha256(string path)
    {
        using FileStream stream =
            File.OpenRead(path);
        return Convert.ToHexString(
                SHA256.HashData(stream))
            .ToLowerInvariant();
    }
}
