using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MagOps.Core.Domain;

namespace MagOps.Infrastructure.Services;

public sealed record FlightMetricDistribution(string Population, string Metric, int Count,
    double? P05, double? P50, double? P90, double? P95, double? Maximum);
public sealed record FlightThresholdObservation(string Population, string Metric, double Threshold,
    int EligibleCount, int WithinCount, double? WithinRatio);
public sealed record FlightQualityAssessmentSaved(string Directory, int ActiveSamples, int Segments);

/// <summary>Descriptive flight evidence only. No recognition, QC or parameter write authority.</summary>
public sealed class FlightQualityAssessmentService
{
    public const string SchemaVersion = "magops-flight-quality-assessment-6";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private sealed record Geometry(PlannedSurveyLineTask Task, double Length, double Ux, double Uy, double Bearing);
    private sealed record Reference(Geometry Geometry, string Kind, double AxisError, double MedianSegmentDistance);
    private sealed record Motion(double? Speed, double? InstantSpeed, double? Climb, double? Bearing, double? Gap, double? WindowSeconds);

    // Per-dialog serial report queue owns this instance. Geometry statistics are
    // descriptive and independent of confirmation/inclusion/anomaly decisions.
    private FieldMagFolderAnalysis? _basisAnalysis;
    private UavMagCanonicalSample[] _basisSamples = [];
    private PlannedSurveyLineTask[] _basisTasks = [];
    private IReadOnlyList<FlightLeg>? _basisLegs;
    private string? _basisOptions;
    private Dictionary<(string, int), Motion>? _basisMotion;
    private readonly Dictionary<string, Reference[]> _basisAlternatives = [];
    public int EvidenceBuilds { get; private set; }
    public int EvidenceReuses { get; private set; }

    public FlightQualityAssessmentSaved Save(string outputRoot, FieldMagFolderAnalysis analysis,
        IReadOnlyList<UavMagCanonicalSample> active, IReadOnlyList<PlannedSurveyLineTask> tasks,
        SurveyExecutionResult execution, AuditProductionRequest request, WholeLegMatchOptions? options = null,
        string inputScope = "CURRENT_SCREENING", FlightQualityAssessmentOptions? qualityOptions = null)
    {
        options ??= request.TrackSegmentation?.MatchOptions;
        options ??= new();
        var recognitionOptions = request.TrackSegmentation?.RecognitionOptions ?? new();
        qualityOptions ??= request.FlightQualityOptions ?? new();
        qualityOptions.Validate();
        var samples = active.ToArray();
        var keys = samples.Select(s => (s.SourceFile, s.SourceRecord)).ToArray();
        if (keys.Distinct().Count() != keys.Length)
            throw new InvalidDataException("飞行评估存在重复来源记录，不能建立可靠样点依据。");
        var lookup = samples.ToDictionary(s => (s.SourceFile, s.SourceRecord));
        string basisOptions = JsonSerializer.Serialize(options);
        bool reuseBasis = ReferenceEquals(analysis, _basisAnalysis) && ReferenceEquals(execution.Legs, _basisLegs) &&
            basisOptions == _basisOptions && samples.Length == _basisSamples.Length && tasks.Count == _basisTasks.Length &&
            samples.Select((sample, i) => ReferenceEquals(sample, _basisSamples[i])).All(x => x) &&
            tasks.Select((task, i) => task == _basisTasks[i]).All(x => x);
        var motion = reuseBasis ? _basisMotion! : MeasureMotion(samples, options);
        if (!reuseBasis) { _basisAnalysis = null; _basisAlternatives.Clear(); }
        if (reuseBasis) EvidenceReuses++; else EvidenceBuilds++;
        var geometries = tasks.Where(t => WorkareaSpatialService.ValidCoordinate(t.Start.Longitude, t.Start.Latitude) &&
                WorkareaSpatialService.ValidCoordinate(t.End.Longitude, t.End.Latitude))
            .Select(t =>
            {
                var (dx, dy) = EndpointLineRecognizer.Delta(t.Start, t.End);
                double length = Math.Sqrt(dx * dx + dy * dy);
                return new Geometry(t, length, dx / Math.Max(.5, length), dy / Math.Max(.5, length),
                    SurveyDirection.Bearing(t.Start, t.End) ?? 0);
            }).Where(g => g.Length >= .5).ToArray();
        var passByLeg = execution.Passes.ToDictionary(p => p.FlightLegId);
        var legByKey = new Dictionary<(string, int), FlightLeg>();
        var referenceByLeg = new Dictionary<string, Reference?>();
        var measurements = new Dictionary<(string Population, string Metric), List<double>>();
        var segmentRows = new List<object?[]>();
        var candidateRows = new List<object?[]>();
        void Add(string population, string metric, double? value)
        {
            if (value is not { } number || !double.IsFinite(number)) return;
            if (!measurements.TryGetValue((population, metric), out var list)) measurements[(population, metric)] = list = [];
            list.Add(number);
        }
        foreach (var leg in execution.Legs)
        {
            Add("SEGMENT_" + leg.Classification, "segment_duration_seconds", (leg.EndUtc - leg.StartUtc).TotalSeconds);
            if (leg.Samples.Count >= 2)
                Add("SEGMENT_" + leg.Classification, "segment_span_m", TrackSegmentationReferenceService.Distance(
                    new(leg.Samples[0].Longitude, leg.Samples[0].Latitude), new(leg.Samples[^1].Longitude, leg.Samples[^1].Latitude)));
            foreach (var s in leg.Samples)
                if (!lookup.ContainsKey((s.SourceFile, s.SourceRecord)) || !legByKey.TryAdd((s.SourceFile, s.SourceRecord), leg))
                    throw new InvalidDataException("评估航段引用缺失或重复的有效样点。");
            passByLeg.TryGetValue(leg.FlightLegId, out var pass);
            double? flightBearing = leg.Samples.Count < 2 ? null : SurveyDirection.Bearing(
                new(leg.Samples[0].Longitude, leg.Samples[0].Latitude), new(leg.Samples[^1].Longitude, leg.Samples[^1].Latitude));
            // Calculate all nearby-looking alternatives, including candidates never seeded by the matcher.
            var alternatives = reuseBasis ? _basisAlternatives[leg.FlightLegId] : geometries.Select(g =>
            {
                double[] distances = leg.Samples.Select(s => SegmentDistance(g, new(s.Longitude, s.Latitude))).Order().ToArray();
                double axis = flightBearing is { } b ? Math.Min(SurveyDirection.Difference(b, g.Bearing),
                    SurveyDirection.Difference(b, (g.Bearing + 180) % 360)) : 90;
                return new Reference(g, "DIAGNOSTIC_ONLY", axis, Quantile(distances, .5) ?? double.MaxValue);
            }).OrderBy(r => r.MedianSegmentDistance).ThenBy(r => r.AxisError).ToArray();
            _basisAlternatives[leg.FlightLegId] = alternatives;
            Reference? reference = pass is not null
                ? alternatives.FirstOrDefault(r => r.Geometry.Task.SurveyTaskStableId == pass.SurveyTaskStableId)
                : alternatives.FirstOrDefault(r => r.AxisError <= options.HeadingToleranceDegrees);
            if (pass is not null && reference is not null)
                reference = reference with { Kind = reference.Geometry.Task.GeometryOrigin == "ACTUAL_TRACK" ? "ACTUAL_FIT_NOT_DESIGN_QC" : "ASSIGNED_CANDIDATE" };
            referenceByLeg[leg.FlightLegId] = reference;
            foreach (var alternative in alternatives.Take(3))
                candidateRows.Add([leg.FlightLegId, alternative.Geometry.Task.SurveyTaskStableId, alternative.Geometry.Task.LineName,
                    alternative.MedianSegmentDistance, alternative.AxisError, "NEARBY_HINT_ONLY"]);
            double pathM = 0;
            for (int i = 1; i < leg.Samples.Count; i++)
            {
                var a = leg.Samples[i - 1]; var b = leg.Samples[i];
                double gap = (b.TimestampUtc - a.TimestampUtc).TotalSeconds;
                if (gap <= 0 || gap > options.MaximumTimeGapSeconds) continue;
                var (x, y) = EndpointLineRecognizer.Delta(new(a.Longitude, a.Latitude), new(b.Longitude, b.Latitude));
                pathM += Math.Sqrt(x * x + y * y);
            }
            double? startDistance = reference is null || leg.Samples.Count == 0 ? null : EndpointDistance(reference.Geometry,
                leg.Samples[0], flightBearing, false);
            double? endDistance = reference is null || leg.Samples.Count == 0 ? null : EndpointDistance(reference.Geometry,
                leg.Samples[^1], flightBearing, true);
            segmentRows.Add([leg.FlightLegId, leg.MissionExecutionId, leg.Classification, leg.ReviewReason, leg.StartUtc, leg.EndUtc,
                leg.PointCount, pathM, flightBearing, reference?.Geometry.Task.LineName, reference?.Geometry.Task.SurveyTaskStableId,
                reference?.Kind, reference?.AxisError, reference?.MedianSegmentDistance, startDistance, endDistance,
                pass?.MatchMethod, pass?.ExecutionDirection, pass?.ReviewConfirmed, pass?.QualityStatus,
                pass?.StartBoundary?.CutUtc, pass?.StartBoundary?.ConfirmedUtc, pass?.EndBoundary?.CutUtc, pass?.EndBoundary?.ConfirmedUtc,
                string.Join("|", leg.CandidateTaskStableIds)]);

        }

        _basisAnalysis = analysis; _basisSamples = samples; _basisTasks = tasks.ToArray();
        _basisLegs = execution.Legs; _basisOptions = basisOptions; _basisMotion = motion;

        string batch = DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture) + "_" + Guid.NewGuid().ToString("N")[..8];
        string directory = Path.Combine(Path.GetFullPath(outputRoot), batch);
        Directory.CreateDirectory(directory);
        using (var stream = File.Create(Path.Combine(directory, "point-evidence.csv.gz")))
        using (var gzip = new GZipStream(stream, CompressionLevel.Fastest))
        using (var writer = new StreamWriter(gzip, new UTF8Encoding(true)))
        {
            WriteRow(writer, ["SourceFile", "SourceRecord", "TimeUtc", "Longitude", "Latitude", "AltitudeM", "LaserAltitudeM", "GpsStatus", "Satellites",
                "InputFlags", "LegId", "Population", "ReferenceTaskId", "ReferenceKind", "AlongM", "SignedCrossM",
                "SegmentDistanceM", "Speed1sMps", "InstantSpeedMps", "VerticalSpeed1sMps", "FlightBearingDeg",
                "DirectedOrAxisErrorDeg", "PreviousGapSec", "MotionWindowSeconds", "Observations"]);
            foreach (var sample in samples.OrderBy(s => s.SourceFile, StringComparer.Ordinal).ThenBy(s => s.TimestampUtc).ThenBy(s => s.SourceRecord))
            {
                var key = (sample.SourceFile, sample.SourceRecord);
                legByKey.TryGetValue(key, out var leg);
                var reference = leg is null ? null : referenceByLeg[leg.FlightLegId];
                string population = leg?.ReviewReason == "AMBIGUOUS_LINE" ? "COMPETING" : leg?.Classification ?? "NO_GEOMETRY_LEG";
                if (reference?.Kind == "ACTUAL_FIT_NOT_DESIGN_QC") population = "ACTUAL_FIT";
                var m = motion[key];
                double? along = null, cross = null, distance = null, direction = null;
                if (reference is not null && Valid(sample))
                {
                    var g = reference.Geometry;
                    var (x, y) = EndpointLineRecognizer.Delta(g.Task.Start, new(sample.Longitude!.Value, sample.Latitude!.Value));
                    along = x * g.Ux + y * g.Uy; cross = x * g.Uy - y * g.Ux;
                    distance = SegmentDistance(g, new(sample.Longitude.Value, sample.Latitude.Value));
                    if (m.Bearing is { } bearing)
                    {
                        var pass = passByLeg.GetValueOrDefault(leg!.FlightLegId);
                        direction = pass?.ExecutionDirection is "FORWARD" or "REVERSE"
                            ? SurveyDirection.Difference(bearing, (g.Bearing + (pass.ExecutionDirection == "REVERSE" ? 180 : 0)) % 360)
                            : Math.Min(SurveyDirection.Difference(bearing, g.Bearing), SurveyDirection.Difference(bearing, (g.Bearing + 180) % 360));
                    }
                }
                var flags = new List<string>();
                if (!Valid(sample)) flags.Add("INVALID_COORDINATE");
                if (m.Speed is null) flags.Add("MOTION_EVIDENCE_MISSING");
                if (m.Gap == 0) flags.Add("DUPLICATE_TIMESTAMP");
                var speedOptions = request.TrackSegmentation is { } speedInput && leg is not null
                    ? TrackSegmentationParameterProfiles.ForWindow(speedInput, leg.MissionExecutionId) : options;
                if (m.Speed is { } speed && (speed < speedOptions.MinimumFlightSpeedMps - 1e-6 || speed > speedOptions.MaximumFlightSpeedMps + 1e-6)) flags.Add("SPEED_AUXILIARY");
                if (cross is { } offset && Math.Abs(offset) > options.CorridorM) flags.Add("CROSS_TRACK");
                if (m.Climb is { } climb && Math.Abs(climb) > options.VerticalSpeedReviewMps) flags.Add("VERTICAL_AUXILIARY");
                if (direction > options.DirectionBreakDegrees) flags.Add("DIRECTION_REVIEW_NOT_CONFIRMED_BREAK");
                foreach (string group in new[] { "ALL_ACTIVE", population })
                {
                    if (request.TrackSegmentation is { Enabled: true, AxisDegrees: { } requestedAxis } && m.Bearing is { } flightHeading)
                        Add(group, "explicit_axis_error_degrees", TrackSegmentationReferenceService.AxisDifference(flightHeading, requestedAxis));
                    Add(group, "speed_1s_mps", m.Speed); Add(group, "instant_speed_mps", m.InstantSpeed);
                    Add(group, "abs_vertical_speed_1s_mps", m.Climb is { } c ? Math.Abs(c) : null);
                    Add(group, "sample_gap_seconds", m.Gap);
                    Add(group, "horizontal_offset_m", cross is { } v ? Math.Abs(v) : null);
                    Add(group, "finite_segment_distance_m", distance); Add(group, "direction_error_degrees", direction);
                }
                WriteRow(writer, [sample.SourceFile, sample.SourceRecord, sample.TimestampUtc, sample.Longitude, sample.Latitude,
                    sample.AltitudeM, sample.LaserAltitudeM, sample.GpsStatus, sample.Satellites, string.Join("|", sample.QualityFlags),
                    leg?.FlightLegId, population, reference?.Geometry.Task.SurveyTaskStableId, reference?.Kind,
                    along, cross, distance, m.Speed, m.InstantSpeed, m.Climb, m.Bearing, direction, m.Gap, m.WindowSeconds, string.Join("|", flags)]);
            }
        }
        var distributions = new List<FlightMetricDistribution>();
        var thresholds = new List<FlightThresholdObservation>();
        foreach (var (key, values) in measurements.OrderBy(p => p.Key.Population).ThenBy(p => p.Key.Metric))
        {
            double[] ordered = values.Order().ToArray();
            distributions.Add(new(key.Population, key.Metric, ordered.Length, Quantile(ordered, .05), Quantile(ordered, .5),
                Quantile(ordered, .9), Quantile(ordered, .95), ordered.Length == 0 ? null : ordered[^1]));
            double[] limits = key.Metric switch
            {
                "horizontal_offset_m" or "finite_segment_distance_m" => [1, 3, 5, 8, 12, 20, 30, 40, 50],
                "speed_1s_mps" or "instant_speed_mps" => [2, 3, 4, 6, 8, 10, 12, 14, 16],
                "direction_error_degrees" => [5, 10, 15, 20, 30, 45, 60, 75, 90, 120, 180],
                "explicit_axis_error_degrees" => [3, 5, 10, 12, 15, 20, 30, 45, 60, 75, 90],
                "segment_duration_seconds" => [1, 2, 5, 10, 20, 30, 60, 120, 300],
                "segment_span_m" => [15, 30, 40, 60, 100, 200, 500, 1000],
                "abs_vertical_speed_1s_mps" => [.5, 1, 2, 3, 4, 6],
                _ => [.2, .5, 1, 2, 5, 8, 10]
            };
            double[] configured = key.Metric switch
            {
                "explicit_axis_error_degrees" => [recognitionOptions.AxisToleranceDegrees],
                "direction_error_degrees" => [options.HeadingToleranceDegrees, options.BodyHeadingToleranceDegrees, options.DirectionBreakDegrees,
                    recognitionOptions.StraightHeadingToleranceDegrees, recognitionOptions.RepeatAxisToleranceDegrees],
                "horizontal_offset_m" => [options.CoreM, options.CorridorM, recognitionOptions.RepeatCrossTrackToleranceM],
                "finite_segment_distance_m" => [options.EndpointSearchM, options.CandidateSearchM],
                "speed_1s_mps" or "instant_speed_mps" => [options.MinimumFlightSpeedMps, options.MaximumFlightSpeedMps],
                "abs_vertical_speed_1s_mps" => [options.VerticalSpeedReviewMps],
                "segment_duration_seconds" => [recognitionOptions.MinimumDurationSeconds],
                "segment_span_m" => [recognitionOptions.MinimumSpanM],
                _ => []
            };
            limits = limits.Concat(configured).Distinct().Order().ToArray();
            foreach (double limit in limits)
            {
                int count = values.Count(v => v <= limit + 1e-6);
                thresholds.Add(new(key.Population, key.Metric, limit, values.Count, count, values.Count == 0 ? null : (double)count / values.Count));
            }
        }
        var taskRows = tasks.Select(task =>
        {
            var passes = execution.Passes.Where(p => p.SurveyTaskStableId == task.SurveyTaskStableId).ToArray();
            var coverage = SurveyCoverage.Union(passes.SelectMany(p => p.Coverage));
            return new object?[] { task.LineName, task.SurveyTaskStableId, task.LengthM, passes.Length,
                coverage.Sum(i => i.End - i.Start), passes.Count(p => p.MatchMethod == "ENDPOINT_PAIR"),
                passes.Count(p => p.MatchMethod == "SINGLE_ENDPOINT"), passes.Count(p => p.MatchMethod == "BODY_TRACK"),
                execution.Missions.Any(m => m.SurveyTaskStableIds.Contains(task.SurveyTaskStableId)),
                passes.Length == 0 ? "NO_MATCH_CANDIDATE" : "IDENTITY_AND_QUALITY_REQUIRE_REVIEW" };
        }).ToArray();
        var flightIndicators = new FlightQualityIndicatorsService().Build(analysis.UavSamples, samples, tasks, execution, qualityOptions);
        WriteCsv(Path.Combine(directory, "line-flight-quality.csv"), FlightQualityIndicatorsService.LineHeader,
            flightIndicators.Lines.Select(l => l.ToRow()));
        WriteCsv(Path.Combine(directory, "task-flight-quality.csv"), FlightQualityIndicatorsService.LineHeader,
            flightIndicators.TaskLines.Select(l => l.ToRow()));
        File.WriteAllText(Path.Combine(directory, "flight-quality-report.md"),
            DescribeFlightQuality(flightIndicators, qualityOptions) + DescribeRecognitionThresholds(request, options, recognitionOptions, distributions, thresholds), Encoding.UTF8);
        WriteCsv(Path.Combine(directory, "static-gps-quality.csv"), FlightQualityIndicatorsService.StaticHeader,
            flightIndicators.StaticTests.Select(t => t.ToRow()));
        WriteCsv(Path.Combine(directory, "static-gps-evidence.csv"), ["SourceFile", "SourceRecord", "TimeUtc", "Longitude", "Latitude", "AltitudeM"],
            flightIndicators.StaticSamples.Select(t => new object?[] { t.SourceFile, t.SourceRecord, t.TimestampUtc, t.Longitude, t.Latitude, t.AltitudeM }));
        WriteCsv(Path.Combine(directory, "task-quality.csv"), ["LineName", "TaskId", "LengthM", "PassCount", "CandidateCoverage",
            "EndpointPairs", "SingleEndpoints", "BodyTracks", "AllowedInAnyWindow", "Status"], taskRows);
        WriteCsv(Path.Combine(directory, "segment-quality.csv"), ["LegId", "WindowId", "Classification", "Reason", "StartUtc", "EndUtc",
            "Points", "PathM", "Bearing", "ReferenceName", "ReferenceId", "ReferenceKind", "AxisError", "MedianSegmentDistanceM",
            "StartEndpointEuclideanM", "EndEndpointEuclideanM", "MatchMethod", "ExecutionDirection", "IdentityReviewed", "QualityStatus",
            "StartCutUtc", "StartConfirmedUtc", "EndCutUtc", "EndConfirmedUtc", "CompetingTasks"], segmentRows);
        WriteCsv(Path.Combine(directory, "nearby-task-hints.csv"), ["LegId", "TaskId", "LineName", "MedianSegmentDistanceM", "AxisError", "Kind"], candidateRows);
        WriteCsv(Path.Combine(directory, "threshold-distributions.csv"), ["Population", "Metric", "Threshold", "EligibleCount", "WithinCount", "WithinRatio"],
            thresholds.Select(t => new object?[] { t.Population, t.Metric, t.Threshold, t.EligibleCount, t.WithinCount, t.WithinRatio }));
        WriteCsv(Path.Combine(directory, "manual-review-template.csv"), ["LegId", "SuggestedClassification", "SuggestedReferenceTaskId",
            "HumanClassification", "HumanTaskId", "HumanStartRecord", "HumanEndRecord", "Reason"],
            execution.Legs.Select(l => new object?[] { l.FlightLegId, l.Classification, l.SurveyTaskStableId, "", "", "", "", "" }));
        File.WriteAllText(Path.Combine(directory, "summary.json"), JsonSerializer.Serialize(new
        {
            SchemaVersion, CreatedUtc = DateTimeOffset.UtcNow, execution.AlgorithmVersion, InputScope = inputScope,
            RawSampleCount = analysis.UavSamples.Count, ActiveSampleCount = active.Count,
            ValidCoordinateCount = samples.Count(Valid), RepresentedSamples = legByKey.Count,
            NotRepresentedSamples = samples.Length - legByKey.Count,
            Segments = execution.Legs.Count, CandidatePasses = execution.Passes.Count,
            CandidateTasks = execution.Passes.Select(p => p.SurveyTaskStableId).Distinct().Count(),
            Classifications = execution.Legs.GroupBy(l => l.Classification).ToDictionary(g => g.Key, g => new { Segments = g.Count(), Points = g.Sum(l => l.PointCount) }),
            Distributions = distributions,
            FlightQualityIndicators = flightIndicators.Summary,
            Interpretation = "候选身份尚非人工真值。ALL_ACTIVE 包含转场/未归属；请按 Population 比较。无身份段的参考线仅是几何提示。"
                + (request.TrackSegmentation?.Enabled == true ? " 实测推断线的偏航和覆盖仅相对拟合线，不是设计偏航和设计覆盖验收。" : "")
        }, Json));
        File.WriteAllText(Path.Combine(directory, "input-and-parameters.json"), JsonSerializer.Serialize(new
        {
            SchemaVersion, analysis.Fingerprint, analysis.SourceDirectory, request.RouteFingerprint, request.ScreeningFingerprint,
            InputScope = inputScope, Options = options, Request = request, Tasks = tasks,
            TrackRecognitionOptions = recognitionOptions,
            ParameterProfile = request.TrackSegmentation?.ParameterProfile,
            ParameterProfileVersion = request.TrackSegmentation?.ParameterProfileVersion,
            PlannedSpeedMps = request.TrackSegmentation?.PlannedSpeedMps,
            WindowPlannedSpeeds = request.TrackSegmentation?.WindowPlannedSpeeds,
            KnownTakeoffPositions = request.TrackSegmentation?.KnownTakeoffPositions,
            TransitEvidenceVersion = TransitEvidenceService.Version,
            RouteContextVersion = TrackRouteContextService.Version,
            RoutePurposes = execution.Legs.Select(l => new { l.FlightLegId, l.ParentRouteId, l.RouteRole, l.RouteRoleEvidence }),
            TransitEvidence = execution.Legs.Where(l => l.TransitEvidence.Count > 0).Select(l => new { l.FlightLegId, l.TransitEvidence }),
            MotionEvidence = execution.Legs.Where(l => l.MotionKind.Length > 0).Select(l => new { l.FlightLegId, l.MotionKind, l.MotionEvidence }),
            ContextEvidence = execution.Legs.Where(l => l.ContextEvidence.Count > 0).Select(l => new { l.FlightLegId, l.ContextEvidence }),
            LocalHitCandidates = execution.Legs.Where(l => l.LocalHitCandidates.Count > 0)
                .Select(l => new { l.FlightLegId, l.LocalHitCandidates }),
            FixedTrackRules = new { ShallowBodyRefinement = TrackShallowTurnRefiner.RuleSnapshot,
                MotionAndTransit = TransitEvidenceService.RuleSnapshot,
                LocalHitCandidateRules = LocalHitCandidateService.RuleSnapshot,
                WholeTrackGeometryRecovery = WholeTrackGeometryRecoveryService.RuleSnapshot,
                ReferenceGeometry = ReferenceMissionGeometryService.RuleSnapshot,
                CoverageVersion = ObservedTrackCoverageService.Version,
                ParallelAxisToleranceDegrees = 5, ParallelSeparationMinM = 12, ParallelSeparationMaxM = 500,
                MinimumParallelNeighbours = 2, MinimumOverlapRatio = .2, HoverSpeedMps = 1, MotionConfirmationSeconds = 2,
                ReferenceDuplicateAxisDegrees = 5, ReferenceDuplicateCrossM = 8, ReferenceDuplicateOverlap = .5,
                ConnectorAxisDegrees = 12, ConnectorCrossM = 8,
                ReferenceImportRules = "KML转角8度、主体60m、平行轴5度、横距12–500m；连接通过点序邻接；空间版本规则独立记录。" },
            QualityAssessmentOptions = qualityOptions,
            EvidenceOptions = new { TargetMotionWindowSeconds = 1, CoarseUtcMotionMethod = TrackMotionService.Version, MinimumBearingTravelM = 1, TailFallback = "PREVIOUS_CONTINUOUS_PAIR",
                QuantileMethod = "NEAREST_RANK", ReferenceSelection = "ASSIGNED_OR_DIRECTION_COMPATIBLE_MINIMUM_MEDIAN_FINITE_DISTANCE" },
            ParameterSnapshotScope = "WholeLegMatchOptions、TrackRecognitionOptions、固定轨迹规则、指定轴与参考分类快照。评估点组测量与识别内部测量并非相同计算；explicit_axis_error_degrees 独立于拟合线统计，包含未命中轨迹。",
            Sources = analysis.Files.Select(f => new { f.Path, f.SourceFileStableId }),
            EvidenceMethod = "按来源分流。相同时间记录的源使用UTC秒中位数/中心4秒窗口且不跨缺秒；精细UTC保留约1秒连续点组测量；瞬时速度另列。窗口覆盖速度范围记录在快照，不混同项目默认。参考线方向为任务有向方向或未归属段无向轴线。"
        }, Json));
        File.WriteAllText(Path.Combine(directory, "README.md"), Readme, Encoding.UTF8);
        var referenceGeometry = request.TrackSegmentation?.ReferenceGeometry ?? [];
        var recognizedCoverage = TaskTrackCoverageService.Summarize(tasks, execution.Passes);
        File.WriteAllText(Path.Combine(directory, "task-coverage.json"), JsonSerializer.Serialize(new
        {
            CoverageVersion = ObservedTrackCoverageService.Version,
            AssessmentIdentity = new
            {
                SourceFingerprint = analysis.Fingerprint,
                InputScope = inputScope,
                TrackSegmentationFingerprint = request.TrackSegmentationFingerprint,
                RecognitionVersion = request.RecognitionVersion
            },
            ReferenceAuthority = request.TrackSegmentation?.ReferenceAuthority,
            IsAuthoritativeReference = request.TrackSegmentation is { } referenceInput &&
                TrackSegmentationReferenceService.IsAuthoritative(referenceInput),
            ReferenceSnapshot = new
            {
                Source = request.TrackSegmentation?.ReferenceSource,
                FileHash = request.TrackSegmentation?.ReferenceFileHash,
                GeometryCount = referenceGeometry.Count,
                VersionCount = referenceGeometry.Sum(g => Math.Max(1, g.Versions.Count)),
                Kinds = referenceGeometry.GroupBy(g => g.Kind).ToDictionary(g => g.Key, g => g.Count()),
                Sources = referenceGeometry.SelectMany(g => g.Versions.Count > 0
                    ? g.Versions.Select(v => v.SourcePath)
                    : new[] { g.SourcePath }).Where(p => !string.IsNullOrWhiteSpace(p))
                    .Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray()
            },
            Recognized = recognizedCoverage,
            Selected = new { Status = "NOT_INCLUDED", Explanation = "飞行质量评估保存点没有生产选择快照；此处不推断右侧纳入范围。" },
            CsvOutput = new { Status = "NOT_GENERATED", Explanation = "飞行质量评估不代表已经生成Oasis CSV；CSV有效覆盖须在导出后独立核算。" },
            Interpretation = "识别覆盖按同一任务、连续时间观测区间并集计算，并在 Missing 中保留未覆盖区间；未匹配或待复核只表示归属证据不足，不等于已证明未飞。识别覆盖不是右侧选择覆盖、CSV输出覆盖或人工质量通过。实测自拟合线仍非独立完整性依据。"
        }, Json));
        // A complete manifest is written last. Incomplete batches are never calibration inputs.
        var files = Directory.GetFiles(directory).Order().Select(path => new
        { File = Path.GetFileName(path), Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) }).ToArray();
        File.WriteAllText(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(new { SchemaVersion, Complete = true, Files = files }, Json));
        return new(directory, active.Count, execution.Legs.Count);
    }

    private static string DescribeRecognitionThresholds(AuditProductionRequest request, WholeLegMatchOptions match,
        TrackRecognitionOptions track, IReadOnlyList<FlightMetricDistribution> distributions, IReadOnlyList<FlightThresholdObservation> thresholds)
    {
        var report = new StringBuilder();
        report.AppendLine().AppendLine("## 识别门槛与标定依据").AppendLine();
        report.AppendLine(request.TrackSegmentation?.Enabled == true ? "本次启用了轨迹分线，以下轨迹门槛参与识别。" : "本次未启用轨迹分线；以下轨迹门槛仅为保存的配置，不参与本次设计测线识别。");
        if (request.TrackSegmentation is { } segmentation)
        {
            report.AppendLine($"参数组：{segmentation.ParameterProfile}，版本{segmentation.ParameterProfileVersion}（实际数值已嵌入快照；切换参数组后需重新分析）。");
            report.AppendLine($"计划速度：{(segmentation.PlannedSpeedMps is { } planned ? planned.ToString("0.##", CultureInfo.InvariantCulture) + "m/s" : "未指定")}；窗口覆盖 {segmentation.WindowPlannedSpeeds.Count} 项；窗口8/12m/s分别使用4–12/4–14m/s质量提示范围，其余计划速度保持项目质量范围。");
        }
        report.AppendLine("秒精度来源使用UTC秒中位数/中心4秒运动窗口；精细UTC仍使用连续约1秒点组。历史CSV字段speed_1s_mps为兼容名称，实际窗口时长见样点证据。起降/升降/范围外证据仅作疑似往返提示，未自动删除或撤销身份。");
        report.AppendLine("评估使用约 1 秒连续点组测量方向；识别方向窗口如下单独记录，调整后需重新分析比较。");
        report.AppendLine($"- 指定测线轴：{(request.TrackSegmentation?.AxisDegrees is { } axis ? axis.ToString("0.##", CultureInfo.InvariantCulture) + "°（双向）" : "未指定")}");
        report.AppendLine($"- 轨迹方向容差：±{track.AxisToleranceDegrees:0.##}°；直飞/转角门槛：{track.StraightHeadingToleranceDegrees:0.##}°，持续 {track.TurnConfirmationSeconds:0.##} 秒确认；方向窗口 {track.DirectionWindowSeconds:0.##} 秒。");
        report.AppendLine($"- 候选最短跨度/时间：{track.MinimumSpanM:0.##} 米 / {track.MinimumDurationSeconds:0.##} 秒；方向一致比例至少 {track.MinimumAlignedRatio:P0}。");
        report.AppendLine($"- 重复身份门槛：轴差 {track.RepeatAxisToleranceDegrees:0.##}°、横距 {track.RepeatCrossTrackToleranceM:0.##} 米；放宽横距需核对相邻测线是否被错误合并。");
        report.AppendLine(track.ExpectedLineSpacingM > 0
            ? $"- 预期测线间距：{track.ExpectedLineSpacingM:0.##} 米；仅用于相邻线重复身份保护和整数倍线距支持证据，不单独赋予测线身份。"
            : "- 预期测线间距：未指定（间距辅助消歧/支持未启用）。");
        report.AppendLine($"- 偏航核心/容错通道：{match.CoreM:0.##} / {match.CorridorM:0.##} 米；端点/主体搜索：{match.EndpointSearchM:0.##} / {match.CandidateSearchM:0.##} 米；速度辅助范围 {match.MinimumFlightSpeedMps:0.##}–{match.MaximumFlightSpeedMps:0.##} 米/秒。");
        report.AppendLine().AppendLine("指定轴偏差独立于拟合线，按双向轴差统计，包含未命中和待复核轨迹；ALL_ACTIVE 中也有连接、往返与悬停。不能为了覆盖全部轨迹而直接放宽门槛。实测拟合偏差小不代表设计偏航合格。");
        report.AppendLine().AppendLine("| 群体 | 有效方向观测 | 中位数 ° | P95 ° | 方向容差内比例 |").AppendLine("| --- | ---: | ---: | ---: | ---: |");
        foreach (var distribution in distributions.Where(d => d.Metric == "explicit_axis_error_degrees"))
        {
            var threshold = thresholds.FirstOrDefault(t => t.Population == distribution.Population && t.Metric == distribution.Metric &&
                Math.Abs(t.Threshold - track.AxisToleranceDegrees) < 1e-6);
            report.AppendLine($"| {distribution.Population} | {distribution.Count} | {distribution.P50:0.##} | {distribution.P95:0.##} | {threshold?.WithinRatio:P1} |");
        }
        report.AppendLine().AppendLine("完整分布见 summary.json；多阈值对比见 threshold-distributions.csv；实际配置和固定规则见 input-and-parameters.json。先人工核对代表性正确/错误航段，再修改识别门槛并重跑同一批数据；报告不自动修改参数，也不自动通过质量审核。");
        return report.ToString();
    }

    private static Dictionary<(string, int), Motion> MeasureMotion(UavMagCanonicalSample[] samples, WholeLegMatchOptions options)
    {
        var result = new Dictionary<(string, int), Motion>();
        var coarseSources = samples.GroupBy(s => s.SourceFile).Where(g => g.Select(s => s.TimestampUtc).Distinct().Count() < g.Count()).Select(g => g.Key).ToHashSet();
        var measured = TrackMotionService.Measure(samples.Where(Valid).Select(s =>
            new SurveyTrackPoint(s.TimestampUtc, new(s.Longitude!.Value, s.Latitude!.Value), s.SourceFile, s.SourceRecord) { AltitudeM = s.AltitudeM }))
            .ToDictionary(o => (o.Source, o.Time.ToUnixTimeSeconds()));
        foreach (var stream in samples.GroupBy(s => s.SourceFile, StringComparer.OrdinalIgnoreCase))
        {
            var ordered = stream.OrderBy(s => s.TimestampUtc).ThenBy(s => s.SourceRecord).ToArray();
            for (int i = 0, right = 0; i < ordered.Length; i++)
            {
                var s = ordered[i];
                double? gap = i == 0 ? null : (s.TimestampUtc - ordered[i - 1].TimestampUtc).TotalSeconds;
                double? instant = gap is > 0 && gap <= options.MaximumEvidenceGapSeconds && Valid(s) && Valid(ordered[i - 1])
                    ? CoordinateDistance(s, ordered[i - 1]) / gap : null;
                right = Math.Max(i, right);
                while (right + 1 < ordered.Length && Valid(ordered[right]) && Valid(ordered[right + 1]) &&
                    (ordered[right + 1].TimestampUtc - s.TimestampUtc).TotalSeconds <= 1 &&
                    (ordered[right + 1].TimestampUtc - ordered[right].TimestampUtc).TotalSeconds is > 0 &&
                    (ordered[right + 1].TimestampUtc - ordered[right].TimestampUtc).TotalSeconds <= options.MaximumEvidenceGapSeconds) right++;
                int first = i, last = right;
                if (last == first && i > 0 && gap is > 0 && gap <= options.MaximumEvidenceGapSeconds && Valid(ordered[i - 1])) first = i - 1;
                double seconds = (ordered[last].TimestampUtc - ordered[first].TimestampUtc).TotalSeconds;
                bool valid = seconds > 0 && Valid(s) && Valid(ordered[first]) && Valid(ordered[last]);
                double? speed = valid ? CoordinateDistance(ordered[first], ordered[last]) / seconds : null;
                double? climb = valid && ordered[first].AltitudeM is { } h0 && ordered[last].AltitudeM is { } h1 && double.IsFinite(h0) && double.IsFinite(h1)
                    ? (h1 - h0) / seconds : null;
                double? bearing = valid && CoordinateDistance(ordered[first], ordered[last]) >= 1 ? SurveyDirection.Bearing(new(ordered[first].Longitude!.Value, ordered[first].Latitude!.Value),
                    new(ordered[last].Longitude!.Value, ordered[last].Latitude!.Value)) : null;
                // Same-second originals share a robust telemetry measurement;
                // adjacent raw speed remains a separate diagnostic channel.
                var measuredKey = (s.SourceFile, s.TimestampUtc.ToUnixTimeSeconds());
                result[(s.SourceFile, s.SourceRecord)] = coarseSources.Contains(s.SourceFile) && Valid(s) && measured.TryGetValue(measuredKey, out var observation)
                    ? new(observation.SpeedMps, instant, observation.ClimbMps, observation.Bearing, gap, observation.DurationSeconds)
                    : new(speed, instant, climb, bearing, gap, valid ? seconds : null);
            }
        }
        return result;
    }
    private static bool Valid(UavMagCanonicalSample s) => s.Longitude is { } lon && s.Latitude is { } lat && WorkareaSpatialService.ValidCoordinate(lon, lat);
    private static string DescribeFlightQuality(FlightQualityIndicatorsResult result, FlightQualityAssessmentOptions options)
    {
        static string N(double? value) => value is { } n ? n.ToString("0.###", CultureInfo.InvariantCulture) : "缺少依据";
        static string Text(string value) => value.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
        var report = new StringBuilder();
        report.AppendLine("# 飞行质量评估").AppendLine();
        report.AppendLine("指标用于飞行质量辅助评估。表格是当前候选归属的描述；人工确认、质量通过和正式生产选用仍需现有核验流程。").AppendLine();
        report.AppendLine($"本工区测线距：{N(options.LineSpacingM)} m；平均偏航限值：{N(options.LineSpacingM / 5)} m；最大偏航限值：{N(options.LineSpacingM / 3)} m。示例为严格小于限值。未提供测线距时不推断也不输出达标结论。").AppendLine();
        report.AppendLine($"设计离地飞高：{N(options.DesignedFlightHeightM)} m；激光高度依据：{(options.LaserAltitudeIsAboveGround ? "用户确认有效离地垂高" : "尚未确认") }。设计偏差提示比例为 {options.HeightToleranceRatio:P0}，不是测高仪器的准确度验收。").AppendLine();
        report.AppendLine("| 测线 | 有效偏航点 | 平均偏航 m | 最大偏航 m | 离地均高 m | 人工归属确认 |")
            .AppendLine("| --- | ---: | ---: | ---: | ---: | --- |");
        foreach (var line in result.TaskLines)
            report.AppendLine($"| {Text(line.LineName)} | {line.ValidOffsetPoints} | {N(line.MeanOffsetM)} | {N(line.MaximumOffsetM)} | {N(line.MeanAboveGroundHeightM)} | {(line.IdentityReviewed ? "已确认" : "待核验")} |");
        report.AppendLine().AppendLine("同一设计任务多次通过按来源记录去重合并；每次通过见 line-flight-quality.csv。GPS/激光原值均保留，零或负激光高度不作为有效离地测距。测高仪器的10%误差指标缺少独立高度参照，未作评价。").AppendLine();
        report.AppendLine("GPS 静态定位采用 N-1 样本标准差，平面为 sqrt(σX²+σY²)，高程为 σZ。它描述静态重复性，不等于对已知控制点的准确度。").AppendLine();
        report.AppendLine("| 静态试验来源 | 样点 | 平面标准差 m | 高程标准差 m | 状态 |")
            .AppendLine("| --- | ---: | ---: | ---: | --- |");
        foreach (var trial in result.StaticTests)
            report.AppendLine($"| {Text(trial.SourceScope)} | {trial.SampleCount} | {N(trial.PlanePrecisionM)} | {N(trial.VerticalPrecisionM)} | {(trial.Status == "AVAILABLE" ? "已计算" : "无法评价：" + trial.Status)} |");
        report.AppendLine().AppendLine("数据收录统计见 summary.json 的 DataRecording：坐标/磁场/激光覆盖、重复时间戳和质量标记。缺少期望采样率时不推断丢样率。静态磁噪声、动态磁噪声与调平后重复线总精度属于另行的数据质量评价。");
        return report.ToString();
    }
    private static double CoordinateDistance(UavMagCanonicalSample a, UavMagCanonicalSample b)
    {
        var (x, y) = EndpointLineRecognizer.Delta(new(a.Longitude!.Value, a.Latitude!.Value), new(b.Longitude!.Value, b.Latitude!.Value));
        return Math.Sqrt(x * x + y * y);
    }
    private static double SegmentDistance(Geometry g, GeoCoordinate point)
    {
        var (x, y) = EndpointLineRecognizer.Delta(g.Task.Start, point);
        double along = x * g.Ux + y * g.Uy, cross = x * g.Uy - y * g.Ux;
        double beyond = along < 0 ? along : Math.Max(0, along - g.Length);
        return Math.Sqrt(cross * cross + beyond * beyond);
    }
    private static double EndpointDistance(Geometry g, FlightLegSample s, double? bearing, bool exit)
    {
        bool reverse = bearing is { } b && SurveyDirection.Difference(b, g.Bearing) > 90;
        var (x, y) = EndpointLineRecognizer.Delta(reverse != exit ? g.Task.End : g.Task.Start, new(s.Longitude, s.Latitude));
        return Math.Sqrt(x * x + y * y);
    }
    private static double? Quantile(double[] sorted, double q) => sorted.Length == 0 ? null : sorted[(int)Math.Clamp(Math.Ceiling(q * sorted.Length) - 1, 0, sorted.Length - 1)];
    private static void WriteCsv(string path, object?[] header, IEnumerable<object?[]> rows)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        WriteRow(writer, header); foreach (var row in rows) WriteRow(writer, row);
    }
    private static void WriteRow(TextWriter writer, object?[] values) => writer.WriteLine(string.Join(",", values.Select(value =>
    {
        string text = value switch { null => "", DateTimeOffset t => t.ToUniversalTime().ToString("O"),
            double n => double.IsFinite(n) ? n.ToString("G17", CultureInfo.InvariantCulture) : "", _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "" };
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    })));
    private const string Readme = """
        # 飞行质量评估与参数标定资料

        - summary.json：分类样点数量、指标分布。缺少证据是空值，不能当作零。
        - flight-quality-report.md：中文逐测线偏航/离地高度和静态 GPS 指标摘要，便于直接阅读。
        - input-and-parameters.json：数据/航线/筛选版本、窗口、排除依据、人工决定、任务几何、可配置识别参数及评估测量参数。
        - point-evidence.csv.gz：全部当前筛选输入逐点时间/位置/高度、约1s点组及瞬时速度、升降、间隔、沿线/横距和提示依据。
        - segment-quality.csv：所有已匹配、竞争和未匹配航段，参考任务、端点欧氏距离、切割/确认时间及核验状态。
        - line-flight-quality.csv：逐任务和逐通过的平均/最大偏航、离地高度及设计限值检查；仅已归属样点，不用未归属参考线评价正式测线。
        - static-gps-quality.csv / static-gps-evidence.csv：明确选定的静态试验结果与原始依据，静态点从原分析读取并与当前飞行筛选分开。
        - task-quality.csv：所有设计任务，包含零候选任务和候选覆盖。
        - nearby-task-hints.csv：每航段最近的三条有限设计线及轴向角差；仅提供检查提示。
        - threshold-distributions.csv：按分类分别统计不同距离/速度/方向阈值下的观测比例，分母只包括指标有效记录。
        - explicit_axis_error_degrees：独立于拟合线的指定双向轴偏差；包含未命中轨迹，当前方向门槛也参与阈值对比。实测拟合线使用 ACTUAL_FIT 群体，不能当作设计偏航合格依据。
        - segment_span_m / segment_duration_seconds：按航段类别统计跨度、持续时间，可用于核对短航段门槛；不是逐点统计。
        - manual-review-template.csv：可填写人工航段类别、实际测线身份和起止记录，用于以后比较漏检/误识别。
        - manifest.json：完整标记与各文件 SHA256。只有完整且校验相符的批次可用于参数比较。

        已匹配航段参考其已建议/人工指定任务；未匹配航段参考方向相近且空间最近的有限测线，均不代表人工真值。
        无端点命中、低核心比例或多质量异常都不能单独推出身份错误。
        速度/升降/方向按1s连续点组测量，过大时间缺口和无效坐标不填补。源文件之间不拼接运动证据。尾部可使用前一连续点对；实际点组时长见 MotionWindowSeconds。位移不足1m不估计方向。
        ALL_ACTIVE 包含起降、转场、连接和未归属数据；请单独比较 SURVEY 与 SURVEY_CANDIDATE，避免用全量速度分布调整作业阈值。
        阈值表是分布观察，不是重新运行分线后的准确率；一次观测超过60度也不代表已满足持续2秒的断点证据。
        当前分段仍受现行算法影响，请结合逐点资料和人工标签评估漏检，不以匹配任务数量增加证明准确性。
        偏航限值来自用户示例报告：平均小于测线距1/5，最大小于1/3；需要本工区明确测线距。示例20m、80m不自动套用。
        AltitudeM为原高度字段；激光原值需在评估设置确认是有效离地垂高后才作为AGL。离地设计偏差不等于测高仪器的10%测量误差。
        GPS静态计算为N-1样本标准差，平面为sqrt(σX²+σY²)；是重复性，不是对已知基准的绝对准确度。
        每批次独立保存，不覆盖旧批次，不自动修改参数、不审批质量、不改变生产选点。本报告评估轨迹飞行质量，不代替磁测仪器质量验收。
        """;
}
