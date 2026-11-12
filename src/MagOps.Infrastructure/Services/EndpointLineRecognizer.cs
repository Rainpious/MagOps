using MagOps.Core.Domain;

namespace MagOps.Infrastructure.Services;

/// <summary>
/// Searches complete temporal traversals of each authorized task. Point distance
/// supplies evidence, never a nearest-line point identity or a production decision.
/// </summary>
public static class EndpointLineRecognizer
{
    internal sealed record Candidate(int Start, int End, PlannedSurveyLineTask Task,
        int Direction, string Method, string StartKind, string EndKind, int Confirmed,
        double Confidence, FlightQualityEvidence Quality);

    private sealed record Motion(double X, double Y, double Speed, double? Climb,
        bool DirectionReliable);

    internal static List<Candidate> Find(List<SurveyTrackPoint> points,
        IReadOnlyList<PlannedSurveyLineTask> tasks, WholeLegMatchOptions options)
    {
        if (points.Count < 3) return [];
        Motion[] motion = BuildMotion(points, options);
        var proposals = new List<Candidate>();
        foreach (PlannedSurveyLineTask task in new SurveyTaskSpatialIndex(tasks,
            Math.Max(options.CandidateSearchM, options.CorridorM)).Nearby(points))
        {
            var (dx, dy) = Delta(task.Start, task.End);
            double length = Math.Sqrt(dx * dx + dy * dy);
            if (!double.IsFinite(length) || length < 0.5 || task.LengthM <= 0) continue;
            double ux = dx / length, uy = dy / length;
            var along = new double[points.Count];
            var cross = new double[points.Count];
            for (int i = 0; i < points.Count; i++)
            {
                var (x, y) = Delta(task.Start, points[i].Coordinate);
                along[i] = x * ux + y * uy;
                cross[i] = x * uy - y * ux;
            }
            int cursor = 0;
            while (cursor < points.Count - 2)
            {
                int seed = cursor;
                while (seed < points.Count - 2 && !Seed(seed)) seed++;
                if (seed >= points.Count - 2) break;
                int sign = motion[seed].X * ux + motion[seed].Y * uy >= 0 ? 1 : -1;
                int start = seed;
                // Include the closest observed entry endpoint in the local lead-in.
                double entry = sign > 0 ? 0 : length;
                for (int i = seed - 1; i >= cursor &&
                    Seconds(points[i], points[seed]) <= options.ObservationSeconds; i--)
                {
                    if (EndpointDistance(i, entry) <= options.EndpointSearchM) start = i;
                    else if (Math.Abs(cross[i]) > options.CandidateSearchM ||
                        Angle(motion[i], sign, ux, uy) > options.DirectionBreakDegrees) break;
                }
                int end = points.Count - 1, confirmed = end;
                string endKind = "DATA_END_REVIEW";
                int turnStart = -1, departure = -1;
                int departureTravelAnchor = -1;
                double departureTravel = 0;
                for (int i = seed + 1; i < points.Count; i++)
                {
                    double stepTime = Seconds(points[i - 1], points[i]);
                    // A short gap does not itself terminate the pass, but cannot
                    // count as observed persistence for the two-second turn rule.
                    // Multiple magnetic records can share one UTC second. They
                    // preserve an episode but never add elapsed time. Only a
                    // backwards timestamp or an actual evidence gap resets it.
                    if (stepTime > options.MaximumEvidenceGapSeconds || stepTime < 0)
                    {
                        turnStart = -1;
                        departure = -1;
                        departureTravelAnchor = -1;
                        departureTravel = 0;
                    }
                    double exit = sign > 0 ? length : 0;
                    if ((along[i] - exit) * sign > options.EndpointSearchM)
                    {
                        end = i - 1; confirmed = i; endKind = "TASK_ENDPOINT";
                        break;
                    }
                    double angle = Angle(motion[i], sign, ux, uy);
                    if (angle > options.DirectionBreakDegrees)
                    {
                        if (turnStart < 0) turnStart = i;
                        if (Seconds(points[turnStart], points[i]) >= options.DirectionConfirmationSeconds)
                        {
                            end = RefineTurn(points, along, cross, turnStart, sign, options);
                            confirmed = i; endKind = "DIRECTION_BREAK";
                            break;
                        }
                    }
                    else turnStart = -1;

                    if (Math.Abs(cross[i]) <= options.CorridorM)
                    {
                        departure = -1; departureTravelAnchor = -1; departureTravel = 0;
                    }
                    else
                    {
                        if (departure < 0) { departure = i; departureTravelAnchor = i; departureTravel = 0; }
                        // Measure between observations with different actual
                        // timestamps. Counting only the last subsecond step at
                        // each UTC boundary undercounts real travel by the
                        // magnetic sample rate. Same-second points add no time
                        // or distance; an implausible jump adds no travel.
                        double travelTime = Seconds(points[departureTravelAnchor], points[i]);
                        if (travelTime > 0)
                        {
                            var (sx, sy) = Delta(points[departureTravelAnchor].Coordinate, points[i].Coordinate);
                            double distance = Math.Sqrt(sx * sx + sy * sy);
                            if (distance / travelTime <= 50) departureTravel += distance;
                            departureTravelAnchor = i;
                        }
                        double observed = Seconds(points[departure], points[i]);
                        bool growing = Math.Abs(cross[i]) - Math.Abs(cross[departure]) > options.CorridorM;
                        bool changedDirection = angle > options.DepartureDirectionChangeDegrees && angle < 180;
                        bool auxiliaryChange = motion[i].Speed < options.DepartureAuxiliaryMinimumSpeedMps ||
                            motion[i].Speed > options.DepartureAuxiliaryMaximumSpeedMps ||
                            motion[i].Climb is { } climb && Math.Abs(climb) > options.VerticalSpeedReviewMps;
                        if (observed >= options.ReturnSeconds && departureTravel >= options.DepartureTravelM &&
                            growing && (changedDirection || auxiliaryChange))
                        {
                            end = Math.Max(start, departure - 1); confirmed = i;
                            endKind = "DEPARTURE_CONFIRMED";
                            break;
                        }
                        // A parallel offset alone is quality debt, not a break.
                    }
                }

                start = ClosestEndpoint(start, Math.Min(end, seed + ForwardCount(seed)), entry, start);
                double target = sign > 0 ? length : 0;
                int tail = end;
                while (tail > start && Seconds(points[tail], points[end]) < options.ObservationSeconds) tail--;
                if (endKind == "TASK_ENDPOINT" || EndpointDistance(end, target) <= options.EndpointSearchM)
                    end = ClosestEndpoint(tail, Math.Min(points.Count - 1, confirmed), target, end);
                if (end < start) { cursor = seed + 1; continue; }
                bool entryHit = EndpointDistance(start, entry) <= options.EndpointSearchM;
                bool exitHit = EndpointDistance(end, target) <= options.EndpointSearchM;
                int count = end - start + 1;
                double progress = (along[end] - along[start]) * sign;
                double required = Math.Min(10, length * 0.5);
                int moving = 0, forward = 0, headings = 0, aligned = 0;
                var signedCross = new List<double>();
                for (int i = start; i <= end; i++)
                {
                    signedCross.Add(cross[i]);
                    if (motion[i].DirectionReliable)
                    {
                        headings++;
                        if (Angle(motion[i], sign, ux, uy) <= options.BodyHeadingToleranceDegrees) aligned++;
                    }
                    if (i > start && Math.Abs(along[i] - along[i - 1]) > 0.5)
                    { moving++; if ((along[i] - along[i - 1]) * sign > 0) forward++; }
                }
                // This is identity support for a newly discovered candidate, not
                // a QC rejection of an already assigned task. No P95 hard gate.
                bool paired = entryHit && exitHit;
                signedCross.Sort();
                double lateralSpread = signedCross[(int)((count - 1) * .90)] - signedCross[(int)((count - 1) * .10)];
                double minimumBody = Math.Min(options.MinimumBodySpanM, length * .20);
                // A stable whole-body relationship can establish identity even outside 8 m.
                // A shallow crossing is not a body: its direction/spread do not fit.
                bool body = progress >= minimumBody && Seconds(points[start], points[end]) >= 2 &&
                    headings >= 3 && (double)aligned / headings >= .80 &&
                    (entryHit || exitHit || lateralSpread <= options.CoreM * 2);
                bool supported = paired || body;
                double monotonicity = moving == 0 ? 0 : (double)forward / moving;
                bool accepted = count >= 3 && progress >= required && progress / length >= options.MinimumLegCoverage &&
                    supported && monotonicity >= options.MinimumMonotonicity;
                if (accepted)
                {
                    var stopped = RefineTerminalStop(points, along, cross, start, end, sign, length, options);
                    var refined = stopped is null ? RefineTerminal(points, along, cross, start, end,
                        confirmed, sign, length, options) : null;
                    if (stopped is { } stop)
                    {
                        end = stop.End;
                        confirmed = stop.Confirmed;
                        endKind = stop.Kind;
                    }
                    if (refined is { } terminal)
                    {
                        end = terminal.End;
                        confirmed = terminal.Confirmed;
                        endKind = "TERMINAL_TURN";
                        // Preserve endpoint evidence for task identity. Refining the
                        // measurement boundary must not erase a previously observed hit.
                    }
                    else if (endKind == "TASK_ENDPOINT" && !exitHit)
                    {
                        // Search tolerance is not a production extension. Without an
                        // observed endpoint, cut at the closest terminal projection.
                        int nearest = end;
                        for (int i = tail; i <= end; i++)
                            if (Math.Abs(along[i] - target) < Math.Abs(along[nearest] - target)) nearest = i;
                        end = nearest;
                        endKind = "TASK_ENDPOINT_PROJECTION";
                    }
                    FlightQualityEvidence quality = Quality(start, end, cross, motion, points, options);
                    double confidence = Math.Clamp((paired ? 0.55 : 0.25) +
                        0.15 * quality.CorePointRatio + 0.15 * quality.CorridorPointRatio +
                        0.15 * monotonicity, 0, 1);
                    proposals.Add(new(start, end, task, sign,
                        paired ? "ENDPOINT_PAIR" : entryHit || exitHit ? "SINGLE_ENDPOINT" : "BODY_TRACK",
                        entryHit ? "TASK_ENDPOINT" : start == 0 ? "DATA_START_REVIEW" : "TRACK_ENTRY",
                        endKind.StartsWith("TERMINAL_", StringComparison.Ordinal) ? endKind : exitHit ? "TASK_ENDPOINT" : endKind,
                        confirmed, confidence, quality));
                }
                // Failed proposals cannot consume a later execution. Advance to
                // the next spatial/direction episode instead of skipping the traced tail.
                if (accepted)
                    cursor = Math.Max(seed + 1, end + 1);
                else
                {
                    cursor = seed + 1;
                    double seedAngle = Angle(motion[seed], sign, ux, uy);
                    while (cursor < points.Count - 2 && Seed(cursor) &&
                        Math.Abs(Angle(motion[cursor], sign, ux, uy) - seedAngle) <= 5 &&
                        Math.Abs(cross[cursor] - cross[seed]) <= options.CoreM) cursor++;
                }
            }

            bool Seed(int i) => Math.Abs(cross[i]) <= Math.Max(options.CandidateSearchM, options.CorridorM) &&
                // A seed at either design endpoint is preferred, but an
                // interior seed is retained as a whole-body fallback for
                // interrupted or incomplete executions.
                along[i] >= -options.EndpointSearchM && along[i] <= length + options.EndpointSearchM &&
                motion[i].DirectionReliable &&
                Math.Min(Angle(motion[i], 1, ux, uy), Angle(motion[i], -1, ux, uy)) <= options.HeadingToleranceDegrees;

            double EndpointDistance(int i, double endpoint) =>
                Math.Sqrt((along[i] - endpoint) * (along[i] - endpoint) + cross[i] * cross[i]);

            int ClosestEndpoint(int from, int to, double endpoint, int fallback)
            {
                int nearest = fallback;
                double best = options.EndpointSearchM;
                for (int i = from; i <= to; i++)
                {
                    double distance = EndpointDistance(i, endpoint);
                    // Equal-distance dwell records cannot extend a task past its
                    // first observed endpoint. Keep the earliest tied observation.
                    if (distance < best - 1e-7 || distance <= best && nearest == fallback && best == options.EndpointSearchM)
                    { best = distance; nearest = i; }
                }
                return nearest;
            }

            int ForwardCount(int i)
            {
                int last = i;
                while (last + 1 < points.Count && Seconds(points[i], points[last + 1]) <= options.ObservationSeconds) last++;
                return last - i;
            }
        }
        return proposals;
    }

    private sealed record TerminalCut(int End, int Confirmed);
    internal static Candidate? Clip(Candidate candidate, List<SurveyTrackPoint> points,
        int from, int to, WholeLegMatchOptions options)
    {
        if (from == candidate.Start && to == candidate.End) return candidate;
        if (to - from < 2) return null;
        var segment = points.GetRange(from, to - from + 1);
        var (dx, dy) = Delta(candidate.Task.Start, candidate.Task.End);
        double length = Math.Sqrt(dx * dx + dy * dy);
        var cross = segment.Select(p => { var (x, y) = Delta(candidate.Task.Start, p.Coordinate); return (x * dy - y * dx) / length; }).ToArray();
        var (sx, sy) = Delta(segment[0].Coordinate, segment[^1].Coordinate);
        double progress = (sx * dx + sy * dy) / length * candidate.Direction;
        if (progress < Math.Max(Math.Min(10, length * .5), length * options.MinimumLegCoverage) ||
            Seconds(segment[0], segment[^1]) < 2) return null;
        return candidate with
        {
            Start = from, End = to,
            Method = "PARTIAL_CONFLICT_BODY",
            StartKind = from == candidate.Start ? candidate.StartKind : "CONFLICT_INTERVAL_BOUNDARY",
            EndKind = to == candidate.End ? candidate.EndKind : "CONFLICT_INTERVAL_BOUNDARY",
            Confirmed = to == candidate.End ? candidate.Confirmed : to,
            Quality = Quality(0, segment.Count - 1, cross, BuildMotion(segment, options), segment, options)
        };
    }
    private sealed record StopCut(int End, int Confirmed, string Kind);

    private static StopCut? RefineTerminalStop(List<SurveyTrackPoint> points,
        double[] along, double[] cross, int start, int end, int sign, double length, WholeLegMatchOptions options)
    {
        double terminal = sign > 0 ? length : 0;
        for (int first = start + 2; first <= end; first++)
        {
            if (Math.Sqrt(Math.Pow(along[first] - terminal, 2) + cross[first] * cross[first]) > options.EndpointSearchM) continue;
            int last = first;
            while (last + 1 < points.Count && Continuous(last, last + 1) &&
                Distance(first, last + 1) <= options.TerminalStationaryRadiusM) last++;
            if (Seconds(points[first], points[last]) < options.TerminalStationarySeconds) continue;
            // The radius can admit the last moving sample before the plateau.
            // Locate its actual observed centre, taking the first tied record.
            double centreAlong = Median(along[first..(last + 1)]), centreCross = Median(cross[first..(last + 1)]);
            int stoppedAt = first;
            double nearest = double.PositiveInfinity;
            for (int i = first; i <= last; i++)
            {
                double distance = Math.Pow(along[i] - centreAlong, 2) + Math.Pow(cross[i] - centreCross, 2);
                if (distance < nearest - 1e-9) { nearest = distance; stoppedAt = i; }
            }
            if (stoppedAt > end || Seconds(points[stoppedAt], points[last]) < options.TerminalStationarySeconds) continue;
            // It must follow actual along-line progress, not ground jitter.
            int before = first - 1;
            while (before > start && Seconds(points[before - 1], points[first]) <= options.TerminalStableSeconds) before--;
            if ((along[first] - along[before]) * sign < options.CoreM) { first = last; continue; }
            bool vertical = false;
            int confirmed = last;
            double? initialHeight = points[stoppedAt].AltitudeM;
            if (initialHeight is { } h && double.IsFinite(h))
            {
                int rising = 0, falling = 0;
                double runHeight = h;
                for (int i = stoppedAt + 1; i <= last; i++)
                {
                    if (points[i - 1].AltitudeM is not { } a || points[i].AltitudeM is not { } b ||
                        !double.IsFinite(a) || !double.IsFinite(b)) continue;
                    double dt = Seconds(points[i - 1], points[i]), dh = b - a;
                    // A delayed altitude jump is not observed physical climbing.
                    if (dt < 0 || dt > options.MaximumEvidenceGapSeconds || dt == 0 && Math.Abs(dh) > .01 ||
                        dt > 0 && Math.Abs(dh) / dt > options.TerminalSearchSpeedMps)
                    { rising = falling = 0; runHeight = b; continue; }
                    if (dt == 0) continue;
                    if (dh > .1) rising++; else if (dh < -.1) falling++;
                    if (Math.Abs(b - runHeight) >= options.TerminalVerticalChangeM && Math.Max(rising, falling) >= 3 &&
                        Math.Max(rising, falling) >= (rising + falling) * .8)
                    { vertical = true; confirmed = i; break; }
                }
            }
            // Look beyond a long frozen plateau, but never across an evidence gap.
            // Direction plus physical displacement must support a departure.
            bool departure = false;
            int look = last;
            while (look + 1 < points.Count && Continuous(look, look + 1) &&
                Seconds(points[last], points[look + 1]) <= options.ObservationSeconds) look++;
            for (int i = last + 1; i <= look; i++)
            {
                double x = (along[i] - along[last]) * sign, y = cross[i] - cross[last];
                double d = Math.Sqrt(x * x + y * y);
                if (d < options.CoreM ||
                    Seconds(points[last], points[i]) < options.DirectionConfirmationSeconds) continue;
                double angle = Math.Acos(Math.Clamp(x / d, -1, 1)) * 180 / Math.PI;
                if (angle > options.DirectionBreakDegrees || d >= options.DepartureTravelM && Math.Abs(cross[i]) > options.CorridorM &&
                    angle > options.DepartureDirectionChangeDegrees)
                { departure = true; confirmed = i; break; }
            }
            if (vertical || departure)
            {
                var heights = points.GetRange(stoppedAt, last - stoppedAt + 1).Select(p => p.AltitudeM)
                    .Where(h => h.HasValue && double.IsFinite(h.Value)).Select(h => h!.Value).Order().ToArray();
                bool frozenHeight = heights.Length >= 3 &&
                    heights[(int)((heights.Length - 1) * .9)] - heights[(int)((heights.Length - 1) * .1)] < .1;
                // Magnetic variation supports review of a plateau; it never
                // supplies the cut or claims a true climb/freeze by itself.
                var mag = points.GetRange(first, last - first + 1).Select(p => p.RawMagNt)
                    .Where(v => v.HasValue && double.IsFinite(v.Value)).Select(v => v!.Value).ToArray();
                string kind = vertical ? "TERMINAL_STATIONARY_VERTICAL" : frozenHeight ? "TERMINAL_POSITION_FREEZE_REVIEW" : "TERMINAL_STATIONARY_DEPARTURE";
                if (frozenHeight && mag.Length > 1 && mag.Max() - mag.Min() > 0)
                    kind += "_MAG_VARIATION_AUXILIARY";
                return new(stoppedAt, confirmed, kind);
            }
            first = last;
        }
        return null;

        bool Continuous(int a, int b)
        {
            double dt = Seconds(points[a], points[b]);
            double distance = Distance(a, b);
            return points[a].SourceFile == points[b].SourceFile && dt >= 0 && dt <= options.MaximumEvidenceGapSeconds &&
                (dt == 0 ? distance < .01 : distance / dt <= 50);
        }
        double Distance(int a, int b) => Math.Sqrt(Math.Pow(along[b] - along[a], 2) + Math.Pow(cross[b] - cross[a], 2));
    }
    private sealed record LocalFit(double AlongSpeed, double CrossSpeed, double Error);

    private static TerminalCut? RefineTerminal(List<SurveyTrackPoint> points,
        double[] along, double[] cross, int start, int end, int confirmed,
        int sign, double length, WholeLegMatchOptions options)
    {
        // All evidence remains inside this continuous source stream and a bounded
        // terminal neighborhood. Fit the actual incoming body, without moving data.
        double terminal = sign > 0 ? length : 0;
        double radius = Math.Min(length * .25,
            Math.Max(options.DepartureTravelM, options.TerminalSearchSpeedMps * options.ObservationSeconds));
        if (Math.Abs(along[end] - terminal) > radius) return null;
        int from = end;
        while (from > start && Seconds(points[from - 1], points[end]) <= options.ObservationSeconds) from--;
        for (int suspect = from; suspect <= end; suspect++)
        {
            if (Math.Abs(along[suspect] - terminal) > radius) continue;
            int before = suspect;
            while (before > start && Seconds(points[before - 1], points[suspect]) <= options.TerminalStableSeconds) before--;
            if (suspect - before < 3 || Seconds(points[before], points[suspect]) < options.TerminalStableSeconds * .8) continue;
            bool continuousBody = true;
            for (int i = before; i < suspect; i++)
                if (!Continuous(i, i + 1)) { continuousBody = false; break; }
            if (!continuousBody) continue;
            LocalFit incoming = Fit(before, suspect);
            if (incoming.AlongSpeed * sign <= 0 || Heading(incoming, sign, 0) > options.BodyHeadingToleranceDegrees ||
                incoming.Error > options.CoreM * options.CoreM) continue;
            int evidence = suspect;
            while (evidence + 1 < points.Count &&
                Seconds(points[suspect], points[evidence]) < options.DirectionConfirmationSeconds &&
                Continuous(evidence, evidence + 1)) evidence++;
            if (Seconds(points[suspect], points[evidence]) < options.DirectionConfirmationSeconds || evidence - suspect < 3) continue;
            if (evidence > end && Seconds(points[end], points[evidence]) > options.MaximumEvidenceGapSeconds) continue;
            LocalFit outgoing = Fit(suspect, evidence);
            double baseHeading = Math.Atan2(incoming.CrossSpeed, incoming.AlongSpeed);
            if (Heading(outgoing, 1, baseHeading) < options.TerminalTurnDegrees) continue;
            double duration = Seconds(points[suspect], points[evidence]);
            double lateral = cross[evidence] - cross[suspect] - incoming.CrossSpeed * duration;
            if (Math.Abs(lateral) < options.CoreM) continue;
            // A short excursion returning to the same stable body is quality
            // evidence, not an exit. Do not cross gaps to manufacture recovery.
            int observationEnd = evidence;
            while (observationEnd + 1 < points.Count &&
                Seconds(points[suspect], points[observationEnd + 1]) <= options.ObservationSeconds &&
                Continuous(observationEnd, observationEnd + 1)) observationEnd++;
            bool recovered = false;
            for (int i = evidence + 1; i <= observationEnd; i++)
            {
                double residual = cross[i] - cross[suspect] - incoming.CrossSpeed * Seconds(points[suspect], points[i]);
                if (Math.Abs(residual) <= options.CoreM &&
                    Heading(Fit(i, observationEnd), 1, baseHeading) <= options.BodyHeadingToleranceDegrees)
                { recovered = true; break; }
            }
            // The incoming fit may already contain the first disturbed point.
            // Its robust lateral baseline protects a brief excursion from an
            // extrapolated slope that would incorrectly hide the return.
            if (!recovered)
            {
                double baseline = Median(cross[before..(suspect + 1)]);
                recovered = Enumerable.Range(evidence + 1, Math.Max(0, observationEnd - evidence))
                    .Any(i => observationEnd - i >= 2 &&
                              Math.Abs(cross[i] - baseline) <= options.CoreM &&
                              Math.Abs(cross[i + 1] - baseline) <= options.CoreM &&
                              Math.Abs(cross[i + 2] - baseline) <= options.CoreM &&
                              Heading(Fit(i, observationEnd), 1, baseHeading) <= options.BodyHeadingToleranceDegrees);
            }
            if (recovered) continue;

            // Locate the change in raw points, instead of cutting at the forward
            // heading window or at the later confirmation sample. Two time fits
            // distinguish a real bend from speed changes along a straight line.
            int fitStart = before, fitEnd = evidence;
            double bestError = double.PositiveInfinity;
            int best = -1;
            for (int cut = before + 2; cut < evidence - 1 && cut <= end; cut++)
            {
                if (Seconds(points[cut], points[suspect]) > 1 || Seconds(points[suspect], points[cut]) > 1) continue;
                LocalFit first = Fit(fitStart, cut), second = Fit(cut, fitEnd);
                if (first.AlongSpeed * sign <= 0 || Heading(first, sign, 0) > options.BodyHeadingToleranceDegrees ||
                    Heading(second, 1, Math.Atan2(first.CrossSpeed, first.AlongSpeed)) < options.TerminalTurnDegrees) continue;
                double error = first.Error * (cut - fitStart + 1) + second.Error * (fitEnd - cut + 1);
                if (error < bestError) { bestError = error; best = cut; }
            }
            if (best >= start + 2 && best < end &&
                (along[best] - along[start]) * sign >= Math.Max(Math.Min(10, length * .5), length * options.MinimumLegCoverage))
                return new(best, Math.Max(confirmed, evidence));
        }
        return null;

        bool Continuous(int a, int b)
        {
            double dt = Seconds(points[a], points[b]);
            double dx = along[b] - along[a], dy = cross[b] - cross[a];
            return dt > 0 && dt <= options.MaximumEvidenceGapSeconds && Math.Sqrt(dx * dx + dy * dy) / dt <= 50;
        }

        LocalFit Fit(int a, int b)
        {
            int n = b - a + 1;
            double meanT = 0, meanX = 0, meanY = 0;
            for (int i = a; i <= b; i++)
            { meanT += Seconds(points[a], points[i]); meanX += along[i]; meanY += cross[i]; }
            meanT /= n; meanX /= n; meanY /= n;
            double variance = 0, covX = 0, covY = 0;
            for (int i = a; i <= b; i++)
            {
                double t = Seconds(points[a], points[i]) - meanT;
                variance += t * t; covX += t * (along[i] - meanX); covY += t * (cross[i] - meanY);
            }
            double vx = variance > 0 ? covX / variance : 0, vy = variance > 0 ? covY / variance : 0;
            // Only lateral residuals determine stability; speed changes are auxiliary.
            double error = 0;
            for (int i = a; i <= b; i++)
            {
                double residual = cross[i] - meanY - vy * (Seconds(points[a], points[i]) - meanT);
                error += residual * residual;
            }
            return new(vx, vy, error / n);
        }

        static double Heading(LocalFit fit, int direction, double reference) =>
            Math.Sqrt(fit.AlongSpeed * fit.AlongSpeed + fit.CrossSpeed * fit.CrossSpeed) < .5
                ? 0 : Math.Acos(Math.Clamp((fit.AlongSpeed * Math.Cos(reference) +
                    fit.CrossSpeed * Math.Sin(reference)) * direction /
                    Math.Sqrt(fit.AlongSpeed * fit.AlongSpeed + fit.CrossSpeed * fit.CrossSpeed), -1, 1)) * 180 / Math.PI;
    }

    private static int RefineTurn(List<SurveyTrackPoint> points, double[] along,
        double[] cross, int first, int sign, WholeLegMatchOptions options)
    {
        int boundary = first;
        // Locate the first raw movement leaving the fixed execution direction,
        // within a bounded local neighborhood; do not rebase direction on the turn.
        for (int i = first; i > 0 && Seconds(points[i - 1], points[first]) <= 1; i--)
        {
            double x = (along[i] - along[i - 1]) * sign;
            double y = cross[i] - cross[i - 1];
            double distance = Math.Sqrt(x * x + y * y);
            if (distance < 0.5) continue;
            if (Math.Acos(Math.Clamp(x / distance, -1, 1)) * 180 / Math.PI > options.DirectionBreakDegrees)
                boundary = i - 1;
            else break;
        }
        return boundary;
    }

    private static FlightQualityEvidence Quality(int start, int end, double[] cross,
        Motion[] motion, List<SurveyTrackPoint> points, WholeLegMatchOptions options)
    {
        int core = 0, corridor = 0, speeds = 0, outsideSpeed = 0;
        for (int i = start; i <= end; i++)
        {
            if (Math.Abs(cross[i]) <= options.CoreM) core++;
            if (Math.Abs(cross[i]) <= options.CorridorM) corridor++;
            if (double.IsFinite(motion[i].Speed))
            {
                speeds++;
                if (SpeedOutside(motion[i].Speed, options)) outsideSpeed++;
            }
        }
        int count = end - start + 1;
        var warnings = new List<string>();
        if (core < count * 0.90) warnings.Add("CORE_DEVIATION_REVIEW");
        if (corridor < count) warnings.Add("CROSS_TRACK_REVIEW");
        double[] usableCross = Enumerable.Range(start, count)
            .Select(i => cross[i]).Where(double.IsFinite).ToArray();
        double signedMedian = Median(usableCross);
        double signConsistency = DominantSignRatio(usableCross);
        if (Math.Abs(signedMedian) > options.CorridorM &&
            signConsistency >= 0.80 &&
            usableCross.Count(x => Math.Abs(x) > options.CorridorM) >= count * 0.80)
            warnings.Add("SYSTEMATIC_LATERAL_OFFSET_REVIEW");
        if (outsideSpeed > 0) warnings.Add("FLIGHT_SPEED_REVIEW");
        if (Enumerable.Range(start, count).Any(i => motion[i].Climb is { } c && Math.Abs(c) > options.VerticalSpeedReviewMps))
            warnings.Add("VERTICAL_MOTION_REVIEW");
        return new((double)core / count, (double)corridor / count,
            speeds == 0 ? 0 : (double)outsideSpeed / speeds, count - corridor,
            Enumerable.Range(start, count).Any(i => points[i].AltitudeM.HasValue), warnings)
        {
            MedianSignedCrossTrackM = signedMedian,
            LateralSignConsistency = signConsistency
        };
    }

    private static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return 0;
        double[] ordered = values.OrderBy(x => x).ToArray();
        int middle = ordered.Length / 2;
        return ordered.Length % 2 == 0
            ? (ordered[middle - 1] + ordered[middle]) / 2d
            : ordered[middle];
    }

    private static double DominantSignRatio(IReadOnlyList<double> values)
    {
        int positive = values.Count(x => x > 0.5d);
        int negative = values.Count(x => x < -0.5d);
        int usable = positive + negative;
        return usable == 0 ? 0d : (double)Math.Max(positive, negative) / usable;
    }

    private static Motion[] BuildMotion(List<SurveyTrackPoint> points, WholeLegMatchOptions options)
    {
        var result = new Motion[points.Count];
        int right = 0;
        for (int i = 0; i < points.Count; i++)
        {
            right = Math.Max(right, i);
            while (right + 1 < points.Count && Seconds(points[i], points[right + 1]) <= 1 &&
                Seconds(points[right], points[right + 1]) <= options.MaximumEvidenceGapSeconds) right++;
            int first = i, last = right;
            // Sparse observations still support net direction, but cannot prove
            // sustained motion through an unobserved gap. Never use an equal-
            // timestamp neighbour as a speed measurement.
            if (last == first && i + 1 < points.Count && Seconds(points[i], points[i + 1]) > 0 &&
                Seconds(points[i], points[i + 1]) <= options.MaximumTimeGapSeconds) last = i + 1;
            if (last == first && i > 0 && Seconds(points[i - 1], points[i]) > 0 &&
                Seconds(points[i - 1], points[i]) <= options.MaximumTimeGapSeconds) first = i - 1;
            double duration = Seconds(points[first], points[last]);
            if (duration <= 0) { result[i] = new(0, 0, double.NaN, null, false); continue; }
            var (x, y) = Delta(points[first].Coordinate, points[last].Coordinate);
            double distance = Math.Sqrt(x * x + y * y);
            double? climb = points[first].AltitudeM is { } h1 && points[last].AltitudeM is { } h2 &&
                double.IsFinite(h1) && double.IsFinite(h2) ? (h2 - h1) / duration : null;
            result[i] = new(x, y, distance / duration, climb, distance >= 1);
        }
        return result;
    }

    // Coordinate conversion and differencing produce tiny roundoff at inclusive bounds.
    private static bool SpeedOutside(double speed, WholeLegMatchOptions options) =>
        speed < options.MinimumFlightSpeedMps - 1e-6 || speed > options.MaximumFlightSpeedMps + 1e-6;

    private static double Angle(Motion motion, int sign, double ux, double uy) =>
        !motion.DirectionReliable ? double.NaN :
        Math.Acos(Math.Clamp((motion.X * ux + motion.Y * uy) * sign /
            Math.Sqrt(motion.X * motion.X + motion.Y * motion.Y), -1, 1)) * 180 / Math.PI;

    public static double Seconds(SurveyTrackPoint first, SurveyTrackPoint last) =>
        (last.TimestampUtc - first.TimestampUtc).TotalSeconds;

    public static (double X, double Y) Delta(GeoCoordinate a, GeoCoordinate b) =>
        ((b.Longitude - a.Longitude) * 111_319.49 * Math.Cos((a.Latitude + b.Latitude) * Math.PI / 360),
            (b.Latitude - a.Latitude) * 111_319.49);
}
