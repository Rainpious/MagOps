using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MagOps.Core.Domain;
using MagOps.Infrastructure.Storage;

namespace MagOps.Infrastructure.Services;

public sealed record DjiFlightRecordSummary(
    int FormatVersion,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    double DurationSeconds,
    double TotalDistanceKm,
    double? StartLongitude,
    double? StartLatitude,
    double MaxHeightM,
    double MaxHorizontalSpeedMps,
    double MaxVerticalSpeedMps,
    string AircraftName,
    string AppVersion,
    string FilenameTimeText);

public sealed record DjiFlightImportItemResult(
    string SourcePath,
    string DestinationPath,
    bool Imported,
    bool Duplicate,
    string Message,
    DjiFlightSessionRecord? Session);

public sealed record DjiFlightImportBatchResult(
    string BatchId,
    string DestinationDirectory,
    IReadOnlyList<DjiFlightImportItemResult> Items)
{
    public int ImportedCount => Items.Count(x => x.Imported);
    public int DuplicateCount => Items.Count(x => x.Duplicate);
    public int RejectedCount => Items.Count(x => !x.Imported && !x.Duplicate);
}

public sealed record DjiMcdatRegistrationResult(
    IReadOnlyList<DjiMcdatCatalogRecord> Registered,
    int DuplicateCount);

public sealed record DjiDailyFlightSummary(
    DateTime BeijingDate,
    IReadOnlyList<DjiFlightSessionRecord> Sessions,
    int MagneticMatchedFlightCount,
    IReadOnlyDictionary<string, int> MagneticSamplesBySession)
{
    public int FlightCount => Sessions.Count;
    public string ProjectTimeZoneId { get; init; } = "Asia/Shanghai";
    public double TotalDistanceKm => Sessions.Sum(x => x.TotalDistanceKm);
    public double TotalDurationSeconds => Sessions.Sum(x => x.DurationSeconds);

    public string BeijingTimeRange
    {
        get
        {
            if (Sessions.Count == 0)
                return "";

            List<DateTimeOffset> starts = Sessions
                .Select(x => ParseUtc(x.InternalStartUtc))
                .Where(x => x.HasValue)
                .Select(x =>
                    ProjectTime.Display(x!.Value, TimeZoneInfo.FindSystemTimeZoneById(ProjectTimeZoneId)))
                .OrderBy(x => x)
                .ToList();
            List<DateTimeOffset> ends = Sessions
                .Select(x => ParseUtc(x.InternalEndUtc))
                .Where(x => x.HasValue)
                .Select(x =>
                    ProjectTime.Display(x!.Value, TimeZoneInfo.FindSystemTimeZoneById(ProjectTimeZoneId)))
                .OrderBy(x => x)
                .ToList();

            if (starts.Count == 0 ||
                ends.Count == 0)
            {
                return "";
            }

            return $"{starts[0]:HH:mm}–{ends[^1]:HH:mm}";
        }
    }

    private static DateTimeOffset? ParseUtc(string value) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out DateTimeOffset parsed)
            ? parsed.ToUniversalTime()
            : null;
}

public sealed class DjiPilotFlightRecordService
{
    public const string ParserVersion = "dji-pilot2-summary-1";
    public const int MagneticMatchPaddingSeconds = 120;
    public const int MagneticMatchMinSamples = 10;

    private const ulong XorMagic = 0x123456789ABCDEF0UL;
    private const ulong JonesPolynomialReflected = 0x95AC9329AC4BC9B5UL;

    private static readonly Regex FlightRecordNameRegex = new(
        @"(?i)^DJIFlightRecord_(?<date>\d{4}-\d{2}-\d{2})_\[(?<hour>\d{2})-(?<minute>\d{2})-(?<second>\d{2})\]\.txt$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex McdatNameRegex = new(
        @"(?i)(?<date>\d{4}-\d{2}-\d{2})_(?<hour>\d{2})-(?<minute>\d{2})-(?<second>\d{2})_FLY\d+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly JsonStore _store;

    public DjiPilotFlightRecordService(JsonStore store)
    {
        _store = store;
    }

    public List<DjiFlightSessionRecord> ListSessions(string root) =>
        _store.Read(
            ProjectPaths.Json(root, "dji_flight_sessions.json"),
            new List<DjiFlightSessionRecord>());

    public List<DjiMcdatCatalogRecord> ListMcdat(string root) =>
        _store.Read(
            ProjectPaths.Json(root, "dji_mcdat_catalog.json"),
            new List<DjiMcdatCatalogRecord>());

    public DjiFlightRecordSummary ParseSummary(string path)
    {
        string full = Path.GetFullPath(path);
        if (!File.Exists(full))
            throw new FileNotFoundException("DJI FlightRecord 文件不存在。", full);

        using FileStream stream = File.OpenRead(full);
        Span<byte> prefix = stackalloc byte[100];
        stream.ReadExactly(prefix);

        ulong recordsOffset =
            BinaryPrimitives.ReadUInt64LittleEndian(prefix[..8]);
        ushort detailLength =
            BinaryPrimitives.ReadUInt16LittleEndian(prefix.Slice(8, 2));
        int formatVersion = prefix[10];

        if (recordsOffset <= 103 ||
            recordsOffset > (ulong)stream.Length)
        {
            throw new InvalidDataException(
                $"DJI records offset 异常：{recordsOffset} / 文件 {stream.Length} bytes。");
        }
        if (detailLength < 380)
        {
            throw new InvalidDataException(
                $"DJI Details 声明长度异常：{detailLength}。");
        }

        if (formatVersion != 13 &&
            formatVersion != 14)
        {
            throw new InvalidDataException(
                $"当前仅验证 DJI FlightRecord v13/v14 摘要；文件版本为 v{formatVersion}。");
        }

        stream.Position = 100;
        Span<byte> auxHeader = stackalloc byte[3];
        stream.ReadExactly(auxHeader);
        if (auxHeader[0] != 0)
            throw new InvalidDataException("DJI v13/v14 首个 Auxiliary block 不是 Info。");

        ushort auxSize =
            BinaryPrimitives.ReadUInt16LittleEndian(auxHeader.Slice(1, 2));
        if (auxSize < 8 || auxSize > 65500)
            throw new InvalidDataException($"DJI Auxiliary Info 长度异常：{auxSize}。");

        byte[] auxStream = new byte[auxSize];
        stream.ReadExactly(auxStream);
        byte firstByte = auxStream[0];

        Span<byte> magicData = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(
            magicData,
            unchecked(XorMagic * firstByte));
        ulong keyValue =
            Crc64Jones(
                firstByte,
                magicData);

        Span<byte> key = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(key, keyValue);

        byte[] decoded = new byte[auxSize - 1];
        for (int i = 1; i < auxStream.Length; i++)
        {
            decoded[i - 1] =
                (byte)(auxStream[i] ^ key[(i - 1) % 8]);
        }

        if (decoded.Length < 3)
            throw new InvalidDataException("DJI Auxiliary Info 解码结果过短。");

        ushort infoLength =
            BinaryPrimitives.ReadUInt16LittleEndian(
                decoded.AsSpan(1, 2));
        if (infoLength < 380 ||
            3 + infoLength > decoded.Length)
        {
            throw new InvalidDataException(
                $"DJI Details 长度异常：{infoLength}。");
        }

        ReadOnlySpan<byte> info =
            decoded.AsSpan(3, infoLength);

        long startUnixMs =
            BinaryPrimitives.ReadInt64LittleEndian(
                info.Slice(91, 8));
        DateTimeOffset startUtc;
        try
        {
            startUtc =
                DateTimeOffset.FromUnixTimeMilliseconds(
                    startUnixMs);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new InvalidDataException(
                "DJI Details 的 start_time 超出有效 Unix 时间范围。",
                ex);
        }

        double longitude =
            ReadDoubleLittleEndian(info.Slice(99, 8));
        double latitude =
            ReadDoubleLittleEndian(info.Slice(107, 8));
        double totalDistanceKm =
            ReadSingleLittleEndian(info.Slice(115, 4));
        int totalTimeMs =
            BinaryPrimitives.ReadInt32LittleEndian(
                info.Slice(119, 4));
        double maxHeightM =
            ReadSingleLittleEndian(info.Slice(123, 4));
        double maxHorizontalSpeedMps =
            ReadSingleLittleEndian(info.Slice(127, 4));
        double maxVerticalSpeedMps =
            ReadSingleLittleEndian(info.Slice(131, 4));
        string aircraftName =
            ReadFixedString(info.Slice(280, 32));
        string appVersion =
            $"{info[377]}.{info[378]}.{info[379]}";

        if (totalTimeMs < 0 ||
            totalTimeMs > 24 * 60 * 60 * 1000)
        {
            throw new InvalidDataException(
                $"DJI 飞行时长异常：{totalTimeMs} ms。");
        }
        if (!double.IsFinite(totalDistanceKm) ||
            totalDistanceKm < 0d ||
            totalDistanceKm > 10000d)
        {
            throw new InvalidDataException(
                $"DJI 摘要飞行距离异常：{totalDistanceKm}。");
        }

        double? validLongitude =
            double.IsFinite(longitude) &&
            longitude is >= -180d and <= 180d
                ? longitude
                : null;
        double? validLatitude =
            double.IsFinite(latitude) &&
            latitude is >= -90d and <= 90d
                ? latitude
                : null;
        if (validLongitude == 0d &&
            validLatitude == 0d)
        {
            validLongitude = null;
            validLatitude = null;
        }

        DateTimeOffset endUtc =
            startUtc.AddMilliseconds(totalTimeMs);

        return new DjiFlightRecordSummary(
            formatVersion,
            startUtc,
            endUtc,
            totalTimeMs / 1000d,
            totalDistanceKm,
            validLongitude,
            validLatitude,
            FiniteOrZero(maxHeightM),
            FiniteOrZero(maxHorizontalSpeedMps),
            FiniteOrZero(maxVerticalSpeedMps),
            aircraftName,
            appVersion,
            FilenameTimeText(Path.GetFileName(full)));
    }

    public DjiFlightImportBatchResult ImportFolder(
        string root,
        string workareaPath,
        string destinationRoot,
        string sourceFolder)
    {
        string folder = Path.GetFullPath(sourceFolder);
        if (!Directory.Exists(folder))
            throw new DirectoryNotFoundException(
                $"DJI FlightRecord 目录不存在：{folder}");

        List<string> files = Directory
            .EnumerateFiles(
                folder,
                "DJIFlightRecord_*.txt",
                SearchOption.AllDirectories)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return ImportFiles(
            root,
            workareaPath,
            destinationRoot,
            files);
    }

    public DjiFlightImportBatchResult ImportFiles(
        string root,
        string workareaPath,
        string destinationRoot,
        IEnumerable<string> sourceFiles)
    {
        string projectRoot = Path.GetFullPath(root);
        string workarea = NormalizeWorkarea(workareaPath);
        if (workarea.Length == 0)
            throw new ArgumentException("导入 DJI 飞行记录前必须选择当前工区。");

        List<string> files = sourceFiles
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(Path.GetFullPath)
            .Where(File.Exists)
            .Where(x =>
                FlightRecordNameRegex.IsMatch(
                    Path.GetFileName(x)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (files.Count == 0)
        {
            throw new InvalidOperationException(
                "所选目录未发现 DJIFlightRecord_*.txt。");
        }

        string batchId =
            DateTimeOffset.Now.ToString(
                "yyyyMMdd_HHmmssfff",
                CultureInfo.InvariantCulture);
        string destinationDirectory =
            Path.Combine(
                Path.GetFullPath(destinationRoot),
                "01_导入原始数据",
                "DJI_Pilot2",
                batchId);
        Directory.CreateDirectory(destinationDirectory);

        List<DjiFlightSessionRecord> sessions =
            ListSessions(projectRoot);
        var items =
            new List<DjiFlightImportItemResult>();

        foreach (string source in files)
        {
            string sha = Sha256(source);
            DjiFlightSessionRecord? duplicate =
                sessions.FirstOrDefault(x =>
                    x.WorkareaPath.Equals(
                        workarea,
                        StringComparison.OrdinalIgnoreCase) &&
                    x.SourceSha256.Equals(
                        sha,
                        StringComparison.OrdinalIgnoreCase));
            if (duplicate is not null)
            {
                items.Add(
                    new DjiFlightImportItemResult(
                        source,
                        ResolveStoredPath(
                            projectRoot,
                            duplicate.SourceFile),
                        Imported: false,
                        Duplicate: true,
                        "SHA-256 已存在，未重复入库",
                        duplicate));
                continue;
            }

            DjiFlightRecordSummary summary;
            try
            {
                summary = ParseSummary(source);
            }
            catch (Exception ex)
            {
                items.Add(
                    new DjiFlightImportItemResult(
                        source,
                        "",
                        Imported: false,
                        Duplicate: false,
                        ex.Message,
                        null));
                continue;
            }

            string target =
                UniquePath(
                    destinationDirectory,
                    Path.GetFileName(source),
                    sha);
            File.Copy(
                source,
                target,
                overwrite: false);
            string targetSha =
                Sha256(target);
            if (!targetSha.Equals(
                    sha,
                    StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(target);
                throw new IOException(
                    $"DJI FlightRecord 复制后 SHA-256 校验失败：{Path.GetFileName(source)}");
            }

            string beijingDate =
                summary.StartUtc
                    .ToOffset(
                        FieldMagDataService.BeijingUtcOffset)
                    .ToString(
                        "yyyy-MM-dd",
                        CultureInfo.InvariantCulture);
            string importedAt =
                DateTimeOffset.UtcNow.ToString("O");
            var session =
                new DjiFlightSessionRecord
                {
                    SessionId =
                        "DJIFLIGHT-" +
                        sha[..16].ToUpperInvariant(),
                    WorkareaPath = workarea,
                    SourceFile =
                        ProjectRelativeOrAbsolute(
                            projectRoot,
                            target),
                    SourceFileName =
                        Path.GetFileName(source),
                    SourceSha256 = targetSha,
                    FormatVersion =
                        summary.FormatVersion,
                    InternalStartUtc =
                        summary.StartUtc
                            .ToUniversalTime()
                            .ToString("O"),
                    InternalEndUtc =
                        summary.EndUtc
                            .ToUniversalTime()
                            .ToString("O"),
                    BeijingDate = beijingDate,
                    DurationSeconds =
                        summary.DurationSeconds,
                    TotalDistanceKm =
                        summary.TotalDistanceKm,
                    StartLongitude =
                        summary.StartLongitude,
                    StartLatitude =
                        summary.StartLatitude,
                    MaxHeightM =
                        summary.MaxHeightM,
                    MaxHorizontalSpeedMps =
                        summary.MaxHorizontalSpeedMps,
                    MaxVerticalSpeedMps =
                        summary.MaxVerticalSpeedMps,
                    AircraftName =
                        summary.AircraftName,
                    AppVersion =
                        summary.AppVersion,
                    FilenameTimeText =
                        summary.FilenameTimeText,
                    ParserVersion =
                        ParserVersion,
                    ImportBatchId =
                        batchId,
                    ImportedAt =
                        importedAt
                };
            sessions.Add(session);
            items.Add(
                new DjiFlightImportItemResult(
                    source,
                    target,
                    Imported: true,
                    Duplicate: false,
                    "已解析 Details、复制并通过 SHA-256 校验",
                    session));
        }

        _store.Write(
            ProjectPaths.Json(
                projectRoot,
                "dji_flight_sessions.json"),
            sessions
                .OrderBy(x => x.InternalStartUtc, StringComparer.Ordinal)
                .ToList());
        _store.AppendJsonLine(
            ProjectPaths.DataDirectory(projectRoot),
            "dji_flight_records_imported",
            new
            {
                batch_id = batchId,
                workarea_path = workarea,
                imported = items.Count(x => x.Imported),
                duplicates = items.Count(x => x.Duplicate),
                rejected = items.Count(x => !x.Imported && !x.Duplicate)
            });

        return new DjiFlightImportBatchResult(
            batchId,
            destinationDirectory,
            items);
    }

    public DjiMcdatRegistrationResult RegisterMcdatFiles(
        string root,
        string workareaPath,
        IEnumerable<string> sourceFiles)
    {
        string projectRoot = Path.GetFullPath(root);
        string workarea = NormalizeWorkarea(workareaPath);
        if (workarea.Length == 0)
            throw new ArgumentException("登记 MCDat 前必须选择当前工区。");

        List<DjiMcdatCatalogRecord> rows =
            ListMcdat(projectRoot);
        var registered =
            new List<DjiMcdatCatalogRecord>();
        int duplicates = 0;

        foreach (string source in sourceFiles
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(Path.GetFullPath)
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string sha = Sha256(source);
            if (rows.Any(x =>
                    x.WorkareaPath.Equals(
                        workarea,
                        StringComparison.OrdinalIgnoreCase) &&
                    x.Sha256.Equals(
                        sha,
                        StringComparison.OrdinalIgnoreCase)))
            {
                duplicates++;
                continue;
            }

            FileInfo info =
                new(source);
            var row =
                new DjiMcdatCatalogRecord
                {
                    McdatId =
                        "DJIMCDAT-" +
                        sha[..16].ToUpperInvariant(),
                    WorkareaPath =
                        workarea,
                    SourceFileName =
                        info.Name,
                    SourcePathHint =
                        info.FullName,
                    Sha256 =
                        sha,
                    SizeBytes =
                        info.Length,
                    FilenameTimeText =
                        McdatFilenameTimeText(
                            info.Name),
                    LinkedSessionId =
                        "",
                    RegisteredAt =
                        DateTimeOffset.UtcNow.ToString("O")
                };
            rows.Add(row);
            registered.Add(row);
        }

        _store.Write(
            ProjectPaths.Json(
                projectRoot,
                "dji_mcdat_catalog.json"),
            rows);
        _store.AppendJsonLine(
            ProjectPaths.DataDirectory(projectRoot),
            "dji_mcdat_registered",
            new
            {
                workarea_path = workarea,
                registered = registered.Count,
                duplicates
            });

        return new DjiMcdatRegistrationResult(
            registered,
            duplicates);
    }

    public DjiDailyFlightSummary BuildDailySummary(
        string root,
        string workareaPath,
        DateTime beijingDate,
        FieldMagFolderAnalysis? dailyMagAnalysis)
    {
        TimeZoneInfo timeZone = TimeZoneInfo.FindSystemTimeZoneById(dailyMagAnalysis?.ProjectTimeZoneId ?? "Asia/Shanghai");
        string workarea =
            NormalizeWorkarea(workareaPath);
        string dateText =
            beijingDate.Date.ToString(
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture);

        List<DjiFlightSessionRecord> sessions =
            ListSessions(root)
                .Where(x =>
                    x.WorkareaPath.Equals(
                        workarea,
                        StringComparison.OrdinalIgnoreCase) &&
                    (DateTimeOffset.TryParse(x.InternalStartUtc, CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out DateTimeOffset stamp) &&
                        ProjectTime.Date(stamp, timeZone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) == dateText))
                .OrderBy(x =>
                    x.InternalStartUtc,
                    StringComparer.Ordinal)
                .ToList();

        DateTimeOffset[] timestamps =
            dailyMagAnalysis?.UavSamples
                .Where(x =>
                    !x.SensorStatus.Equals(
                        "F",
                        StringComparison.OrdinalIgnoreCase))
                .Select(x => x.TimestampUtc)
                .OrderBy(x => x)
                .ToArray()
            ?? [];

        var matchCounts =
            new Dictionary<string, int>(
                StringComparer.OrdinalIgnoreCase);
        int matched = 0;

        foreach (DjiFlightSessionRecord session in sessions)
        {
            int count = 0;
            if (TryParseUtc(
                    session.InternalStartUtc,
                    out DateTimeOffset startUtc) &&
                TryParseUtc(
                    session.InternalEndUtc,
                    out DateTimeOffset endUtc) &&
                timestamps.Length > 0)
            {
                DateTimeOffset lower =
                    startUtc.AddSeconds(
                        -MagneticMatchPaddingSeconds);
                DateTimeOffset upper =
                    endUtc.AddSeconds(
                        MagneticMatchPaddingSeconds);
                int first =
                    LowerBound(
                        timestamps,
                        lower);
                int afterLast =
                    UpperBound(
                        timestamps,
                        upper);
                count =
                    Math.Max(
                        0,
                        afterLast - first);
            }

            matchCounts[session.SessionId] =
                count;
            if (count >=
                MagneticMatchMinSamples)
            {
                matched++;
            }
        }

        return new DjiDailyFlightSummary(
            beijingDate.Date,
            sessions,
            matched,
            matchCounts) { ProjectTimeZoneId = timeZone.Id };
    }

    public static string ResolveStoredPath(
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

    private static bool TryParseUtc(
        string value,
        out DateTimeOffset result)
    {
        if (DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out result))
        {
            result =
                result.ToUniversalTime();
            return true;
        }

        result = default;
        return false;
    }

    private static int LowerBound(
        IReadOnlyList<DateTimeOffset> values,
        DateTimeOffset target)
    {
        int lo = 0;
        int hi = values.Count;
        while (lo < hi)
        {
            int mid =
                lo + (hi - lo) / 2;
            if (values[mid] < target)
                lo = mid + 1;
            else
                hi = mid;
        }
        return lo;
    }

    private static int UpperBound(
        IReadOnlyList<DateTimeOffset> values,
        DateTimeOffset target)
    {
        int lo = 0;
        int hi = values.Count;
        while (lo < hi)
        {
            int mid =
                lo + (hi - lo) / 2;
            if (values[mid] <= target)
                lo = mid + 1;
            else
                hi = mid;
        }
        return lo;
    }

    private static string NormalizeWorkarea(string value) =>
        (value ?? "")
            .Replace('\\', '/')
            .Trim('/');

    private static string FilenameTimeText(
        string fileName)
    {
        Match match =
            FlightRecordNameRegex.Match(
                fileName ?? "");
        if (!match.Success)
            return "";

        return
            $"{match.Groups["date"].Value} " +
            $"{match.Groups["hour"].Value}:" +
            $"{match.Groups["minute"].Value}:" +
            $"{match.Groups["second"].Value}";
    }

    private static string McdatFilenameTimeText(
        string fileName)
    {
        Match match =
            McdatNameRegex.Match(
                fileName ?? "");
        if (!match.Success)
            return "";

        return
            $"{match.Groups["date"].Value} " +
            $"{match.Groups["hour"].Value}:" +
            $"{match.Groups["minute"].Value}:" +
            $"{match.Groups["second"].Value}";
    }

    private static string ProjectRelativeOrAbsolute(
        string projectRoot,
        string path)
    {
        string root =
            Path.GetFullPath(projectRoot)
                .TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
        string full =
            Path.GetFullPath(path);
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
        string candidate =
            Path.Combine(
                directory,
                Path.GetFileName(fileName));
        if (!File.Exists(candidate))
            return candidate;

        string stem =
            Path.GetFileNameWithoutExtension(
                fileName);
        string ext =
            Path.GetExtension(fileName);
        for (int i = 0; i < 1000; i++)
        {
            string suffix =
                i == 0
                    ? sha256[..8]
                    : $"{sha256[..8]}_{i:D2}";
            candidate =
                Path.Combine(
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

    private static ulong Crc64Jones(
        ulong crc,
        ReadOnlySpan<byte> data)
    {
        foreach (byte value in data)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
            {
                crc =
                    (crc >> 1) ^
                    ((crc & 1UL) != 0
                        ? JonesPolynomialReflected
                        : 0UL);
            }
        }

        return crc;
    }

    private static double ReadDoubleLittleEndian(
        ReadOnlySpan<byte> span) =>
        BitConverter.Int64BitsToDouble(
            BinaryPrimitives.ReadInt64LittleEndian(span));

    private static float ReadSingleLittleEndian(
        ReadOnlySpan<byte> span) =>
        BitConverter.Int32BitsToSingle(
            BinaryPrimitives.ReadInt32LittleEndian(span));

    private static string ReadFixedString(
        ReadOnlySpan<byte> span)
    {
        int zero =
            span.IndexOf((byte)0);
        if (zero >= 0)
            span = span[..zero];
        return Encoding.UTF8
            .GetString(span)
            .Trim();
    }

    private static double FiniteOrZero(
        double value) =>
        double.IsFinite(value)
            ? value
            : 0d;
}
