using MagOps.Core.Analysis;

namespace MagOps.Reporting;

/// <summary>Read-only statistics over explicitly mapped formal CSV records. Does not select production data.</summary>
public static class ReportSurveyStatistics
{
    public static ReportSurveyResult Build(ReportInputs inputs, IReadOnlyList<ReportSurveyPoint> input,
        List<ReportSource> sources, List<ReportFact> facts, List<string> warnings)
    {
        foreach (double parameter in new[] { inputs.MaximumTimeGapSeconds, inputs.MaximumSpatialGapM,
                     inputs.KmlMatchToleranceM, inputs.RepeatMatchToleranceM, inputs.GradientLimitNtKm })
            if (!double.IsFinite(parameter) || parameter <= 0) throw new InvalidDataException("报告质量参数必须为正的有限数值。");
        if (inputs.SurveyTasks.GroupBy(t => t.TaskId, StringComparer.Ordinal).Any(g => string.IsNullOrWhiteSpace(g.Key) || g.Count() != 1))
            throw new InvalidDataException("母测线映射中的任务ID不能为空或重复。");
        var tasks = new Dictionary<string, (ReportSurveyTask Mapping, ReportRouteGeometry Route, ReportRouteGeometry Direction)>();
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var routes = new Dictionary<(string Path, string Placemark), ReportRouteGeometry>();
        string SourceHash(string path)
        {
            path = Path.GetFullPath(path);
            if (!hashes.TryGetValue(path, out string? hash)) hashes.Add(path, hash = ReportSnapshotBuilder.Hash(path));
            return hash;
        }
        foreach (var task in inputs.SurveyTasks)
        {
            if (task.Role is not ("SURVEY" or "CHECK" or "TIE" or "EXCLUDE")) throw new InvalidDataException("测线用途仅支持SURVEY/CHECK/TIE/EXCLUDE。");
            if (!task.Confirmed || string.IsNullOrWhiteSpace(task.Evidence)) { warnings.Add("母线映射未确认或缺少依据：" + task.TaskId); continue; }
            if (task.Role == "EXCLUDE") continue;
            if (string.IsNullOrWhiteSpace(task.MotherId)) throw new InvalidDataException("正式任务必须有明确母线稳定ID：" + task.TaskId);
            if (task.DesignAglM is { } agl && (!double.IsFinite(agl) || agl < 0)) throw new InvalidDataException("设计离地飞高必须为非负有限数值。");
            foreach (var design in task.DesignSources)
            {
                if (!SourceHash(design.Path).Equals(design.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("引用的设计来源已变化，请重新引用并确认母线映射：" + task.TaskId);
                if (!sources.Contains(design)) sources.Add(design);
            }
            ReportRouteGeometry route;
            ReportRouteGeometry? designGeometry = null;
            if (task.DesignStartLon is { } sx && task.DesignStartLat is { } sy && task.DesignEndLon is { } ex && task.DesignEndLat is { } ey)
            {
                if (!double.IsFinite(sx) || !double.IsFinite(sy) || !double.IsFinite(ex) || !double.IsFinite(ey) || sx is < -180 or > 180 || ex is < -180 or > 180 || sy is < -90 or > 90 || ey is < -90 or > 90 ||
                    ReportRouteGeometry.Distance(sx, sy, ex, ey) < .01) throw new InvalidDataException("引用设计的测线端点无效。");
                designGeometry = new() { Vertices = [new(sx, sy, null), new(ex, ey, null)], AltitudeMode = "design-geometry-only" };
            }
            if (string.IsNullOrWhiteSpace(task.KmlPath))
            {
                if (designGeometry is null)
                { warnings.Add("缺少已确认设计几何或参考KML，未统计母线任务：" + task.TaskId); continue; }
                route = designGeometry;
            }
            else
            {
                string path = Path.GetFullPath(task.KmlPath), hash = SourceHash(path);
                var key = (path, task.Placemark);
                if (!routes.TryGetValue(key, out var cached))
                {
                    cached = ReportRouteGeometry.Read(path, task.Placemark);
                    if (ReportSnapshotBuilder.Hash(path) != hash) throw new IOException("读取过程中KML变化。");
                    routes.Add(key, cached);
                }
                route = cached;
                if (!sources.Any(s => s.Role == "survey-kml" && s.Path == path)) sources.Add(new("survey-kml", path, hash));
            }
            if (designGeometry is not null && !ReferenceEquals(route, designGeometry)) route = route.Restrict(designGeometry);
            if (designGeometry is null)
            {
                var start = route.Vertices[0]; var end = route.Vertices[^1];
                var (x, y) = ReportRouteGeometry.XY(end.Lon, end.Lat, start.Lon, start.Lat);
                double length = Math.Sqrt(x * x + y * y);
                if (length < .01 || route.Vertices.Any(v =>
                {
                    var (vx, vy) = ReportRouteGeometry.XY(v.Lon, v.Lat, start.Lon, start.Lat);
                    return Math.Abs(vx * y - vy * x) / length > 1;
                })) throw new InvalidDataException("KML包含弯折或连接段，不能直接作为母线方向；请先引用正式设计几何：" + task.TaskId);
            }
            tasks.Add(task.TaskId, (task, route, designGeometry ?? route));
        }
        int omitted = input.Count(p => !tasks.ContainsKey(p.TaskId));
        if (omitted > 0) warnings.Add($"有{omitted}个CSV点未进入正式母线统计（未映射、未确认、排除或缺少KML）；任务/Pass附表仍保留原CSV诊断范围。");
        if (tasks.Count == 0)
        {
            facts.Add(new("母测线条数", "", "条", "UNAVAILABLE", "", "请在母测线资料中确认稳定身份、用途和KML方向依据；不按名称推断母线"));
            if (inputs.RepeatPairs.Length > 0) throw new InvalidDataException("重复线配对尚无可用正式任务映射。");
            return new([], [], []);
        }
        // The first confirmed mapping of a mother defines its positive direction; never use flight order.
        var axes = tasks.Values.GroupBy(t => t.Mapping.MotherId, StringComparer.Ordinal).ToDictionary(g => g.Key, g =>
        {
            var vertices = g.First().Direction.Vertices;
            var start = vertices[0]; var end = vertices[^1];
            var (x, y) = ReportRouteGeometry.XY(end.Lon, end.Lat, start.Lon, start.Lat);
            double length = Math.Sqrt(x * x + y * y);
            if (length < .01) throw new InvalidDataException("母线方向参考起终点重合。");
            return (Start: start, X: x / length, Y: y / length);
        });
        double Along(string mother, double lon, double lat)
        {
            var a = axes[mother]; var (x, y) = ReportRouteGeometry.XY(lon, lat, a.Start.Lon, a.Start.Lat);
            return x * a.X + y * a.Y;
        }
        var fragments = new List<List<ReportSurveyPoint>>();
        var cadence = new Dictionary<string, ReportFragmentCadence>();
        int serial = 0;
        foreach (var group in input.Where(p => tasks.ContainsKey(p.TaskId)).GroupBy(p => (p.TaskId, p.Pass)))
        {
            var (mapping, route, direction) = tasks[group.Key.TaskId];
            string role = inputs.RepeatPairs.Any(p => p.CheckTask == group.Key.TaskId && p.CheckPass == group.Key.Pass) ? "CHECK" : mapping.Role;
            List<ReportSurveyPoint> current = [];
            int sign = 0;
            void Flush()
            {
                if (current.Count == 0) return;
                bool reversed = current.Count > 1 && current[^1].ChainageM < current[0].ChainageM;
                var evidence = SamplingCadence.Analyze(current.Select(p => (double?)(p.Time - current[0].Time).TotalSeconds).ToArray());
                if (reversed) current.Reverse();
                string id = (++serial).ToString(System.Globalization.CultureInfo.InvariantCulture);
                cadence.Add(id, new(id, mapping.MotherId, mapping.TaskId, group.Key.Pass, role, current.Count, evidence));
                fragments.Add(current.Select(p => p with { Fragment = id, Reversed = reversed }).ToList());
                current = []; sign = 0;
            }
            foreach (var source in group)
            {
                double along = Along(mapping.MotherId, source.Longitude, source.Latitude);
                var insideDesign = direction.Match(source.Longitude, source.Latitude, double.MaxValue) is not null;
                var hit = insideDesign ? route.Match(source.Longitude, source.Latitude, inputs.KmlMatchToleranceM) : null;
                var navigation = direction.EvaluateHorizontal(source.Longitude, source.Latitude);
                bool compatible = mapping.HeightDatumConfirmed && route.AltitudeMode == "absolute" && hit is { Height: not null } && hit.Offset <= inputs.KmlMatchToleranceM;
                double? deviation = compatible && source.AltitudeM is { } h ? h - hit!.Height!.Value : null;
                var point = source with { MotherId = mapping.MotherId, Role = role, ChainageM = along, OffsetM = navigation.Offset,
                    HeightDeviationM = deviation, AboveGroundM = deviation is { } d && mapping.DesignAglM is { } design ? design + d : null };
                if (current.Count > 0)
                {
                    var previous = current[^1];
                    double dt = (point.Time - previous.Time).TotalSeconds;
                    double distance = ReportRouteGeometry.Distance(previous.Longitude, previous.Latitude, point.Longitude, point.Latitude);
                    int nextSign = Math.Abs(along - previous.ChainageM) > .01 ? Math.Sign(along - previous.ChainageM) : 0;
                    bool sourceGap = point.SourceId.Length > 0 && (previous.SourceId != point.SourceId || point.SourceRecord != previous.SourceRecord + 1);
                    if (dt < 0 || dt > inputs.MaximumTimeGapSeconds || distance > inputs.MaximumSpatialGapM ||
                        point.CsvRow != previous.CsvRow + 1 || sourceGap || sign != 0 && nextSign != 0 && nextSign != sign) Flush();
                    else if (nextSign != 0) sign = nextSign;
                }
                current.Add(point);
            }
            Flush();
        }
        var ordered = fragments.OrderBy(f => f[0].MotherId, StringComparer.Ordinal).ThenBy(f => f[0].ChainageM)
            .ThenBy(f => f[0].TaskId, StringComparer.Ordinal).ThenBy(f => f[0].Pass, StringComparer.Ordinal).ThenBy(f => f[0].CsvRow).ToArray();
        var mothers = new List<ReportMotherLine>();
        foreach (var group in ordered.GroupBy(f => (f[0].MotherId, f[0].Role)))
        {
            var rows = group.SelectMany(f => f).ToArray();
            var retainedTasks = rows.Select(p => p.TaskId).ToHashSet(StringComparer.Ordinal);
            var mappings = tasks.Values.Where(t => retainedTasks.Contains(t.Mapping.TaskId)).ToArray();
            var noise = new Moments(); double length = 0, duration = 0; int timeRejected = 0, motherIntervals = 0;
            foreach (var fragment in group)
            {
                for (int i = 1; i < fragment.Count; i++) length += ReportRouteGeometry.Distance(fragment[i - 1].Longitude, fragment[i - 1].Latitude, fragment[i].Longitude, fragment[i].Latitude);
                duration += Math.Abs((fragment[^1].Time - fragment[0].Time).TotalSeconds);
                motherIntervals += Math.Max(0, fragment.Count - 1);
                timeRejected += AddNoise(fragment, inputs.GradientLimitNtKm, noise, cadence[fragment[0].Fragment].Evidence.IntervalSeconds);
            }
            var designIntervals = mappings.Select(t => (Start: t.Direction.Vertices.Min(v => Along(group.Key.MotherId, v.Lon, v.Lat)), End: t.Direction.Vertices.Max(v => Along(group.Key.MotherId, v.Lon, v.Lat)))).ToArray();
            var covered = group.SelectMany(f => designIntervals.Select(d =>
                (Start: Math.Max(f[0].ChainageM, d.Start), End: Math.Min(f[^1].ChainageM, d.End)))).Where(i => i.End >= i.Start);
            mothers.Add(new(group.Key.MotherId, mappings[0].Mapping.MotherName, group.Key.Role, rows.Length,
                rows.Select(p => p.TaskId).Distinct().Count(), rows.Select(p => (p.TaskId, p.Pass)).Distinct().Count(), length / 1000,
                Union(covered) / 1000, Union(designIntervals) / 1000,
                Mean(rows.Select(p => p.OffsetM)), Max(rows.Select(p => p.OffsetM)), Mean(rows.Select(p => p.HeightDeviationM)),
                Mean(rows.Select(p => p.AboveGroundM)), rows.Count(p => p.AboveGroundM is not null), noise.Count,
                noise.Sd is { } sd ? sd / Math.Sqrt(70) : null)
            {
                ValidOffsetPoints = rows.Count(p => p.OffsetM is not null),
                HeightDeviationPoints = rows.Count(p => p.HeightDeviationM is not null),
                MinHeightDeviationM = Min(rows.Select(p => p.HeightDeviationM)),
                MaxHeightDeviationM = Max(rows.Select(p => p.HeightDeviationM)),
                MaxAbsHeightDeviationM = Max(rows.Select(p => p.HeightDeviationM is { } d ? (double?)Math.Abs(d) : null)),
                MinAboveGroundM = Min(rows.Select(p => p.AboveGroundM)),
                MaxAboveGroundM = Max(rows.Select(p => p.AboveGroundM)),
                TimeRejectedNoiseWindows = timeRejected,
                UnknownCadenceFragments = group.Count(f => cadence[f[0].Fragment].Evidence.IntervalSeconds is null),
                DurationSeconds = duration,
                AverageSpeedMps = duration > 0 ? length / duration : null,
                EffectiveSamplingRateHz = duration > 0 ? motherIntervals / duration : null,
                MeanPointSpacingM = motherIntervals > 0 ? length / motherIntervals : null
            });
        }
        var points = ordered.SelectMany(f => f).ToArray();
        var repeats = Repeat(inputs, points, tasks.ToDictionary(t => t.Key, t => t.Value.Mapping));
        void Fact(string name, string value, string unit, string method, string status = "AVAILABLE") => facts.Add(new(name, value, unit, status, "production-csv;survey-kml", method));
        Fact("报告磁通道", "RawMagNt", "nT", "原始磁通道；不以rigai/dTgz或调平值补缺");
        Fact("母测线条数", mothers.Where(m => m.Role == "SURVEY").Select(m => m.MotherId).Distinct().Count().ToString(), "条", "已确认母线稳定ID；检查线及联络线单列");
        Fact("正式测线点数", points.Length.ToString(), "点", "已确认SURVEY/CHECK/TIE任务；完整有效点集，无抽稀");
        Fact("母测线有效完成长度", F(mothers.Where(m => m.Role == "SURVEY").Sum(m => m.CoveredLengthKm)), "km", "沿母线轴向有效连续区间并集，多个Pass不重复累加；仅适用于直线母测线");
        Fact("检查线采集长度", F(mothers.Where(m => m.Role == "CHECK").Sum(m => m.TrackLengthKm)), "km", "检查线连续采集轨迹，排除前采集量");
        Fact("联络线采集长度", F(mothers.Where(m => m.Role == "TIE").Sum(m => m.TrackLengthKm)), "km", "明确联络测线任务；不含连接航段或几何切割辅助线");
        Fact("母线反向片段", ordered.Count(f => f[0].Reversed).ToString(), "段", "以首条确认KML起终点为母线正向；整段反转所有字段，不改原始时间和源记录");
        Fact("原始磁有效点", points.Count(p => p.RawMagNt is not null).ToString(), "点", "缺失原始磁值不补值；四阶差分窗口不得跨缺失点或连续片段边界");
        var surveyPoints = points.Where(p => p.Role == "SURVEY").ToArray();
        int heightCount = surveyPoints.Count(p => p.AboveGroundM is not null);
        int deviationCount = surveyPoints.Count(p => p.HeightDeviationM is not null);
        int offsetCount = surveyPoints.Count(p => p.OffsetM is not null);
        Fact("实际飞高覆盖", $"{heightCount}/{surveyPoints.Length}", "点", "仅正式SURVEY有效点为分母；基准已确认的absolute KML航线；匹配位置插值KML海拔，设计AGL＋实测海拔－KML海拔");
        if (Mean(surveyPoints.Select(p => p.AboveGroundM)) is { } heightMean) Fact("正式测线实际飞高均值", F(heightMean), "m", "仅有效SURVEY飞高点算术均值；相对设计地形面的估计，不代表侧向偏移处正下方真实AGL");
        if (Max(surveyPoints.Select(p => p.AboveGroundM)) is { } heightMax) Fact("正式测线实际飞高最大值", F(heightMax), "m", "仅有效SURVEY飞高点最大值；相对设计地形面的估计");
        Fact("正式测线飞高偏差覆盖", $"{deviationCount}/{surveyPoints.Length}", "点", "仅具有有效实测海拔与匹配KML绝对高程的SURVEY点；分母为正式SURVEY点集");
        Fact("正式测线偏航覆盖", $"{offsetCount}/{surveyPoints.Length}", "点", "仅有效设计几何匹配的SURVEY点；分母为正式SURVEY点集");
        if (Mean(surveyPoints.Select(p => p.OffsetM)) is { } offsetMean) Fact("正式测线平均偏航距", F(offsetMean), "m", "仅有效SURVEY偏航距点算术均值");
        if (Max(surveyPoints.Select(p => p.OffsetM)) is { } offsetMax) Fact("正式测线最大偏航距", F(offsetMax), "m", "仅有效SURVEY偏航距点最大值");
        if (Mean(surveyPoints.Select(p => p.HeightDeviationM)) is { } deviationMean) Fact("正式测线平均高程偏差", F(deviationMean), "m", "仅有效SURVEY点的有符号（实测海拔－KML绝对高程）算术均值；不代表高程基准结论");
        if (Max(surveyPoints.Select(p => p.HeightDeviationM is { } d ? (double?)Math.Abs(d) : null)) is { } deviationMaxAbs) Fact("正式测线最大绝对高程偏差", F(deviationMaxAbs), "m", "仅有效SURVEY高程偏差点的绝对值最大值；不代表高程基准结论");
        if (heightCount < surveyPoints.Length) warnings.Add("部分正式测线点缺少可靠实际飞高；已保留缺项及SURVEY覆盖率，不用激光、海拔或设计值直接替代。");
        double seconds = ordered.Sum(f => Math.Abs((f[^1].Time - f[0].Time).TotalSeconds));
        int intervals = ordered.Sum(f => Math.Max(0, f.Count - 1));
        double metres = mothers.Sum(m => m.TrackLengthKm) * 1000;
        if (seconds > 0)
        {
            Fact("连续作业平均速度参考", F(metres / seconds), "m/s", "连续片段水平轨迹总长/原采集时序时长；不跨断口，方向反转不改变时长", "REFERENCE_ONLY");
            Fact("有效采样率参考", F(intervals / seconds), "Hz", "连续片段样点间隔数/总时长；同秒多记录不逐点倒数；为有效平均率，不证明等间隔采样", "REFERENCE_ONLY");
        }
        if (intervals > 0) Fact("平均点距", F(metres / intervals), "m", "连续片段内相邻完整有效点的水平距离均值，不跨断口");
        var allNoise = new Moments(); foreach (var f in ordered.Where(f => f[0].Role == "SURVEY"))
            AddNoise(f, inputs.GradientLimitNtKm, allNoise, cadence[f[0].Fragment].Evidence.IntervalSeconds);
        if (allNoise.Sd is { } allSd) Fact("动态磁噪声参考（窗口汇总）", F(allSd / Math.Sqrt(70)), "nT",
            $"RawMagNt四阶差分样本标准差/√70；正式SURVEY母线有效窗口汇总N={allNoise.Count}；梯度≤{F(inputs.GradientLimitNtKm)}nT/km；仅片段内、已证实节拍相符的五点窗口计算；CHECK/TIE不参与，公式及项目适用性须按规范核对", "REFERENCE_ONLY");
        else warnings.Add("动态噪声有效四阶差分窗口不足，未生成数值。");
        var lineNoise = mothers.Where(m => m.Role == "SURVEY" && m.DynamicNoiseNt is not null).Select(m => m.DynamicNoiseNt!.Value).ToArray();
        Fact("动态磁噪声逐母线有效覆盖", $"{lineNoise.Length}/{mothers.Count(m => m.Role == "SURVEY")}", "条", "分母为正式SURVEY母线；逐母线值仅在有效窗口足够时生成");
        if (lineNoise.Length > 0)
        {
            Fact("动态磁噪声逐母线最小值", F(lineNoise.Min()), "nT", "正式SURVEY各母线动态噪声参考值的最小值；每母线先汇总，不等同窗口汇总");
            Fact("动态磁噪声逐母线最大值", F(lineNoise.Max()), "nT", "正式SURVEY各母线动态噪声参考值的最大值；每母线先汇总，不等同窗口汇总");
            Fact("动态磁噪声逐母线算术平均", F(lineNoise.Average()), "nT", "正式SURVEY有效母线动态噪声参考值的非加权算术平均；不等同窗口汇总");
        }
        double designLength = mothers.Where(m => m.Role == "SURVEY").Sum(m => m.DesignLengthKm);
        if (designLength > 0) Fact("线长度完成率参考", F(100 * mothers.Where(m => m.Role == "SURVEY").Sum(m => m.CoveredLengthKm) / designLength), "%", "分母仅为本次映射KML的设计长度，未证明覆盖完整设计版本；不作为面积完成率", "REFERENCE_ONLY");
        var before = repeats.Where(r => r.PrecisionNt is not null).ToArray();
        var after = before.Where(r => !r.Excluded).ToArray();
        double? Total(ReportRepeatResult[] rs) => rs.Length == 0 ? null : Math.Sqrt(rs.Sum(r => r.Pairs * Math.Pow(r.PrecisionNt!.Value, 2)) / rs.Sum(r => r.Pairs));
        Fact("重复线精度参考 排除前", Total(before) is { } precisionBefore ? F(precisionBefore) : "", "nT",
            before.Length > 0 ? "共同覆盖内原始磁差值平方和/(2N)开方；公式与采样适用性待规范核对" : "没有具备有效精度值的重复线配对；留空不代表零",
            before.Length > 0 ? "REFERENCE_ONLY" : "UNAVAILABLE");
        Fact("重复线精度参考 排除后", Total(after) is { } precisionAfter ? F(precisionAfter) : "", "nT",
            after.Length > 0 ? "共同覆盖内原始磁差值平方和/(2N)开方；整线人工排除仅作用于本次精度评价；公式与采样适用性待规范核对" : "排除后没有具备有效精度值的重复线配对；留空不代表零",
            after.Length > 0 ? "REFERENCE_ONLY" : "UNAVAILABLE");
        Fact("有效检查长度", F(after.Sum(r => r.MatchedLengthKm)), "km", "排除后实际成功配对连续检查段长度；不同人工配对可能重叠，应人工核对");
        if (mothers.Where(m => m.Role == "SURVEY").Sum(m => m.CoveredLengthKm) is > 0 and var completed)
            Fact("有效检查率参考", F(100 * after.Sum(r => r.MatchedLengthKm) / completed), "%", "有效配对长度/正式母线有效完成长度；最低检查量及配对重复须核对，未自动判合格", "REFERENCE_ONLY");
        warnings.Add("磁噪声及重复线精度当前为计算参考，尚未完成模板公式与适用规范确认；不自动判定合格。实际完成面积留空。");
        foreach (var source in hashes)
            if (ReportSnapshotBuilder.Hash(source.Key) != source.Value) throw new IOException("母线统计过程中设计来源发生变化。");
        int unknown = cadence.Values.Count(c => c.Evidence.IntervalSeconds is null);
        int timeRejectedTotal = mothers.Sum(m => m.TimeRejectedNoiseWindows);
        Fact("动态噪声时间筛选", $"{timeRejectedTotal}", "窗", "因采样节拍未知或五点窗口时间步长不连续而排除；详见片段采样证据.csv", "REFERENCE_ONLY");
        if (unknown > 0) warnings.Add($"{unknown}个连续片段无法证明采样节拍，未用于动态磁噪声计算；完整点集仍保留。");
        return new(mothers, points, repeats) { Cadences = cadence.Values.ToArray() };
    }

    internal static double Union(IEnumerable<(double Start, double End)> input)
    {
        double total = 0, end = double.NegativeInfinity;
        foreach (var interval in input.Select(i => (Start: Math.Min(i.Start, i.End), End: Math.Max(i.Start, i.End))).OrderBy(i => i.Start))
        { total += Math.Max(0, interval.End - Math.Max(interval.Start, end)); end = Math.Max(end, interval.End); }
        return total;
    }
    private static string F(double value) => value.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
    private static double? Mean(IEnumerable<double?> values) { var a = values.OfType<double>().ToArray(); return a.Length == 0 ? null : a.Average(); }
    private static double? Min(IEnumerable<double?> values) { var a = values.OfType<double>().ToArray(); return a.Length == 0 ? null : a.Min(); }
    private static double? Max(IEnumerable<double?> values) { var a = values.OfType<double>().ToArray(); return a.Length == 0 ? null : a.Max(); }
    private static int AddNoise(IReadOnlyList<ReportSurveyPoint> points, double limit, Moments result, double? interval)
    {
        int rejected = 0;
        var t = points.Select(p => p.RawMagNt ?? double.NaN).ToArray();
        var gradients = DynamicNoiseService.PointGradients(t, points.Select(p => p.Longitude).ToArray(), points.Select(p => p.Latitude).ToArray(), "geographic");
        for (int i = 0; i + 4 < points.Count; i++)
        {
            if (interval is null || Enumerable.Range(i, 4).Any(j =>
            {
                double a = (points[j].Time - points[0].Time).TotalSeconds;
                double b = (points[j + 1].Time - points[0].Time).TotalSeconds;
                bool reversed = points[j].Reversed;
                return (reversed ? b >= a : b <= a) || !SamplingCadence.Matches(a, b, interval.Value, reversed);
            })) { rejected++; continue; }
            if (Enumerable.Range(i, 5).Any(j => !double.IsFinite(t[j]) || !double.IsFinite(gradients[j]) || gradients[j] > limit)) continue;
            result.Add(MagneticNoiseMath.FourthDifference(t[i], t[i + 1], t[i + 2], t[i + 3], t[i + 4]));
        }
        return rejected;
    }
    private static ReportRepeatResult[] Repeat(ReportInputs inputs, ReportSurveyPoint[] points, Dictionary<string, ReportSurveyTask> tasks)
    {
        if (inputs.RepeatPairs.GroupBy(p => (p.CheckTask, p.CheckPass)).Any(g => g.Count() > 1))
            throw new InvalidDataException("同一检查任务/Pass不得重复参与配对，避免重复计数。");
        var results = new List<ReportRepeatResult>();
        foreach (var pair in inputs.RepeatPairs)
        {
            if (pair.Excluded && string.IsNullOrWhiteSpace(pair.Reason)) throw new InvalidDataException("排除整条重复线必须填写理由。");
            if (string.IsNullOrWhiteSpace(pair.Name) || string.IsNullOrWhiteSpace(pair.Reason) ||
                !tasks.ContainsKey(pair.OriginalTask) || !tasks.ContainsKey(pair.CheckTask) ||
                (pair.OriginalTask == pair.CheckTask && pair.OriginalPass == pair.CheckPass)) throw new InvalidDataException("原测/检查配对缺少名称、依据或有效任务/Pass。");
            var original = points.Where(p => p.TaskId == pair.OriginalTask && p.Pass == pair.OriginalPass)
                .GroupBy(p => p.Fragment).Select(g => g.ToArray()).ToArray();
            var checks = points.Where(p => p.TaskId == pair.CheckTask && p.Pass == pair.CheckPass).ToArray();
            if (original.Length == 0 || checks.Length == 0) throw new InvalidDataException("原测/检查配对没有对应有效Pass：" + pair.Name);
            // Use the original axis for both tasks (their mother IDs may deliberately differ).
            var first = original.SelectMany(f => f).MinBy(p => p.ChainageM)!;
            var last = original.SelectMany(f => f).MaxBy(p => p.ChainageM)!;
            var (dx, dy) = ReportRouteGeometry.XY(last.Longitude, last.Latitude, first.Longitude, first.Latitude);
            double axisLength = Math.Sqrt(dx * dx + dy * dy);
            if (axisLength < .01) { results.Add(new(pair.Name, pair.Excluded, pair.Reason, 0, 0, null, null)); continue; }
            double Along(ReportSurveyPoint p) { var (x, y) = ReportRouteGeometry.XY(p.Longitude, p.Latitude, first.Longitude, first.Latitude); return (x * dx + y * dy) / axisLength; }
            var originalFragments = original.Select(f => f.OrderBy(Along).ToArray()).ToArray();
            double squares = 0, matchedLength = 0; int count = 0;
            var matches = new List<ReportRepeatPoint>();
            ReportSurveyPoint? previousMatch = null; string? previousOriginalFragment = null;
            foreach (var check in checks)
            {
                var candidates = new List<(double Mag, string Fragment)>();
                double c = Along(check);
                foreach (var fragment in originalFragments)
                {
                    int low = 0, high = fragment.Length - 1;
                    while (low < high) { int mid = (low + high) / 2; if (Along(fragment[mid]) < c) low = mid + 1; else high = mid; }
                    int right = low, left = Math.Max(0, right - 1);
                    if (Math.Abs(c - Along(fragment[right])) <= 1e-7)
                    {
                        if (right + 1 < fragment.Length && Math.Abs(Along(fragment[right + 1]) - c) <= 1e-7) continue;
                        left = right;
                    }
                    var a = fragment[left]; var b = fragment[right];
                    if (c < Along(a) - 1e-7 || c > Along(b) + 1e-7 || a.RawMagNt is null || b.RawMagNt is null) continue;
                    double span = Along(b) - Along(a), f = span <= 1e-8 ? 0 : (c - Along(a)) / span;
                    if (left == right && Math.Abs(c - Along(a)) > 1e-7) continue;
                    double lon = a.Longitude + f * (b.Longitude - a.Longitude), lat = a.Latitude + f * (b.Latitude - a.Latitude);
                    if (ReportRouteGeometry.Distance(lon, lat, check.Longitude, check.Latitude) <= inputs.RepeatMatchToleranceM)
                        candidates.Add((a.RawMagNt.Value + f * (b.RawMagNt.Value - a.RawMagNt.Value), a.Fragment));
                }
                if (candidates.Count != 1 || check.RawMagNt is not { } raw) { previousMatch = null; previousOriginalFragment = null; continue; }
                double diff = raw - candidates[0].Mag; squares += diff * diff; count++;
                matches.Add(new(check.Fragment + ":" + candidates[0].Fragment, check.CsvRow, c, candidates[0].Mag, raw));
                if (previousMatch is { } prev && prev.Fragment == check.Fragment && previousOriginalFragment == candidates[0].Fragment && Math.Abs(prev.CsvRow - check.CsvRow) == 1)
                    matchedLength += ReportRouteGeometry.Distance(prev.Longitude, prev.Latitude, check.Longitude, check.Latitude);
                previousMatch = check;
                previousOriginalFragment = candidates[0].Fragment;
            }
            results.Add(new(pair.Name, pair.Excluded, pair.Reason, count, matchedLength / 1000,
                count == 0 ? null : Math.Sqrt(squares / count), count == 0 ? null : Math.Sqrt(squares / (2 * count))) { Matches = matches });
        }
        return results.ToArray();
    }
    private sealed class Moments
    {
        public int Count { get; private set; }
        private double _mean, _m2;
        public double? Sd => Count < 2 ? null : Math.Sqrt(Math.Max(0, _m2 / (Count - 1)));
        public void Add(double x) { if (!double.IsFinite(x)) return; Count++; double d = x - _mean; _mean += d / Count; _m2 += d * (x - _mean); }
    }
}
