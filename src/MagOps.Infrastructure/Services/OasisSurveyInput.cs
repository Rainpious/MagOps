using System.Globalization;
using System.IO.Compression;
using Microsoft.VisualBasic.FileIO;
using MagOps.Core.Coordinates;

namespace MagOps.Infrastructure.Services;

public sealed record OasisMapPoint(double X, double Y, double Field);
public sealed record OasisSurveyLine(string Name, string Pass, string TaskId, List<OasisMapPoint> Points);
public sealed record OasisSurveyInput(List<OasisSurveyLine> Lines, bool IsLevelled, double Meridian,
    double Inclination, double GridDeclination, double TotalField, double MaxPositionErrorM)
{
    public static bool IsSurveyFile(string path) => path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".csv.gz", StringComparison.OrdinalIgnoreCase);

    public static string FindProcessingFile(string directory, GeneratedArtifactContext context, string latestExport,
        CancellationToken token)
    {
        if (!Directory.Exists(directory)) return "";
        var paths = new DirectoryInfo(directory).EnumerateFiles()
            .Where(f => IsSurveyFile(f.Name))
            .OrderByDescending(f => f.FullName.Equals(latestExport, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(f => f.LastWriteTimeUtc).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var file in paths)
        {
            token.ThrowIfCancellationRequested();
            if (DiurnalArtifactBundle.Outputs.Any(entry => entry.Value.Stage != "prelevel_dtgz" &&
                (file.Name.Equals(entry.Key, StringComparison.OrdinalIgnoreCase) ||
                 file.Name.Equals(GeneratedArtifactIdentity.TaggedName(entry.Key, entry.Value.Stage), StringComparison.OrdinalIgnoreCase)))) continue;
            try
            {
                Read(file.FullName, context, token);
                return file.FullName;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
                or MalformedLineException or FormatException or ArgumentException or System.Text.Json.JsonException) { }
        }
        return "";
    }

    public static OasisSurveyInput Read(string path) => Read(path, null, CancellationToken.None);

    private static OasisSurveyInput Read(string path, GeneratedArtifactContext? expected, CancellationToken token)
    {
        if (File.Exists(path + GeneratedArtifactIdentity.Suffix) ||
            (expected is not null && Path.GetFileName(path).Contains("__MagOps_", StringComparison.OrdinalIgnoreCase)))
        {
            var identity = GeneratedArtifactIdentity.Inspect(path);
            if (identity.Status != "VERIFIED" || identity.IsHistoricalVersion || identity.IsProductionStale || identity.IsProjectSpatialReferenceStale)
                throw new InvalidDataException(identity.Warning ?? "源成果为历史版本或身份校验未通过。");
            var meta = identity.Metadata!;
            if (meta.ProcessingStage.Contains("REVIEW_DRAFT", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(meta.QualityStatus, "REVIEW_DRAFT", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("源成果的阶段标记为复核草稿。");
            if (expected is not null &&
                ((!string.IsNullOrEmpty(meta.ProjectId) && !string.Equals(meta.ProjectId, expected.ProjectId, StringComparison.OrdinalIgnoreCase)) ||
                 (!string.IsNullOrEmpty(meta.WorkareaId) && !string.Equals(meta.WorkareaId, expected.WorkareaId, StringComparison.OrdinalIgnoreCase))))
                throw new InvalidDataException("源成果属于其他项目或工区。");
        }
        if (path.Contains("REVIEW_DRAFT", StringComparison.OrdinalIgnoreCase) ||
            path.Contains(".review-draft", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("请选择已核定的处理用 CSV。当前路径标为复核草稿。");
        using var file = File.OpenRead(path);
        using Stream stream = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
            ? new GZipStream(file, CompressionMode.Decompress) : file;
        using var reader = new TextFieldParser(stream, System.Text.Encoding.UTF8, true);
        reader.SetDelimiters(",");
        var header = reader.ReadFields() ?? throw new InvalidDataException("CSV 没有表头。");
        int Col(string name) => Array.FindLastIndex(header, c => c.Trim().Equals(name, StringComparison.OrdinalIgnoreCase));
        int Required(string name)
        {
            int index = Col(name);
            return index >= 0 ? index : throw new InvalidDataException($"CSV 缺少 {name} 列。");
        }
        int line = Col("LineName"), pass = Col("LinePass"), task = Col("LineTaskId");
        if (line < 0) line = Required("Line");
        int[] alternateLineColumns = Enumerable.Range(0, header.Length).Where(i => i != line &&
            header[i].Trim().Equals(header[line].Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
        int x = Required("X"), y = Required("Y"), lon = Required("Longitude"), lat = Required("Latitude");
        int inc = Required("Inc"), dec = Required("Dec"), tof = Required("Tof");
        int field = Col("dT");
        bool levelled = field >= 0;
        if (!levelled) field = Required("dTgz");
        var lines = new List<OasisSurveyLine>();
        double cm = double.NaN, maxError = 0, sumInc = 0, sumSin = 0, sumCos = 0, sumField = 0;
        int count = 0;
        while (!reader.EndOfData)
        {
            token.ThrowIfCancellationRequested();
            long rowNumber = reader.LineNumber;
            var row = reader.ReadFields()!;
            if (row.Length != header.Length) throw new InvalidDataException($"第 {rowNumber} 行列数与表头不符。");
            double Number(int i) => double.TryParse(row[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
                && double.IsFinite(v) && Math.Abs(v) < 1e30 ? v : throw new InvalidDataException($"第 {rowNumber} 行 {header[i]} 缺少有效数值。");
            string name = row[line].Trim(), flight = pass < 0 ? "" : row[pass].Trim();
            string taskId = task < 0 ? "" : row[task].Trim();
            if (alternateLineColumns.Any(i => row[i].Trim() is not ("" or "D0") && row[i].Trim() != name))
                throw new InvalidDataException($"第 {rowNumber} 行的同名线号列互相冲突。");
            if (name.Length == 0) throw new InvalidDataException($"第 {rowNumber} 行缺少测线号。");
            var point = new OasisMapPoint(Number(x), Number(y), Number(field));
            double longitude = Number(lon), latitude = Number(lat);
            if (!double.IsFinite(cm))
            {
                double near = Math.Round(longitude / 3) * 3;
                cm = Enumerable.Range(-2, 5).Select(offset => near + offset * 3)
                    .Where(candidate => candidate >= -180 && candidate <= 180)
                    .MinBy(candidate => Error(candidate));
            }
            double Error(double candidate)
            {
                var en = CoordinateService.GaussKrugerForward(longitude, latitude, candidate);
                return double.Hypot(en.Easting - point.X, en.Northing - point.Y);
            }
            maxError = Math.Max(maxError, Error(cm));
            if (maxError > 5) throw new InvalidDataException($"第 {rowNumber} 行经纬度与米制高斯投影不符，无法配准 KMZ（误差 {maxError:F1} m）。");
            var en = CoordinateService.GaussKrugerForward(longitude, latitude, cm);
            var north = CoordinateService.GaussKrugerForward(longitude, latitude + 0.00001, cm);
            double az = Number(dec) * Math.PI / 180 + Math.Atan2(north.Easting - en.Easting, north.Northing - en.Northing);
            sumSin += Math.Sin(az); sumCos += Math.Cos(az);
            double inclination = Number(inc), total = Number(tof);
            if (Math.Abs(inclination) > 90 || total <= 0) throw new InvalidDataException($"第 {rowNumber} 行地磁方向或总场超出范围。");
            sumInc += inclination;
            sumField += total;
            if (lines.Count == 0 || lines[^1].Name != name || lines[^1].Pass != flight || lines[^1].TaskId != taskId)
                lines.Add(new(name, flight, taskId, []));
            lines[^1].Points.Add(point);
            count++;
        }
        if (lines.Select(l => l.Name).Distinct().Count() < 2) throw new InvalidDataException("至少需要两条相邻实测线判断线间空隙。");
        return new(lines, levelled, cm, sumInc / count, Math.Atan2(sumSin, sumCos) * 180 / Math.PI, sumField / count, maxError);
    }
}
