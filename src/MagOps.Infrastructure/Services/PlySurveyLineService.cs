using System.Globalization;
using System.Text;
using MagOps.Core.SurveyPlanning;

namespace MagOps.Infrastructure.Services;

/// <summary>
/// Reads the simple Oasis XY PLY files used by MagOps and writes a raw
/// Line/X/Y endpoint table that can feed the existing concave CONNECT/SPLIT
/// preprocessor and v25 route workflow.
/// </summary>
public sealed class PlySurveyLineService
{
    private readonly SurveyLinePlanner _planner = new();

    public SurveyLinePlanResult Plan(
        string sourcePath,
        SurveyLinePlanOptions options) =>
        _planner.Plan(ReadBoundary(sourcePath), options);

    public IReadOnlyList<SurveyBoundaryPoint> ReadBoundary(string sourcePath)
    {
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("请选择有效的 PLY 工区边界文件。", sourcePath);

        byte[] bytes = File.ReadAllBytes(sourcePath);
        string text = DecodePlyText(bytes);
        var points = new List<SurveyBoundaryPoint>();

        int row = 0;
        foreach (string rawLine in text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None))
        {
            row++;
            string line = rawLine.Trim();
            if (line.Length == 0)
                continue;

            string[] cells = line
                .Split(new[] { '\t', ' ', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (cells.Length != 2)
            {
                throw new InvalidDataException(
                    $"PLY 第 {row} 行必须只有 X、Y 两个数值字段：{rawLine}");
            }

            if (!double.TryParse(
                    cells[0],
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out double x) ||
                !double.TryParse(
                    cells[1],
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out double y))
            {
                throw new InvalidDataException(
                    $"PLY 第 {row} 行 X/Y 不是有效数字：{rawLine}");
            }

            points.Add(new SurveyBoundaryPoint(x, y));
        }

        if (points.Count < 3)
            throw new InvalidDataException("PLY 工区边界至少需要 3 个有效 XY 点。");

        // The planning distance/spacing contract is metres. Geographic-degree
        // PLY must be projected first rather than silently treating degrees as m.
        bool looksGeographic = points.All(p =>
            p.X is >= -180 and <= 180 &&
            p.Y is >= -90 and <= 90);
        if (looksGeographic)
        {
            throw new InvalidDataException(
                "PLY 自动划线要求平面米制 XY 坐标；当前数据看起来是经纬度。" +
                "请先在“坐标与空间”中转换为项目高斯-克吕格坐标后再导出 PLY。");
        }

        return points;
    }

    public void WriteEndpointCsv(
        string outputPath,
        SurveyLinePlanResult plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Lines.Count == 0)
            throw new InvalidOperationException("自动划线没有生成任何有效测线端点。");

        string? directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        using var writer = new StreamWriter(
            outputPath,
            false,
            new UTF8Encoding(true));
        writer.WriteLine("Line,X,Y");

        foreach (SurveyPlannedLine line in plan.Lines)
        {
            for (int segmentIndex = 0; segmentIndex < line.Segments.Count; segmentIndex++)
            {
                SurveyLineSegment segment = line.Segments[segmentIndex];
                WritePoint(writer, line.LineLabel, segment.Start);
                WritePoint(writer, line.LineLabel, segment.End);

                if (segmentIndex < line.Segments.Count - 1)
                    writer.WriteLine($"{line.LineLabel},,");
            }
        }
    }

    private static void WritePoint(
        TextWriter writer,
        string line,
        SurveyBoundaryPoint point)
    {
        writer.Write(line);
        writer.Write(',');
        writer.Write(point.X.ToString("0.###############", CultureInfo.InvariantCulture));
        writer.Write(',');
        writer.WriteLine(point.Y.ToString("0.###############", CultureInfo.InvariantCulture));
    }

    private static string DecodePlyText(byte[] bytes)
    {
        if (bytes.Length >= 3 &&
            bytes[0] == 0xEF &&
            bytes[1] == 0xBB &&
            bytes[2] == 0xBF)
        {
            return new UTF8Encoding(false, true).GetString(bytes, 3, bytes.Length - 3);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

        // Some legacy PLY files may be UTF-16 without a BOM. A high share of
        // NUL bytes in alternating positions is a reliable signal for this
        // numeric/tab/newline format.
        if (LooksLikeUtf16Le(bytes))
            return Encoding.Unicode.GetString(bytes);
        if (LooksLikeUtf16Be(bytes))
            return Encoding.BigEndianUnicode.GetString(bytes);

        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException(
                "PLY 文本编码无法识别。当前支持 UTF-8、UTF-8 BOM、UTF-16 LE/BE。",
                ex);
        }
    }

    private static bool LooksLikeUtf16Le(byte[] bytes)
    {
        if (bytes.Length < 8)
            return false;
        int samples = Math.Min(bytes.Length / 2, 80);
        int zeroHigh = 0;
        for (int i = 0; i < samples; i++)
        {
            if (bytes[i * 2 + 1] == 0)
                zeroHigh++;
        }
        return zeroHigh >= samples * 3 / 4;
    }

    private static bool LooksLikeUtf16Be(byte[] bytes)
    {
        if (bytes.Length < 8)
            return false;
        int samples = Math.Min(bytes.Length / 2, 80);
        int zeroHigh = 0;
        for (int i = 0; i < samples; i++)
        {
            if (bytes[i * 2] == 0)
                zeroHigh++;
        }
        return zeroHigh >= samples * 3 / 4;
    }
}
