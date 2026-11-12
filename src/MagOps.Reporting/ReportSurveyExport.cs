using System.Globalization;
using System.Text;
using DocumentFormat.OpenXml.Wordprocessing;

namespace MagOps.Reporting;

internal static class ReportSurveyExport
{
    public static void FillBoundary(SdtContentBlock content, ReportSnapshot snapshot)
    {
        content.RemoveAllChildren();
        if (snapshot.BoundaryVertices.Count == 0) { content.Append(ReportTemplate.P("设计边界、拐点及设计面积尚未提供明确来源；实际完成面积留空。")); return; }
        content.Append(ReportTemplate.P("设计边界依据：" + snapshot.Inputs.BoundaryEvidence + "；经纬度为KML地理坐标，投影面积采用" + snapshot.Inputs.BoundaryDatum + "，中央经线" + snapshot.Inputs.BoundaryCentralMeridian + "°。下列前20个拐点，完整经纬度/投影坐标及区域/孔洞身份见设计边界拐点.csv。"));
        var values = snapshot.BoundaryVertices.Take(20).Select(p => new[] { $"{p.Polygon}/{p.Ring}/{p.Index}",
            p.Longitude.ToString("0.########", CultureInfo.InvariantCulture), p.Latitude.ToString("0.########", CultureInfo.InvariantCulture), p.Ring == 0 ? "外环" : "孔洞" }).ToArray();
        var table = ReportTemplate.Table(["区域/环/点号", "经度°", "纬度°", "用途"], values[0]);
        var template = table.Elements<TableRow>().Last();
        foreach (var value in values.Skip(1))
        {
            var row = (TableRow)template.CloneNode(true);
            foreach (var cell in row.Elements<TableCell>().Zip(value))
            { cell.First.RemoveAllChildren<Paragraph>(); cell.First.Append(ReportTemplate.P(cell.Second)); }
            table.Append(row);
        }
        // Word merges adjacent tables even across content-control boundaries. Keep headers independent.
        content.Append(table, ReportTemplate.P(""));
    }

    public static void Write(string directory, ReportSnapshot snapshot)
    {
        ReportDeliveryTables.Write(directory, snapshot);
        if (snapshot.QualityReference is { } reference) QualityCheckService.WriteDynamicLines(directory, reference.Result);
        if (snapshot.Survey is not { } survey) return;
        void Csv(string name, string header, IEnumerable<object?[]> rows)
        {
            string Cell(object? value) => "\"" + (Convert.ToString(value, CultureInfo.InvariantCulture) ?? "").Replace("\"", "\"\"") + "\"";
            File.WriteAllLines(Path.Combine(directory, name), new[] { header }.Concat(rows.Select(r => string.Join(",", r.Select(Cell)))), new UTF8Encoding(true));
        }
        Csv("母测线统计.csv", "MotherId,Name,Role,Points,Tasks,Passes,TrackLengthKm,CoveredLengthKm,DesignLengthKm,MeanOffsetM,MaxOffsetM,ValidOffsetPoints,MeanHeightDeviationM,MinHeightDeviationM,MaxHeightDeviationM,MaxAbsHeightDeviationM,HeightDeviationPoints,MeanAboveGroundM,MinAboveGroundM,MaxAboveGroundM,HeightPoints,NoiseWindows,动态噪声Sv参考值_nT,TimeRejectedNoiseWindows,UnknownCadenceFragments,DurationSeconds,AverageSpeedReferenceMps,EffectiveSamplingRateReferenceHz,MeanPointSpacingM",
            survey.Mothers.Select(m => new object?[] { m.MotherId, m.Name, m.Role, m.Points, m.Tasks, m.Passes, m.TrackLengthKm,
                m.CoveredLengthKm, m.DesignLengthKm, m.MeanOffsetM, m.MaxOffsetM, m.ValidOffsetPoints, m.MeanHeightDeviationM, m.MinHeightDeviationM, m.MaxHeightDeviationM, m.MaxAbsHeightDeviationM, m.HeightDeviationPoints, m.MeanAboveGroundM, m.MinAboveGroundM, m.MaxAboveGroundM, m.HeightPoints, m.NoiseWindows, m.DynamicNoiseNt, m.TimeRejectedNoiseWindows, m.UnknownCadenceFragments,
                m.DurationSeconds, m.AverageSpeedMps, m.EffectiveSamplingRateHz, m.MeanPointSpacingM }));
        Csv("片段采样证据.csv", "Fragment,MotherId,TaskId,Pass,Role,Points,IntervalSeconds,Pairs,MatchingPairs,DuplicatePairs,ReversePairs,InvalidPairs,Note",
            survey.Cadences.Select(c => new object?[] { c.Fragment, c.MotherId, c.TaskId, c.Pass, c.Role, c.Points,
                c.Evidence.IntervalSeconds, c.Evidence.Pairs, c.Evidence.MatchingPairs, c.Evidence.DuplicatePairs, c.Evidence.ReversePairs, c.Evidence.InvalidPairs, c.Evidence.Note }));
        Csv("母测线有序点集.csv", "MotherId,Role,Fragment,Reversed,TaskId,Pass,CsvRow,SourceId,SourceRecord,Utc,Longitude,Latitude,AltitudeM,RawMagNt,ChainageM,OffsetM,HeightDeviationM,AboveGroundM",
            survey.Points.Select(p => new object?[] { p.MotherId, p.Role, p.Fragment, p.Reversed, p.TaskId, p.Pass, p.CsvRow, p.SourceId,
                p.SourceRecord, p.Time.ToString("O", CultureInfo.InvariantCulture), p.Longitude, p.Latitude, p.AltitudeM, p.RawMagNt,
                p.ChainageM, p.OffsetM, p.HeightDeviationM, p.AboveGroundM }));
        Csv("重复线精度参考.csv", "Name,Excluded,Reason,Pairs,MatchedLengthKm,RmsDifferenceNt,PrecisionReferenceNt",
            survey.Repeats.Select(p => new object?[] { p.Name, p.Excluded, p.Reason, p.Pairs, p.MatchedLengthKm, p.RmsDifferenceNt, p.PrecisionNt }));
        Csv("重复线配对点.csv", "Name,Excluded,Fragment,CheckCsvRow,ChainageM,OriginalRawMagNt,CheckRawMagNt,DifferenceNt",
            survey.Repeats.SelectMany(r => r.Matches.Select(p => new object?[] { r.Name, r.Excluded, p.Fragment, p.CheckCsvRow, p.ChainageM, p.OriginalMagNt, p.CheckMagNt, p.CheckMagNt - p.OriginalMagNt })));
        Csv("独立静态试验.csv", "Name,Kind,InputPoints,ResampledPoints,DurationSeconds,NoiseWindows,NoiseReferenceNt,PlaneRepeatabilityM,VerticalRepeatabilityM,Conditions",
            snapshot.StaticTrials.Select(t => new object?[] { t.Name, t.Kind, t.InputPoints, t.ResampledPoints, t.DurationSeconds, t.NoiseWindows, t.NoiseNt, t.PlaneRepeatabilityM, t.VerticalRepeatabilityM, t.Conditions }));
        Csv("独立静态曲线.csv", "Name,Kind,Utc,RawMagNt",
            snapshot.StaticTrials.SelectMany(t => t.Curve.Select(p => new object?[] { t.Name, t.Kind, p.Time.ToString("O", CultureInfo.InvariantCulture), p.RawMagNt })));
        Csv("报告人工补录.csv", "Category,Name,Value,Unit,Evidence,SourcePath,Confirmed",
            snapshot.Inputs.Supplements.Select(p => new object?[] { p.Category, p.Name, p.Value, p.Unit, p.Evidence, p.SourcePath, p.Confirmed }));
        Csv("设计边界拐点.csv", "Polygon,Ring,Index,Longitude,Latitude,Easting,Northing",
            snapshot.BoundaryVertices.Select(p => new object?[] { p.Polygon, p.Ring, p.Index, p.Longitude, p.Latitude, p.Easting, p.Northing }));
    }

    public static void Fill(SdtContentBlock content, ReportSnapshot snapshot)
    {
        content.RemoveAllChildren();
        Table AddTable(string[] header, IEnumerable<string[]> rows)
        {
            var values = rows.ToArray();
            var table = ReportTemplate.Table(header, values.Length > 0 ? values[0] : Enumerable.Repeat("", header.Length).ToArray());
            var template = table.Elements<TableRow>().Last();
            foreach (var value in values.Skip(1))
            {
                var row = (TableRow)template.CloneNode(true);
                foreach (var pair in row.Elements<TableCell>().Zip(value))
                { pair.First.RemoveAllChildren<Paragraph>(); pair.First.Append(ReportTemplate.P(pair.Second)); }
                table.Append(row);
            }
            return table;
        }
        string Number(double? x) => x?.ToString("0.###", CultureInfo.InvariantCulture) ?? "缺项";
        if (snapshot.QualityReference is { Result.DynamicLines.Length: > 0 } reference)
        {
            content.Append(ReportTemplate.P("独立验算逐母线动态噪声（REFERENCE_ONLY）：按输入首次出现顺序排列，完整表及来源见“动态噪声逐线统计.csv”；空值表示数据不足，不表示零。独立参考不替代正式测线质量统计。"));
            foreach (var batch in reference.Result.DynamicLines.Chunk(25))
            {
                var table = ReportTemplate.Table(["序号 / 母线", "Sv nT", "有效窗口", "状态"], ["", "", "", ""]);
                var template = table.Elements<TableRow>().Last();
                foreach (var line in batch)
                {
                    var row = (TableRow)template.CloneNode(true);
                    string[] values = [$"{line.LineOrder} / {line.MotherId}", line.SvNt?.ToString("0.######", CultureInfo.InvariantCulture) ?? "缺项",
                        line.ValidWindows.ToString(CultureInfo.InvariantCulture), line.Status == "REFERENCE_ONLY" ? "独立参考" : "不足/缺项"];
                    foreach (var pair in row.Elements<TableCell>().Zip(values))
                    { pair.First.RemoveAllChildren<Paragraph>(); pair.First.Append(ReportTemplate.P(pair.Second)); }
                    table.Append(row);
                }
                template.Remove(); content.Append(table, ReportTemplate.P(""));
            }
        }
        if (snapshot.StaticTrials.Count > 0)
        {
            content.Append(ReportTemplate.P("独立静态试验参考值（公式及项目适用条件需按规范核实，不自动判合格）；完整UTC窗口、采样、条件和来源见同目录“静态磁噪声报告统计.csv”及“静态定位报告统计.csv”。"));
            content.Append(AddTable(["试验名称", "类型", "采样间隔秒", "原始/等效点", "时长秒", "有效窗口", "噪声/重复性参考"], snapshot.StaticTrials.Select(t => new[]
            { t.Name, t.Kind, "见附表", $"{t.InputPoints}/{t.ResampledPoints}", Number(t.DurationSeconds), t.NoiseWindows.ToString(CultureInfo.InvariantCulture),
                t.Kind == "GPS" ? $"平面{Number(t.PlaneRepeatabilityM)}m；高程{Number(t.VerticalRepeatabilityM)}m" : Number(t.NoiseNt) + " nT" })));
        }
        if (snapshot.Survey is not { Mothers.Count: > 0 } survey)
        { content.Append(ReportTemplate.P("母测线映射尚未完整确认，缺少正式母线质量和工作量依据。")); return; }
        content.Append(ReportTemplate.P("母测线按确认身份归并，反向子线统一方向；断口与Pass分别保留。下表列示前20条；全部SURVEY统计见同目录“正式母测线报告统计.csv”，重复线配对及排除前后汇总见“重复线报告统计.csv”和“重复线报告汇总.csv”。"));
        content.Append(AddTable(["正式母测线（SURVEY）", "有效点数", "完成长度km", "平均/最大偏航距m", "高程偏差均值m", "实际飞高均值m", "动态噪声Sv nT"],
            survey.Mothers.Where(m => m.Role == "SURVEY").Take(20).Select(m => new[] { (m.Name.Length > 0 ? m.Name : m.MotherId) + " / " + m.Role,
                m.Points.ToString(CultureInfo.InvariantCulture), Number(m.CoveredLengthKm), Number(m.MeanOffsetM) + " / " + Number(m.MaxOffsetM), Number(m.MeanHeightDeviationM), Number(m.MeanAboveGroundM), Number(m.DynamicNoiseNt) })));
        if (survey.Repeats.Count > 0)
        {
            content.Append(ReportTemplate.P("重复线计算参考（原始磁通道；共同覆盖内配对；精度公式与适用规范仍待核对，不自动判合格）"));
            content.Append(AddTable(["配对", "点对数", "匹配长度km", "差值RMS nT", "精度参考nT", "汇总状态及理由"], survey.Repeats.Take(20)
                .Select(p => new[] { p.Name, p.Pairs.ToString(CultureInfo.InvariantCulture), Number(p.MatchedLengthKm), Number(p.RmsDifferenceNt), Number(p.PrecisionNt), p.Excluded ? "已排除：" + p.Reason : "参与汇总：" + p.Reason })));
        }
        content.Append(ReportTemplate.P("实际飞高＝对应设计AGL＋实测海拔－KML航线海拔；只对高度模式、基准及位置匹配均有效的点计算。实际完成面积留空。"));
    }
}
