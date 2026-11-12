using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using System.Text.RegularExpressions;
using MagOps.Infrastructure.Storage;

namespace MagOps.Infrastructure.Services;

public enum FieldMagSensorFamily
{
    Unknown,
    UavOpticalPump,
    Gsm19T,
    DiurnalCorrection,
    Gsm19TG
}

public enum FieldMagDataRole
{
    Unknown,
    Raw,
    Aggregate,
    DiurnalSegment,
    DiurnalMerged,
    OtherDerived
}

public sealed record FieldMagFileScan(
    string Path,
    FieldMagSensorFamily SensorFamily,
    FieldMagDataRole DataRole,
    int RecordCount,
    DateTimeOffset? FirstTimeUtc,
    DateTimeOffset? LastTimeUtc,
    string Evidence,
    IReadOnlyList<string> Warnings)
{
    /// <summary>Content-derived source identity; independent of file path/name.</summary>
    public string SourceFileStableId { get; init; } = "";
    /// <summary>SHA-256 of the exact bytes decoded to produce this parsed snapshot.</summary>
    public string SourceContentSha256 { get; init; } = "";
}

public sealed record UavMagCanonicalSample(
    DateTimeOffset TimestampUtc,
    double? Longitude,
    double? Latitude,
    double? AltitudeM,
    double? LaserAltitudeM,
    double? MagneticFieldNt,
    double? Signal,
    string GpsStatus,
    int? Satellites,
    string SensorStatus,
    string SourceFile,
    int SourceRecord,
    IReadOnlyList<string> QualityFlags);

public sealed record Gsm19TCanonicalSample(
    DateTimeOffset TimestampUtc,
    double MagneticFieldNt,
    double? Sq,
    string SourceFile,
    int SourceRecord,
    IReadOnlyList<string> QualityFlags)
{
    public string SourceFileStableId { get; init; } = "";
    public string SessionStableId { get; init; } = "";
    public int SessionOrdinal { get; init; }
}

public enum GsmSessionBoundaryKind
{
    FileStart,
    ExplicitHeader,
    UtcDateRollover,
    LongGap
}

public sealed record GsmSessionBoundaryEvidence(
    GsmSessionBoundaryKind Kind,
    int SourceRecord,
    DateTimeOffset TimestampUtc,
    string Detail);

public sealed record GsmSession(
    string SessionStableId,
    string SourceFileStableId,
    string SourceFile,
    int Ordinal,
    DateTimeOffset StartTimeUtc,
    DateTimeOffset EndTimeUtc,
    int FirstSourceRecord,
    int LastSourceRecord,
    int RecordCount,
    string ColumnEvidence,
    IReadOnlyList<GsmSessionBoundaryEvidence> BoundaryEvidence,
    string QcStatus,
    IReadOnlyList<string> QcReasons);

public sealed record FieldDataQualityIssue(
    string Severity,
    string Code,
    string Scope,
    string TimeRangeUtc,
    string Message);

public sealed record FieldMagDateSummary(
    DateTime UtcDate,
    int UavFileCount,
    int UavRecordCount,
    int BaseFileCount,
    int BaseRecordCount,
    int FlaggedRecordCount,
    DateTimeOffset? FirstTimeUtc,
    DateTimeOffset? LastTimeUtc)
{
    public bool HasFlightData => UavRecordCount > 0;
}

public sealed record FieldMagBeijingDateSummary(
    DateTime BeijingDate,
    int UavFileCount,
    int UavRecordCount,
    int BaseFileCount,
    int BaseRecordCount,
    int FlaggedRecordCount,
    DateTimeOffset? FirstTimeUtc,
    DateTimeOffset? LastTimeUtc)
{
    public bool HasFlightData => UavRecordCount > 0;
}

public sealed record GemLinkBridgeJob(
    DateTime UtcDate,
    int RoverRecordCount,
    int BaseRecordCount,
    DateTimeOffset? RoverFirstTimeUtc,
    DateTimeOffset? RoverLastTimeUtc,
    DateTimeOffset? BaseFirstTimeUtc,
    DateTimeOffset? BaseLastTimeUtc,
    IReadOnlyList<string> Warnings);

public sealed record FieldMagExportResult(
    IReadOnlyList<string> Files,
    string ManifestPath);

public sealed record GemLinkBridgeExportResult(
    IReadOnlyList<string> Files,
    IReadOnlyList<GemLinkBridgeJob> Jobs,
    string ManifestPath);

public sealed record FieldMagIncrementalScanStatistics(
    int DiscoveredFiles,
    int ReusedFiles,
    int AddedFiles,
    int ChangedFiles,
    int RemovedFiles,
    int ReparsedCacheMissFiles,
    int UnstableFiles,
    int CacheReadFailures,
    int CacheWriteFailures,
    bool CacheInvalidated,
    bool ForceFullScan)
{
    public int ParsedFiles =>
        AddedFiles + ChangedFiles + ReparsedCacheMissFiles;
}

public sealed record FieldMagIncrementalCacheSummary(
    DateTimeOffset UpdatedAtUtc,
    string ParserVersion,
    int IndexedFileCount,
    int UavRecordCount,
    int BaseRecordCount,
    int BeijingDateCount,
    int WarningCount,
    string Fingerprint,
    FieldMagIncrementalScanStatistics LastScan);

public sealed record FieldMagIncrementalAnalysisResult(
    FieldMagFolderAnalysis Analysis,
    FieldMagIncrementalScanStatistics Statistics,
    FieldMagIncrementalCacheSummary Summary,
    string CacheDirectory,
    string SourceSnapshotKey);

public sealed record FieldMagCachedAnalysisRestoreResult(
    FieldMagFolderAnalysis Analysis,
    FieldMagIncrementalCacheSummary Summary,
    string SourceSnapshotKey,
    DateTimeOffset UpdatedAtUtc);

public sealed class FieldMagFolderAnalysis
{
    public string ProjectTimeZoneId { get; init; } = "Asia/Shanghai";
    public string SourceDirectory { get; init; } = "";
    public string Fingerprint { get; init; } = "";
    public IReadOnlyList<FieldMagFileScan> Files { get; init; } = [];
    public IReadOnlyList<UavMagCanonicalSample> UavSamples { get; init; } = [];
    public IReadOnlyList<Gsm19TCanonicalSample> BaseSamples { get; init; } = [];
    public IReadOnlyList<GsmSession> GsmSessions { get; init; } = [];
    public IReadOnlyList<FieldDataQualityIssue> QualityIssues { get; init; } = [];
    public IReadOnlyList<GemLinkBridgeJob> GemLinkJobs { get; init; } = [];
    public int UavRawFileCount => Files.Count(x => x.SensorFamily == FieldMagSensorFamily.UavOpticalPump && x.DataRole == FieldMagDataRole.Raw);
    public int BaseRawFileCount => Files.Count(x => x.SensorFamily == FieldMagSensorFamily.Gsm19T && x.DataRole == FieldMagDataRole.Raw);
    public int DerivedFileCount => Files.Count(x => x.DataRole is FieldMagDataRole.Aggregate or FieldMagDataRole.DiurnalSegment or FieldMagDataRole.DiurnalMerged or FieldMagDataRole.OtherDerived);
    public int WarningCount => QualityIssues.Count(x => !x.Severity.Equals("INFO", StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Field-data ingestion layer for optical-pump UAV magnetometer and GEM GSM-19T
/// files. It performs classification, record-level recovery, full-project
/// canonical integration and quality checks. Native diurnal correction is
/// implemented independently in MagOps.Core.Diurnal; UTC dates remain an
/// internal calculation boundary rather than a required physical-file split.
/// </summary>
public sealed class FieldMagDataService
{
    public const string IncrementalCacheSchemaVersion = "field-mag-cache-1";
    public const string IncrementalCacheParserVersion = "field-mag-parser-6-gsm-role-date-guard";
    private const string IncrementalCacheIndexFileName = "index.json";
    private const string IncrementalCacheSummaryFileName = "summary.json";
    private const string IncrementalAnalysisSnapshotFileName = "analysis.json.gz";

    private static readonly JsonSerializerOptions CacheJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    public static readonly TimeSpan BeijingUtcOffset = TimeSpan.FromHours(8);
    public const string BeijingProgressTimeZone = "Asia/Shanghai (UTC+08:00)";

    public static DateTime BeijingDate(DateTimeOffset timestampUtc) =>
        timestampUtc.ToOffset(BeijingUtcOffset).Date;

    private static readonly string[] SupportedExtensions =
    [
        ".txt", ".csv", ".tsv", ".dat", ".asc", ".xyz", ".log", ".stn"
    ];

    private static readonly Regex UavTimestampRegex = new(
        @"(?<!\d)(?<date>20\d{2}(?:[-/.]?\d{2}){2})\s*[,;\t ]+\s*(?<time>\d{1,2}:\d{2}:\d{2}(?:\.\d+)?)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // GSM-19T records are line-oriented. Do not use \s between fields here:
    // in .NET regex \s also matches CR/LF, which can merge adjacent data lines
    // into one match and silently drop every following record in that match.
    private static readonly Regex BaseRowRegex = new(
        @"(?m)^[\t ]*(?<time>\d{6}(?:\.\d+)?)[\t ]+(?<mag>[+-]?\d+(?:\.\d+)?)(?:[\t ]+(?<sq>[+-]?\d+(?:\.\d+)?))?(?:[\t ]+.*)?\r?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex IsoDateRegex = new(
        @"(?<!\d)(?<year>20\d{2})[-/.]?(?<month>\d{2})[-/.]?(?<day>\d{2})(?!\d)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ShortDateRegex = new(
        @"(?<!\d)(?<month>\d{1,2})[-/.](?<day>\d{1,2})[-/.](?<year>\d{2})(?!\d)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Native GEM GSM-19T survey headers use a day + Roman-month + year form,
    // for example: "/ID 1 file 14 survey.b   09 IX 26". Some real
    // GSM-19T exports omit the separator before survey.b, e.g.
    // "/ID 2025027 file 07survey.b   15 IX 26".
    // Some continuation files may lose the "/ID" prefix but retain
    // "file N survey.b DD <RomanMonth> YY", so do not require /ID here.
    private static readonly Regex GsmSurveyFileDateRegex = new(
        @"(?im)\bfile[\t ]+\d+[\t ]*survey\.b[\t ]+(?<day>\d{1,2})[\t ]+(?<month>XII|XI|IX|VIII|VII|VI|IV|III|II|I|X|V)[\t ]+(?<year>\d{2,4})(?=[\t ]|\r?$)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex DiurnalMarkerRegex = new(
        @"(?<![A-Za-z0-9_])i(?:---|\d{3}|\+\+\+)(?![A-Za-z0-9_])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string BuildSourceSnapshotKey(string sourceDirectory)
    {
        if (string.IsNullOrWhiteSpace(sourceDirectory))
            return "";
        string root = Path.GetFullPath(sourceDirectory.Trim());
        if (!Directory.Exists(root))
            return root;

        List<SourceFileMetadata> files = DiscoverFiles(root)
            .Select(path => SourceMetadata(root, path))
            .ToList();
        return ComputeSourceSnapshotKey(root, files);
    }

    public static string ResolveIncrementalCacheDirectory(
        string projectRoot,
        string sourceDirectory)
    {
        string project = Path.GetFullPath(projectRoot);
        string source = Path.GetFullPath(sourceDirectory);
        string projectPrefix =
            project.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;

        string locator;
        if (source.Equals(project, StringComparison.OrdinalIgnoreCase))
        {
            locator = "project:.";
        }
        else if (source.StartsWith(
            projectPrefix,
            StringComparison.OrdinalIgnoreCase))
        {
            locator =
                "project:" +
                Path.GetRelativePath(project, source)
                    .Replace('\\', '/');
        }
        else
        {
            locator =
                "external:" +
                source.Replace('\\', '/');
        }

        string key = Convert.ToHexString(
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(
                        locator.ToUpperInvariant())))
            .ToLowerInvariant()[..24];

        return Path.Combine(
            project,
            "00_项目管理",
            "magops",
            "cache",
            "field",
            key);
    }

    public static bool TryReadIncrementalCacheSummary(
        string cacheDirectory,
        out FieldMagIncrementalCacheSummary summary)
    {
        summary = default!;
        if (string.IsNullOrWhiteSpace(cacheDirectory))
            return false;

        string path = Path.Combine(
            Path.GetFullPath(cacheDirectory),
            IncrementalCacheSummaryFileName);
        if (!File.Exists(path))
            return false;

        try
        {
            FieldMagIncrementalCacheSummary? loaded =
                JsonSerializer.Deserialize<FieldMagIncrementalCacheSummary>(
                    File.ReadAllText(path, Encoding.UTF8),
                    CacheJsonOptions);
            if (loaded is null ||
                !loaded.ParserVersion.Equals(
                    IncrementalCacheParserVersion,
                    StringComparison.Ordinal))
            {
                return false;
            }

            summary = loaded;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryRestoreIncrementalAnalysis(
        string sourceDirectory,
        string cacheDirectory,
        out FieldMagCachedAnalysisRestoreResult result)
    {
        result = default!;
        if (string.IsNullOrWhiteSpace(sourceDirectory) ||
            string.IsNullOrWhiteSpace(cacheDirectory))
        {
            return false;
        }

        string sourceRoot;
        string cacheRoot;
        try
        {
            sourceRoot = Path.GetFullPath(sourceDirectory);
            cacheRoot = Path.GetFullPath(cacheDirectory);
        }
        catch
        {
            return false;
        }

        if (!Directory.Exists(sourceRoot) ||
            !TryReadIncrementalCacheSummary(
                cacheRoot,
                out FieldMagIncrementalCacheSummary summary))
        {
            return false;
        }

        string path = Path.Combine(
            cacheRoot,
            IncrementalAnalysisSnapshotFileName);
        if (!File.Exists(path))
            return false;

        try
        {
            FieldMagAnalysisSnapshotDocument? snapshot;
            using (FileStream stream =
                File.OpenRead(path))
            using (var gzip =
                new GZipStream(
                    stream,
                    CompressionMode.Decompress))
            {
                snapshot =
                    JsonSerializer.Deserialize<FieldMagAnalysisSnapshotDocument>(
                        gzip,
                        CacheJsonOptions);
            }

            if (snapshot is null ||
                !snapshot.SchemaVersion.Equals(
                    IncrementalCacheSchemaVersion,
                    StringComparison.Ordinal) ||
                !snapshot.ParserVersion.Equals(
                    IncrementalCacheParserVersion,
                    StringComparison.Ordinal) ||
                !snapshot.Analysis.Fingerprint.Equals(
                    summary.Fingerprint,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            FieldMagFolderAnalysis analysis =
                RebaseCachedAnalysis(
                    snapshot.Analysis,
                    snapshot.SourceDirectory,
                    sourceRoot);
            result =
                new FieldMagCachedAnalysisRestoreResult(
                    analysis,
                    summary,
                    snapshot.SourceSnapshotKey,
                    snapshot.UpdatedAtUtc);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public FieldMagIncrementalAnalysisResult AnalyzeFolderIncremental(
        string sourceDirectory,
        string cacheDirectory,
        bool forceFullScan = false)
    {
        if (string.IsNullOrWhiteSpace(sourceDirectory))
            throw new ArgumentException(
                "项目外业数据目录不能为空。",
                nameof(sourceDirectory));
        if (string.IsNullOrWhiteSpace(cacheDirectory))
            throw new ArgumentException(
                "项目外业缓存目录不能为空。",
                nameof(cacheDirectory));

        string root = Path.GetFullPath(sourceDirectory.Trim());
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException(
                $"项目外业数据目录不存在：{root}");

        string cacheRoot = Path.GetFullPath(cacheDirectory);
        string cacheFilesDirectory =
            Path.Combine(cacheRoot, "files");
        Directory.CreateDirectory(cacheFilesDirectory);

        string indexPath = Path.Combine(
            cacheRoot,
            IncrementalCacheIndexFileName);
        FieldMagCacheIndexDocument? oldIndex =
            TryReadCacheIndex(
                indexPath,
                out bool cacheInvalidated);

        Dictionary<string, FieldMagCacheIndexEntry> oldEntries =
            oldIndex?.Files
                .GroupBy(
                    x => x.RelativePath,
                    StringComparer.OrdinalIgnoreCase)
                .Select(x => x.Last())
                .ToDictionary(
                    x => x.RelativePath,
                    StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, FieldMagCacheIndexEntry>(
                StringComparer.OrdinalIgnoreCase);

        List<string> paths = DiscoverFiles(root);
        var metadata = new List<SourceFileMetadata>(paths.Count);
        var parsed = new List<ParsedMagFile>(paths.Count);
        var newEntries = new List<FieldMagCacheIndexEntry>(paths.Count);
        var discoveredPaths =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        int reused = 0;
        int added = 0;
        int changed = 0;
        int reparsedCacheMiss = 0;
        int unstable = 0;
        int cacheReadFailures = 0;
        int cacheWriteFailures = 0;

        foreach (string path in paths)
        {
            SourceFileMetadata current;
            try
            {
                current = SourceMetadata(root, path);
            }
            catch
            {
                continue;
            }

            metadata.Add(current);
            discoveredPaths.Add(current.RelativePath);
            oldEntries.TryGetValue(
                current.RelativePath,
                out FieldMagCacheIndexEntry? previous);

            bool metadataUnchanged =
                !forceFullScan &&
                !cacheInvalidated &&
                previous is not null &&
                previous.Length == current.Length &&
                previous.LastWriteTimeUtcTicks ==
                    current.LastWriteTimeUtcTicks;

            if (metadataUnchanged)
            {
                try
                {
                    ParsedMagFile cached =
                        ReadParsedCache(
                            cacheRoot,
                            root,
                            previous!);
                    parsed.Add(cached);
                    newEntries.Add(previous!);
                    reused++;
                    continue;
                }
                catch
                {
                    cacheReadFailures++;
                    reparsedCacheMiss++;
                }
            }
            else if (previous is null)
            {
                added++;
            }
            else
            {
                changed++;
            }

            long beforeLength;
            long beforeTicks;
            try
            {
                var before = new FileInfo(path);
                if (!before.Exists)
                    throw new FileNotFoundException(
                        "扫描前文件已不存在。",
                        path);
                beforeLength = before.Length;
                beforeTicks =
                    before.LastWriteTimeUtc.Ticks;
            }
            catch (Exception ex)
            {
                unstable++;
                parsed.Add(
                    ParseFailure(
                        path,
                        $"扫描前无法稳定读取文件：{ex.Message}"));
                continue;
            }

            ParsedMagFile parsedFile;
            bool parseSucceeded = true;
            try
            {
                parsedFile = Parse(path);
            }
            catch (Exception ex)
            {
                parseSucceeded = false;
                parsedFile = ParseFailure(
                    path,
                    $"读取失败：{ex.Message}");
            }

            bool stable = false;
            try
            {
                var after = new FileInfo(path);
                stable =
                    after.Exists &&
                    after.Length == beforeLength &&
                    after.LastWriteTimeUtc.Ticks ==
                        beforeTicks;
            }
            catch
            {
                stable = false;
            }

            if (!stable)
            {
                unstable++;
                parsedFile = ParseFailure(
                    path,
                    "扫描期间文件仍在写入或发生变化；本次未采用该文件内容，下一次扫描将重新解析");
                parseSucceeded = false;
            }

            parsed.Add(parsedFile);

            if (!parseSucceeded)
                continue;

            string cacheFile =
                CacheFileRelativePath(
                    current.RelativePath);
            var entry = new FieldMagCacheIndexEntry(
                current.RelativePath,
                current.Length,
                current.LastWriteTimeUtcTicks,
                cacheFile,
                parsedFile.SensorFamily,
                parsedFile.DataRole,
                parsedFile.RecordCount,
                parsedFile.FirstTimeUtc,
                parsedFile.LastTimeUtc,
                AffectedBeijingDates(parsedFile));

            try
            {
                WriteParsedCache(
                    cacheRoot,
                    parsedFile,
                    current.RelativePath,
                    cacheFile);
                newEntries.Add(entry);
            }
            catch
            {
                cacheWriteFailures++;
            }
        }

        int removed = oldEntries.Keys.Count(
            path => !discoveredPaths.Contains(path));

        FieldMagFolderAnalysis analysis =
            BuildFolderAnalysis(root, parsed);

        string snapshotKey =
            ComputeSourceSnapshotKey(
                root,
                metadata);

        var preliminaryStats =
            new FieldMagIncrementalScanStatistics(
                metadata.Count,
                reused,
                added,
                changed,
                removed,
                reparsedCacheMiss,
                unstable,
                cacheReadFailures,
                cacheWriteFailures,
                cacheInvalidated,
                forceFullScan);

        var summary =
            new FieldMagIncrementalCacheSummary(
                DateTimeOffset.UtcNow,
                IncrementalCacheParserVersion,
                newEntries.Count,
                analysis.UavSamples.Count,
                analysis.BaseSamples.Count,
                BuildBeijingDateSummaries(analysis).Count,
                analysis.WarningCount,
                analysis.Fingerprint,
                preliminaryStats);

        bool indexPersisted = false;
        try
        {
            var index = new FieldMagCacheIndexDocument(
                IncrementalCacheSchemaVersion,
                IncrementalCacheParserVersion,
                DateTimeOffset.UtcNow,
                newEntries);
            WriteJsonAtomic(indexPath, index);
            indexPersisted = true;
        }
        catch
        {
            cacheWriteFailures++;
        }

        if (indexPersisted)
        {
            try
            {
                WriteAnalysisSnapshot(
                    cacheRoot,
                    root,
                    snapshotKey,
                    analysis);
            }
            catch
            {
                cacheWriteFailures++;
            }

            try
            {
                PruneUnreferencedCacheFiles(
                    cacheRoot,
                    newEntries);
            }
            catch
            {
                cacheWriteFailures++;
            }
        }

        FieldMagIncrementalScanStatistics finalStats =
            preliminaryStats with
            {
                CacheWriteFailures =
                    cacheWriteFailures
            };
        summary = summary with
        {
            LastScan = finalStats
        };

        if (indexPersisted)
        {
            try
            {
                WriteJsonAtomic(
                    Path.Combine(
                        cacheRoot,
                        IncrementalCacheSummaryFileName),
                    summary);
            }
            catch
            {
                finalStats = finalStats with
                {
                    CacheWriteFailures =
                        finalStats.CacheWriteFailures + 1
                };
                summary = summary with
                {
                    LastScan = finalStats
                };
            }
        }

        return new FieldMagIncrementalAnalysisResult(
            analysis,
            finalStats,
            summary,
            cacheRoot,
            snapshotKey);
    }

    public FieldMagFolderAnalysis AnalyzeFolder(string sourceDirectory)
    {
        if (string.IsNullOrWhiteSpace(sourceDirectory))
            throw new ArgumentException("项目外业数据目录不能为空。", nameof(sourceDirectory));

        string root = Path.GetFullPath(sourceDirectory.Trim());
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"项目外业数据目录不存在：{root}");

        List<string> paths = DiscoverFiles(root);
        var parsed = new List<ParsedMagFile>(paths.Count);
        foreach (string path in paths)
        {
            try
            {
                parsed.Add(Parse(path));
            }
            catch (Exception ex)
            {
                parsed.Add(new ParsedMagFile(
                    path,
                    FieldMagSensorFamily.Unknown,
                    FieldMagDataRole.Unknown,
                    [],
                    [],
                    null,
                    null,
                    "读取失败",
                    [$"读取失败：{ex.Message}"]));
            }
        }

        return BuildFolderAnalysis(root, parsed);
    }

    public static IReadOnlyList<FieldMagDateSummary> BuildDateSummaries(
        FieldMagFolderAnalysis analysis)
    {
        DateTime[] dates = analysis.UavSamples
            .Select(x => x.TimestampUtc.UtcDateTime.Date)
            .Concat(analysis.BaseSamples.Select(x => x.TimestampUtc.UtcDateTime.Date))
            .Distinct()
            .OrderBy(x => x)
            .ToArray();

        var result = new List<FieldMagDateSummary>(dates.Length);
        foreach (DateTime date in dates)
        {
            List<UavMagCanonicalSample> uav = analysis.UavSamples
                .Where(x => x.TimestampUtc.UtcDateTime.Date == date)
                .ToList();
            List<Gsm19TCanonicalSample> baseRows = analysis.BaseSamples
                .Where(x => x.TimestampUtc.UtcDateTime.Date == date)
                .ToList();
            List<DateTimeOffset> stamps = uav.Select(x => x.TimestampUtc)
                .Concat(baseRows.Select(x => x.TimestampUtc))
                .OrderBy(x => x)
                .ToList();

            result.Add(new FieldMagDateSummary(
                date,
                uav.Select(x => x.SourceFile).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                uav.Count,
                baseRows.Select(x => x.SourceFile).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                baseRows.Count,
                uav.Count(x => x.QualityFlags.Count > 0) +
                    baseRows.Count(x => x.QualityFlags.Count > 0),
                stamps.Count == 0 ? null : stamps[0],
                stamps.Count == 0 ? null : stamps[^1]));
        }

        return result;
    }

    public static IReadOnlyList<FieldMagBeijingDateSummary> BuildBeijingDateSummaries(
        FieldMagFolderAnalysis analysis) => BuildProjectDateSummaries(analysis, ProjectTime.Beijing);

    public static IReadOnlyList<FieldMagBeijingDateSummary> BuildProjectDateSummaries(
        FieldMagFolderAnalysis analysis, TimeZoneInfo? timeZone = null)
    {
        timeZone ??= TimeZoneInfo.FindSystemTimeZoneById(analysis.ProjectTimeZoneId);
        DateTime[] dates = analysis.UavSamples
            .Select(x => ProjectTime.Date(x.TimestampUtc, timeZone))
            .Concat(analysis.BaseSamples.Select(x => ProjectTime.Date(x.TimestampUtc, timeZone)))
            .Distinct()
            .OrderBy(x => x)
            .ToArray();

        var result = new List<FieldMagBeijingDateSummary>(dates.Length);
        foreach (DateTime date in dates)
        {
            List<UavMagCanonicalSample> uav = analysis.UavSamples
                .Where(x => ProjectTime.Date(x.TimestampUtc, timeZone) == date)
                .ToList();
            List<Gsm19TCanonicalSample> baseRows = analysis.BaseSamples
                .Where(x => ProjectTime.Date(x.TimestampUtc, timeZone) == date)
                .ToList();
            List<DateTimeOffset> stamps = uav.Select(x => x.TimestampUtc)
                .Concat(baseRows.Select(x => x.TimestampUtc))
                .OrderBy(x => x)
                .ToList();

            result.Add(new FieldMagBeijingDateSummary(
                date,
                uav.Select(x => x.SourceFile).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                uav.Count,
                baseRows.Select(x => x.SourceFile).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                baseRows.Count,
                uav.Count(x => x.QualityFlags.Count > 0) +
                    baseRows.Count(x => x.QualityFlags.Count > 0),
                stamps.Count == 0 ? null : stamps[0],
                stamps.Count == 0 ? null : stamps[^1]));
        }

        return result;
    }

    public static FieldMagFolderAnalysis SliceByBeijingDate(
        FieldMagFolderAnalysis analysis,
        DateTime beijingDate) => SliceByProjectDate(analysis, beijingDate, ProjectTime.Beijing);

    public static FieldMagFolderAnalysis SliceByProjectDate(
        FieldMagFolderAnalysis analysis, DateTime beijingDate, TimeZoneInfo? timeZone = null)
    {
        timeZone ??= TimeZoneInfo.FindSystemTimeZoneById(analysis.ProjectTimeZoneId);
        DateTime date = beijingDate.Date;
        List<UavMagCanonicalSample> uav = analysis.UavSamples
            .Where(x => ProjectTime.Date(x.TimestampUtc, timeZone) == date)
            .ToList();
        List<Gsm19TCanonicalSample> baseRows = analysis.BaseSamples
            .Where(x => ProjectTime.Date(x.TimestampUtc, timeZone) == date)
            .ToList();

        HashSet<string> sourceFiles = uav.Select(x => x.SourceFile)
            .Concat(baseRows.Select(x => x.SourceFile))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var scans = new List<FieldMagFileScan>();
        foreach (FieldMagFileScan scan in analysis.Files.Where(x => sourceFiles.Contains(x.Path)))
        {
            List<DateTimeOffset> stamps = uav
                .Where(x => x.SourceFile.Equals(scan.Path, StringComparison.OrdinalIgnoreCase))
                .Select(x => x.TimestampUtc)
                .Concat(baseRows
                    .Where(x => x.SourceFile.Equals(scan.Path, StringComparison.OrdinalIgnoreCase))
                    .Select(x => x.TimestampUtc))
                .OrderBy(x => x)
                .ToList();

            int recordCount =
                uav.Count(x => x.SourceFile.Equals(scan.Path, StringComparison.OrdinalIgnoreCase)) +
                baseRows.Count(x => x.SourceFile.Equals(scan.Path, StringComparison.OrdinalIgnoreCase));
            scans.Add(scan with
            {
                RecordCount = recordCount,
                FirstTimeUtc = stamps.Count == 0 ? null : stamps[0],
                LastTimeUtc = stamps.Count == 0 ? null : stamps[^1]
            });
        }

        // One Beijing day spans parts of two UTC dates. Quality issues only carry
        // a rendered UTC range string, so include issues from those two possible
        // UTC calendar dates without changing the underlying QC rules.
        DateTime firstUtcDate = ProjectTime.DayStartUtc(date, timeZone).UtcDateTime.Date;
        DateTime lastUtcDate = ProjectTime.DayStartUtc(date.AddDays(1), timeZone).AddTicks(-1).UtcDateTime.Date;
        string utcDateA = firstUtcDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string utcDateB = lastUtcDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        List<FieldDataQualityIssue> issues = analysis.QualityIssues
            .Where(x =>
                x.TimeRangeUtc.Contains(utcDateA, StringComparison.Ordinal) ||
                x.TimeRangeUtc.Contains(utcDateB, StringComparison.Ordinal))
            .ToList();

        return new FieldMagFolderAnalysis
        {
            SourceDirectory = analysis.SourceDirectory,
            ProjectTimeZoneId = timeZone.Id,
            Fingerprint = $"{analysis.Fingerprint}:TZ:{timeZone.Id}:{date:yyyyMMdd}",
            Files = scans,
            UavSamples = uav,
            BaseSamples = baseRows,
            GsmSessions = analysis.GsmSessions
                .Where(session => baseRows.Any(row =>
                    row.SessionStableId.Equals(session.SessionStableId, StringComparison.OrdinalIgnoreCase)))
                .ToList(),
            QualityIssues = issues,
            GemLinkJobs = analysis.GemLinkJobs
                .Where(x =>
                    x.UtcDate.Date >= firstUtcDate &&
                    x.UtcDate.Date <= lastUtcDate)
                .ToList()
        };
    }

    public static FieldMagFolderAnalysis SliceByUtcDate(
        FieldMagFolderAnalysis analysis,
        DateTime utcDate)
    {
        DateTime date = utcDate.Date;
        List<UavMagCanonicalSample> uav = analysis.UavSamples
            .Where(x => x.TimestampUtc.UtcDateTime.Date == date)
            .ToList();
        List<Gsm19TCanonicalSample> baseRows = analysis.BaseSamples
            .Where(x => x.TimestampUtc.UtcDateTime.Date == date)
            .ToList();

        HashSet<string> sourceFiles = uav.Select(x => x.SourceFile)
            .Concat(baseRows.Select(x => x.SourceFile))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var scans = new List<FieldMagFileScan>();
        foreach (FieldMagFileScan scan in analysis.Files.Where(x => sourceFiles.Contains(x.Path)))
        {
            List<DateTimeOffset> stamps = uav.Where(x => x.SourceFile.Equals(scan.Path, StringComparison.OrdinalIgnoreCase))
                .Select(x => x.TimestampUtc)
                .Concat(baseRows.Where(x => x.SourceFile.Equals(scan.Path, StringComparison.OrdinalIgnoreCase))
                    .Select(x => x.TimestampUtc))
                .OrderBy(x => x)
                .ToList();

            int recordCount = uav.Count(x => x.SourceFile.Equals(scan.Path, StringComparison.OrdinalIgnoreCase)) +
                baseRows.Count(x => x.SourceFile.Equals(scan.Path, StringComparison.OrdinalIgnoreCase));
            scans.Add(scan with
            {
                RecordCount = recordCount,
                FirstTimeUtc = stamps.Count == 0 ? null : stamps[0],
                LastTimeUtc = stamps.Count == 0 ? null : stamps[^1]
            });
        }

        string dateKey = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        List<FieldDataQualityIssue> issues = analysis.QualityIssues
            .Where(x => x.TimeRangeUtc.Contains(dateKey, StringComparison.Ordinal))
            .ToList();

        return new FieldMagFolderAnalysis
        {
            SourceDirectory = analysis.SourceDirectory,
            Fingerprint = $"{analysis.Fingerprint}:{date:yyyyMMdd}",
            Files = scans,
            UavSamples = uav,
            BaseSamples = baseRows,
            GsmSessions = analysis.GsmSessions
                .Where(session => baseRows.Any(row =>
                    row.SessionStableId.Equals(session.SessionStableId, StringComparison.OrdinalIgnoreCase)))
                .ToList(),
            QualityIssues = issues,
            GemLinkJobs = analysis.GemLinkJobs
                .Where(x => x.UtcDate.Date == date)
                .ToList()
        };
    }

    public static FieldSourceSummary ToUavSourceSummary(FieldMagFolderAnalysis analysis)
    {
        IReadOnlyList<UavMagCanonicalSample> usable = ResolveUavTimestampConflicts(analysis.UavSamples)
            .Where(IsUsableUavForTrack)
            .ToList();
        IReadOnlyList<GeoCoordinate> track = DownsampleTrack(
            usable
                .Where(x => x.Longitude.HasValue && x.Latitude.HasValue)
                .Select(x => new GeoCoordinate(x.Longitude!.Value, x.Latitude!.Value))
                .ToList(),
            20d,
            60000);

        IReadOnlyList<string> files = analysis.Files
            .Where(x => x.SensorFamily == FieldMagSensorFamily.UavOpticalPump && x.DataRole == FieldMagDataRole.Raw)
            .Select(x => x.Path)
            .ToList();
        if (files.Count == 0)
        {
            files = analysis.Files
                .Where(x => x.SensorFamily == FieldMagSensorFamily.UavOpticalPump && x.DataRole == FieldMagDataRole.Aggregate)
                .Select(x => x.Path)
                .ToList();
        }

        return new FieldSourceSummary(
            "UAV_MAG",
            files,
            analysis.UavSamples.Count,
            track.Count,
            analysis.UavSamples.Count == 0 ? null : analysis.UavSamples[0].TimestampUtc,
            analysis.UavSamples.Count == 0 ? null : analysis.UavSamples[^1].TimestampUtc,
            track,
            analysis.QualityIssues
                .Where(x => x.Scope.Equals("UAV", StringComparison.OrdinalIgnoreCase) && !x.Severity.Equals("INFO", StringComparison.OrdinalIgnoreCase))
                .Select(x => x.Message)
                .ToList());
    }

    public static FieldSourceSummary ToBaseSourceSummary(FieldMagFolderAnalysis analysis)
    {
        IReadOnlyList<string> files = analysis.Files
            .Where(x => x.SensorFamily == FieldMagSensorFamily.Gsm19T && x.DataRole == FieldMagDataRole.Raw)
            .Select(x => x.Path)
            .ToList();
        if (files.Count == 0)
        {
            files = analysis.Files
                .Where(x => x.SensorFamily == FieldMagSensorFamily.Gsm19T && x.DataRole == FieldMagDataRole.Aggregate)
                .Select(x => x.Path)
                .ToList();
        }

        return new FieldSourceSummary(
            "GSM19T",
            files,
            analysis.BaseSamples.Count,
            0,
            analysis.BaseSamples.Count == 0 ? null : analysis.BaseSamples[0].TimestampUtc,
            analysis.BaseSamples.Count == 0 ? null : analysis.BaseSamples[^1].TimestampUtc,
            [],
            analysis.QualityIssues
                .Where(x => x.Scope.Equals("GSM19T", StringComparison.OrdinalIgnoreCase) && !x.Severity.Equals("INFO", StringComparison.OrdinalIgnoreCase))
                .Select(x => x.Message)
                .ToList());
    }


    public FieldMagExportResult WriteIntegratedCanonicalOutputs(
        string outputDirectory,
        FieldMagFolderAnalysis analysis,
        GeneratedArtifactContext? artifactContext = null)
    {
        string target = Path.GetFullPath(outputDirectory);
        IReadOnlyDictionary<string, string> existingArtifactIds =
            ReadVerifiedArtifactIds(target);
        FieldMagExportResult staged = AtomicDirectoryBundle.Execute(
            target,
            stage => BuildIntegratedCanonicalOutputs(
                stage,
                analysis,
                artifactContext,
                existingArtifactIds),
            (stage, result) =>
            {
                if (!File.Exists(result.ManifestPath))
                    throw new InvalidDataException("磁测整合 manifest 未生成。");
                foreach (string file in result.Files)
                {
                    if (!File.Exists(file) || new FileInfo(file).Length == 0)
                        throw new InvalidDataException($"磁测整合成果缺失或为空：{Path.GetFileName(file)}");
                }
            });

        return new FieldMagExportResult(
            staged.Files.Select(file => Path.Combine(target, Path.GetFileName(file))).ToList(),
            Path.Combine(target, Path.GetFileName(staged.ManifestPath)));
    }

    private FieldMagExportResult BuildIntegratedCanonicalOutputs(
        string outputDirectory,
        FieldMagFolderAnalysis analysis,
        GeneratedArtifactContext? artifactContext = null,
        IReadOnlyDictionary<string, string>? existingArtifactIds = null)
    {
        string directory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(directory);

        // v0.8.9.2 compact-storage migration: remove only the explicitly
        // managed verbose predecessors so a successful re-export actually
        // releases the old hundreds-of-MB disk usage.
        File.Delete(
            Path.Combine(
                directory,
                "UAV磁测_整合.csv"));
        File.Delete(
            Path.Combine(
                directory,
                "GSM19T_日变站_整合.csv"));

        var files = new List<string>();

        MagDataSourceCatalog sourceCatalog =
            MagDataCompactExport.BuildSourceCatalog(
                analysis);
        DiurnalBaseValiditySummary baseValidity =
            DiurnalBaseValidityGate.Evaluate(
                analysis.BaseSamples);
        IReadOnlyList<FieldMagDailyCoverageSummary> coverageSummaries =
            FieldMagCoverageService.Analyze(analysis);

        string inventory =
            Path.Combine(
                directory,
                "文件识别清单.csv");
        WriteInventoryCsv(
            inventory,
            analysis.Files);
        files.Add(inventory);

        string uav =
            Path.Combine(
                directory,
                "UAV磁测_整合.csv.gz");
        WriteIntegratedUavCsvGzip(
            uav,
            analysis.UavSamples,
            sourceCatalog);
        files.Add(uav);

        string basePath =
            Path.Combine(
                directory,
                "GSM19T_日变站_整合.csv.gz");
        WriteIntegratedBaseCsvGzip(
            basePath,
            analysis.BaseSamples,
            sourceCatalog);
        files.Add(basePath);

        string coverageCsv =
            Path.Combine(
                directory,
                "19T覆盖检查_每日摘要.csv");
        WriteCoverageSummaryCsv(coverageCsv, coverageSummaries, TimeZoneInfo.FindSystemTimeZoneById(analysis.ProjectTimeZoneId));
        files.Add(coverageCsv);

        string qc =
            Path.Combine(
                directory,
                "数据质量报告.md");
        WriteIntegratedQualityMarkdown(
            qc,
            analysis);
        files.Add(qc);

        string manifest =
            Path.Combine(
                directory,
                "磁测整合_manifest.json");
        File.WriteAllText(
            manifest,
            JsonSerializer.Serialize(
                new
                {
                    schema_version =
                        "magops-field-mag-integrated-6",
                    generated_at_utc =
                        DateTimeOffset.UtcNow.ToString(
                            "O",
                            CultureInfo.InvariantCulture),
                    source_directory =
                        analysis.SourceDirectory,
                    source_fingerprint =
                        analysis.Fingerprint,
                    physical_partitioning =
                        "none",
                    storage =
                        new
                        {
                            full_data_format =
                                "CSV inside GZip",
                            compression =
                                "gzip / SmallestSize",
                            traceability =
                                "SourceId + SourceRecord",
                            source_path_per_row =
                                false,
                            temporal_downsampling =
                                false,
                            numeric_precision_reduction =
                                false
                        },
                    source_id_policy =
                        "1-based deterministic ids sorted by source path; full source path stored once in manifest",
                    source_files =
                        sourceCatalog.Entries.Select(x =>
                            new
                            {
                                source_id =
                                    x.SourceId,
                                sensor =
                                    x.Sensor,
                                relative_path =
                                    x.RelativePath,
                                original_source =
                                    x.SourceFile,
                                canonical_records =
                                    x.CanonicalRecordCount
                            })
                            .ToArray(),
                    diurnal_grouping_basis =
                        "UTC date inside MagOps native correction engine",
                    business_day_basis =
                        ProjectTime.Basis(TimeZoneInfo.FindSystemTimeZoneById(analysis.ProjectTimeZoneId)),
                    note =
                        "UAV 与 GSM-19T 仍各自覆盖整个工区周期；完整表为 CSV.GZ + SourceId。UTC 日期与时间在 CSV 中分为 DateUtc / TimeUtc 两列，内部计算仍使用完整 TimestampUtc。没有降采样、没有降低磁值/坐标写出精度。",
                    uav_records =
                        analysis.UavSamples.Count,
                    base_records =
                        analysis.BaseSamples.Count,
                    base_records_production_eligible =
                        baseValidity.AcceptedRecords,
                    base_records_production_rejected =
                        baseValidity.RejectedRecords,
                    base_sessions_rejected =
                        baseValidity.RejectedSessions,
                    accepted_base_median_nt =
                        baseValidity.AcceptedMedianFieldNt,
                    daily_base_coverage = coverageSummaries.Select(x =>
                        new
                        {
                            beijing_date = x.BeijingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                            uav_eligible_records = x.UavEligibleRecords,
                            covered_uav_records = x.CoveredUavRecords,
                            uncovered_uav_records = x.UncoveredUavRecords,
                            coverage_percent = x.CoveragePercent,
                            base_records = x.BaseRecords,
                            accepted_base_records = x.AcceptedBaseRecords,
                            rejected_base_records = x.RejectedBaseRecords,
                            status = x.Status,
                            reasons = x.Reasons
                        }).ToArray(),
                    parser_version =
                        IncrementalCacheParserVersion,
                    utc_dates =
                        BuildDateSummaries(analysis)
                            .Select(x =>
                                x.UtcDate.ToString(
                                    "yyyy-MM-dd",
                                    CultureInfo.InvariantCulture))
                            .ToArray(),
                    quality_issue_count =
                        analysis.QualityIssues.Count,
                    outputs =
                        files.Select(x =>
                            new
                            {
                                file =
                                    Path.GetFileName(x),
                                bytes =
                                    new FileInfo(x).Length
                            })
                            .ToArray()
                },
                new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder =
                        JavaScriptEncoder.Create(
                            UnicodeRanges.All)
                }),
            new UTF8Encoding(true));
        files.Add(manifest);

        LabelFieldDataOutputs(
            files,
            analysis,
            "field_data_integration",
            artifactContext,
            existingArtifactIds);

        return new FieldMagExportResult(
            files,
            manifest);
    }

    public FieldMagExportResult WriteCanonicalOutputs(
        string outputDirectory,
        FieldMagFolderAnalysis analysis,
        DateTime operationDate)
    {
        string directory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(directory);
        string prefix = operationDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var files = new List<string>();

        string inventory = Path.Combine(directory, $"{prefix}_文件识别清单.csv");
        WriteInventoryCsv(inventory, analysis.Files);
        files.Add(inventory);

        string uav = Path.Combine(directory, $"{prefix}_UAV_All.csv");
        WriteUavCsv(uav, analysis.UavSamples);
        files.Add(uav);

        string basePath = Path.Combine(directory, $"{prefix}_Base_All.csv");
        WriteBaseCsv(basePath, analysis.BaseSamples);
        files.Add(basePath);

        string qc = Path.Combine(directory, $"{prefix}_数据质量报告.md");
        WriteQualityMarkdown(qc, analysis, operationDate);
        files.Add(qc);

        string manifest = Path.Combine(directory, $"{prefix}_数据整理_manifest.json");
        File.WriteAllText(
            manifest,
            JsonSerializer.Serialize(new
            {
                schema_version = "magops-field-mag-dataset-1",
                operation_date = operationDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                source_directory = analysis.SourceDirectory,
                fingerprint = analysis.Fingerprint,
                file_count = analysis.Files.Count,
                uav_raw_files = analysis.UavRawFileCount,
                gsm19t_raw_files = analysis.BaseRawFileCount,
                derived_files = analysis.DerivedFileCount,
                uav_records = analysis.UavSamples.Count,
                base_records = analysis.BaseSamples.Count,
                quality_issue_count = analysis.QualityIssues.Count,
                utc_dates = analysis.GemLinkJobs.Select(x => x.UtcDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).ToArray(),
                outputs = files.Select(Path.GetFileName).ToArray()
            }, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(true));
        files.Add(manifest);

        return new FieldMagExportResult(files, manifest);
    }

    public FieldMagExportResult WriteCanonicalOutputsByDate(
        string outputDirectory,
        FieldMagFolderAnalysis analysis)
    {
        string directory = Path.GetFullPath(outputDirectory);
        IReadOnlyList<FieldMagDateSummary> dates = BuildDateSummaries(analysis);
        if (dates.Count == 0)
            throw new InvalidOperationException("项目外业数据中没有可按 UTC 日期归类的 UAV / GSM-19T 记录。");

        var files = new List<string>();
        foreach (FieldMagDateSummary date in dates.OrderBy(x => x.UtcDate))
        {
            FieldMagFolderAnalysis slice = SliceByUtcDate(analysis, date.UtcDate);
            string dateDirectory = Path.Combine(
                directory,
                date.UtcDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            FieldMagExportResult daily = WriteCanonicalOutputs(
                dateDirectory,
                slice,
                date.UtcDate);
            files.AddRange(daily.Files);
        }

        Directory.CreateDirectory(directory);
        string manifest = Path.Combine(directory, "项目外业数据_manifest.json");
        File.WriteAllText(
            manifest,
            JsonSerializer.Serialize(new
            {
                schema_version = "magops-field-project-dataset-1",
                source_directory = analysis.SourceDirectory,
                source_fingerprint = analysis.Fingerprint,
                partition_basis = "canonical UTC timestamp date",
                note = "无人机记录直接使用自带 UTC 日期/时间；GSM-19T 使用已识别的绝对 UTC。规范数据按 UTC 日期分目录输出，不要求用户预先指定作业日期。",
                dates = dates.Select(x => new
                {
                    utc_date = x.UtcDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    uav_files = x.UavFileCount,
                    uav_records = x.UavRecordCount,
                    base_files = x.BaseFileCount,
                    base_records = x.BaseRecordCount,
                    flagged_records = x.FlaggedRecordCount
                }).ToArray(),
                outputs = files.Select(x => Path.GetRelativePath(directory, x).Replace('\\', '/')).ToArray()
            },
            new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
            }),
            new UTF8Encoding(true));
        files.Add(manifest);

        return new FieldMagExportResult(files, manifest);
    }

    public GemLinkBridgeExportResult WriteGemLinkBridgePackage(
        string outputDirectory,
        FieldMagFolderAnalysis analysis,
        DateTime operationDate,
        GeneratedArtifactContext? artifactContext = null)
    {
        string target = Path.GetFullPath(outputDirectory);
        IReadOnlyDictionary<string, string> existingArtifactIds =
            ReadVerifiedArtifactIds(target, "field_gemlink_bridge");
        GemLinkBridgeExportResult staged = AtomicDirectoryBundle.Execute(
            target,
            stage =>
            {
                GemLinkBridgeExportResult result = BuildGemLinkBridgePackage(
                    stage,
                    analysis,
                    operationDate,
                    artifactContext,
                    existingArtifactIds);
                var currentNames = result.Files.Select(Path.GetFileName)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (string obsoleteName in existingArtifactIds.Keys.Where(x => !currentNames.Contains(x)))
                {
                    string obsoletePath = Path.Combine(stage, obsoleteName);
                    if (File.Exists(obsoletePath)) File.Delete(obsoletePath);
                    string obsoleteIdentity = obsoletePath + GeneratedArtifactIdentity.Suffix;
                    if (File.Exists(obsoleteIdentity)) File.Delete(obsoleteIdentity);
                }
                return result;
            },
            (stage, result) =>
            {
                if (!File.Exists(result.ManifestPath))
                    throw new InvalidDataException("GEMLink 分段任务 manifest 未生成。");
                foreach (string file in result.Files)
                {
                    if (!File.Exists(file) || new FileInfo(file).Length == 0)
                        throw new InvalidDataException($"GEMLink 分段成果缺失或为空：{Path.GetFileName(file)}");
                    if (artifactContext is not null && GeneratedArtifactIdentity.Inspect(file).Status != "VERIFIED")
                        throw new InvalidDataException($"GEMLink 分段成果身份校验失败：{Path.GetFileName(file)}");
                }
            },
            preserveUnrelatedFiles: true);

        return new GemLinkBridgeExportResult(
            staged.Files.Select(file => Path.Combine(target, Path.GetFileName(file))).ToList(),
            staged.Jobs,
            Path.Combine(target, Path.GetFileName(staged.ManifestPath)));
    }

    private GemLinkBridgeExportResult BuildGemLinkBridgePackage(
        string outputDirectory,
        FieldMagFolderAnalysis analysis,
        DateTime operationDate,
        GeneratedArtifactContext? artifactContext,
        IReadOnlyDictionary<string, string> existingArtifactIds)
    {
        string directory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(directory);
        var files = new List<string>();
        var jobs = new List<GemLinkBridgeJob>();

        foreach (GemLinkBridgeJob job in analysis.GemLinkJobs.OrderBy(x => x.UtcDate))
        {
            string dateKey = job.UtcDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            List<UavMagCanonicalSample> rover = ResolveUavTimestampConflicts(analysis.UavSamples)
                .Where(IsUsableUavForDiurnal)
                .Where(x => x.TimestampUtc.UtcDateTime.Date == job.UtcDate.Date)
                .OrderBy(x => x.TimestampUtc)
                .ToList();
            List<Gsm19TCanonicalSample> baseRows = ResolveBaseTimestampConflicts(analysis.BaseSamples)
                .Where(x => x.TimestampUtc.UtcDateTime.Date == job.UtcDate.Date)
                .OrderBy(x => x.TimestampUtc)
                .ToList();

            string roverPath = Path.Combine(directory, $"Rover_{dateKey}_MAGOPS.csv");
            WriteUavCsv(roverPath, rover);
            files.Add(roverPath);

            string basePath = Path.Combine(directory, $"Base_{dateKey}_MAGOPS.csv");
            WriteBaseCsv(basePath, baseRows);
            files.Add(basePath);
            jobs.Add(job);
        }

        string jobList = Path.Combine(directory, "GEMLink分段任务清单.csv");
        using (var writer = new StreamWriter(jobList, false, new UTF8Encoding(true)))
        {
            writer.WriteLine("UTC日期,Rover记录数,Base记录数,Rover时间范围UTC,Base时间范围UTC,风险提示");
            foreach (GemLinkBridgeJob job in jobs)
            {
                writer.WriteLine(string.Join(",",
                    Csv(job.UtcDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                    job.RoverRecordCount.ToString(CultureInfo.InvariantCulture),
                    job.BaseRecordCount.ToString(CultureInfo.InvariantCulture),
                    Csv(FormatUtcRange(job.RoverFirstTimeUtc, job.RoverLastTimeUtc)),
                    Csv(FormatUtcRange(job.BaseFirstTimeUtc, job.BaseLastTimeUtc)),
                    Csv(string.Join("；", job.Warnings))));
            }
        }
        files.Add(jobList);

        string readme = Path.Combine(directory, "GEMLink分段任务说明.md");
        File.WriteAllText(readme,
            BuildGemLinkBridgeReadme(analysis, operationDate),
            new UTF8Encoding(true));
        files.Add(readme);

        string manifest = Path.Combine(directory, "GEMLink分段任务_manifest.json");
        File.WriteAllText(manifest,
            JsonSerializer.Serialize(new
            {
                schema_version = "magops-gemlink-bridge-1",
                operation_date = operationDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                source_directory = analysis.SourceDirectory,
                source_fingerprint = analysis.Fingerprint,
                note = "MagOps 原生日变已成为正式主链；本包仅按绝对 UTC 日期准备 GEMLink 5.4.8.1 黄金对照/兼容核查数据。Rover/Base *_MAGOPS.csv 不冒充厂家专有输入格式。",
                jobs = jobs.Select(x => new
                {
                    utc_date = x.UtcDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    rover_records = x.RoverRecordCount,
                    base_records = x.BaseRecordCount,
                    warnings = x.Warnings
                }).ToArray(),
                outputs = files.Select(Path.GetFileName).ToArray()
            }, new JsonSerializerOptions
            {
                WriteIndented = true,
                // This is a local UTF-8 JSON manifest, not HTML/JavaScript.
                // Allow Unicode so human-readable compatibility boundaries remain literal
                // (for example: "不冒充已验证可被 GEMLink 直接导入") instead of \uXXXX escapes.
                Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
            }),
            new UTF8Encoding(true));
        files.Add(manifest);

        LabelFieldDataOutputs(
            files,
            analysis,
            "field_gemlink_bridge",
            artifactContext,
            existingArtifactIds);

        return new GemLinkBridgeExportResult(files, jobs, manifest);
    }

    private static void LabelFieldDataOutputs(
        IReadOnlyList<string> files,
        FieldMagFolderAnalysis analysis,
        string stage,
        GeneratedArtifactContext? artifactContext,
        IReadOnlyDictionary<string, string>? existingArtifactIds = null)
    {
        if (artifactContext is null) return;
        string[] sources = analysis.Files
            .Select(x => x.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (string path in files)
        {
            if (!File.Exists(path)) continue;
            GeneratedArtifactIdentity.Write(
                path,
                "field_data",
                stage,
                analysis.Fingerprint,
                sources,
                artifactId: existingArtifactIds is not null &&
                    existingArtifactIds.TryGetValue(Path.GetFileName(path), out string? existingId)
                    ? existingId
                    : null,
                context: artifactContext);
        }
    }

    private static IReadOnlyDictionary<string, string> ReadVerifiedArtifactIds(
        string directory,
        string? expectedStage = null)
    {
        if (!Directory.Exists(directory))
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                     .Where(x => !x.EndsWith(GeneratedArtifactIdentity.Suffix, StringComparison.OrdinalIgnoreCase)))
        {
            ArtifactIdentityInspection inspection = GeneratedArtifactIdentity.Inspect(path);
            if (inspection.Status == "VERIFIED" &&
                (string.IsNullOrWhiteSpace(expectedStage) ||
                 inspection.Metadata?.ProcessingStage.Equals(expectedStage, StringComparison.OrdinalIgnoreCase) == true) &&
                !string.IsNullOrWhiteSpace(inspection.Metadata?.ArtifactId))
                result[Path.GetFileName(path)] = inspection.Metadata.ArtifactId;
        }
        return result;
    }

    private static FieldMagFolderAnalysis BuildFolderAnalysis(
        string root,
        IReadOnlyList<ParsedMagFile> parsed)
    {
        IReadOnlyList<UavMagCanonicalSample> uav =
            BuildCanonicalUav(
                parsed,
                out int uavDuplicates,
                out bool uavUsedAggregateFallback);
        IReadOnlyList<Gsm19TCanonicalSample> baseSamples =
            BuildCanonicalBase(
                parsed,
                out int baseDuplicates,
                out bool baseUsedAggregateFallback);
        IReadOnlyList<FieldDataQualityIssue> issues =
            BuildQualityIssues(
                parsed,
                uav,
                baseSamples,
                uavDuplicates,
                baseDuplicates,
                uavUsedAggregateFallback,
                baseUsedAggregateFallback);
        IReadOnlyList<GemLinkBridgeJob> jobs =
            BuildGemLinkJobs(
                uav,
                baseSamples);

        var scans = parsed.Select(file =>
            new FieldMagFileScan(
                file.Path,
                file.SensorFamily,
                file.DataRole,
                file.RecordCount,
                file.FirstTimeUtc,
                file.LastTimeUtc,
                file.Evidence,
                file.Warnings)
            {
                SourceFileStableId = file.SourceFileStableId,
                SourceContentSha256 = file.SourceContentSha256
            })
            .ToList();
        IReadOnlyList<GsmSession> gsmSessions = BuildGsmSessionQc(
            parsed.SelectMany(file => file.Sessions).ToArray(),
            baseSamples);

        return new FieldMagFolderAnalysis
        {
            SourceDirectory = root,
            Fingerprint =
                ComputeFingerprint(
                    scans,
                    uav,
                    baseSamples),
            Files = scans,
            UavSamples = uav,
            BaseSamples = baseSamples,
            GsmSessions = gsmSessions,
            QualityIssues = issues,
            GemLinkJobs = jobs
        };
    }

    private static ParsedMagFile ParseFailure(
        string path,
        string warning) =>
        new(
            path,
            FieldMagSensorFamily.Unknown,
            FieldMagDataRole.Unknown,
            [],
            [],
            null,
            null,
            "读取失败或文件不稳定",
            [warning]);

    private static SourceFileMetadata SourceMetadata(
        string root,
        string path)
    {
        var info = new FileInfo(path);
        return new SourceFileMetadata(
            Path.GetRelativePath(root, path)
                .Replace('\\', '/'),
            info.Length,
            info.LastWriteTimeUtc.Ticks);
    }

    private static string ComputeSourceSnapshotKey(
        string root,
        IEnumerable<SourceFileMetadata> files)
    {
        var sb = new StringBuilder(
            Path.GetFullPath(root))
            .AppendLine();
        foreach (SourceFileMetadata file in files
            .OrderBy(
                x => x.RelativePath,
                StringComparer.OrdinalIgnoreCase))
        {
            sb.Append(file.RelativePath)
                .Append('|')
                .Append(file.Length)
                .Append('|')
                .Append(file.LastWriteTimeUtcTicks)
                .AppendLine();
        }

        return Convert.ToHexString(
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(
                        sb.ToString())))
            .ToLowerInvariant();
    }

    private static FieldMagCacheIndexDocument? TryReadCacheIndex(
        string indexPath,
        out bool invalidated)
    {
        invalidated = false;
        if (!File.Exists(indexPath))
            return null;

        try
        {
            FieldMagCacheIndexDocument? document =
                JsonSerializer.Deserialize<FieldMagCacheIndexDocument>(
                    File.ReadAllText(
                        indexPath,
                        Encoding.UTF8),
                    CacheJsonOptions);
            if (document is null ||
                !document.SchemaVersion.Equals(
                    IncrementalCacheSchemaVersion,
                    StringComparison.Ordinal) ||
                !document.ParserVersion.Equals(
                    IncrementalCacheParserVersion,
                    StringComparison.Ordinal))
            {
                invalidated = true;
                return null;
            }

            return document;
        }
        catch
        {
            invalidated = true;
            return null;
        }
    }

    private static ParsedMagFile ReadParsedCache(
        string cacheRoot,
        string sourceRoot,
        FieldMagCacheIndexEntry entry)
    {
        string cachePath =
            ResolveCachePath(
                cacheRoot,
                entry.CacheFile);
        if (!File.Exists(cachePath))
            throw new FileNotFoundException(
                "外业解析缓存文件不存在。",
                cachePath);

        CachedParsedMagFile? cached;
        using (FileStream stream =
            File.OpenRead(cachePath))
        using (var gzip =
            new GZipStream(
                stream,
                CompressionMode.Decompress))
        {
            cached =
                JsonSerializer.Deserialize<CachedParsedMagFile>(
                    gzip,
                    CacheJsonOptions);
        }

        if (cached is null ||
            !cached.SchemaVersion.Equals(
                IncrementalCacheSchemaVersion,
                StringComparison.Ordinal) ||
            !cached.ParserVersion.Equals(
                IncrementalCacheParserVersion,
                StringComparison.Ordinal) ||
            !cached.RelativePath.Equals(
                entry.RelativePath,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "外业解析缓存版本或文件身份不匹配。");
        }

        string fullPath =
            CombineSourceRelativePath(
                sourceRoot,
                entry.RelativePath);

        IReadOnlyList<UavMagCanonicalSample> uav =
            cached.UavRows
                .Select(row =>
                    row with
                    {
                        SourceFile = fullPath
                    })
                .ToList();
        IReadOnlyList<Gsm19TCanonicalSample> baseRows =
            cached.BaseRows
                .Select(row =>
                    row with
                    {
                        SourceFile = fullPath
                    })
                .ToList();

        return new ParsedMagFile(
            fullPath,
            cached.SensorFamily,
            cached.DataRole,
            uav,
            baseRows,
            cached.FirstTimeUtc,
            cached.LastTimeUtc,
            cached.Evidence,
            cached.Warnings,
            cached.ExplicitRecordCount,
            cached.SourceFileStableId,
            cached.GsmSessions.Select(session => session with { SourceFile = fullPath }).ToArray(),
            cached.SourceContentSha256);
    }

    private static void WriteParsedCache(
        string cacheRoot,
        ParsedMagFile file,
        string relativePath,
        string cacheFile)
    {
        string path =
            ResolveCachePath(
                cacheRoot,
                cacheFile);
        Directory.CreateDirectory(
            Path.GetDirectoryName(path)!);

        var cached =
            new CachedParsedMagFile(
                IncrementalCacheSchemaVersion,
                IncrementalCacheParserVersion,
                relativePath,
                file.SensorFamily,
                file.DataRole,
                file.UavRows
                    .Select(row =>
                        row with
                        {
                            SourceFile = relativePath
                        })
                    .ToList(),
                file.BaseRows
                    .Select(row =>
                        row with
                        {
                            SourceFile = relativePath
                        })
                    .ToList(),
                file.FirstTimeUtc,
                file.LastTimeUtc,
                file.Evidence,
                file.Warnings,
                file.ExplicitRecordCount,
                file.SourceFileStableId,
                file.Sessions.Select(session => session with { SourceFile = relativePath }).ToArray(),
                file.SourceContentSha256);

        string temp =
            path +
            ".tmp." +
            Guid.NewGuid().ToString("N");
        try
        {
            using (FileStream stream =
                new(
                    temp,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None))
            using (var gzip =
                new GZipStream(
                    stream,
                    CompressionLevel.SmallestSize))
            {
                JsonSerializer.Serialize(
                    gzip,
                    cached,
                    CacheJsonOptions);
            }

            File.Move(
                temp,
                path,
                overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                try
                {
                    File.Delete(temp);
                }
                catch
                {
                }
            }
        }
    }

    private static IReadOnlyList<string> AffectedBeijingDates(
        ParsedMagFile file) =>
        file.UavRows
            .Select(row =>
                BeijingDate(
                    row.TimestampUtc))
            .Concat(
                file.BaseRows.Select(row =>
                    BeijingDate(
                        row.TimestampUtc)))
            .Distinct()
            .OrderBy(date => date)
            .Select(date =>
                date.ToString(
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture))
            .ToList();

    private static string CacheFileRelativePath(
        string relativePath)
    {
        string key = Convert.ToHexString(
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(
                        relativePath.ToUpperInvariant())))
            .ToLowerInvariant();
        return $"files/{key}.json.gz";
    }

    private static string ResolveCachePath(
        string cacheRoot,
        string relativePath)
    {
        string root =
            Path.GetFullPath(cacheRoot)
                .TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
        string combined =
            Path.GetFullPath(
                Path.Combine(
                    root,
                    relativePath.Replace(
                        '/',
                        Path.DirectorySeparatorChar)));
        string prefix =
            root +
            Path.DirectorySeparatorChar;
        if (!combined.StartsWith(
                prefix,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"缓存相对路径越界：{relativePath}");
        }

        return combined;
    }

    private static string CombineSourceRelativePath(
        string sourceRoot,
        string relativePath)
    {
        string root =
            Path.GetFullPath(sourceRoot)
                .TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
        string combined =
            Path.GetFullPath(
                Path.Combine(
                    root,
                    relativePath.Replace(
                        '/',
                        Path.DirectorySeparatorChar)));
        string prefix =
            root +
            Path.DirectorySeparatorChar;
        if (!combined.StartsWith(
                prefix,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"源数据相对路径越界：{relativePath}");
        }

        return combined;
    }

    private static void WriteJsonAtomic<T>(
        string path,
        T value)
    {
        Directory.CreateDirectory(
            Path.GetDirectoryName(path)!);
        string temp =
            path +
            ".tmp." +
            Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(
                temp,
                JsonSerializer.Serialize(
                    value,
                    CacheJsonOptions),
                new UTF8Encoding(false));
            File.Move(
                temp,
                path,
                overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                try
                {
                    File.Delete(temp);
                }
                catch
                {
                }
            }
        }
    }

    private static void WriteAnalysisSnapshot(
        string cacheRoot,
        string sourceRoot,
        string sourceSnapshotKey,
        FieldMagFolderAnalysis analysis)
    {
        string path = Path.Combine(
            cacheRoot,
            IncrementalAnalysisSnapshotFileName);
        Directory.CreateDirectory(
            Path.GetDirectoryName(path)!);

        var document =
            new FieldMagAnalysisSnapshotDocument(
                IncrementalCacheSchemaVersion,
                IncrementalCacheParserVersion,
                DateTimeOffset.UtcNow,
                sourceSnapshotKey,
                Path.GetFullPath(sourceRoot),
                analysis);

        string temp =
            path +
            ".tmp." +
            Guid.NewGuid().ToString("N");
        try
        {
            using (FileStream stream =
                new(
                    temp,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None))
            using (var gzip =
                new GZipStream(
                    stream,
                    CompressionLevel.SmallestSize))
            {
                JsonSerializer.Serialize(
                    gzip,
                    document,
                    CacheJsonOptions);
            }

            File.Move(
                temp,
                path,
                overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                try
                {
                    File.Delete(temp);
                }
                catch
                {
                }
            }
        }
    }

    private static FieldMagFolderAnalysis RebaseCachedAnalysis(
        FieldMagFolderAnalysis analysis,
        string snapshotSourceRoot,
        string currentSourceRoot)
    {
        string previousRoot =
            Path.GetFullPath(snapshotSourceRoot);
        string currentRoot =
            Path.GetFullPath(currentSourceRoot);

        if (previousRoot.Equals(
            currentRoot,
            StringComparison.OrdinalIgnoreCase))
        {
            return analysis;
        }

        string Rebase(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return path;

            try
            {
                string full = Path.GetFullPath(path);
                string prefix =
                    previousRoot.TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar) +
                    Path.DirectorySeparatorChar;
                if (!full.StartsWith(
                        prefix,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return path;
                }

                string relative =
                    Path.GetRelativePath(
                        previousRoot,
                        full);
                return Path.GetFullPath(
                    Path.Combine(
                        currentRoot,
                        relative));
            }
            catch
            {
                return path;
            }
        }

        return new FieldMagFolderAnalysis
        {
            SourceDirectory = currentRoot,
            Fingerprint = analysis.Fingerprint,
            Files = analysis.Files
                .Select(row =>
                    row with
                    {
                        Path = Rebase(row.Path)
                    })
                .ToList(),
            UavSamples = analysis.UavSamples
                .Select(row =>
                    row with
                    {
                        SourceFile = Rebase(row.SourceFile)
                    })
                .ToList(),
            BaseSamples = analysis.BaseSamples
                .Select(row =>
                    row with
                    {
                        SourceFile = Rebase(row.SourceFile)
                    })
                .ToList(),
            GsmSessions = analysis.GsmSessions
                .Select(session => session with { SourceFile = Rebase(session.SourceFile) })
                .ToList(),
            QualityIssues = analysis.QualityIssues,
            GemLinkJobs = analysis.GemLinkJobs
        };
    }

    private static void PruneUnreferencedCacheFiles(
        string cacheRoot,
        IReadOnlyList<FieldMagCacheIndexEntry> entries)
    {
        string directory =
            Path.Combine(
                cacheRoot,
                "files");
        if (!Directory.Exists(directory))
            return;

        HashSet<string> keep =
            entries
                .Select(entry =>
                    ResolveCachePath(
                        cacheRoot,
                        entry.CacheFile))
                .ToHashSet(
                    StringComparer.OrdinalIgnoreCase);

        foreach (string path in Directory.EnumerateFiles(
            directory,
            "*.json.gz",
            SearchOption.TopDirectoryOnly))
        {
            string full = Path.GetFullPath(path);
            if (!keep.Contains(full))
                File.Delete(full);
        }
    }

    public static IReadOnlyList<string> SourcePaths(string root) => DiscoverFiles(root, strict: true);

    private static List<string> DiscoverFiles(string root, bool strict = false)
    {
        var result = new List<string>();
        try
        {
            foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                string fileName = Path.GetFileName(file);
                if (fileName.StartsWith(
                        "DJIFlightRecord_",
                        StringComparison.OrdinalIgnoreCase))
                {
                    // DJI Pilot 2 binary .txt belongs to the dedicated flight-session
                    // ledger. Do not feed it into the magnetic text parser.
                    continue;
                }

                string ext = Path.GetExtension(file).ToLowerInvariant();
                if (SupportedExtensions.Contains(ext))
                    result.Add(Path.GetFullPath(file));
            }
        }
        catch (UnauthorizedAccessException) when (!strict)
        {
            // The caller still gets all accessible files and an empty/partial analysis.
        }

        return result
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => NaturalSortKey(Path.GetFileName(x)), StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static ParsedMagFile Parse(string path)
    {
        byte[] content = File.ReadAllBytes(path);
        string sha256 = Convert.ToHexString(SHA256.HashData(content));
        using var stream = new MemoryStream(content, writable: false);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        string text = reader.ReadToEnd();
        (FieldMagSensorFamily sensor, FieldMagDataRole role, string evidence) = Classify(path, text);

        ParsedMagFile parsed = sensor switch
        {
            FieldMagSensorFamily.UavOpticalPump => ParseUav(path, text, role, evidence),
            FieldMagSensorFamily.Gsm19T => ParseBase(path, text, role, evidence),
            FieldMagSensorFamily.DiurnalCorrection => ParseDerived(path, text, role, evidence),
            FieldMagSensorFamily.Gsm19TG => new ParsedMagFile(path, sensor, role, [], [], null, null,
                evidence, ["已识别 GSM-19TG 移动梯度数据；当前读取器尚不支持此列格式，未解析观测，不作为日变基站输入。"]),
            _ => new ParsedMagFile(path, sensor, role, [], [], null, null, evidence, [])
        };
        return parsed with { SourceContentSha256 = sha256 };
    }

    private static (FieldMagSensorFamily Sensor, FieldMagDataRole Role, string Evidence) Classify(string path, string text)
    {
        string name = Path.GetFileName(path).ToLowerInvariant();
        string probe = text.Length > 65536 ? text[..65536] : text;
        string lower = probe.ToLowerInvariant();

        // Match the complete instrument model before filename-derived roles.
        // GSM-19TG is a rover gradient instrument, not a GSM-19T base export.
        if (Regex.IsMatch(probe, @"\bGSM-19TG\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return (FieldMagSensorFamily.Gsm19TG, FieldMagDataRole.Raw, "检测到 GSM-19TG 移动梯度仪文件头；不归为 GSM-19T 日变基站");

        bool looksDiurnal = name.Contains("rigai", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("diurnal", StringComparison.OrdinalIgnoreCase) ||
                            lower.Contains("i---", StringComparison.OrdinalIgnoreCase) ||
                            DiurnalMarkerRegex.IsMatch(lower);
        if (looksDiurnal)
        {
            FieldMagDataRole role = LooksAggregateName(name)
                ? FieldMagDataRole.DiurnalMerged
                : FieldMagDataRole.DiurnalSegment;
            return (FieldMagSensorFamily.DiurnalCorrection, role, "检测到 rigai/diurnal 或 GEMLink 插值标记");
        }

        bool hasGsmColumnHeader =
            probe.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Take(120)
                .Any(line => TryParseGsm19TColumnMap(line, out _));
        bool looksBase = Regex.IsMatch(probe, @"\bgem systems gsm-19t\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
                         hasGsmColumnHeader ||
                         Path.GetExtension(path).Equals(".stn", StringComparison.OrdinalIgnoreCase);
        if (looksBase)
        {
            FieldMagDataRole role = LooksAggregateName(name) ? FieldMagDataRole.Aggregate : FieldMagDataRole.Raw;
            return (FieldMagSensorFamily.Gsm19T, role, "检测到 GEM Systems GSM-19T 文件头或可解析 /time ... nT ... 列定义");
        }

        bool looksUav = lower.Contains("quspin", StringComparison.OrdinalIgnoreCase) ||
                        lower.Contains("total-field magnetometer", StringComparison.OrdinalIgnoreCase) ||
                        lower.Contains("total field magnetometer", StringComparison.OrdinalIgnoreCase) ||
                        (ContainsCoordinateHeaders(lower) && (lower.Contains("mag", StringComparison.OrdinalIgnoreCase) || lower.Contains("磁", StringComparison.Ordinal)));
        if (looksUav)
        {
            FieldMagDataRole role = LooksAggregateName(name) ? FieldMagDataRole.Aggregate : FieldMagDataRole.Raw;
            return (FieldMagSensorFamily.UavOpticalPump, role, "检测到 QuSpin/光泵总场磁力仪特征或经纬度+Mag 字段");
        }

        return (FieldMagSensorFamily.Unknown, FieldMagDataRole.Unknown, "未识别为 UAV 光泵、GSM-19T 或日改派生文件");
    }

    private static bool LooksAggregateName(string name)
    {
        string normalized = NormalizeToken(Path.GetFileNameWithoutExtension(name));
        string[] hints =
        [
            "afterall", "affter", "merged", "merge", "aggregate", "combined", "拼接", "整合", "合并"
        ];
        if (hints.Any(hint => normalized.Contains(NormalizeToken(hint), StringComparison.OrdinalIgnoreCase)))
            return true;

        // Historical field outputs use names such as after_allT / rigaiAll.
        // Do not treat every filename containing the letters "all" (for example "small") as an aggregate.
        return normalized.EndsWith("all", StringComparison.OrdinalIgnoreCase) ||
               normalized.EndsWith("allt", StringComparison.OrdinalIgnoreCase) ||
               normalized.EndsWith("all19t", StringComparison.OrdinalIgnoreCase) ||
               normalized.EndsWith("alluav", StringComparison.OrdinalIgnoreCase) ||
               normalized.EndsWith("allbase", StringComparison.OrdinalIgnoreCase) ||
               normalized.EndsWith("allmag", StringComparison.OrdinalIgnoreCase);
    }

    private static ParsedMagFile ParseUav(
        string path,
        string text,
        FieldMagDataRole role,
        string evidence)
    {
        var warnings = new List<string>();
        string? headerLine = FindUavHeaderLine(text);
        HeaderMap map = HeaderMap.From(headerLine);
        MatchCollection matches = UavTimestampRegex.Matches(text);
        var rows = new List<UavMagCanonicalSample>(matches.Count);

        for (int i = 0; i < matches.Count; i++)
        {
            Match match = matches[i];
            int end = i + 1 < matches.Count ? matches[i + 1].Index : text.Length;
            string segment = text[match.Index..end].Trim();
            if (!TryParseUtcTimestamp(match.Groups["date"].Value, match.Groups["time"].Value, out DateTimeOffset stamp))
                continue;

            string[] fields = SplitUavSegment(segment, map.Delimiter);
            int dateOffset = map.DateIndex >= 0 ? map.DateIndex : 0;
            double? lon = FieldNumber(fields, map.LongitudeIndex, dateOffset);
            double? lat = FieldNumber(fields, map.LatitudeIndex, dateOffset);
            if (!IsLonLat(lon, lat))
                (lon, lat) = InferLonLat(fields);

            double? mag = FieldNumber(fields, map.MagIndex, dateOffset);
            if (!mag.HasValue)
                mag = InferMagneticField(fields);

            double? altitude = FieldNumber(fields, map.AltitudeIndex, dateOffset);
            double? laser = FieldNumber(fields, map.LaserAltitudeIndex, dateOffset);
            double? signal = FieldNumber(fields, map.SignalIndex, dateOffset);
            string gps = FieldText(fields, map.GpsStatusIndex, dateOffset);
            int? satellites = FieldInteger(fields, map.SatellitesIndex, dateOffset);
            string sensorStatus = FieldText(fields, map.SensorStatusIndex, dateOffset);
            if (string.IsNullOrWhiteSpace(sensorStatus))
                sensorStatus = InferSensorStatus(fields);

            rows.Add(new UavMagCanonicalSample(
                stamp,
                lon,
                lat,
                altitude,
                laser,
                mag,
                signal,
                gps,
                satellites,
                sensorStatus,
                path,
                i + 1,
                []));
        }

        if (matches.Count == 0)
            warnings.Add("未识别到“UTC日期 + UTC时间”记录起点；该文件暂不能进入规范 UAV 数据集");
        if (rows.Count > 0 && rows.Count != CountPhysicalDataLines(text))
            warnings.Add("采用 record-level 时间戳提取；物理文本行数与恢复记录数不同，已按记录而非按行解析");
        if (rows.Count > 0 && rows.All(x => !x.Longitude.HasValue || !x.Latitude.HasValue))
            warnings.Add("已恢复时间记录，但未识别到有效经纬度列");

        DateTimeOffset? first = rows.Count == 0 ? null : rows.Min(x => x.TimestampUtc);
        DateTimeOffset? last = rows.Count == 0 ? null : rows.Max(x => x.TimestampUtc);
        return new ParsedMagFile(path, FieldMagSensorFamily.UavOpticalPump, role, rows, [], first, last, evidence, warnings);
    }

    private static ParsedMagFile ParseBase(
        string path,
        string text,
        FieldMagDataRole role,
        string evidence)
    {
        var warnings = new List<string>();
        string sourceFileStableId = ComputeGsmSourceFileStableId(text);
        DateTime[] surveyDates = GsmSurveyFileDateRegex.Matches(text)
            .Select(match => TryGsmSurveyDateParts(match, out DateTime date) ? (DateTime?)date.Date : null)
            .Where(date => date.HasValue).Select(date => date!.Value).Distinct().ToArray();
        if (surveyDates.Length > 1)
        {
            warnings.Add("GSM-19T 文件内存在多个采集日期：" +
                string.Join(", ", surveyDates.Select(date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))) +
                "；未将首个日期推广到全部记录。请按采集日期拆分并核对时区后重新读取，原文件保持不变。");
            return new ParsedMagFile(path, FieldMagSensorFamily.Gsm19T, role, [], [], null, null,
                evidence, warnings, SourceFileStableId: sourceFileStableId);
        }
        if (surveyDates.Length == 1)
        {
            Match filenameDate = IsoDateRegex.Match(Path.GetFileName(path));
            if (filenameDate.Success && TryDateParts(filenameDate, out DateTime namedDate) && namedDate.Date != surveyDates[0])
                warnings.Add("GSM-19T 采集头日期与文件名日期不一致；当前仅按采集头解析，实际日期及仪器时区仍需核对。");
        }
        if (!TryFindHeaderDate(text, path, out DateTime headerDate))
        {
            warnings.Add("未从 /ID 或文件名识别 GSM-19T 日期，无法建立绝对 UTC 时间轴");
            return new ParsedMagFile(path, FieldMagSensorFamily.Gsm19T, role, [], [], null, null,
                evidence, warnings, SourceFileStableId: sourceFileStableId);
        }

        var rows = new List<Gsm19TCanonicalSample>();
        var boundarySeeds = new List<(int SourceRecord, GsmSessionBoundaryKind Kind, string Detail)>();
        DateTime currentDate = headerDate.Date;
        TimeSpan? previous = null;
        DateTimeOffset? previousTimestamp = null;
        int record = 0;
        int malformedDataLines = 0;
        Gsm19TColumnMap? columnMap = null;
        bool sawExplicitColumnHeader = false;
        bool pendingExplicitHeaderBoundary = false;
        string columnEvidence = "fallback:time=0,mag=1,sq=2";

        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } rawLine)
        {
            string line = rawLine.Trim();
            if (line.Length == 0)
                continue;

            if (TryParseGsm19TColumnMap(line, out Gsm19TColumnMap parsedMap))
            {
                string nextEvidence = $"header:time={parsedMap.TimeIndex},mag={parsedMap.MagneticIndex},sq={parsedMap.SqIndex?.ToString(CultureInfo.InvariantCulture) ?? "none"}";
                // A repeated/changed explicit data header after records is an auditable block boundary.
                if (record > 0 && !string.Equals(columnEvidence, nextEvidence, StringComparison.Ordinal))
                    pendingExplicitHeaderBoundary = true;
                else if (record > 0 && sawExplicitColumnHeader)
                    pendingExplicitHeaderBoundary = true;
                columnMap = parsedMap;
                sawExplicitColumnHeader = true;
                columnEvidence = nextEvidence;
                continue;
            }

            if (line.StartsWith("/", StringComparison.Ordinal))
                continue;

            string[] fields = SplitGsmFields(line);
            Gsm19TColumnMap activeMap = columnMap ?? Gsm19TColumnMap.Default;
            if (fields.Length <= activeMap.TimeIndex ||
                !LooksLikeGsmCompactTime(fields[activeMap.TimeIndex]))
                continue;

            if (fields.Length <= activeMap.MagneticIndex ||
                !TryParseCompactTime(fields[activeMap.TimeIndex], out TimeSpan time,
                    out bool explicitNextDayMidnight) ||
                !double.TryParse(fields[activeMap.MagneticIndex], NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double mag))
            {
                malformedDataLines++;
                continue;
            }

            double? sq = null;
            if (activeMap.SqIndex.HasValue)
            {
                int sqIndex = activeMap.SqIndex.Value;
                if (fields.Length <= sqIndex ||
                    !double.TryParse(fields[sqIndex], NumberStyles.Float,
                        CultureInfo.InvariantCulture, out double parsedSq))
                {
                    malformedDataLines++;
                    continue;
                }
                sq = parsedSq;
            }
            else if (!sawExplicitColumnHeader && fields.Length > 2 &&
                     double.TryParse(fields[2], NumberStyles.Float,
                         CultureInfo.InvariantCulture, out double fallbackSq))
            {
                sq = fallbackSq;
            }

            bool dateRollover = false;
            if (explicitNextDayMidnight)
            {
                currentDate = currentDate.AddDays(1);
                dateRollover = true;
            }
            else if (previous.HasValue && time + TimeSpan.FromHours(12) < previous.Value)
            {
                currentDate = currentDate.AddDays(1);
                dateRollover = true;
            }
            previous = time;

            var timestamp = new DateTimeOffset(
                currentDate.Year, currentDate.Month, currentDate.Day,
                time.Hours, time.Minutes, time.Seconds, TimeSpan.Zero)
                .AddTicks(time.Ticks % TimeSpan.TicksPerSecond);

            int nextRecord = record + 1;
            if (nextRecord == 1)
                boundarySeeds.Add((nextRecord, GsmSessionBoundaryKind.FileStart, "source file start"));
            else if (pendingExplicitHeaderBoundary)
                boundarySeeds.Add((nextRecord, GsmSessionBoundaryKind.ExplicitHeader, columnEvidence));
            else if (dateRollover)
                boundarySeeds.Add((nextRecord, GsmSessionBoundaryKind.UtcDateRollover, "UTC date rollover"));
            else if (previousTimestamp.HasValue &&
                     (timestamp - previousTimestamp.Value).TotalSeconds > 999d)
                boundarySeeds.Add((nextRecord, GsmSessionBoundaryKind.LongGap,
                    $">{999.ToString(CultureInfo.InvariantCulture)}s timeline gap"));

            pendingExplicitHeaderBoundary = false;
            previousTimestamp = timestamp;
            record = nextRecord;
            rows.Add(new Gsm19TCanonicalSample(timestamp, mag, sq, path, record, [])
            {
                SourceFileStableId = sourceFileStableId
            });
        }

        if (malformedDataLines > 0)
            warnings.Add($"检测到 {malformedDataLines} 条疑似 GSM-19T 数据行无法按文件列头完整解析；原文件已保留，MagOps 未自动猜测或修复这些记录");
        if (rows.Count == 0)
            warnings.Add("未识别到 GSM-19T 的 HHMMSS.s / nT / SQ 数据记录");

        IReadOnlyList<GsmSession> sessions =
            BuildGsmSessions(sourceFileStableId, path, rows, boundarySeeds);
        var sessionByRecord = sessions
            .SelectMany(session => Enumerable.Range(
                session.FirstSourceRecord,
                session.LastSourceRecord - session.FirstSourceRecord + 1)
                .Select(sourceRecord => (sourceRecord, session)))
            .ToDictionary(x => x.sourceRecord, x => x.session);
        rows = rows.Select(row =>
        {
            GsmSession session = sessionByRecord[row.SourceRecord];
            return row with {
                SessionStableId = session.SessionStableId,
                SessionOrdinal = session.Ordinal
            };
        }).ToList();

        DateTimeOffset? first = rows.Count == 0 ? null : rows.Min(x => x.TimestampUtc);
        DateTimeOffset? last = rows.Count == 0 ? null : rows.Max(x => x.TimestampUtc);
        return new ParsedMagFile(path, FieldMagSensorFamily.Gsm19T, role, [], rows, first, last,
            evidence, warnings, SourceFileStableId: sourceFileStableId, GsmSessions: sessions);
    }

    private static string ComputeGsmSourceFileStableId(string text)
    {
        // Identity is content-derived, so moving/renaming the immutable raw file does not change it.
        byte[] bytes = Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal));
        string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return "gsm-file-" + hash[..32];
    }

    private static IReadOnlyList<GsmSession> BuildGsmSessions(
        string sourceFileStableId,
        string path,
        IReadOnlyList<Gsm19TCanonicalSample> rows,
        IReadOnlyList<(int SourceRecord, GsmSessionBoundaryKind Kind, string Detail)> seeds)
    {
        if (rows.Count == 0) return [];
        var starts = seeds.GroupBy(x => x.SourceRecord).OrderBy(x => x.Key).ToArray();
        var result = new List<GsmSession>();
        for (int i = 0; i < starts.Length; i++)
        {
            int firstRecord = starts[i].Key;
            int lastRecord = i + 1 < starts.Length ? starts[i + 1].Key - 1 : rows[^1].SourceRecord;
            Gsm19TCanonicalSample[] members = rows
                .Where(x => x.SourceRecord >= firstRecord && x.SourceRecord <= lastRecord)
                .OrderBy(x => x.SourceRecord).ToArray();
            if (members.Length == 0) continue;
            string stableId = ComputeGsmSessionStableId(
                sourceFileStableId, firstRecord, members[0].TimestampUtc);
            var evidence = starts[i].Select(seed => new GsmSessionBoundaryEvidence(
                seed.Kind, seed.SourceRecord, members[0].TimestampUtc, seed.Detail)).ToArray();
            string sessionColumnEvidence = starts[i]
                .Where(seed => seed.Kind == GsmSessionBoundaryKind.ExplicitHeader)
                .Select(seed => seed.Detail)
                .FirstOrDefault() ?? "inherited/fallback column mapping";
            result.Add(new GsmSession(
                stableId, sourceFileStableId, path, result.Count + 1,
                members[0].TimestampUtc, members[^1].TimestampUtc,
                firstRecord, lastRecord, members.Length,
                sessionColumnEvidence, evidence, "PENDING", []));
        }
        return result;
    }

    private static string ComputeGsmSessionStableId(
        string sourceFileStableId, int firstSourceRecord, DateTimeOffset startTimeUtc)
    {
        string evidence = string.Join("|",
            sourceFileStableId,
            firstSourceRecord.ToString(CultureInfo.InvariantCulture),
            startTimeUtc.UtcDateTime.Ticks.ToString(CultureInfo.InvariantCulture));
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(evidence)))
            .ToLowerInvariant();
        return "gsm-session-" + hash[..32];
    }

    private sealed record Gsm19TColumnMap(
        int TimeIndex,
        int MagneticIndex,
        int? SqIndex)
    {
        public static Gsm19TColumnMap Default { get; } =
            new(0, 1, 2);
    }

    private static bool TryParseGsm19TColumnMap(
        string line,
        out Gsm19TColumnMap map)
    {
        map = Gsm19TColumnMap.Default;
        string trimmed = line.Trim();
        if (!trimmed.StartsWith("/", StringComparison.Ordinal))
            return false;

        string[] tokens = SplitGsmFields(trimmed.TrimStart('/'));
        int timeIndex = Array.FindIndex(
            tokens,
            token => token.Equals("time", StringComparison.OrdinalIgnoreCase));
        int magneticIndex = Array.FindIndex(
            tokens,
            token => token.Equals("nT", StringComparison.OrdinalIgnoreCase));
        int sqIndex = Array.FindIndex(
            tokens,
            token => token.Equals("sq", StringComparison.OrdinalIgnoreCase));

        if (timeIndex < 0 || magneticIndex < 0)
            return false;

        map = new Gsm19TColumnMap(
            timeIndex,
            magneticIndex,
            sqIndex >= 0 ? sqIndex : null);
        return true;
    }

    private static string[] SplitGsmFields(string line) =>
        Regex.Split(
                line.Trim(),
                @"[\t ,;]+",
                RegexOptions.CultureInvariant)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToArray();

    private static bool LooksLikeGsmCompactTime(string token) =>
        Regex.IsMatch(
            token,
            @"^\d{6}(?:\.\d+)?$",
            RegexOptions.CultureInvariant);

    private static ParsedMagFile ParseDerived(
        string path,
        string text,
        FieldMagDataRole role,
        string evidence)
    {
        MatchCollection timestampMatches = UavTimestampRegex.Matches(text);
        int interpolationMarks = DiurnalMarkerRegex.Matches(text).Count;
        int recordCount = Math.Max(timestampMatches.Count, interpolationMarks);
        DateTimeOffset? first = null;
        DateTimeOffset? last = null;
        foreach (Match match in timestampMatches)
        {
            if (!TryParseUtcTimestamp(match.Groups["date"].Value, match.Groups["time"].Value, out DateTimeOffset stamp))
                continue;
            if (!first.HasValue || stamp < first.Value)
                first = stamp;
            if (!last.HasValue || stamp > last.Value)
                last = stamp;
        }
        return new ParsedMagFile(path, FieldMagSensorFamily.DiurnalCorrection, role, [], [], first, last, evidence, [], recordCount);
    }

    private static IReadOnlyList<UavMagCanonicalSample> BuildCanonicalUav(
        IReadOnlyList<ParsedMagFile> parsed,
        out int duplicateCount,
        out bool usedAggregateFallback)
    {
        List<ParsedMagFile> raw = parsed
            .Where(x => x.SensorFamily == FieldMagSensorFamily.UavOpticalPump && x.DataRole == FieldMagDataRole.Raw && x.UavRows.Count > 0)
            .ToList();
        List<ParsedMagFile> selected = raw.Count > 0
            ? raw
            : parsed.Where(x => x.SensorFamily == FieldMagSensorFamily.UavOpticalPump && x.DataRole == FieldMagDataRole.Aggregate && x.UavRows.Count > 0).ToList();
        usedAggregateFallback = raw.Count == 0 && selected.Count > 0;

        List<UavMagCanonicalSample> all = selected
            .SelectMany(x => x.UavRows)
            .OrderBy(x => x.TimestampUtc)
            .ThenBy(x => x.SourceFile, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.SourceRecord)
            .ToList();
        var exactKeys = new HashSet<string>(StringComparer.Ordinal);
        var unique = new List<UavMagCanonicalSample>(all.Count);
        duplicateCount = 0;
        foreach (UavMagCanonicalSample row in all)
        {
            if (!exactKeys.Add(UavExactKey(row)))
            {
                duplicateCount++;
                continue;
            }
            unique.Add(row);
        }

        return ApplyUavQualityFlags(unique);
    }

    private static IReadOnlyList<Gsm19TCanonicalSample> BuildCanonicalBase(
        IReadOnlyList<ParsedMagFile> parsed,
        out int duplicateCount,
        out bool usedAggregateFallback)
    {
        List<ParsedMagFile> raw = parsed
            .Where(x => x.SensorFamily == FieldMagSensorFamily.Gsm19T && x.DataRole == FieldMagDataRole.Raw && x.BaseRows.Count > 0)
            .ToList();
        List<ParsedMagFile> selected = raw.Count > 0
            ? raw
            : parsed.Where(x => x.SensorFamily == FieldMagSensorFamily.Gsm19T && x.DataRole == FieldMagDataRole.Aggregate && x.BaseRows.Count > 0).ToList();
        usedAggregateFallback = raw.Count == 0 && selected.Count > 0;

        List<Gsm19TCanonicalSample> all = selected
            .SelectMany(x => x.BaseRows)
            .OrderBy(x => x.TimestampUtc)
            .ThenBy(x => x.SourceFile, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.SourceRecord)
            .ToList();
        var exactKeys = new HashSet<string>(StringComparer.Ordinal);
        var unique = new List<Gsm19TCanonicalSample>(all.Count);
        duplicateCount = 0;
        foreach (Gsm19TCanonicalSample row in all)
        {
            if (!exactKeys.Add(BaseExactKey(row)))
            {
                duplicateCount++;
                continue;
            }
            unique.Add(row);
        }

        return ApplyBaseQualityFlags(unique);
    }

    private static IReadOnlyList<UavMagCanonicalSample> ApplyUavQualityFlags(IReadOnlyList<UavMagCanonicalSample> rows)
    {
        if (rows.Count == 0)
            return [];

        List<double> mags = rows.Where(x => x.MagneticFieldNt.HasValue).Select(x => x.MagneticFieldNt!.Value).OrderBy(x => x).ToList();
        double median = Median(mags);
        double mad = mags.Count == 0
            ? 0d
            : Median(mags.Select(x => Math.Abs(x - median)).OrderBy(x => x).ToList());
        double outlierThreshold = Math.Max(500d, mad <= 0d ? 500d : 20d * mad);

        HashSet<long> timestampConflicts = rows
            .GroupBy(x => x.TimestampUtc.UtcDateTime.Ticks)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet();

        var result = new List<UavMagCanonicalSample>(rows.Count);
        for (int i = 0; i < rows.Count; i++)
        {
            UavMagCanonicalSample row = rows[i];
            var flags = new List<string>();
            if (timestampConflicts.Contains(row.TimestampUtc.UtcDateTime.Ticks))
                flags.Add("TIMESTAMP_CONFLICT");
            if (row.SensorStatus.Equals("F", StringComparison.OrdinalIgnoreCase) || row.SensorStatus.Equals("FAIL", StringComparison.OrdinalIgnoreCase))
                flags.Add("SENSOR_F");
            if (!IsLonLat(row.Longitude, row.Latitude))
                flags.Add("NO_GNSS");
            if (row.MagneticFieldNt.HasValue && mags.Count > 0 && Math.Abs(row.MagneticFieldNt.Value - median) > outlierThreshold)
                flags.Add("MAG_OUTLIER");
            if (i > 0)
            {
                double gap = (row.TimestampUtc - rows[i - 1].TimestampUtc).TotalSeconds;
                if (gap > 2d)
                    flags.Add("TIME_GAP");
            }
            result.Add(row with { QualityFlags = flags });
        }
        return result;
    }

    /// <summary>Re-evaluate only the explicitly selected, time-resolved Base collection. Never reuse stale QC flags.</summary>
    public static IReadOnlyList<Gsm19TCanonicalSample> EvaluateSelectedBaseQuality(
        IReadOnlyList<Gsm19TCanonicalSample> rows) =>
        ApplyBaseQualityFlags(rows.Select(row => row with { QualityFlags = [] }).ToArray());

    private static IReadOnlyList<Gsm19TCanonicalSample> ApplyBaseQualityFlags(
        IReadOnlyList<Gsm19TCanonicalSample> rows)
    {
        if (rows.Count == 0)
            return [];

        HashSet<long> timestampConflicts = rows
            .GroupBy(x => x.TimestampUtc.UtcDateTime.Ticks)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet();

        var flagsByIndex = Enumerable
            .Range(0, rows.Count)
            .Select(_ => new List<string>())
            .ToList();

        // Pass 1: hard physical/SQ checks. These do not delete canonical data;
        // they only establish production-blocking QC flags.
        for (int i = 0; i < rows.Count; i++)
        {
            Gsm19TCanonicalSample row = rows[i];
            List<string> flags = flagsByIndex[i];

            if (timestampConflicts.Contains(row.TimestampUtc.UtcDateTime.Ticks))
                flags.Add("TIMESTAMP_CONFLICT");

            if (!double.IsFinite(row.MagneticFieldNt) ||
                row.MagneticFieldNt < DiurnalBaseValidityGate.MinPhysicalFieldNt ||
                row.MagneticFieldNt > DiurnalBaseValidityGate.MaxPhysicalFieldNt)
            {
                flags.Add("BASE_VALUE_OUT_OF_RANGE");
            }

            if (row.Sq.HasValue)
            {
                if (!double.IsFinite(row.Sq.Value) ||
                    row.Sq.Value < 0d ||
                    row.Sq.Value > DiurnalBaseValidityGate.MaxSq)
                {
                    flags.Add("SQ_OUT_OF_RANGE");
                }

                if (row.Sq.Value < DiurnalBaseValidityGate.MinSq)
                    flags.Add("LOW_SQ");
            }
        }

        // Pass 2: robust same-UTC-day Base level check. A fixed station should
        // not jump thousands of nT relative to the day's dominant distribution.
        var dayStats = new Dictionary<DateTime, (double Median, double Threshold, int CandidateCount)>();
        foreach (IGrouping<DateTime, int> day in
                 Enumerable.Range(0, rows.Count)
                     .GroupBy(i => rows[i].TimestampUtc.UtcDateTime.Date))
        {
            List<int> candidates = day
                .Where(i => !HasAnyFlag(
                    flagsByIndex[i],
                    "BASE_VALUE_OUT_OF_RANGE",
                    "SQ_OUT_OF_RANGE",
                    "LOW_SQ"))
                .ToList();

            if (candidates.Count == 0)
                continue;

            List<double> values = candidates
                .Select(i => rows[i].MagneticFieldNt)
                .OrderBy(x => x)
                .ToList();
            double median = Median(values);
            List<double> deviations = values
                .Select(x => Math.Abs(x - median))
                .OrderBy(x => x)
                .ToList();
            double mad = Median(deviations);
            double threshold = Math.Max(
                DiurnalBaseValidityGate.DayOutlierFloorNt,
                DiurnalBaseValidityGate.DayOutlierMadMultiplier * mad);

            dayStats[day.Key] = (median, threshold, candidates.Count);
            if (candidates.Count < 5)
                continue;

            foreach (int i in candidates)
            {
                if (Math.Abs(rows[i].MagneticFieldNt - median) > threshold)
                    flagsByIndex[i].Add("BASE_DAY_OUTLIER");
            }
        }

        // Pass 3: first-class GSM session gate. Session identity is assigned by
        // deterministic parser boundaries; QC no longer invents SourceFile+UTC-date sessions.
        foreach (IGrouping<string, int> session in
                 Enumerable.Range(0, rows.Count)
                     .GroupBy(
                         i => string.IsNullOrWhiteSpace(rows[i].SessionStableId)
                             ? string.Join("\u001f", "legacy-review", rows[i].SourceFile,
                                 rows[i].TimestampUtc.UtcDateTime.Date.ToString(
                                     "yyyy-MM-dd", CultureInfo.InvariantCulture))
                             : rows[i].SessionStableId,
                         StringComparer.OrdinalIgnoreCase))
        {
            List<int> indices = session.ToList();
            int invalidCount = indices.Count(i => HasAnyFlag(
                flagsByIndex[i],
                "BASE_VALUE_OUT_OF_RANGE",
                "SQ_OUT_OF_RANGE",
                "LOW_SQ",
                "BASE_DAY_OUTLIER"));

            List<double> validValues = indices
                .Where(i => !HasAnyFlag(
                    flagsByIndex[i],
                    "BASE_VALUE_OUT_OF_RANGE",
                    "SQ_OUT_OF_RANGE",
                    "LOW_SQ",
                    "BASE_DAY_OUTLIER"))
                .Select(i => rows[i].MagneticFieldNt)
                .OrderBy(x => x)
                .ToList();

            bool invalidFraction =
                indices.Count > 0 &&
                invalidCount / (double)indices.Count >=
                DiurnalBaseValidityGate.SessionInvalidFraction;

            DateTime utcDate = rows[indices[0]].TimestampUtc.UtcDateTime.Date;
            bool sourceLevelMismatch = false;
            if (validValues.Count > 0 &&
                dayStats.TryGetValue(utcDate, out var dayStat) &&
                dayStat.CandidateCount >= 5)
            {
                double sessionMedian = Median(validValues);
                sourceLevelMismatch =
                    Math.Abs(sessionMedian - dayStat.Median) >
                    DiurnalBaseValidityGate.SessionMedianDeviationNt;
            }

            if (validValues.Count == 0 || invalidFraction || sourceLevelMismatch)
            {
                foreach (int i in indices)
                    flagsByIndex[i].Add("BASE_SESSION_INVALID");
            }
        }

        // Pass 4: timeline diagnostics. Gap flags remain useful for audit, while
        // the production interpolator separately refuses gaps over its limit.
        for (int i = 1; i < rows.Count; i++)
        {
            Gsm19TCanonicalSample row = rows[i];
            Gsm19TCanonicalSample previous = rows[i - 1];
            double gap = (row.TimestampUtc - previous.TimestampUtc).TotalSeconds;
            // A >999 s gap is both Session-boundary evidence and an audit fact.
            // Preserve it on the first sample of the new Session, but never
            // evaluate magnetic-jump continuity across a Session boundary.
            if (gap > 60d)
                flagsByIndex[i].Add("TIME_GAP");
            if (gap > 999d)
                flagsByIndex[i].Add("GEMLINK_LONG_GAP");
            if (!string.Equals(row.SessionStableId, previous.SessionStableId, StringComparison.OrdinalIgnoreCase))
                continue;
            if (gap is > 0 and <= 60d &&
                Math.Abs(row.MagneticFieldNt - previous.MagneticFieldNt) > 200d)
            {
                flagsByIndex[i].Add("MAG_JUMP");
            }
        }

        var result = new List<Gsm19TCanonicalSample>(rows.Count);
        for (int i = 0; i < rows.Count; i++)
        {
            result.Add(
                rows[i] with
                {
                    QualityFlags = flagsByIndex[i]
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList()
                });
        }
        return result;
    }

    private static IReadOnlyList<GsmSession> BuildGsmSessionQc(
        IReadOnlyList<GsmSession> sessions,
        IReadOnlyList<Gsm19TCanonicalSample> samples)
    {
        var flagsBySession = samples
            .Where(x => !string.IsNullOrWhiteSpace(x.SessionStableId))
            .GroupBy(x => x.SessionStableId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.SelectMany(x => x.QualityFlags)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                StringComparer.OrdinalIgnoreCase);

        return sessions.Select(session =>
        {
            string[] flags = flagsBySession.TryGetValue(session.SessionStableId, out string[]? found)
                ? found : [];
            string[] blocking = flags.Where(flag =>
                flag is "BASE_VALUE_OUT_OF_RANGE" or "SQ_OUT_OF_RANGE" or "LOW_SQ" or
                    "BASE_DAY_OUTLIER" or "BASE_SESSION_INVALID" or "TIMESTAMP_CONFLICT")
                .ToArray();
            string status = blocking.Length > 0 ? "INVALID" :
                flags.Length > 0 ? "REVIEW" : "PASS";
            return session with { QcStatus = status, QcReasons = blocking.Length > 0 ? blocking : flags };
        }).ToArray();
    }

    private static bool HasAnyFlag(
        IReadOnlyCollection<string> flags,
        params string[] candidates) =>
        candidates.Any(candidate =>
            flags.Contains(
                candidate,
                StringComparer.OrdinalIgnoreCase));

    private static IReadOnlyList<FieldDataQualityIssue> BuildQualityIssues(
        IReadOnlyList<ParsedMagFile> parsed,
        IReadOnlyList<UavMagCanonicalSample> uav,
        IReadOnlyList<Gsm19TCanonicalSample> baseSamples,
        int uavDuplicates,
        int baseDuplicates,
        bool uavAggregateFallback,
        bool baseAggregateFallback)
    {
        var issues = new List<FieldDataQualityIssue>();

        int unknown = parsed.Count(x => x.SensorFamily == FieldMagSensorFamily.Unknown);
        if (unknown > 0)
            issues.Add(new FieldDataQualityIssue("INFO", "UNCLASSIFIED_FILES", "FILES", "—", $"有 {unknown} 个支持扩展名文件未识别为 UAV / GSM-19T / 日改派生数据，已排除在规范拼接之外。"));

        int derived = parsed.Count(x => x.DataRole is FieldMagDataRole.Aggregate or FieldMagDataRole.DiurnalSegment or FieldMagDataRole.DiurnalMerged or FieldMagDataRole.OtherDerived);
        if (derived > 0)
            issues.Add(new FieldDataQualityIssue("INFO", "DERIVED_EXCLUDED", "FILES", "—", $"识别到 {derived} 个已拼接/日改派生文件；原始文件存在时不会再次混入规范拼接。"));

        if (uavAggregateFallback)
            issues.Add(new FieldDataQualityIssue("WARNING", "UAV_AGGREGATE_FALLBACK", "UAV", "—", "未发现可解析的 UAV Raw 文件，规范数据集回退使用已拼接 Aggregate 文件。"));
        if (baseAggregateFallback)
            issues.Add(new FieldDataQualityIssue("WARNING", "BASE_AGGREGATE_FALLBACK", "GSM19T", "—", "未发现可解析的 GSM-19T Raw 文件，规范数据集回退使用已拼接 Aggregate 文件。"));

        if (uavDuplicates > 0)
            issues.Add(new FieldDataQualityIssue("INFO", "UAV_EXACT_DUPLICATE", "UAV", "—", $"UAV 发现 {uavDuplicates} 条时间戳与数据内容均相同的重复记录，规范数据集中去重；原始文件保持不变。"));
        if (baseDuplicates > 0)
            issues.Add(new FieldDataQualityIssue("INFO", "BASE_EXACT_DUPLICATE", "GSM19T", "—", $"GSM-19T 发现 {baseDuplicates} 条时间戳与数据内容均相同的重复记录，规范数据集中去重；原始文件保持不变。"));

        AddFlagIssue(issues, uav, "TIMESTAMP_CONFLICT", "WARNING", "UAV_TIMESTAMP_CONFLICT", "UAV", "UAV 存在相同 UTC 时间戳但数据内容不同的记录；完整整合数据全部保留，MagOps 原生日变按源记录逐条处理并要求人工复核。", x => x.TimestampUtc);
        AddFlagIssue(issues, baseSamples, "TIMESTAMP_CONFLICT", "WARNING", "BASE_TIMESTAMP_CONFLICT", "GSM19T", "GSM-19T 存在相同 UTC 时间戳但磁值/SQ 不同的记录；完整整合数据全部保留。若 0.1 秒兼容键重复，原生日变将保守拒绝该 UTC 日并要求人工复核。", x => x.TimestampUtc);

        AddFlagIssue(issues, uav, "SENSOR_F", "WARNING", "UAV_SENSOR_F", "UAV", "UAV 记录的传感器状态为 F；完整整合数据保留，但默认不用于航迹完成度和 MagOps 原生日变数值计算。", x => x.TimestampUtc);
        AddFlagIssue(issues, uav, "MAG_OUTLIER", "WARNING", "UAV_MAG_OUTLIER", "UAV", "UAV 磁值相对当日主体分布出现显著离群；仅标记，不自动删除。", x => x.TimestampUtc);
        AddFlagIssue(issues, uav, "NO_GNSS", "WARNING", "UAV_NO_GNSS", "UAV", "UAV 记录未识别到有效经纬度；可参与磁值留档，但不能用于航迹进度匹配。", x => x.TimestampUtc);
        AddFlagIssue(issues, uav, "TIME_GAP", "WARNING", "UAV_TIME_GAP", "UAV", "UAV 时间轴存在大于 2 秒的记录间隔，请核对文件缺失、停机或跨段情况。", x => x.TimestampUtc);

        AddFlagIssue(issues, baseSamples, "BASE_VALUE_OUT_OF_RANGE", "ERROR", "BASE_VALUE_OUT_OF_RANGE", "GSM19T", $"GSM-19T 磁值超出生产安全范围 {DiurnalBaseValidityGate.MinPhysicalFieldNt:0}–{DiurnalBaseValidityGate.MaxPhysicalFieldNt:0} nT；canonical 仍保留，但原生日变生产模式会排除这些记录。", x => x.TimestampUtc);
        AddFlagIssue(issues, baseSamples, "SQ_OUT_OF_RANGE", "ERROR", "BASE_SQ_OUT_OF_RANGE", "GSM19T", $"GSM-19T SQ 超出 0–{DiurnalBaseValidityGate.MaxSq:0} 合法范围，可能存在字段错位/源文件损坏；生产日变会排除。", x => x.TimestampUtc);
        AddFlagIssue(issues, baseSamples, "LOW_SQ", "WARNING", "BASE_LOW_SQ", "GSM19T", $"GSM-19T 存在 SQ < {DiurnalBaseValidityGate.MinSq:0} 的记录；完整数据保留，但生产日变不会使用这些 Base 点。", x => x.TimestampUtc);
        AddFlagIssue(issues, baseSamples, "BASE_DAY_OUTLIER", "ERROR", "BASE_DAY_OUTLIER", "GSM19T", $"GSM-19T 磁值相对同一 UTC 日的稳健主体分布偏离过大（阈值至少 {DiurnalBaseValidityGate.DayOutlierFloorNt:0} nT）；生产日变会排除。", x => x.TimestampUtc);
        AddFlagIssue(issues, baseSamples, "BASE_SESSION_INVALID", "ERROR", "BASE_SESSION_INVALID", "GSM19T", "GSM-19T 某采集 Session 被判定为无效（坏值占比过高、无有效记录或整体场值与当日主体明显不一致）；该 Session 仅留档，不进入生产日变。", x => x.TimestampUtc);
        AddFlagIssue(issues, baseSamples, "MAG_JUMP", "WARNING", "BASE_MAG_JUMP", "GSM19T", "GSM-19T 在 60 秒内出现超过 200 nT 的跳变，请核对基站启动、干扰或记录质量；该标记本身不单独剔除，生产门控由物理/SQ/稳健离群规则决定。", x => x.TimestampUtc);
        AddFlagIssue(issues, baseSamples, "TIME_GAP", "WARNING", "BASE_TIME_GAP", "GSM19T", "GSM-19T 时间轴存在超过 60 秒的空档。", x => x.TimestampUtc);
        AddFlagIssue(issues, baseSamples, "GEMLINK_LONG_GAP", "WARNING", "GEMLINK_LONG_GAP", "GSM19T", "GSM-19T 相邻记录间隔超过 999 秒；GEMLink 对照包仍可保留该事实，但 MagOps 生产日变默认拒绝跨越此类长空档插值。", x => x.TimestampUtc);

        int utcDates = uav.Select(x => x.TimestampUtc.UtcDateTime.Date)
            .Concat(baseSamples.Select(x => x.TimestampUtc.UtcDateTime.Date))
            .Distinct()
            .Count();
        if (utcDates > 1)
            issues.Add(new FieldDataQualityIssue("INFO", "CROSS_UTC_DATE", "TIME", "—", $"完整整合数据跨 {utcDates} 个 UTC 日期；MagOps 原生日变在同一完整文件内按绝对 UTC Date 内部分组，不跨日期插值，也不再要求物理拆分文件。"));

        foreach (ParsedMagFile file in parsed.Where(x => x.Warnings.Count > 0))
        {
            foreach (string warning in file.Warnings)
                issues.Add(new FieldDataQualityIssue("WARNING", "FILE_PARSE_WARNING", "FILES", "—", $"{Path.GetFileName(file.Path)}：{warning}"));
        }

        return issues;
    }

    private static IReadOnlyList<GemLinkBridgeJob> BuildGemLinkJobs(
        IReadOnlyList<UavMagCanonicalSample> uav,
        IReadOnlyList<Gsm19TCanonicalSample> baseSamples)
    {
        IReadOnlyList<UavMagCanonicalSample> bridgeUav = ResolveUavTimestampConflicts(uav);
        IReadOnlyList<Gsm19TCanonicalSample> bridgeBase = ResolveBaseTimestampConflicts(baseSamples);
        DateTime[] dates = bridgeUav.Select(x => x.TimestampUtc.UtcDateTime.Date)
            .Concat(bridgeBase.Select(x => x.TimestampUtc.UtcDateTime.Date))
            .Distinct()
            .OrderBy(x => x)
            .ToArray();
        var result = new List<GemLinkBridgeJob>(dates.Length);
        foreach (DateTime date in dates)
        {
            List<UavMagCanonicalSample> rover = bridgeUav
                .Where(IsUsableUavForDiurnal)
                .Where(x => x.TimestampUtc.UtcDateTime.Date == date)
                .ToList();
            List<Gsm19TCanonicalSample> baseRows = bridgeBase
                .Where(x => x.TimestampUtc.UtcDateTime.Date == date)
                .ToList();
            var warnings = new List<string>();
            if (rover.Count == 0)
                warnings.Add("该 UTC 日期没有可用 Rover 记录");
            if (baseRows.Count == 0)
                warnings.Add("该 UTC 日期没有 Base 记录");
            if (baseRows.Any(x => x.QualityFlags.Contains("GEMLINK_LONG_GAP")))
                warnings.Add("Base 含 >999 s 长空档");
            if (baseRows.Any(x => x.QualityFlags.Contains("LOW_SQ")))
                warnings.Add("Base 含低 SQ 记录");
            if (baseRows.Any(x => x.QualityFlags.Contains("MAG_JUMP")))
                warnings.Add("Base 含磁值跳变");
            if (baseRows.Any(x => !DiurnalBaseValidityGate.IsEligible(x)))
                warnings.Add("Base 含被 MagOps 生产门控排除的记录；GEMLink Golden 对照需人工确认");

            result.Add(new GemLinkBridgeJob(
                date,
                rover.Count,
                baseRows.Count,
                rover.Count == 0 ? null : rover.Min(x => x.TimestampUtc),
                rover.Count == 0 ? null : rover.Max(x => x.TimestampUtc),
                baseRows.Count == 0 ? null : baseRows.Min(x => x.TimestampUtc),
                baseRows.Count == 0 ? null : baseRows.Max(x => x.TimestampUtc),
                warnings));
        }
        return result;
    }

    private static void AddFlagIssue<T>(
        List<FieldDataQualityIssue> issues,
        IReadOnlyList<T> rows,
        string flag,
        string severity,
        string code,
        string scope,
        string message,
        Func<T, DateTimeOffset> timestamp)
    {
        List<T> matches = rows.Where(row => QualityFlagsOf(row).Contains(flag)).ToList();
        if (matches.Count == 0)
            return;
        DateTimeOffset first = matches.Min(timestamp);
        DateTimeOffset last = matches.Max(timestamp);
        issues.Add(new FieldDataQualityIssue(
            severity,
            code,
            scope,
            FormatUtcRange(first, last),
            $"{message} 命中 {matches.Count} 条。"));
    }

    private static IReadOnlyList<string> QualityFlagsOf<T>(T row) => row switch
    {
        UavMagCanonicalSample uav => uav.QualityFlags,
        Gsm19TCanonicalSample baseRow => baseRow.QualityFlags,
        _ => []
    };

    private static string UavExactKey(UavMagCanonicalSample row) => string.Join("|",
        row.TimestampUtc.UtcDateTime.Ticks.ToString(CultureInfo.InvariantCulture),
        Number(row.Longitude, "R"),
        Number(row.Latitude, "R"),
        Number(row.AltitudeM, "R"),
        Number(row.LaserAltitudeM, "R"),
        Number(row.MagneticFieldNt, "R"),
        Number(row.Signal, "R"),
        row.GpsStatus,
        row.Satellites?.ToString(CultureInfo.InvariantCulture) ?? "",
        row.SensorStatus);

    private static string BaseExactKey(Gsm19TCanonicalSample row) => string.Join("|",
        row.TimestampUtc.UtcDateTime.Ticks.ToString(CultureInfo.InvariantCulture),
        row.MagneticFieldNt.ToString("R", CultureInfo.InvariantCulture),
        Number(row.Sq, "R"));

    private static IReadOnlyList<UavMagCanonicalSample> ResolveUavTimestampConflicts(IEnumerable<UavMagCanonicalSample> rows) =>
        rows.GroupBy(x => x.TimestampUtc.UtcDateTime.Ticks)
            .Select(group =>
            {
                UavMagCanonicalSample chosen = group
                    .OrderByDescending(UavPreference)
                    .ThenBy(x => x.SourceFile, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(x => x.SourceRecord)
                    .First();
                IReadOnlyList<string> flags = group
                    .SelectMany(x => x.QualityFlags)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                return chosen with { QualityFlags = flags };
            })
            .OrderBy(x => x.TimestampUtc)
            .ToList();

    private static IReadOnlyList<Gsm19TCanonicalSample> ResolveBaseTimestampConflicts(IEnumerable<Gsm19TCanonicalSample> rows) =>
        rows.GroupBy(x => x.TimestampUtc.UtcDateTime.Ticks)
            .Select(group =>
            {
                Gsm19TCanonicalSample chosen = group
                    .OrderByDescending(x => x.Sq ?? double.MinValue)
                    .ThenBy(x => x.SourceFile, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(x => x.SourceRecord)
                    .First();
                IReadOnlyList<string> flags = group
                    .SelectMany(x => x.QualityFlags)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                return chosen with { QualityFlags = flags };
            })
            .OrderBy(x => x.TimestampUtc)
            .ToList();

    private static int UavPreference(UavMagCanonicalSample row)
    {
        int score = 0;
        if (!row.SensorStatus.Equals("F", StringComparison.OrdinalIgnoreCase)) score += 4;
        if (IsLonLat(row.Longitude, row.Latitude)) score += 2;
        if (row.MagneticFieldNt.HasValue) score += 1;
        return score;
    }

    private static bool IsUsableUavForTrack(UavMagCanonicalSample row) =>
        !row.SensorStatus.Equals("F", StringComparison.OrdinalIgnoreCase) &&
        IsLonLat(row.Longitude, row.Latitude);

    private static bool IsUsableUavForDiurnal(UavMagCanonicalSample row) =>
        !row.SensorStatus.Equals("F", StringComparison.OrdinalIgnoreCase) &&
        row.MagneticFieldNt.HasValue;

    private static string? FindUavHeaderLine(string text)
    {
        using var reader = new StringReader(text);
        for (int i = 0; i < 120; i++)
        {
            string? line = reader.ReadLine();
            if (line is null)
                break;
            string normalized = NormalizeToken(line);
            if ((normalized.Contains("utcdate", StringComparison.OrdinalIgnoreCase) ||
                 normalized.Contains("utcdata", StringComparison.OrdinalIgnoreCase)) &&
                normalized.Contains("utctime", StringComparison.OrdinalIgnoreCase) &&
                (normalized.Contains("lon", StringComparison.OrdinalIgnoreCase) || normalized.Contains("longitude", StringComparison.OrdinalIgnoreCase)))
                return line;
        }
        return null;
    }

    private static string[] SplitUavSegment(string segment, char delimiter)
    {
        string cleaned = segment.Trim().Replace("\r", " ").Replace("\n", " ");
        if (delimiter == ' ')
            return cleaned.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return cleaned.Split(delimiter, StringSplitOptions.TrimEntries)
            .Select(x => x.Trim().Trim('"'))
            .ToArray();
    }

    private static double? FieldNumber(string[] fields, int headerIndex, int dateOffset)
    {
        int index = headerIndex < 0 ? -1 : headerIndex - dateOffset;
        if (index < 0 || index >= fields.Length)
            return null;
        return TryNumber(fields[index], out double value) ? value : null;
    }

    private static string FieldText(string[] fields, int headerIndex, int dateOffset)
    {
        int index = headerIndex < 0 ? -1 : headerIndex - dateOffset;
        return index >= 0 && index < fields.Length ? fields[index].Trim().Trim('"') : "";
    }

    private static int? FieldInteger(string[] fields, int headerIndex, int dateOffset)
    {
        double? value = FieldNumber(fields, headerIndex, dateOffset);
        return value.HasValue && value.Value is >= int.MinValue and <= int.MaxValue
            ? (int)Math.Round(value.Value)
            : null;
    }

    private static (double? Lon, double? Lat) InferLonLat(IReadOnlyList<string> fields)
    {
        for (int i = 0; i + 1 < fields.Count; i++)
        {
            if (!TryNumber(fields[i], out double lon) || !TryNumber(fields[i + 1], out double lat))
                continue;
            if (IsLonLat(lon, lat))
                return (lon, lat);
        }
        return (null, null);
    }

    private static double? InferMagneticField(IReadOnlyList<string> fields)
    {
        foreach (string field in fields)
        {
            if (TryNumber(field, out double value) && value is >= 10000d and <= 100000d)
                return value;
        }
        return null;
    }

    private static string InferSensorStatus(IReadOnlyList<string> fields)
    {
        for (int i = fields.Count - 1; i >= 0; i--)
        {
            string token = fields[i].Trim().Trim('"');
            if (token.Equals("T", StringComparison.OrdinalIgnoreCase) || token.Equals("F", StringComparison.OrdinalIgnoreCase))
                return token.ToUpperInvariant();
        }
        return "";
    }

    private static bool TryFindHeaderDate(string text, string path, out DateTime date)
    {
        string header = text.Length > 8192 ? text[..8192] : text;

        // Prefer the survey-file date because GEM headers can also contain
        // firmware/calibration dates that are not the acquisition date.
        Match gsmSurveyDate = GsmSurveyFileDateRegex.Match(header);
        if (gsmSurveyDate.Success && TryGsmSurveyDateParts(gsmSurveyDate, out date))
            return true;

        Match iso = IsoDateRegex.Match(header);
        if (iso.Success && TryDateParts(iso, out date))
            return true;
        Match shortDate = ShortDateRegex.Match(header);
        if (shortDate.Success &&
            int.TryParse(shortDate.Groups["year"].Value, out int yy) &&
            int.TryParse(shortDate.Groups["month"].Value, out int mm) &&
            int.TryParse(shortDate.Groups["day"].Value, out int dd))
        {
            int year = yy >= 70 ? 1900 + yy : 2000 + yy;
            if (TryCreateDate(year, mm, dd, out date))
                return true;
        }

        Match fileDate = IsoDateRegex.Match(Path.GetFileName(path));
        if (fileDate.Success && TryDateParts(fileDate, out date))
            return true;

        date = default;
        return false;
    }

    private static bool TryGsmSurveyDateParts(Match match, out DateTime date)
    {
        date = default;
        if (!int.TryParse(match.Groups["day"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int day) ||
            !int.TryParse(match.Groups["year"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int year) ||
            !TryRomanMonth(match.Groups["month"].Value, out int month))
            return false;

        if (year < 100)
            year = year >= 70 ? 1900 + year : 2000 + year;

        return TryCreateDate(year, month, day, out date);
    }

    private static bool TryRomanMonth(string value, out int month)
    {
        month = value.Trim().ToUpperInvariant() switch
        {
            "I" => 1,
            "II" => 2,
            "III" => 3,
            "IV" => 4,
            "V" => 5,
            "VI" => 6,
            "VII" => 7,
            "VIII" => 8,
            "IX" => 9,
            "X" => 10,
            "XI" => 11,
            "XII" => 12,
            _ => 0
        };
        return month != 0;
    }

    private static bool TryDateParts(Match match, out DateTime date)
    {
        if (int.TryParse(match.Groups["year"].Value, out int year) &&
            int.TryParse(match.Groups["month"].Value, out int month) &&
            int.TryParse(match.Groups["day"].Value, out int day) &&
            TryCreateDate(year, month, day, out date))
            return true;
        date = default;
        return false;
    }

    private static int CountMalformedBaseDataLines(string text)
    {
        int count = 0;
        bool inData = false;
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            if (!inData)
            {
                if (Regex.IsMatch(
                    line,
                    @"^[\t ]*/time[\t ]+n?t[\t ]+sq",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                {
                    inData = true;
                }
                continue;
            }

            if (string.IsNullOrWhiteSpace(line) || BaseRowRegex.IsMatch(line))
                continue;

            // After the /time nT sq header, a numeric-looking non-matching line
            // is suspicious source data. Report it, but do not guess how to split,
            // repair, or invent missing fields.
            if (Regex.IsMatch(line, @"^[\t ]*\d", RegexOptions.CultureInvariant))
                count++;
        }
        return count;
    }

    private static bool TryCreateDate(int year, int month, int day, out DateTime date)
    {
        try
        {
            date = new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc);
            return true;
        }
        catch
        {
            date = default;
            return false;
        }
    }

    private static bool TryParseUtcTimestamp(string dateText, string timeText, out DateTimeOffset stamp)
    {
        stamp = default;
        string compactDate = dateText.Replace("-", "").Replace("/", "").Replace(".", "");
        if (compactDate.Length != 8 || !int.TryParse(compactDate[..4], out int year) || !int.TryParse(compactDate.Substring(4, 2), out int month) || !int.TryParse(compactDate.Substring(6, 2), out int day))
            return false;
        if (!TimeSpan.TryParse(timeText, CultureInfo.InvariantCulture, out TimeSpan time))
            return false;
        try
        {
            stamp = new DateTimeOffset(year, month, day, time.Hours, time.Minutes, time.Seconds, TimeSpan.Zero)
                .AddTicks(time.Ticks % TimeSpan.TicksPerSecond);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParseCompactTime(
        string value,
        out TimeSpan time,
        out bool explicitNextDayMidnight)
    {
        time = default;
        explicitNextDayMidnight = false;

        string text = value.Trim();
        string[] pieces = text.Split('.', 2);
        string whole = pieces[0].PadLeft(6, '0');
        if (whole.Length != 6 ||
            !int.TryParse(whole[..2], out int hour) ||
            !int.TryParse(
                whole.Substring(2, 2),
                out int minute) ||
            !int.TryParse(
                whole.Substring(4, 2),
                out int second))
        {
            return false;
        }

        double fractional = 0d;
        if (pieces.Length == 2 &&
            !double.TryParse(
                "0." + pieces[1],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out fractional))
        {
            return false;
        }

        if (hour == 24 &&
            minute == 0 &&
            second == 0 &&
            Math.Abs(fractional) < 1e-12)
        {
            // GEMLink-compatible 240000.0 semantics. At the canonical
            // layer this means the next UTC date at 00:00:00.
            explicitNextDayMidnight = true;
            time = TimeSpan.Zero;
            return true;
        }

        if (hour is < 0 or > 23 ||
            minute is < 0 or > 59 ||
            second is < 0 or > 59)
        {
            return false;
        }

        time =
            new TimeSpan(
                0,
                hour,
                minute,
                second)
            .Add(
                TimeSpan.FromSeconds(
                    fractional));
        return true;
    }

    private static int CountPhysicalDataLines(string text)
    {
        int count = 0;
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            if (UavTimestampRegex.IsMatch(line))
                count++;
        }
        return count;
    }

    private static bool ContainsCoordinateHeaders(string text)
    {
        string normalized = NormalizeToken(text);
        bool lon = normalized.Contains("longitude", StringComparison.OrdinalIgnoreCase) || normalized.Contains("lon", StringComparison.OrdinalIgnoreCase) || normalized.Contains("经度", StringComparison.Ordinal);
        bool lat = normalized.Contains("latitude", StringComparison.OrdinalIgnoreCase) || normalized.Contains("lat", StringComparison.OrdinalIgnoreCase) || normalized.Contains("纬度", StringComparison.Ordinal);
        return lon && lat;
    }

    private static string NormalizeToken(string value) => new(
        (value ?? "")
            .ToLowerInvariant()
            .Where(ch => char.IsLetterOrDigit(ch) || ch >= 0x4e00)
            .ToArray());

    private static bool TryNumber(string value, out double result) =>
        double.TryParse(value.Trim().Trim('"'), NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out result) ||
        double.TryParse(value.Trim().Trim('"'), NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out result);

    private static bool IsLonLat(double? lon, double? lat) =>
        lon.HasValue && lat.HasValue && IsLonLat(lon.Value, lat.Value);

    private static bool IsLonLat(double lon, double lat) =>
        double.IsFinite(lon) && double.IsFinite(lat) && lon is >= -180 and <= 180 && lat is >= -90 and <= 90 &&
        !(Math.Abs(lon) < 1e-12 && Math.Abs(lat) < 1e-12);

    private static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
            return 0d;
        int mid = values.Count / 2;
        return values.Count % 2 == 1
            ? values[mid]
            : (values[mid - 1] + values[mid]) / 2d;
    }

    private static string ComputeFingerprint(
        IReadOnlyList<FieldMagFileScan> files,
        IReadOnlyList<UavMagCanonicalSample> uav,
        IReadOnlyList<Gsm19TCanonicalSample> baseSamples)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Append(string text) => hash.AppendData(Encoding.UTF8.GetBytes(text));

        foreach (FieldMagFileScan file in files
                     .OrderBy(x => Path.GetFileName(x.Path), StringComparer.OrdinalIgnoreCase)
                     .ThenBy(x => x.SensorFamily)
                     .ThenBy(x => x.DataRole))
        {
            Append($"FILE|{file.SourceFileStableId}|{file.SourceContentSha256}|{file.SensorFamily}|{file.DataRole}|{file.RecordCount}\n");
        }
        foreach (UavMagCanonicalSample row in uav)
        {
            Append(string.Join("|",
                "UAV",
                row.TimestampUtc.UtcDateTime.Ticks.ToString(CultureInfo.InvariantCulture),
                Number(row.Longitude, "R"),
                Number(row.Latitude, "R"),
                Number(row.MagneticFieldNt, "R"),
                row.SensorStatus) + "\n");
        }
        foreach (Gsm19TCanonicalSample row in baseSamples)
        {
            Append(string.Join("|",
                "BASE",
                row.TimestampUtc.UtcDateTime.Ticks.ToString(CultureInfo.InvariantCulture),
                row.MagneticFieldNt.ToString("R", CultureInfo.InvariantCulture),
                Number(row.Sq, "R"),
                row.SourceFileStableId,
                row.SessionStableId,
                row.SourceRecord.ToString(CultureInfo.InvariantCulture)) + "\n");
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static IReadOnlyList<GeoCoordinate> DownsampleTrack(
        IReadOnlyList<GeoCoordinate> points,
        double minSpacingM,
        int maxPoints)
    {
        if (points.Count <= 1)
            return points.ToList();
        var result = new List<GeoCoordinate> { points[0] };
        GeoCoordinate previous = points[0];
        for (int i = 1; i < points.Count; i++)
        {
            GeoCoordinate current = points[i];
            if (MagOps.Core.Routes.RouteCoordinateMath.Haversine(previous.Longitude, previous.Latitude, current.Longitude, current.Latitude) < minSpacingM)
                continue;
            result.Add(current);
            previous = current;
            if (result.Count >= maxPoints)
                break;
        }
        if (result.Count < maxPoints && result[^1] != points[^1])
            result.Add(points[^1]);
        return result;
    }

    private static void WriteCoverageSummaryCsv(
        string path,
        IReadOnlyList<FieldMagDailyCoverageSummary> summaries, TimeZoneInfo timeZone)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        writer.WriteLine(ProjectTime.Basis(timeZone) == BeijingProgressTimeZone
            ? "BeijingDate,UavEligibleRecords,CoveredUavRecords,UncoveredUavRecords,CoveragePercent,BaseRecords,AcceptedBaseRecords,RejectedBaseRecords,FirstUncoveredBeijing,LastUncoveredBeijing,LargestUncoveredSeconds,UncoveredSpanCount,Status,Reasons"
            : "ProjectDate,UavEligibleRecords,CoveredUavRecords,UncoveredUavRecords,CoveragePercent,BaseRecords,AcceptedBaseRecords,RejectedBaseRecords,FirstUncoveredProjectTime,LastUncoveredProjectTime,LargestUncoveredSeconds,UncoveredSpanCount,Status,Reasons");
        foreach (FieldMagDailyCoverageSummary row in summaries.OrderBy(x => x.BeijingDate))
        {
            string first = row.FirstUncoveredUtc.HasValue
                ? ProjectTime.Display(row.FirstUncoveredUtc.Value, timeZone).ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)
                : "";
            string last = row.LastUncoveredUtc.HasValue
                ? ProjectTime.Display(row.LastUncoveredUtc.Value, timeZone).ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)
                : "";
            writer.WriteLine(string.Join(",", new[]
            {
                Csv(row.BeijingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                row.UavEligibleRecords.ToString(CultureInfo.InvariantCulture),
                row.CoveredUavRecords.ToString(CultureInfo.InvariantCulture),
                row.UncoveredUavRecords.ToString(CultureInfo.InvariantCulture),
                row.CoveragePercent.ToString("0.000", CultureInfo.InvariantCulture),
                row.BaseRecords.ToString(CultureInfo.InvariantCulture),
                row.AcceptedBaseRecords.ToString(CultureInfo.InvariantCulture),
                row.RejectedBaseRecords.ToString(CultureInfo.InvariantCulture),
                Csv(first),
                Csv(last),
                row.LargestUncoveredSpanSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                row.UncoveredSpanCount.ToString(CultureInfo.InvariantCulture),
                Csv(row.Status),
                Csv(string.Join("|", row.Reasons))
            }));
        }
    }

    private static void WriteInventoryCsv(string path, IReadOnlyList<FieldMagFileScan> files)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        writer.WriteLine("文件,SourceFileStableId,传感器类型,数据角色,记录数,首条UTC,末条UTC,识别依据,警告");
        foreach (FieldMagFileScan row in files)
        {
            writer.WriteLine(string.Join(",",
                Csv(row.Path),
                Csv(row.SourceFileStableId),
                Csv(SensorLabel(row.SensorFamily)),
                Csv(RoleLabel(row.DataRole)),
                row.RecordCount.ToString(CultureInfo.InvariantCulture),
                Csv(FormatUtc(row.FirstTimeUtc)),
                Csv(FormatUtc(row.LastTimeUtc)),
                Csv(row.Evidence),
                Csv(string.Join("；", row.Warnings))));
        }
    }

    private static void WriteIntegratedUavCsvGzip(
        string path,
        IEnumerable<UavMagCanonicalSample> rows,
        MagDataSourceCatalog sourceCatalog)
    {
        MagDataCompactExport.WriteGzipCsv(
            path,
            writer =>
            {
                writer.WriteLine(
                    "DateUtc,TimeUtc,Longitude,Latitude,AltitudeM,LaserAltitudeM,MagNt,Signal,GpsStatus,Satellites,SensorStatus,SourceId,SourceRecord,QualityFlags");

                foreach (UavMagCanonicalSample row in rows)
                {
                    writer.WriteLine(
                        string.Join(
                            ",",
                            Csv(row.TimestampUtc.UtcDateTime.ToString(
                                "yyyy-MM-dd",
                                CultureInfo.InvariantCulture)),
                            Csv(row.TimestampUtc.UtcDateTime.ToString(
                                "HH:mm:ss.fff",
                                CultureInfo.InvariantCulture)),
                            Number(
                                row.Longitude,
                                "0.########"),
                            Number(
                                row.Latitude,
                                "0.########"),
                            Number(
                                row.AltitudeM,
                                "0.###"),
                            Number(
                                row.LaserAltitudeM,
                                "0.###"),
                            Number(
                                row.MagneticFieldNt,
                                "0.###"),
                            Number(
                                row.Signal,
                                "0.###"),
                            Csv(row.GpsStatus),
                            row.Satellites?.ToString(
                                CultureInfo.InvariantCulture) ??
                                "",
                            Csv(row.SensorStatus),
                            sourceCatalog
                                .GetSourceId(row.SourceFile)
                                .ToString(
                                    CultureInfo.InvariantCulture),
                            row.SourceRecord.ToString(
                                CultureInfo.InvariantCulture),
                            Csv(string.Join(
                                ";",
                                row.QualityFlags))));
                }
            });
    }

    private static void WriteIntegratedBaseCsvGzip(
        string path,
        IEnumerable<Gsm19TCanonicalSample> rows,
        MagDataSourceCatalog sourceCatalog)
    {
        MagDataCompactExport.WriteGzipCsv(
            path,
            writer =>
            {
                writer.WriteLine(
                    "DateUtc,TimeUtc,MagNt,SQ,SourceId,SourceRecord,QualityFlags");

                foreach (Gsm19TCanonicalSample row in rows)
                {
                    writer.WriteLine(
                        string.Join(
                            ",",
                            Csv(row.TimestampUtc.UtcDateTime.ToString(
                                "yyyy-MM-dd",
                                CultureInfo.InvariantCulture)),
                            Csv(row.TimestampUtc.UtcDateTime.ToString(
                                "HH:mm:ss.fff",
                                CultureInfo.InvariantCulture)),
                            row.MagneticFieldNt.ToString(
                                "0.###",
                                CultureInfo.InvariantCulture),
                            Number(
                                row.Sq,
                                "0.###"),
                            sourceCatalog
                                .GetSourceId(row.SourceFile)
                                .ToString(
                                    CultureInfo.InvariantCulture),
                            row.SourceRecord.ToString(
                                CultureInfo.InvariantCulture),
                            Csv(string.Join(
                                ";",
                                row.QualityFlags))));
                }
            });
    }

    private static void WriteUavCsv(string path, IEnumerable<UavMagCanonicalSample> rows)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        writer.WriteLine("DateUtc,TimeUtc,Longitude,Latitude,AltitudeM,LaserAltitudeM,MagNt,Signal,GpsStatus,Satellites,SensorStatus,SourceFile,SourceRecord,QualityFlags");
        foreach (UavMagCanonicalSample row in rows)
        {
            writer.WriteLine(string.Join(",",
                Csv(row.TimestampUtc.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                Csv(row.TimestampUtc.UtcDateTime.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)),
                Number(row.Longitude, "0.########"),
                Number(row.Latitude, "0.########"),
                Number(row.AltitudeM, "0.###"),
                Number(row.LaserAltitudeM, "0.###"),
                Number(row.MagneticFieldNt, "0.###"),
                Number(row.Signal, "0.###"),
                Csv(row.GpsStatus),
                row.Satellites?.ToString(CultureInfo.InvariantCulture) ?? "",
                Csv(row.SensorStatus),
                Csv(row.SourceFile),
                row.SourceRecord.ToString(CultureInfo.InvariantCulture),
                Csv(string.Join(";", row.QualityFlags))));
        }
    }

    private static void WriteBaseCsv(string path, IEnumerable<Gsm19TCanonicalSample> rows)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        writer.WriteLine("DateUtc,TimeUtc,MagNt,SQ,SourceFileStableId,SessionStableId,SessionOrdinal,SourceFile,SourceRecord,QualityFlags");
        foreach (Gsm19TCanonicalSample row in rows)
        {
            writer.WriteLine(string.Join(",",
                Csv(row.TimestampUtc.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                Csv(row.TimestampUtc.UtcDateTime.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)),
                row.MagneticFieldNt.ToString("0.###", CultureInfo.InvariantCulture),
                Number(row.Sq, "0.###"),
                Csv(row.SourceFileStableId),
                Csv(row.SessionStableId),
                row.SessionOrdinal.ToString(CultureInfo.InvariantCulture),
                Csv(row.SourceFile),
                row.SourceRecord.ToString(CultureInfo.InvariantCulture),
                Csv(string.Join(";", row.QualityFlags))));
        }
    }

    private static void WriteIntegratedQualityMarkdown(
        string path,
        FieldMagFolderAnalysis analysis)
    {
        IReadOnlyList<FieldMagDateSummary> dates =
            BuildDateSummaries(analysis);
        DiurnalBaseValiditySummary baseValidity =
            DiurnalBaseValidityGate.Evaluate(analysis.BaseSamples);
        IReadOnlyList<FieldMagDailyCoverageSummary> coverageSummaries =
            FieldMagCoverageService.Analyze(analysis);

        string range =
            dates.Count == 0
                ? "无可识别 UTC 日期"
                : dates.Count == 1
                    ? dates[0].UtcDate.ToString(
                        "yyyy-MM-dd",
                        CultureInfo.InvariantCulture)
                    : $"{dates[0].UtcDate:yyyy-MM-dd} ~ {dates[^1].UtcDate:yyyy-MM-dd}";

        var sb =
            new StringBuilder();
        sb.AppendLine(
                "# 外业磁测完整整合数据质量报告")
            .AppendLine()
            .AppendLine(
                $"- 来源目录：`{analysis.SourceDirectory}`")
            .AppendLine(
                $"- UTC 数据范围：{range}")
            .AppendLine(
                $"- 文件：{analysis.Files.Count}（UAV Raw {analysis.UavRawFileCount}；GSM-19T Raw {analysis.BaseRawFileCount}；派生/已合并 {analysis.DerivedFileCount}）")
            .AppendLine(
                $"- UAV 完整规范记录：{analysis.UavSamples.Count}")
            .AppendLine(
                $"- GSM-19T 完整规范记录：{analysis.BaseSamples.Count}")
            .AppendLine(
                $"- GSM-19T 生产日变可用：{baseValidity.AcceptedRecords}；排除：{baseValidity.RejectedRecords}；无效 session：{baseValidity.RejectedSessions}")
            .AppendLine(
                $"- 有效 Base 中位数（诊断统计，不自动作为 Datum）：{(baseValidity.AcceptedMedianFieldNt.HasValue ? baseValidity.AcceptedMedianFieldNt.Value.ToString("0.###", CultureInfo.InvariantCulture) + " nT" : "不可用")}")
            .AppendLine(
                $"- 数据指纹：`{analysis.Fingerprint}`")
            .AppendLine()
            .AppendLine(
                "## 数据组织")
            .AppendLine()
            .AppendLine(
                "- `UAV磁测_整合.csv.gz` 与 `GSM19T_日变站_整合.csv.gz` 各覆盖当前工区扫描到的完整 canonical 数据，不再按日期拆分物理文件。")
            .AppendLine(
                "- 完整表使用 GZip 无损压缩；逐行 `SourceFile` 改为整数 `SourceId`，完整来源路径只在 `磁测整合_manifest.json` 中保存一次。")
            .AppendLine(
                "- 没有时间/空间降采样，也没有降低磁值或坐标的写出精度。")
            .AppendLine(
                "- MagOps 原生日变在计算内部按绝对 **UTC Date** 分组；不同 UTC 日期之间禁止插值。")
            .AppendLine(
                "- UTC+8 只用于外业业务作业日、进度和简报，不参与日变数学分组。")
            .AppendLine()
            .AppendLine(
                "## 每日 19T 生产覆盖")
            .AppendLine();

        if (coverageSummaries.Count == 0)
        {
            sb.AppendLine("没有可用于覆盖检查的 UAV 磁测记录。");
        }
        else
        {
            foreach (FieldMagDailyCoverageSummary coverage in coverageSummaries.OrderBy(x => x.BeijingDate))
            {
                string reasons = coverage.Reasons.Count == 0
                    ? "—"
                    : string.Join(" / ", coverage.Reasons);
                sb.AppendLine(
                    $"- {coverage.BeijingDate:yyyy-MM-dd}（北京时间）：{coverage.CoveragePercent:0.0}%；UAV {coverage.CoveredUavRecords}/{coverage.UavEligibleRecords} 可生产日变；缺 {coverage.UncoveredUavRecords}；Base 可用/排除 {coverage.AcceptedBaseRecords}/{coverage.RejectedBaseRecords}；状态 {coverage.Status}；原因 {reasons}。");
            }
        }

        sb.AppendLine()
            .AppendLine(
                "## QC 提示")
            .AppendLine();

        if (analysis.QualityIssues.Count == 0)
        {
            sb.AppendLine(
                "未发现当前规则覆盖的质量问题。");
        }
        else
        {
            foreach (FieldDataQualityIssue issue in
                     analysis.QualityIssues)
            {
                sb.AppendLine(
                    $"- **{issue.Severity} / {issue.Code}** [{issue.Scope}] {issue.TimeRangeUtc} — {issue.Message}");
            }
        }

        sb.AppendLine()
            .AppendLine(
                "## 处理边界")
            .AppendLine()
            .AppendLine(
                "MagOps 保留原始资料，不覆盖原始 Rover/Base；canonical 整合用于可追溯计算和后续处理。")
            .AppendLine(
                "日变数值由 MagOps.Core.Diurnal 独立实现；GEMLink 5.4.8.1 仅用于 Golden/兼容对照，不是生产运行时依赖。")
            .AppendLine(
                "Base QC 不删除原始/canonical 记录；但生产日变会通过 Base Validity Gate 排除物理越界、非法/低 SQ、同日稳健离群、无效 session 与时间冲突记录，并拒绝超限长空档插值。");

        File.WriteAllText(
            path,
            sb.ToString(),
            new UTF8Encoding(true));
    }

    private static void WriteQualityMarkdown(string path, FieldMagFolderAnalysis analysis, DateTime operationDate)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# 外业磁测数据质量报告 — {operationDate:yyyy-MM-dd}")
            .AppendLine()
            .AppendLine($"- 来源目录：`{analysis.SourceDirectory}`")
            .AppendLine($"- 文件：{analysis.Files.Count}（UAV Raw {analysis.UavRawFileCount}；GSM-19T Raw {analysis.BaseRawFileCount}；派生/已合并 {analysis.DerivedFileCount}）")
            .AppendLine($"- UAV 规范记录：{analysis.UavSamples.Count}")
            .AppendLine($"- GSM-19T 规范记录：{analysis.BaseSamples.Count}")
            .AppendLine($"- 数据指纹：`{analysis.Fingerprint}`")
            .AppendLine()
            .AppendLine("## QC 提示")
            .AppendLine();
        if (analysis.QualityIssues.Count == 0)
        {
            sb.AppendLine("未发现当前规则覆盖的质量问题。");
        }
        else
        {
            foreach (FieldDataQualityIssue issue in analysis.QualityIssues)
                sb.AppendLine($"- **{issue.Severity} / {issue.Code}** [{issue.Scope}] {issue.TimeRangeUtc} — {issue.Message}");
        }
        sb.AppendLine()
            .AppendLine("## 处理边界")
            .AppendLine()
            .AppendLine("该旧式按日期导出仅作为兼容能力保留；正式生产主链已改为完整 UAV/GSM-19T 整合 + MagOps 原生日变。原始记录不会被覆盖。")
            .AppendLine("SQ、物理范围、同日稳健离群与 session 一致性用于生产 Base Validity Gate；60 秒内 >200 nT 跳变仍作为人工复核提示。原始记录始终保留。 ");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }

    private static string BuildGemLinkBridgeReadme(FieldMagFolderAnalysis analysis, DateTime operationDate)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# GEMLink 分段任务 — {operationDate:yyyy-MM-dd}")
            .AppendLine()
            .AppendLine("本目录用于解决旧版 GEMLink 对跨 UTC 零点/日期匹配不可靠的问题：MagOps 先把 Rover 与 Base 都转换为绝对 UTC 时间，再按 UTC 日期分组。")
            .AppendLine()
            .AppendLine("**当前用途：** MagOps 已有独立原生日变算法；本目录仅用于 GEMLink 5.4.8.1 黄金对照、兼容核查和异常人工复核。`Rover_*_MAGOPS.csv` 与 `Base_*_MAGOPS.csv` 仍是规范化核对数据，不冒充厂家专有输入格式。")
            .AppendLine()
            .AppendLine("## 任务")
            .AppendLine();
        foreach (GemLinkBridgeJob job in analysis.GemLinkJobs.OrderBy(x => x.UtcDate))
        {
            sb.AppendLine($"- **{job.UtcDate:yyyy-MM-dd} UTC** — Rover {job.RoverRecordCount}；Base {job.BaseRecordCount}；" +
                          (job.Warnings.Count == 0 ? "无当前规则风险" : string.Join("；", job.Warnings)));
        }
        sb.AppendLine()
            .AppendLine("后续黄金测试用于确认尚未静态确定的边界行为（重复 Base 时间键、范围外 Rover、NoValue、逐字节输出格式等）；正式生产主链不再依赖 GEMLink。 ");
        return sb.ToString();
    }

    public static string SensorLabel(FieldMagSensorFamily sensor) => sensor switch
    {
        FieldMagSensorFamily.UavOpticalPump => "无人机光泵",
        FieldMagSensorFamily.Gsm19T => "GSM-19T",
        FieldMagSensorFamily.Gsm19TG => "GSM-19TG（移动梯度，暂不支持读取）",
        FieldMagSensorFamily.DiurnalCorrection => "日改派生",
        _ => "未识别"
    };

    public static string RoleLabel(FieldMagDataRole role) => role switch
    {
        FieldMagDataRole.Raw => "Raw 原始",
        FieldMagDataRole.Aggregate => "Aggregate 已拼接",
        FieldMagDataRole.DiurnalSegment => "日改分段结果",
        FieldMagDataRole.DiurnalMerged => "日改合并结果",
        FieldMagDataRole.OtherDerived => "其他派生",
        _ => "未识别"
    };

    private static string FormatUtcRange(DateTimeOffset? first, DateTimeOffset? last)
    {
        if (!first.HasValue && !last.HasValue)
            return "—";
        if (first.HasValue && last.HasValue)
            return $"{FormatUtc(first)} – {FormatUtc(last)}";
        return FormatUtc(first ?? last);
    }

    private static string FormatUtc(DateTimeOffset? value) => value.HasValue
        ? value.Value.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff 'UTC'", CultureInfo.InvariantCulture)
        : "";

    private static string Number(double? value, string format) => value.HasValue
        ? value.Value.ToString(format, CultureInfo.InvariantCulture)
        : "";

    private static string Csv(string value)
    {
        string text = value ?? "";
        return text.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? "\"" + text.Replace("\"", "\"\"") + "\""
            : text;
    }

    private static string NaturalSortKey(string value)
    {
        var sb = new StringBuilder();
        foreach (Match match in Regex.Matches(value ?? "", @"\d+|\D+"))
        {
            if (long.TryParse(match.Value, out long number))
                sb.Append(number.ToString("D18", CultureInfo.InvariantCulture));
            else
                sb.Append(match.Value.ToLowerInvariant());
        }
        return sb.ToString();
    }

    private sealed record FieldMagAnalysisSnapshotDocument(
        string SchemaVersion,
        string ParserVersion,
        DateTimeOffset UpdatedAtUtc,
        string SourceSnapshotKey,
        string SourceDirectory,
        FieldMagFolderAnalysis Analysis);

    private sealed record SourceFileMetadata(
        string RelativePath,
        long Length,
        long LastWriteTimeUtcTicks);

    private sealed record FieldMagCacheIndexEntry(
        string RelativePath,
        long Length,
        long LastWriteTimeUtcTicks,
        string CacheFile,
        FieldMagSensorFamily SensorFamily,
        FieldMagDataRole DataRole,
        int RecordCount,
        DateTimeOffset? FirstTimeUtc,
        DateTimeOffset? LastTimeUtc,
        IReadOnlyList<string> BeijingDates);

    private sealed record FieldMagCacheIndexDocument(
        string SchemaVersion,
        string ParserVersion,
        DateTimeOffset UpdatedAtUtc,
        IReadOnlyList<FieldMagCacheIndexEntry> Files);

    private sealed record CachedParsedMagFile(
        string SchemaVersion,
        string ParserVersion,
        string RelativePath,
        FieldMagSensorFamily SensorFamily,
        FieldMagDataRole DataRole,
        IReadOnlyList<UavMagCanonicalSample> UavRows,
        IReadOnlyList<Gsm19TCanonicalSample> BaseRows,
        DateTimeOffset? FirstTimeUtc,
        DateTimeOffset? LastTimeUtc,
        string Evidence,
        IReadOnlyList<string> Warnings,
        int? ExplicitRecordCount,
        string SourceFileStableId,
        IReadOnlyList<GsmSession> GsmSessions,
        string SourceContentSha256);

    private sealed record ParsedMagFile(
        string Path,
        FieldMagSensorFamily SensorFamily,
        FieldMagDataRole DataRole,
        IReadOnlyList<UavMagCanonicalSample> UavRows,
        IReadOnlyList<Gsm19TCanonicalSample> BaseRows,
        DateTimeOffset? FirstTimeUtc,
        DateTimeOffset? LastTimeUtc,
        string Evidence,
        IReadOnlyList<string> Warnings,
        int? ExplicitRecordCount = null,
        string SourceFileStableId = "",
        IReadOnlyList<GsmSession>? GsmSessions = null,
        string SourceContentSha256 = "")
    {
        public IReadOnlyList<GsmSession> Sessions => GsmSessions ?? [];
        public int RecordCount => ExplicitRecordCount ?? (UavRows.Count > 0 ? UavRows.Count : BaseRows.Count > 0 ? BaseRows.Count : 0);
    }

    private sealed class HeaderMap
    {
        public char Delimiter { get; init; } = ',';
        public int DateIndex { get; init; } = -1;
        public int TimeIndex { get; init; } = -1;
        public int LongitudeIndex { get; init; } = -1;
        public int LatitudeIndex { get; init; } = -1;
        public int AltitudeIndex { get; init; } = -1;
        public int LaserAltitudeIndex { get; init; } = -1;
        public int MagIndex { get; init; } = -1;
        public int SignalIndex { get; init; } = -1;
        public int GpsStatusIndex { get; init; } = -1;
        public int SatellitesIndex { get; init; } = -1;
        public int SensorStatusIndex { get; init; } = -1;

        public static HeaderMap From(string? line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return new HeaderMap { Delimiter = ',' };
            char delimiter = DetectDelimiter(line);
            string[] headers = delimiter == ' '
                ? line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : line.Split(delimiter, StringSplitOptions.TrimEntries).Select(x => x.Trim().Trim('"')).ToArray();
            return new HeaderMap
            {
                Delimiter = delimiter,
                DateIndex = FindHeader(headers, ["utcdate", "utcdata", "date", "日期"]),
                TimeIndex = FindHeader(headers, ["utctime", "time", "时间"]),
                LongitudeIndex = FindHeader(headers, ["longitude", "lon", "lng", "经度"]),
                LatitudeIndex = FindHeader(headers, ["latitude", "lat", "纬度"]),
                AltitudeIndex = FindHeader(headers, ["altitude", "gpsaltitude", "height", "海拔"]),
                LaserAltitudeIndex = FindHeader(headers, ["laseraltimeter", "laseraltitude", "laserheight", "激光高度"]),
                MagIndex = FindHeader(headers, ["mag", "magnt", "magneticfield", "totalfield", "field"]),
                SignalIndex = FindHeader(headers, ["signal", "signalstrength"]),
                GpsStatusIndex = FindHeader(headers, ["gpsstatus", "gpsfix", "fix"]),
                SatellitesIndex = FindHeader(headers, ["satellite", "satellites", "satnum", "numsats"]),
                SensorStatusIndex = FindHeader(headers, ["sensorstatus", "magstatus", "status"])
            };
        }

        private static int FindHeader(IReadOnlyList<string> headers, IReadOnlyList<string> candidates)
        {
            for (int i = 0; i < headers.Count; i++)
            {
                string normalized = NormalizeToken(headers[i]);
                foreach (string candidate in candidates)
                {
                    string wanted = NormalizeToken(candidate);
                    if (normalized.Equals(wanted, StringComparison.OrdinalIgnoreCase))
                        return i;
                }
            }
            for (int i = 0; i < headers.Count; i++)
            {
                string normalized = NormalizeToken(headers[i]);
                foreach (string candidate in candidates)
                {
                    string wanted = NormalizeToken(candidate);
                    if (normalized.Contains(wanted, StringComparison.OrdinalIgnoreCase))
                        return i;
                }
            }
            return -1;
        }

        private static char DetectDelimiter(string line)
        {
            var counts = new Dictionary<char, int>
            {
                [','] = line.Count(c => c == ','),
                ['\t'] = line.Count(c => c == '\t'),
                [';'] = line.Count(c => c == ';')
            };
            KeyValuePair<char, int> best = counts.OrderByDescending(x => x.Value).First();
            return best.Value > 0 ? best.Key : ' ';
        }
    }
}
