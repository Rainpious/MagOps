using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.VisualBasic.FileIO;

namespace MagOps.Infrastructure.Services;

public sealed record MotherLineCsvGroup(string Key, string LineName, string LineTaskId, int LineOrder, int Records)
{
    public int LinePass { get; init; }
}
public sealed record MotherLineCsvInputRow(int Record, string GroupKey, int Run, DateTimeOffset TimestampUtc,
    double Longitude, double Latitude, IReadOnlyList<string> Values);
public sealed record MotherLineCsvInput(string Path, string Sha256, IReadOnlyList<MotherLineCsvGroup> Groups,
    IReadOnlyList<MotherLineCsvInputRow> Rows)
{
    public bool IsReviewDraft { get; init; }
}
public sealed record MotherLineCsvBinding(string GroupKey, string PartitionCode, string MotherLineStableId,
    bool IsQualityRepeat = false);

/// <summary>Reads already processed observations without rerunning recognition or corrections.
/// SourceRecord identifies the CSV data record (1-based), never an invented raw instrument record.</summary>
public static class MotherLineProcessingCsvImportService
{
    public const string Version = "mother-processing-csv-import-2";
    public const string SourceKind = "PROCESSING_CSV";
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static MotherLineCsvInput Read(string path, CancellationToken token = default)
    {
        path = System.IO.Path.GetFullPath(path);
        token.ThrowIfCancellationRequested();
        byte[] bytes = File.ReadAllBytes(path);
        token.ThrowIfCancellationRequested();
        string hash = Convert.ToHexString(SHA256.HashData(bytes));
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF'); }
        catch (DecoderFallbackException ex) { throw new InvalidDataException("处理CSV须为UTF-8编码，请另存副本后导入。", ex); }
        using var parser = new TextFieldParser(new StringReader(text))
        { TextFieldType = FieldType.Delimited, HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = false };
        parser.SetDelimiters(",");
        try
        {
            var header = parser.ReadFields() ?? throw new InvalidDataException("处理CSV为空。");
            var columns = MagneticPreLevelProcessingService.ProcessingColumns;
            Require(header.Length == columns.Count && header.Select(h => h.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() == columns.Count,
                "需要完整且无重复的MagOps处理CSV列；不将其他阶段文件自动解释为已改正成果");
            var mapping = columns.Select(c => Array.FindIndex(header, h => h.Trim().Equals(c, StringComparison.OrdinalIgnoreCase))).ToArray();
            Require(mapping.All(i => i >= 0), "CSV缺少处理列；需要LineName至dTgz的完整20列");
            var rows = new List<MotherLineCsvInputRow>();
            var groups = new Dictionary<string, MotherLineCsvGroup>(StringComparer.Ordinal);
            int record = 0, run = 0;
            var runs = new Dictionary<string, (int Run, DateTimeOffset Time)>(StringComparer.Ordinal);
            while (!parser.EndOfData)
            {
                token.ThrowIfCancellationRequested();
                var cells = parser.ReadFields()!; record++;
                Require(cells.Length == header.Length, $"第{record}条记录列数不一致");
                var values = mapping.Select(i => cells[i]).ToArray();
                Require(!string.IsNullOrWhiteSpace(values[0]), $"第{record}条缺测线名");
                Require(int.TryParse(values[1], NumberStyles.Integer, Invariant, out int order) && order > 0,
                    $"第{record}条LineOrder须为正整数");
                Require(int.TryParse(values[2], NumberStyles.Integer, Invariant, out int pass) && pass > 0,
                    $"第{record}条LinePass须为正整数");
                Require(DateOnly.TryParseExact(values[3], "yyyy-MM-dd", Invariant, DateTimeStyles.None, out var date) &&
                    TimeOnly.TryParseExact(values[4], ["HH:mm:ss", "HH:mm:ss.FFFFFFF"], Invariant, DateTimeStyles.None, out _),
                    $"第{record}条DateUtc/TimeUtc无效");
                var time = TimeOnly.Parse(values[4], Invariant);
                var stamp = new DateTimeOffset(date.ToDateTime(time), TimeSpan.Zero);
                for (int i = 6; i < values.Length; i++)
                    if (!string.IsNullOrWhiteSpace(values[i]))
                        Require(double.TryParse(values[i], NumberStyles.Float, Invariant, out double n) && double.IsFinite(n),
                            $"第{record}条{columns[i]}不是有限数值");
                double RequiredNumber(int index)
                {
                    Require(double.TryParse(values[index], NumberStyles.Float, Invariant, out double n) && double.IsFinite(n),
                        $"第{record}条缺少有效{columns[index]}");
                    return n;
                }
                double lon = RequiredNumber(6), lat = RequiredNumber(7);
                Require(WorkareaSpatialService.ValidCoordinate(lon, lat), $"第{record}条经纬度超出范围");
                _ = RequiredNumber(12);
                string key = JsonSerializer.Serialize(new { Name = values[0], Task = values[5], Order = order, Pass = pass });
                if (!runs.TryGetValue(key, out var state) || stamp < state.Time) state = (++run, stamp);
                runs[key] = (state.Run, stamp);
                rows.Add(new(record, key, state.Run, stamp, lon, lat, values));
                groups[key] = groups.TryGetValue(key, out var group) ? group with { Records = group.Records + 1 }
                    : new(key, values[0], values[5], order, 1) { LinePass = pass };
            }
            Require(rows.Count > 0, "处理CSV没有数据记录");
            return new(path, hash, groups.Values.ToArray(), rows)
            { IsReviewDraft = path.Contains("REVIEW_DRAFT", StringComparison.OrdinalIgnoreCase) ||
                path.Contains(".review-draft", StringComparison.OrdinalIgnoreCase) };
        }
        catch (MalformedLineException ex) { throw new InvalidDataException("CSV引号或记录格式错误。", ex); }
    }

    public static MotherLineProductionBatch Capture(string projectId, string workareaId,
        PartitionMotherLineIdentityCatalog catalog, MotherLineCsvInput input,
        IReadOnlyList<MotherLineCsvBinding> bindings, CancellationToken token = default)
    {
        PartitionMotherLineIdentityService.Validate(catalog);
        Require(!string.IsNullOrWhiteSpace(projectId) && !string.IsNullOrWhiteSpace(workareaId), "请先打开已登记项目和工区");
        Require(bindings.Select(b => b.GroupKey).Distinct(StringComparer.Ordinal).Count() == bindings.Count, "同组不能重复绑定");
        var groups = input.Groups.Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var b in bindings)
            Require(groups.Contains(b.GroupKey) && catalog.Partitions.Any(p => p.PartitionCode == b.PartitionCode &&
                p.Lines.Any(l => l.MotherStableId == b.MotherLineStableId)), "绑定不属于本CSV或当前分区母线目录");
        var byGroup = bindings.ToDictionary(b => b.GroupKey, StringComparer.Ordinal);
        var observations = new List<MotherLineProductionObservation>(input.Rows.Count);
        foreach (var row in input.Rows)
        {
            token.ThrowIfCancellationRequested();
            var v = row.Values;
            // Catalog geometry uses standard false origins. Preserve processing values, but reject
            // incompatible XY rather than sorting in one projection and exporting another.
            var xy = MagOps.Core.Coordinates.CoordinateService.GaussKrugerForward(
                row.Longitude, row.Latitude, catalog.CentralMeridian, catalog.Datum);
            Require(double.TryParse(v[8], NumberStyles.Float, Invariant, out double x) &&
                double.TryParse(v[9], NumberStyles.Float, Invariant, out double y) &&
                double.IsFinite(x) && double.IsFinite(y) &&
                Math.Sqrt(Math.Pow(x - xy.Easting, 2) + Math.Pow(y - xy.Northing, 2)) <= 2,
                $"第{row.Record}条X/Y与当前目录投影不一致（容差2米）；自定义假东/假北或其他基准需先核对，不能自动重投影");
            double? Optional(int index) => string.IsNullOrWhiteSpace(v[index]) ? null : double.Parse(v[index], Invariant);
            var sample = new UavMagCanonicalSample(row.TimestampUtc, row.Longitude, row.Latitude, Optional(10), Optional(11),
                Optional(12), null, "", null, "", input.Path, row.Record,
                ["PROCESSING_CSV_RAW_LINEAGE_UNVERIFIED", "PROCESSING_CSV_PROJECT_DESIGN_APPROVAL_UNVERIFIED"]);
            byGroup.TryGetValue(row.GroupKey, out var binding);
            string task = string.IsNullOrWhiteSpace(v[5]) ? "csv-group-" + Digest(row.GroupKey) : v[5];
            string runId = "csv-run-" + input.Sha256 + "-" + row.Run.ToString(Invariant);
            var assignment = new MagneticSegmentAssignment(input.Path, row.Record, row.TimestampUtc, row.Longitude, row.Latitude,
                binding is null ? MagneticSegmentClass.UnmatchedNoSurveyLine : MagneticSegmentClass.SurveyLine,
                true, row.Run, v[5], task, v[0], int.Parse(v[1], Invariant), int.Parse(v[2], Invariant), null, null, null, null)
            {
                PartitionCode = binding?.PartitionCode ?? "", MotherLineStableId = binding?.MotherLineStableId ?? "",
                ExecutionSegmentStableId = runId, IsQualityRepeat = binding?.IsQualityRepeat ?? false
            };
            observations.Add(new(input.Sha256, sample, assignment, runId) { ProcessingValues = v.ToArray() });
        }
        string snapshot = Digest(JsonSerializer.Serialize(new { Version, projectId, workareaId, catalog.DesignId, input.Sha256, input.IsReviewDraft,
            Bindings = bindings.OrderBy(b => b.GroupKey, StringComparer.Ordinal).ToArray() }));
        var batch = new MotherLineProductionBatch(projectId, workareaId, catalog.DesignId, snapshot, observations)
        { SourceKind = SourceKind, OriginalLineageVerified = false, IsReviewDraft = input.IsReviewDraft };
        MotherLineCandidateFileService.VerifySources([batch], token);
        return batch;
    }

    private static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static void Require(bool valid, string message)
    { if (!valid) throw new InvalidDataException(message + "；原文件未改动。"); }
}
