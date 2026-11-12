using System.Globalization;
using System.Text;

namespace MagOps.Reporting;

/// <summary>Human-readable report tables; reuse calculated results without changing their scope.</summary>
internal static class ReportDeliveryTables
{
    internal static void Csv(string directory, string name, string header, IEnumerable<object?[]> rows)
    {
        string Cell(object? value) => "\"" + (Convert.ToString(value, CultureInfo.InvariantCulture) ?? "").Replace("\"", "\"\"") + "\"";
        File.WriteAllLines(Path.Combine(directory, name), new[] { header }.Concat(rows.Select(r => string.Join(",", r.Select(Cell)))), new UTF8Encoding(true));
    }

    public static void WriteReference(string directory, QualityCheckResult result)
    {
        QualityCheckService.WriteOffsetLines(directory, result);
        foreach (var item in new[] { ("STATIC", "独立验算静态噪声统计.csv"), ("OFFSET", "独立验算偏航距统计.csv"), ("HEIGHT", "独立验算飞高偏差统计.csv"), ("AGL_ESTIMATE", "独立验算实际飞高估计统计.csv") })
        {
            if (!result.Metrics.Any(m => m.Metric == item.Item1)) continue;
            var metric = result.Metrics.Single(m => m.Metric == item.Item1);
            var groups = result.Groups.Where(g => g.Metric == item.Item1).ToArray();
            var rows = new List<object?[]> { new object?[] { "本次选中记录", "全部", metric.Value, null, null, metric.Unit, metric.ValidCount, metric.RejectedCount, metric.Status, metric.Formula + "；" + metric.Note, result.Request.SourcePath, result.Request.SourceSha256 } };
            rows.AddRange(groups.Select(g => new object?[] { g.GroupKind == "MOTHER" ? "母测线" : "采集段", g.Group, g.Value, g.Minimum, g.Maximum, g.Unit, g.ValidCount, g.RejectedCount, g.Status, g.Method, result.Request.SourcePath, result.Request.SourceSha256 }));
            Csv(directory, item.Item1 == "OFFSET" && result.Request.OffsetReferenceMode == "ACTUAL_FIT" ? item.Item2.Replace("偏航距", "实测拟合偏离") : item.Item2, "统计范围,分组名称,计算参考值,最小值,最大值,单位,有效数量,剔除数量,计算状态,计算说明,来源文件,来源SHA256", rows);
        }
    }

    public static void Write(string directory, ReportSnapshot snapshot)
    {
        if (snapshot.QualityReference is { } reference) WriteReference(directory, reference.Result);
        var trials = snapshot.Inputs.StaticTrials.Select(t => (Input: t, Result: snapshot.StaticTrials.SingleOrDefault(r => r.Name == t.Name))).ToArray();
        string Kind(string kind) => kind switch { "AIR" => "航空磁力仪", "GROUND" => "地面站", "GPS" => "GPS静态定位", _ => kind };
        Csv(directory, "静态磁噪声报告统计.csv", "试验名称,试验类型,选定开始UTC,选定结束UTC,等效采样间隔_秒,原始点数,等效点数,观测时长_秒,有效差分窗口数,静态噪声Sn参考值_nT,状态,设备及运行条件,计算说明,来源文件",
            trials.Where(t => t.Input.Kind != "GPS").Select(t => new object?[] { t.Input.Name, Kind(t.Input.Kind), t.Input.StartUtc, t.Input.EndUtc, t.Input.SampleIntervalSeconds, t.Result?.InputPoints, t.Result?.ResampledPoints, t.Result?.DurationSeconds, t.Result?.NoiseWindows, t.Result?.NoiseNt,
                t.Result is null ? "未计算：试验未确认或条件缺失" : t.Result.NoiseNt is null ? "有效窗口不足" : "计算参考", t.Input.Conditions, "原始磁通道；sd(D4/16)；按指定UTC窗口抽取实际样点，不插值；适用规范及试验时长须核对", t.Input.CsvPath }));
        Csv(directory, "静态定位报告统计.csv", "试验名称,选定开始UTC,选定结束UTC,记录采样间隔_秒,原始点数,观测时长_秒,平面重复性参考值_米,高程重复性参考值_米,状态,设备及运行条件,计算说明,来源文件",
            trials.Where(t => t.Input.Kind == "GPS").Select(t => new object?[] { t.Input.Name, t.Input.StartUtc, t.Input.EndUtc, t.Input.SampleIntervalSeconds, t.Result?.InputPoints, t.Result?.DurationSeconds, t.Result?.PlaneRepeatabilityM, t.Result?.VerticalRepeatabilityM,
                t.Result is null ? "未计算：试验未确认或条件缺失" : "计算参考；缺值表示有效数据不足", t.Input.Conditions, "平面sqrt(sx²+sy²)，高程样本标准差；重复性不等同绝对精度", t.Input.CsvPath }));
        if (snapshot.Survey is not { } survey) return;
        Csv(directory, "正式母测线报告统计.csv", "母测线身份,母测线号,测点数,偏航有效点数,平均偏航距_米,最大偏航距_米,高程偏差有效点数,平均有符号高程偏差_米,最大绝对高程偏差_米,实际飞高有效点数,平均实际飞高_米,最小实际飞高_米,最大实际飞高_米,动态噪声Sv参考值_nT,有效噪声窗口数,状态,范围说明",
            survey.Mothers.Where(m => m.Role == "SURVEY").Select(m => new object?[] { m.MotherId, m.Name, m.Points, m.ValidOffsetPoints, m.MeanOffsetM, m.MaxOffsetM, m.HeightDeviationPoints, m.MeanHeightDeviationM, m.MaxAbsHeightDeviationM, m.HeightPoints, m.MeanAboveGroundM, m.MinAboveGroundM, m.MaxAboveGroundM, m.DynamicNoiseNt, m.NoiseWindows,
                "计算参考；空值不代表零，按对应有效点数判断覆盖", "仅已映射并纳入正式选择的SURVEY母线；实际飞高=设计AGL+实测海拔-KML海拔；不自动判合格" }));
        Csv(directory, "重复线报告统计.csv", "配对名称,是否参与汇总,人工依据或排除理由,有效点对数,有效匹配长度_千米,差值均方根_nT,精度参考值_nT,计算说明",
            survey.Repeats.Select(r => new object?[] { r.Name, r.Excluded ? "已排除" : "参与", r.Reason, r.Pairs, r.MatchedLengthKm, r.RmsDifferenceNt, r.PrecisionNt, "共同覆盖原始磁通道；精度sqrt(Σ差值²/(2N))；公式适用性待核，不自动判合格" }));
        Csv(directory, "重复线报告汇总.csv", "统计项,计算参考值,单位,状态,计算说明",
            snapshot.Facts.Where(f => f.Id.StartsWith("重复线精度参考 ", StringComparison.Ordinal) || f.Id is "有效检查长度" or "有效检查率参考")
                .Select(f => new object?[] { f.Id, f.Value, f.Unit, f.Status, f.Method }));
    }
}
