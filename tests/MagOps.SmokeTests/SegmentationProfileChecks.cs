using System.Text.Json;
using MagOps.Core.Domain;
using MagOps.Infrastructure.Services;

namespace MagOps.SmokeTests;

internal static class SegmentationProfileChecks
{
    public static void Run()
    {
        ShallowTurnChecks.Run();
        KmlContinuationChecks.Run();
        void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        var speed = TrackSegmentationParameterProfiles.ApplySpeed(12, new() { Enabled = true });
        var profile = TrackSegmentationParameterProfiles.Apply("草原风扰", speed);
        Check(profile.RecognitionOptions.AxisToleranceDegrees == 5 && profile.MatchOptions.MaximumFlightSpeedMps == 14,
            "environment must preserve independent speed choice");
        profile = profile with { WindowPlannedSpeeds = new Dictionary<string, double> { ["slow"] = 8 } };
        Check(TrackSegmentationParameterProfiles.ForWindow(profile, "slow").MaximumFlightSpeedMps == 12 &&
            TrackSegmentationParameterProfiles.ForWindow(profile, "fast").MaximumFlightSpeedMps == 14, "window speed override");
        Check(JsonSerializer.Deserialize<TrackSegmentationInput>(JsonSerializer.Serialize(profile))!.WindowPlannedSpeeds["slow"] == 8,
            "profile/window snapshot survives JSON");
        var spaced = profile with { RecognitionOptions = profile.RecognitionOptions with { ExpectedLineSpacingM = 20 } };
        Check(JsonSerializer.Deserialize<TrackSegmentationInput>(JsonSerializer.Serialize(spaced))!.RecognitionOptions.ExpectedLineSpacingM == 20,
            "expected line spacing survives the reproducible analysis snapshot");
        var spacingCompatible = typeof(TrackSegmentationService).GetMethod("SpacingCompatible", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        Check((bool)spacingCompatible.Invoke(null, [40d, 20d])! && !(bool)spacingCompatible.Invoke(null, [30d, 20d])!,
            "spacing supports integer line-gap multiples and rejects half-spacing ambiguity");
        var original = profile.MatchOptions;
        var changed = original with { MinimumFlightSpeedMps = 2, MaximumFlightSpeedMps = 20 };
        Check(original.TerminalSearchSpeedMps == changed.TerminalSearchSpeedMps &&
            original.VerticalMotionHorizontalSpeedMps == changed.VerticalMotionHorizontalSpeedMps &&
            original.DepartureAuxiliaryMaximumSpeedMps == changed.DepartureAuxiliaryMaximumSpeedMps,
            "quality speed range must not alter recognition scales");
        const double metre = 1 / 111319.49;
        var start = new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.Zero);
        var runType = typeof(TrackSegmentationService).GetNestedType("Run", System.Reflection.BindingFlags.NonPublic)!;
        var fragmentList = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(runType))!;
        object Fragment(int first, int last, double y, double heading) => Activator.CreateInstance(runType,
            "fixture", Enumerable.Range(first, last - first + 1).Select(i => new SurveyTrackPoint(start.AddSeconds(i),
                new(i * 8 * metre, y * metre), "fragment.uav", i)).ToList(), heading, (last - first) * 8d, false, null, false)!;
        fragmentList.Add(Fragment(0, 4, 0, 90)); fragmentList.Add(Fragment(5, 10, 0, 90));
        var recover = typeof(TrackSegmentationService).GetMethod("RecoverFragments", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var recovered = ((System.Collections.IEnumerable)recover.Invoke(null, [fragmentList, new TrackRecognitionOptions()])!).Cast<object>().ToArray();
        Check(recovered.Length == 1 && (bool)runType.GetProperty("Straight")!.GetValue(recovered[0])!, "short collinear fragments regain identity eligibility without dropping points");
        fragmentList.Clear(); fragmentList.Add(Fragment(0, 4, 0, 90)); fragmentList.Add(Fragment(5, 10, 20, 90));
        Check(((System.Collections.IEnumerable)recover.Invoke(null, [fragmentList, new TrackRecognitionOptions()])!).Cast<object>().Count() == 2,
            "fragment recovery never stitches neighbouring 20m lines");
        var points = Enumerable.Range(0, 21).SelectMany(second => Enumerable.Range(0, 10).Select(j =>
            new SurveyTrackPoint(start.AddSeconds(second), new(second * 8 * metre, 0), "coarse.uav", second * 10 + j) { AltitudeM = 100 })).ToArray();
        var motion = TrackMotionService.Measure(points);
        Check(motion.Count == 21 && motion.All(o => Math.Abs(o.SpeedMps!.Value - 8) < .01), "coarse motion speed is stable and sample count untouched");
        UavMagCanonicalSample Canonical(SurveyTrackPoint p) => new(p.TimestampUtc, p.Coordinate.Longitude, p.Coordinate.Latitude,
            p.AltitudeM, null, 50000, null, "A", 12, "T", p.SourceFile, p.SourceRecord, []);
        var relaunch = Enumerable.Range(0, 100).SelectMany(second => Enumerable.Range(0, 10).Select(j =>
            new SurveyTrackPoint(start.AddSeconds(second), new((second <= 20 ? second * 8 : second <= 65 ? 160 : 160 + (second - 65) * 8) * metre, 0),
                "relaunch.uav", second * 10 + j) { AltitudeM = 100 })).Select(Canonical).ToArray();
        Check(new FlightWindowInferenceService().Infer(relaunch).Candidates.Count == 2, "coarse timestamp stationary plateau and relaunch form two inferred windows");
        var vertical = Enumerable.Range(0, 10).Select(i => new SurveyTrackPoint(start.AddSeconds(i), new(0, 0), "vertical.uav", i) { AltitudeM = 100 + i }).ToArray();
        var leg = new FlightLeg("vertical", "flight", start, start.AddSeconds(9), "SURVEY", "task", 10, "MATCHED")
        { Samples = vertical.Select(p => new FlightLegSample(p.SourceFile, p.SourceRecord, p.TimestampUtc, p.Coordinate.Longitude, p.Coordinate.Latitude, p.AltitudeM)).ToArray() };
        var execution = new SurveyExecutionResult("test", [], [], [leg], [], []);
        var annotated = TransitEvidenceService.Annotate(execution, vertical.Select(Canonical).ToArray(), new(), null);
        Check(annotated.Legs[0].TransitEvidence.Count == 0 && annotated.Legs[0].MotionKind == "STATIONARY_VERTICAL" &&
            annotated.Legs[0].ContextEvidence.Count > 0 && annotated.Legs[0].SurveyTaskStableId == "task" &&
            annotated.Legs[0].Samples.Count == 10, "vertical evidence preserves identity and samples");
        var slope = vertical.Select((p, i) => p with { Coordinate = new(i * 8 * metre, 0) }).ToArray();
        var slopeLeg = leg with { Samples = slope.Select(p => new FlightLegSample(p.SourceFile, p.SourceRecord, p.TimestampUtc, p.Coordinate.Longitude, p.Coordinate.Latitude, p.AltitudeM)).ToArray() };
        Check(TransitEvidenceService.Annotate(execution with { Legs = [slopeLeg] }, slope.Select(Canonical).ToArray(), new(), null)
            .Legs[0].TransitEvidence.Count == 0, "terrain-following climb is not stationary vertical evidence");
        string directory = Path.Combine(Path.GetTempPath(), "MagOps-profile-" + Guid.NewGuid().ToString("N"));
        FlightLeg RouteLeg(string id, int second, double x1, double y1, double x2, double y2, string? task, string reason) =>
            new(id, "route-flight", start.AddSeconds(second), start.AddSeconds(second + 10), task is null ? "UNRESOLVED" : "SURVEY", task, 2, reason)
            { Samples = [new("route.uav", second, start.AddSeconds(second), x1 * metre, y1 * metre),
                new("route.uav", second + 10, start.AddSeconds(second + 10), x2 * metre, y2 * metre)] };
        var outbound = RouteLeg("out", 0, -100, 0, -40, 0, null, "UNRESOLVED");
        var overlap = RouteLeg("overlap", 11, -40, 0, 0, 0, "return-local", "ACTUAL_ISOLATED_OR_CROSS_AXIS_REVIEW");
        var work1 = RouteLeg("work1", 22, 0, 0, 800, 0, "work1", "ACTUAL_PARALLEL_GROUP");
        var connector = RouteLeg("connector", 33, 800, 0, 800, 20, null, "UNRESOLVED");
        var work2 = RouteLeg("work2", 44, 800, 20, 0, 20, "work2", "ACTUAL_PARALLEL_GROUP");
        var work3 = RouteLeg("work3", 55, 0, 40, 800, 40, "work3", "ACTUAL_PARALLEL_GROUP");
        var routeExecution = execution with { Legs = [outbound, overlap, work1, connector, work2, work3] };
        var context = TrackRouteContextService.Annotate(routeExecution, new() { Enabled = true, KnownTakeoffPositions = [new(-100 * metre, 0)] });
        Check(context.Legs[1].RouteRole == "TRANSIT" && context.Legs[1].SurveyTaskStableId == "return-local" &&
            context.Legs[1].Samples.SequenceEqual(overlap.Samples), "local line overlap inherits transit without erasing identity or samples");
        Check(context.Legs[0].ParentRouteId == context.Legs[1].ParentRouteId && context.Legs[2].RouteRole == "SURVEY" &&
            context.Legs[3].RouteRole == "CONNECTOR", "route propagation stops at shared work endpoints and connectors remain separate");
        var uncertain = TrackRouteContextService.Annotate(routeExecution, new() { Enabled = true });
        Check(uncertain.Legs[1].RouteRole != "TRANSIT", "unsupported terminal convergence cannot exclude valid lines");
        var cut = routeExecution with { Legs = [outbound, overlap with { StartUtc = start.AddSeconds(100), EndUtc = start.AddSeconds(110) },
            work1 with { StartUtc = start.AddSeconds(111), EndUtc = start.AddSeconds(121) },
            work2 with { StartUtc = start.AddSeconds(122), EndUtc = start.AddSeconds(132) },
            work3 with { StartUtc = start.AddSeconds(133), EndUtc = start.AddSeconds(143) }] };
        Check(TrackRouteContextService.Annotate(cut, new() { Enabled = true, KnownTakeoffPositions = [new(-100 * metre, 0)] })
            .Legs[1].RouteRole != "TRANSIT", "route context never crosses a missing-time gap");
        Directory.CreateDirectory(directory);
        try
        {
            string library = Path.Combine(directory, "library.json"), defaults = Path.Combine(directory, "defaults.json");
            SegmentationParameterStore.SaveLibrary("fixture", spaced, library);
            Check(SegmentationParameterStore.LoadLibrary(library)["fixture"].MatchOptions.MaximumFlightSpeedMps == 14 &&
                SegmentationParameterStore.LoadLibrary(library)["fixture"].RecognitionOptions.ExpectedLineSpacingM == 20, "custom library preserves speed and spacing");
            SegmentationParameterStore.SaveDefaults(defaults, profile);
            Check(SegmentationParameterStore.LoadDefaults(defaults)!.WindowPlannedSpeeds.Count == 0, "batch window ids never carry into project defaults");
        }
        finally { Directory.Delete(directory, true); }
    }
}
