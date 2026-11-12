using MagOps.Core.Domain;

namespace MagOps.Infrastructure.Services;

public sealed record FlightQualityAssessmentOptions(
    double? LineSpacingM = null,
    double? DesignedFlightHeightM = null,
    double HeightToleranceRatio = .10d,
    IReadOnlyList<string>? StaticTestSourceFiles = null,
    DateTimeOffset? StaticTestStartUtc = null,
    DateTimeOffset? StaticTestEndUtc = null,
    bool LaserAltitudeIsAboveGround = false)
{
    public void Validate()
    {
        if (LineSpacingM is { } spacing && (!double.IsFinite(spacing) || spacing <= 0)) throw new ArgumentOutOfRangeException(nameof(LineSpacingM));
        if (DesignedFlightHeightM is { } height && (!double.IsFinite(height) || height <= 0)) throw new ArgumentOutOfRangeException(nameof(DesignedFlightHeightM));
        if (!double.IsFinite(HeightToleranceRatio) || HeightToleranceRatio < 0) throw new ArgumentOutOfRangeException(nameof(HeightToleranceRatio));
        if (StaticTestStartUtc is { } start && StaticTestEndUtc is { } end && end < start) throw new ArgumentException("静态 GPS 试验结束时间早于开始时间。");
        if ((StaticTestStartUtc is null) != (StaticTestEndUtc is null)) throw new ArgumentException("静态试验起止时间必须同时填写。");
    }
}

public sealed record FlightLineQualityRow(
    string LineName, string TaskId, string LegId, string Classification, int ValidOffsetPoints,
    double? MeanOffsetM, double? MaximumOffsetM, double? P95OffsetM, int AltitudePoints,
    double? MeanAltitudeM, double? MinimumAltitudeM, double? MaximumAltitudeM,
    double? DesignedHeightM, double? HeightErrorMeanM, double? HeightErrorP95M,
    double? MeanOffsetThresholdM, double? MaximumOffsetThresholdM,
    int? OffsetWithinMeanLimit, int? OffsetWithinMaximumLimit, string QualityStatus, bool IdentityReviewed)
{
    public string HeightReference { get; init; } = "UNCONFIRMED_LASER";
    public double? MeanAboveGroundHeightM => HeightReference == "USER_CONFIRMED_AGL" ? MeanAltitudeM : null;
    public double HeightToleranceRatio { get; init; }
    public string HeightDesignCheck => HeightErrorP95M is not { } error || DesignedHeightM is not { } design ? "UNAVAILABLE" : error <= design * HeightToleranceRatio ? "P95_WITHIN_DESIGN_TOLERANCE" : "P95_EXCEEDS_DESIGN_TOLERANCE";
    public string NavigationMeanCheck => MeanOffsetM is not { } m || MeanOffsetThresholdM is not { } limit ? "UNAVAILABLE" : m < limit ? "WITHIN_LIMIT" : "EXCEEDS_LIMIT";
    public string NavigationMaximumCheck => MaximumOffsetM is not { } m || MaximumOffsetThresholdM is not { } limit ? "UNAVAILABLE" : m < limit ? "WITHIN_LIMIT" : "EXCEEDS_LIMIT";
    public object?[] ToRow() => [LineName, TaskId, LegId, Classification, ValidOffsetPoints, MeanOffsetM, MaximumOffsetM, P95OffsetM,
        AltitudePoints, MeanAltitudeM, MinimumAltitudeM, MaximumAltitudeM, DesignedHeightM, HeightErrorMeanM, HeightErrorP95M,
        MeanOffsetThresholdM, MaximumOffsetThresholdM, OffsetWithinMeanLimit, OffsetWithinMaximumLimit, QualityStatus, IdentityReviewed, NavigationMeanCheck, NavigationMaximumCheck, HeightReference, HeightDesignCheck, MeanAboveGroundHeightM];
}

public sealed record StaticGpsQualityRow(
    string SourceScope, int SampleCount, double? MeanLongitude, double? MeanLatitude, double? MeanAltitudeM,
    double? PlanePrecisionM, double? VerticalPrecisionM, string Status, string Reason)
{
    public object?[] ToRow() => [SourceScope, SampleCount, MeanLongitude, MeanLatitude, MeanAltitudeM, PlanePrecisionM, VerticalPrecisionM, Status, Reason];
}

public sealed record FlightQualityIndicatorsResult(
    IReadOnlyList<FlightLineQualityRow> Lines,
    IReadOnlyList<FlightLineQualityRow> TaskLines,
    IReadOnlyList<StaticGpsQualityRow> StaticTests,
    IReadOnlyList<UavMagCanonicalSample> StaticSamples,
    object Summary)
{
}

public sealed class FlightQualityIndicatorsService
{
    public static readonly object?[] LineHeader = ["LineName", "TaskId", "LegId", "Classification", "ValidOffsetPoints", "MeanOffsetM", "MaximumOffsetM", "P95OffsetM", "AltitudePoints", "MeanAltitudeM", "MinimumAltitudeM", "MaximumAltitudeM", "DesignedHeightM", "HeightErrorMeanM", "HeightErrorP95M", "MeanOffsetThresholdM", "MaximumOffsetThresholdM", "OffsetWithinMeanLimit", "OffsetWithinMaximumLimit", "QualityStatus", "IdentityReviewed", "NavigationMeanCheck", "NavigationMaximumCheck", "HeightReference", "HeightDesignCheck", "MeanAboveGroundHeightM"];
    public static readonly object?[] StaticHeader = ["SourceScope", "SampleCount", "MeanLongitude", "MeanLatitude", "MeanAltitudeM", "PlanePrecisionM", "VerticalPrecisionM", "Status", "Reason"];
    public FlightQualityIndicatorsResult Build(
        IReadOnlyList<UavMagCanonicalSample> rawSamples,
        IReadOnlyList<UavMagCanonicalSample> activeSamples,
        IReadOnlyList<PlannedSurveyLineTask> tasks,
        SurveyExecutionResult execution,
        FlightQualityAssessmentOptions options)
    {
        options.Validate();
        var taskById = tasks.Where(t => !string.IsNullOrWhiteSpace(t.SurveyTaskStableId))
            .GroupBy(t => t.SurveyTaskStableId, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.Single(), StringComparer.OrdinalIgnoreCase);
        var sourceLookup = activeSamples.ToDictionary(s => (s.SourceFile, s.SourceRecord));
        var legs = execution.Legs.ToDictionary(l => l.FlightLegId, StringComparer.OrdinalIgnoreCase);
        // A line spacing is a project design input. Inferring it from task centers
        // is unsafe when clipped, cross, or partitioned lines are present.
        double? spacing = options.LineSpacingM;
        var lines = new List<FlightLineQualityRow>();
        FlightLineQualityRow MakeRow(PlannedSurveyLineTask task, string legId, string classification,
            IEnumerable<FlightLegSample> observations, bool reviewed, string qualityStatus)
        {
            var samples = observations.Where(s => sourceLookup.ContainsKey((s.SourceFile, s.SourceRecord)))
                .DistinctBy(s => (s.SourceFile, s.SourceRecord)).ToArray();
            var offsets = Valid(task.Start) && Valid(task.End) && TaskLength(task) >= .5
                ? samples.Where(s => Valid(new GeoCoordinate(s.Longitude, s.Latitude)))
                    .Select(s => Math.Abs(SignedCross(task, new(s.Longitude, s.Latitude)))).Where(double.IsFinite).Order().ToArray()
                : [];
            var heights = samples.Select(s => sourceLookup[(s.SourceFile, s.SourceRecord)].LaserAltitudeM)
                .Where(v => v is { } h && IsUsableLaserHeight(h)).Select(v => v!.Value).ToArray();
            double? heightMean = Mean(heights);
            double? heightError = options.LaserAltitudeIsAboveGround && options.DesignedFlightHeightM is { } design && heightMean is { } actual ? Math.Abs(actual - design) : null;
            double? heightP95 = options.LaserAltitudeIsAboveGround && options.DesignedFlightHeightM is { } design2 && heights.Length > 0 ? Quantile(heights.Select(h => Math.Abs(h - design2)).Order().ToArray(), .95) : null;
            double? meanLimit = spacing / 5d, maxLimit = spacing / 3d;
            if (task.GeometryOrigin == "ACTUAL_TRACK") { meanLimit = null; maxLimit = null; }
            return new(task.LineName, task.SurveyTaskStableId, legId, classification, offsets.Length, Mean(offsets),
                offsets.Length == 0 ? null : offsets[^1], Quantile(offsets, .95), heights.Length, heightMean,
                heights.Length == 0 ? null : heights.Min(), heights.Length == 0 ? null : heights.Max(),
                options.DesignedFlightHeightM, heightError, heightP95, meanLimit, maxLimit,
                meanLimit is { } ml ? offsets.Count(x => x < ml) : null,
                maxLimit is { } xl ? offsets.Count(x => x < xl) : null, qualityStatus, reviewed)
            { HeightReference = options.LaserAltitudeIsAboveGround ? "USER_CONFIRMED_AGL" : "UNCONFIRMED_LASER",
                HeightToleranceRatio = options.HeightToleranceRatio };
        }
        foreach (var pass in execution.Passes)
        {
            if (!legs.TryGetValue(pass.FlightLegId, out var leg) || !taskById.TryGetValue(pass.SurveyTaskStableId, out var task)) continue;
            lines.Add(MakeRow(task, leg.FlightLegId, leg.Classification, leg.Samples, pass.ReviewConfirmed, pass.QualityStatus));
        }
        var taskLines = new List<FlightLineQualityRow>();
        foreach (var task in tasks)
        {
            var passes = execution.Passes.Where(p => string.Equals(p.SurveyTaskStableId, task.SurveyTaskStableId, StringComparison.OrdinalIgnoreCase)
                && legs.ContainsKey(p.FlightLegId)).ToArray();
            taskLines.Add(MakeRow(task, "", "TASK_AGGREGATE", passes.SelectMany(p => legs[p.FlightLegId].Samples),
                passes.Length > 0 && passes.All(p => p.ReviewConfirmed), passes.Length == 0 ? "NO_MATCH_CANDIDATE" : "CANDIDATE_AGGREGATE_REQUIRES_REVIEW"));
        }
        bool staticSelected = options.StaticTestSourceFiles?.Count > 0 || options.StaticTestStartUtc is not null;
        var staticSamples = rawSamples.Where(s => staticSelected && (options.StaticTestSourceFiles is null || options.StaticTestSourceFiles.Count == 0 || options.StaticTestSourceFiles.Contains(s.SourceFile, StringComparer.OrdinalIgnoreCase)) &&
            (options.StaticTestStartUtc is null || s.TimestampUtc >= options.StaticTestStartUtc) &&
            (options.StaticTestEndUtc is null || s.TimestampUtc <= options.StaticTestEndUtc)).ToArray();
        var staticRows = staticSamples.GroupBy(s => s.SourceFile, StringComparer.OrdinalIgnoreCase)
            .Select(g => BuildStatic(g.ToArray(), options)).ToArray();
        var staticResult = staticRows.Length == 1 ? staticRows[0] : BuildStatic([], options);
        var surveyKeys = execution.Passes.Where(p => legs.ContainsKey(p.FlightLegId))
            .SelectMany(p => legs[p.FlightLegId].Samples).Select(s => (s.SourceFile, s.SourceRecord)).ToHashSet();
        var surveySamples = activeSamples.Where(s => surveyKeys.Contains((s.SourceFile, s.SourceRecord))).ToArray();
        var laser = surveySamples.Select(s => s.LaserAltitudeM).Where(v => v is { } h && IsUsableLaserHeight(h)).Select(v => v!.Value).ToArray();
        var gps = surveySamples.Select(s => s.AltitudeM).Where(v => v is { } h && double.IsFinite(h)).Select(v => v!.Value).ToArray();
        var summary = new
        {
            NavigationPositioning = new { Metric = "每条测线平均/最大偏航距", LineSpacingM = spacing,
                AssignedPasses = lines.Count, EvaluatedLines = taskLines.Count(l => l.ValidOffsetPoints > 0), MeanOfLineMeansM = Mean(taskLines.Select(x => x.MeanOffsetM).Where(x => x is { }).Select(x => x!.Value).ToArray()),
                MaximumOfLineMaximumsM = lines.Count == 0 ? (double?)null : taskLines.Max(x => x.MaximumOffsetM),
                MeanThresholdM = spacing / 5d, MaximumThresholdM = spacing / 3d,
                LinesWithMeanWithinLimit = spacing is null ? (int?)null : taskLines.Count(x => x.MeanOffsetM is { } m && spacing is { } sp && m < sp / 5d),
                LinesWithMaximumWithinLimit = spacing is null ? (int?)null : taskLines.Count(x => x.MaximumOffsetM is { } m && spacing is { } sp && m < sp / 3d),
                Status = lines.Count == 0 ? "NO_LINE_REFERENCE" : spacing is null ? "DESCRIPTIVE_ONLY_NO_LINE_SPACING" : "AVAILABLE" },
            FlightHeight = new { DesignedFlightHeightM = options.DesignedFlightHeightM, HeightToleranceRatio = options.HeightToleranceRatio,
                LaserAltitudeIsAboveGround = options.LaserAltitudeIsAboveGround, LaserAltitudeCount = laser.Length,
                Population = "ASSIGNED_SURVEY_SAMPLES", PopulationSamples = surveySamples.Length,
                MeanLaserAltitudeM = Mean(laser), MinimumLaserAltitudeM = laser.Length == 0 ? (double?)null : laser.Min(),
                MeanAboveGroundHeightM = options.LaserAltitudeIsAboveGround ? Mean(laser) : null,
                MaximumLaserAltitudeM = laser.Length == 0 ? (double?)null : laser.Max(), MeanGpsAltitudeM = Mean(gps),
                LinesWithHeightMeanWithinLimit = options.LaserAltitudeIsAboveGround && options.DesignedFlightHeightM is not null ? (int?)taskLines.Count(x => x.HeightErrorMeanM is { } e && options.DesignedFlightHeightM is { } h && e <= h * options.HeightToleranceRatio) : null,
                Note = "设计偏差检查只用于提示；缺失高度不算达标，均值通过也不代表逐点通过。",
                MeasurementAccuracyCheck = "UNAVAILABLE_NO_INDEPENDENT_HEIGHT_REFERENCE",
                Status = laser.Length == 0 ? "NO_LASER_ALTITUDE" : options.DesignedFlightHeightM is null ? "DESCRIPTIVE_ONLY_NO_DESIGN_HEIGHT" : options.LaserAltitudeIsAboveGround ? "AVAILABLE" : "HEIGHT_REFERENCE_UNCONFIRMED" },
            GpsStaticPositioning = new { Status = staticRows.Length > 1 ? "SEPARATE_STATIC_TRIALS" : staticResult.Status,
                SampleCount = staticSamples.Length, staticResult.PlanePrecisionM, staticResult.VerticalPrecisionM,
                Trials = staticRows, EvidenceSource = "RAW_ANALYSIS_EXPLICIT_STATIC_TEST_SELECTION", staticResult.Reason },
            DataRecording = new { TotalSamples = activeSamples.Count, RawSampleCount = rawSamples.Count,
                ValidCoordinateSamples = activeSamples.Count(Valid), MissingCoordinateSamples = activeSamples.Count(s => !Valid(s)),
                DuplicateSourceRecordCount = activeSamples.GroupBy(s => (s.SourceFile, s.SourceRecord)).Sum(g => Math.Max(0, g.Count() - 1)),
                DuplicateTimestampCount = activeSamples.GroupBy(s => new { Source = s.SourceFile.ToUpperInvariant(), s.TimestampUtc }).Sum(g => Math.Max(0, g.Count() - 1)),
                QualityFlagSampleCount = activeSamples.Count(s => s.QualityFlags.Count > 0), SourceFileCount = activeSamples.Select(s => s.SourceFile).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                LaserAltitudeCoverage = activeSamples.Count == 0 ? null : (double?)activeSamples.Count(s => s.LaserAltitudeM is { } h && IsUsableLaserHeight(h)) / activeSamples.Count,
                MagneticValueCoverage = activeSamples.Count == 0 ? null : (double?)activeSamples.Count(s => s.MagneticFieldNt is { } m && double.IsFinite(m)) / activeSamples.Count }
        };
        return new(lines, taskLines, staticRows.Length == 0 ? [staticResult] : staticRows, staticSamples, summary);
    }

    private static StaticGpsQualityRow BuildStatic(UavMagCanonicalSample[] samples, FlightQualityAssessmentOptions options)
    {
        bool selected = options.StaticTestSourceFiles?.Count > 0 || options.StaticTestStartUtc is not null;
        bool multipleSources = samples.Select(s => s.SourceFile).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1;
        var coordinates = samples.Where(Valid).ToArray();
        var anchor = coordinates.FirstOrDefault();
        var coords = anchor is null ? [] : coordinates.Select(s => EndpointLineRecognizer.Delta(new(anchor.Longitude!.Value, anchor.Latitude!.Value), new(s.Longitude!.Value, s.Latitude!.Value))).ToArray();
        double? plane = coords.Length >= 2 ? Math.Sqrt(Math.Pow(Sd(coords.Select(x => x.X).ToArray()), 2) + Math.Pow(Sd(coords.Select(x => x.Y).ToArray()), 2)) : null;
        var z = samples.Select(s => s.AltitudeM).Where(v => v is { } h && double.IsFinite(h)).Select(v => v!.Value).ToArray();
        double? vertical = z.Length >= 2 ? Sd(z) : null;
        if (!selected || multipleSources) { plane = null; vertical = null; }
        string status = !selected ? "UNAVAILABLE_NO_STATIC_TEST_WINDOW" : multipleSources ? "STATIC_WINDOW_MULTIPLE_SOURCES_REVIEW" : plane is null || vertical is null ? "STATIC_WINDOW_INSUFFICIENT_VALID_POINTS" : "AVAILABLE";
        return new(selected ? samples.FirstOrDefault()?.SourceFile ?? "EXPLICIT_STATIC_TEST_WINDOW" : "NONE", samples.Length, coordinates.Length == 0 ? null : coordinates.Average(s => s.Longitude!.Value), coordinates.Length == 0 ? null : coordinates.Average(s => s.Latitude!.Value), Mean(z), plane, vertical, status,
            "GPS静态精度只对明确选定的静态试验窗口计算，不用飞行轨迹代替。");
    }

    private static double TaskLength(PlannedSurveyLineTask task)
    { var (x, y) = EndpointLineRecognizer.Delta(task.Start, task.End); return Math.Sqrt(x * x + y * y); }
    private static bool IsUsableLaserHeight(double value) => double.IsFinite(value) && value > 0;
    private static bool Valid(GeoCoordinate p) => WorkareaSpatialService.ValidCoordinate(p.Longitude, p.Latitude);
    private static bool Valid(UavMagCanonicalSample s) => s.Longitude is { } x && s.Latitude is { } y && WorkareaSpatialService.ValidCoordinate(x, y);
    private static double SignedCross(PlannedSurveyLineTask task, GeoCoordinate point) { var (dx, dy) = EndpointLineRecognizer.Delta(task.Start, task.End); double l = Math.Sqrt(dx * dx + dy * dy); var (x, y) = EndpointLineRecognizer.Delta(task.Start, point); return (x * dy - y * dx) / Math.Max(.5, l); }
    private static double? Mean(double[] values) => values.Length == 0 ? null : values.Average();
    private static double? Quantile(double[] values, double q) => values.Length == 0 ? null : values[(int)Math.Clamp(Math.Ceiling(values.Length * q) - 1, 0, values.Length - 1)];
    private static double Sd(double[] values) { if (values.Length < 2) return double.NaN; double mean = values.Average(); return Math.Sqrt(values.Sum(x => (x - mean) * (x - mean)) / (values.Length - 1)); }
}
