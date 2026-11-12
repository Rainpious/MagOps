using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MagOps.Core.Coordinates;
using MagOps.Infrastructure.Services;
using Microsoft.VisualBasic.FileIO;

namespace MagOps.Application;

public sealed record IntermediateTableMapping
{
    public int SchemaVersion { get; init; } = 1;
    public string ProcessingStage { get; init; } = "";
    public string StageEvidence { get; init; } = "";
    public string CoordinateMode { get; init; } = "";
    public string XColumn { get; init; } = "";
    public string YColumn { get; init; } = "";
    public string MagneticColumn { get; init; } = "";
    public string MagneticMeaning { get; init; } = "";
    public ProjectionParameters? Projection { get; init; }
    public string Delimiter { get; init; } = ",";
}
public sealed record IntermediateTableRow(int PhysicalLine, IReadOnlyList<string> Values,
    double? Longitude, double? Latitude, double? MagneticValue, IReadOnlyList<string> Reasons);
public sealed record IntermediateTableResult(string SourcePath, string SourceSha256, string MappingSha256,
    IntermediateTableMapping Mapping, IReadOnlyList<string> Headers, IReadOnlyList<IntermediateTableRow> Rows)
{
    public bool ReadOnly => true;
    public bool ProductionEligible => false;
    public int InvalidCount => Rows.Count(r => r.Reasons.Count > 0);
}

/// <summary>Independent continuation inventory. Does not segment, correct or infer an unknown CRS/stage.</summary>
public sealed class IntermediateTableInspection
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, Converters = { new JsonStringEnumConverter() } };
    public ApplicationResult<IntermediateTableResult> Inspect(string input, string? mappingPath, CancellationToken cancellation = default)
    {
        string op = "op-" + Guid.NewGuid().ToString("N");
        try
        {
            if (!File.Exists(input) || !new[] { ".csv", ".tsv" }.Contains(Path.GetExtension(input).ToLowerInvariant()))
                throw new ArgumentException("需要已有的 CSV/TSV 文件。");
            if (string.IsNullOrWhiteSpace(mappingPath)) throw new ArgumentException("需要显式字段、处理阶段及坐标映射 JSON。");
            byte[] mapBytes = File.ReadAllBytes(mappingPath);
            var mapping = JsonSerializer.Deserialize<IntermediateTableMapping>(mapBytes, Json) ?? throw new ArgumentException("映射不能为空。");
            if (mapping.SchemaVersion != 1 || mapping.Delimiter is not ("," or "\t") ||
                string.IsNullOrWhiteSpace(mapping.ProcessingStage) || string.IsNullOrWhiteSpace(mapping.StageEvidence) ||
                string.IsNullOrWhiteSpace(mapping.MagneticMeaning) || mapping.CoordinateMode is not ("geographic" or "projected"))
                throw new ArgumentException("需声明处理阶段及依据、磁场列含义、geographic/projected 坐标模式和逗号/制表符分隔。");
            if (mapping.CoordinateMode == "projected")
            {
                var p = mapping.Projection ?? throw new ArgumentException("纯 XY 必须声明投影参数。");
                if (!Enum.IsDefined(p.Kind) || !Enum.IsDefined(p.AxisOrder) ||
                    p.Kind == CoordinateProjectionKind.GaussKruger &&
                    (!p.CentralMeridian.HasValue || !double.IsFinite(p.CentralMeridian.Value) || Math.Abs(p.CentralMeridian.Value) > 180 ||
                        p.Datum is not ("WGS84" or "CGCS2000") || !double.IsFinite(p.FalseEasting) || !double.IsFinite(p.FalseNorthing)))
                    throw new ArgumentException("投影参数无效；高斯 XY 需要显式中央经线和受支持的基准。");
            }
            cancellation.ThrowIfCancellationRequested();
            byte[] bytes = File.ReadAllBytes(input);
            using var stream = new MemoryStream(bytes);
            using var parser = new TextFieldParser(stream, new UTF8Encoding(false, true), true)
            { HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = false };
            parser.SetDelimiters(mapping.Delimiter);
            var header = parser.ReadFields() ?? throw new ArgumentException("表格为空。");
            if (header.Any(string.IsNullOrWhiteSpace) || header.Distinct(StringComparer.Ordinal).Count() != header.Length)
                throw new ArgumentException("表头缺失或重名，不能确定列映射。");
            int Index(string name) { int i = Array.IndexOf(header, name); return i >= 0 ? i : throw new ArgumentException("缺少映射列：" + name); }
            int x = Index(mapping.XColumn), y = Index(mapping.YColumn), m = Index(mapping.MagneticColumn);
            if (new[] { x, y, m }.Distinct().Count() != 3) throw new ArgumentException("坐标列与磁场列必须分别指定。");
            var rows = new List<IntermediateTableRow>();
            while (!parser.EndOfData)
            {
                cancellation.ThrowIfCancellationRequested();
                int physical = checked((int)parser.LineNumber);
                string[] fields = parser.ReadFields()!;
                var reasons = new List<string>(); double? lon = null, lat = null, mag = null;
                if (fields.Length != header.Length) reasons.Add("FIELD_COUNT_MISMATCH");
                else
                {
                    if (Number(fields[m], out double v)) mag = v; else reasons.Add("INVALID_MAGNETIC_VALUE");
                    if (!Number(fields[x], out double a) || !Number(fields[y], out double b)) reasons.Add("INVALID_COORDINATES");
                    else
                    {
                        try
                        {
                            if (mapping.CoordinateMode == "projected")
                            {
                                var p = mapping.Projection!;
                                var ll = CoordinateProjectionService.Inverse(p.AxisOrder == XyAxisOrder.EastingNorthing ? a : b,
                                    p.AxisOrder == XyAxisOrder.EastingNorthing ? b : a, p);
                                lon = ll.Longitude; lat = ll.Latitude;
                            }
                            else { lon = a; lat = b; }
                            if (!double.IsFinite(lon.Value) || !double.IsFinite(lat.Value) || Math.Abs(lon.Value) > 180 || Math.Abs(lat.Value) > 90)
                                throw new ArgumentException("经纬度超范围。");
                        }
                        catch (ArgumentException ex) { lon = lat = null; reasons.Add("INVALID_COORDINATES: " + ex.Message); }
                    }
                }
                rows.Add(new(physical, fields, lon, lat, mag, reasons));
            }
            string Hash(byte[] b) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(b));
            return ApplicationResult<IntermediateTableResult>.Success(op, null, new(Path.GetFullPath(input), Hash(bytes), Hash(mapBytes), mapping, header, rows));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or JsonException or MalformedLineException or OperationCanceledException)
        { return ApplicationResult<IntermediateTableResult>.Failure(op, new(ex is OperationCanceledException ? "CANCELLED" : ex is IOException or UnauthorizedAccessException ? "ENVIRONMENT_ERROR" : "VALIDATION_ERROR", ex.Message)); }
    }
    private static bool Number(string text, out double value) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
}
