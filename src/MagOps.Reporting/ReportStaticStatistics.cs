using MagOps.Core.Analysis;
using System.Globalization;
using Microsoft.VisualBasic.FileIO;

namespace MagOps.Reporting;

internal static class ReportStaticStatistics
{
    public static IReadOnlyList<ReportStaticResult> Build(ReportInputs inputs, List<ReportSource> sources,
        List<ReportFact> facts, List<string> warnings)
    {
        var results = new List<ReportStaticResult>();
        if (inputs.StaticTrials.GroupBy(t => t.Name, StringComparer.Ordinal).Any(g => string.IsNullOrWhiteSpace(g.Key) || g.Count() != 1))
            throw new InvalidDataException("独立静态试验名称不能为空或重复。");
        foreach (var trial in inputs.StaticTrials)
        {
            if (!trial.Confirmed || string.IsNullOrWhiteSpace(trial.Conditions)) { warnings.Add("静态试验未确认或缺少设备/运行条件：" + trial.Name); continue; }
            if (trial.Kind is not ("AIR" or "GROUND" or "GPS")) throw new InvalidDataException("静态试验类型必须为AIR/GROUND/GPS。");
            // Require explicit UTC, not the workstation's local time zone.
            if (!DateTimeOffset.TryParseExact(trial.StartUtc, ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var start) ||
                !DateTimeOffset.TryParseExact(trial.EndUtc, ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var end) || end <= start)
                throw new InvalidDataException("静态试验需明确开始和结束UTC（末尾Z），结束须晚于开始。");
            if (!double.IsFinite(trial.SampleIntervalSeconds) || trial.SampleIntervalSeconds <= 0) throw new InvalidDataException("静态等效采样间隔必须为正有限数值。");
            string path = Path.GetFullPath(trial.CsvPath), hash = ReportSnapshotBuilder.Hash(path);
            using var reader = new TextFieldParser(path, System.Text.Encoding.UTF8, true);
            reader.SetDelimiters(","); reader.HasFieldsEnclosedInQuotes = true;
            var header = reader.ReadFields() ?? [];
            var columns = header.Select((h, i) => (Name: h.Trim().Trim('\uFEFF'), Index: i)).ToDictionary(p => p.Name, p => p.Index, StringComparer.Ordinal);
            foreach (string key in trial.Kind == "GPS" ? new[] { "DateUtc", "TimeUtc", "Longitude", "Latitude", "AltitudeM" } : new[] { "DateUtc", "TimeUtc", "RawMagNt" })
                if (!columns.ContainsKey(key)) throw new InvalidDataException("独立静态试验CSV缺少字段：" + key);
            string Cell(string[] row, string key) => columns.TryGetValue(key, out int i) ? row[i] : "";
            double? Number(string[] row, string key) => double.TryParse(Cell(row, key), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value) ? value : null;
            var selected = new List<(DateTimeOffset Time, double? Mag, double? Lon, double? Lat, double? Z)>();
            DateTimeOffset? previous = null;
            while (!reader.EndOfData)
            {
                var row = reader.ReadFields() ?? [];
                if (row.Length != header.Length) throw new InvalidDataException("静态试验CSV列数不一致。");
                if (!DateTimeOffset.TryParse(Cell(row, "DateUtc") + "T" + Cell(row, "TimeUtc"), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time)) throw new InvalidDataException("静态试验CSV时间无效。");
                if (time < start || time > end) continue;
                if (previous is { } prev && time < prev) throw new InvalidDataException("静态试验时间逆序，请核实来源。");
                previous = time;
                selected.Add((time, Number(row, "RawMagNt"), Number(row, "Longitude"), Number(row, "Latitude"), Number(row, "AltitudeM")));
            }
            if (ReportSnapshotBuilder.Hash(path) != hash) throw new IOException("读取静态试验时来源变化。");
            sources.Add(new("static-trial:" + trial.Name, path, hash));
            double duration = selected.Count > 0 ? (selected[^1].Time - selected[0].Time).TotalSeconds : 0;
            var bins = new SortedDictionary<long, (double Error, ReportStaticPoint Point)>();
            if (selected.Count > 0)
                foreach (var row in selected)
                {
                    double relative = (row.Time - selected[0].Time).TotalSeconds / trial.SampleIntervalSeconds;
                    long bin = (long)Math.Round(relative);
                    double error = Math.Abs(relative - bin);
                    if (error > .1 || bins.TryGetValue(bin, out var kept) && kept.Error <= error) continue;
                    bins[bin] = (error, new(row.Time, row.Mag));
                }
            var differences = new List<double>();
            if (trial.Kind != "GPS")
                foreach (long bin in bins.Keys)
                {
                    var window = Enumerable.Range(0, 5).Select(i => bins.TryGetValue(bin + i, out var value) ? value.Point.RawMagNt : null).ToArray();
                    if (window.Any(v => v is null)) continue;
                    differences.Add(MagneticNoiseMath.FourthDifference(window[0]!.Value, window[1]!.Value, window[2]!.Value, window[3]!.Value, window[4]!.Value) / 16);
                }
            double? noise = Sd(differences);
            double? plane = null, vertical = null;
            if (trial.Kind == "GPS")
            {
                var valid = selected.Where(r => r.Lon is >= -180 and <= 180 && r.Lat is >= -90 and <= 90).ToArray();
                if (valid.Length >= 2)
                {
                    var xy = valid.Select(r => ReportRouteGeometry.XY(r.Lon!.Value, r.Lat!.Value, valid[0].Lon!.Value, valid[0].Lat!.Value)).ToArray();
                    plane = Math.Sqrt(Math.Pow(Sd(xy.Select(p => p.X))!.Value, 2) + Math.Pow(Sd(xy.Select(p => p.Y))!.Value, 2));
                }
                vertical = Sd(selected.Select(r => r.Z).OfType<double>());
            }
            var result = new ReportStaticResult(trial.Name, trial.Kind, selected.Count, bins.Count, duration, differences.Count, noise,
                plane, vertical, trial.Conditions, bins.Values.Select(b => b.Point).ToArray());
            results.Add(result);
            string F(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
            if (noise is { } n) facts.Add(new("静态磁噪声参考 " + trial.Name, F(n), "nT", "REFERENCE_ONLY", "static-trial:" + trial.Name,
                $"独立{trial.Kind}试验；RawMagNt；四阶差分/16后样本标准差；等效间隔{F(trial.SampleIntervalSeconds)}秒，窗口{differences.Count}；公式分母及项目适用性须按可靠规范核实"));
            if (plane is { } p) facts.Add(new("静态GPS平面重复性 " + trial.Name, F(p), "m", "REFERENCE_ONLY", "static-trial:" + trial.Name, "独立静态窗口，sqrt(sx²+sy²)，不等同于控制点绝对精度"));
            facts.Add(new("静态试验有效覆盖 " + trial.Name, $"{selected.Count}原始点 / {bins.Count}等效点 / {F(duration)}秒", "", "REFERENCE_ONLY", "static-trial:" + trial.Name,
                "只在指定UTC窗口抽取最近实际样点；容差为等效间隔10%，不插值、不跨缺失采样格计算差分；覆盖与静态条件需人工核对"));
            if (vertical is { } z) facts.Add(new("静态GPS高程重复性 " + trial.Name, F(z), "m", "REFERENCE_ONLY", "static-trial:" + trial.Name, "独立静态窗口内海拔高程样本标准差"));
            if (selected.Count < 2 || trial.Kind != "GPS" && noise is null) warnings.Add("静态试验有效数据或差分窗口不足：" + trial.Name);
            if (trial.Kind != "GPS" && (duration < 7200 || Math.Abs(trial.SampleIntervalSeconds - .5) > 1e-9))
                warnings.Add("静态试验时长/等效间隔与历史模板的2小时、0.5秒条件不同，须按本项目规范复核：" + trial.Name);
        }
        return results;
    }
    private static double? Sd(IEnumerable<double> source) => MagneticNoiseMath.SampleStandardDeviation(source);
}
