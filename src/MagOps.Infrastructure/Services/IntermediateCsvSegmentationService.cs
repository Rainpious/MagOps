using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.VisualBasic.FileIO;

namespace MagOps.Infrastructure.Services;

public sealed record IntermediateCsvRow(int Record, string Raw, string Source, string? Error);

/// <summary>A spatial-only snapshot. No magnetic or altitude channel is interpreted.</summary>
public sealed record IntermediateCsvSnapshot(string Path, string Sha256, int CodePage, char Delimiter,
    string Header, string[] Columns, IReadOnlyDictionary<string, int> Mapping,
    IReadOnlyList<IntermediateCsvRow> Rows, FieldMagFolderAnalysis Analysis,
    IReadOnlyList<MissionTimeWindow> Windows, int TimeReversals, int CarriedClocks)
{
    public int InvalidRecords => Rows.Count(r => r.Error is not null);
}

public static class IntermediateCsvSegmentationService
{
    public const string Version = "intermediate-spatial-csv-4";
    public const string TimeBasis = "SOURCE_CLOCK_UNSPECIFIED";
    public static readonly TimeZoneInfo SourceClock = TimeZoneInfo.CreateCustomTimeZone(
        TimeBasis, TimeSpan.Zero, "源文件时间（时区未确认）", "源文件时间");
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    public static readonly string[] AddedColumns = ["MagOps_LineName", "MagOps_LineOrder", "MagOps_LinePass",
        "MagOps_TaskId", "MagOps_SourceSha256", "MagOps_SourceRecord", "MagOps_SelectionStatus"];

    public static string BusinessHeader(IntermediateCsvSnapshot input)
    {
        // Reserve current segmentation identifiers; keep source channels separately.
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Line", "LineOrder" };
        return string.Join(input.Delimiter, input.Columns.Select((column, index) =>
        {
            string name = column.Trim().ToLowerInvariant() switch
            {
                "日期" or "date" or "dateutc" => "Date",
                "时间" or "time" or "timeutc" => "Time",
                "经度" or "longitude" or "lon" => "Longitude",
                "纬度" or "latitude" or "lat" => "Latitude",
                "x" or "平面坐标一" => "X", "y" or "平面坐标二" => "Y",
                "dt" or "差值" => "dt", "高程" => "Altitude",
                "line" or "线号" => "SourceLine",
                "lineorder" or "测线顺序" => "SourceLineOrder",
                "原始观测" => "MagRaw", "机动补偿后结果" => "MagComp",
                "日改后结果" => "MagDiurnal", "正常场改正值" => "NormalCorrection",
                "噪声" => "Noise", "改正后总场" => "MagCorrected", "备注" => "Remarks",
                _ => column.Trim()
            };
            // ASCII channel identifiers avoid the Chinese-header import failure reported in Oasis.
            // Unknown business semantics stay in the manifest instead of being guessed here.
            if (string.IsNullOrWhiteSpace(name) || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
                name = "Field_" + (index + 1);
            if (!char.IsAsciiLetter(name[0])) name = "Field_" + name;
            string stem = name;
            int suffix = 2;
            while (!used.Add(name)) name = stem + "_" + suffix++;
            return Quote(name, input.Delimiter);
        }));
    }

    public static string EnsureWorkspaceDirectory(string root, IntermediateCsvSnapshot input)
    {
        string stem = System.IO.Path.GetFileNameWithoutExtension(input.Path);
        stem = new string(stem.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(stem)) stem = "中间成果";
        if (stem.Length > 80) stem = stem[..80];
        if (System.Text.RegularExpressions.Regex.IsMatch(stem.Split('.')[0], @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase)) stem = "CSV_" + stem;
        // Ordinarily use the CSV name alone. A same-name file with changed content gets a suffix.
        foreach (string name in new[] { stem, stem + "__" + input.Sha256[..12], stem + "__" + input.Sha256 })
        {
            string directory = System.IO.Path.Combine(root, name);
            string identity = System.IO.Path.Combine(directory, "来源身份.sha256");
            if (Directory.Exists(directory))
            {
                if (File.Exists(identity) && File.ReadAllText(identity).Trim() == input.Sha256) return directory;
                continue;
            }
            Directory.CreateDirectory(directory);
            File.WriteAllText(identity, input.Sha256, Encoding.ASCII);
            return directory;
        }
        throw new InvalidDataException("同名来源目录身份冲突，请检查中间成果目录。");
    }

    public static IntermediateCsvSnapshot Read(string path)
    {
        path = System.IO.Path.GetFullPath(path);
        // One immutable byte snapshot prevents hashing one version and parsing another.
        byte[] bytes = File.ReadAllBytes(path);
        string hash = Convert.ToHexString(SHA256.HashData(bytes));
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding encoding;
        string text;
        try
        {
            encoding = new UTF8Encoding(false, true);
            text = encoding.GetString(bytes).TrimStart('\uFEFF');
        }
        catch (DecoderFallbackException)
        {
            encoding = Encoding.GetEncoding(936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            text = encoding.GetString(bytes);
        }
        using var records = Records(text).GetEnumerator();
        if (!records.MoveNext()) throw new InvalidDataException("CSV 为空。");
        string header = records.Current;
        char delimiter = DetectDelimiter(header);
        string[] columns = Fields(header, delimiter);
        if (columns.Distinct(StringComparer.OrdinalIgnoreCase).Count() != columns.Length)
            throw new InvalidDataException("CSV 表头有重复列名，请先区分列名。");
        if (columns.Any(c => AddedColumns.Contains(c, StringComparer.OrdinalIgnoreCase) || c == "MagOps_ExclusionReason"))
            throw new InvalidDataException("该文件已有 MagOps 分线字段，请选择原中间成果文件以恢复同一核验会话。");
        int Find(params string[] names)
        {
            var indices = columns.Select((c, i) => (c, i)).Where(x => names.Contains(x.c, StringComparer.OrdinalIgnoreCase)).ToArray();
            return indices.Length == 1 ? indices[0].i : throw new InvalidDataException("字段缺失或有歧义：" + string.Join("/", names));
        }
        var map = new Dictionary<string, int>
        {
            ["Date"] = Find("日期", "Date", "DateUtc"), ["Time"] = Find("时间", "Time", "TimeUtc"),
            ["Longitude"] = Find("经度", "Longitude", "Lon"), ["Latitude"] = Find("纬度", "Latitude", "Lat")
        };
        var rows = new List<IntermediateCsvRow>();
        var samples = new List<UavMagCanonicalSample>();
        var windows = new List<MissionTimeWindow>();
        DateTimeOffset? previous = null;
        var run = new List<UavMagCanonicalSample>();
        int runIndex = 0, reversals = 0, carriedClocks = 0;
        void FinishRun()
        {
            if (run.Count == 0) return;
            // A singleton/equal-time run stays visible for review; the matcher requires a positive window duration.
            var end = run[^1].TimestampUtc > run[0].TimestampUtc ? run[^1].TimestampUtc : run[^1].TimestampUtc.AddTicks(1);
            windows.Add(new MissionTimeWindow($"csv-run-{++runIndex:D5}", "中间成果空间分线", run[0].TimestampUtc, end)
            { SourceFiles = [run[0].SourceFile], TaskScopeSource = "AUTO" });
            run.Clear();
        }
        int record = 0;
        bool hasRow = records.MoveNext();
        while (hasRow)
        {
            record++;
            string raw = records.Current;
            bool hasNext = records.MoveNext();
            string? nextRaw = hasNext ? records.Current : null;
            string source = $"csv:{hash}/run-{runIndex + 1:D5}";
            try
            {
                string[] values = Fields(raw, delimiter);
                if (values.Length != columns.Length) throw new FormatException("列数与表头不一致");
                DateTimeOffset time;
                try { time = ParseSourceTime(values[map["Date"]], values[map["Time"]]); }
                catch (FormatException) when (previous is not null && nextRaw is not null)
                {
                    time = CarryClock(values[map["Date"]], values[map["Time"]]);
                    string[] nextValues = Fields(nextRaw, delimiter);
                    if (nextValues.Length != columns.Length) throw new FormatException("秒为60的记录缺少可靠后邻记录");
                    var nextTime = ParseSourceTime(nextValues[map["Date"]], nextValues[map["Time"]]);
                    if (time <= previous || (time - previous.Value).TotalSeconds > 1 || nextTime < time || (nextTime - time).TotalSeconds > 1)
                        throw new FormatException("秒为60的记录不满足前后各1秒内的连续进位关系");
                    carriedClocks++;
                }
                double lon = double.Parse(values[map["Longitude"]], NumberStyles.Float, Invariant);
                double lat = double.Parse(values[map["Latitude"]], NumberStyles.Float, Invariant);
                if (!double.IsFinite(lon) || !double.IsFinite(lat) || !WorkareaSpatialService.ValidCoordinate(lon, lat))
                    throw new FormatException("经纬度无效");
                if (previous is { } p && (time < p || (time - p).TotalSeconds > 900 || time.Date != p.Date))
                {
                    if (time < p) reversals++;
                    FinishRun();
                    source = $"csv:{hash}/run-{runIndex + 1:D5}";
                }
                var sample = new UavMagCanonicalSample(time, lon, lat, null, null, null, null, "", null, "",
                    source, record, []);
                run.Add(sample); samples.Add(sample); previous = time;
                rows.Add(new(record, raw, source, null));
            }
            catch (Exception ex) when (ex is FormatException or ArgumentOutOfRangeException or MalformedLineException)
            {
                rows.Add(new(record, raw, source, ex.Message));
                FinishRun(); previous = null;
            }
            hasRow = hasNext;
        }
        FinishRun();
        if (samples.Count < 3) throw new InvalidDataException("至少需要 3 条有效日期时间和经纬度记录。");
        return new(path, hash, encoding.CodePage, delimiter, header, columns, map, rows,
            new FieldMagFolderAnalysis { SourceDirectory = System.IO.Path.GetDirectoryName(path)!,
                Fingerprint = Version + ":" + hash, ProjectTimeZoneId = TimeBasis, UavSamples = samples }, windows, reversals, carriedClocks);
    }

    private static DateTimeOffset CarryClock(string date, string clock)
    {
        string value = clock.Trim();
        string[] parts;
        if (value.Contains(':')) parts = value.Split(':');
        else
        {
            var digits = value.Split('.');
            if (digits.Length > 2 || digits[0].Length > 6 || digits[0].Any(c => !char.IsAsciiDigit(c)))
                throw new FormatException("无法解释时间进位");
            string padded = digits[0].PadLeft(6, '0');
            parts = [padded[..2], padded[2..4], padded[4..] + (digits.Length == 2 ? "." + digits[1] : "")];
        }
        if (parts.Length != 3 || !int.TryParse(parts[0], out int hour) || hour is < 0 or > 23 ||
            !int.TryParse(parts[1], out int minute) || minute is < 0 or > 59 ||
            !decimal.TryParse(parts[2], NumberStyles.AllowDecimalPoint, Invariant, out var second) || second != 60)
            throw new FormatException("时间超出有效范围且不是整秒60进位");
        return ParseSourceTime(date, $"{hour:D2}{minute:D2}00").AddSeconds(60);
    }

    public static DateTimeOffset ParseSourceTime(string date, string time)
    {
        if (!DateTime.TryParseExact(date.Trim(), ["yyyyMMdd", "yyyy-MM-dd", "yyyy/MM/dd"], Invariant,
                DateTimeStyles.None, out var day)) throw new FormatException("日期格式应为 yyyyMMdd 或 yyyy-MM-dd");
        string clock = time.Trim();
        if (!clock.Contains(':'))
        {
            var parts = clock.Split('.');
            if (parts.Length > 2 || parts[0].Length > 6 || parts.Any(p => p.Length == 0 || p.Any(c => c < '0' || c > '9')))
                throw new FormatException("时间格式应为 HHmmss.s 或 HH:mm:ss.s");
            string digits = parts[0].PadLeft(6, '0');
            clock = digits[..2] + ":" + digits[2..4] + ":" + digits[4..] + (parts.Length == 2 ? "." + parts[1] : "");
        }
        if (!TimeSpan.TryParseExact(clock, [@"hh\:mm\:ss", @"hh\:mm\:ss\.FFFFFFF"], Invariant, out var tod) ||
            tod < TimeSpan.Zero || tod >= TimeSpan.FromDays(1)) throw new FormatException("时间超出有效范围");
        // Zero offset is an adapter transport only; this is NOT a confirmed UTC timestamp.
        return new DateTimeOffset(DateTime.SpecifyKind(day + tod, DateTimeKind.Unspecified), TimeSpan.Zero);
    }

    public static string Export(IntermediateCsvSnapshot input, AuditProductionResult audit,
        AuditProductionRequest request, string outputRoot)
    {
        if (IntermediateSegmentationEvidence.Problem(request.TrackSegmentation) is { } basisError)
            throw new InvalidDataException(basisError);
        if (request.SourceFingerprint != input.Analysis.Fingerprint || audit.ProductionRequestFingerprint != ProductionSnapshotService.RequestFingerprint(request))
            throw new InvalidDataException("分线选择与当前输入/参数不一致，请重新核验。");
        string current = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(input.Path)));
        if (current != input.Sha256) throw new InvalidDataException("输入 CSV 已变化，请重新载入核验；旧稿保留。");
        var assignments = MagneticSurveyLineSegmentationService.FromAudit(input.Analysis.UavSamples,
            audit.EffectiveTasks ?? [], audit).Assignments.ToDictionary(a => (a.SourceFile, a.SourceRecord));
        var selected = audit.SelectedSamples.Select(s => (s.SourceFile, s.SourceRecord)).ToHashSet();
        if (selected.Count == 0) throw new InvalidDataException("尚无已纳入记录，未生成空成果。");
        if (selected.Any(k => !assignments.ContainsKey(k))) throw new InvalidDataException("选中记录缺少分线结果。");
        string batch = "空间分线_" + DateTimeOffset.Now.ToString("yyyyMMdd_HHmmss_fff", Invariant) + "_" + Guid.NewGuid().ToString("N")[..8];
        string final = System.IO.Path.Combine(outputRoot, batch), staging = final + ".partial";
        Directory.CreateDirectory(staging);
        string output = System.IO.Path.Combine(staging, "正式测线_空间筛选.csv");
        int retained = 0;
        using (var keep = new StreamWriter(output, false, new UTF8Encoding(true)))
        using (var reject = new StreamWriter(System.IO.Path.Combine(staging, "未纳入记录.csv"), false, new UTF8Encoding(true)))
        using (var trace = new StreamWriter(System.IO.Path.Combine(staging, "分线追溯.csv"), false, new UTF8Encoding(true)))
        {
            keep.WriteLine("Line" + input.Delimiter + "LineOrder" + input.Delimiter + BusinessHeader(input));
            reject.WriteLine(BusinessHeader(input));
            trace.WriteLine("来源记录号,是否纳入,测线名称,测线顺序,测线趟次,任务标识,排除原因");
            foreach (var row in input.Rows)
            {
                if (selected.Contains((row.Source, row.Record)))
                {
                    var a = assignments[(row.Source, row.Record)];
                    if (string.IsNullOrWhiteSpace(a.LineName)) throw new InvalidDataException("选中记录缺少线号，未提交成果。");
                    if (a.LineOrder is null) throw new InvalidDataException("选中记录缺少测线顺序，未提交成果。");
                    keep.WriteLine(Quote(a.LineName, input.Delimiter) + input.Delimiter + a.LineOrder.Value.ToString(Invariant) + input.Delimiter + row.Raw); retained++;
                    string[] extra = [row.Record.ToString(Invariant), "是", a.LineName, a.LineOrder?.ToString(Invariant) ?? "",
                        a.LinePass?.ToString(Invariant) ?? "", a.SurveyTaskStableId, ""];
                    trace.WriteLine(string.Join(',', extra.Select(v => Quote(v, ','))));
                }
                else
                {
                    reject.WriteLine(row.Raw);
                    trace.WriteLine(row.Record.ToString(Invariant) + ",否,,,,," + Quote(row.Error ?? "未纳入或已按核验异常区间排除", ','));
                }
            }
        }
        if (retained != selected.Count) throw new InvalidDataException("输出来源计数不一致，未提交成果。");
        SegmentationReviewStore.Save(System.IO.Path.Combine(staging, SegmentationReviewStore.AutomaticStateName), request);
        File.WriteAllText(System.IO.Path.Combine(staging, "母线编号对照.json"), JsonSerializer.Serialize(audit.ActualLineNumbers, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(System.IO.Path.Combine(staging, "未归属判定.json"), JsonSerializer.Serialize(request.MotherLineRestoration?.UnassignedEvidence ?? [], new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(System.IO.Path.Combine(staging, "独立航段对照.json"), JsonSerializer.Serialize(
            assignments.Values.Where(a => a.IncludeInProcessing && a.LineTaskId.StartsWith("unassigned:", StringComparison.Ordinal))
                .GroupBy(a => a.LineTaskId).Select(g => new
                {
                    LegId = g.Key["unassigned:".Length..], Line = g.First().LineName, LineOrder = g.First().LineOrder,
                    Sources = g.GroupBy(a => a.SourceFile).Select(source => new { Source = source.Key,
                        FirstRecord = source.Min(a => a.SourceRecord), LastRecord = source.Max(a => a.SourceRecord), Count = source.Count() })
                }), new JsonSerializerOptions { WriteIndented = true }));
        string Hash(string p) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)));
        File.WriteAllText(System.IO.Path.Combine(staging, "manifest.json"), JsonSerializer.Serialize(new
        {
            Schema = Version, InputPath = input.Path, input.Sha256, input.CodePage, Delimiter = input.Delimiter.ToString(),
            input.Mapping, TimeBasis, TimeAdapter = "Zero offset transport only; not UTC", input.TimeReversals, input.CarriedClocks,
            OriginalHeader = input.Header, ExportHeader = "Line" + input.Delimiter + "LineOrder" + input.Delimiter + BusinessHeader(input),
            LineField = "Line", LineOrderField = "LineOrder", LineSource = "Current segmentation assignment LineName and LineOrder; inferred identity when no design KML exists",
            ClockCarryRule = "Exact second 60 only; previous < carried <= next within 1 second each; equal next timestamp retained; source text unchanged",
            TotalRecords = input.Rows.Count, RetainedRecords = retained, ExcludedRecords = input.Rows.Count - retained,
            input.InvalidRecords, Processing = "Spatial and relative time selection only; all business columns unchanged",
            RequestFingerprint = audit.ProductionRequestFingerprint, Status = "SPATIAL_SELECTION_REVIEW",
            Files = Directory.GetFiles(staging).Select(p => new { Name = System.IO.Path.GetFileName(p), Sha256 = Hash(p) }).ToArray()
        }, new JsonSerializerOptions { WriteIndented = true }));
        Directory.Move(staging, final);
        return System.IO.Path.Combine(final, System.IO.Path.GetFileName(output));
    }

    private static string Quote(string value, char delimiter) => value.IndexOfAny([delimiter, '"', '\r', '\n']) < 0
        ? value : "\"" + value.Replace("\"", "\"\"") + "\"";

    private static string[] Fields(string raw, char delimiter)
    {
        using var parser = new TextFieldParser(new StringReader(raw)) { HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = false };
        parser.SetDelimiters(delimiter.ToString());
        return parser.ReadFields() ?? [];
    }

    private static char DetectDelimiter(string header)
    {
        var counts = new Dictionary<char, int> { [','] = 0, ['\t'] = 0, [';'] = 0 };
        bool quoted = false;
        for (int i = 0; i < header.Length; i++)
        {
            if (header[i] == '"')
            {
                if (quoted && i + 1 < header.Length && header[i + 1] == '"') { i++; continue; }
                quoted = !quoted;
            }
            else if (!quoted && counts.ContainsKey(header[i])) counts[header[i]]++;
        }
        return counts.MaxBy(p => p.Value).Key;
    }

    // Preserve each original CSV record verbatim, including quoted newlines and whitespace.
    private static IEnumerable<string> Records(string text)
    {
        bool quoted = false;
        int begin = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '"')
            {
                if (quoted && i + 1 < text.Length && text[i + 1] == '"') { i++; continue; }
                quoted = !quoted;
            }
            if (!quoted && text[i] is '\r' or '\n')
            {
                if (i > begin) yield return text[begin..i];
                if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                begin = i + 1;
            }
        }
        if (quoted) throw new InvalidDataException("CSV 引号未闭合，停止导入以保护行边界。");
        if (begin < text.Length) yield return text[begin..];
    }
}
