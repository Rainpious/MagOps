using MagOps.Core.Domain;

namespace MagOps.Infrastructure.Services;

/// <summary>Offers partial/reverse-hit identities for review without changing passes, boundaries or samples.</summary>
public static class LocalHitCandidateService
{
    public const string Version = "local-hit-review-candidates-1";
    public static object RuleSnapshot => new
    {
        Version, MinimumContinuousSeconds = 2, MaximumCandidates = 3,
        SeedMethods = "SINGLE_ENDPOINT,BODY_TRACK; or REVERSE; or coverage<0.95; excluding USER_ASSIGNED",
        MinimumContinuousTravelM = "min(20,MinimumBodySpanM)",
        MaximumEvidenceGap = "MaximumEvidenceGapSeconds", MinimumMotionSpeedMps = 1,
        DirectionTolerance = "AxisToleranceDegrees in actual mode; HeadingToleranceDegrees in design mode",
        Distance = "CandidateSearchM, bounded by finite reference extent",
        ActualFitExtensionM = "DepartureTravelM", Authority = "REVIEW_ONLY"
    };

    public static SurveyExecutionResult Annotate(SurveyExecutionResult execution,
        IReadOnlyList<PlannedSurveyLineTask> tasks, IReadOnlyList<MissionTimeWindow> windows,
        TrackSegmentationInput? input = null)
    {
        var options = input?.MatchOptions ?? new();
        var trackOptions = input?.RecognitionOptions ?? new();
        var tasksById = tasks.GroupBy(t => t.SurveyTaskStableId, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single(), StringComparer.OrdinalIgnoreCase);
        // Only weak/partial evidence is useful for filling an unmatched leg.
        // A complete, reliable actual-track pass is already a resolved identity;
        // using every ACTUAL_TRACK pass here would turn nearby transit into a
        // large and misleading candidate list.
        var seeds = execution.Passes.Where(p => p.MatchMethod != "USER_ASSIGNED" &&
                (p.ExecutionDirection == "REVERSE" || p.PlannedCoverage < .95 ||
                 p.MatchMethod is "SINGLE_ENDPOINT" or "BODY_TRACK"))
            .GroupBy(p => p.SurveyTaskStableId, StringComparer.OrdinalIgnoreCase)
            .Where(g => tasksById.ContainsKey(g.Key)).ToArray();
        var windowsById = windows.ToDictionary(w => w.MissionExecutionId, StringComparer.OrdinalIgnoreCase);
        var legs = execution.Legs.Select(leg =>
        {
            // Outside a flight window, explicit connector identity and assigned
            // legs are not promoted into a second-pass search.
            if (leg.SurveyTaskStableId is not null || leg.Samples.Count < 2 ||
                leg.RouteRole is "CONNECTOR" or "TRANSIT" ||
                leg.ReviewReason.Contains("REFERENCE_CONNECTOR", StringComparison.Ordinal) ||
                !windowsById.TryGetValue(leg.MissionExecutionId, out var window)) return leg;
            var motion = TrackMotionService.Measure(leg.Samples.Select(p => new SurveyTrackPoint(
                p.TimestampUtc, new(p.Longitude, p.Latitude), p.SourceFile, p.SourceRecord) { AltitudeM = p.AltitudeM }));
            if (motion.Count < 2) return leg;
            var proposals = new List<LocalHitCandidateEvidence>();
            foreach (var seed in seeds)
            {
                if (window.TaskScopeSource == "MANUAL" &&
                    !window.SurveyTaskStableIds.Contains(seed.Key, StringComparer.OrdinalIgnoreCase)) continue;
                var task = tasksById[seed.Key];
                if (SurveyDirection.Bearing(task.Start, task.End) is not { } heading) continue;
                bool isTie = input?.ReferenceGeometry.Any(g => g.Id == seed.Key && g.Kind == "TIE") == true;
                if (!isTie && input?.AxisDegrees is { } axis &&
                    TrackSegmentationReferenceService.AxisDifference(axis, heading) > trackOptions.AxisToleranceDegrees) continue;
                var (dx, dy) = EndpointLineRecognizer.Delta(task.Start, task.End);
                double length = Math.Sqrt(dx * dx + dy * dy);
                if (length < 1) continue;
                bool actualFit = task.GeometryOrigin == "ACTUAL_TRACK";
                double margin = actualFit ? options.DepartureTravelM : options.EndpointSearchM;
                double directionLimit = input?.Enabled == true ? trackOptions.AxisToleranceDegrees : options.HeadingToleranceDegrees;
                // Independent references keep their endpoints. A fitted actual
                // line may propose a bounded extension, never a new 100% claim.
                double span = 0, seconds = 0, bestSpan = 0, bestSeconds = 0;
                double? firstAlong = null, priorAlong = null;
                DateTimeOffset? firstTime = null, priorTime = null;
                string? priorSource = null;
                int direction = 0, eligible = 0;
                bool extension = false;
                var offsets = new List<double>(); var errors = new List<double>();
                foreach (var m in motion)
                {
                    var (x, y) = EndpointLineRecognizer.Delta(task.Start, m.Coordinate);
                    double along = (x * dx + y * dy) / length;
                    double cross = Math.Abs(x * dy - y * dx) / length;
                    double error = m.Bearing is { } b ? TrackSegmentationReferenceService.AxisDifference(b, heading) : 180;
                    bool supported = cross <= options.CandidateSearchM && along >= -margin && along <= length + margin &&
                        error <= directionLimit && m.SpeedMps is >= 1;
                    int currentDirection = m.Bearing is { } bearing && SurveyDirection.Difference(bearing, heading) < 90 ? 1 : -1;
                    bool continuous = supported && priorTime is { } time &&
                        string.Equals(priorSource, m.Source, StringComparison.OrdinalIgnoreCase) &&
                        (m.Time - time).TotalSeconds is > 0 && (m.Time - time).TotalSeconds <= options.MaximumEvidenceGapSeconds &&
                        currentDirection == direction && priorAlong is { } lastAlong && (along - lastAlong) * direction >= -.5;
                    if (!supported) { firstAlong = null; firstTime = null; priorTime = null; continue; }
                    eligible++; offsets.Add(cross); errors.Add(error);
                    extension |= actualFit && (along < 0 || along > length);
                    if (!continuous) { firstAlong = along; firstTime = m.Time; }
                    direction = currentDirection;
                    span = Math.Abs(along - firstAlong!.Value);
                    seconds = (m.Time - firstTime!.Value).TotalSeconds;
                    if (span > bestSpan && seconds >= 2) { bestSpan = span; bestSeconds = seconds; }
                    priorTime = m.Time; priorSource = m.Source; priorAlong = along;
                }
                if (bestSpan < Math.Min(20, options.MinimumBodySpanM) || bestSeconds < 2) continue;
                offsets.Sort(); errors.Sort();
                proposals.Add(new(seed.Key, task.GeometryOrigin, seed.Select(p => p.FlightLegId).Distinct().ToArray(),
                    seed.Any(p => p.ExecutionDirection == "REVERSE"), (double)eligible / motion.Count,
                    bestSpan, bestSeconds, offsets[offsets.Count / 2], errors[errors.Count / 2], extension));
            }
            var ranked = proposals.OrderByDescending(p => p.SupportedRatio)
                .ThenBy(p => p.MedianCrossTrackM).ThenByDescending(p => p.ContinuousSpanM)
                .ThenBy(p => p.TaskId, StringComparer.Ordinal).Take(3).ToArray();
            if (ranked.Length == 0) return leg;
            return leg with
            {
                LocalHitCandidates = ranked,
                CandidateTaskStableIds = ranked.Select(p => p.TaskId).Concat(leg.CandidateTaskStableIds)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                ContextEvidence = leg.ContextEvidence.Concat(new[]
                { "局部/反向命中提供复核候选；只表示附近存在测线身份依据，不自动归属、纳入或通过质量检查。" }).Distinct().ToArray()
            };
        }).ToArray();
        return execution with { Legs = legs };
    }
}
