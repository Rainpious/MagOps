using MagOps.Core.Domain;
using MagOps.Infrastructure.Services;

internal static class TerminalStopChecks
{
    public static void Run()
    {
        var defaults = new WholeLegMatchOptions();
        Check(defaults.ObservationSeconds == 5 && defaults.TerminalStationarySeconds == 5 &&
            defaults.TerminalStationaryRadiusM == 2,
            "terminal stationary defaults must be 5-second observation, 5-second duration and 2-metre radius");
        const double metre = 1d / 111319.49;
        var time = DateTimeOffset.Parse("2026-10-11T01:00:00Z");
        var task = new PlannedSurveyLineTask(1, 1, "A", "A", "A", new(0, 0), new(200 * metre, 0), 200)
            { SurveyTaskStableId = "A" };
        SurveyTrackPoint Point(int i, double x, double height = 100, double mag = 50000) =>
            new(time.AddSeconds(i * .2), new(x * metre, 0), "source", i) { AltitudeM = height, RawMagNt = mag };
        var body = Enumerable.Range(0, 126).Select(i => Point(i, i * 1.6)).ToArray();
        SurveyExecutionResult Match(SurveyTrackPoint[] points) => new WholeLegMatcherService().Match([task], points,
            [new("mission", "", points[0].TimestampUtc, points[^1].TimestampUtc, "flight") { SurveyTaskStableIds = ["A"] }]);
        void Conserved(SurveyExecutionResult result, SurveyTrackPoint[] points)
        {
            var records = result.Legs.SelectMany(l => l.Samples).Select(p => p.SourceRecord).Order().ToArray();
            Check(records.SequenceEqual(points.Select(p => p.SourceRecord).Order()), "terminal cut must preserve each original sample once");
        }
        var frozen = body.Concat(Enumerable.Range(126, 150).Select(i => Point(i, 200, 100, 50000 + (i - 125) * .5)))
            .Concat(Enumerable.Range(276, 50).Select(i => Point(i, 200 - (i - 275) * 1.8, 300))).ToArray();
        var result = Match(frozen);
        var forward = result.Passes.Single(p => p.ExecutionDirection == "FORWARD");
        Check(forward.Samples[^1].SourceRecord == 125 && forward.EndBoundary!.Kind.StartsWith("TERMINAL_POSITION_FREEZE_REVIEW") &&
            forward.EndBoundary.ConfirmedUtc > forward.EndBoundary.CutUtc,
            "long endpoint freeze with subsequent reversal must backtrack to the last survey point and retain auxiliary magnet evidence");
        Conserved(result, frozen);

        var climb = body.Concat(Enumerable.Range(126, 45).Select(i => Point(i, 200, 100 + (i - 125) * .5))).ToArray();
        result = Match(climb);
        Check(result.Passes.Single().EndBoundary!.Kind == "TERMINAL_STATIONARY_VERTICAL" &&
            result.Passes.Single().Samples[^1].SourceRecord == 125, "stationary horizontal position plus smooth vertical climb separates the tail");
        Conserved(result, climb);

        var internalPause = body.Take(64).Concat(Enumerable.Range(64, 25).Select(i => Point(i, 100.8, 100, 50000 + i)))
            .Concat(Enumerable.Range(89, 62).Select(i => Point(i, 102.4 + (i - 89) * 1.6))).ToArray();
        result = Match(internalPause);
        Check(result.Passes.Single().Samples.Count == internalPause.Length,
            "interior pause and magnetic variation alone cannot remove task data");
        Conserved(result, internalPause);

        var second = task with { Order = 2, TaskId = "B", LineName = "B", SurveyTaskStableId = "B", Start = new(160 * metre, 0), End = new(240 * metre, 0), LengthM = 80 };
        var continuous = Enumerable.Range(0, 226).Select(i => Point(i, i * 1.6)).ToArray();
        result = new WholeLegMatcherService().Match([task, second], continuous,
            [new("mission", "", time, continuous[^1].TimestampUtc, "flight") { SurveyTaskStableIds = ["A", "B"] }]);
        Check(result.Passes.Any(p => p.SurveyTaskStableId == "A") && result.Passes.Any(p => p.SurveyTaskStableId == "B") &&
            result.Legs.Any(l => l.CandidateTaskStableIds.Count == 2),
            "co-linear KML endpoint competition keeps clear bodies and only leaves the true overlap for review");
        Conserved(result, continuous);

        string root = Path.Combine(Path.GetTempPath(), "MagOps-review-" + Guid.NewGuid().ToString("N"));
        try
        {
            var request = new AuditProductionRequest("source-fingerprint", [], [], [], true)
                { RouteFingerprint = "route", RecognitionVersion = WholeLegMatcherService.AlgorithmVersion,
                  ManualIncludedFlightLegIds = ["manual-leg"], DefaultMatchedSelectionsApplied = true };
            Check(SegmentationReviewStore.LoadLatest(root, null) is null, "no review state uses initial request");
            SegmentationReviewStore.Save(Path.Combine(root, SegmentationReviewStore.SavedInputName), request);
            var loaded = SegmentationReviewStore.LoadLatest(root, null)!;
            Check(loaded.SourceFingerprint == request.SourceFingerprint && loaded.ManualIncludedFlightLegIds.Single() == "manual-leg",
                "main-entry review store retains fingerprints and explicit whole-leg selections");
            SegmentationReviewStore.Save(Path.Combine(root, SegmentationReviewStore.AutomaticStateName), request with { ManualIncludedFlightLegIds = [] });
            File.SetLastWriteTimeUtc(Path.Combine(root, SegmentationReviewStore.AutomaticStateName), DateTime.UtcNow.AddSeconds(2));
            loaded = SegmentationReviewStore.LoadLatest(root, null)!;
            Check(loaded.ManualIncludedFlightLegIds.Count == 0 && loaded.DefaultMatchedSelectionsApplied,
                "a newer explicitly empty review cannot fall back to older included legs");

            var emptyAnalysis = new FieldMagFolderAnalysis { Fingerprint = "source-current", UavSamples = [] };
            var priorScreening = new WorkareaScreeningInput(null, new())
                { SourceFingerprint = emptyAnalysis.Fingerprint, RouteFingerprint = "stale-inner-route" };
            priorScreening = priorScreening with { ContextFingerprint = WorkareaSpatialService.ContextFingerprint(priorScreening) };
            string priorScreeningFingerprint = new WorkareaSpatialService().Screen(emptyAnalysis, priorScreening).Fingerprint;
            var scopedRequest = request with
            {
                SourceFingerprint = emptyAnalysis.Fingerprint,
                RouteFingerprint = "current-route",
                ScreeningFingerprint = priorScreeningFingerprint,
                SpatialScreening = priorScreening,
                ManualIncludedFlightLegIds = ["kept-leg"]
            };
            string scopedPath = Path.Combine(root, SegmentationReviewStore.SavedInputName);
            SegmentationReviewStore.Save(scopedPath, scopedRequest);
            File.SetLastWriteTimeUtc(scopedPath, DateTime.UtcNow.AddSeconds(3));
            var currentScreening = priorScreening with { RouteFingerprint = "current-route" };
            currentScreening = currentScreening with { ContextFingerprint = WorkareaSpatialService.ContextFingerprint(currentScreening) };
            string expectedScreeningFingerprint = new WorkareaSpatialService().Screen(emptyAnalysis, currentScreening).Fingerprint;
            loaded = SegmentationReviewStore.LoadLatest(root, currentScreening, emptyAnalysis)!;
            Check(loaded.ScreeningFingerprint == expectedScreeningFingerprint &&
                loaded.ManualIncludedFlightLegIds.SequenceEqual(new[] { "kept-leg" }),
                "unchanged source, route and spatial scope rebase stale nested screening context without dropping whole-leg decisions");

            var changedScope = currentScreening with { Options = new WorkareaScreeningOptions(BufferM: 1500) };
            loaded = SegmentationReviewStore.LoadLatest(root, changedScope, emptyAnalysis)!;
            Check(loaded.ScreeningFingerprint == priorScreeningFingerprint &&
                loaded.ManualIncludedFlightLegIds.SequenceEqual(new[] { "kept-leg" }),
                "changed spatial scope does not silently rebase its prior review fingerprint");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
