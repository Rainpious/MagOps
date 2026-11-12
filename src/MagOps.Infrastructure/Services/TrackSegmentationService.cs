using MagOps.Core.Domain;

namespace MagOps.Infrastructure.Services;

public sealed record TrackSegmentationResult(IReadOnlyList<PlannedSurveyLineTask> Tasks, SurveyExecutionResult Execution, double? AxisDegrees);

/// <summary>Reference-assisted recognition. Remainders stay visible; inferred identities are explicitly actual lines.</summary>
public sealed class TrackSegmentationService
{
    public const string AlgorithmVersion = "track-axis-parallel-12-whole-track-recovery";
    private sealed record Run(string Mission, List<SurveyTrackPoint> Points, double Heading, double Length, bool Straight, DateTimeOffset? EndConfirmedUtc = null, bool BodyRefined = false)
    {
        public IReadOnlyList<string> CompetingTaskIds { get; init; } = [];
    }

    public TrackSegmentationResult Recognize(IReadOnlyList<PlannedSurveyLineTask> design,
        IReadOnlyList<SurveyTrackPoint> points, IReadOnlyList<MissionTimeWindow> windows, TrackSegmentationInput input)
    {
        var options = input.RecognitionOptions ?? new();
        options.Validate();
        SegmentationParameterStore.Validate(input);
        var matchOptions = input.MatchOptions ?? new();
        if (input.AxisDegrees is { } axis && (!double.IsFinite(axis) || axis < 0 || axis >= 180))
            throw new InvalidDataException("测线轴方向必须为 0（北）至小于 180 度；双向飞行均允许。");
        if (input.ReferenceGeometry is null || input.ReferenceGeometry.Any(g => string.IsNullOrWhiteSpace(g.Id) || g.Kind is not ("SURVEY" or "TIE" or "CONNECTOR" or "REVIEW") ||
            !WorkareaSpatialService.ValidCoordinate(g.Start.Longitude, g.Start.Latitude) ||
            !WorkareaSpatialService.ValidCoordinate(g.End.Longitude, g.End.Latitude)))
            throw new InvalidDataException("参考线快照包含无效类别或坐标。");
        if (input.ReferenceGeometry.Select(g => g.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != input.ReferenceGeometry.Count)
            throw new InvalidDataException("参考线快照包含重复身份，请重新导入。");
        bool authoritativeReference = TrackSegmentationReferenceService.IsAuthoritative(input);
        // A known mission identity needs evidence of a traversal, not the
        // 60 m/5 s threshold used to invent a line from an anonymous run.
        if (authoritativeReference) matchOptions = matchOptions with
        { MinimumLegCoverage = 0, MinimumBodySpanM = Math.Min(matchOptions.MinimumBodySpanM, 20) };
        var references = input.ReferenceGeometry.Where(g => g.Kind is "SURVEY" or "TIE").Select((g, i) =>
            new PlannedSurveyLineTask(i + 1, 0, g.Id, "参考航线", g.Name, g.Start, g.End,
                TrackSegmentationReferenceService.Distance(g.Start, g.End))
            { SurveyTaskStableId = g.Id, SurveyLineStableId = g.Id, SegmentId = g.Id, GeometryOrigin = "IMPORTED_REFERENCE" }).ToArray();
        var validDesign = design.Where(t => !string.IsNullOrWhiteSpace(t.SurveyTaskStableId))
            .GroupBy(t => t.SurveyTaskStableId).Where(g => g.Count() == 1).Select(g => g.Single()).ToArray();
        // Keep formal identities when auxiliary geometry describes the same line. Unmapped references can assist missing tasks.
        var known = input.UseDesignTasks && validDesign.Length > 0 ? validDesign.Concat(references.Where(r => !validDesign.Any(t =>
                TrackSegmentationReferenceService.AxisDifference(SurveyDirection.Bearing(r.Start, r.End) ?? 0, SurveyDirection.Bearing(t.Start, t.End) ?? 0) <= 5 &&
                DistanceToLine(r.Start, t.Start, t.End) < 8 && DistanceToLine(r.End, t.Start, t.End) < 8 && Overlap(t, r) > .5))
            .Select((r, i) => r with { Order = validDesign.Max(t => t.Order) + i + 1 })).ToArray() : references;
        var explicitTieIds = input.ReferenceGeometry.Where(g => g.Kind == "TIE").Select(g => g.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // An explicitly supplied axis constrains both reference matching and actual inference.
        // Keep incompatible design identities visible for manual review, but never seed automatic matches with them.
        bool AxisAllows(PlannedSurveyLineTask task) => input.AxisDegrees is null || explicitTieIds.Contains(task.SurveyTaskStableId) ||
            SurveyDirection.Bearing(task.Start, task.End) is { } heading &&
            TrackSegmentationReferenceService.AxisDifference(heading, input.AxisDegrees.Value) <= options.AxisToleranceDegrees;
        var matchingTasks = known.Where(AxisAllows).ToArray();
        // Actual inference is separate from the legacy matching engine and its no-task semantics.
        var matchWindows = windows.Select(w => w with
        {
            SurveyTaskStableIds = w.TaskScopeSource == "MANUAL"
                ? w.SurveyTaskStableIds.Where(id => matchingTasks.Any(t => t.SurveyTaskStableId == id)).ToArray()
                : matchingTasks.Select(t => t.SurveyTaskStableId).ToArray()
        }).ToArray();
        // Apply recovery after connector and explicit-axis evidence has been established.
        var baseline = new WholeLegMatcherService().Match(matchingTasks, points, matchWindows, matchOptions with { EnableGeometryRecovery = false });
        var runs = new List<Run>();
        foreach (var leg in baseline.Legs.Where(l => l.SurveyTaskStableId is null))
        {
            var stream = leg.Samples.Select(s => new SurveyTrackPoint(s.TimestampUtc, new(s.Longitude, s.Latitude), s.SourceFile, s.SourceRecord)
                { AltitudeM = s.AltitudeM }).ToList();
            // Outside a selected mission remains unresolved, not silently promoted into a measurement.
            if (!windows.Any(w => w.MissionExecutionId == leg.MissionExecutionId))
            { runs.Add(new(leg.MissionExecutionId, stream, 0, 0, false)); continue; }
            foreach (var continuous in WholeLegMatcherService.Split(stream, matchOptions))
                runs.AddRange(RecoverFragments(SplitRuns(leg.MissionExecutionId, continuous, options, matchOptions).ToList(), options)
                    .Select(r => r with { CompetingTaskIds = leg.CandidateTaskStableIds }));
        }
        runs = ConsolidateStationaryRuns(runs, matchOptions);
        var recoveredLegs = new List<FlightLeg>();
        var recoveredPasses = new List<SurveyLinePass>();
        if (authoritativeReference)
        {
            var remaining = new List<Run>();
            foreach (var run in runs)
            {
                // Retrying a refined body uses the same competition and manual
                // window scope as the first pass. Never retry an ambiguous
                // interval on its own and thereby erase a neighbouring rival.
                if (!run.Straight || run.CompetingTaskIds.Count > 0)
                { remaining.Add(run); continue; }
                var scopedWindows = matchWindows.Where(w => w.MissionExecutionId == run.Mission).ToArray();
                if (scopedWindows.Length == 0) { remaining.Add(run); continue; }
                var retry = new WholeLegMatcherService().Match(matchingTasks, run.Points, scopedWindows, matchOptions with { EnableGeometryRecovery = false });
                if (retry.Passes.Count == 0)
                {
                    remaining.Add(run with { CompetingTaskIds = retry.Legs.SelectMany(l => l.CandidateTaskStableIds).Distinct().ToArray() });
                    continue;
                }
                var retryPasses = retry.Passes.ToDictionary(p => p.FlightLegId);
                foreach (var part in retry.Legs)
                {
                    if (retryPasses.TryGetValue(part.FlightLegId, out var pass))
                    {
                        string id = part.MissionExecutionId + "/reference-" + TrackSegmentationReferenceService.Hash(
                            $"{part.Samples[0].SourceFile}/{part.Samples[0].SourceRecord}/{part.Samples[^1].SourceRecord}")[..16];
                        recoveredLegs.Add(part with { FlightLegId = id });
                        recoveredPasses.Add(pass with { FlightLegId = id, PassId = id + "/pass" });
                    }
                    else
                    {
                        var residual = part.Samples.Select(s => new SurveyTrackPoint(s.TimestampUtc, new(s.Longitude, s.Latitude), s.SourceFile, s.SourceRecord)
                            { AltitudeM = s.AltitudeM }).ToList();
                        remaining.AddRange(SplitRuns(part.MissionExecutionId, residual, options, matchOptions)
                            .Select(r => r with { CompetingTaskIds = part.CandidateTaskStableIds }));
                    }
                }
            }
            runs = ConsolidateStationaryRuns(remaining, matchOptions);
        }
        double? inferredAxis = input.AxisDegrees ?? TrackSegmentationReferenceService.DominantAxis(
            runs.Where(r => r.Straight).Select(r => (r.Heading, r.Length))
                .Concat(known.Select(t => (SurveyDirection.Bearing(t.Start, t.End) ?? 0, t.LengthM))));
        var groups = new List<List<Run>>();
        foreach (Run run in runs.Where(r => r.Straight && r.CompetingTaskIds.Count == 0).OrderByDescending(r => r.Length).ThenBy(r => r.Points[0].TimestampUtc))
        {
            // A longest-run anchor prevents single-link chains from joining neighbouring 20 m lines.
            var group = groups.FirstOrDefault(g => SameLine(g[0], run, options));
            if (group is null) groups.Add([run]); else group.Add(run);
        }
        var tasks = known.ToList();
        var taskByRun = new Dictionary<Run, PlannedSurveyLineTask>();
        var supported = new HashSet<Run>();
        var inferred = new List<(List<Run> Group, PlannedSurveyLineTask Task)>();
        // When an operator supplies the actual mission KML, its survey lines
        // are the identity authority. Keep unmatched straight tracks visible
        // as review evidence, but do not invent a second line namespace from
        // the same flight. Pure actual-track mode (and CSV-assisted mode)
        // retains the existing inferred-identity fallback.
        if (!authoritativeReference)
        foreach (var group in groups)
        {
            Run anchor = group[0];
            if (input.AxisDegrees is { } requestedAxis && TrackSegmentationReferenceService.AxisDifference(anchor.Heading, requestedAxis) > options.AxisToleranceDegrees)
                continue;
            var a = anchor.Points[0].Coordinate; var b = anchor.Points[^1].Coordinate;
            if ((SurveyDirection.Bearing(a, b) ?? 0) >= 180) (a, b) = (b, a);
            double bearing = SurveyDirection.Bearing(a, b) ?? 0;
            double radians = bearing * Math.PI / 180, ux = Math.Sin(radians), uy = Math.Cos(radians);
            var along = group.SelectMany(r => r.Points).Select(p =>
            { var (x, y) = EndpointLineRecognizer.Delta(a, p.Coordinate); return x * ux + y * uy; }).ToArray();
            var start = Offset(a, ux * along.Min(), uy * along.Min());
            var end = Offset(a, ux * along.Max(), uy * along.Max());
            string id = "actual-" + TrackSegmentationReferenceService.Hash($"{anchor.Points[0].SourceFile}/{anchor.Points[0].SourceRecord}/{anchor.Points[^1].SourceRecord}")[..20];
            inferred.Add((group, new(0, 0, id, "实测推断", "", start, end, TrackSegmentationReferenceService.Distance(start, end))
                { SurveyTaskStableId = id, SurveyLineStableId = id, SegmentId = id, GeometryOrigin = "ACTUAL_TRACK" }));
        }
        var origin = inferred.FirstOrDefault().Task?.Start ?? new GeoCoordinate(0, 0);
        double axisRadians = (inferredAxis ?? 0) * Math.PI / 180;
        double Across(GeoCoordinate p)
        { var (x, y) = EndpointLineRecognizer.Delta(origin, p); return x * Math.Cos(axisRadians) - y * Math.Sin(axisRadians); }
        int order = tasks.Count == 0 ? 0 : tasks.Max(t => t.Order);
        int number = 0;
        foreach (var entry in inferred.OrderBy(e => Across(e.Task.Start)).ThenBy(e => e.Task.SurveyTaskStableId, StringComparer.Ordinal))
        {
            var usedNames = tasks.Select(t => t.LineName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            string name;
            do { name = $"r{++number}"; } while (usedNames.Contains(name));
            var task = entry.Task with { Order = ++order, LineName = name };
            tasks.Add(task);
            bool aligned = inferredAxis.HasValue && TrackSegmentationReferenceService.AxisDifference(entry.Group[0].Heading, inferredAxis.Value) <= options.AxisToleranceDegrees;
            double Separation(PlannedSurveyLineTask other)
            { var (x, y) = EndpointLineRecognizer.Delta(task.Start, other.Start); double radians = entry.Group[0].Heading * Math.PI / 180; return Math.Abs(x * Math.Cos(radians) - y * Math.Sin(radians)); }
            int neighbours = inferred.Count(other => other.Task.SurveyTaskStableId != task.SurveyTaskStableId &&
                TrackSegmentationReferenceService.AxisDifference(other.Group[0].Heading, entry.Group[0].Heading) <= 5 &&
                Separation(other.Task) is >= 12 and <= 500 && Overlap(task, other.Task) > .2 &&
                SpacingCompatible(Separation(other.Task), options.ExpectedLineSpacingM));
            int spacingNeighbours = options.ExpectedLineSpacingM > 0 ? inferred.Count(other =>
                other.Task.SurveyTaskStableId != task.SurveyTaskStableId &&
                TrackSegmentationReferenceService.AxisDifference(other.Group[0].Heading, entry.Group[0].Heading) <= 5 &&
                Overlap(task, other.Task) > .2 && SpacingCompatible(Separation(other.Task), options.ExpectedLineSpacingM)) : 0;
            foreach (Run run in entry.Group)
            {
                taskByRun[run] = task;
                // One corroborating line at a plausible spacing can support an edge line;
                // without spacing, preserve the legacy two-neighbour rule.
                if ((aligned || input.AxisDegrees is null) && (neighbours >= 2 || options.ExpectedLineSpacingM > 0 && spacingNeighbours >= 1)) supported.Add(run);
            }
        }
        var legs = baseline.Legs.Where(l => l.SurveyTaskStableId is not null).Concat(recoveredLegs).ToList();
        var passes = baseline.Passes.Concat(recoveredPasses).ToList();
        foreach (Run run in runs.Where(r => r.Points.Count > 0))
        {
            string id = run.Mission + "/actual-" + TrackSegmentationReferenceService.Hash($"{run.Points[0].SourceFile}/{run.Points[0].SourceRecord}/{run.Points[^1].SourceRecord}")[..16];
            taskByRun.TryGetValue(run, out var task);
            bool connectorEvidence = input.ReferenceGeometry.Any(g => g.Kind == "CONNECTOR" &&
                TrackSegmentationReferenceService.AxisDifference(SurveyDirection.Bearing(g.Start, g.End) ?? 0, run.Heading) <= 12 &&
                (!authoritativeReference ? DistanceToSegment(run.Points[run.Points.Count / 2].Coordinate, g.Start, g.End) < 8 :
                 run.Points.Where((_, i) => i % Math.Max(1, run.Points.Count / 50) == 0)
                    .Count(p => DistanceToSegment(p.Coordinate, g.Start, g.End) < matchOptions.CorridorM) >=
                 run.Points.Where((_, i) => i % Math.Max(1, run.Points.Count / 50) == 0).Count() * .8));
            bool axisRejected = run.Straight && input.AxisDegrees is { } requested &&
                TrackSegmentationReferenceService.AxisDifference(run.Heading, requested) > options.AxisToleranceDegrees;
            string reason = run.CompetingTaskIds.Count > 0 ? "AMBIGUOUS_LINE" : axisRejected ? "EXPLICIT_AXIS_MISMATCH_REVIEW" : task is null
                ? connectorEvidence ? task is null ? "REFERENCE_CONNECTOR_REVIEW" : "REFERENCE_CONNECTOR_CONFLICT_REVIEW" : authoritativeReference ? "KML_REFERENCE_UNMATCHED_REVIEW" : "TRACK_TURN_OR_TRANSIT_RETAINED"
                : connectorEvidence ? "REFERENCE_CONNECTOR_CONFLICT_REVIEW" :
                supported.Contains(run) ? "ACTUAL_PARALLEL_GROUP;IDENTITY_NOT_QC" : "ACTUAL_ISOLATED_OR_CROSS_AXIS_REVIEW";
            if (run.BodyRefined) reason += ";SHALLOW_BODY_BOUNDARY_REFINED";
            var leg = new FlightLeg(id, run.Mission, run.Points[0].TimestampUtc, run.Points[^1].TimestampUtc,
                task is null ? axisRejected || authoritativeReference && run.Straight ? "SURVEY_CANDIDATE" : "UNRESOLVED" : "SURVEY", task?.SurveyTaskStableId, run.Points.Count, reason)
            { Samples = run.Points.Select(p => new FlightLegSample(p.SourceFile, p.SourceRecord, p.TimestampUtc, p.Coordinate.Longitude, p.Coordinate.Latitude, p.AltitudeM)).ToArray(),
                CandidateTaskStableIds = run.CompetingTaskIds,
                RouteRole = connectorEvidence && task is null ? "CONNECTOR" : "UNDETERMINED",
                RouteRoleEvidence = connectorEvidence ? ["实际轨迹持续贴合KML连接线，保留复核"] : [] };
            legs.Add(leg);
            if (task is null) continue;
            var pass = WholeLegMatcherService.CreateManualAssignmentPass(leg, task, matchOptions)!;
            var quality = Quality(run.Points, task, TrackSegmentationParameterProfiles.ForWindow(input, run.Mission));
            passes.Add(pass with
            {
                MatchMethod = "ACTUAL_TRACK", MatchConfidence = supported.Contains(run) && !connectorEvidence ? .85 : .5,
                ExecutionDirection = TrackSegmentationReferenceService.AxisDifference(run.Heading, SurveyDirection.Bearing(task.Start, task.End) ?? 0) <= 12 &&
                    SurveyDirection.Difference(run.Heading, SurveyDirection.Bearing(task.Start, task.End) ?? 0) < 90 ? "FORWARD" : "REVERSE",
                FlightQuality = quality,
                StartBoundary = Boundary(run.Points[0]), EndBoundary = Boundary(run.Points[^1], run.EndConfirmedUtc)
            });
        }
        if (input.AxisDegrees is { } flightAxis)
        {
            // Validate measured direction too: a compatible reference alone is not sufficient evidence.
            var rejected = legs.Where(l => l.SurveyTaskStableId is not null && !explicitTieIds.Contains(l.SurveyTaskStableId) &&
                (l.Samples.Count < 2 || SurveyDirection.Bearing(new(l.Samples[0].Longitude, l.Samples[0].Latitude),
                    new(l.Samples[^1].Longitude, l.Samples[^1].Latitude)) is not { } heading ||
                    TrackSegmentationReferenceService.AxisDifference(heading, flightAxis) > options.AxisToleranceDegrees))
                .Select(l => l.FlightLegId).ToHashSet();
            for (int i = 0; i < legs.Count; i++) if (rejected.Contains(legs[i].FlightLegId))
                legs[i] = legs[i] with { Classification = "SURVEY_CANDIDATE", CandidateTaskStableIds = [legs[i].SurveyTaskStableId!],
                    SurveyTaskStableId = null, ReviewReason = "EXPLICIT_AXIS_MISMATCH_REVIEW" };
            passes.RemoveAll(p => rejected.Contains(p.FlightLegId));
        }
        var missions = baseline.Missions.Select(m => m with
        { SurveyTaskStableIds = m.SurveyTaskStableIds.Concat(legs.Where(l => l.MissionExecutionId == m.MissionExecutionId && l.SurveyTaskStableId is not null)
            .Select(l => l.SurveyTaskStableId!)).Distinct().ToArray() }).ToArray();
        // A manual window scope still limits assignments, including inferred actual identities.
        foreach (var window in windows.Where(w => w.TaskScopeSource == "MANUAL"))
        {
            var allowed = window.SurveyTaskStableIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var excluded = legs.Where(l => l.MissionExecutionId == window.MissionExecutionId && l.SurveyTaskStableId is not null &&
                !allowed.Contains(l.SurveyTaskStableId)).Select(l => l.FlightLegId).ToHashSet();
            for (int i = 0; i < legs.Count; i++) if (excluded.Contains(legs[i].FlightLegId))
                legs[i] = legs[i] with { Classification = "SURVEY_CANDIDATE", CandidateTaskStableIds = [legs[i].SurveyTaskStableId!],
                    SurveyTaskStableId = null, ReviewReason = "WINDOW_TASK_SCOPE_EXCLUDED_REVIEW" };
            passes.RemoveAll(p => excluded.Contains(p.FlightLegId));
            missions = missions.Select(m => m.MissionExecutionId == window.MissionExecutionId
                ? m with { SurveyTaskStableIds = m.SurveyTaskStableIds.Where(allowed.Contains).ToArray() } : m).ToArray();
        }
        var warnings = baseline.Warnings.ToList();
        if (authoritativeReference)
            warnings.Add("KML_AUTHORITATIVE_REFERENCE: 未匹配轨迹保留为待复核，不自动生成新的实测测线身份；连接线和返航段不进入批量作业候选。");
        var execution = baseline with { AlgorithmVersion = AlgorithmVersion + "/" + WholeLegMatcherService.AlgorithmVersion, Missions = missions,
            Legs = legs.OrderBy(l => l.StartUtc).ThenBy(l => l.FlightLegId).ToArray(), Passes = passes.OrderBy(p => p.EndUtc).ToArray(),
            Warnings = warnings.Concat(["实测推断边界与测线身份需图形复核；无设计参考时不代表已验证设计偏航或覆盖完整性。"]).ToArray() };
        execution = WholeTrackGeometryRecoveryService.Apply(execution, matchingTasks, matchWindows, matchOptions);
        return new(tasks, LocalHitCandidateService.Annotate(execution, tasks, windows, input), inferredAxis);
    }

    private static IEnumerable<Run> SplitRuns(string mission, List<SurveyTrackPoint> points, TrackRecognitionOptions options, WholeLegMatchOptions matchOptions)
    {
        if (points.Count == 0) yield break;
        var headings = new double?[points.Count];
        var motion = new int[points.Count];
        var observations = TrackMotionService.Measure(points).ToDictionary(o => (o.Source, o.Time.ToUnixTimeSeconds()));
        for (int i = 0, right = 0; i < points.Count; i++)
        {
            right = Math.Max(right, i);
            while (right + 1 < points.Count && (points[right + 1].TimestampUtc - points[i].TimestampUtc).TotalSeconds <= options.DirectionWindowSeconds) right++;
            int last = right == i && i + 1 < points.Count && (points[i + 1].TimestampUtc - points[i].TimestampUtc).TotalSeconds <= matchOptions.MaximumTimeGapSeconds ? i + 1 : right;
            headings[i] = SurveyDirection.Bearing(points[i].Coordinate, points[last].Coordinate);
            if (observations.TryGetValue((points[i].SourceFile, points[i].TimestampUtc.ToUnixTimeSeconds()), out var observed))
            {
                double speed = observed.SpeedMps ?? 0;
                double climb = Math.Abs(observed.ClimbMps ?? 0);
                motion[i] = climb > matchOptions.VerticalSpeedReviewMps && speed < matchOptions.VerticalMotionHorizontalSpeedMps ? 2 : speed < 1 ? 1 : 0;
                // Direction of metre-scale drift is not a flight turn. Use
                // robust second-based motion to gate the original cut geometry.
                if (observed.SpeedMps is < 1.5 || observed.SpeedMps.HasValue && motion[i] != 0) headings[i] = null;
                if (!observed.SpeedMps.HasValue) motion[i] = 0;
            }
            else if (i > 0) motion[i] = motion[i - 1];
        }
        int start = 0, turn = -1;
        double? basis = null;
        int state = motion[0], stateTurn = -1;
        var spans = new List<(int Start, int End, int Confirmed)>();
        for (int i = 0; i < points.Count; i++)
        {
            // A brief low-speed observation invalidates turn persistence, not
            // the last reliable heading. Sustained motion-state changes below
            // provide the actual stationary boundary.
            if (headings[i] is null) turn = -1;
            basis ??= headings[i];
            if (motion[i] != state)
            {
                if (stateTurn < 0) stateTurn = i;
                if ((points[i].TimestampUtc - points[stateTurn].TimestampUtc).TotalSeconds >= 2)
                {
                    if (stateTurn > start) spans.Add((start, stateTurn - 1, i));
                    start = stateTurn; basis = headings[i]; state = motion[i]; stateTurn = -1; turn = -1;
                    continue;
                }
            }
            else stateTurn = -1;
            if (headings[i] is { } h && basis is { } b && SurveyDirection.Difference(h, b) > options.StraightHeadingToleranceDegrees)
            {
                if (turn < 0) turn = i;
                if ((points[i].TimestampUtc - points[turn].TimestampUtc).TotalSeconds >= options.TurnConfirmationSeconds)
                { if (turn > start) spans.Add((start, turn - 1, i)); start = turn; basis = h; state = motion[start]; stateTurn = -1; turn = -1; }
            }
            else turn = -1;
        }
        spans.Add((start, points.Count - 1, points.Count - 1));
        foreach (var coarse in spans)
        foreach (var span in TrackShallowTurnRefiner.Refine(points, coarse.Start, coarse.End, coarse.Confirmed, options, matchOptions))
        {
            int first = span.First, last = span.Last, confirmed = span.Confirmed;
            var chunk = points.GetRange(first, last - first + 1);
            double length = TrackSegmentationReferenceService.Distance(chunk[0].Coordinate, chunk[^1].Coordinate);
            double heading = SurveyDirection.Bearing(chunk[0].Coordinate, chunk[^1].Coordinate) ?? 0;
            int count = 0, aligned = 0;
            for (int i = first; i <= last; i++) if (headings[i] is { } h)
            { count++; if (SurveyDirection.Difference(h, heading) <= options.StraightHeadingToleranceDegrees) aligned++; }
            bool straight = length >= options.MinimumSpanM && (chunk[^1].TimestampUtc - chunk[0].TimestampUtc).TotalSeconds >= options.MinimumDurationSeconds && count > 0 && (double)aligned / count >= options.MinimumAlignedRatio &&
                Enumerable.Range(first, last - first + 1).Count(i => motion[i] != 0) <= chunk.Count / 2;
            yield return new(mission, chunk, heading, length, straight, points[confirmed].TimestampUtc, span.Refined);
        }
    }

    private static List<Run> ConsolidateStationaryRuns(List<Run> runs, WholeLegMatchOptions options)
    {
        var result = new List<Run>();
        foreach (var group in runs.GroupBy(r => (r.Mission, Source: r.Points[0].SourceFile)))
        {
            var ordered = group.OrderBy(r => r.Points[0].TimestampUtc).ThenBy(r => r.Points[0].SourceRecord).ToArray();
            for (int i = 0; i < ordered.Length; i++)
            {
                var current = ordered[i];
                if (current.Straight || current.CompetingTaskIds.Count > 0) { result.Add(current); continue; }
                var origin = current.Points[0].Coordinate;
                bool Within(Run r) => !r.Straight && r.CompetingTaskIds.Count == 0 && r.Points.All(p =>
                    TrackSegmentationReferenceService.Distance(origin, p.Coordinate) <= options.TerminalStationaryRadiusM) &&
                    r.Points.Zip(r.Points.Skip(1)).All(p => (p.Second.TimestampUtc - p.First.TimestampUtc).TotalSeconds is >= 0 and <= 1);
                if (!Within(current)) { result.Add(current); continue; }
                int last = i;
                while (last + 1 < ordered.Length && Within(ordered[last + 1]) &&
                    (ordered[last + 1].Points[0].TimestampUtc - ordered[last].Points[^1].TimestampUtc).TotalSeconds is >= 0 and <= 1)
                    last++;
                if (last > i && (ordered[last].Points[^1].TimestampUtc - current.Points[0].TimestampUtc).TotalSeconds >= options.TerminalStationarySeconds)
                {
                    var merged = ordered.Skip(i).Take(last - i + 1).SelectMany(r => r.Points).ToList();
                    result.Add(current with { Points = merged, Length = TrackSegmentationReferenceService.Distance(origin, merged[^1].Coordinate),
                        Heading = 0, EndConfirmedUtc = ordered[last].EndConfirmedUtc, BodyRefined = false });
                    i = last;
                }
                else result.Add(current);
            }
        }
        return result;
    }

    /// <summary>Recover short straight fragments only inside one already continuous stream.
    /// Directed headings, whole-body corridor and stationary-motion checks prevent stitching turns/adjacent lines.</summary>
    private static IEnumerable<Run> RecoverFragments(List<Run> runs, TrackRecognitionOptions options)
    {
        for (int i = 0; i < runs.Count; i++)
        {
            Run current = runs[i];
            while (i + 1 < runs.Count)
            {
                Run next = runs[i + 1];
                // Preserve every independently established work leg and genuine reversal.
                if (current.Straight || next.Straight || current.Length < 1 || next.Length < 1 ||
                    SurveyDirection.Difference(current.Heading, next.Heading) > options.StraightHeadingToleranceDegrees) break;
                var merged = current.Points.Concat(next.Points).ToList();
                var a = merged[0].Coordinate; var b = merged[^1].Coordinate;
                double length = TrackSegmentationReferenceService.Distance(a, b);
                double heading = SurveyDirection.Bearing(a, b) ?? 0;
                if (length < 1 || merged.Any(p => DistanceToLine(p.Coordinate, a, b) > 8)) break;
                var motion = TrackMotionService.Measure(merged);
                // A real pause or stationary climb is a boundary, even when position and heading agree.
                if (motion.Any(o => o.SpeedMps is < 1 || o.SpeedMps is < 4 && o.ClimbMps is { } c && Math.Abs(c) > 2)) break;
                var headings = motion.Where(o => o.Bearing.HasValue && o.SpeedMps is >= 1).ToArray();
                if (headings.Length < 2 || headings.Count(o => SurveyDirection.Difference(o.Bearing!.Value, heading) <=
                    options.StraightHeadingToleranceDegrees) < headings.Length * options.MinimumAlignedRatio) break;
                bool straight = length >= options.MinimumSpanM &&
                    (merged[^1].TimestampUtc - merged[0].TimestampUtc).TotalSeconds >= options.MinimumDurationSeconds;
                current = new(current.Mission, merged, heading, length, straight, next.EndConfirmedUtc, current.BodyRefined || next.BodyRefined);
                i++;
            }
            yield return current;
        }
    }

    private static bool SameLine(Run anchor, Run other, TrackRecognitionOptions options)
    {
        if (TrackSegmentationReferenceService.AxisDifference(anchor.Heading, other.Heading) > options.RepeatAxisToleranceDegrees) return false;
        double repeatTolerance = options.ExpectedLineSpacingM > 0
            ? Math.Min(options.RepeatCrossTrackToleranceM, options.ExpectedLineSpacingM * .35)
            : options.RepeatCrossTrackToleranceM;
        return DistanceToLine(other.Points[other.Points.Count / 2].Coordinate, anchor.Points[0].Coordinate, anchor.Points[^1].Coordinate) <= repeatTolerance &&
            Overlap(new(0, 0, "", "", "", anchor.Points[0].Coordinate, anchor.Points[^1].Coordinate, anchor.Length),
                new(0, 0, "", "", "", other.Points[0].Coordinate, other.Points[^1].Coordinate, other.Length)) > .2;
    }
    private static bool SpacingCompatible(double separation, double spacing)
    {
        if (spacing <= 0) return true;
        double multiple = Math.Max(1, Math.Round(separation / spacing));
        return Math.Abs(separation - multiple * spacing) <= Math.Max(3, spacing * .25);
    }

    private static double Overlap(PlannedSurveyLineTask a, PlannedSurveyLineTask b)
    {
        var (dx, dy) = EndpointLineRecognizer.Delta(a.Start, a.End); double length = Math.Sqrt(dx * dx + dy * dy);
        double Along(GeoCoordinate p) { var (x, y) = EndpointLineRecognizer.Delta(a.Start, p); return (x * dx + y * dy) / length; }
        double u = Along(b.Start), v = Along(b.End);
        return Math.Max(0, Math.Min(length, Math.Max(u, v)) - Math.Max(0, Math.Min(u, v))) / Math.Max(1, Math.Min(length, Math.Abs(v - u)));
    }
    private static double DistanceToLine(GeoCoordinate p, GeoCoordinate a, GeoCoordinate b)
    { var (x, y) = EndpointLineRecognizer.Delta(a, p); var (dx, dy) = EndpointLineRecognizer.Delta(a, b); return Math.Abs(x * dy - y * dx) / Math.Max(.01, Math.Sqrt(dx * dx + dy * dy)); }
    private static double DistanceToSegment(GeoCoordinate p, GeoCoordinate a, GeoCoordinate b)
    {
        var (x, y) = EndpointLineRecognizer.Delta(a, p); var (dx, dy) = EndpointLineRecognizer.Delta(a, b);
        double t = Math.Clamp((x * dx + y * dy) / Math.Max(.01, dx * dx + dy * dy), 0, 1);
        return Math.Sqrt(Math.Pow(x - t * dx, 2) + Math.Pow(y - t * dy, 2));
    }
    private static GeoCoordinate Offset(GeoCoordinate origin, double east, double north)
    {
        double latitude = origin.Latitude + north / 111319.49;
        return new(origin.Longitude + east / (111319.49 * Math.Cos((origin.Latitude + latitude) * Math.PI / 360)), latitude);
    }
    private static ExecutionBoundaryEvidence Boundary(SurveyTrackPoint p, DateTimeOffset? confirmed = null) => new("TRACK_TURN_BACKTRACK", p.TimestampUtc, confirmed ?? p.TimestampUtc, p.SourceFile, p.SourceRecord);
    private static FlightQualityEvidence Quality(List<SurveyTrackPoint> points, PlannedSurveyLineTask task, WholeLegMatchOptions options)
    {
        var cross = points.Select(p => DistanceToLine(p.Coordinate, task.Start, task.End)).ToArray();
        var observations = TrackMotionService.Measure(points);
        int moving = observations.Count(o => o.SpeedMps.HasValue);
        int outside = observations.Count(o => o.SpeedMps is { } speed &&
            (speed < options.MinimumFlightSpeedMps || speed > options.MaximumFlightSpeedMps));
        return new((double)cross.Count(x => x <= options.CoreM) / cross.Length, (double)cross.Count(x => x <= options.CorridorM) / cross.Length,
            moving == 0 ? 0 : (double)outside / moving, cross.Count(x => x > options.CorridorM), points.Any(p => p.AltitudeM.HasValue),
            ["ACTUAL_REFERENCE_ONLY: 偏航为相对实测拟合线，无法替代设计航线偏航质检。"]);
    }
}
