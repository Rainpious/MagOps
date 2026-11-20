using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.VisualBasic.FileIO;
using MagOps.Infrastructure.Services;
using MagOps.Infrastructure.Storage;

namespace MagOps.Reporting;

public sealed class ReportSnapshotBuilder
{
    public static string Hash(string path)
    { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }

    public ReportSnapshot Build(string root, string workarea, string workareaId, ReportInputs inputs)
    {
        _ = ReportInputStore.DirectoryFor(root, workarea);
        if (string.IsNullOrWhiteSpace(inputs.ProjectName) || string.IsNullOrWhiteSpace(inputs.WorkareaName))
            throw new InvalidDataException("请填写项目名和工区名。");
        if (!DateOnly.TryParseExact(inputs.ReportDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            throw new InvalidDataException("报告日期请使用 yyyy-MM-dd 格式。");
        if (!File.Exists(inputs.ProductionCsv)) throw new FileNotFoundException("请选择已核验的处理用 CSV。", inputs.ProductionCsv);
        var sources = new List<ReportSource>();
        var facts = new List<ReportFact>();
        var warnings = ReportSharedSources.Changes(root, workarea, workareaId, inputs.SharedItems, inputs.ReportDate)
            .Select(label => "共用资料待确认：" + label).ToList();
        string hash = Hash(inputs.ProductionCsv);
        sources.Add(new("production-csv", Path.GetFullPath(inputs.ProductionCsv), hash));
        ReadProcessingReference(inputs.ProductionCsv, hash, sources, facts, warnings);
        if (!sources.Any(s => s.Role == "processing-manifest" && Path.GetFileName(s.Path) == "报告正式测线_manifest.json"))
            warnings.Add("当前采用已有CSV范围，可能已受日变有效性或其他生产筛选影响；完整正式测线原始磁评估请从“分线与处理→准备报告正式测线数据”生成来源。");
        var figurePaths = inputs.FigurePaths.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Concat(inputs.SharedItems.Where(i => i.Category == "成果图").Select(i => i.Value))
            .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (figurePaths.Length > 20) throw new InvalidDataException("每份草稿最多附20张成果PNG图。");
        foreach (string figurePath in figurePaths)
        {
            if (!Path.GetExtension(figurePath).Equals(".png", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("成果图请使用PNG；GRD/MAP须先从成图软件导出。");
            if (new FileInfo(figurePath).Length > 30 * 1024 * 1024) throw new InvalidDataException("单张成果图不能超过30MB。");
            sources.Add(new("report-figure", figurePath, Hash(figurePath)));
        }
        if (!string.IsNullOrWhiteSpace(inputs.TemplatePath))
            sources.Add(new("user-template", Path.GetFullPath(inputs.TemplatePath), Hash(inputs.TemplatePath)));
        var csvIdentity = GeneratedArtifactIdentity.Inspect(inputs.ProductionCsv);
        if (csvIdentity.Status is "HASH_MISMATCH" or "INVALID_METADATA")
            throw new InvalidDataException("处理CSV配套身份校验失败，请核实文件版本后重新选择。");
        if (csvIdentity.IsProductionStale) warnings.Add("所选CSV被标记为生产范围已过期，本草稿只反映该文件的历史内容，请核对后使用。");
        string sourceFreshness = GeneratedArtifactIdentity.InspectSourceFreshnessStatus(inputs.ProductionCsv);
        if (sourceFreshness == "STALE")
            warnings.Add("处理CSV的上游来源已变化或已删除；本草稿仍反映当前CSV文件内容，但该CSV可能不是由当前原始资料生成。请重新处理并核对。");
        else if (sourceFreshness == "UNVERIFIED")
            warnings.Add("无法确认处理CSV的上游来源是否与当前原始资料一致；本草稿仅反映所选CSV内容，请人工核对来源。");
        if (csvIdentity.Status == "VERIFIED")
        {
            string sidecar = inputs.ProductionCsv + GeneratedArtifactIdentity.Suffix;
            sources.Add(new("production-identity", Path.GetFullPath(sidecar), Hash(sidecar)));
            var knownPaths = sources.Select(s => Path.GetFullPath(s.Path))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (string sourcePath in csvIdentity.Metadata!.SourceFiles)
            {
                if (!Path.IsPathRooted(sourcePath))
                {
                    warnings.Add("处理CSV身份记录了非绝对上游来源路径，无法将该来源纳入报告实时更新检查。");
                    continue;
                }
                string fullSource = Path.GetFullPath(sourcePath);
                if (!knownPaths.Add(fullSource)) continue;
                try
                {
                    sources.Add(new("production-source", fullSource, Hash(fullSource)));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    warnings.Add("无法将处理CSV的上游来源纳入报告实时更新检查：" + Path.GetFileName(fullSource));
                }
            }
        }
        var lines = new Dictionary<(string Name, string Task, string Pass), (long Count, double M)>();
        var surveyPoints = new List<ReportSurveyPoint>();
        var track = new List<ReportTrackPoint>();
        using var reader = new TextFieldParser(inputs.ProductionCsv, System.Text.Encoding.UTF8, true);
        reader.SetDelimiters(","); reader.HasFieldsEnclosedInQuotes = true; reader.TrimWhiteSpace = false;
        string[] header = reader.ReadFields() ?? throw new InvalidDataException("CSV 没有表头。");
        var columns = header.Select((x, i) => (Name: x.Trim().Trim('\uFEFF'), Index: i))
            .ToDictionary(x => x.Name, x => x.Index, StringComparer.OrdinalIgnoreCase);
        foreach (string required in new[] { "LineName", "LinePass", "LineTaskId", "Longitude", "Latitude", "DateUtc", "TimeUtc", "AltitudeM" })
            if (!columns.ContainsKey(required)) throw new InvalidDataException($"不是受支持的处理用 CSV，缺少字段：{required}");
        string Cell(string[] row, string key) => columns.TryGetValue(key, out int i) && i < row.Length ? row[i] : "";
        if (!columns.ContainsKey("RawMagNt")) warnings.Add("CSV缺少原始磁通道RawMagNt，仅提供非磁性统计；不得以dTgz或rigai替代磁质量输入。");
        double? Number(string[] row, string key) => double.TryParse(Cell(row, key), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v) ? v : null;
        long count = 0, broken = 0; double? minimumHeight = null, maximumHeight = null;
        DateTimeOffset? firstTime = null, lastTime = null, previousTime = null;
        (string Name, string Task, string Pass)? previousKey = null;
        double? previousLon = null, previousLat = null;
        int segment = 0, stride = 25; long nextTrack = 0;
        ReportTrackPoint? lastPoint = null;
        void Keep(ReportTrackPoint point)
        {
            if (track.LastOrDefault() != point) track.Add(point);
            if (track.Count <= 40000) return;
            // Retain every segment boundary while thinning the interiors.
            track = track.Where((p, i) => i == 0 || i == track.Count - 1 || i % 2 == 0 ||
                p.Segment != track[i - 1].Segment || p.Segment != track[i + 1].Segment).ToList();
            stride = Math.Min(stride * 2, 1000000);
        }
        while (!reader.EndOfData)
        {
            string[] row = reader.ReadFields() ?? [];
            if (row.Length != header.Length) throw new InvalidDataException($"CSV 第 {count + 2} 行列数与表头不一致。");
            var key = (Name: Cell(row, "LineName"), Task: Cell(row, "LineTaskId"), Pass: Cell(row, "LinePass"));
            if (string.IsNullOrWhiteSpace(key.Name) || string.IsNullOrWhiteSpace(key.Task) || string.IsNullOrWhiteSpace(key.Pass))
                throw new InvalidDataException($"CSV 第 {count + 2} 行缺少测线/任务/Pass 身份。");
            double? lon = Number(row, "Longitude"), lat = Number(row, "Latitude");
            if (lon is not (>= -180 and <= 180) || lat is not (>= -90 and <= 90))
                throw new InvalidDataException($"CSV 第 {count + 2} 行经纬度无效。");
            if (!DateTimeOffset.TryParse(Cell(row, "DateUtc") + "T" + Cell(row, "TimeUtc"), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time))
                throw new InvalidDataException($"CSV 第 {count + 2} 行 UTC 无效。");
            double gap = previousTime is null ? double.PositiveInfinity : (time - previousTime.Value).TotalSeconds;
            bool continuous = key == previousKey && gap >= 0 && gap <= 5;
            if (!continuous)
            {
                if (lastPoint is not null) Keep(lastPoint);
                segment++; broken += key == previousKey ? 1 : 0;
            }
            double distance = continuous ? Distance(previousLon!.Value, previousLat!.Value, lon.Value, lat.Value) : 0;
            var stats = lines.GetValueOrDefault(key); lines[key] = (stats.Count + 1, stats.M + distance);
            var point = new ReportTrackPoint(segment.ToString(CultureInfo.InvariantCulture), lon.Value, lat.Value);
            if (!continuous || count >= nextTrack) { Keep(point); nextTrack = count + stride; }
            lastPoint = point;
            if (Number(row, "AltitudeM") is { } height)
            { minimumHeight = Math.Min(minimumHeight ?? height, height); maximumHeight = Math.Max(maximumHeight ?? height, height); }
            surveyPoints.Add(new(count + 2, key.Task, key.Pass, time, lon.Value, lat.Value,
                Number(row, "AltitudeM"), Number(row, "RawMagNt")));
            firstTime = firstTime is null || time < firstTime ? time : firstTime;
            lastTime = lastTime is null || time > lastTime ? time : lastTime;
            previousKey = key; previousLon = lon; previousLat = lat; previousTime = time; count++;
        }
        if (lastPoint is not null) Keep(lastPoint);
        if (count == 0) throw new InvalidDataException("处理用 CSV 为空，不能生成报告。");
        if (hash != Hash(inputs.ProductionCsv)) throw new IOException("统计过程中 CSV 已变化，请重新生成。");
        var traced = ReportProductionTrace.Attach(surveyPoints, sources, facts, warnings);
        var survey = ReportSurveyStatistics.Build(inputs, traced, sources, facts, warnings);
        var staticTrials = ReportStaticStatistics.Build(inputs, sources, facts, warnings);
        IReadOnlyList<BoundaryVertex> boundaryVertices = [];
        if (!string.IsNullOrWhiteSpace(inputs.DesignBoundaryKml))
        {
            if (string.IsNullOrWhiteSpace(inputs.BoundaryEvidence) || !double.TryParse(inputs.BoundaryCentralMeridian, NumberStyles.Float, CultureInfo.InvariantCulture, out double meridian))
                throw new InvalidDataException("设计边界必须填写明确的用途/版本依据和计算中央经线。");
            string path = Path.GetFullPath(inputs.DesignBoundaryKml), boundaryHash = Hash(path);
            var measured = SpatialBoundaryMeasurementService.MeasureKml(path, new(inputs.BoundaryDatum, meridian));
            if (Hash(path) != boundaryHash) throw new IOException("读取时设计边界发生变化。");
            sources.Add(new("design-boundary", path, boundaryHash)); boundaryVertices = measured.Vertices;
            facts.Add(new("设计面积", (measured.AreaSquareMetres / 1000000).ToString("0.######", CultureInfo.InvariantCulture), "km²", "AVAILABLE", "design-boundary",
                $"{inputs.BoundaryDatum}高斯投影，中央经线{meridian}°；外环扣孔洞，各独立区域累加，重叠/自交阻断；" + inputs.BoundaryEvidence));
        }
        ReportQualityReference? qualityReference = null;
        if (!string.IsNullOrWhiteSpace(inputs.QualityCheckResultPath))
            qualityReference = QualityCheckService.AddReportReference(inputs.QualityCheckResultPath, sources, facts, warnings);
        foreach (var supplement in inputs.Supplements)
        {
            if (string.IsNullOrWhiteSpace(supplement.Name) || string.IsNullOrWhiteSpace(supplement.Category))
                throw new InvalidDataException("报告补录必须填写类别和名称。");
            if (!string.IsNullOrWhiteSpace(supplement.SourcePath))
            {
                string path = Path.GetFullPath(supplement.SourcePath);
                sources.Add(new("supplement-evidence", path, Hash(path)));
            }
            if (supplement.Confirmed && string.IsNullOrWhiteSpace(supplement.Evidence))
                throw new InvalidDataException("已确认补录必须填写来源说明。");
            if (supplement.Category == "实际完成面积") throw new InvalidDataException("实际完成面积本阶段保留空白，不能通过补录覆盖。");
            facts.Add(new("人工补录 " + supplement.Category + " " + supplement.Name, supplement.Value, supplement.Unit,
                "REFERENCE_ONLY", supplement.SourcePath, "仅本报告使用；" + (supplement.Confirmed ? "人工已确认；" : "尚未确认；") + supplement.Evidence));
        }
        void Fact(string id, string value, string unit, string method) => facts.Add(new(id, value, unit, "AVAILABLE", "production-csv", method));
        Fact("retained_points", count.ToString(CultureInfo.InvariantCulture), "点", "最终CSV行数，不包含表头");
        Fact("retained_lines", lines.Keys.Select(x => x.Task).Distinct().Count().ToString(), "条", "按LineTaskId去重，不等于独立设计验收");
        Fact("retained_passes", lines.Count.ToString(), "组", "按LineName/LineTaskId/LinePass分组");
        Fact("retained_length", (lines.Values.Sum(x => x.M) / 1000).ToString("0.000", CultureInfo.InvariantCulture), "km", "同一连续任务/Pass内水平球面距离累加；不跨负时间或大于5秒缺口，非设计工作量，含重复经过");
        Fact("utc_range", $"{firstTime:yyyy-MM-dd HH:mm:ss} — {lastTime:yyyy-MM-dd HH:mm:ss}", "UTC", "最终CSV的UTC范围");
        if (minimumHeight is not null) Fact("elevation_range", $"{minimumHeight:0.###} — {maximumHeight:0.###}", "m", "AltitudeM海拔高程；不能代替离地飞行高度");
        foreach (string metric in new[] { "设计偏航验收", "离地高度验收", "GPS静态精度", "动态磁噪声", "静态磁噪声", "重复线总精度", "工区面积" })
            facts.Add(new(metric, "待补充独立依据", "", "UNAVAILABLE", "", "不得由拟合偏差、海拔范围或原始磁场极差替代"));
        facts.Add(new("实际完成面积", "", "km²", "UNAVAILABLE", "", "本阶段留空；后续按成图KMZ影像有效覆盖范围确认"));
        warnings.Add("这是报告草稿；统计范围是所选最终CSV，不自动证明已包含全部正式作业段或满足规范。");
        warnings.Add("长度按保留轨迹累计，包含重复飞行；不代表设计线长度、面积或独立完成工作量。");
        if (broken > 0) warnings.Add($"同一任务/Pass中有{broken}处时间断续，长度和图件已断开。");
        foreach (var section in new[] { ("任务目的", inputs.Objectives), ("方法与设备", inputs.Methods),
                     ("处理说明", inputs.ProcessingDescription), ("成果解释", inputs.Interpretation), ("结论建议", inputs.Conclusions) })
            if (string.IsNullOrWhiteSpace(section.Item2)) warnings.Add(section.Item1 + "尚未填写。");
        if (!string.IsNullOrWhiteSpace(inputs.AssessmentDirectory))
            ReadAssessment(inputs.AssessmentDirectory, sources, facts, warnings, lines.Keys.Select(x => x.Task).ToHashSet(StringComparer.Ordinal));
        string projectFile = ProjectPaths.ExistingProject(root);
        string projectHash = Hash(projectFile);
        var project = new WorkspaceService(new JsonStore()).GetWorkspaceReadOnly(root);
        if (projectHash != Hash(projectFile)) throw new IOException("读取时项目配置发生变化，请重新检查报告。");
        sources.Add(new("project", projectFile, projectHash));
        if (project.CentralMeridian is null) warnings.Add("项目中央子午线尚未确认。");
        foreach (var source in sources)
            if (!Hash(source.Path).Equals(source.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("检查过程中报告来源发生变化，请重新检查：" + source.Role);
        return new("magops-report-snapshot-2", DateTimeOffset.UtcNow, project.ProjectId, workareaId,
            project.CentralMeridian, inputs, sources, facts,
            lines.Select(x => new ReportLine(x.Key.Name, x.Key.Task, x.Key.Pass, x.Value.Count, x.Value.M / 1000)).ToArray(), track, warnings) { Survey = survey, StaticTrials = staticTrials, BoundaryVertices = boundaryVertices, QualityReference = qualityReference };
    }

    private static void ReadAssessment(string directory, List<ReportSource> sources, List<ReportFact> facts, List<string> warnings, HashSet<string> productionTasks)
    {
        string manifestPath = Path.Combine(directory, "manifest.json");
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        if (!manifest.RootElement.GetProperty("Complete").GetBoolean()) throw new InvalidDataException("飞行评估批次未完整保存。");
        if (!manifest.RootElement.TryGetProperty("SchemaVersion", out var schema) || schema.GetString() != FlightQualityAssessmentService.SchemaVersion)
            throw new InvalidDataException("飞行评估版本不受支持，请用当前版本重新保存评估。");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.RootElement.GetProperty("Files").EnumerateArray())
        {
            string name = file.GetProperty("File").GetString() ?? "";
            if (Path.GetFileName(name) != name || name.Contains('/') || name.Contains('\\') || !seen.Add(name))
                throw new InvalidDataException("评估清单包含非法或重复路径。");
            string path = Path.Combine(directory, name);
            string expected = file.GetProperty("Sha256").GetString() ?? "";
            if (!File.Exists(path) || !Hash(path).Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("飞行评估文件已变化或缺失：" + name);
            sources.Add(new("assessment:" + name, Path.GetFullPath(path), expected));
        }
        if (!seen.Contains("summary.json") || !seen.Contains("input-and-parameters.json"))
            throw new InvalidDataException("评估清单缺少统计或输入参数文件。");
        sources.Add(new("assessment-manifest", Path.GetFullPath(manifestPath), Hash(manifestPath)));
        using var summary = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "summary.json")));
        using var parameters = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "input-and-parameters.json")));
        string fingerprint = parameters.RootElement.TryGetProperty("Fingerprint", out var fp) ? fp.ToString() : "未提供";
        facts.Add(new("assessment_source", fingerprint, "", "REFERENCE_ONLY", "assessment:input-and-parameters.json", "已核对评估文件内部哈希；尚未证明与最终CSV的批次及选择范围一致"));
        if (seen.Contains("task-flight-quality.csv"))
        {
            using var taskReader = new TextFieldParser(Path.Combine(directory, "task-flight-quality.csv"), System.Text.Encoding.UTF8, true);
            taskReader.SetDelimiters(","); taskReader.HasFieldsEnclosedInQuotes = true;
            var header = taskReader.ReadFields() ?? [];
            int taskColumn = Array.FindIndex(header, h => h.Trim().Trim('\uFEFF') == "TaskId");
            if (taskColumn < 0) throw new InvalidDataException("评估任务统计缺少TaskId，无法核对任务范围。");
            var assessmentTasks = new HashSet<string>(StringComparer.Ordinal);
            while (!taskReader.EndOfData)
            {
                var row = taskReader.ReadFields() ?? [];
                if (row.Length != header.Length) throw new InvalidDataException("评估任务统计列数不一致。");
                if (!string.IsNullOrWhiteSpace(row[taskColumn])) assessmentTasks.Add(row[taskColumn]);
            }
            int matched = productionTasks.Count(assessmentTasks.Contains);
            string[] missing = productionTasks.Where(t => !assessmentTasks.Contains(t)).OrderBy(t => t, StringComparer.Ordinal).ToArray();
            int extra = assessmentTasks.Count(t => !productionTasks.Contains(t));
            facts.Add(new("assessment_task_scope", $"对应{matched}/{productionTasks.Count}；缺失{missing.Length}；额外{extra}", "任务",
                "REFERENCE_ONLY", "assessment:task-flight-quality.csv", "仅按TaskId精确比较集合；不证明Pass、点集、批次或质量合格，不能提升为最终CSV质量结论"));
            if (missing.Length > 0) warnings.Add("飞行评估缺少最终CSV任务：" + string.Join("、", missing) + "。这些任务没有对应评估依据。");
            if (extra > 0) warnings.Add($"飞行评估另含{extra}个最终CSV以外的任务，汇总指标不能直接代表最终成果。");
        }
        else warnings.Add("飞行评估未附任务统计，无法核对其对最终CSV的任务覆盖范围。");
        if (summary.RootElement.TryGetProperty("FlightQualityIndicators", out var indicators))
        {
            void Reference(string group, string key, string title, string unit, string method)
            {
                if (indicators.TryGetProperty(group, out var section) && section.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number)
                    facts.Add(new(title, value.GetDouble().ToString("0.######", CultureInfo.InvariantCulture), unit,
                        "REFERENCE_ONLY", "assessment:summary.json", "评估独立范围；" + method));
            }
            Reference("NavigationPositioning", "MeanOfLineMeansM", "评估参考 各线平均偏航均值", "m", "相对评估任务几何，不自动作为设计偏航验收");
            Reference("NavigationPositioning", "MaximumOfLineMaximumsM", "评估参考 最大偏航", "m", "相对评估任务几何，不自动作为设计偏航验收");
            Reference("FlightHeight", "MeanAboveGroundHeightM", "评估参考 平均离地高度", "m", "仅在评估明确确认激光离地基准时有值");
            Reference("GpsStaticPositioning", "PlanePrecisionM", "评估参考 静态平面重复性", "m", "来自明确静态试验窗口，不等于已知控制点准确度");
            Reference("GpsStaticPositioning", "VerticalPrecisionM", "评估参考 静态高程重复性", "m", "来自明确静态试验窗口，不用飞行高程范围替代");
        }
        warnings.Add("已附飞行评估来源，但其范围可能含未纳入航段。当前只保存独立参考，不将其候选统计写成最终生产质量结论。");
    }

    private static void ReadProcessingReference(string csv, string csvHash, List<ReportSource> sources,
        List<ReportFact> facts, List<string> warnings)
    {
        var matches = new List<(string Path, string Hash, string Text)>();
        string directory = Path.GetDirectoryName(Path.GetFullPath(csv))!;
        foreach (string path in Directory.EnumerateFiles(directory, "日变改正_manifest*.json")
                     .Concat(Directory.EnumerateFiles(directory, "报告正式测线_manifest.json")))
        {
            try
            {
                string manifestHash = Hash(path);
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                var root = document.RootElement;
                if (!root.TryGetProperty("artifact_identities", out var identities) || identities.ValueKind != JsonValueKind.Array) continue;
                bool matchesCsv = identities.EnumerateArray().Any(i =>
                    i.TryGetProperty("file", out var name) && name.GetString() == Path.GetFileName(csv) &&
                    i.TryGetProperty("content_sha256", out var contentHash) && csvHash.Equals(contentHash.GetString(), StringComparison.OrdinalIgnoreCase));
                if (!matchesCsv) continue;
                var parts = new List<string>();
                void Add(JsonElement element, string key, string title)
                {
                    if (element.TryGetProperty(key, out var value) && value.ValueKind is JsonValueKind.String or JsonValueKind.Number)
                        parts.Add(title + "：" + value.ToString());
                }
                Add(root, "algorithm_version", "处理算法版本"); Add(root, "generated_at_utc", "处理生成时间UTC");
                Add(root, "production_status", "成果状态"); Add(root, "primary_diurnal_formula", "日变计算公式");
                Add(root, "interpolation", "日变插值记录");
                if (root.TryGetProperty("prelevel", out var prelevel))
                {
                    Add(prelevel, "t0_mode", "T0模式"); Add(prelevel, "fixed_t0_nt", "T0记录值nT");
                    Add(prelevel, "formula", "预调平公式"); Add(prelevel, "height_source", "IGRF输入高程字段");
                    Add(prelevel, "igrf_coefficient_sha256", "IGRF系数指纹");
                }
                if (Hash(path) != manifestHash) throw new IOException("读取时处理清单发生变化。");
                matches.Add((path, manifestHash, string.Join("\n", parts)));
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or IOException or UnauthorizedAccessException)
            { warnings.Add("处理清单无法读取，未自动采用：" + Path.GetFileName(path)); }
        }
        if (matches.Count != 1)
        {
            warnings.Add(matches.Count == 0 ? "未找到与所选CSV内容哈希一致的处理清单，处理步骤需人工补充。" : "存在多个匹配处理清单，未自动选择；请核对处理记录。");
            return;
        }
        var match = matches[0];
        sources.Add(new("processing-manifest", match.Path, match.Hash));
        using (var reference = JsonDocument.Parse(File.ReadAllText(match.Path)))
        {
            if (reference.RootElement.TryGetProperty("blocked_reasons", out var reasons) && reasons.ValueKind == JsonValueKind.Array)
                foreach (var reason in reasons.EnumerateArray()) warnings.Add("正式测线来源核验提醒：" + reason.ToString());
            if (reference.RootElement.TryGetProperty("report_source_references", out var references))
                foreach (var item in references.EnumerateArray())
                {
                    var source = item.Deserialize<ReportSource>() ?? throw new InvalidDataException("报告来源引用为空。");
                    if (!Hash(source.Path).Equals(source.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("正式测线来源的核验状态已变化，请从分线与处理重新准备报告数据。");
                    sources.Add(source);
                }
        }
        facts.Add(new("processing_record", match.Text, "", "REFERENCE_ONLY", "processing-manifest",
            "同目录清单所记CSV名称与SHA256一致；只转录处理记录，不证明质量合格或已完成外部调平/成图"));
    }

    private static double Distance(double x1, double y1, double x2, double y2)
    {
        double r = Math.PI / 180, a = Math.Pow(Math.Sin((y2 - y1) * r / 2), 2) +
            Math.Cos(y1 * r) * Math.Cos(y2 * r) * Math.Pow(Math.Sin((x2 - x1) * r / 2), 2);
        return 6371008.8 * 2 * Math.Asin(Math.Sqrt(Math.Clamp(a, 0, 1)));
    }
}
