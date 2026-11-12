using System.Text.Json;
using MagOps.Core.Domain;
using MagOps.Infrastructure.Services;

internal static class ReviewReuseChecks
{
    public static void Run()
    {
        const double metre = 1d / 111319.49;
        var time = DateTimeOffset.Parse("2026-10-13T00:00:00Z");
        var rows = Enumerable.Range(0, 81).Select(i => new UavMagCanonicalSample(time.AddSeconds(i),
            (i < 21 ? i * 8 : i < 30 ? 160 : 160 + (i - 29) * 8) * metre, 0,
            i < 29 ? 911.2 : 1147.8, null, 50000 + i, null, "A", 12, "T", "reuse.uav", i, [])).ToArray();
        var analysis = new FieldMagFolderAnalysis { Fingerprint = "reuse-fixture", UavSamples = rows };
        var task = new PlannedSurveyLineTask(1, 1, "L1", "A", "L1", new(0, 0), new(560 * metre, 0), 560)
            { SurveyTaskStableId = "reuse-L1" };
        foreach (bool actual in new[] { false, true })
        {
            var input = new TrackSegmentationInput { Enabled = actual, UseDesignTasks = true, AxisDegrees = 90 };
            var request = new AuditProductionRequest(analysis.Fingerprint,
                [new("reuse-mission", "", time, time.AddSeconds(80), "flight")
                    { SurveyTaskStableIds = [task.SurveyTaskStableId], TaskScopeSource = "MANUAL" }], [], [], true)
            {
                RouteFingerprint = "reuse-route", RequirePassConfirmation = true,
                TrackSegmentation = input, TrackSegmentationFingerprint = TrackSegmentationReferenceService.Fingerprint(input)
            };
            request = request with { RecognitionVersion = AuditProductionService.RecognitionVersion(request) };
            var service = new AuditProductionService();
            var first = service.Analyze(analysis, [task], request, request.RouteFingerprint);
            void Equal(AuditProductionRequest next)
            {
                var reused = service.Analyze(analysis, [task], next, next.RouteFingerprint, reuseRecognition: true);
                var full = new AuditProductionService().Analyze(analysis, [task], next, next.RouteFingerprint);
                string Comparable(AuditProductionResult result) => JsonSerializer.Serialize(new
                {
                    result.Status, result.IsReviewDraft, result.Execution,
                    Selections = result.Selections.Select(selection => selection with { SelectionId = "generated" }),
                    result.SelectedSamples, result.BlockedReasons, result.ProductionAnomalies,
                    result.ProductionSnapshotFingerprint, result.ProductionRequestFingerprint,
                    result.RecognizedCoverage, result.SelectedCoverage, result.ManualIncludedFlightLegIds
                });
                var legacyHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
                {
                    Version = ProductionAnomalyDetector.Version, Source = analysis.Fingerprint,
                    Route = next.RouteFingerprint, Request = next,
                    Selected = full.SelectedSamples.Select(x => new { x.SourceFile, x.SourceRecord })
                        .OrderBy(x => x.SourceFile, StringComparer.Ordinal).ThenBy(x => x.SourceRecord)
                }))));
                Check(legacyHash == reused.ProductionSnapshotFingerprint, "streaming snapshot hash must retain the prior exact JSON contract");
                var left = Comparable(reused); var right = Comparable(full);
                Check(left == right,
                    "reuse must equal full analysis for every identity, boundary, warning, decision and source record");
            }
            Equal(request with { ManualIncludedFlightLegIds = first.Execution.Legs.Select(l => l.FlightLegId).ToArray() });
            Check(service.RecognitionRuns == 1 && service.RecognitionReuses == 1 &&
                service.LastStages.Single(s => s.Name == "anomaly-detection").Reused,
                "right-side inclusion reuses identity, route evidence and anomaly detection");
            var included = service.Analyze(analysis, [task], request with
                { ManualIncludedFlightLegIds = first.Execution.Legs.Select(l => l.FlightLegId).ToArray() },
                request.RouteFingerprint, reuseRecognition: true);
            var anomaly = included.ProductionAnomalies.First(a => a.Kind == "POSITION_FREEZE_HEIGHT_JUMP");
            Equal(request with { ManualIncludedFlightLegIds = first.Execution.Legs.Select(l => l.FlightLegId).ToArray(),
                AnomalyDecisions = [new(anomaly.AnomalyId, "RESTORE", "verified original records")] });
            Equal(request);
            var assignable = first.Execution.Legs.First(l => l.MissionExecutionId == "reuse-mission" && l.Samples.Count >= 2);
            Equal(request with { ManualLegAssignments = [new(assignable.FlightLegId, task.SurveyTaskStableId, "manual")],
                ManualIncludedFlightLegIds = [assignable.FlightLegId] });
            Check(service.RecognitionRuns == 1, "manual assignment never re-identifies the source trajectory");
            Equal(request); // Removing manual assignment restores the automatic baseline.
            if (first.Execution.Passes.Count > 0)
            {
                var pass = first.Execution.Passes[0];
                Equal(request with { ConfirmedPassIds = [pass.PassId],
                    QualityConfirmations = [new(pass.PassId, "FAIL", "fixture")] });
                Equal(request with { ConfirmedPassIds = [pass.PassId],
                    QualityConfirmations = [new(pass.PassId, "PASS", "fixture")] });
            }
            var changed = input with { MatchOptions = input.MatchOptions with { HeadingToleranceDegrees = 3 } };
            Equal(request with { TrackSegmentation = changed,
                TrackSegmentationFingerprint = TrackSegmentationReferenceService.Fingerprint(changed) });
            Check(service.RecognitionRuns == 2, "threshold changes invalidate the recognition snapshot even on a reuse path");
            Equal(request with { MissionWindows = request.MissionWindows.Select(w => w with { EndUtc = time.AddSeconds(70) }).ToArray() });
            Check(service.RecognitionRuns == 3, "window changes invalidate the recognition snapshot");
            Equal(request with { ProjectCentralMeridian = 111 });
            Check(service.RecognitionRuns == 4, "central meridian changes invalidate the recognition snapshot");
            var windows = request.MissionWindows.Select(w => w with { TaskScopeSource = "AUTO" }).ToArray();
            Equal(request with { MissionWindows = windows });
            int runs = service.RecognitionRuns;
            Equal(request with { MissionWindows = windows.Select(w => w with { SurveyTaskStableIds = ["previous-derived-id"] }).ToArray() });
            Check(service.RecognitionRuns == runs, "derived AUTO task IDs are output, not recognition authority");
            Equal(request with { MissionWindows = request.MissionWindows.Select(w => w with { SurveyTaskStableIds = [] }).ToArray() });
            Check(service.RecognitionRuns == runs + 1, "MANUAL task scope changes invalidate recognition");
            Equal(request with { RouteFingerprint = "changed-route" });
            Check(service.RecognitionRuns == runs + 2, "reference route changes invalidate recognition");
            service.Analyze(analysis, [task with { End = new(600 * metre, 0), LengthM = 600 }], request, request.RouteFingerprint, reuseRecognition: true);
            Check(service.RecognitionRuns == runs + 3, "task geometry changes invalidate reuse even with a stale route fingerprint");
            var other = new FieldMagFolderAnalysis { UavSamples = rows.Take(70).ToArray(), Fingerprint = "changed-source" };
            service.Analyze(other, [task], request with { SourceFingerprint = other.Fingerprint }, request.RouteFingerprint, reuseRecognition: true);
            Check(service.RecognitionRuns == runs + 4, "source replacement cannot reuse a session snapshot");

        }
        Console.WriteLine("PASS: decision overlays equal full recognition and all recognition-changing inputs invalidate reuse");
    }

    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
