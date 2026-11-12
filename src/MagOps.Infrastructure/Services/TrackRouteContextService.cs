using MagOps.Core.Domain;

namespace MagOps.Infrastructure.Services;

/// <summary>Review-only route purposes. Local matches, samples and inclusion decisions remain intact.</summary>
public static class TrackRouteContextService
{
    public const string Version = "route-purpose-fan-context-5-motion-evidence";
    private sealed record Route(string Id, FlightLeg[] Legs);
    private sealed record Terminal(Route Route, bool Start, GeoCoordinate Position, double? Heading, DateTimeOffset Time);

    public static SurveyExecutionResult Annotate(SurveyExecutionResult execution, TrackSegmentationInput? input)
    {
        if (input?.Enabled != true) return execution;
        var options = input.MatchOptions;
        var routes = new List<Route>();
        // Never propagate over a source change, mission boundary or missing-time interval.
        foreach (var group in execution.Legs.Where(l => l.Samples.Count > 0)
            .GroupBy(l => (l.MissionExecutionId, l.Samples[0].SourceFile)))
        {
            var batch = new List<FlightLeg>();
            foreach (var leg in group.OrderBy(l => l.StartUtc).ThenBy(l => l.Samples[0].SourceRecord))
            {
                if (batch.Count > 0 && ((leg.StartUtc - batch[^1].EndUtc).TotalSeconds > options.MaximumTimeGapSeconds ||
                    leg.StartUtc < batch[^1].EndUtc)) Flush();
                batch.Add(leg);
            }
            Flush();
            void Flush()
            {
                if (batch.Count == 0) return;
                routes.Add(new(batch[0].FlightLegId + "/route", batch.ToArray())); batch.Clear();
            }
        }
        var terminals = routes.SelectMany(r => new[] { End(r, true), End(r, false) }).ToArray();
        var reliability = new Dictionary<string, bool>();
        var assigned = execution.Legs.Where(l => l.SurveyTaskStableId is not null && l.Samples.Count >= 2).ToArray();
        var passes = execution.Passes.ToDictionary(p => p.FlightLegId);
        var annotated = new Dictionary<string, FlightLeg>();
        foreach (var route in routes)
        {
            var anchors = route.Legs.Where(ReliableSurvey).ToArray();
            foreach (var leg in route.Legs)
                annotated[leg.FlightLegId] = leg with { ParentRouteId = route.Id,
                    RouteRole = ReliableSurvey(leg) ? RoutePurposeMarkers.Survey : leg.RouteRole == RoutePurposeMarkers.Connector ? RoutePurposeMarkers.Connector : RoutePurposeMarkers.Undetermined,
                    RouteRoleEvidence = leg.RouteRole == RoutePurposeMarkers.Connector ? leg.RouteRoleEvidence : [] };
            // Without a reliable work structure we retain hints rather than labelling an entire flight transit.
            if (anchors.Length == 0) continue;
            MarkTerminal(true, anchors[0]); MarkTerminal(false, anchors[^1]);
            // Connectors need neighbouring, distinct confirmed work legs; unresolved straight lines stay candidates.
            for (int i = 0; i + 1 < anchors.Length; i++)
            {
                var left = anchors[i]; var right = anchors[i + 1];
                if (left.SurveyTaskStableId == right.SurveyTaskStableId) continue;
                var bridge = route.Legs.Where(l => l.StartUtc > left.EndUtc && l.EndUtc < right.StartUtc && !anchors.Contains(l)).ToArray();
                if (bridge.Length == 0 || bridge.Any(l => l.SurveyTaskStableId is not null)) continue;
                var from = Position(left.Samples[^1]); var to = Position(right.Samples[0]);
                if (Distance(from, Position(bridge[0].Samples[0])) > 40 ||
                    Distance(to, Position(bridge[^1].Samples[^1])) > 40) continue;
                // Require a compact end-to-end bridge, not an entire missing work line between anchors.
                double direct = Distance(from, to);
                if (direct > 500 || bridge.Sum(l => Distance(Position(l.Samples[0]), Position(l.Samples[^1]))) > Math.Max(60, direct * 2)) continue;
                foreach (var leg in bridge)
                    SetRole(leg, RoutePurposeMarkers.Connector, "连续短轨迹衔接两条可靠作业测线端点，且没有独立测线身份；连接用途待核验");
            }
            void MarkTerminal(bool start, FlightLeg anchor)
            {
                var terminal = terminals.First(t => t.Route.Id == route.Id && t.Start == start);
                bool known = input.KnownTakeoffPositions.Any(p => Distance(p, terminal.Position) <= 50);
                var cluster = terminals.Where(t => Distance(t.Position, terminal.Position) <= 30 &&
                    Math.Abs((t.Time - terminal.Time).TotalSeconds) >= 180 && t.Heading.HasValue).ToArray();
                bool fan = terminal.Heading.HasValue && cluster.Select(t => t.Route.Id).Distinct().Count() >= 2 &&
                    cluster.Any(t => TrackSegmentationReferenceService.AxisDifference(t.Heading!.Value, terminal.Heading.Value) >= 20);
                var prefix = route.Legs.Where(l => start ? l.EndUtc < anchor.StartUtc : l.StartUtc > anchor.EndUtc).ToArray();
                bool ground = prefix.Any(l => l.TransitEvidence.Concat(l.ContextEvidence)
                    .Any(e => e.Contains("重复低高度") || e.Contains("持续升降")));
                if (!known && !(fan && ground)) return;
                foreach (var leg in prefix)
                    SetRole(leg, RoutePurposeMarkers.Transit, known ? "连续轨迹从/到已知起降区域，止于可靠作业边界" :
                        "多架次端部呈扇状汇聚，并有停留/原地升降证据；沿连续轨迹追踪到作业边界");
            }
            void SetRole(FlightLeg leg, string role, string evidence)
            {
                var hints = leg.TransitEvidence.ToList();
                if (role == RoutePurposeMarkers.Transit) hints.Add("整段往返上下文：" + evidence + (leg.SurveyTaskStableId is null ? "" : "；局部空间命中仅供参考"));
                annotated[leg.FlightLegId] = annotated[leg.FlightLegId] with { RouteRole = role,
                    ParentRouteId = route.Id + "/" + role + "/" + (leg.EndUtc < anchors[0].StartUtc ? "out" : "in"),
                    RouteRoleEvidence = [evidence], TransitEvidence = hints.Distinct().ToArray() };
            }
        }
        return execution with { Legs = execution.Legs.Select(l => annotated.GetValueOrDefault(l.FlightLegId, l)).ToArray() };

        bool ReliableSurvey(FlightLeg leg)
        {
            if (reliability.TryGetValue(leg.FlightLegId, out bool reliable)) return reliable;
            return reliability[leg.FlightLegId] = Compute();
            bool Compute()
            {
            if (leg.SurveyTaskStableId is null || leg.TransitEvidence.Count > 0) return false;
            if (input.ReferenceGeometry.Any(g => g.Id == leg.SurveyTaskStableId && g.Kind is RoutePurposeMarkers.Survey or "TIE") &&
                passes.TryGetValue(leg.FlightLegId, out var pass) && pass.AlongTrackMonotonicity >= options.MinimumMonotonicity &&
                pass.HeadingErrorDeg <= input.RecognitionOptions.AxisToleranceDegrees && pass.MedianCrossTrackM <= options.CorridorM &&
                pass.DurationSec >= 2) return true;
            if (leg.ReviewReason.Contains("REVIEW")) return false;
            var a = Position(leg.Samples[0]); var b = Position(leg.Samples[^1]);
            double? heading = SurveyDirection.Bearing(a, b);
            if (heading is null || Distance(a, b) < input.RecognitionOptions.MinimumSpanM) return false;
            // A return corridor may itself be parallel. Require shared work-end extents, not just parallel neighbours.
            return assigned.Where(other => other.FlightLegId != leg.FlightLegId && other.SurveyTaskStableId is not null &&
                other.SurveyTaskStableId != leg.SurveyTaskStableId && other.Samples.Count >= 2 &&
                other.TransitEvidence.Count == 0 && EndpointCompatible(a, b, other)).Take(2).Count() >= 2;
            }
        }
        bool EndpointCompatible(GeoCoordinate a, GeoCoordinate b, FlightLeg other)
        {
            var c = Position(other.Samples[0]); var d = Position(other.Samples[^1]);
            if (SurveyDirection.Bearing(c, d) is not { } h || TrackSegmentationReferenceService.AxisDifference(h, SurveyDirection.Bearing(a, b)!.Value) > 5) return false;
            var (dx, dy) = EndpointLineRecognizer.Delta(a, b); double length = Math.Sqrt(dx * dx + dy * dy);
            (double Along, double Cross) Project(GeoCoordinate p) { var (x, y) = EndpointLineRecognizer.Delta(a, p); return ((x * dx + y * dy) / length, Math.Abs(x * dy - y * dx) / length); }
            var u = Project(c); var v = Project(d);
            return u.Cross is >= 12 and <= 500 && v.Cross is >= 12 and <= 500 &&
                Math.Abs(Math.Min(u.Along, v.Along)) <= 40 && Math.Abs(Math.Max(u.Along, v.Along) - length) <= 40;
        }
    }
    private static Terminal End(Route route, bool start)
    {
        var points = route.Legs.SelectMany(l => l.Samples).OrderBy(p => p.TimestampUtc).ThenBy(p => p.SourceRecord).ToArray();
        var endpoint = start ? points[0] : points[^1]; var position = Position(endpoint);
        var remote = (start ? points : points.Reverse()).FirstOrDefault(p => Distance(position, Position(p)) >= 80);
        return new(route, start, position, remote is null ? null : SurveyDirection.Bearing(position, Position(remote)), endpoint.TimestampUtc);
    }
    private static GeoCoordinate Position(FlightLegSample sample) => new(sample.Longitude, sample.Latitude);
    private static double Distance(GeoCoordinate a, GeoCoordinate b) => TrackSegmentationReferenceService.Distance(a, b);
}
