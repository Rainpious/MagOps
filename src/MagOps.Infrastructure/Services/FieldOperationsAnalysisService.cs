using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MagOps.Core.Domain;
using MagOps.Core.Routes;

namespace MagOps.Infrastructure.Services;

public sealed record PlannedFieldSortie(
    int Order,
    string Name,
    IReadOnlyList<GeoCoordinate> Coordinates)
{
    public string LogicalName { get; init; } = "";
    public string SourceFile { get; init; } = "";
    public string SourceSha256 { get; init; } = "";
    public DateTimeOffset? MeasurementTimeUtc { get; init; }
    public bool IsRepeatedMeasurement { get; init; }
    public bool IsLatestRepeatedMeasurement { get; init; }
}

public sealed record RepeatedFieldRouteMeasurement(
    string LogicalName,
    string DisplayName,
    IReadOnlyList<GeoCoordinate> Coordinates,
    string SourceFile,
    string SourceSha256,
    DateTimeOffset MeasurementTimeUtc,
    bool IsLatest);

public sealed record FieldRouteSnapshot(
    string SourcePath,
    string SourceLabel,
    string Fingerprint,
    DateTime SourceModifiedAt,
    IReadOnlyList<PlannedFieldSortie> Sorties)
{
    public IReadOnlyList<PlannedSurveyLineTask> SurveyLines { get; init; } = [];
    public IReadOnlyList<RepeatedFieldRouteMeasurement> RepeatedMeasurements { get; init; } = [];
    public bool HasSurveyLineIdentity => SurveyLines.Count > 0;
    public bool IsPartialWorkspace { get; init; }
}

public sealed record FieldSourceSummary(
    string Kind,
    IReadOnlyList<string> Files,
    int SampleCount,
    int TrackPointCount,
    DateTimeOffset? FirstTime,
    DateTimeOffset? LastTime,
    IReadOnlyList<GeoCoordinate> Track,
    IReadOnlyList<string> Warnings);

public sealed record FieldSortieProgressEstimate(
    int Order,
    string SortieName,
    string Status,
    double CoverageRatio,
    bool CompletedBefore,
    bool CompletedToday,
    string Evidence);

public sealed record FieldRepeatedSortiePassEstimate(
    string SortieName,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    double CoverageRatio,
    int MatchedPointCount);

public sealed record FieldTakeoffRecommendation(
    int Order,
    int PlanDay,
    string RecommendationId,
    GeoCoordinate Coordinate,
    IReadOnlyList<string> SortieNames,
    double MeanDistanceM,
    double MaxDistanceM);

public sealed record FieldDailyAnalysisResult(
    DateTime Date,
    FieldRouteSnapshot Route,
    FieldSourceSummary Gsm19T,
    FieldSourceSummary UavMag,
    IReadOnlyList<FieldSortieProgressEstimate> SortieProgress,
    IReadOnlyList<FieldTakeoffRecommendation> Recommendations,
    IReadOnlyList<string> CompletedToday,
    IReadOnlyList<string> CompletedCumulative,
    IReadOnlyList<string> RemainingSorties,
    string ProgressSummary,
    string DataSummary,
    string NextActionSummary,
    IReadOnlyList<string> Warnings)
{
    public string ProgressAlgorithmVersion { get; init; } = "legacy-sortie-coverage";
    public string ProjectTimeZoneId { get; init; } = "Asia/Shanghai";
    public bool SurveyLineMatchingAvailable { get; init; }
    public IReadOnlyList<FieldSurveyLineProgressEstimate> SurveyLineProgress { get; init; } = [];
    public IReadOnlyList<string> TodayCompletedSurveyLineTaskIds { get; init; } = [];
    public IReadOnlyList<string> CompletedSurveyLineTaskIds { get; init; } = [];
    public double PlannedSurveyLineKm { get; init; }
    public double TodayCompletedSurveyLineKm { get; init; }
    public double CompletedSurveyLineKm { get; init; }
    public double RemainingSurveyLineKm { get; init; }
    public IReadOnlyList<string> RepeatedSortiesToday { get; init; } = [];
    public IReadOnlyList<string> RepeatedSortiesCumulative { get; init; } = [];
    public IReadOnlyList<FieldRepeatedSortiePassEstimate> RepeatedPassesToday { get; init; } = [];
}

/// <summary>
/// Reads the current workarea's same-day magnetic-survey files and estimates
/// route progress from UAV GNSS tracks. The estimate is intentionally kept
/// separate from RoutePlanner: it consumes already-designed route geometry and
/// never changes route generation semantics.
/// </summary>
public sealed class FieldOperationsAnalysisService
{
    private readonly SurveyLineProgressService _surveyLineProgress = new();

    public const double DefaultTrackMatchToleranceM = 12d;
    public const double CompletionThreshold = 0.90d;
    public const double PartialThreshold = 0.20d;
    public const double RepeatedFlightTimeGapMinutes = 10d;
    private const int RepeatedFlightMinimumMatchedPoints = 5;

    private static readonly string[] TextExtensions =
    [
        ".csv", ".txt", ".tsv", ".dat", ".asc", ".xyz", ".log", ".stn"
    ];

    private static readonly string[] GsmHints =
    [
        "gsm-19", "gsm19", "gsm_19", "gsm 19", "19t", "19-t", "gem systems"
    ];

    private static readonly string[] UavHints =
    [
        "uav", "drone", "无人机", "航磁", "磁测", "airborne", "matrice", "m350", "m400", "gpam", "mag", "tmi", "magnetic", "totalfield", "total field"
    ];

    private static readonly string[] LonHeaders =
    [
        "lon", "longitude", "gpslon", "gpslongitude", "经度", "lng"
    ];

    private static readonly string[] LatHeaders =
    [
        "lat", "latitude", "gpslat", "gpslatitude", "纬度"
    ];

    private static readonly string[] TimeHeaders =
    [
        "timestamp", "datetime", "dateandtime", "gpstime", "utctime", "time", "时间", "时刻"
    ];

    private static readonly string[] DateHeaders =
    [
        "date", "日期"
    ];

    public FieldRouteSnapshot FromRoutePlan(RoutePlanResult plan, string sourceLabel = "当前航线工作台")
    {
        if (plan.Sorties.Count == 0)
            throw new InvalidOperationException("当前航线计划没有架次。 ");

        var sorties = new List<PlannedFieldSortie>();
        for (int i = 0; i < plan.Sorties.Count; i++)
        {
            RouteSortie sortie = plan.Sorties[i];
            var coords = sortie.Points
                .Select(point =>
                {
                    var ll = RouteCoordinateMath.ToLonLat(point, plan.Mode, plan.CentralMeridian);
                    return new GeoCoordinate(ll.Lon, ll.Lat);
                })
                .ToList();
            if (coords.Count < 2)
                continue;

            sorties.Add(new PlannedFieldSortie(
                i + 1,
                string.IsNullOrWhiteSpace(sortie.Name)
                    ? (string.IsNullOrWhiteSpace(plan.PartitionCode)
                        ? $"A{i + 1:D2}"
                        : $"{plan.PartitionCode}-A{i + 1:D2}")
                    : sortie.Name,
                coords));
        }

        IReadOnlyList<PlannedSurveyLineTask> surveyLines =
            BuildSurveyLineTasks(
                plan.Sorties,
                plan.Mode,
                plan.CentralMeridian,
                plan.PartitionCode);

        return BuildSnapshot(
            plan.SourcePath,
            sourceLabel,
            File.Exists(plan.SourcePath) ? File.GetLastWriteTime(plan.SourcePath) : DateTime.Now,
            sorties,
            surveyLines);
    }

    public FieldRouteSnapshot FromSubdivisionWorkspace(RouteSubdivisionWorkspace workspace, string sourcePath)
    {
        var plans = workspace.Partitions.Where(p => p.Plan is { Sorties.Count: > 0 }).Select(p => p.Plan!).ToArray();
        return FromRoutePlans(plans, sourcePath,
            $"整个工区（已生成 {plans.Length}/{workspace.Partitions.Count} 个子区）") with
        { IsPartialWorkspace = plans.Length != workspace.Partitions.Count };
    }

    public FieldRouteSnapshot FromRoutePlans(IEnumerable<RoutePlanResult> source, string sourcePath, string sourceLabel)
    {
        var plans = source.ToArray();
        if (plans.Length == 0 || plans.Select(p => p.CentralMeridian).Distinct().Count() != 1 ||
            plans.Select(p => p.PartitionCode).Distinct(StringComparer.OrdinalIgnoreCase).Count() != plans.Length)
            throw new InvalidDataException("全工区航线需要同一投影、身份唯一的子区计划。");
        var snapshots = plans.Select(p => FromRoutePlan(p)).ToArray();
        var sorties = snapshots.SelectMany(s => s.Sorties).Select((s, i) => s with { Order = i + 1 }).ToArray();
        if (sorties.Select(s => s.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != sorties.Length)
            throw new InvalidDataException("子区架次名称重复，不能静默合并进度。");
        var sortieOrders = sorties.ToDictionary(s => s.Name, s => s.Order, StringComparer.OrdinalIgnoreCase);
        return BuildSnapshot(sourcePath, sourceLabel, snapshots.Max(s => s.SourceModifiedAt), sorties,
            snapshots.SelectMany(s => s.SurveyLines).Select((s, i) => s with
            { Order = i + 1, SortieOrder = sortieOrders[s.SortieName] }).ToArray());
    }

    public FieldRouteSnapshot? TryLoadManagedRouteSnapshot(
        string routeProjectDirectory,
        string routeExportDirectory,
        string routeImportRootDirectory = "")
    {
        FieldRouteSnapshot? project = TryLoadLatestRouteProject(routeProjectDirectory);
        FieldRouteSnapshot? kml = TryLoadLatestRouteKmlSet(routeExportDirectory);
        FieldRouteSnapshot? imported = string.IsNullOrWhiteSpace(routeImportRootDirectory)
            ? null
            : TryLoadLatestImportedRouteKmlSet(routeImportRootDirectory);

        // The route-project JSON preserves SourceLines, which are the business
        // identity of survey tasks. A generated KML is only a connected flight
        // trajectory and must not replace that richer identity merely because
        // the export file has a newer timestamp. A genuinely newer external
        // imported KML may still supersede the project source.
        if (project is { HasSurveyLineIdentity: true })
        {
            // Field progress is task accounting, not "newest connected polyline"
            // selection. SourceLines are the authoritative survey-task identity.
            // Imported/generated KML remains a usable fallback when no rich route
            // project is available.
            return project;
        }

        return new[] { project, kml, imported }
            .Where(x => x is not null)
            .Cast<FieldRouteSnapshot>()
            .OrderByDescending(x => x.SourceModifiedAt)
            .FirstOrDefault();
    }

    public FieldDailyAnalysisResult Analyze(
        string workareaRoot,
        DateTime date,
        FieldRouteSnapshot route,
        IEnumerable<FieldDailyProgressRecord> history,
        int expectedDailySorties,
        int acceptedRelocations,
        IEnumerable<string>? manualGsm19TFiles = null,
        IEnumerable<string>? manualUavFiles = null,
        FieldMagFolderAnalysis? preparedData = null,
        double trackToleranceM = DefaultTrackMatchToleranceM)
    {
        if (!Directory.Exists(workareaRoot))
            throw new DirectoryNotFoundException($"当前工区目录不存在：{workareaRoot}");
        if (route.Sorties.Count == 0)
            throw new InvalidOperationException("没有可用于外业进度分析的设计航线。 ");

        date = date.Date;
        TimeZoneInfo timeZone = TimeZoneInfo.FindSystemTimeZoneById(preparedData?.ProjectTimeZoneId ?? "Asia/Shanghai");
        FieldSourceSummary gsm;
        FieldSourceSummary uav;
        if (preparedData is not null)
        {
            gsm = FieldMagDataService.ToBaseSourceSummary(preparedData);
            uav = FieldMagDataService.ToUavSourceSummary(preparedData);
        }
        else
        {
            List<string> candidates = DiscoverDailyFiles(workareaRoot, date);
            List<string> gsmFiles = candidates.Where(path => Classify(path) == "GSM19T").ToList();
            List<string> uavFiles = candidates.Where(path => Classify(path) == "UAV_MAG").ToList();

            AddManualFiles(gsmFiles, manualGsm19TFiles);
            AddManualFiles(uavFiles, manualUavFiles);

            gsm = ReadSourceSummary("GSM19T", gsmFiles);
            uav = ReadSourceSummary("UAV_MAG", uavFiles);
        }

        List<FieldDailyProgressRecord> historyRows = history
            .Where(row =>
                string.Equals(
                    row.ProgressTimeZone,
                    ProjectTime.Basis(timeZone),
                    StringComparison.OrdinalIgnoreCase))
            .Where(row =>
                row.Date.CompareTo(
                    date.ToString(
                        "yyyy-MM-dd",
                        CultureInfo.InvariantCulture)) < 0)
            .Where(row =>
                row.RouteFingerprint.Equals(
                    route.Fingerprint,
                    StringComparison.OrdinalIgnoreCase))
            .ToList();

        HashSet<string> repeatedBefore =
            historyRows
                .SelectMany(row => row.RepeatedSorties)
                .ToHashSet(
                    StringComparer.OrdinalIgnoreCase);
        var repeatedToday =
            new List<string>();
        var repeatedCumulative =
            new HashSet<string>(
                repeatedBefore,
                StringComparer.OrdinalIgnoreCase);
        var repeatedPassesToday =
            new List<FieldRepeatedSortiePassEstimate>();

        bool useSurveyLineMatching =
            route.SurveyLines.Count > 0;

        var progress =
            new List<FieldSortieProgressEstimate>(
                route.Sorties.Count);
        var todayCompleted =
            new List<string>();
        var cumulativeOrdered =
            new List<string>();
        var remaining =
            new List<string>();
        IReadOnlyList<FieldSurveyLineProgressEstimate> lineProgress = [];
        var todayCompletedLineIds =
            new List<string>();
        var cumulativeLineIds =
            new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

        double plannedSurveyLineKm = 0d;
        double todayCompletedSurveyLineKm = 0d;
        double completedSurveyLineKm = 0d;
        double remainingSurveyLineKm = 0d;
        string progressAlgorithmVersion =
            "legacy-sortie-coverage";

        if (useSurveyLineMatching)
        {
            progressAlgorithmVersion =
                SurveyLineProgressService.AlgorithmVersion;
            HashSet<string> completedBeforeLineIds =
                historyRows
                    .Where(row =>
                        row.ProgressAlgorithmVersion.Equals(
                            SurveyLineProgressService.AlgorithmVersion,
                            StringComparison.OrdinalIgnoreCase))
                    .SelectMany(row =>
                        row.CompletedSurveyLineTaskIds)
                    .ToHashSet(
                        StringComparer.OrdinalIgnoreCase);

            IReadOnlyList<SurveyTrackPoint> trackPoints =
                BuildSurveyTrackPoints(
                    preparedData,
                    uav);
            lineProgress =
                _surveyLineProgress.Analyze(
                    route.SurveyLines,
                    trackPoints,
                    completedBeforeLineIds);

            todayCompletedLineIds =
                lineProgress
                    .Where(x => x.CompletedToday)
                    .Select(x => x.TaskId)
                    .ToList();
            cumulativeLineIds.UnionWith(
                completedBeforeLineIds);
            cumulativeLineIds.UnionWith(
                todayCompletedLineIds);

            plannedSurveyLineKm =
                route.SurveyLines.Sum(x => x.LengthM) /
                1000d;
            todayCompletedSurveyLineKm =
                route.SurveyLines
                    .Where(x =>
                        todayCompletedLineIds.Contains(
                            x.TaskId,
                            StringComparer.OrdinalIgnoreCase))
                    .Sum(x => x.LengthM) /
                1000d;
            completedSurveyLineKm =
                route.SurveyLines
                    .Where(x =>
                        cumulativeLineIds.Contains(
                            x.TaskId))
                    .Sum(x => x.LengthM) /
                1000d;
            remainingSurveyLineKm =
                Math.Max(
                    0d,
                    plannedSurveyLineKm -
                    completedSurveyLineKm);

            foreach (PlannedFieldSortie sortie in route.Sorties
                .OrderBy(x => x.Order))
            {
                List<PlannedSurveyLineTask> sortieLines =
                    route.SurveyLines
                        .Where(x =>
                            x.SortieName.Equals(
                                sortie.Name,
                                StringComparison.OrdinalIgnoreCase))
                        .OrderBy(x => x.Order)
                        .ToList();

                if (sortieLines.Count == 0)
                {
                    progress.Add(
                        new FieldSortieProgressEstimate(
                            sortie.Order,
                            sortie.Name,
                            "需测线身份",
                            0d,
                            CompletedBefore: false,
                            CompletedToday: false,
                            "该架次未恢复到测线任务身份，未使用区域覆盖法自动判定"));
                    remaining.Add(sortie.Name);
                    continue;
                }

                double sortieLength =
                    sortieLines.Sum(x => x.LengthM);
                double completedLength =
                    sortieLines
                        .Where(x =>
                            cumulativeLineIds.Contains(
                                x.TaskId))
                        .Sum(x => x.LengthM);
                int completedCount =
                    sortieLines.Count(x =>
                        cumulativeLineIds.Contains(
                            x.TaskId));
                int todayLineCount =
                    sortieLines.Count(x =>
                        todayCompletedLineIds.Contains(
                            x.TaskId,
                            StringComparer.OrdinalIgnoreCase));
                double todayLineKm =
                    sortieLines
                        .Where(x =>
                            todayCompletedLineIds.Contains(
                                x.TaskId,
                                StringComparer.OrdinalIgnoreCase))
                        .Sum(x => x.LengthM) /
                    1000d;

                bool completedBefore =
                    sortieLines.All(x =>
                        completedBeforeLineIds.Contains(
                            x.TaskId));
                bool completedNow =
                    sortieLines.All(x =>
                        cumulativeLineIds.Contains(
                            x.TaskId));
                bool completedToday =
                    !completedBefore &&
                    completedNow;

                string status =
                    completedBefore
                        ? "此前完成"
                        : completedToday
                            ? "当日完成"
                            : completedCount > 0 ||
                              lineProgress.Any(x =>
                                  x.SortieName.Equals(
                                      sortie.Name,
                                      StringComparison.OrdinalIgnoreCase) &&
                                  x.CoverageRatio >=
                                      SurveyLineProgressService.PartialCoverageThreshold)
                                ? "部分完成"
                                : "未开展";

                double ratio =
                    sortieLength <= 0d
                        ? 0d
                        : completedLength /
                            sortieLength;
                List<FieldSurveyLineProgressEstimate> sortieLineProgress =
                    lineProgress
                        .Where(x =>
                            x.SortieName.Equals(
                                sortie.Name,
                                StringComparison.OrdinalIgnoreCase))
                        .ToList();
                List<FieldSurveyLineProgressEstimate> matchedLineProgress =
                    sortieLineProgress
                        .Where(x =>
                            x.MatchedPointCount > 0)
                        .ToList();
                string diagnostic =
                    matchedLineProgress.Count == 0
                        ? "无有效轨迹匹配"
                        : $"横向偏差 P95 最大 {matchedLineProgress.Max(x => x.CrossTrackP95M):0.#} m；" +
                          $"最大缺口 {matchedLineProgress.Max(x => x.MaxGapM):0.#} m；" +
                          $"动态容差 {matchedLineProgress.Min(x => x.MatchToleranceM):0.#}–{matchedLineProgress.Max(x => x.MatchToleranceM):0.#} m";
                string evidence =
                    $"测线任务完成 {completedCount}/{sortieLines.Count}；" +
                    $"累计 {completedLength / 1000d:0.00}/{sortieLength / 1000d:0.00} km；" +
                    $"当日新增 {todayLineCount} 条 / {todayLineKm:0.00} km；" +
                    diagnostic;

                progress.Add(
                    new FieldSortieProgressEstimate(
                        sortie.Order,
                        sortie.Name,
                        status,
                        ratio,
                        completedBefore,
                        completedToday,
                        evidence));

                if (completedToday)
                    todayCompleted.Add(
                        sortie.Name);
                if (completedNow)
                    cumulativeOrdered.Add(
                        sortie.Name);
                else
                    remaining.Add(
                        sortie.Name);
            }
        }
        else
        {
            HashSet<string> completedBefore = historyRows
                .SelectMany(row => row.CompletedSorties)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            TrackGrid? trackGrid = uav.Track.Count > 0
                ? new TrackGrid(uav.Track, trackToleranceM)
                : null;
            IReadOnlyList<SurveyTrackPoint> timedTrackPoints =
                preparedData is null
                    ? []
                    : BuildSurveyTrackPoints(
                        preparedData,
                        uav);

            foreach (PlannedFieldSortie sortie in route.Sorties.OrderBy(x => x.Order))
            {
                IReadOnlyList<FieldRepeatedSortiePassEstimate> qualifyingPasses =
                    timedTrackPoints.Count == 0
                        ? []
                        : DetectQualifyingSortiePasses(
                            sortie,
                            timedTrackPoints,
                            trackToleranceM);

                bool completedBeforeThisSortie =
                    completedBefore.Contains(
                        sortie.Name);
                bool repeatedTodayThisSortie =
                    completedBeforeThisSortie
                        ? qualifyingPasses.Count >= 1
                        : qualifyingPasses.Count >= 2;

                if (repeatedTodayThisSortie)
                {
                    repeatedToday.Add(
                        sortie.Name);
                    repeatedCumulative.Add(
                        sortie.Name);

                    int firstRepeatIndex =
                        completedBeforeThisSortie
                            ? 0
                            : 1;
                    repeatedPassesToday.AddRange(
                        qualifyingPasses
                            .Skip(firstRepeatIndex));
                }

                string repeatDiagnostic =
                    repeatedTodayThisSortie
                        ? BuildRepeatedFlightDiagnostic(
                            qualifyingPasses,
                            completedBeforeThisSortie, timeZone)
                        : repeatedBefore.Contains(
                            sortie.Name)
                            ? "；历史已识别重复飞行，重复飞行不重复计入完成架次"
                            : "";

                if (completedBeforeThisSortie)
                {
                    progress.Add(new FieldSortieProgressEstimate(
                        sortie.Order,
                        sortie.Name,
                        "此前完成",
                        1d,
                        CompletedBefore: true,
                        CompletedToday: false,
                        "历史外业进度记录（兼容模式；当前航线源未保留测线任务身份）" +
                        repeatDiagnostic));
                    cumulativeOrdered.Add(sortie.Name);
                    continue;
                }

                double coverage = trackGrid is null
                    ? 0d
                    : EstimateCoverage(
                        sortie.Coordinates,
                        trackGrid);
                bool completedToday =
                    coverage >= CompletionThreshold;
                string status = completedToday
                    ? "当日完成"
                    : coverage >= PartialThreshold
                        ? "部分覆盖"
                        : "未开展";
                string evidence = trackGrid is null
                    ? "未读取到可用的无人机经纬度航迹"
                    : $"兼容模式：设计航线覆盖 {coverage:P0}（容差 {trackToleranceM:0.#} m）" +
                      repeatDiagnostic;

                progress.Add(new FieldSortieProgressEstimate(
                    sortie.Order,
                    sortie.Name,
                    status,
                    coverage,
                    CompletedBefore: false,
                    CompletedToday: completedToday,
                    evidence));
                if (completedToday)
                {
                    todayCompleted.Add(sortie.Name);
                    cumulativeOrdered.Add(sortie.Name);
                }
                else
                {
                    remaining.Add(sortie.Name);
                }
            }
        }

        IReadOnlyList<FieldTakeoffRecommendation> recommendations = MagOps.Core.Routes.LaunchSitePlanning.Enabled ? BuildTakeoffRecommendations(
            route,
            remaining,
            expectedDailySorties,
            acceptedRelocations) : [];

        var warnings = new List<string>();
        warnings.AddRange(gsm.Warnings);
        warnings.AddRange(uav.Warnings);
        if (gsm.Files.Count == 0)
            warnings.Add("未自动发现所选作业日 GSM-19T 数据；19T 数据状态无法确认。 ");
        if (uav.Files.Count == 0)
            warnings.Add("未自动发现所选作业日无人机磁测数据；当日任务进度无法由实飞轨迹匹配。 ");
        else if (uav.TrackPointCount == 0)
            warnings.Add("无人机数据已发现，但没有识别到可用经纬度航迹；请检查 Raw 文件、GNSS 字段和 SensorStatus。 ");
        if (!useSurveyLineMatching)
            warnings.Add("当前航线源没有保留“架次→测线任务”身份；本次仅使用低容差兼容估算。正式项目进度建议使用绘制航线工程作为进度航线源。");

        string progressSummary = useSurveyLineMatching
            ? $"设计测线 {plannedSurveyLineKm:0.00} km；当日确认完成 {todayCompletedSurveyLineKm:0.00} km；" +
              $"累计完成 {completedSurveyLineKm:0.00} km；剩余 {remainingSurveyLineKm:0.00} km；" +
              $"架次累计完成 {cumulativeOrdered.Count}/{route.Sorties.Count}。"
            : $"兼容模式：设计航线 {route.Sorties.Count} 架次；当日估算完成 {todayCompleted.Count} 架次；" +
              $"累计完成 {cumulativeOrdered.Count} 架次；剩余 {remaining.Count} 架次；" +
              $"重复飞行累计 {repeatedCumulative.Count} 架次（不重复计完成）。";

        string gsmTime = FormatTimeSpan(gsm.FirstTime, gsm.LastTime, timeZone);
        string uavTime = FormatTimeSpan(uav.FirstTime, uav.LastTime, timeZone);
        string dataSummary =
            $"GSM-19T：{gsm.Files.Count} 个文件 / {gsm.SampleCount} 条记录{gsmTime}；" +
            $"无人机磁测：{uav.Files.Count} 个文件 / {uav.SampleCount} 条记录 / {uav.TrackPointCount} 个航迹点{uavTime}。";

        int plannedDays = remaining.Count == 0
            ? 0
            : (int)Math.Ceiling(remaining.Count / (double)Math.Max(1, expectedDailySorties));
        string nextActionSummary = !MagOps.Core.Routes.LaunchSitePlanning.Enabled
            ? (remaining.Count == 0 ? "当前设计航线已全部完成。" : $"剩余 {remaining.Count} 个架次，按每日 {Math.Max(1, expectedDailySorties)} 架次约需 {plannedDays} 个作业日。")
            : recommendations.Count == 0
            ? remaining.Count == 0
                ? "当前设计航线已全部完成。"
                : "未生成推荐起飞点；请检查后续架次数量与航线起点。"
            : $"按预计每日 {Math.Max(1, expectedDailySorties)} 架次、可接受 {Math.Max(0, acceptedRelocations)} 次转场，" +
              $"将 {remaining.Count} 个未开展架次按设计顺序规划为约 {plannedDays} 个作业日，共推荐 {recommendations.Count} 个起飞点。";

        return new FieldDailyAnalysisResult(
            date,
            route,
            gsm,
            uav,
            progress,
            recommendations,
            todayCompleted,
            cumulativeOrdered,
            remaining,
            progressSummary,
            dataSummary,
            nextActionSummary,
            warnings.Distinct(StringComparer.OrdinalIgnoreCase).ToList())
        {
            ProjectTimeZoneId = timeZone.Id,
            ProgressAlgorithmVersion =
                progressAlgorithmVersion,
            SurveyLineMatchingAvailable =
                useSurveyLineMatching,
            SurveyLineProgress =
                lineProgress,
            TodayCompletedSurveyLineTaskIds =
                todayCompletedLineIds,
            CompletedSurveyLineTaskIds =
                cumulativeLineIds
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
            PlannedSurveyLineKm =
                plannedSurveyLineKm,
            TodayCompletedSurveyLineKm =
                todayCompletedSurveyLineKm,
            CompletedSurveyLineKm =
                completedSurveyLineKm,
            RemainingSurveyLineKm =
                remainingSurveyLineKm,
            RepeatedSortiesToday =
                repeatedToday
                    .Distinct(
                        StringComparer.OrdinalIgnoreCase)
                    .OrderBy(
                        x => x,
                        StringComparer.OrdinalIgnoreCase)
                    .ToList(),
            RepeatedSortiesCumulative =
                repeatedCumulative
                    .OrderBy(
                        x => x,
                        StringComparer.OrdinalIgnoreCase)
                    .ToList(),
            RepeatedPassesToday =
                repeatedPassesToday
                    .OrderBy(x => x.StartUtc)
                    .ToList()
        };
    }

    public FieldDailyProgressRecord ToRecord(
        FieldDailyAnalysisResult analysis,
        string workareaName,
        int expectedDailySorties,
        int acceptedRelocations)
    {
        return new FieldDailyProgressRecord
        {
            Date = analysis.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Workarea = workareaName,
            RouteSource = analysis.Route.SourcePath,
            RouteFingerprint = analysis.Route.Fingerprint,
            PlannedSortieCount = analysis.Route.Sorties.Count,
            TodayCompletedSorties = analysis.CompletedToday.ToList(),
            CompletedSorties = analysis.CompletedCumulative.ToList(),
            RemainingSorties = analysis.RemainingSorties.ToList(),
            TodayRepeatedSorties = analysis.RepeatedSortiesToday.ToList(),
            RepeatedSorties = analysis.RepeatedSortiesCumulative.ToList(),
            Gsm19TFiles = analysis.Gsm19T.Files.ToList(),
            UavMagFiles = analysis.UavMag.Files.ToList(),
            Gsm19TSampleCount = analysis.Gsm19T.SampleCount,
            UavMagSampleCount = analysis.UavMag.SampleCount,
            UavTrackPointCount = analysis.UavMag.TrackPointCount,
            ExpectedDailySorties = Math.Max(1, expectedDailySorties),
            AcceptedRelocations = Math.Max(0, acceptedRelocations),
            ProgressAlgorithmVersion =
                analysis.ProgressAlgorithmVersion,
            SurveyLineMatchingAvailable =
                analysis.SurveyLineMatchingAvailable,
            PlannedSurveyLineKm =
                analysis.PlannedSurveyLineKm,
            TodayCompletedSurveyLineKm =
                analysis.TodayCompletedSurveyLineKm,
            CompletedSurveyLineKm =
                analysis.CompletedSurveyLineKm,
            RemainingSurveyLineKm =
                analysis.RemainingSurveyLineKm,
            TodayCompletedSurveyLineTaskIds =
                analysis.TodayCompletedSurveyLineTaskIds.ToList(),
            CompletedSurveyLineTaskIds =
                analysis.CompletedSurveyLineTaskIds.ToList(),
            ProgressTimeZone = ProjectTime.Basis(TimeZoneInfo.FindSystemTimeZoneById(analysis.ProjectTimeZoneId)),
            DailyCompletionPercent = Math.Min(
                100d,
                analysis.CompletedToday.Count /
                (double)Math.Max(1, expectedDailySorties) * 100d),
            TotalCompletionPercent =
                analysis.SurveyLineMatchingAvailable &&
                analysis.PlannedSurveyLineKm > 0d
                    ? analysis.CompletedSurveyLineKm /
                        analysis.PlannedSurveyLineKm * 100d
                    : analysis.Route.Sorties.Count == 0
                        ? 0d
                        : analysis.CompletedCumulative.Count /
                            (double)analysis.Route.Sorties.Count * 100d,
            ProgressSummary = analysis.ProgressSummary,
            DataSummary = analysis.DataSummary,
            NextActionSummary = analysis.NextActionSummary,
            Warnings = analysis.Warnings.ToList(),
            Recommendations = analysis.Recommendations.Select(x => new FieldTakeoffRecommendationRecord
            {
                PlanDay = x.PlanDay,
                RecommendationId = x.RecommendationId,
                Longitude = x.Coordinate.Longitude,
                Latitude = x.Coordinate.Latitude,
                SortieNames = x.SortieNames.ToList(),
                MeanDistanceM = x.MeanDistanceM,
                MaxDistanceM = x.MaxDistanceM
            }).ToList()
        };
    }

    public static string WriteRecommendationCsv(
        string path,
        IEnumerable<FieldTakeoffRecommendation> recommendations)
    {
        string full = Path.GetFullPath(path);
        return AtomicFileWriter.Write(full, stream =>
        {
            using var writer = new StreamWriter(stream, new UTF8Encoding(true), leaveOpen: true);
            writer.WriteLine("后续批次,推荐点,经度,纬度,服务架次,平均距离_m,最远距离_m");
            foreach (FieldTakeoffRecommendation row in recommendations)
            {
                writer.WriteLine(string.Join(",",
                    Csv($"批次 {row.PlanDay}"),
                    Csv(row.RecommendationId),
                    row.Coordinate.Longitude.ToString("0.########", CultureInfo.InvariantCulture),
                    row.Coordinate.Latitude.ToString("0.########", CultureInfo.InvariantCulture),
                    Csv(string.Join(";", row.SortieNames)),
                    row.MeanDistanceM.ToString("0.0", CultureInfo.InvariantCulture),
                    row.MaxDistanceM.ToString("0.0", CultureInfo.InvariantCulture)));
            }
            writer.Flush();
        });
    }

    private FieldRouteSnapshot? TryLoadLatestRouteProject(string directory)
    {
        if (!Directory.Exists(directory))
            return null;

        FileInfo[] baselineFiles = new DirectoryInfo(directory)
            .EnumerateFiles(
                "*外业测线任务基准.json",
                SearchOption.TopDirectoryOnly)
            .OrderByDescending(x => x.LastWriteTimeUtc)
            .ToArray();

        var partitionProjects = new List<(FileInfo File, RouteProjectFile Project)>();
        foreach (FileInfo file in baselineFiles)
        {
            try
            {
                RouteProjectFile project = new RouteProjectService().Load(file.FullName);
                if (file == baselineFiles[0] && project.SubdivisionWorkspace is { } full)
                    return FromSubdivisionWorkspace(full, file.FullName);
                if (!string.IsNullOrWhiteSpace(project.PartitionCode))
                    partitionProjects.Add((file, project));
            }
            catch
            {
                // One stale route-project file must not hide other valid partitions.
            }
        }

        if (partitionProjects.Count > 0)
        {
            var selected = partitionProjects
                .GroupBy(
                    x => x.Project.PartitionCode.Trim(),
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => group
                    .OrderByDescending(x => x.File.LastWriteTimeUtc)
                    .First())
                .OrderBy(x => x.Project.PartitionCode, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var sorties = new List<PlannedFieldSortie>();
            var surveyLines = new List<PlannedSurveyLineTask>();
            int sortieOrder = 1;
            int lineOrder = 1;

            foreach ((FileInfo file, RouteProjectFile project) in selected)
            {
                string mode = ResolveProjectMode(project);
                for (int i = 0; i < project.Sorties.Count; i++)
                {
                    RouteSortie sortie = project.Sorties[i];
                    List<GeoCoordinate> coords = sortie.Points.Select(point =>
                    {
                        var ll = RouteCoordinateMath.ToLonLat(
                            point,
                            mode,
                            project.CentralMeridian);
                        return new GeoCoordinate(ll.Lon, ll.Lat);
                    }).ToList();
                    if (coords.Count < 2)
                        continue;

                    string name = string.IsNullOrWhiteSpace(sortie.Name)
                        ? $"{project.PartitionCode}-A{i + 1:D2}"
                        : sortie.Name;
                    sorties.Add(new PlannedFieldSortie(
                        sortieOrder++,
                        name,
                        coords));
                }

                IReadOnlyList<PlannedSurveyLineTask> projectLines =
                    BuildSurveyLineTasks(
                        project.Sorties,
                        mode,
                        project.CentralMeridian,
                        project.PartitionCode);
                foreach (PlannedSurveyLineTask line in projectLines)
                    surveyLines.Add(line with { Order = lineOrder++ });
            }

            if (sorties.Count > 0)
            {
                DateTime modified = selected.Max(x => x.File.LastWriteTime);
                return BuildSnapshot(
                    directory,
                    $"分区航线工程（{selected.Length} 个分区）",
                    modified,
                    sorties,
                    surveyLines);
            }
        }

        FileInfo? latest = new DirectoryInfo(directory)
            .EnumerateFiles("*.json", SearchOption.TopDirectoryOnly)
            .OrderByDescending(x => x.LastWriteTimeUtc)
            .FirstOrDefault();
        if (latest is null)
            return null;

        try
        {
            RouteProjectFile project = new RouteProjectService().Load(latest.FullName);
            if (project.SubdivisionWorkspace is { } full)
                return FromSubdivisionWorkspace(full, latest.FullName);
            string mode = ResolveProjectMode(project);
            var sorties = new List<PlannedFieldSortie>();
            for (int i = 0; i < project.Sorties.Count; i++)
            {
                RouteSortie sortie = project.Sorties[i];
                var coords = sortie.Points.Select(point =>
                {
                    var ll = RouteCoordinateMath.ToLonLat(
                        point,
                        mode,
                        project.CentralMeridian);
                    return new GeoCoordinate(ll.Lon, ll.Lat);
                }).ToList();
                if (coords.Count < 2)
                    continue;
                sorties.Add(new PlannedFieldSortie(
                    i + 1,
                    string.IsNullOrWhiteSpace(sortie.Name) ? $"A{i + 1:D2}" : sortie.Name,
                    coords));
            }
            IReadOnlyList<PlannedSurveyLineTask> surveyLines =
                BuildSurveyLineTasks(
                    project.Sorties,
                    mode,
                    project.CentralMeridian,
                    project.PartitionCode);
            return sorties.Count == 0
                ? null
                : BuildSnapshot(
                    latest.FullName,
                    "最新航线编辑工程",
                    latest.LastWriteTime,
                    sorties,
                    surveyLines);
        }
        catch
        {
            return null;
        }
    }

    public FieldRouteSnapshot? TryLoadImportedRouteKmlSnapshot(string batchDirectory)
    {
        if (!Directory.Exists(batchDirectory))
            return null;

        IReadOnlyList<RouteKmlImportItem> files =
            RouteKmlImportService.InspectBatch(
                batchDirectory);
        if (files.Count == 0)
            return null;

        var candidates =
            new List<ImportedSortieCandidate>();

        foreach (RouteKmlImportItem item in files)
        {
            try
            {
                List<SpatialGeometry> routeGeometries =
                    SpatialGeometryService
                        .ParseKml(item.ImportedPath)
                        .Where(x =>
                            (x.Type is "LineString" or "Track") &&
                            x.Coordinates.Count >= 2)
                        .ToList();

                var localNameCounts =
                    new Dictionary<string, int>(
                        StringComparer.OrdinalIgnoreCase);

                for (int i = 0; i < routeGeometries.Count; i++)
                {
                    SpatialGeometry geometry =
                        routeGeometries[i];
                    string rawName =
                        string.IsNullOrWhiteSpace(
                            geometry.Name)
                            ? routeGeometries.Count == 1
                                ? Path.GetFileNameWithoutExtension(
                                    item.FileName)
                                : $"{Path.GetFileNameWithoutExtension(item.FileName)}_{i + 1:D2}"
                            : geometry.Name.Trim();

                    int localIndex =
                        localNameCounts.TryGetValue(
                            rawName,
                            out int existingCount)
                            ? existingCount + 1
                            : 1;
                    localNameCounts[rawName] =
                        localIndex;

                    string sourceTaskName =
                        localIndex == 1
                            ? rawName
                            : $"{rawName}_{localIndex:D2}";

                    candidates.Add(
                        new ImportedSortieCandidate(
                            sourceTaskName,
                            geometry.Coordinates,
                            item.ImportedPath,
                            item.FileName,
                            item.Sha256,
                            item.MeasurementTimeUtc ??
                                item.SourceLastWriteUtc ??
                                new DateTimeOffset(
                                    File.GetLastWriteTimeUtc(
                                        item.ImportedPath),
                                    TimeSpan.Zero),
                            i));
                }
            }
            catch
            {
                // Continue defensively with the remaining imported KML files.
            }
        }

        if (candidates.Count == 0)
            return null;

        // Route-definition rule:
        // - same SHA bytes were already collapsed by InspectBatch;
        // - different SHA files remain independent formal design tasks even when
        //   they carry the same route name (for example left/right route groups).
        // Actual repeated measurement is inferred later from UAV track time gaps,
        // never from duplicate route names or KML file timestamps.
        var sorties =
            new List<PlannedFieldSortie>();
        var usedNames =
            new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
        int order = 1;

        foreach (ImportedSortieCandidate candidate in candidates
            .OrderBy(
                x => NaturalSortKey(x.LogicalName),
                StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.MeasurementTimeUtc)
            .ThenBy(
                x => x.ImportedFileName,
                StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.GeometryIndex))
        {
            string taskName =
                UniqueImportedSortieName(
                    candidate.LogicalName,
                    usedNames);

            sorties.Add(
                new PlannedFieldSortie(
                    order++,
                    taskName,
                    candidate.Coordinates)
                {
                    LogicalName =
                        candidate.LogicalName,
                    SourceFile =
                        candidate.ImportedPath,
                    SourceSha256 =
                        candidate.SourceSha256,
                    MeasurementTimeUtc =
                        candidate.MeasurementTimeUtc,
                    IsRepeatedMeasurement =
                        false,
                    IsLatestRepeatedMeasurement =
                        false
                });
        }

        string manifest =
            Path.Combine(
                batchDirectory,
                RouteKmlImportService.ManifestFileName);
        DateTime modified =
            File.Exists(manifest)
                ? File.GetLastWriteTime(manifest)
                : files
                    .Select(x =>
                        x.MeasurementTimeUtc ??
                        x.SourceLastWriteUtc ??
                        DateTimeOffset.MinValue)
                    .DefaultIfEmpty(
                        DateTimeOffset.MinValue)
                    .Max()
                    .LocalDateTime;

        return BuildSnapshot(
            batchDirectory,
            "外部导入航线 KML",
            modified,
            sorties);
    }

    private FieldRouteSnapshot? TryLoadLatestImportedRouteKmlSet(string importRootDirectory)
    {
        if (!Directory.Exists(importRootDirectory))
            return null;

        DirectoryInfo[] batches = new DirectoryInfo(importRootDirectory)
            .EnumerateDirectories("*", SearchOption.TopDirectoryOnly)
            .OrderByDescending(x => ImportedRouteBatchTimestamp(x.FullName))
            .ThenByDescending(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (DirectoryInfo batch in batches)
        {
            FieldRouteSnapshot? snapshot = TryLoadImportedRouteKmlSnapshot(batch.FullName);
            if (snapshot is not null)
                return snapshot;
        }

        return null;
    }

    private static DateTime ImportedRouteBatchTimestamp(string batchDirectory)
    {
        string manifest = Path.Combine(batchDirectory, RouteKmlImportService.ManifestFileName);
        return File.Exists(manifest)
            ? File.GetLastWriteTime(manifest)
            : Directory.GetLastWriteTime(batchDirectory);
    }

    private static string UniqueImportedSortieName(string baseName, HashSet<string> usedNames)
    {
        string normalized = string.IsNullOrWhiteSpace(baseName) ? "外部航线" : baseName.Trim();
        if (usedNames.Add(normalized))
            return normalized;

        for (int i = 2; i < 1000; i++)
        {
            string candidate = $"{normalized}_{i:D2}";
            if (usedNames.Add(candidate))
                return candidate;
        }

        return $"{normalized}_{Guid.NewGuid():N}";
    }

    private FieldRouteSnapshot? TryLoadLatestRouteKmlSet(string routeExportDirectory)
    {
        string kmlDirectory = Directory.Exists(Path.Combine(routeExportDirectory, "01_KML"))
            ? Path.Combine(routeExportDirectory, "01_KML")
            : routeExportDirectory;
        if (!Directory.Exists(kmlDirectory))
            return null;

        FileInfo[] files = new DirectoryInfo(kmlDirectory)
            .EnumerateFiles("*.kml", SearchOption.TopDirectoryOnly)
            .OrderBy(x => NaturalSortKey(x.Name), StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (files.Length == 0)
            return null;

        var sorties = new List<PlannedFieldSortie>();
        int order = 1;
        foreach (FileInfo file in files)
        {
            try
            {
                SpatialGeometry? geometry = SpatialGeometryService.ParseKml(file.FullName)
                    .FirstOrDefault(x => (x.Type is "LineString" or "Track") && x.Coordinates.Count >= 2);
                if (geometry is null)
                    continue;
                sorties.Add(new PlannedFieldSortie(order++, Path.GetFileNameWithoutExtension(file.Name), geometry.Coordinates));
            }
            catch
            {
                // Ignore one malformed KML and continue with the rest of the route set.
            }
        }
        if (sorties.Count == 0)
            return null;

        DateTime modified = files.Max(x => x.LastWriteTime);
        return BuildSnapshot(kmlDirectory, "最新设计航线 KML", modified, sorties);
    }

    private static IReadOnlyList<PlannedSurveyLineTask> BuildSurveyLineTasks(
        IReadOnlyList<RouteSortie> sorties,
        string mode,
        double centralMeridian,
        string partitionCode = "")
    {
        var result =
            new List<PlannedSurveyLineTask>();
        int order = 1;

        for (int sortieIndex = 0; sortieIndex < sorties.Count; sortieIndex++)
        {
            RouteSortie sortie = sorties[sortieIndex];
            string sortieName =
                string.IsNullOrWhiteSpace(sortie.Name)
                    ? (string.IsNullOrWhiteSpace(partitionCode)
                        ? $"A{sortieIndex + 1:D2}"
                        : $"{partitionCode}-A{sortieIndex + 1:D2}")
                    : sortie.Name;

            List<(string LineName, RouteEndpoint First, RouteEndpoint Second)> definitions =
                ResolveSurveyLineDefinitions(sortie);
            foreach ((string lineName, RouteEndpoint first, RouteEndpoint second) in definitions)
            {
                var a =
                    RouteCoordinateMath.ToLonLat(
                        first,
                        mode,
                        centralMeridian);
                var b =
                    RouteCoordinateMath.ToLonLat(
                        second,
                        mode,
                        centralMeridian);
                var start =
                    new GeoCoordinate(
                        a.Lon,
                        a.Lat);
                var end =
                    new GeoCoordinate(
                        b.Lon,
                        b.Lat);
                double lengthM =
                    RouteCoordinateMath.Haversine(
                        start.Longitude,
                        start.Latitude,
                        end.Longitude,
                        end.Latitude);
                if (!double.IsFinite(lengthM) ||
                    lengthM <= 0.5d)
                {
                    continue;
                }

                RouteTaskReference? stableReference = sortie.TaskReferences.FirstOrDefault(r =>
                    r.LineName.Equals(lineName, StringComparison.OrdinalIgnoreCase) &&
                    r.GeometryFingerprint.Equals(
                        RouteIdentityService.Fingerprint(first, second),
                        StringComparison.OrdinalIgnoreCase));
                stableReference ??= sortie.TaskReferences.FirstOrDefault(r =>
                    r.ExecutionSegmentId.Equals(lineName, StringComparison.OrdinalIgnoreCase));
                string businessLineName = stableReference?.LineName is { Length: > 0 } parent
                    ? parent
                    : lineName;
                string taskId =
                    SurveyLineTaskId(
                        businessLineName + "|" + lineName,
                        start,
                        end);
                string geometryFingerprint =
                    RouteIdentityService.Fingerprint(first, second);
                result.Add(
                    new PlannedSurveyLineTask(
                        order++,
                        sortieIndex + 1,
                        taskId,
                        sortieName,
                        businessLineName,
                        start,
                        end,
                        lengthM)
                    {
                        PartitionCode = partitionCode?.Trim() ?? "",
                        MotherLineTaskId = MotherSurveyLineTaskId(businessLineName),
                        SegmentId = taskId,
                        ExecutionSegmentId = stableReference?.ExecutionSegmentId ?? "",
                        ExecutionSegmentStableId = stableReference?.ExecutionSegmentStableId ?? "",
                        SurveyTaskStableId = stableReference?.SurveyTaskStableId ?? "",
                        SurveyLineStableId = stableReference?.SurveyLineStableId ?? "",
                        MotherLineStableId = stableReference?.MotherLineStableId ?? "",
                        IsQualityRepeat = stableReference?.SurveyPurpose == "QUALITY_REPEAT"
                    });
            }
        }

        return result;
    }

    private static List<(string LineName, RouteEndpoint First, RouteEndpoint Second)>
        ResolveSurveyLineDefinitions(
            RouteSortie sortie)
    {
        var result =
            new List<(string LineName, RouteEndpoint First, RouteEndpoint Second)>();

        if (sortie.SourceLines.Count > 0 &&
            sortie.Points.Count >=
                sortie.SourceLines.Count * 2)
        {
            for (int i = 0; i < sortie.SourceLines.Count; i++)
            {
                result.Add(
                    (
                        sortie.SourceLines[i],
                        sortie.Points[i * 2],
                        sortie.Points[i * 2 + 1]));
            }
            return result;
        }

        List<string> lineOrder = sortie.Points
            .Select(x => x.Line?.Trim() ?? "")
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (string lineName in lineOrder)
        {
            List<RouteEndpoint> points = sortie.Points
                .Where(x =>
                    (x.Line ?? "").Trim().Equals(
                        lineName,
                        StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (points.Count < 2)
                continue;

            RouteEndpoint first = points[0];
            RouteEndpoint second = points[1];
            double best = double.NegativeInfinity;
            for (int i = 0; i < points.Count; i++)
            {
                for (int j = i + 1; j < points.Count; j++)
                {
                    double dx = points[i].X - points[j].X;
                    double dy = points[i].Y - points[j].Y;
                    double d2 = dx * dx + dy * dy;
                    if (d2 <= best)
                        continue;

                    best = d2;
                    first = points[i];
                    second = points[j];
                }
            }

            result.Add(
                (
                    lineName,
                    first,
                    second));
        }

        return result;
    }

    private static string MotherSurveyLineTaskId(string lineName)
    {
        string canonical = lineName.Trim().ToUpperInvariant();
        string hash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
        return $"SLM-{hash[..16]}";
    }

    private static string SurveyLineTaskId(
        string lineName,
        GeoCoordinate start,
        GeoCoordinate end)
    {
        string first =
            $"{start.Longitude:F7},{start.Latitude:F7}";
        string second =
            $"{end.Longitude:F7},{end.Latitude:F7}";
        if (string.Compare(
                first,
                second,
                StringComparison.Ordinal) > 0)
        {
            (first, second) =
                (second, first);
        }

        // The survey line is the atomic task. Its identity must survive a
        // different sortie grouping or a reversed flight direction.
        string canonical =
            $"{lineName.Trim().ToUpperInvariant()}|" +
            $"{first}|{second}";
        string hash =
            Convert.ToHexString(
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
        return $"SL-{hash[..16]}";
    }

    private static IReadOnlyList<SurveyTrackPoint> BuildSurveyTrackPoints(
        FieldMagFolderAnalysis? preparedData,
        FieldSourceSummary uav)
    {
        if (preparedData is not null)
        {
            return preparedData.UavSamples
                .Where(x =>
                    x.Longitude.HasValue &&
                    x.Latitude.HasValue &&
                    !x.SensorStatus.Equals(
                        "F",
                        StringComparison.OrdinalIgnoreCase))
                // QuSpin files commonly record several magnetic rows per second.
                // Downsample independently per source file; grouping only by UTC
                // second silently discarded simultaneous/overlapping UAV files and
                // could make real flown survey lines disappear from field progress.
                .GroupBy(
                    x => x.SourceFile,
                    StringComparer.OrdinalIgnoreCase)
                .SelectMany(fileGroup =>
                    fileGroup
                        .GroupBy(x => x.TimestampUtc)
                        .Select(group =>
                            group
                                .OrderBy(x => x.QualityFlags.Count)
                                .ThenBy(x => x.SourceRecord)
                                .First()))
                .OrderBy(x => x.SourceFile, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.TimestampUtc)
                .ThenBy(x => x.SourceRecord)
                .Select(x =>
                    new SurveyTrackPoint(
                        x.TimestampUtc,
                        new GeoCoordinate(
                            x.Longitude!.Value,
                            x.Latitude!.Value),
                        x.SourceFile,
                        x.SourceRecord)
                    { AltitudeM = x.AltitudeM })
                .ToList();
        }

        DateTimeOffset synthetic =
            DateTimeOffset.MinValue;
        return uav.Track
            .Select((point, index) =>
                new SurveyTrackPoint(
                    synthetic.AddSeconds(index),
                    point,
                    "compatibility-track",
                    index + 1))
            .ToList();
    }

    private static FieldRouteSnapshot BuildSnapshot(
        string sourcePath,
        string sourceLabel,
        DateTime modifiedAt,
        IReadOnlyList<PlannedFieldSortie> sorties,
        IReadOnlyList<PlannedSurveyLineTask>? surveyLines = null,
        IReadOnlyList<RepeatedFieldRouteMeasurement>? repeatedMeasurements = null)
    {
        using var sha = SHA256.Create();
        var sb = new StringBuilder();
        sb.AppendLine("FIELD-PROGRESS-ROUTE-FINGERPRINT|7285|distinct-kml-tasks|temporal-repeat-flight");
        foreach (PlannedFieldSortie sortie in sorties.OrderBy(x => x.Order))
        {
            sb.Append(sortie.Order).Append('|').Append(sortie.Name).Append('|');
            foreach (GeoCoordinate point in CanonicalFingerprintSamples(sortie.Coordinates))
            {
                sb.Append(point.Longitude.ToString("F5", CultureInfo.InvariantCulture))
                    .Append(',')
                    .Append(point.Latitude.ToString("F5", CultureInfo.InvariantCulture))
                    .Append(';');
            }
            sb.AppendLine();
        }
        if (repeatedMeasurements is not null)
        {
            foreach (RepeatedFieldRouteMeasurement repeat in repeatedMeasurements
                .OrderBy(x => x.LogicalName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.MeasurementTimeUtc))
            {
                sb.Append("REPEAT|")
                    .Append(repeat.LogicalName)
                    .Append('|')
                    .Append(repeat.SourceSha256)
                    .Append('|')
                    .Append(repeat.MeasurementTimeUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))
                    .Append('|');
                foreach (GeoCoordinate point in CanonicalFingerprintSamples(repeat.Coordinates))
                {
                    sb.Append(point.Longitude.ToString("F5", CultureInfo.InvariantCulture))
                        .Append(',')
                        .Append(point.Latitude.ToString("F5", CultureInfo.InvariantCulture))
                        .Append(';');
                }
                sb.AppendLine();
            }
        }

        if (surveyLines is not null)
        {
            foreach (PlannedSurveyLineTask line in surveyLines.OrderBy(x => x.Order))
            {
                sb.Append("LINE|")
                    .Append(line.TaskId)
                    .Append('|')
                    .Append(line.SortieName)
                    .Append('|')
                    .Append(line.LineName)
                    .Append('|')
                    .Append(line.LengthM.ToString("F2", CultureInfo.InvariantCulture))
                    .AppendLine();
            }
        }

        string fingerprint = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
        return new FieldRouteSnapshot(sourcePath, sourceLabel, fingerprint, modifiedAt, sorties)
        {
            SurveyLines = surveyLines ?? [],
            RepeatedMeasurements =
                repeatedMeasurements ?? []
        };
    }

    private static IReadOnlyList<GeoCoordinate> CanonicalFingerprintSamples(IReadOnlyList<GeoCoordinate> points)
    {
        if (points.Count == 0)
            return [];
        if (points.Count == 1)
            return [points[0]];

        var cumulative = new double[points.Count];
        for (int i = 1; i < points.Count; i++)
        {
            cumulative[i] = cumulative[i - 1] + RouteCoordinateMath.Haversine(
                points[i - 1].Longitude, points[i - 1].Latitude,
                points[i].Longitude, points[i].Latitude);
        }
        double total = cumulative[^1];
        if (!double.IsFinite(total) || total <= 0.01d)
            return [points[0], points[^1]];

        const int sampleCount = 13;
        var result = new List<GeoCoordinate>(sampleCount);
        int segment = 1;
        for (int sample = 0; sample < sampleCount; sample++)
        {
            double target = total * sample / (sampleCount - 1d);
            while (segment < cumulative.Length - 1 && cumulative[segment] < target)
                segment++;
            int previous = Math.Max(0, segment - 1);
            double span = Math.Max(1e-9d, cumulative[segment] - cumulative[previous]);
            double t = Math.Clamp((target - cumulative[previous]) / span, 0d, 1d);
            GeoCoordinate a = points[previous];
            GeoCoordinate b = points[segment];
            result.Add(new GeoCoordinate(
                a.Longitude + (b.Longitude - a.Longitude) * t,
                a.Latitude + (b.Latitude - a.Latitude) * t));
        }
        return result;
    }

    private static string ResolveProjectMode(RouteProjectFile project)
    {
        if (project.CoordMode.Contains("经纬", StringComparison.OrdinalIgnoreCase))
            return "geographic";
        if (project.CoordMode.Contains("平面", StringComparison.OrdinalIgnoreCase))
            return "projected";
        return project.Sorties.SelectMany(x => x.Points).All(p =>
            (p.X is >= -180 and <= 180) && (p.Y is >= -90 and <= 90))
            ? "geographic"
            : "projected";
    }

    public static IReadOnlyList<string> DailySourcePaths(string root, DateTime date) => DiscoverDailyFiles(root, date, strict: true);

    private static List<string> DiscoverDailyFiles(string workareaRoot, DateTime date, bool strict = false)
    {
        var folderLayout = new FolderLayoutService();
        string[] roots =
        [
            folderLayout.ResolveWorkareaFolderFromRoot(workareaRoot, "workarea.raw"),
            folderLayout.ResolveWorkareaFolderFromRoot(workareaRoot, "workarea.field")
        ];
        string[] generatedOperationKeys =
        [
            "equipment_checklist_export", "equipment_test_export",
            "field_daily_progress", "field_takeoff_export", "daily_log_export",
            "issue_evidence", "field_qc_export", "field_data_integration",
            "field_gemlink_bridge", "processing_diurnal", "processing_excel",
            "processing_oasis", "processing_analysis", "figure_export", "report_export"
        ];
        string[] generatedOutputDirectories = folderLayout.Load().Operations
            .Where(operation => operation.Scope.Equals("workarea", StringComparison.OrdinalIgnoreCase) &&
                                generatedOperationKeys.Contains(operation.Key, StringComparer.OrdinalIgnoreCase))
            .Select(operation => folderLayout.ResolveOperationDirectory(
                workareaRoot,
                operation.Key,
                workareaRelativePath: ".",
                timestamp: new DateTimeOffset(
                    date.Date,
                    TimeZoneInfo.Local.GetUtcOffset(date.Date))))
            .Where(directory => roots.Any(root => IsPathWithin(root, directory)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var result = new List<string>();
        foreach (string root in roots.Where(Directory.Exists))
        {
            try
            {
                foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    if (IsGeneratedFieldOutputPath(file, generatedOutputDirectories))
                        continue;
                    string ext = Path.GetExtension(file).ToLowerInvariant();
                    if (!TextExtensions.Contains(ext) && ext is not ".gpx" and not ".kml")
                        continue;
                    if (MatchesDate(file, date))
                        result.Add(Path.GetFullPath(file));
                }
            }
            catch (UnauthorizedAccessException) when (!strict)
            {
                // Continue scanning other governed roots.
            }
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }


    private static bool IsGeneratedFieldOutputPath(
        string file,
        IReadOnlyList<string> generatedOutputDirectories)
    {
        // Use the effective folder configuration so renamed or relocated MagOps
        // outputs never re-enter raw UAV/Base discovery as new observations.
        return generatedOutputDirectories.Any(directory => IsPathWithin(directory, file));
    }

    private static bool IsPathWithin(string directory, string path)
    {
        string root = Path.GetFullPath(directory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string candidate = Path.GetFullPath(path);
        return candidate.Equals(root, StringComparison.OrdinalIgnoreCase) ||
               candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
               candidate.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesDate(string path, DateTime date)
    {
        string name = Path.GetFileName(path);
        string[] tokens =
        [
            date.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
            date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            date.ToString("yyyy_MM_dd", CultureInfo.InvariantCulture)
        ];
        if (tokens.Any(token => name.Contains(token, StringComparison.OrdinalIgnoreCase)))
            return true;
        try
        {
            return File.GetLastWriteTime(path).Date == date.Date;
        }
        catch
        {
            return false;
        }
    }

    private static void AddManualFiles(List<string> target, IEnumerable<string>? paths)
    {
        if (paths is null)
            return;
        foreach (string path in paths.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            string full = Path.GetFullPath(path.Trim());
            if (File.Exists(full) && !target.Contains(full, StringComparer.OrdinalIgnoreCase))
                target.Add(full);
        }
    }

    private static string Classify(string path)
    {
        string text = (Path.GetFileName(path) + " " + ReadProbe(path)).ToLowerInvariant();
        if (GsmHints.Any(hint => text.Contains(hint, StringComparison.OrdinalIgnoreCase)))
            return "GSM19T";
        if (UavHints.Any(hint => text.Contains(hint, StringComparison.OrdinalIgnoreCase)))
            return "UAV_MAG";

        string ext = Path.GetExtension(path).ToLowerInvariant();
        // GSM-19T station exports commonly use .stn. When no stronger filename/content
        // hint exists, treating .stn as the ground magnetometer source is safer than
        // silently dropping the file from the same-day source summary.
        if (ext == ".stn")
            return "GSM19T";
        if (ext is ".gpx" or ".kml")
            return "UAV_MAG";
        return HasCoordinateHeader(text) && (text.Contains("mag", StringComparison.OrdinalIgnoreCase) || text.Contains("磁", StringComparison.Ordinal))
            ? "UAV_MAG"
            : "";
    }

    private static string ReadProbe(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".gpx" or ".kml")
            return "";
        try
        {
            using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var sb = new StringBuilder();
            for (int i = 0; i < 12 && !reader.EndOfStream; i++)
                sb.AppendLine(reader.ReadLine());
            return sb.ToString();
        }
        catch
        {
            return "";
        }
    }

    private static bool HasCoordinateHeader(string text) =>
        LonHeaders.Any(hint => text.Contains(hint, StringComparison.OrdinalIgnoreCase)) &&
        LatHeaders.Any(hint => text.Contains(hint, StringComparison.OrdinalIgnoreCase));

    private static FieldSourceSummary ReadSourceSummary(string kind, IReadOnlyList<string> files)
    {
        var allTrack = new List<GeoCoordinate>();
        int samples = 0;
        DateTimeOffset? first = null;
        DateTimeOffset? last = null;
        var warnings = new List<string>();

        foreach (string file in files)
        {
            try
            {
                ParsedFieldFile parsed = ParseFile(file);
                samples += parsed.SampleCount;
                AppendTrack(allTrack, parsed.Track);
                if (parsed.FirstTime.HasValue && (!first.HasValue || parsed.FirstTime.Value < first.Value))
                    first = parsed.FirstTime;
                if (parsed.LastTime.HasValue && (!last.HasValue || parsed.LastTime.Value > last.Value))
                    last = parsed.LastTime;
                warnings.AddRange(parsed.Warnings.Select(x => $"{Path.GetFileName(file)}：{x}"));
            }
            catch (Exception ex)
            {
                warnings.Add($"{Path.GetFileName(file)}：读取失败（{ex.Message}）");
            }
        }

        IReadOnlyList<GeoCoordinate> track = DownsampleTrack(allTrack, 20d, 60000);
        return new FieldSourceSummary(kind, files, samples, track.Count, first, last, track, warnings);
    }

    private static ParsedFieldFile ParseFile(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".gpx")
        {
            List<GeoCoordinate> points = SpatialGeometryService.ParseGpx(path)
                .SelectMany(x => x.Coordinates)
                .ToList();
            return new ParsedFieldFile(points.Count, points, null, null, []);
        }
        if (ext == ".kml")
        {
            List<GeoCoordinate> points = SpatialGeometryService.ParseKml(path)
                .Where(x => x.Type is "Track" or "LineString")
                .SelectMany(x => x.Coordinates)
                .ToList();
            return new ParsedFieldFile(points.Count, points, null, null, []);
        }
        return ParseDelimitedText(path);
    }

    private static ParsedFieldFile ParseDelimitedText(string path)
    {
        const int maxRows = 250000;
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var preview = new List<string[]>();
        string? firstLine = null;
        char delimiter = ',';
        while (!reader.EndOfStream && preview.Count < 40)
        {
            string? line = reader.ReadLine();
            if (string.IsNullOrWhiteSpace(line) || IsComment(line))
                continue;
            firstLine ??= line;
            delimiter = DetectDelimiter(firstLine);
            preview.Add(Split(line, delimiter));
        }

        if (preview.Count == 0)
            return new ParsedFieldFile(0, [], null, null, ["文件中没有可读取的数据行"]);

        bool hasHeader = preview[0].Any(token => !TryNumber(token, out _));
        string[] header = hasHeader ? preview[0] : [];
        List<string[]> dataPreview = hasHeader ? preview.Skip(1).ToList() : preview;
        int lonIndex = hasHeader ? FindHeader(header, LonHeaders) : -1;
        int latIndex = hasHeader ? FindHeader(header, LatHeaders) : -1;
        if ((lonIndex < 0 || latIndex < 0) && dataPreview.Count > 0)
            (lonIndex, latIndex) = InferLonLatColumns(dataPreview);
        int timeIndex = hasHeader ? FindHeader(header, TimeHeaders) : -1;
        int dateIndex = hasHeader ? FindHeader(header, DateHeaders) : -1;

        var track = new List<GeoCoordinate>();
        DateTimeOffset? firstTime = null;
        DateTimeOffset? lastTime = null;
        int rows = 0;

        void Consume(string[] fields)
        {
            if (fields.Length == 0)
                return;
            rows++;
            if (lonIndex >= 0 && latIndex >= 0 && lonIndex < fields.Length && latIndex < fields.Length &&
                TryNumber(fields[lonIndex], out double lon) && TryNumber(fields[latIndex], out double lat) &&
                IsLonLat(lon, lat))
            {
                track.Add(new GeoCoordinate(lon, lat));
            }

            DateTimeOffset? stamp = ParseTimestamp(fields, dateIndex, timeIndex);
            if (stamp.HasValue)
            {
                if (!firstTime.HasValue || stamp < firstTime)
                    firstTime = stamp;
                if (!lastTime.HasValue || stamp > lastTime)
                    lastTime = stamp;
            }
        }

        foreach (string[] row in dataPreview)
            Consume(row);

        while (!reader.EndOfStream && rows < maxRows)
        {
            string? line = reader.ReadLine();
            if (string.IsNullOrWhiteSpace(line) || IsComment(line))
                continue;
            Consume(Split(line, delimiter));
        }

        var warnings = new List<string>();
        if (rows >= maxRows && !reader.EndOfStream)
            warnings.Add($"记录超过 {maxRows} 行，本次仅读取前 {maxRows} 行用于进度估算");
        if (lonIndex < 0 || latIndex < 0)
            warnings.Add("未识别经纬度列；该文件只计入资料记录数，不参与航迹匹配");
        return new ParsedFieldFile(rows, track, firstTime, lastTime, warnings);
    }

    private static DateTimeOffset? ParseTimestamp(string[] fields, int dateIndex, int timeIndex)
    {
        string date = dateIndex >= 0 && dateIndex < fields.Length ? fields[dateIndex].Trim() : "";
        string time = timeIndex >= 0 && timeIndex < fields.Length ? fields[timeIndex].Trim() : "";
        string combined = date.Length > 0 && time.Length > 0 && dateIndex != timeIndex
            ? date + " " + time
            : time.Length > 0 ? time : date;
        if (combined.Length == 0)
            return null;
        return DateTimeOffset.TryParse(combined, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out DateTimeOffset parsed)
            || DateTimeOffset.TryParse(combined, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out parsed)
                ? parsed
                : null;
    }

    private static char DetectDelimiter(string line)
    {
        var counts = new Dictionary<char, int>
        {
            [','] = line.Count(c => c == ','),
            ['\t'] = line.Count(c => c == '\t'),
            [';'] = line.Count(c => c == ';')
        };
        KeyValuePair<char, int> best = counts.OrderByDescending(x => x.Value).First();
        return best.Value > 0 ? best.Key : ' ';
    }

    private static string[] Split(string line, char delimiter) => delimiter == ' '
        ? line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        : line.Split(delimiter).Select(x => x.Trim().Trim('"')).ToArray();

    private static bool IsComment(string line)
    {
        string trimmed = line.TrimStart();
        return trimmed.StartsWith("#", StringComparison.Ordinal) ||
               trimmed.StartsWith("//", StringComparison.Ordinal) ||
               trimmed.StartsWith(";", StringComparison.Ordinal);
    }

    private static int FindHeader(IReadOnlyList<string> headers, IReadOnlyList<string> candidates)
    {
        for (int i = 0; i < headers.Count; i++)
        {
            string normalized = NormalizeHeader(headers[i]);
            if (candidates.Any(candidate => normalized == NormalizeHeader(candidate) || normalized.Contains(NormalizeHeader(candidate), StringComparison.OrdinalIgnoreCase)))
                return i;
        }
        return -1;
    }

    private static string NormalizeHeader(string value) => new(
        (value ?? "")
            .Trim()
            .ToLowerInvariant()
            .Where(ch => char.IsLetterOrDigit(ch) || ch >= 0x4e00)
            .ToArray());

    private static (int Lon, int Lat) InferLonLatColumns(IReadOnlyList<string[]> rows)
    {
        int maxColumns = Math.Min(16, rows.Max(x => x.Length));
        int bestLon = -1;
        int bestLat = -1;
        int bestScore = 0;
        for (int lon = 0; lon < maxColumns; lon++)
        {
            for (int lat = 0; lat < maxColumns; lat++)
            {
                if (lon == lat)
                    continue;
                int score = 0;
                foreach (string[] row in rows)
                {
                    if (lon >= row.Length || lat >= row.Length)
                        continue;
                    if (TryNumber(row[lon], out double x) && TryNumber(row[lat], out double y) && IsLonLat(x, y))
                        score++;
                }
                if (score > bestScore)
                {
                    bestScore = score;
                    bestLon = lon;
                    bestLat = lat;
                }
            }
        }
        int required = Math.Max(2, rows.Count / 2);
        return bestScore >= required ? (bestLon, bestLat) : (-1, -1);
    }

    private static bool TryNumber(string value, out double result) =>
        double.TryParse(value.Trim().Trim('"'), NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out result) ||
        double.TryParse(value.Trim().Trim('"'), NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out result);

    private static bool IsLonLat(double lon, double lat) =>
        double.IsFinite(lon) && double.IsFinite(lat) && lon is >= -180 and <= 180 && lat is >= -90 and <= 90 &&
        !(Math.Abs(lon) < 1e-12 && Math.Abs(lat) < 1e-12);

    private static IReadOnlyList<GeoCoordinate> DownsampleTrack(
        IReadOnlyList<GeoCoordinate> points,
        double minSpacingM,
        int maxPoints)
    {
        if (points.Count <= 1)
            return points.ToList();
        var result = new List<GeoCoordinate> { points[0] };
        GeoCoordinate previous = points[0];
        for (int i = 1; i < points.Count; i++)
        {
            GeoCoordinate current = points[i];
            if (RouteCoordinateMath.Haversine(previous.Longitude, previous.Latitude, current.Longitude, current.Latitude) < minSpacingM)
                continue;
            result.Add(current);
            previous = current;
            if (result.Count >= maxPoints)
                break;
        }
        if (result.Count < maxPoints && result[^1] != points[^1])
            result.Add(points[^1]);
        return result;
    }

    private static void AppendTrack(List<GeoCoordinate> target, IReadOnlyList<GeoCoordinate> source)
    {
        foreach (GeoCoordinate point in source)
        {
            if (target.Count > 0)
            {
                GeoCoordinate last = target[^1];
                if (Math.Abs(last.Longitude - point.Longitude) < 1e-12 && Math.Abs(last.Latitude - point.Latitude) < 1e-12)
                    continue;
            }
            target.Add(point);
        }
    }

    private static IReadOnlyList<FieldRepeatedSortiePassEstimate> DetectQualifyingSortiePasses(
        PlannedFieldSortie sortie,
        IReadOnlyList<SurveyTrackPoint> timedTrackPoints,
        double toleranceM)
    {
        if (sortie.Coordinates.Count < 2 ||
            timedTrackPoints.Count == 0)
        {
            return [];
        }

        IReadOnlyList<GeoCoordinate> routeSamples =
            Resample(
                sortie.Coordinates,
                Math.Max(
                    5d,
                    toleranceM * 0.75d));
        if (routeSamples.Count == 0)
            return [];

        var routeGrid =
            new TrackGrid(
                routeSamples,
                toleranceM);

        List<SurveyTrackPoint> matched =
            timedTrackPoints
                .Where(x =>
                    routeGrid.IsNearTrack(
                        x.Coordinate))
                .OrderBy(x => x.TimestampUtc)
                .ToList();
        if (matched.Count <
            RepeatedFlightMinimumMatchedPoints)
        {
            return [];
        }

        var groups =
            new List<List<SurveyTrackPoint>>();
        var current =
            new List<SurveyTrackPoint>();
        foreach (SurveyTrackPoint point in matched)
        {
            if (current.Count > 0)
            {
                double gapMinutes =
                    (point.TimestampUtc -
                     current[^1].TimestampUtc)
                        .TotalMinutes;
                if (gapMinutes >=
                    RepeatedFlightTimeGapMinutes)
                {
                    groups.Add(current);
                    current =
                        new List<SurveyTrackPoint>();
                }
            }
            current.Add(point);
        }
        if (current.Count > 0)
            groups.Add(current);

        var passes =
            new List<FieldRepeatedSortiePassEstimate>();
        foreach (List<SurveyTrackPoint> group in groups)
        {
            if (group.Count <
                RepeatedFlightMinimumMatchedPoints)
            {
                continue;
            }

            IReadOnlyList<GeoCoordinate> passTrack =
                group
                    .Select(x => x.Coordinate)
                    .ToList();
            var passGrid =
                new TrackGrid(
                    passTrack,
                    toleranceM);
            double coverage =
                EstimateCoverage(
                    sortie.Coordinates,
                    passGrid);
            if (coverage <
                CompletionThreshold)
            {
                continue;
            }

            passes.Add(
                new FieldRepeatedSortiePassEstimate(
                    sortie.Name,
                    group[0].TimestampUtc,
                    group[^1].TimestampUtc,
                    coverage,
                    group.Count));
        }

        return passes;
    }

    private static string BuildRepeatedFlightDiagnostic(
        IReadOnlyList<FieldRepeatedSortiePassEstimate> qualifyingPasses,
        bool completedBefore, TimeZoneInfo timeZone)
    {
        if (qualifyingPasses.Count == 0)
            return "";

        FieldRepeatedSortiePassEstimate latest =
            qualifyingPasses[^1];
        int repeatPassCount =
            completedBefore
                ? qualifyingPasses.Count
                : Math.Max(
                    0,
                    qualifyingPasses.Count - 1);

        DateTimeOffset start =
            ProjectTime.Display(latest.StartUtc, timeZone);
        DateTimeOffset end =
            ProjectTime.Display(latest.EndUtc, timeZone);

        return
            $"；识别重复飞行 {repeatPassCount} 次（同任务完整覆盖时段间隔 ≥ " +
            $"{RepeatedFlightTimeGapMinutes:0.#} min），最近后测时段 " +
            $"{start:HH:mm:ss}–{end:HH:mm:ss} {ProjectTime.Label(timeZone)}；重复飞行不重复计入完成架次";
    }

    private static double EstimateCoverage(IReadOnlyList<GeoCoordinate> route, TrackGrid grid)
    {
        IReadOnlyList<GeoCoordinate> samples = Resample(route, 100d);
        if (samples.Count == 0)
            return 0d;
        int matched = samples.Count(grid.IsNearTrack);
        return (double)matched / samples.Count;
    }

    private static IReadOnlyList<GeoCoordinate> Resample(IReadOnlyList<GeoCoordinate> points, double spacingM)
    {
        if (points.Count == 0)
            return [];
        if (points.Count == 1)
            return [points[0]];

        var result = new List<GeoCoordinate> { points[0] };
        for (int i = 1; i < points.Count; i++)
        {
            GeoCoordinate a = points[i - 1];
            GeoCoordinate b = points[i];
            double distance = RouteCoordinateMath.Haversine(a.Longitude, a.Latitude, b.Longitude, b.Latitude);
            int steps = Math.Max(1, (int)Math.Ceiling(distance / spacingM));
            for (int step = 1; step <= steps; step++)
            {
                double t = (double)step / steps;
                result.Add(new GeoCoordinate(
                    a.Longitude + (b.Longitude - a.Longitude) * t,
                    a.Latitude + (b.Latitude - a.Latitude) * t));
            }
        }
        return result;
    }

    private static IReadOnlyList<FieldTakeoffRecommendation> BuildTakeoffRecommendations(
        FieldRouteSnapshot route,
        IReadOnlyList<string> remaining,
        int expectedDailySorties,
        int acceptedRelocations)
    {
        if (remaining.Count == 0)
            return [];

        int dailyCapacity = Math.Max(1, expectedDailySorties);
        int maxLocationsPerDay = Math.Max(1, acceptedRelocations + 1);
        Dictionary<string, PlannedFieldSortie> byName = route.Sorties
            .Where(x => x.Coordinates.Count > 0)
            .ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);

        var result = new List<FieldTakeoffRecommendation>();
        int globalOrder = 1;
        int planDay = 1;
        for (int offset = 0; offset < remaining.Count; offset += dailyCapacity, planDay++)
        {
            List<PlannedFieldSortie> targets = remaining
                .Skip(offset)
                .Take(dailyCapacity)
                .Where(byName.ContainsKey)
                .Select(name => byName[name])
                .OrderBy(x => x.Order)
                .ToList();
            if (targets.Count == 0)
                continue;

            int clusterCount = Math.Min(targets.Count, maxLocationsPerDay);
            List<int> medoids = InitializeMedoids(targets, clusterCount);
            int[] assignments = new int[targets.Count];

            for (int iteration = 0; iteration < 12; iteration++)
            {
                for (int i = 0; i < targets.Count; i++)
                {
                    assignments[i] = Enumerable.Range(0, medoids.Count)
                        .OrderBy(cluster => Distance(targets[i].Coordinates[0], targets[medoids[cluster]].Coordinates[0]))
                        .First();
                }

                bool changed = false;
                for (int cluster = 0; cluster < medoids.Count; cluster++)
                {
                    int[] members = Enumerable.Range(0, targets.Count)
                        .Where(i => assignments[i] == cluster)
                        .ToArray();
                    if (members.Length == 0)
                        continue;
                    int best = members
                        .OrderBy(candidate => members.Sum(other => Distance(targets[candidate].Coordinates[0], targets[other].Coordinates[0])))
                        .ThenBy(candidate => targets[candidate].Order)
                        .First();
                    if (best != medoids[cluster])
                    {
                        medoids[cluster] = best;
                        changed = true;
                    }
                }
                if (!changed)
                    break;
            }

            var clusters = Enumerable.Range(0, medoids.Count)
                .Select(cluster => new
                {
                    Cluster = cluster,
                    Members = Enumerable.Range(0, targets.Count).Where(i => assignments[i] == cluster).ToArray()
                })
                .Where(x => x.Members.Length > 0)
                .OrderBy(x => x.Members.Min(i => targets[i].Order))
                .ToArray();

            for (int locationIndex = 0; locationIndex < clusters.Length; locationIndex++)
            {
                var cluster = clusters[locationIndex];
                int medoidIndex = medoids[cluster.Cluster];
                GeoCoordinate point = targets[medoidIndex].Coordinates[0];
                double[] distances = cluster.Members
                    .Select(i => Distance(point, targets[i].Coordinates[0]))
                    .ToArray();
                result.Add(new FieldTakeoffRecommendation(
                    globalOrder++,
                    planDay,
                    $"D{planDay:D2}-P{locationIndex + 1:D2}",
                    point,
                    cluster.Members.Select(i => targets[i].Name).OrderBy(name => byName[name].Order).ToList(),
                    distances.Length == 0 ? 0d : distances.Average(),
                    distances.Length == 0 ? 0d : distances.Max()));
            }
        }
        return result;
    }

    private static List<int> InitializeMedoids(IReadOnlyList<PlannedFieldSortie> targets, int count)
    {
        var medoids = new List<int> { 0 };
        while (medoids.Count < count)
        {
            int next = Enumerable.Range(0, targets.Count)
                .Where(i => !medoids.Contains(i))
                .OrderByDescending(i => medoids.Min(m => Distance(targets[i].Coordinates[0], targets[m].Coordinates[0])))
                .ThenBy(i => targets[i].Order)
                .First();
            medoids.Add(next);
        }
        return medoids;
    }

    private static double Distance(GeoCoordinate a, GeoCoordinate b) =>
        RouteCoordinateMath.Haversine(a.Longitude, a.Latitude, b.Longitude, b.Latitude);

    private static string FormatTimeSpan(DateTimeOffset? first, DateTimeOffset? last, TimeZoneInfo timeZone)
    {
        if (!first.HasValue && !last.HasValue)
            return "";
        if (first.HasValue && last.HasValue)
            return $"（{ProjectTime.Label(timeZone)} {ProjectTime.Display(first.Value, timeZone):MM-dd HH:mm}–{ProjectTime.Display(last.Value, timeZone):MM-dd HH:mm}）";
        DateTimeOffset value = first ?? last ?? DateTimeOffset.MinValue;
        return $"（{ProjectTime.Label(timeZone)} {ProjectTime.Display(value, timeZone):MM-dd HH:mm}）";
    }

    private static string NaturalSortKey(string value)
    {
        var sb = new StringBuilder();
        foreach (char ch in value)
        {
            if (char.IsDigit(ch))
                sb.Append(ch);
            else
                sb.Append(char.ToUpperInvariant(ch));
        }
        return sb.ToString();
    }

    private static string Csv(string value)
    {
        string text = value ?? "";
        if (text.Contains('"'))
            text = text.Replace("\"", "\"\"");
        return text.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? $"\"{text}\"" : text;
    }

    private sealed record ImportedSortieCandidate(
        string LogicalName,
        IReadOnlyList<GeoCoordinate> Coordinates,
        string ImportedPath,
        string ImportedFileName,
        string SourceSha256,
        DateTimeOffset MeasurementTimeUtc,
        int GeometryIndex);

    private sealed record ParsedFieldFile(
        int SampleCount,
        IReadOnlyList<GeoCoordinate> Track,
        DateTimeOffset? FirstTime,
        DateTimeOffset? LastTime,
        IReadOnlyList<string> Warnings);

    private sealed class TrackGrid
    {
        private readonly double _originLon;
        private readonly double _originLat;
        private readonly double _cosLat;
        private readonly double _cellSize;
        private readonly double _tolerance;
        private readonly Dictionary<(int X, int Y), List<(double X, double Y)>> _cells = new();

        public TrackGrid(IReadOnlyList<GeoCoordinate> track, double toleranceM)
        {
            _originLon = track.Average(x => x.Longitude);
            _originLat = track.Average(x => x.Latitude);
            _cosLat = Math.Max(0.05d, Math.Cos(_originLat * Math.PI / 180d));
            _cellSize = Math.Max(1d, toleranceM);
            _tolerance = Math.Max(1d, toleranceM);
            foreach (GeoCoordinate point in track)
            {
                (double x, double y) = ToMeters(point);
                var key = Cell(x, y);
                if (!_cells.TryGetValue(key, out List<(double X, double Y)>? list))
                {
                    list = [];
                    _cells[key] = list;
                }
                list.Add((x, y));
            }
        }

        public bool IsNearTrack(GeoCoordinate point)
        {
            (double x, double y) = ToMeters(point);
            (int cx, int cy) = Cell(x, y);
            double limit2 = _tolerance * _tolerance;
            int radius = Math.Max(1, (int)Math.Ceiling(_tolerance / _cellSize));
            for (int dx = -radius; dx <= radius; dx++)
            {
                for (int dy = -radius; dy <= radius; dy++)
                {
                    if (!_cells.TryGetValue((cx + dx, cy + dy), out List<(double X, double Y)>? values))
                        continue;
                    foreach ((double px, double py) in values)
                    {
                        double ex = px - x;
                        double ey = py - y;
                        if (ex * ex + ey * ey <= limit2)
                            return true;
                    }
                }
            }
            return false;
        }

        private (double X, double Y) ToMeters(GeoCoordinate point)
        {
            double x = (point.Longitude - _originLon) * 111320d * _cosLat;
            double y = (point.Latitude - _originLat) * 110540d;
            return (x, y);
        }

        private (int X, int Y) Cell(double x, double y) =>
            ((int)Math.Floor(x / _cellSize), (int)Math.Floor(y / _cellSize));
    }
}
