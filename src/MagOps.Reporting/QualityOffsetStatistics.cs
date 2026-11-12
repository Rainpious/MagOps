using System.Globalization;
using System.Text;

namespace MagOps.Reporting;

/// <summary>Spatial identity is not an acquisition Task/Pass composite key.</summary>
public static class QualitySpatialIdentity
{
    public static string Key(string[] row, int width, QualityColumns columns)
    {
        if (row.Length != width) return "";
        int? column = columns.Mother ?? columns.Segment;
        return column is { } c ? c >= 0 && c < width ? row[c].Trim() : "" : "FILE";
    }
}

public sealed record QualityOffsetLine(int LineOrder, string Identity, int InputPoints, int ValidPoints,
    int EndOutsidePoints, int UnmappedPoints, int InvalidCoordinatePoints, int OtherRejectedPoints,
    double? MeanM, double? MaximumM, double? RmsM, double? P95M, double ComputablePointRatio,
    string KmlPath, string Placemark, string Status);

public sealed record QualityOffsetSummary(int InputLines, int ComputableLines, int InputPoints,
    int ValidPoints, int EndOutsidePoints, int UnmappedPoints, int InvalidCoordinatePoints, int OtherRejectedPoints,
    double? PointWeightedMeanM, double? MaximumM, double? RmsM, double? P95M,
    double? EqualLineMeanM, double ComputablePointRatio);

public sealed partial class QualityCheckService
{
    public static string SpatialKey(string[] row, int width, QualityColumns columns, bool offset) =>
        offset ? QualitySpatialIdentity.Key(row, width, columns) : QualityTimeReader.SegmentKey(row, width, columns);

    private static double? P95(double[] values)
    {
        if (values.Length == 0) return null;
        var sorted = values.Order().ToArray();
        double position = (sorted.Length - 1) * .95;
        int lower = (int)Math.Floor(position), upper = (int)Math.Ceiling(position);
        return sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
    }

    private static double? Rms(double[] values)
    {
        if (values.Length == 0) return null;
        double scale = values.Max(Math.Abs);
        return scale == 0 ? 0 : scale * Math.Sqrt(values.Average(v => Math.Pow(v / scale, 2)));
    }

    private static QualityOffsetLine BuildOffsetLine(int order, string identity, IReadOnlyList<QualityDetail> points, string path, string placemark)
    {
        var values = points.Where(p => p.Value is not null).Select(p => p.Value!.Value).ToArray();
        int end = points.Count(p => p.ReasonCode == "END_OUTSIDE");
        int unmapped = points.Count(p => p.ReasonCode == "UNMAPPED");
        int invalid = points.Count(p => p.ReasonCode == "INVALID_COORDINATE");
        int other = points.Count - values.Length - end - unmapped - invalid;
        return new(order, identity, points.Count, values.Length, end, unmapped, invalid, other,
            values.Length == 0 ? null : values.Average(), values.Length == 0 ? null : values.Max(), Rms(values), P95(values),
            points.Count == 0 ? 0 : (double)values.Length / points.Count, path, placemark,
            values.Length == 0 ? "INSUFFICIENT" : values.Length == points.Count ? "REFERENCE_ONLY" : "PARTIAL_REFERENCE");
    }

    public static QualityOffsetSummary? OffsetSummary(QualityCheckResult result)
    {
        if (result.OffsetLines.Length == 0) return null;
        var lines = result.OffsetLines;
        var values = result.Details.Where(p => p.Metric == "OFFSET" && p.Value is not null).Select(p => p.Value!.Value).ToArray();
        var means = lines.Where(l => l.MeanM is not null).Select(l => l.MeanM!.Value).ToArray();
        int input = lines.Sum(l => l.InputPoints);
        return new(lines.Length, means.Length, input, values.Length, lines.Sum(l => l.EndOutsidePoints),
            lines.Sum(l => l.UnmappedPoints), lines.Sum(l => l.InvalidCoordinatePoints), lines.Sum(l => l.OtherRejectedPoints),
            values.Length == 0 ? null : values.Average(), values.Length == 0 ? null : values.Max(), Rms(values), P95(values),
            means.Length == 0 ? null : means.Average(), input == 0 ? 0 : (double)values.Length / input);
    }

    public static void WriteOffsetLines(string directory, QualityCheckResult result)
    {
        if (result.OffsetLines.Length == 0) return;
        bool fitted = result.Request.OffsetReferenceMode == "ACTUAL_FIT";
        string Name(string value) => fitted ? value.Replace("偏航距", "实测拟合偏离") : value;
        string Row(params object?[] cells) => string.Join(",", cells.Select(v => "\"" +
            (v is IFormattable f ? f.ToString(null, CultureInfo.InvariantCulture) : v?.ToString() ?? "").Replace("\"", "\"\"") + "\""));
        string Header(params string[] cells) => Row(cells.Select(c => (object?)Name(c)).ToArray());
        using (var writer = new StreamWriter(new FileStream(Path.Combine(directory, Name("偏航距逐母线统计.csv")), FileMode.CreateNew), new UTF8Encoding(true)))
        {
            writer.WriteLine(Header("顺序", "母线身份", "状态", "输入点数", "可计算点数", "整线端点外点数", "未对应点数", "坐标无效点数", "其他不可计算点数",
                "平均偏航距_m", "最大偏航距_m", "RMS_m", "P95_m", "可计算点比例", "外部参考KML（嵌入/拟合时为空）", "参考线名称", "统计口径"));
            foreach (var l in result.OffsetLines)
                writer.WriteLine(Row(l.LineOrder, l.Identity, l.Status, l.InputPoints, l.ValidPoints, l.EndOutsidePoints,
                    l.UnmappedPoints, l.InvalidCoordinatePoints, l.OtherRejectedPoints, l.MeanM, l.MaximumM, l.RmsM, l.P95M,
                    l.ComputablePointRatio, l.KmlPath, l.Placemark, OffsetBasis(result.Request) + "；有限参考折线水平距离；不按匹配容差剔除；P95线性插值；比例非里程覆盖率"));
        }
        var summary = OffsetSummary(result)!;
        using var output = new StreamWriter(new FileStream(Path.Combine(directory, Name("偏航距总体统计.csv")), FileMode.CreateNew), new UTF8Encoding(true));
        output.WriteLine(Header("输入母线数", "可计算母线数", "输入点数", "可计算点数", "整线端点外点数", "未对应点数", "坐标无效点数", "其他不可计算点数",
            "点加权平均偏航距_m", "最大偏航距_m", "RMS_m", "P95_m", "母线等权平均偏航距_m", "可计算点比例", "统计范围"));
        output.WriteLine(Row(summary.InputLines, summary.ComputableLines, summary.InputPoints, summary.ValidPoints, summary.EndOutsidePoints,
            summary.UnmappedPoints, summary.InvalidCoordinatePoints, summary.OtherRejectedPoints, summary.PointWeightedMeanM, summary.MaximumM,
            summary.RmsM, summary.P95M, summary.EqualLineMeanM, summary.ComputablePointRatio,
            summary.ValidPoints == summary.InputPoints ? "输入范围内全部点可计算；不自动判定合格" : "仅输入范围内可计算点；存在未计算点，不代表完整工区；不自动判定合格"));
        using (var detail = new StreamWriter(new FileStream(Path.Combine(directory, Name("偏航距逐点明细.csv")), FileMode.CreateNew), new UTF8Encoding(true)))
        {
            detail.WriteLine(Header("原CSV记录号", "母线身份", "偏航距_m", "处理代码", "处理说明", "来源文件", "来源SHA256"));
            foreach (var p in result.Details.Where(p => p.Metric == "OFFSET").OrderBy(p => p.FirstRecord))
                detail.WriteLine(Row(p.FirstRecord, p.Segment, p.Value, p.ReasonCode, p.Reason, result.Request.SourcePath, result.Request.SourceSha256));
        }
        using (var parameters = new StreamWriter(new FileStream(Path.Combine(directory, Name("偏航距报告参数.csv")), FileMode.CreateNew), new UTF8Encoding(true)))
        {
            parameters.WriteLine(Header("参数", "参考值", "单位", "统计口径", "状态"));
            string status = summary.ValidPoints == 0 ? "INSUFFICIENT" : summary.ValidPoints == summary.InputPoints ? "REFERENCE_ONLY" : "PARTIAL_REFERENCE";
            parameters.WriteLine(Row(Name("平均偏航距"), summary.PointWeightedMeanM, "m", "可计算点等权", status));
            parameters.WriteLine(Row(Name("最大偏航距"), summary.MaximumM, "m", "全部可计算点极大值", status));
            parameters.WriteLine(Row(Name("偏航距RMS"), summary.RmsM, "m", Name("sqrt(mean(偏航距平方))"), status));
            parameters.WriteLine(Row(Name("偏航距P95"), summary.P95M, "m", "排序后位置(N-1)*0.95线性插值", status));
            parameters.WriteLine(Row(Name("母线等权平均偏航距"), summary.EqualLineMeanM, "m", "各可计算母线点均值再等权平均", status));
            parameters.WriteLine(Row("可计算点比例", summary.ComputablePointRatio, "比例", "可计算点数/输入点数；非里程覆盖率", status));
        }
        using var explanation = new StreamWriter(new FileStream(Path.Combine(directory, Name("偏航距成果说明.txt")), FileMode.CreateNew), new UTF8Encoding(true));
        explanation.WriteLine(OffsetBasis(result.Request));
        explanation.WriteLine(Name("偏航距独立验算成果（计算参考，不自动判定合格）"));
        if (fitted) explanation.WriteLine("拟合与评估使用同一批点；整线平移会被吸收，不表示设计偏航距。各母线选定范围全部有效坐标等权拟合，不自动去异常；不同Pass合并拟合可能掩盖相对偏移，请先确认分组和范围。");
        explanation.WriteLine("输入为已经分线的正式测线点；本步骤不修改正式测线身份或剔除生产数据。优先母线字段，其次测线字段；Task/Pass仅作来源，不参与偏航身份复合匹配。");
        explanation.WriteLine("输入为WGS84经纬度；逐设计线段以中点纬度建立局部等距近似（球半径6371008.8m），计算有限折线无符号水平距离。反向线段使用相同尺度；这是局部近似，不是椭球测地线精算。横向大偏差仍计入，匹配容差不用于偏航筛点；飞高匹配算法保持独立。");
        explanation.WriteLine("折线内部顶点参与最近距离；最近点位于整线外端且沿向超出时单列END_OUTSIDE，不无限延长，不补零。未对应、无效坐标和其他缺项分别计数。");
        if (result.Request.OffsetReferenceMode == "DESIGN_KML") explanation.WriteLine("弯折路径必须人工确认完整母线范围；整架次KML不能直接作为母线。空间候选仅供确认，不能自动改归属。");
        else explanation.WriteLine("嵌入/拟合参考线坐标与依据保存在质量验算结果.json；嵌入快照不重新读取原KML，不代表原文件当前新鲜度或生产批准。");
        explanation.WriteLine("平均值和RMS按可计算点等权；P95采用排序后(N-1)*0.95位置线性插值。母线等权均值另列。可计算点比例不代表设计里程覆盖率。");
        explanation.WriteLine($"输入母线{summary.InputLines}条，可计算{summary.ComputableLines}条；输入点{summary.InputPoints}，可计算{summary.ValidPoints}，整线端外{summary.EndOutsidePoints}，未对应{summary.UnmappedPoints}，无效坐标{summary.InvalidCoordinatePoints}，其他缺项{summary.OtherRejectedPoints}。");
        explanation.WriteLine("空数值表示未计算，不能解释为零。存在缺项时总体仅代表可计算范围，不代表整个工区完整结果。项目限值未明确，不输出超限率或合格结论。");
        explanation.WriteLine("算法版本：" + OffsetAlgorithmVersion + "；来源：" + result.Request.SourcePath + "；SHA256：" + result.Request.SourceSha256);
    }
}
