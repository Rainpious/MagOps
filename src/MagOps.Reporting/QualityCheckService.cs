using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MagOps.Core.Analysis;

namespace MagOps.Reporting;

public sealed record QualityColumns(int? Magnetic = null, int? Segment = null, int? RelativeSeconds = null,
    int? Longitude = null, int? Latitude = null, int? Altitude = null,
    int? Timestamp = null, int? Date = null, int? Mother = null, int? Task = null, int? Source = null, int? SourceRecord = null);
public sealed record QualityRouteMapping(string Segment, string Placemark)
{
    public string KmlPath { get; init; } = "";
    public bool GeometryConfirmed { get; init; }
}
public sealed record QualityCheckRequest
{
    public string SourcePath { get; init; } = "";
    public string SourceSha256 { get; init; } = "";
    public QualityColumns Columns { get; init; } = new();
    public string[] Metrics { get; init; } = [];
    public int FirstRecord { get; init; } = 1;
    public int? LastRecord { get; init; }
    public double IntervalSeconds { get; init; } = .5;
    public bool AutoInterval { get; init; }
    public bool ConfirmRowCadence { get; init; }
    public bool ConfirmSegmentBoundaries { get; init; }
    public string MagneticChannelRole { get; init; } = "";
    public bool MagneticUnitNtConfirmed { get; init; }
    public string TrialConditions { get; init; } = "";
    public bool FilterGradient { get; init; }
    public double GradientLimitNtKm { get; init; } = 600;
    public bool Wgs84Confirmed { get; init; }
    public string KmlPath { get; init; } = "";
    public string OffsetReferenceMode { get; init; } = "DESIGN_KML";
    public QualityOffsetReference[] OffsetReferences { get; init; } = [];
    public QualityRouteMapping[] Routes { get; init; } = [];
    public double MatchToleranceM { get; init; } = 100;
    public bool HeightDatumConfirmed { get; init; }
    public string HeightDatum { get; init; } = "";
    public double? DesignAglM { get; init; }
    public string HeightInputError { get; init; } = "";
}

public sealed record QualityMetricResult(string Metric, string Status, double? Value, string Unit,
    int ValidCount, int RejectedCount, string Formula, string Note);
public sealed record QualityDetail(string Metric, int FirstRecord, int LastRecord, string Segment,
    double? Value, string Reason)
{
    public int RunId { get; init; }
    public string ReasonCode { get; init; } = "";
}
public sealed record QualityGroupResult(string Metric, string GroupKind, string Group, string Status,
    double? Value, double? Minimum, double? Maximum, int ValidCount, int RejectedCount, string Unit, string Method);
public sealed record QualityDynamicLine(int LineOrder, string MotherId, int FirstRecord, int SourceRows,
    int RunCount, string Status, double? SvNt, int ValidWindows, int RejectedWindows, string Method);
public sealed record ReportQualityReference(string ResultPath, string ResultSha256, QualityCheckResult Result);
public sealed record QualityCheckResult(QualityCheckRequest Request, DateTimeOffset CreatedUtc,
    ReportSource[] Sources, QualityMetricResult[] Metrics, QualityDetail[] Details, string[] Warnings)
{
    public string SchemaVersion { get; init; } = "magops-quality-check-1";
    public QualityGroupResult[] Groups { get; init; } = [];
    public string TimeEvidence { get; init; } = "";
    public string DesignCatalogFingerprint { get; init; } = "";
    public QualityDynamicLine[] DynamicLines { get; init; } = [];
    public string DynamicAlgorithmVersion { get; init; } = "";
    public QualityOffsetLine[] OffsetLines { get; init; } = [];
    public QualityOffsetReference[] OffsetReferenceGeometries { get; init; } = [];
}

/// <summary>Read-only independent calculation. No project, production identity or invented UTC.</summary>
public sealed partial class QualityCheckService
{
    public const string OffsetAlgorithmVersion = "horizontal-offset-v3";
    public static readonly string[] SupportedMetrics = ["STATIC", "DYNAMIC", "OFFSET", "HEIGHT"];
    public static string Hash(string path)
    { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }

    public QualityCheckResult Compute(QualityCheckRequest request)
    {
        string path = Path.GetFullPath(request.SourcePath);
        string hash = Hash(path);
        if (hash != request.SourceSha256) throw new IOException("来源已变化，请重新载入并核对字段映射。");
        var table = new TextTableAnalysisService().Read(path);
        if (Hash(path) != hash) throw new IOException("读取过程中来源变化。");
        var columns = request.Columns;
        if (request.FirstRecord < 1 || request.LastRecord is { } last && last < request.FirstRecord || request.FirstRecord > table.Rows.Count)
            throw new InvalidDataException("数据记录范围无效（不含表头，从1开始）。");
        if (request.Metrics.Length == 0 || request.Metrics.Any(m => !SupportedMetrics.Contains(m)))
            throw new InvalidDataException("请选择支持的验算指标。");
        var rows = table.Rows.Select((cells, i) => new Row(i + 1, cells)).Where(r => r.Record >= request.FirstRecord && r.Record <= (request.LastRecord ?? int.MaxValue)).ToArray();
        var details = new List<QualityDetail>();
        var metrics = new List<QualityMetricResult>();
        var warnings = new List<string> { "所有数值仅供参考，不自动判定合格；独立验算结果不替代正式报告的选点、原始磁通道或母线身份确认。" };
        var sources = new List<ReportSource> { new("quality-table", path, hash) };
        var timeEvidence = QualityTimeReader.Read(table, columns, request.FirstRecord, request.LastRecord);
        bool hasTime = columns.RelativeSeconds is not null || columns.Timestamp is not null;
        double? interval = request.AutoInterval ? timeEvidence.Cadence.IntervalSeconds : request.IntervalSeconds;
        bool hasDynamic = request.Metrics.Contains("DYNAMIC");
        var groups = new List<QualityGroupResult>();
        string designCatalogFingerprint = "";
        var offsetLines = new List<QualityOffsetLine>();
        var offsetGeometries = new List<QualityOffsetReference>();
        if (hasTime) warnings.Add(timeEvidence.Mode + "：" + timeEvidence.Cadence.Note);
        if (!hasTime && request.Metrics.Contains("STATIC"))
            warnings.Add(request.ConfirmRowCadence
                ? "静态噪声没有相对秒列，按用户确认的采样间隔与文件行序计算；无法检测漏记录，未生成UTC或持续时间。"
                : "静态噪声没有可用时间证据；暂不计算。");
        if (hasDynamic)
            warnings.Add(hasTime
                ? "动态噪声按原记录序列计算参考值；时间仅作断口/采样证据，不要求全局稳定节拍。时间精度不足或无法解析的相邻记录仍按原行序保留，不能据此声称采样率或持续时间。"
                : "动态噪声按原记录序列计算参考值；未提供可用时间，不报告采样率或持续时间，也不能发现时间字段之外的漏采。");
        if (request.Metrics.Contains("DYNAMIC") && !request.FilterGradient)
            warnings.Add("动态噪声未做水平梯度筛选，与正式报告默认筛选口径不同。");

        int[] runIds = new int[rows.Length];
        int currentRun = 0;
        for (int i = 0; i < rows.Length; i++)
        {
            if (i > 0 && !Adjacent(rows[i - 1], rows[i], "STATIC")) currentRun++;
            runIds[i] = currentRun;
        }
        int[] DynamicRunIds()
        {
            var ids = new int[rows.Length]; int id = 0;
            for (int i = 0; i < rows.Length; i++)
            {
                if (i > 0 && !Adjacent(rows[i - 1], rows[i], "DYNAMIC")) id++;
                ids[i] = id;
            }
            return ids;
        }
        int[] dynamicRunIds = hasDynamic ? DynamicRunIds() : [];
        if (hasDynamic && hasTime)
        {
            int unknownPairs = 0, repeatedPairs = 0, reversePairs = 0, cadenceGaps = 0;
            for (int i = 1; i < rows.Length; i++)
            {
                if (!SameIdentity(rows[i - 1], rows[i])) continue;
                double? a = timeEvidence.Seconds[rows[i - 1].Record - request.FirstRecord];
                double? b = timeEvidence.Seconds[rows[i].Record - request.FirstRecord];
                if (a is null || b is null) { unknownPairs++; continue; }
                if (b == a) repeatedPairs++;
                else if (b < a) reversePairs++;
                else if (timeEvidence.Cadence.IntervalSeconds is { } step && b - a > step * 1.1 + 1e-8) cadenceGaps++;
            }
            warnings.Add($"动态时间证据：连续身份相邻对中，无法解析{unknownPairs}对、时间相同{repeatedPairs}对、时间倒退{reversePairs}对、按已观测主节拍推断的大间隔{cadenceGaps}对；仅倒退与有主节拍支持的大间隔断开窗口，同秒/未知时间不单独断开。");
        }

        foreach (string metric in request.Metrics.Distinct())
        {
            try
            {
                if (metric is "STATIC" or "DYNAMIC") Noise(metric);
                else Spatial(metric);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or FormatException or System.Xml.XmlException)
            { metrics.Add(new(metric, "BLOCKED", null, metric is "STATIC" or "DYNAMIC" ? "nT" : "m", 0, 0, "", ex.Message)); }
        }
        foreach (var source in sources)
            if (Hash(source.Path) != source.Sha256) throw new IOException("计算过程中来源已变化，请重新载入。");
        BuildGroups();
        var result = new QualityCheckResult(request, DateTimeOffset.UtcNow, sources.ToArray(), metrics.ToArray(), details.ToArray(), warnings.ToArray())
        { Groups = groups.ToArray(), TimeEvidence = timeEvidence.Mode + "；" + timeEvidence.Cadence.Note, DesignCatalogFingerprint = designCatalogFingerprint,
            OffsetLines = offsetLines.ToArray(), OffsetReferenceGeometries = offsetGeometries.ToArray(), DynamicLines = BuildDynamicLines(), DynamicAlgorithmVersion = hasDynamic ? "dynamic-record-sequence-v1" : "" };
        ValidateCatalog(result);
        return result;

        QualityDynamicLine[] BuildDynamicLines()
        {
            if (!hasDynamic || columns.Mother is not { } mother || mother < 0 || mother >= table.Headers.Length) return [];
            var byMother = groups.Where(g => g.Metric == "DYNAMIC" && g.GroupKind == "MOTHER").ToDictionary(g => g.Group, StringComparer.Ordinal);
            var metric = metrics.Single(m => m.Metric == "DYNAMIC");
            return rows.Where(r => Shaped(r) && r.Cells[mother].Trim().Length > 0).GroupBy(r => r.Cells[mother].Trim(), StringComparer.Ordinal)
                .Select((g, i) =>
                {
                    byMother.TryGetValue(g.Key, out var value);
                    return new QualityDynamicLine(i + 1, g.Key, g.First().Record, g.Count(),
                        g.Select(r => dynamicRunIds[r.Record - request.FirstRecord]).Distinct().Count(), value?.Status ?? metric.Status,
                        value?.Value, value?.ValidCount ?? 0, value?.RejectedCount ?? 0,
                        value?.Method ?? metric.Note);
                }).ToArray();
        }

        void Need(bool condition, string reason) { if (!condition) throw new InvalidDataException(reason); }
        void ValidateColumns(params int?[] used)
        {
            foreach (int? col in used)
                Need(col is null || col >= 0 && col < table.Headers.Length, $"本指标使用的字段映射第{col + 1}列超出表头范围（共{table.Headers.Length}列）。");
        }
        bool Shaped(Row r) => r.Cells.Length == table.Headers.Length;
        double? Number(Row row, int? col) => Shaped(row) && col is { } i && i >= 0 && i < row.Cells.Length &&
            double.TryParse(row.Cells[i], NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v) ? v : null;
        string Segment(Row row) => QualityTimeReader.SegmentKey(row.Cells, table.Headers.Length, columns);
        bool SameIdentity(Row a, Row b)
        {
            foreach (var col in new[] { columns.Task, columns.Source, columns.Segment, columns.Mother })
                if (col is { } i && (i < 0 || i >= a.Cells.Length || a.Cells[i].Trim().Length == 0 || a.Cells[i].Trim() != b.Cells[i].Trim())) return false;
            return true;
        }
        bool Adjacent(Row a, Row b, string metric)
        {
            if (!Shaped(a) || !Shaped(b) || !SameIdentity(a, b)) return false;
            if (metric == "STATIC")
                return (columns.Segment is null || Segment(a).Length > 0) &&
                    (!hasTime || interval is { } step && SamplingCadence.Matches(timeEvidence.Seconds[a.Record - request.FirstRecord], timeEvidence.Seconds[b.Record - request.FirstRecord], step));
            if (columns.Mother is { } mother && (mother < 0 || mother >= a.Cells.Length || a.Cells[mother].Trim().Length == 0)) return false;
            if (columns.Task is { } task && (task < 0 || task >= a.Cells.Length || a.Cells[task].Trim().Length == 0)) return false;
            if (columns.Source is { } source && (source < 0 || source >= a.Cells.Length || a.Cells[source].Trim().Length == 0)) return false;
            if (columns.Segment is { } segment && (segment < 0 || segment >= a.Cells.Length || a.Cells[segment].Trim().Length == 0)) return false;
            if (columns.Magnetic is { } magnetic && (Number(a, magnetic) is null || Number(b, magnetic) is null)) return false;
            if (columns.SourceRecord is { } record)
            {
                if (Number(a, record) is not { } ra || Number(b, record) is not { } rb || ra != Math.Truncate(ra) || rb != Math.Truncate(rb) || rb - ra != 1) return false;
            }
            // Unknown/coarse time does not split. Infer gaps only from observed cadence, never the UI default.
            if (hasTime)
            {
                double? ta = timeEvidence.Seconds[a.Record - request.FirstRecord], tb = timeEvidence.Seconds[b.Record - request.FirstRecord];
                if (ta is { } x && tb is { } y && y < x) return false;
                if (ta is { } start && tb is { } end && timeEvidence.Cadence.IntervalSeconds is { } observed && end - start > observed * 1.1 + 1e-8) return false;
            }
            return true;
        }
        IEnumerable<Row[]> Runs(string metric)
        {
            int first = 0;
            for (int i = 1; i <= rows.Length; i++)
                if (i == rows.Length || !Adjacent(rows[i - 1], rows[i], metric))
                { yield return rows[first..i]; first = i; }
        }
        void Noise(string metric)
        {
            ValidateColumns(columns.Magnetic, columns.Segment, columns.RelativeSeconds, columns.Timestamp, columns.Date, columns.Mother, columns.Task, columns.Source, columns.SourceRecord);
            Need(columns.Magnetic is not null && request.MagneticUnitNtConfirmed, "请选择磁值列并确认单位为nT。");
            if (metric == "STATIC")
            {
                Need(!string.IsNullOrWhiteSpace(request.MagneticChannelRole), "静态噪声须填写通道角色/处理阶段。");
                Need(interval is { } step && double.IsFinite(step) && step > 0, "静态噪声不能确定采样间隔；请提供有效时间证据或按采集依据指定间隔。");
                Need(hasTime || request.ConfirmRowCadence, "静态噪声没有可用时间列时，须确认按行序等间隔采样。");
                Need(request.ConfirmSegmentBoundaries, "静态噪声须确认采集段/Pass边界。");
            }
            Need(metric != "STATIC" || !string.IsNullOrWhiteSpace(request.TrialConditions), "静态验算须记录试验设备与运行条件。");
            bool gradient = metric == "DYNAMIC" && request.FilterGradient;
            if (gradient)
            {
                ValidateColumns(columns.Longitude, columns.Latitude);
                Need(columns.Longitude is not null && columns.Latitude is not null && request.Wgs84Confirmed, "梯度筛选须映射WGS84经纬度并确认坐标含义。");
                Need(double.IsFinite(request.GradientLimitNtKm) && request.GradientLimitNtKm > 0, "梯度阈值须为正有限数值。");
            }
            var differences = new List<double>();
            int attempted = Math.Max(0, rows.Length - 4);
            // Iterate original positions so every rejected cross-run window is auditable.
            double[] grads = Enumerable.Repeat(double.NaN, rows.Length).ToArray();
            if (gradient)
                foreach (var run in Runs(metric))
                {
                    // Invalid coordinates or magnetic values also delimit gradient evidence.
                    int start = 0;
                    bool Valid(Row r) => Number(r, columns.Magnetic) is not null &&
                        Number(r, columns.Longitude) is >= -180 and <= 180 && Number(r, columns.Latitude) is >= -90 and <= 90;
                    while (start < run.Length)
                    {
                        if (!Valid(run[start])) { start++; continue; }
                        int end = start + 1;
                        while (end < run.Length && Valid(run[end])) end++;
                        var part = run[start..end];
                        var g = DynamicNoiseService.PointGradients(part.Select(r => Number(r, columns.Magnetic)!.Value).ToArray(),
                            part.Select(r => Number(r, columns.Longitude)!.Value).ToArray(), part.Select(r => Number(r, columns.Latitude)!.Value).ToArray(), "geographic");
                        for (int j = 0; j < part.Length; j++) grads[part[j].Record - request.FirstRecord] = g[j];
                        start = end;
                    }
                }
            if (metric == "DYNAMIC")
            {
                int high = gradient ? grads.Count(g => double.IsFinite(g) && g > request.GradientLimitNtKm) : 0;
                int unavailable = gradient ? Enumerable.Range(0, rows.Length).Count(i => Number(rows[i], columns.Magnetic) is not null && !double.IsFinite(grads[i])) : 0;
                warnings.Add($"动态梯度筛选：{(gradient ? "开启；WGS84经纬度；阈值" + request.GradientLimitNtKm.ToString("G8", CultureInfo.InvariantCulture) + " nT/km" : "关闭")}；超限点{high}；梯度不可用点{unavailable}。配置与窗口采用原因已留存。");
            }
            int[] metricRuns = metric == "DYNAMIC" ? dynamicRunIds : runIds;
            for (int i = 0; i + 4 < rows.Length; i++)
            {
                var window = rows[i..(i + 5)];
                string reason = "";
                if (window.Any(r => !Shaped(r))) reason = "列数不一致";
                else if (window.Any(r => Number(r, columns.Magnetic) is null)) reason = "磁值无效";
                else if (Enumerable.Range(0, 4).Any(j => !Adjacent(window[j], window[j + 1], metric))) reason = "跨采集段、身份边界或时间方向倒退";
                else if (gradient && Enumerable.Range(i, 5).Any(j => !double.IsFinite(grads[j]) || grads[j] > request.GradientLimitNtKm)) reason = "梯度超限或不可计算";
                double? d = reason.Length > 0 ? null : MagneticNoiseMath.FourthDifference(Number(window[0], columns.Magnetic)!.Value,
                    Number(window[1], columns.Magnetic)!.Value, Number(window[2], columns.Magnetic)!.Value,
                    Number(window[3], columns.Magnetic)!.Value, Number(window[4], columns.Magnetic)!.Value);
                if (d is { } finite && !double.IsFinite(finite)) { d = null; reason = "差分溢出"; }
                if (d is { } value) differences.Add(value);
                details.Add(new QualityDetail(metric, window[0].Record, window[^1].Record, Segment(window[0]),
                    metric == "STATIC" ? d / 16 : d, reason) { RunId = metricRuns[window[0].Record - request.FirstRecord] });
            }
            double? noise = MagneticNoiseMath.Noise(differences, metric == "STATIC");
            if (noise is { } n && !double.IsFinite(n)) throw new InvalidDataException("标准差溢出，请核查磁通道量纲。");
            metrics.Add(new(metric, noise is null ? "INSUFFICIENT" : "REFERENCE_ONLY", noise, "nT", differences.Count,
                attempted - differences.Count, metric == "STATIC" ? MagneticNoiseMath.StaticFormula : MagneticNoiseMath.DynamicFormula,
                metric == "DYNAMIC"
                    ? $"算法dynamic-record-sequence-v1；选中{rows.Length}点；有效窗口{differences.Count}；逐母线合并其连续片段内的有效差分后计算样本标准差/√70；未知/粗时间不阻断，不声明采样率；梯度筛选={(gradient ? "开，限值" + request.GradientLimitNtKm.ToString("G8", CultureInfo.InvariantCulture) + " nT/km" : "关")}；项目适用性待核实"
                    : $"选中{rows.Length}点；有效磁值{rows.Count(r => Number(r, columns.Magnetic) is not null)}；有效/剔除数量为五点窗口；段内汇总差分后计算样本标准差；间隔{interval:G8}秒；通道角色：{request.MagneticChannelRole}；公式项目适用性待核实"));
        }
        void Spatial(string metric)
        {
            if (metric == "OFFSET") { Offset(); return; }
            ValidateColumns(columns.Longitude, columns.Latitude, columns.Segment);
            Need(request.Wgs84Confirmed && columns.Longitude is not null && columns.Latitude is not null, "请映射WGS84经纬度并确认；平面坐标需先转换。");
            Need(!string.IsNullOrWhiteSpace(request.KmlPath), "请选择设计测线KML，并填写外部段号与KML线名称的人工对应关系。");
            Need(double.IsFinite(request.MatchToleranceM) && request.MatchToleranceM > 0, "空间匹配容差须为正有限数值。");
            Need(request.Routes.All(r => !string.IsNullOrWhiteSpace(r.Segment)) && request.Routes.Select(r => r.Segment).Distinct().Count() == request.Routes.Length, "KML对应段号不能为空或重复；无段号时使用FILE。");
            if (metric == "HEIGHT")
            {
                ValidateColumns(columns.Altitude);
                Need(string.IsNullOrWhiteSpace(request.HeightInputError), request.HeightInputError);
                Need(columns.Altitude is not null && request.HeightDatumConfirmed && !string.IsNullOrWhiteSpace(request.HeightDatum), "飞高偏差需要实测高程（米），并确认与KML绝对高程基准一致，填写基准依据。");
                Need(request.DesignAglM is null || double.IsFinite(request.DesignAglM.Value) && request.DesignAglM >= 0, "设计离地飞高须为非负有限数值或留空。");
            }
            var catalog = QualityRouteCatalog.Read(request.KmlPath);
            string fingerprint = CatalogFingerprint(catalog);
            if (designCatalogFingerprint.Length == 0) designCatalogFingerprint = fingerprint;
            Need(designCatalogFingerprint == fingerprint, "计算过程中设计目录内容变化，请重新读取。");
            warnings.AddRange(catalog.Warnings);
            foreach (var source in catalog.Sources)
                if (!sources.Any(s => s.Path == source.Path)) sources.Add(source);
            var selectedSegments = rows.Select(Segment).ToHashSet(StringComparer.Ordinal);
            var mappings = QualityRouteCatalog.Suggest(catalog, selectedSegments).ToDictionary(r => r.Segment);
            foreach (var mapping in request.Routes) mappings[mapping.Segment] = mapping;
            var routes = new Dictionary<string, ReportRouteGeometry>(StringComparer.Ordinal);
            var routeErrors = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var mapping in mappings.Values.Where(r => selectedSegments.Contains(r.Segment)))
            {
                if (mapping.KmlPath.Length == 0 && mapping.Placemark.Length == 0)
                { routeErrors[mapping.Segment] = "段号没有唯一同名设计线，请指定文件和线名称"; continue; }
                var candidates = catalog.Entries.Where(e => (mapping.KmlPath.Length == 0 || Path.GetFullPath(mapping.KmlPath).Equals(e.Path, StringComparison.OrdinalIgnoreCase)) &&
                    (mapping.Placemark.Length == 0 || e.Placemark == mapping.Placemark)).ToArray();
                if (candidates.Length != 1) { routeErrors[mapping.Segment] = "设计线对应缺失或有多个候选，请指定文件和线名称"; continue; }
                try { routes[mapping.Segment] = ReportRouteGeometry.Read(candidates[0].Path, candidates[0].Placemark); }
                catch (InvalidDataException ex) { routeErrors[mapping.Segment] = ex.Message; }
            }
            Need(metric != "HEIGHT" || routes.Values.All(r => r.AltitudeMode == "absolute" && r.Vertices.All(v => v.Height is not null)), "飞高偏差仅支持带完整高度的absolute KML；不能将贴地或relativeToGround当作绝对高程。");
            var values = new List<double>();
            foreach (var row in rows)
            {
                string reason = "";
                double? value = null;
                if (Number(row, columns.Longitude) is not (>= -180 and <= 180) || Number(row, columns.Latitude) is not (>= -90 and <= 90)) reason = "坐标无效";
                else if (!routes.TryGetValue(Segment(row), out var route)) reason = routeErrors.GetValueOrDefault(Segment(row), "段号未对应设计线");
                else
                {
                    var hit = route.Match(Number(row, columns.Longitude)!.Value, Number(row, columns.Latitude)!.Value, request.MatchToleranceM);
                    if (hit is null) reason = "超出设计端点/匹配容差或几何有歧义";
                    else if (metric == "OFFSET") value = hit.Offset;
                    else if (Number(row, columns.Altitude) is { } z && hit.Height is { } reference) value = z - reference;
                    else reason = "实测或参考高程缺失";
                }
                if (value is { } v && !double.IsFinite(v)) { value = null; reason = "计算溢出"; }
                if (value is { } valid) values.Add(valid);
                details.Add(new(metric, row.Record, row.Record, Segment(row), value, reason));
                if (metric == "HEIGHT" && request.DesignAglM is { } agl)
                {
                    double? estimate = value is { } deviation && double.IsFinite(agl + deviation) ? agl + deviation : null;
                    details.Add(new("AGL_ESTIMATE", row.Record, row.Record, Segment(row), estimate,
                        estimate is not null ? "" : reason.Length > 0 ? reason : "实际飞高估计溢出"));
                }
            }
            metrics.Add(new(metric, values.Count == 0 ? "INSUFFICIENT" : "REFERENCE_ONLY", values.Count == 0 ? null : values.Average(), "m",
                values.Count, rows.Length - values.Count, metric == "OFFSET" ? "route-offset-v1: mean unsigned point-to-polyline distance" : "height-deviation-v1: mean(observed-reference absolute height)",
                metric == "OFFSET" ? "逐点距离及排除原因见明细；不外推设计端点；人工对应，无自动母线身份推断。" : "有符号高程偏差；基准依据：" + request.HeightDatum + "；实际飞高估计仅在指定设计AGL时另列明细。"));
            if (metric == "HEIGHT" && request.DesignAglM is not null)
            {
                var estimates = details.Where(d => d.Metric == "AGL_ESTIMATE" && d.Value is not null).Select(d => d.Value!.Value).ToArray();
                metrics.Add(new("AGL_ESTIMATE", estimates.Length == 0 ? "INSUFFICIENT" : "REFERENCE_ONLY", estimates.Length == 0 ? null : estimates.Average(), "m", estimates.Length, rows.Length - estimates.Length,
                    "agl-estimate-v1: design AGL + observed absolute height - KML absolute height", "相对设计地形面的离地飞高估计；不代表侧向偏移处正下方真实AGL，设计AGL及地形关系须核实"));
            }
        }

        void Offset()
        {
            ValidateColumns(columns.Longitude, columns.Latitude, columns.Mother, columns.Segment);
            Need(request.Wgs84Confirmed && columns.Longitude is not null && columns.Latitude is not null, "请映射WGS84经纬度并确认；平面坐标需先转换。");
            Need(request.OffsetReferenceMode is "DESIGN_KML" or "SEGMENTATION_REFERENCE" or "ACTUAL_FIT", "偏航参考模式无效。");
            bool external = request.OffsetReferenceMode == "DESIGN_KML";
            Need(request.OffsetReferences is not null && request.OffsetReferences.All(r => r is not null), "分线参考集合无效。");
            Need(!external || !string.IsNullOrWhiteSpace(request.KmlPath), "请选择设计测线KML或文件夹。");
            Need(!external || request.Routes.All(r => !string.IsNullOrWhiteSpace(r.Segment)) && request.Routes.Select(r => r.Segment).Distinct(StringComparer.Ordinal).Count() == request.Routes.Length,
                "设计对应身份不能为空或重复；无母线/线号列时使用FILE。");
            var catalog = external ? QualityRouteCatalog.Read(request.KmlPath) : new QualityRouteCatalogResult([], [], []);
            string fingerprint = external ? CatalogFingerprint(catalog) : "";
            if (designCatalogFingerprint.Length == 0) designCatalogFingerprint = fingerprint;
            Need(designCatalogFingerprint == fingerprint, "计算过程中设计目录内容变化，请重新读取。");
            warnings.AddRange(catalog.Warnings);
            foreach (var source in catalog.Sources)
                if (!sources.Any(s => s.Path == source.Path)) sources.Add(source);
            string Identity(Row row) => QualitySpatialIdentity.Key(row.Cells, table.Headers.Length, columns);
            var identities = rows.Select(Identity).Distinct(StringComparer.Ordinal).ToArray();
            var mappings = QualityRouteCatalog.Suggest(catalog, identities).ToDictionary(m => m.Segment, StringComparer.Ordinal);
            foreach (var mapping in request.Routes) mappings[mapping.Segment] = mapping;
            var values = new List<double>();
            int order = 0;
            foreach (var line in rows.GroupBy(Identity))
            {
                ReportRouteGeometry? geometry = null;
                string error = "", code = "", kmlPath = "", placemark = "";
                if (line.Key.Length == 0) { error = "母线身份缺失"; code = "UNMAPPED"; }
                else if (!external)
                {
                    try
                    {
                        var reference = request.OffsetReferenceMode == "ACTUAL_FIT"
                            ? QualityOffsetReferences.Fit(line.Key, line.Select(r => (Number(r, columns.Longitude), Number(r, columns.Latitude))))
                            : QualityOffsetReferences.Resolve(request.OffsetReferences!, line.Key);
                        geometry = QualityOffsetReferences.Geometry(reference);
                        placemark = reference.Name;
                        offsetGeometries.Add(reference with { Identity = line.Key });
                    }
                    catch (InvalidDataException ex) { error = ex.Message; code = "INVALID_GEOMETRY"; }
                }
                else if (!mappings.TryGetValue(line.Key, out var mapping) || mapping.KmlPath.Length == 0 && mapping.Placemark.Length == 0)
                { error = "母线没有唯一设计对应，请确认文件和设计线名称"; code = "UNMAPPED"; }
                else
                {
                    var matches = catalog.Entries.Where(e => (mapping.KmlPath.Length == 0 || Path.GetFullPath(mapping.KmlPath).Equals(e.Path, StringComparison.OrdinalIgnoreCase)) &&
                        (mapping.Placemark.Length == 0 || e.Placemark == mapping.Placemark)).ToArray();
                    if (matches.Length != 1) { error = "设计对应缺失或有多个候选"; code = "UNMAPPED"; }
                    else
                    {
                        kmlPath = matches[0].Path; placemark = matches[0].Placemark;
                        try
                        {
                            geometry = ReportRouteGeometry.Read(kmlPath, placemark);
                            if (!geometry.IsStraight && !mapping.GeometryConfirmed)
                            { geometry = null; error = "弯折设计路径尚未确认母线范围；请核对并确认，不可将整架次路径直接作为母线"; code = "GEOMETRY_UNCONFIRMED"; }
                        }
                        catch (Exception ex) when (ex is InvalidDataException or FormatException or System.Xml.XmlException)
                        { geometry = null; error = ex.Message; code = "INVALID_GEOMETRY"; }
                    }
                }
                var pointDetails = new List<QualityDetail>();
                foreach (var row in line)
                {
                    double? value = null;
                    string reason = error, reasonCode = code;
                    if (geometry is not null)
                    {
                        if (Number(row, columns.Longitude) is not (>= -180 and <= 180) || Number(row, columns.Latitude) is not (>= -90 and <= 90))
                        { reason = "坐标无效"; reasonCode = "INVALID_COORDINATE"; }
                        else
                        {
                            var hit = geometry.EvaluateHorizontal(Number(row, columns.Longitude)!.Value, Number(row, columns.Latitude)!.Value);
                            value = hit.Offset; reasonCode = hit.Status;
                            reason = hit.Status switch { "VALID" => "", "END_OUTSIDE" => "超出整条设计线外端，不作端点外推", _ => "设计几何不可计算" };
                        }
                    }
                    if (value is { } v && !double.IsFinite(v)) { value = null; reason = "计算溢出"; reasonCode = "NUMERIC_ERROR"; }
                    if (value is { } valid) values.Add(valid);
                    var detail = new QualityDetail("OFFSET", row.Record, row.Record, line.Key, value, reason) { ReasonCode = reasonCode };
                    details.Add(detail); pointDetails.Add(detail);
                }
                offsetLines.Add(BuildOffsetLine(++order, line.Key, pointDetails, kmlPath, placemark));
            }
            int rejected = rows.Length - values.Count;
            metrics.Add(new("OFFSET", values.Count == 0 ? "INSUFFICIENT" : rejected > 0 ? "PARTIAL_REFERENCE" : "REFERENCE_ONLY",
                values.Count == 0 ? null : values.Average(), "m", values.Count, rejected, (request.OffsetReferenceMode == "ACTUAL_FIT" ? "actual-fit-pca-v1 + " : "") + OffsetAlgorithmVersion + ": midpoint local finite-polyline distance; no distance clipping",
                OffsetBasis(request) + "；大偏差仍参与统计；整线外端另列，不补零。点加权均值；P95采用线性插值。可计算点比例不是设计里程覆盖率。"));
            if (request.OffsetReferenceMode == "ACTUAL_FIT") warnings.Add("实测拟合偏离仅反映输入点相对自身拟合直线的离散，不反映设计位置偏差；整线平移会被吸收。不同Pass共用母线拟合，异常点不自动剔除，弯线/连接段须先分线核对。");
            if (offsetLines.Any(l => l.UnmappedPoints > 0 || l.OtherRejectedPoints > 0))
                warnings.Add("部分母线缺少唯一设计对应或有效几何，仅相应母线未计算；总体偏航结果为可计算点的局部参考，详见逐母线统计。");
        }

        void BuildGroups()
        {
            string Mother(int record) => columns.Mother is { } c && c >= 0 && c < table.Headers.Length && table.Rows[record - 1].Length == table.Headers.Length ? table.Rows[record - 1][c].Trim() : "";
            foreach (var metric in details.GroupBy(d => d.Metric))
            {
                Add("SEGMENT", metric.GroupBy(d => metric.Key is "STATIC" or "DYNAMIC" ? d.Segment + " / 连续段" + (d.RunId + 1) : d.Segment));
                if (metric.Key is "STATIC" or "DYNAMIC")
                {
                    var existing = groups.Where(g => g.Metric == metric.Key && g.GroupKind == "SEGMENT").Select(g => g.Group).ToHashSet(StringComparer.Ordinal);
                    foreach (var run in rows.GroupBy(r => (metric.Key == "DYNAMIC" ? dynamicRunIds : runIds)[r.Record - request.FirstRecord]))
                    {
                        string group = Segment(run.First()) + " / 连续段" + (run.Key + 1);
                        if (existing.Add(group))
                            groups.Add(new(metric.Key, "SEGMENT", group, "INSUFFICIENT", null, null, null, 0, 0, "nT", "该连续段不足五点，无可计算的差分窗口"));
                    }
                }
                if (columns.Mother is not null)
                {
                    Add("MOTHER", metric.Where(d => Mother(d.FirstRecord).Length > 0 && Enumerable.Range(d.FirstRecord, d.LastRecord - d.FirstRecord + 1).All(r => Mother(r) == Mother(d.FirstRecord))).GroupBy(d => Mother(d.FirstRecord)));
                    if (metric.Key == "DYNAMIC")
                    {
                        var existing = groups.Where(g => g.Metric == metric.Key && g.GroupKind == "MOTHER").Select(g => g.Group).ToHashSet(StringComparer.Ordinal);
                        foreach (string mother in rows.Select(r => Mother(r.Record)).Where(m => m.Length > 0).Distinct(StringComparer.Ordinal))
                            if (existing.Add(mother)) groups.Add(new(metric.Key, "MOTHER", mother, "INSUFFICIENT", null, null, null, 0, 0, "nT", "母线有效差分窗口不足两个；保持空值，不纳入线值平均"));
                    }
                }
                void Add(string kind, IEnumerable<IGrouping<string, QualityDetail>> grouped)
                {
                    foreach (var g in grouped)
                    {
                        var values = g.Where(d => d.Value is not null && d.Reason.Length == 0).Select(d => d.Value!.Value).ToArray();
                        bool noise = metric.Key is "STATIC" or "DYNAMIC";
                        double? value = noise ? MagneticNoiseMath.SampleStandardDeviation(values) / (metric.Key == "DYNAMIC" ? Math.Sqrt(70) : 1) : values.Length == 0 ? null : values.Average();
                        groups.Add(new(metric.Key, kind, g.Key, value is null ? "INSUFFICIENT" : "REFERENCE_ONLY", value,
                            noise || values.Length == 0 ? null : values.Min(), noise || values.Length == 0 ? null : values.Max(), values.Length, g.Count() - values.Length,
                            noise ? "nT" : "m", noise ? "各连续采集段内有效差分窗口汇总的噪声参考；不把差分值极值当作噪声极值" : (metric.Key == "OFFSET" ? OffsetBasis(request) + "；" : "") + "有效观测点均值及最小/最大值"));
                    }
                }
            }
        }
    }

    public static string Export(QualityCheckResult result, string parentDirectory, IReadOnlyDictionary<string, byte[]>? figures = null)
    {
        ValidateCatalog(result);
        foreach (var source in result.Sources)
            if (Hash(source.Path) != source.Sha256) throw new IOException("验算后来源已变化，请重新计算后导出。");
        string directory = Path.Combine(Path.GetFullPath(parentDirectory), "质量验算_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);
        void NewFile(string name, string contents)
        { using var stream = new FileStream(Path.Combine(directory, name), FileMode.CreateNew); using var writer = new StreamWriter(stream, new UTF8Encoding(true)); writer.Write(contents); }
        string Csv(object? value) => "\"" + (value is IFormattable f ? f.ToString(null, CultureInfo.InvariantCulture) : value?.ToString() ?? "").Replace("\"", "\"\"") + "\"";
        string Row(params object?[] cells) => string.Join(",", cells.Select(Csv));
        void CsvFile(string name, string header, IEnumerable<string> lines)
        {
            using var stream = new FileStream(Path.Combine(directory, name), FileMode.CreateNew);
            using var writer = new StreamWriter(stream, new UTF8Encoding(true));
            writer.WriteLine(header);
            foreach (string line in lines) writer.WriteLine(line);
        }
        CsvFile("验算汇总.csv", Row("指标", "状态", "参考值", "单位", "有效数量", "剔除数量", "公式", "说明"),
            result.Metrics.Select(m => Row(m.Metric, m.Status, m.Value, m.Unit, m.ValidCount, m.RejectedCount, m.Formula, m.Note)));
        CsvFile("观测与窗口明细.csv", Row("指标", "起始记录", "结束记录", "采集段或母线身份", "数值", "处理原因", "连续段号", "处理原因代码"),
            result.Details.Select(d => Row(d.Metric, d.FirstRecord, d.LastRecord, d.Segment, d.Value, d.Reason, d.RunId, d.ReasonCode)));
        CsvFile("分组统计.csv", Row("指标", "分组类型", "分组名称", "状态", "参考值", "最小值", "最大值", "有效数量", "剔除数量", "单位", "计算说明"),
            result.Groups.Select(g => Row(g.Metric, g.GroupKind, g.Group, g.Status, g.Value, g.Minimum, g.Maximum, g.ValidCount, g.RejectedCount, g.Unit, g.Method)));
        CsvFile("报告参数清单.csv", Row("指标", "范围", "统计口径", "数值", "单位", "状态", "依据"),
            ReportParameters(result).Select(p => Row(p.Metric, p.Scope, p.Statistic, p.Value, p.Unit, p.Status, p.Evidence)));
        WriteDynamicLines(directory, result);
        ReportDeliveryTables.WriteReference(directory, result);
        int imageIndex = 0;
        foreach (var figure in figures ?? new Dictionary<string, byte[]>())
        {
            string name = figure.Key.Contains("动态噪声", StringComparison.Ordinal) && figure.Key.Contains("直方图", StringComparison.Ordinal)
                ? "动态噪声直方图.png" : figure.Key.Contains("实测拟合偏离", StringComparison.Ordinal) ? "实测拟合偏离分布图.png" : figure.Key.Contains("偏航距", StringComparison.Ordinal) ? "偏航距分布图.png" : $"质量图件_{++imageIndex:00}.png";
            File.WriteAllBytes(Path.Combine(directory, name), figure.Value);
        }
        using (var stream = new FileStream(Path.Combine(directory, "质量验算结果.json"), FileMode.CreateNew))
            JsonSerializer.Serialize(stream, result, new JsonSerializerOptions { WriteIndented = true });
        // Completion marker is written last; absence means interrupted export.
        NewFile("完成标记.txt", "COMPLETE\n" + string.Join("\n", result.Metrics.Select(m => m.Metric + ": " + m.Status)) + "\n" + string.Join("\n", result.Warnings));
        return directory;
    }

    public static void WriteDynamicLines(string directory, QualityCheckResult result)
    {
        if (!result.Request.Metrics.Contains("DYNAMIC")) return;
        string Cell(object? value) => "\"" + (Convert.ToString(value, CultureInfo.InvariantCulture) ?? "").Replace("\"", "\"\"") + "\"";
        string Row(params object?[] cells) => string.Join(",", cells.Select(Cell));
        File.WriteAllLines(Path.Combine(directory, "动态噪声逐线统计.csv"),
            new[] { "线序,母测线号,动态噪声Sv_nT,有效窗口数,剔除窗口数,原始点数,连续片段数,首条记录,计算状态,算法版本,计算公式,是否启用梯度筛选,梯度限值_nT每千米,时间证据,来源文件,来源SHA256,计算说明" }
                .Concat(result.DynamicLines.Select(l => Row(l.LineOrder, l.MotherId, l.SvNt, l.ValidWindows, l.RejectedWindows,
                    l.SourceRows, l.RunCount, l.FirstRecord, l.Status, result.DynamicAlgorithmVersion, result.Metrics.FirstOrDefault(m => m.Metric == "DYNAMIC")?.Formula,
                    result.Request.FilterGradient, result.Request.FilterGradient ? result.Request.GradientLimitNtKm : null,
                    result.TimeEvidence, result.Request.SourcePath, result.Request.SourceSha256, l.Method))), new UTF8Encoding(true));
        var metric = result.Metrics.FirstOrDefault(m => m.Metric == "DYNAMIC");
        var usable = result.DynamicLines.Where(l => l.SvNt is not null).ToArray();
        var bins = HistogramBins(result.DynamicLines);
        File.WriteAllLines(Path.Combine(directory, "动态噪声直方图分箱.csv"),
            new[] { "区间序号,动态噪声下限_nT,动态噪声上限_nT,线数,占比,区间边界规则,区间母测线号" }.Concat(
                bins.Select((b, i) => Row(b.Index, b.Lower, b.Upper, b.Count,
                    usable.Length == 0 ? null : b.Count * 1d / usable.Length,
                    i == bins.Length - 1 ? "左闭右开；末箱右闭" : "左闭右开", string.Join("、", b.Lines)))), new UTF8Encoding(true));
        File.WriteAllText(Path.Combine(directory, "动态噪声成果说明.txt"),
            "动态噪声逐母线成果（REFERENCE_ONLY，独立参考）\n" +
            "按母线首次出现的输入记录顺序；SvNt为空表示数据不足或输入缺项，不能当作0。\n" +
            "有效/剔除窗口为完全归属该母线的五点窗口；跨母线窗口不归入任何线，详见观测与窗口明细.csv。原始点数含该母线选中行；缺少母线号的记录不虚构身份。\n" +
            $"母线{result.DynamicLines.Length}条，有效{usable.Length}条；全表窗口合算Sv={metric?.Value?.ToString("G17", CultureInfo.InvariantCulture) ?? "空"} nT；逐线Sv等权平均={(usable.Length > 0 ? usable.Average(l => l.SvNt!.Value).ToString("G17", CultureInfo.InvariantCulture) : "空")} nT。\n" +
            "直方图区间左闭右开，末箱包含最大值；没有有效逐线Sv时不绘图。" +
            (usable.Length > 0 && usable.Max(l => l.SvNt!.Value) - usable.Min(l => l.SvNt!.Value) < 1e-12 ? "所有有效Sv相同时，分箱范围仅为显示需要而扩展。" : "") + "\n" +
            "上述两种统计口径不同；没有自动合格判定。未映射母线号或旧结果缺少逐线证据时，逐线表为空，需重新映射并计算。\n" +
            result.DynamicAlgorithmVersion + "\n" + metric?.Formula + "\n" + metric?.Note + "\n" + result.TimeEvidence, new UTF8Encoding(true));
    }

    public sealed record DynamicNoiseBin(int Index, double Lower, double Upper, int Count, string[] Lines);
    public static DynamicNoiseBin[] HistogramBins(IEnumerable<QualityDynamicLine> input)
    {
        var values = input.Where(l => l.SvNt is { } v && double.IsFinite(v)).ToArray();
        if (values.Length == 0) return [];
        double min = values.Min(l => l.SvNt!.Value), max = values.Max(l => l.SvNt!.Value);
        if (max - min < 1e-12) { double pad = Math.Max(Math.Abs(min) * .05, .001); min -= pad; max += pad; }
        int count = Math.Clamp((int)Math.Ceiling(Math.Log2(values.Length) + 1), 5, 12);
        double width = (max - min) / count;
        return Enumerable.Range(0, count).Select(i =>
        {
            double lower = min + i * width, upper = i == count - 1 ? max : min + (i + 1) * width;
            var lines = values.Where(l => i == count - 1 ? l.SvNt!.Value >= lower && l.SvNt!.Value <= upper : l.SvNt!.Value >= lower && l.SvNt!.Value < upper).ToArray();
            return new DynamicNoiseBin(i + 1, lower, upper, lines.Length, lines.Select(l => l.MotherId).ToArray());
        }).ToArray();
    }

    public static ReportQualityReference AddReportReference(string path, List<ReportSource> sources, List<ReportFact> facts, List<string> warnings)
    {
        string full = Path.GetFullPath(path), hash = Hash(full);
        ReferencePayload payload;
        using (var stream = File.OpenRead(full)) payload = JsonSerializer.Deserialize<ReferencePayload>(stream)
            ?? throw new InvalidDataException("质量验算结果为空。");
        var result = new QualityCheckResult(payload.Request, payload.CreatedUtc, payload.Sources, payload.Metrics, [], payload.Warnings ?? [])
        { SchemaVersion = payload.SchemaVersion, Groups = payload.Groups ?? [], DesignCatalogFingerprint = payload.DesignCatalogFingerprint ?? "",
            OffsetReferenceGeometries = payload.OffsetReferenceGeometries ?? [], DynamicLines = payload.DynamicLines ?? [], TimeEvidence = payload.TimeEvidence ?? "", DynamicAlgorithmVersion = payload.DynamicAlgorithmVersion ?? "" };
        if (result.SchemaVersion != "magops-quality-check-1" || result.Sources is null || result.Metrics is null || result.Request is null || result.Sources.Length == 0)
            throw new InvalidDataException("质量验算结果结构不完整。");
        if (!result.Sources.Any(s => s.Role == "quality-table" && Path.GetFullPath(s.Path).Equals(Path.GetFullPath(result.Request.SourcePath), StringComparison.OrdinalIgnoreCase) && s.Sha256 == result.Request.SourceSha256))
            throw new InvalidDataException("质量验算请求与来源指纹不一致。");
        ValidateCatalog(result);
        foreach (var source in result.Sources)
        {
            if (Hash(source.Path) != source.Sha256) throw new IOException("引用的质量验算来源已变化，请重新验算。");
            sources.Add(source with { Role = "quality-reference-source:" + source.Role });
        }
        if (Hash(full) != hash) throw new IOException("读取时验算结果变化。");
        sources.Add(new("quality-reference", full, hash));
        foreach (var metric in ReportParameters(result).Where(m => m.Value is { } value && double.IsFinite(value) && m.Status == "REFERENCE_ONLY"))
            facts.Add(new("独立质量验算参考 " + (metric.Metric == "OFFSET" && result.Request.OffsetReferenceMode == "ACTUAL_FIT" ? "ACTUAL_FIT_DEVIATION" : metric.Metric) + " " + metric.Scope + " " + metric.Statistic,
                metric.Value?.ToString("G17", CultureInfo.InvariantCulture) ?? "", metric.Unit,
                metric.Value is not null && metric.Status == "REFERENCE_ONLY" ? "REFERENCE_ONLY" : "UNAVAILABLE", full,
                metric.Evidence + "；外部验算引用，不覆盖正式报告统计"));
        warnings.Add("已人工选用独立质量验算结果作为参考附表；未自动替换正式测线统计或作合格判定。");
        if (result.DynamicLines.Length == 0 && result.Groups.Any(g => g.Metric == "DYNAMIC" && g.GroupKind == "MOTHER"))
            warnings.Add("旧验算结果缺少逐母线顺序及点数证据；请重新验算后生成逐线柱状图。");
        return new(full, hash, result);
    }
    private sealed record Row(int Record, string[] Cells);
    // The report reference does not need potentially millions of per-window detail records.
    private sealed record ReferencePayload(QualityCheckRequest Request, ReportSource[] Sources, QualityMetricResult[] Metrics, string SchemaVersion)
    {
        public QualityGroupResult[]? Groups { get; init; }
        public string? DesignCatalogFingerprint { get; init; }
        public QualityOffsetReference[]? OffsetReferenceGeometries { get; init; }
        public QualityDynamicLine[]? DynamicLines { get; init; }
        public string? TimeEvidence { get; init; }
        public string? DynamicAlgorithmVersion { get; init; }
        public DateTimeOffset CreatedUtc { get; init; }
        public string[]? Warnings { get; init; }
    }

    private static string CatalogFingerprint(QualityRouteCatalogResult catalog) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Join("\n", catalog.Sources.OrderBy(s => s.Path, StringComparer.OrdinalIgnoreCase).Select(s => s.Path.ToUpperInvariant() + "\t" + s.Sha256)))));
    private static void ValidateCatalog(QualityCheckResult result)
    {
        if (result.DesignCatalogFingerprint.Length > 0 && CatalogFingerprint(QualityRouteCatalog.Read(result.Request.KmlPath)) != result.DesignCatalogFingerprint)
            throw new IOException("设计KML目录的文件集合或内容已变化，请重新验算。");
    }

    public sealed record ReportParameter(string Metric, string Scope, string Statistic, double? Value, string Unit, string Status, string Evidence);
    public static ReportParameter[] ReportParameters(QualityCheckResult result)
    {
        var output = result.Metrics.Select(m => new ReportParameter(m.Metric, "SELECTED_RECORDS", m.Metric == "DYNAMIC" ? "POOLED_WINDOW_REFERENCE" : "POOLED_REFERENCE", m.Value, m.Unit, m.Status, m.Formula + "；" + m.Note)).ToList();
        foreach (var byMetric in result.Groups.Where(g => g.GroupKind == "MOTHER").GroupBy(g => g.Metric))
        {
            var usable = byMetric.Where(g => g.Value is not null && g.Status == "REFERENCE_ONLY").ToArray();
            string note = $"已映射母线{byMetric.Count()}，有效{usable.Length}；不按点数加权，不替代全表有效窗口汇总；未证明完整正式测线范围";
            if (byMetric.Key == "DYNAMIC")
            {
                output.Add(new(byMetric.Key, "MAPPED_MOTHERS", "TOTAL_LINE_COUNT", byMetric.Count(), "条", "REFERENCE_ONLY", note));
                output.Add(new(byMetric.Key, "MAPPED_MOTHERS", "VALID_LINE_COUNT", usable.Length, "条", "REFERENCE_ONLY", note));
            }
            if (usable.Length == 0) continue;
            output.Add(new(byMetric.Key, "MAPPED_MOTHERS", "MIN_OF_LINE_VALUES", usable.Min(g => g.Value), usable[0].Unit, "REFERENCE_ONLY", note));
            output.Add(new(byMetric.Key, "MAPPED_MOTHERS", "MAX_OF_LINE_VALUES", usable.Max(g => g.Value), usable[0].Unit, "REFERENCE_ONLY", note));
            output.Add(new(byMetric.Key, "MAPPED_MOTHERS", "MEAN_OF_LINE_VALUES", usable.Average(g => g.Value!.Value), usable[0].Unit, "REFERENCE_ONLY", note));
        }
        output.Add(new("ACTUAL_AREA", "SURVEY", "KMZ_COVERAGE", null, "km2", "DEFERRED", "实际完成面积后续按成图KMZ影像覆盖确定，保持空值"));
        return output.Select(p => p.Metric == "OFFSET" ? p with { Evidence = OffsetBasis(result.Request) + "；" + p.Evidence } : p).ToArray();
    }
}
