using System.Globalization;
using System.Text.Json;
using MagOps.Application;
using MagOps.Infrastructure.Services;

namespace MagOps.SmokeTests;

internal static class SegmentationCandidateWorkflowChecks
{
    internal static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "magops-cli-candidate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string csv = Path.Combine(directory, "input.csv");
        var rows = Enumerable.Range(0, 81).Select(i => string.Join(',', "20261030",
            TimeOnly.MinValue.Add(TimeSpan.FromSeconds(i)).ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            (111 + i * .00006).ToString("R", CultureInfo.InvariantCulture), "30", "unchanged-business-channel")).ToArray();
        File.WriteAllLines(csv, new[] { "Date,Time,Longitude,Latitude,Raw" }.Concat(rows));
        var input = new SegmentationCandidateInput(csv, "intermediate-csv") { AxisDegrees = 90 };
        var workflow = new SegmentationCandidateWorkflow();
        var first = workflow.Compute(input);
        Check(first.InputRecords == 81 && first.Legs.Sum(l => l.Samples.Count) == 81 && first.Audit.Execution.Passes.Count > 0,
            "Actual segmentation computation and full point evidence");
        Check(first.TimeBasis == IntermediateCsvSegmentationService.TimeBasis && !first.ProductionEligible && first.Status == "CANDIDATE_ONLY",
            "Relative source clocks never become confirmed UTC production");
        var parsed = IntermediateCsvSegmentationService.Read(csv);
        var desktop = new AuditProductionService().Analyze(parsed.Analysis, [], first.Review);
        Check(JsonSerializer.Serialize(desktop.Execution) == JsonSerializer.Serialize(first.Audit.Execution) &&
            desktop.SelectedSamples.SequenceEqual(first.Audit.SelectedSamples), "Shared desktop audit authority gives identical candidates and selection");
        string leg = first.Audit.Execution.Passes[0].FlightLegId;
        var patch = new SegmentationCandidatePatch(first.SourceFingerprint, first.RecognitionFingerprint, first.RequestFingerprint) { IncludeLegIds = [leg] };
        var second = workflow.Compute(input, patch);
        Check(second.Review.ManualIncludedFlightLegIds.Contains(leg) && second.AddedSelection.Count > 0 &&
            second.Audit.Execution.Passes.All(p => p.QualityStatus == first.Audit.Execution.Passes.First(q => q.PassId == p.PassId).QualityStatus),
            "Explicit inclusion changes candidate selection without approving QC");
        string review = Path.Combine(directory, "review.json");
        SegmentationReviewStore.Save(review, second.Review);
        var replayInput = input with { AxisDegrees = null, ReviewPath = review };
        var replay = workflow.Compute(replayInput);
        Check(replay.Audit.SelectedSamples.SequenceEqual(second.Audit.SelectedSamples), "Desktop review JSON replays the same explicit selection");
        var removed = workflow.Compute(replayInput, new(second.SourceFingerprint, second.RecognitionFingerprint, second.RequestFingerprint) { ExcludeLegIds = [leg] });
        Check(removed.RemovedSelection.Count == second.Audit.SelectedSamples.Count && removed.Audit.SelectedSamples.Count == 0,
            "Exclusion patch and point-level diff implement reversible selection");
        Reject(() => workflow.Compute(input, patch with { ExpectedRecognitionFingerprint = "stale" }), "Stale patch");
        Reject(() => workflow.Compute(input, patch with { ExpectedRequestFingerprint = "stale" }), "Stale request patch");
        Reject(() => workflow.Compute(input, patch with { ExpectedSourceFingerprint = "stale" }), "Stale source patch");
        Reject(() => workflow.Compute(input, patch with { ExcludeLegIds = [leg] }), "Conflicting include/exclude");
        Reject(() => workflow.Compute(input, patch with { IncludeLegIds = ["unknown"] }), "Unknown leg");
        Reject(() => workflow.Compute(input with { AxisDegrees = null }), "Spatial CSV needs an explicit axis or KML");
        Reject(() => workflow.Compute(replayInput with { AxisDegrees = 90 }), "Saved parameter override");
        SegmentationReviewStore.Save(review, second.Review with { SourceFingerprint = "stale" });
        Reject(() => workflow.Compute(replayInput), "Old source decisions");
        SegmentationReviewStore.Save(review, second.Review with { RecognitionVersion = "old-version" });
        Reject(() => workflow.Compute(replayInput), "Cross-version migration cannot silently apply decisions");
        SegmentationReviewStore.Save(review, second.Review with { RecognitionVersion = "" });
        Reject(() => workflow.Compute(replayInput), "Missing version cannot authorize old decisions");
        SegmentationReviewStore.Save(review, second.Review);
        Reject(() => workflow.Compute(replayInput with { ExpectedRecognitionFingerprint = "stale" }), "Changed replay recognition input");
        Reject(() => workflow.Compute(input with { Tasks = [null!] }), "Null task element");
        var signedReview = second.Review with
        {
            QualityConfirmations = [new(leg + "/pass", "PASS", "original reviewer")],
            ManualLegAssignments = [new(leg, second.Audit.Execution.Passes[0].SurveyTaskStableId, "original assignment reason")]
        };
        SegmentationReviewStore.Save(review, signedReview);
        var signaturePatch = new SegmentationCandidatePatch(second.SourceFingerprint, second.RecognitionFingerprint,
            ProductionSnapshotService.RequestFingerprint(signedReview)) { ExcludeLegIds = [leg] };
        var signedResult = workflow.Compute(replayInput, signaturePatch);
        Check(signedResult.Review.QualityConfirmations.SequenceEqual(signedReview.QualityConfirmations) &&
            signedResult.Review.ManualLegAssignments.SequenceEqual(signedReview.ManualLegAssignments) &&
            signedResult.Review.Overrides.SequenceEqual(signedReview.Overrides), "Selection patch preserves reviewer, identity reason and interval evidence");
        string rawDirectory = Path.Combine(directory, "raw");
        Directory.CreateDirectory(rawDirectory);
        string rawFile = Path.Combine(rawDirectory, "uav_raw_01.txt");
        File.WriteAllLines(rawFile, new[] { "# QuSpin total-field magnetometer-5Hz", "UTCData,UTCTime,Longitude,Latitude,Altitude,Mag,Signal,GpsStatus,Satellites,SensorStatus" }.Concat(Enumerable.Range(0, 81).Select(i =>
            string.Join(',', "2026-10-30", TimeOnly.MinValue.Add(TimeSpan.FromSeconds(i)).ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
                (111 + i * .00006).ToString("R", CultureInfo.InvariantCulture), "30", "100", "50000", "20", "3D", "15", "T"))));
        var raw = workflow.Compute(new(rawDirectory, "raw-folder") { AxisDegrees = 90 });
        Check(raw.InputRecords == 81 && raw.Sources.Single().Path == rawFile && raw.Legs.Sum(l => l.Samples.Count) == 81 &&
            !raw.ProductionEligible, "Raw folder shares read-only parsing and inferred candidate windows");
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        string inputJson = Path.Combine(directory, "input.json");
        File.WriteAllText(inputJson, JsonSerializer.Serialize(input, json));
        string output = Path.Combine(directory, "bundle");
        var published = workflow.Analyze(inputJson, outputDirectory: output);
        Check(published.Ok && File.Exists(Path.Combine(output, SegmentationReviewStore.SavedInputName)) &&
            File.Exists(Path.Combine(output, "candidate.json")), "Atomic candidate bundle and desktop-loadable decisions");
        Check(workflow.Analyze(Path.Combine(output, "replay.json")).Ok, "Generated replay configuration is valid");
        string patchJson = Path.Combine(directory, "patch.json");
        File.WriteAllText(patchJson, JsonSerializer.Serialize(patch, json));
        string patchedOutput = Path.Combine(directory, "patched-bundle");
        Check(workflow.Analyze(inputJson, patchJson, patchedOutput).Ok && File.Exists(Path.Combine(patchedOutput, "decision-patch.json")),
            "Explicit patch and precondition fingerprints survive candidate publication");
        Check(!workflow.Analyze(inputJson, outputDirectory: output).Ok, "Existing candidate bundle never overwritten");
        File.WriteAllText(inputJson, "{\"inputPath\":\"x\",\"inputKind\":\"raw-folder\",\"typo\":1}");
        Check(!workflow.Analyze(inputJson).Ok, "Unknown configuration fields fail explicitly");
        Check(File.ReadAllLines(csv).Skip(1).SequenceEqual(rows), "Original CSV business values remain unchanged");
    }
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    private static void Reject(Action action, string message)
    { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Invalid candidate accepted: " + message); }
}

