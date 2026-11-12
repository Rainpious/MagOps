using MagOps.Core.Domain;
using System.Diagnostics;
using System.Text.Json;

namespace MagOps.Infrastructure.Services;

public sealed record AuditCalculationStage(string Name, double Milliseconds, bool Reused);
public sealed record PassQualityConfirmation(string PassId, string QualityStatus, string ConfirmedBy);
public sealed record ManualFlightLegAssignment(string FlightLegId, string SurveyTaskStableId, string Reason);
public sealed record AuditProductionRequest(
    string SourceFingerprint,
    IReadOnlyList<MissionTimeWindow> MissionWindows,
    IReadOnlyList<PassQualityConfirmation> QualityConfirmations,
    IReadOnlyList<ProductionOverride> Overrides,
    bool AllowReviewDraft = false)
{
    /// <summary>Design-task snapshot used when the review decisions were made.</summary>
    public string RouteFingerprint { get; init; } = "";
    /// <summary>Passes explicitly checked by a person in the execution review UI.</summary>
    public IReadOnlyList<string> ConfirmedPassIds { get; init; } = [];
    /// <summary>When true, only checked passes may contribute to production selection.</summary>
    public bool RequirePassConfirmation { get; init; }
    /// <summary>Explicit user choice to continue processing coordinate-valid samples outside confirmed selections.</summary>
    public bool IncludeUnconfirmedSamples { get; init; }
    /// <summary>Per-leg manual assignment, kept separate from automatic match evidence.</summary>
    public IReadOnlyList<ManualFlightLegAssignment> ManualLegAssignments { get; init; } = [];
    /// <summary>Explicit spatial-CSV review capability; never relaxes formal design mission scopes.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool AllowActualLineRegrouping { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public MotherLineRestorationState? MotherLineRestoration { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool RenumberActualLines { get; init; }
    /// <summary>Unmatched flight legs explicitly included in a review-draft export.</summary>
    public IReadOnlyList<string> ManualIncludedFlightLegIds { get; init; } = [];
    /// <summary>Initial default inclusion has been applied; an empty saved selection is intentional.</summary>
    public bool DefaultMatchedSelectionsApplied { get; init; }
    public WorkareaScreeningInput? SpatialScreening { get; init; }
    public string ScreeningFingerprint { get; init; } = "";
    public string RecognitionVersion { get; init; } = "";
    public FlightQualityAssessmentOptions? FlightQualityOptions { get; init; }
    public TrackSegmentationInput? TrackSegmentation { get; init; }
    public string TrackSegmentationFingerprint { get; init; } = "";
    /// <summary>Project spatial-reference snapshot used by the desktop review.</summary>
    public double? ProjectCentralMeridian { get; init; }
    public ProductionAnomalyDetectionOptions ProductionAnomalyOptions { get; init; } = new();
    /// <summary>Explicit abnormal-point decisions; whole-leg inclusion is not an abnormal-point override.</summary>
    public IReadOnlyList<ProductionAnomalyDecision> AnomalyDecisions { get; init; } = [];
}
public sealed record AuditProductionResult(string Status, bool IsReviewDraft,
    SurveyExecutionResult Execution, IReadOnlyList<ProductionSelectionResult> Selections,
    IReadOnlyList<ExecutionSampleReference> SelectedSamples, IReadOnlyList<string> BlockedReasons)
{
    /// <summary>Point-level anomaly proposals applied to selected production rows.</summary>
    public IReadOnlyList<ProductionAnomalyInterval> ProductionAnomalies { get; init; } = [];
    public string ProductionSnapshotFingerprint { get; init; } = "";
    public string ProductionRequestFingerprint { get; init; } = "";
    public ProductionAnomalyDetectionOptions ProductionAnomalyOptions { get; init; } = new();
    public WorkareaScreeningResult? SpatialScreening { get; init; }
    public WorkareaScreeningInput? SpatialScreeningInput { get; init; }
    public IReadOnlyList<PlannedSurveyLineTask>? EffectiveTasks { get; init; }
    public IReadOnlyList<ActualLineNumber> ActualLineNumbers { get; init; } = [];
    public TrackSegmentationInput? TrackSegmentation { get; init; }
    public double? InferredAxisDegrees { get; init; }
    public IReadOnlyList<TaskTrackCoverage> RecognizedCoverage { get; init; } = [];
    public IReadOnlyList<TaskTrackCoverage> SelectedCoverage { get; init; } = [];
    public IReadOnlyList<TaskTrackCoverage> ProductionCsvCoverage { get; init; } = [];
    /// <summary>Validated explicit per-leg inclusions; distinct from blanket point-level draft export.</summary>
    public IReadOnlyList<string> ManualIncludedFlightLegIds { get; init; } = [];
}


/// <summary>Single production authority. Incomplete evidence can produce an explicitly requested editable draft.</summary>
public sealed class AuditProductionService
{
    // One review session owns this service. No global or on-disk recognition cache.
    private FieldMagFolderAnalysis? _cachedAnalysis;
    private string? _screeningKey;
    private WorkareaScreeningResult? _screening;
    private RecognitionSnapshot? _recognition;
    private SurveyExecutionResult? _evidenceInput;
    private string? _assignmentKey;
    private SurveyExecutionResult? _evidence;
    private string[] _evidenceWarnings = [];
    private SurveyExecutionResult? _anomalyInput;
    private string? _anomalyKey;
    private ProductionAnomalyInterval[] _detected = [];
    public int RecognitionRuns { get; private set; }
    public int RecognitionReuses { get; private set; }
    public IReadOnlyList<AuditCalculationStage> LastStages { get; private set; } = [];
    private sealed record RecognitionSnapshot(string Key, SurveyExecutionResult Execution,
        PlannedSurveyLineTask[] Tasks, double? Axis, string[] Warnings);

    public static string RecognitionInputFingerprint(FieldMagFolderAnalysis analysis,
        IReadOnlyList<PlannedSurveyLineTask> tasks, AuditProductionRequest? request, string routeFingerprint = "") =>
        TrackSegmentationReferenceService.Hash(JsonSerializer.Serialize(new
        {
            Source = analysis.Fingerprint, Route = routeFingerprint, Tasks = tasks,
            Windows = request?.MissionWindows.Select(w => w.TaskScopeSource == "AUTO"
                ? w with { SurveyTaskStableIds = [] } : w).ToArray(),
            request?.SpatialScreening, request?.TrackSegmentation, request?.ProjectCentralMeridian,
            Version = RecognitionVersion(request)
        }));

    public static string RecognitionVersion(AuditProductionRequest? request) => request?.TrackSegmentation?.Enabled == true
        ? TrackSegmentationService.AlgorithmVersion + "/" + WholeLegMatcherService.AlgorithmVersion : WholeLegMatcherService.AlgorithmVersion;

    public AuditProductionResult Analyze(FieldMagFolderAnalysis analysis,
        IReadOnlyList<PlannedSurveyLineTask> tasks, AuditProductionRequest? request,
        string routeFingerprint = "", bool reuseRecognition = false, Action<string>? progress = null)
    {
        var timer = Stopwatch.StartNew();
        var stages = new List<AuditCalculationStage>();
        double previousMs = 0;
        void Stage(string name, bool reused = false)
        {
            double elapsed = timer.Elapsed.TotalMilliseconds;
            stages.Add(new(name, elapsed - previousMs, reused));
            previousMs = elapsed;
            LastStages = stages.ToArray();
        }
        progress?.Invoke("核对数据版本与工区筛选…");
        var blocked = new List<string>();
        bool sourceMatches = request is not null && request.SourceFingerprint == analysis.Fingerprint;
        bool routeMatches = request is not null && request.RouteFingerprint == routeFingerprint;
        bool sameAnalysis = ReferenceEquals(analysis, _cachedAnalysis);
        if (!sameAnalysis) { _recognition = null; _evidenceInput = null; _anomalyInput = null; }
        string screeningKey = JsonSerializer.Serialize(new { request?.SpatialScreening, routeFingerprint });
        bool reuseScreening = reuseRecognition && sameAnalysis && screeningKey == _screeningKey;
        WorkareaScreeningResult? screening = reuseScreening ? _screening : request?.SpatialScreening is { } spatial
            ? new WorkareaSpatialService().Screen(analysis, spatial with { RouteFingerprint = routeFingerprint }) : null;
        _cachedAnalysis = analysis; _screeningKey = screeningKey; _screening = screening;
        Stage("screening", reuseScreening);
        var activeSamples = screening?.IncludedSamples ?? analysis.UavSamples;
        bool screeningMatches = screening is null || request?.ScreeningFingerprint == screening.Fingerprint;
        bool hasDecisions = request is not null && (request.QualityConfirmations.Count > 0 ||
            request.ConfirmedPassIds.Count > 0 || request.ManualLegAssignments.Count > 0 ||
            request.ManualIncludedFlightLegIds.Count > 0 || request.Overrides.Count > 0 || request.AnomalyDecisions.Count > 0);
        bool algorithmMatches = !hasDecisions || request?.RecognitionVersion == RecognitionVersion(request);
        bool trackInputMatches = request?.TrackSegmentationFingerprint == TrackSegmentationReferenceService.Fingerprint(request?.TrackSegmentation);
        if (!trackInputMatches)
            blocked.Add("TRACK_REFERENCE_CHANGED: 轨迹模式、方向或参考线分类已变化；旧决定不会沿用。");
        bool decisionsMatch = sourceMatches && routeMatches && screeningMatches && algorithmMatches && trackInputMatches;
        if (!algorithmMatches)
            blocked.Add("RECOGNITION_VERSION_CHANGED: 测线识别算法已更新，旧归属、QC 与生产区间决定需要重新核验。");
        if (!screeningMatches) blocked.Add("SPATIAL_REVISION_UNCONFIRMED: 工区筛选范围或数据选用已变化；旧核验决定不会沿用。");
        if (screening is not null)
            blocked.AddRange(screening.Warnings.Select(x => "SPATIAL_SCREENING_REVIEW: " + x));
        if (!sourceMatches)
            blocked.Add("SOURCE_REVISION_UNCONFIRMED: 输入指纹未确认或已变化。");
        if (!routeMatches)
            blocked.Add("ROUTE_REVISION_UNCONFIRMED: 航线任务版本未确认或已变化；旧核验决定不会沿用。");
        string recognitionKey = RecognitionInputFingerprint(analysis, tasks, request, routeFingerprint);
        bool reuseIdentity = reuseRecognition && sameAnalysis && _recognition?.Key == recognitionKey;
        SurveyExecutionResult execution;
        PlannedSurveyLineTask[] validTasks;
        double? inferredAxis;
        if (reuseIdentity)
        {
            execution = _recognition!.Execution; validTasks = _recognition.Tasks;
            inferredAxis = _recognition.Axis; blocked.AddRange(_recognition.Warnings);
            RecognitionReuses++;
        }
        else
        {
            progress?.Invoke("全量识别测线身份、连续航段与端点边界…");
            int warningsBefore = blocked.Count;
            var points = activeSamples.Where(p => p.Longitude.HasValue && p.Latitude.HasValue &&
                    WorkareaSpatialService.ValidCoordinate(p.Longitude.Value, p.Latitude.Value))
                .Select(p => new SurveyTrackPoint(p.TimestampUtc, new(p.Longitude!.Value, p.Latitude!.Value), p.SourceFile, p.SourceRecord)
                { AltitudeM = p.AltitudeM, RawMagNt = p.MagneticFieldNt }).ToArray();
            if (points.GroupBy(p => (p.SourceFile, p.SourceRecord)).Any(g => g.Count() > 1))
                throw new InvalidDataException("来源文件/记录索引重复，不能建立点级身份。");
            var windows = request?.MissionWindows ?? [];
            if (windows.Count == 0)
            {
                var inferred = new FlightWindowInferenceService().Infer(activeSamples);
                windows = inferred.Candidates.Select(x => x.Window).ToArray();
                blocked.Add(windows.Count == 0 ? "NO_MISSION_WINDOWS: 未能自动推断飞行窗口。" : $"INFERRED_MISSION_WINDOWS_REVIEW: 已推断 {windows.Count} 个窗口，边界待复核。");
            }
            TrackSegmentationResult? trackResult = null;
            if (request?.TrackSegmentation is { Enabled: true } trackInput)
            {
                trackResult = new TrackSegmentationService().Recognize(tasks, points, windows, trackInput);
                tasks = trackResult.Tasks;
                windows = windows.Select(w => w with { SurveyTaskStableIds = trackResult.Execution.Missions
                    .First(m => m.MissionExecutionId == w.MissionExecutionId).SurveyTaskStableIds }).ToArray();
                blocked.Add("ACTUAL_TRACK_REVIEW: 参考航线与实测推断供身份核验；不代表设计覆盖或质量已通过。");
            }
            if (tasks.Count == 0) blocked.Add("NO_SURVEY_TASKS: 未发现可用测线身份；轨迹保留待复核。");
            var duplicateTaskIds = tasks.Where(t => !string.IsNullOrWhiteSpace(t.SurveyTaskStableId))
                .GroupBy(t => t.SurveyTaskStableId, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            int missingTaskIds = tasks.Count(t => string.IsNullOrWhiteSpace(t.SurveyTaskStableId));
            if (missingTaskIds > 0 || duplicateTaskIds.Count > 0)
                blocked.Add($"AMBIGUOUS_IDENTITY: {missingTaskIds} 个任务缺少稳定身份，{duplicateTaskIds.Count} 个身份重复；仍可查看并手动处理未归属轨迹。");
            validTasks = tasks.Where(t => !string.IsNullOrWhiteSpace(t.SurveyTaskStableId) &&
                !duplicateTaskIds.Contains(t.SurveyTaskStableId)).ToArray();
            if (trackResult is null)
                windows = new MissionTaskSuggestionService().Suggest(activeSamples, validTasks, windows, request?.SpatialScreening, request?.TrackSegmentation?.MatchOptions);
            windows = NormalizeMissionTaskAuthority(windows, validTasks, blocked);
            if (windows.Any(w => string.IsNullOrWhiteSpace(w.MissionExecutionId)) ||
                windows.GroupBy(w => w.MissionExecutionId, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
                throw new InvalidDataException("Mission 必须有唯一 MissionExecutionId。");
            execution = trackResult?.Execution ?? new WholeLegMatcherService().Match(validTasks, points, windows, request?.TrackSegmentation?.MatchOptions);
            if (trackResult is null)
                execution = LocalHitCandidateService.Annotate(execution, validTasks, windows, request?.TrackSegmentation);
            inferredAxis = trackResult?.AxisDegrees;
            _recognition = new(recognitionKey, execution, validTasks, inferredAxis,
                blocked.Skip(warningsBefore).ToArray());
            RecognitionRuns++;
        }
        Stage("recognition", reuseIdentity);
        progress?.Invoke("应用人工归属并核对往返、连接与运动证据…");
        var rawExecution = execution;
        var assignments = decisionsMatch ? request?.ManualLegAssignments ?? [] : [];
        bool regroup = request?.AllowActualLineRegrouping == true;
        if (regroup) validTasks = ActualLineRegroupingService.ExtendTargets(validTasks, rawExecution, assignments);
        string assignmentKey = JsonSerializer.Serialize(new { Assignments = assignments, Regroup = regroup });
        bool reuseEvidence = reuseRecognition && ReferenceEquals(rawExecution, _evidenceInput) && assignmentKey == _assignmentKey;
        if (reuseEvidence)
        {
            execution = _evidence!; blocked.AddRange(_evidenceWarnings);
        }
        else
        {
            int warningsBefore = blocked.Count;
            if (regroup) execution = ActualLineRegroupingService.Reproject(execution, validTasks, assignments);
            execution = ApplyManualAssignments(execution, validTasks,
                assignments, blocked, regroup);
            execution = TransitEvidenceService.Annotate(execution, activeSamples, request?.TrackSegmentation, request?.SpatialScreening?.Range);
            execution = TrackRouteContextService.Annotate(execution, request?.TrackSegmentation);
            if (request?.TrackSegmentation is { } speedInput)
                execution = execution with { Passes = execution.Passes.Select(pass =>
                {
                    var leg = execution.Legs.First(l => l.FlightLegId == pass.FlightLegId);
                    var options = TrackSegmentationParameterProfiles.ForWindow(speedInput, pass.MissionExecutionId);
                    var observations = TrackMotionService.Measure(leg.Samples.Select(p => new SurveyTrackPoint(p.TimestampUtc,
                        new(p.Longitude, p.Latitude), p.SourceFile, p.SourceRecord) { AltitudeM = p.AltitudeM })).Where(o => o.SpeedMps.HasValue).ToArray();
                    if (pass.FlightQuality is null || observations.Length == 0) return pass;
                    int outside = observations.Count(o => o.SpeedMps < options.MinimumFlightSpeedMps || o.SpeedMps > options.MaximumFlightSpeedMps);
                    var warnings = pass.FlightQuality.Warnings.Where(w => w != "FLIGHT_SPEED_REVIEW").ToList();
                    if (outside > 0) warnings.Add("FLIGHT_SPEED_REVIEW");
                    return pass with { FlightQuality = pass.FlightQuality with { SpeedOutsideRatio = (double)outside / observations.Length, Warnings = warnings } };
                }).ToArray() };
            _evidenceInput = rawExecution; _assignmentKey = assignmentKey; _evidence = execution;
            _evidenceWarnings = blocked.Skip(warningsBefore).ToArray();
        }
        Stage("route-evidence", reuseEvidence);
        IReadOnlyList<ActualLineNumber> actualLineNumbers = [];
        if (regroup && request?.RenumberActualLines == true && assignments.Count > 0)
        {
            var numbered = MotherLineGroupingService.Renumber(validTasks, rawExecution, execution);
            validTasks = numbered.Tasks; actualLineNumbers = numbered.Numbers;
        }
        progress?.Invoke("核对已纳入范围、覆盖缺口和生产异常…");
        if (execution.Legs.Any(l => l.Classification == "UNRESOLVED"))
            blocked.Add("UNRESOLVED_LEGS: 存在未解析身份或未归属 Mission 的航段；未确认轨迹不会自动进入生产选择。");
        var confirmations = decisionsMatch ? request?.QualityConfirmations ?? [] : [];
        if (confirmations.GroupBy(x => x.PassId).Any(g => g.Count() > 1) ||
            confirmations.Any(c => string.IsNullOrWhiteSpace(c.ConfirmedBy) ||
                c.QualityStatus is not "PASS" and not "FAIL" || !execution.Passes.Any(p => p.PassId == c.PassId)))
            throw new InvalidDataException("QC 确认必须唯一、注明确认人，并指向本次执行结果中的 Pass。");
        var byId = confirmations.ToDictionary(c => c.PassId);
        var confirmedIds = decisionsMatch
            ? (request?.ConfirmedPassIds ?? []).Where(x => !string.IsNullOrWhiteSpace(x))
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string[] unknownConfirmedIds = confirmedIds.Where(id => !execution.Passes.Any(p =>
            p.PassId.Equals(id, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (unknownConfirmedIds.Length > 0)
            blocked.Add($"STALE_REVIEW_DECISIONS: {unknownConfirmedIds.Length} 条测线核验记录不属于本次执行结果，已忽略。");
        var passes = execution.Passes.Select(p =>
        {
            SurveyLinePass reviewed = p with
            {
                ReviewConfirmed = request?.RequirePassConfirmation != true || confirmedIds.Contains(p.PassId)
            };
            return byId.TryGetValue(p.PassId, out var c)
                ? reviewed with { QualityStatus = c.QualityStatus }
                : reviewed;
        }).ToArray();
        execution = execution with { Passes = passes };
        if (request?.RequirePassConfirmation == true && passes.Any(p => !p.ReviewConfirmed))
            blocked.Add($"PASS_REVIEW_REQUIRED: {passes.Count(p => !p.ReviewConfirmed)} 条测线归属尚未由用户确认；可以继续，但这些记录不会进入生产选择。");
        if (passes.Length == 0 || passes.Any(p => p.QualityStatus == "PENDING_QC"))
            blocked.Add("QC_REVIEW_REQUIRED: Pass 质量未全部确认或没有可用 Pass。");
        SurveyLinePass[] selectablePasses = passes.Where(p => p.ReviewConfirmed).ToArray();
        ProductionOverride[] validOverrides = (decisionsMatch ? request?.Overrides ?? [] : [])
            .Where(o => validTasks.Any(t => t.SurveyTaskStableId.Equals(o.SurveyTaskStableId,
                StringComparison.OrdinalIgnoreCase))).ToArray();
        int rejectedOverrides = (request?.Overrides ?? []).Count - validOverrides.Length;
        if (rejectedOverrides > 0)
            blocked.Add($"UNKNOWN_MANUAL_SELECTION: {rejectedOverrides} 条人工区间选择引用了当前不存在或身份不唯一的任务，已跳过。");
        var selections = new List<ProductionSelectionResult>();
        foreach (var task in validTasks)
        {
            string id = task.SurveyTaskStableId;
            var selection = SurveyProductionSelection.Select(id, selectablePasses, validOverrides, task.IsQualityRepeat);
            selections.Add(selection);
            var completion = SurveyCoverage.Calculate(id, task.LengthM,
                selectablePasses.Where(p => p.SurveyTaskStableId == id && p.QualityStatus == "PASS")
                    .SelectMany(p => p.Coverage));
            if (selection.Status != "READY" || completion.Status != "COMPLETE")
                blocked.Add($"PRODUCTION_SELECTION_REVIEW: {task.LineName} 存在未选择区间或覆盖缺口。");
        }
        var selected = new List<ExecutionSampleReference>();
        foreach (var selection in selections)
        foreach (var entry in selection.Entries)
        {
            var pass = selectablePasses.Single(p => p.PassId == entry.PassId);
            selected.AddRange(pass.Samples.Where(p => p.AlongFraction >= entry.Interval.Start &&
                (p.AlongFraction < entry.Interval.End || entry.Interval.End == 1d && p.AlongFraction <= 1d)));
        }
        var selectedKeys = selected.Select(p => (p.SourceFile, p.SourceRecord)).ToHashSet();
        string[] includedLegIds = decisionsMatch
            ? (request?.ManualIncludedFlightLegIds ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            : [];
        var legsById = execution.Legs.ToDictionary(x => x.FlightLegId, StringComparer.OrdinalIgnoreCase);
        int staleIncludedLegs = includedLegIds.Count(id => !legsById.ContainsKey(id));
        if (staleIncludedLegs > 0)
            blocked.Add($"STALE_INCLUDED_LEGS: {staleIncludedLegs} 条手动纳入记录不属于当前航段，已忽略。");
        int manuallyIncludedCount = 0;
        foreach (string legId in includedLegIds)
        {
            if (!legsById.TryGetValue(legId, out FlightLeg? leg)) continue;
            foreach (FlightLegSample sample in leg.Samples)
                if (selectedKeys.Add((sample.SourceFile, sample.SourceRecord)))
                {
                    selected.Add(new ExecutionSampleReference(sample.SourceFile, sample.SourceRecord, 0d));
                    manuallyIncludedCount++;
                }
        }
        if (manuallyIncludedCount > 0)
            blocked.Add($"USER_INCLUDED_UNCONFIRMED_LEG: 用户明确纳入 {manuallyIncludedCount} 条未进入确认区间的航段记录；成果将标记为待复核草稿。");
        if (request?.IncludeUnconfirmedSamples == true)
        {
            ExecutionSampleReference[] manualSamples = activeSamples
                .Where(p => p.Longitude.HasValue && p.Latitude.HasValue &&
                    !selectedKeys.Contains((p.SourceFile, p.SourceRecord)))
                .Select(p => new ExecutionSampleReference(p.SourceFile, p.SourceRecord, 0d)).ToArray();
            selected.AddRange(manualSamples);
            if (manualSamples.Length > 0)
                blocked.Add($"USER_INCLUDED_UNCONFIRMED: 用户选择继续处理 {manualSamples.Length} 条未进入已确认测线区间的记录；成果将标记为待复核草稿。");
        }
        int duplicateSelected = selected.Count - selected
            .Select(p => (p.SourceFile, p.SourceRecord)).Distinct().Count();
        if (duplicateSelected > 0)
            blocked.Add($"DUPLICATE_SELECTED_SAMPLES: {duplicateSelected} 个重叠选择已合并，成果将标记为待复核草稿。");
        selected = selected.GroupBy(p => (p.SourceFile, p.SourceRecord))
            .Select(g => g.First()).ToList();
        selectedKeys = selected.Select(p => (p.SourceFile, p.SourceRecord)).ToHashSet();
        // Anomaly exclusion is deliberately after all identity/manual selection
        // decisions: adding a leg confirms identity, not every abnormal point.
        var anomalyOptions = request?.ProductionAnomalyOptions ?? new();
        Stage("selection");
        string anomalyKey = JsonSerializer.Serialize(anomalyOptions);
        // QC/selection overlays do not alter geometry. Manual assignments use a new evidence snapshot.
        bool reuseAnomalies = reuseRecognition && ReferenceEquals(_evidence, _anomalyInput) && anomalyKey == _anomalyKey;
        var detected = reuseAnomalies ? _detected : ProductionAnomalyDetector.Detect(execution.Legs, anomalyOptions)
            .Concat(ProductionAnomalyDetector.ConfirmedDepartureTails(execution)).ToArray();
        _anomalyInput = _evidence; _anomalyKey = anomalyKey; _detected = detected;
        Stage("anomaly-detection", reuseAnomalies);
        var decisions = decisionsMatch ? request?.AnomalyDecisions ?? [] : [];
        if (decisions.GroupBy(x => x.AnomalyId).Any(g => g.Count() != 1) ||
            decisions.Any(x => x.Decision is not ("RESTORE" or "EXCLUDE") || string.IsNullOrWhiteSpace(x.Reason)))
            throw new InvalidDataException("异常区间决定必须唯一，使用恢复或排除，并注明依据。");
        var knownAnomalies = detected.Select(x => x.AnomalyId).ToHashSet();
        if (decisions.Any(x => !knownAnomalies.Contains(x.AnomalyId)))
            blocked.Add("ANOMALY_DECISION_STALE: 异常区间已变化，不能可靠对应的旧决定未沿用。");
        var decisionsById = decisions.ToDictionary(x => x.AnomalyId);
        var productionAnomalies = detected.Select(x => decisionsById.TryGetValue(x.AnomalyId, out var d)
            ? x with { ProductionDecision = d.Decision == "RESTORE" ? "USER_RESTORE" : "USER_EXCLUDE", Reason = x.Reason + "人工决定：" + d.Reason }
            : x).ToArray();
        var explicitExclusions = productionAnomalies.Where(x => x.ProductionDecision == "USER_EXCLUDE")
            .SelectMany(x => x.Samples).Select(x => (x.SourceFile, x.SourceRecord)).ToHashSet();
        if (productionAnomalies.Where(x => x.ProductionDecision == "USER_RESTORE").SelectMany(x => x.Samples)
            .Any(x => explicitExclusions.Contains((x.SourceFile, x.SourceRecord))))
            throw new InvalidDataException("手动恢复与手动排除的异常区间存在重叠，请调整生产决定后重新分析。");
        var anomalyKeys = productionAnomalies.Where(x => x.ProductionDecision is "AUTO_EXCLUDE" or "USER_EXCLUDE")
            .SelectMany(x => x.Samples)
            .Select(x => (x.SourceFile, x.SourceRecord)).ToHashSet();
        anomalyKeys.ExceptWith(productionAnomalies.Where(x => x.ProductionDecision == "USER_RESTORE")
            .SelectMany(x => x.Samples).Select(x => (x.SourceFile, x.SourceRecord)));
        var countedExclusions = new HashSet<(string SourceFile, int SourceRecord)>();
        productionAnomalies = productionAnomalies.Select(x => x with
        {
            ExcludedSelectedPoints = x.ProductionDecision is "AUTO_EXCLUDE" or "USER_EXCLUDE"
                ? x.Samples.Count(p => selectedKeys.Contains((p.SourceFile, p.SourceRecord)) &&
                    anomalyKeys.Contains((p.SourceFile, p.SourceRecord)) && countedExclusions.Add((p.SourceFile, p.SourceRecord))) : 0
        }).ToArray();
        int excludedAnomalies = selected.RemoveAll(x => anomalyKeys.Contains((x.SourceFile, x.SourceRecord)));
        selectedKeys = selected.Select(x => (x.SourceFile, x.SourceRecord)).ToHashSet();
        if (excludedAnomalies > 0)
            blocked.Add($"PRODUCTION_ANOMALY_EXCLUDED: 已从生产选择扣除 {excludedAnomalies} 个异常点；测线身份与原始记录保留。");
        if (productionAnomalies.Any(x => x.ProductionDecision == "KEEP_PENDING_REVIEW" &&
            x.Samples.Any(s => selectedKeys.Contains((s.SourceFile, s.SourceRecord)))))
            blocked.Add("PRODUCTION_ANOMALY_REVIEW: 已选数据含证据不足的停留区间，保留并待复核。");
        bool draft = blocked.Count > 0;
        // Recompute the selected footprint from actual chosen observations,
        // not from all passes that happened to be recognised.
        var selectedCoverage = passes.Select(pass =>
        {
            var leg = legsById[pass.FlightLegId];
            var chosen = leg.Samples.Where(p => selectedKeys.Contains((p.SourceFile, p.SourceRecord))).ToArray();
            var task = validTasks.First(t => t.SurveyTaskStableId == pass.SurveyTaskStableId);
            return pass with { Coverage = ObservedTrackCoverageService.Calculate(chosen, task, request?.TrackSegmentation?.MatchOptions ?? new()) };
        }).Where(p => p.Coverage.Count > 0).ToArray();
        Stage("anomaly-decisions-and-coverage");
        progress?.Invoke("核对成果快照和来源记录…");
        var result = new AuditProductionResult(draft ? "REVIEW_DRAFT" : "READY", draft, execution, selections, selected, blocked)
        { SpatialScreening = screening, SpatialScreeningInput = request?.SpatialScreening,
            EffectiveTasks = validTasks, ActualLineNumbers = actualLineNumbers, TrackSegmentation = request?.TrackSegmentation, InferredAxisDegrees = inferredAxis,
            RecognizedCoverage = TaskTrackCoverageService.Summarize(validTasks, passes),
            SelectedCoverage = TaskTrackCoverageService.Summarize(validTasks, selectedCoverage, "SELECTED"),
            ProductionAnomalies = productionAnomalies,
            ProductionAnomalyOptions = anomalyOptions,
            ProductionSnapshotFingerprint = ProductionSnapshotService.Fingerprint(analysis.Fingerprint, routeFingerprint, request, selected),
            ProductionRequestFingerprint = ProductionSnapshotService.RequestFingerprint(request),
            ManualIncludedFlightLegIds = includedLegIds.Where(legsById.ContainsKey).ToArray() };
        Stage("snapshot");
        return result;
    }

    private static SurveyExecutionResult ApplyManualAssignments(SurveyExecutionResult execution,
        IReadOnlyList<PlannedSurveyLineTask> tasks,
        IReadOnlyList<ManualFlightLegAssignment> assignments,
        List<string> warnings, bool allowActualRegrouping = false)
    {
        if (assignments.Count == 0) return execution;
        if (assignments.GroupBy(x => x.FlightLegId, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
        {
            warnings.Add("DUPLICATE_MANUAL_LEG_ASSIGNMENT: 同一航段存在多个手动归属决定，相关决定已跳过。");
            assignments = assignments.GroupBy(x => x.FlightLegId, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() == 1).Select(g => g.Single()).ToArray();
        }
        var taskById = tasks.ToDictionary(x => x.SurveyTaskStableId, StringComparer.OrdinalIgnoreCase);
        var missions = execution.Missions.ToDictionary(x => x.MissionExecutionId, StringComparer.OrdinalIgnoreCase);
        var legById = execution.Legs.ToDictionary(x => x.FlightLegId, StringComparer.OrdinalIgnoreCase);
        var passByLeg = execution.Passes.ToDictionary(x => x.FlightLegId, StringComparer.OrdinalIgnoreCase);
        var legs = execution.Legs.ToDictionary(x => x.FlightLegId, StringComparer.OrdinalIgnoreCase);
        var passes = execution.Passes.ToDictionary(x => x.PassId, StringComparer.OrdinalIgnoreCase);

        foreach (ManualFlightLegAssignment decision in assignments)
        {
            if (!legById.TryGetValue(decision.FlightLegId, out FlightLeg? leg) ||
                !taskById.TryGetValue(decision.SurveyTaskStableId, out PlannedSurveyLineTask? task) ||
                !missions.TryGetValue(leg.MissionExecutionId, out MissionExecution? mission) ||
                (!mission.SurveyTaskStableIds.Contains(task.SurveyTaskStableId, StringComparer.OrdinalIgnoreCase) &&
                    !(allowActualRegrouping && task.GeometryOrigin == "ACTUAL_TRACK")))
            {
                warnings.Add("MANUAL_LEG_ASSIGNMENT_UNAVAILABLE: 有手动归属决定不适用于当前航段、Mission 或测线任务，已跳过。");
                continue;
            }

            SurveyLinePass? pass = WholeLegMatcherService.CreateManualAssignmentPass(leg, task);
            if (pass is null)
            {
                warnings.Add("MANUAL_LEG_ASSIGNMENT_INSUFFICIENT_TRACK: 所选航段缺少可计算沿线位置的有效轨迹点，已保留为待确认。");
                continue;
            }
            if (passByLeg.TryGetValue(leg.FlightLegId, out SurveyLinePass? previous))
                passes.Remove(previous.PassId);
            passes[pass.PassId] = pass;
            passByLeg[leg.FlightLegId] = pass;
            legs[leg.FlightLegId] = leg with
            {
                Classification = "SURVEY",
                SurveyTaskStableId = task.SurveyTaskStableId,
                ReviewReason = string.IsNullOrWhiteSpace(decision.Reason) ? "USER_ASSIGNED" : "USER_ASSIGNED:" + decision.Reason
            };
        }

        return execution with
        {
            Legs = execution.Legs.Select(x => legs[x.FlightLegId]).ToArray(),
            Passes = passes.Values.OrderBy(x => x.EndUtc).ToArray()
        };
    }

    private static IReadOnlyList<MissionTimeWindow> NormalizeMissionTaskAuthority(
        IReadOnlyList<MissionTimeWindow> windows,
        IReadOnlyList<PlannedSurveyLineTask> tasks,
        List<string> blocked)
    {
        // Do not let malformed legacy tasks reach ToDictionary: multiple empty IDs
        // otherwise surface as an opaque ArgumentException ("same key: ").
        var known = tasks.ToDictionary(t => t.SurveyTaskStableId, StringComparer.OrdinalIgnoreCase);
        var normalized = new List<MissionTimeWindow>(windows.Count);
        foreach (MissionTimeWindow window in windows)
        {
            string[] explicitIds = (window.SurveyTaskStableIds ?? [])
                .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (explicitIds.Length > 0)
            {
                string[] unknown = explicitIds.Where(x => !known.ContainsKey(x)).ToArray();
                if (unknown.Length > 0)
                    blocked.Add($"MISSION_TASK_IDENTITY_UNAVAILABLE: Mission {window.MissionExecutionId} 有 {unknown.Length} 个任务身份不存在或不唯一；相关轨迹保留为未归属项。");
                normalized.Add(window with { SurveyTaskStableIds = explicitIds.Where(known.ContainsKey).ToArray() });
                continue;
            }

            // Legacy JSON compatibility: a display sortie name may be migrated only
            // when it deterministically resolves to the current authoritative task set.
            if (!string.IsNullOrWhiteSpace(window.PlannedSortieName))
            {
                string[] migrated = tasks.Where(t => t.SortieName.Equals(window.PlannedSortieName,
                        StringComparison.OrdinalIgnoreCase))
                    .Select(t => t.SurveyTaskStableId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (migrated.Length > 0)
                {
                    normalized.Add(window with { SurveyTaskStableIds = migrated });
                    blocked.Add($"LEGACY_MISSION_TASK_MIGRATION_REVIEW: Mission {window.MissionExecutionId} 的旧架次名 {window.PlannedSortieName} 已确定性映射到 {migrated.Length} 个 Stable Task；需复核后重新保存新格式。");
                    continue;
                }
            }

            normalized.Add(window with { SurveyTaskStableIds = [] });
            blocked.Add($"MISSION_TASK_AUTHORITY_REQUIRED: Mission {window.MissionExecutionId} 缺少 SurveyTaskStableIds；相关轨迹保留为未归属项，可在核验时手动处理。");
        }
        return normalized;
    }

}
