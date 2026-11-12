using MagOps.Core.Domain;
using MagOps.Infrastructure.Services;
using MagOps.Core.Geomagnetics;
using MagOps.Core.Diurnal;
using System.IO.Compression;

internal static class ProductionAnomalyChecks
{
    public static void Run()
    {
        var t = DateTimeOffset.Parse("2026-10-12T00:00:00Z");
        FlightLegSample P(int i, double x, double h) => new("uav.txt", i, t.AddSeconds(i), x, 0, h);
        var frozen = Enumerable.Range(0, 7).Select(i => P(i, 100, 911.2))
            .Append(P(7, 100, 1147.8)).ToArray();
        var leg = new FlightLeg("leg", "mission", t, frozen[^1].TimestampUtc, "SURVEY", "task", frozen.Length, "") { Samples = frozen };
        var found = ProductionAnomalyDetector.Detect([leg]);
        Check(found.Count == 1 && found[0].ProductionDecision == "AUTO_EXCLUDE" && found[0].Samples.Count == 8,
            "frozen coordinates with a height jump produce one confirmed production exclusion");

        var movingHeight = Enumerable.Range(0, 8).Select(i => P(i, 100, 900 + i * 2d)).ToArray();
        var climb = leg with { Samples = movingHeight, EndUtc = movingHeight[^1].TimestampUtc };
        Check(ProductionAnomalyDetector.Detect([climb]).Single().Kind == "STATIONARY_VERTICAL",
            "smooth vertical movement at a stationary coordinate is identified separately");

        var uncertain = Enumerable.Range(0, 7).Select(i => P(i, 100, 900 + (i % 2))).ToArray();
        var review = leg with { Samples = uncertain, EndUtc = uncertain[^1].TimestampUtc };
        Check(ProductionAnomalyDetector.Detect([review]).Single().ProductionDecision == "KEEP_PENDING_REVIEW",
            "stationary evidence without reliable recovery remains pending review");
        var gap = leg with { Samples = frozen.Select((p, i) => p with { TimestampUtc = t.AddSeconds(i * 2) }).ToArray() };
        Check(ProductionAnomalyDetector.Detect([gap]).Count == 0, "time gaps cannot be stitched into stationary duration");
        var splitSources = leg with { Samples = frozen.Select((p, i) => p with { SourceFile = i < 4 ? "a" : "b" }).ToArray() };
        Check(ProductionAnomalyDetector.Detect([splitSources]).Count == 0, "short observations from different sources cannot form a stationary cluster");
        var sameTime = leg with { Samples = frozen.Select(p => p with { TimestampUtc = t }).ToArray() };
        Check(ProductionAnomalyDetector.Detect([sameTime]).Count == 0, "duplicate UTC records do not establish duration");
        var terrain = leg with { Samples = Enumerable.Range(0, 50).Select(i => P(i, i * 8 / 111320d, 900 + i)).ToArray() };
        Check(ProductionAnomalyDetector.Detect([terrain]).Count == 0, "terrain following at survey speed is not stationary vertical motion");
        var crossing = leg with { Samples = Enumerable.Range(0, 50).Select(i => P(i, Math.Sin(i) * 20 / 111320d, 900)).ToArray() };
        Check(ProductionAnomalyDetector.Detect([crossing]).Count == 0, "returning to a coordinate is not a stationary cluster");
        Integration(t);
    }

    private static void Integration(DateTimeOffset t)
    {
        const double metre = 1 / 111320d;
        var rows = Enumerable.Range(0, 70).Select(i => new UavMagCanonicalSample(t.AddSeconds(i),
            (i <= 20 ? i * 8 : i <= 29 ? 160 : 160 + (i - 29) * 8) * metre, 0,
            i < 29 ? 911.2 : 1147.8, null, 50000 + i, null, "A", 12, "T", "production-uav", i, [])).ToArray();
        var analysis = new FieldMagFolderAnalysis { Fingerprint = "p0-production", UavSamples = rows,
            BaseSamples = Enumerable.Range(0, 70).Select(i => new Gsm19TCanonicalSample(t.AddSeconds(i), 49900, null, "base", i, [])).ToArray() };
        var task = new PlannedSurveyLineTask(1, 1, "L1", "L1", "L1", new(0, 0), new(480 * metre, 0), 480)
        {
            SurveyTaskStableId = "p0-task", PartitionCode = "P01", MotherLineTaskId = "legacy-L1",
            MotherLineStableId = "mother-L1", SurveyLineStableId = "line-L1",
            ExecutionSegmentId = "L1.S1", ExecutionSegmentStableId = "segment-L1-1"
        };
        var request = new AuditProductionRequest(analysis.Fingerprint,
            [new("mission", "", t, t.AddSeconds(69), "flight") { SurveyTaskStableIds = [task.SurveyTaskStableId] }], [], [], true)
            { RouteFingerprint = "route", RecognitionVersion = WholeLegMatcherService.AlgorithmVersion };
        var service = new AuditProductionService();
        var first = service.Analyze(analysis, [task], request, "route");
        request = request with { ManualIncludedFlightLegIds = first.Execution.Legs.Select(l => l.FlightLegId).ToArray() };
        var audit = service.Analyze(analysis, [task], request, "route");
        var confirmed = audit.ProductionAnomalies.Single(a => a.Kind == "POSITION_FREEZE_HEIGHT_JUMP");
        var chosen = audit.SelectedSamples.Select(s => s.SourceRecord).ToHashSet();
        Check(!confirmed.Samples.Any(s => chosen.Contains(s.SourceRecord)) && chosen.Contains(19) && chosen.Contains(30),
            "manual whole-leg inclusion subtracts frozen/jump points and retains normal before/after data");
        Check(audit.Execution.Passes.Count == first.Execution.Passes.Count, "anomaly exclusion preserves line identity");
        var restoredRequest = request with { AnomalyDecisions = [new(confirmed.AnomalyId, "RESTORE", "reviewed normal data")] };
        var restored = service.Analyze(analysis, [task], restoredRequest, "route");
        Check(confirmed.Samples.All(s => restored.SelectedSamples.Any(p => p.SourceRecord == s.SourceRecord)),
            "explicit abnormal-point restoration wins over automatic exclusion");
        Check(restored.ProductionSnapshotFingerprint != audit.ProductionSnapshotFingerprint, "restoration changes production snapshot");
        var empty = service.Analyze(analysis, [task], restoredRequest with { ManualIncludedFlightLegIds = [], RequirePassConfirmation = true }, "route");
        Check(empty.SelectedSamples.Count == 0, "restoring an anomaly never includes a left-side leg");
        var blanket = service.Analyze(analysis, [task], request with
        {
            ManualIncludedFlightLegIds = [], RequirePassConfirmation = true, IncludeUnconfirmedSamples = true
        }, "route");
        Check(blanket.ProductionAnomalies.Sum(a => a.ExcludedSelectedPoints) == confirmed.Samples.Count &&
            blanket.SelectedSamples.Count == rows.Length - confirmed.Samples.Count,
            "blanket draft selection reports all actually excluded anomaly rows");
        var changedVersion = service.Analyze(analysis, [task], restoredRequest with
        {
            ManualIncludedFlightLegIds = [], QualityConfirmations = [], Overrides = [], ConfirmedPassIds = [],
            RecognitionVersion = "obsolete-version", IncludeUnconfirmedSamples = true
        }, "route");
        Check(changedVersion.BlockedReasons.Any(r => r.StartsWith("RECOGNITION_VERSION_CHANGED:")) &&
            changedVersion.ProductionAnomalies.All(a => a.ProductionDecision != "USER_RESTORE"),
            "anomaly-only decisions cannot migrate silently across recognition versions");

        string root = Path.Combine(Path.GetTempPath(), "MagOps-p0-" + Guid.NewGuid().ToString("N"));
        try
        {
            string target = Path.Combine(root, "output");
            SegmentationReviewStore.Save(Path.Combine(target, SegmentationReviewStore.SavedInputName), request);
            var reload = SegmentationReviewStore.LoadLatest(target, null)!;
            Check(service.Analyze(analysis, [task], reload, "route").SelectedSamples.Count == audit.SelectedSamples.Count,
                "saved review replays the same anomaly-filtered selection");
            var exporter = new DiurnalCorrectionExportService();
            var models = IgrfDatParser.Load(Path.Combine(AppContext.BaseDirectory, "config", "geomagnetics", "igrf.dat"));
            var result = exporter.WriteNativeCorrection(target, analysis, new DiurnalCorrectionOptions(), models,
                surveyLines: [task], surveyLineFingerprint: "route", auditRequest: request);
            var csv = File.ReadAllLines(result.ProcessingCsvPath);
            Check(csv.Length - 1 == audit.SelectedSamples.Count, "processing CSV exactly materializes the filtered selection");
            var passes = csv.Skip(1).Select(l => l.Split(',')[2]).Distinct().ToArray();
            Check(passes.Length >= 2, "Oasis runs on both sides of the anomaly have different LinePass values");
            Check(result.FinalExportedRows.Count == csv.Length - 1 &&
                result.FinalExportedRows.Select(r => r.Assignment!.Value.LinePass!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .SequenceEqual(csv.Skip(1).Select(l => l.Split(',')[2])),
                "export result exposes exact final CSV rows and correction-gap LinePass in emitted order");
            using (var parser = new Microsoft.VisualBasic.FileIO.TextFieldParser(result.ProcessingCsvPath))
            {
                parser.SetDelimiters(","); parser.HasFieldsEnclosedInQuotes = true;
                Check(parser.ReadFields()!.SequenceEqual(MagneticPreLevelProcessingService.ProcessingColumns), "canonical production column contract");
                Check(result.FinalProcessingRows.Count == result.FinalExportedRows.Count, "all final computed columns captured");
                for (int i = 0; i < result.FinalProcessingRows.Count; i++)
                {
                    var values = result.FinalProcessingRows[i]; var sample = result.FinalExportedRows[i].Sample;
                    Check(values.SourceFile == sample.SourceFile && values.SourceRecord == sample.SourceRecord &&
                        parser.ReadFields()!.SequenceEqual(values.Values), "all emitted processing columns bind to original source record");
                }
                Check(parser.EndOfData, "processing payload does not omit emitted rows");
            }
            Check(result.FinalExportedRows.Select(r => (r.Sample.SourceFile, r.Sample.SourceRecord)).ToHashSet()
                .SetEquals(audit.SelectedSamples.Select(s => (s.SourceFile, s.SourceRecord))),
                "export callback snapshot excludes rejected anomaly points and preserves selected source identity");
            string trace = result.Files.Single(f => f.Contains("production_trace") && f.EndsWith(".csv.gz"));
            using (var stream = File.OpenRead(trace))
            using (var gzip = new GZipStream(stream, CompressionMode.Decompress))
            using (var reader = new StreamReader(gzip))
            {
                var lines = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
                var records = lines.Skip(1).ToArray();
                Check(records.Count(r => r.Contains(",EXPORTED,")) == csv.Length - 1 &&
                    records.Count(r => r.Contains(",ANOMALY_EXCLUDED,")) == confirmed.Samples.Count,
                    "trace accounts for every exported and abnormal excluded source row");
                var header = lines[0].Trim().Split(',');
                string Value(string row, string field) => row.Trim().Split(',')[Array.IndexOf(header, field)].Trim('"');
                Check(records.Where(r => r.Contains(",ANOMALY_EXCLUDED,") || r.Contains(",EXPORTED,")).All(r =>
                    Value(r,"PartitionCode")=="P01" && Value(r,"MotherLineStableId")=="mother-L1" &&
                    Value(r,"SurveyTaskStableId")=="p0-task" && Value(r,"ExecutionSegmentStableId")=="segment-L1-1"),
                    "exported and anomalous excluded rows retain their recognized child-to-mother lineage");
            }
            using (var stream=File.OpenRead(result.LineSegmentationAuditPath))
            using (var gzip=new GZipStream(stream,CompressionMode.Decompress))
            using (var reader=new StreamReader(gzip))
            {
                var header=reader.ReadLine()!.Split(',');
                var entries=reader.ReadToEnd().Split('\n',StringSplitOptions.RemoveEmptyEntries).Select(r=>r.Trim().Split(',')).ToArray();
                Check(entries.Length==rows.Length && entries.All(r=>r.Length==header.Length &&
                    r[Array.IndexOf(header,"MotherLineStableId")]=="mother-L1" &&
                    r[Array.IndexOf(header,"ExecutionSegmentStableId")]=="segment-L1-1"),
                    "compressed segmentation audit exports complete lineage without losing source records");
            }
            Check(!GeneratedArtifactIdentity.Inspect(result.ProcessingCsvPath).IsProductionStale, "fresh export matches saved review snapshot");
            SegmentationReviewStore.Save(Path.Combine(target, SegmentationReviewStore.SavedInputName), restoredRequest);
            Check(GeneratedArtifactIdentity.Inspect(result.ProcessingCsvPath).IsProductionStale, "changed manual anomaly decision makes old CSV stale");
            SegmentationReviewStore.Save(Path.Combine(target, SegmentationReviewStore.SavedInputName), request);
            Check(!GeneratedArtifactIdentity.Inspect(result.ProcessingCsvPath).IsProductionStale, "returning to the saved input matches the existing CSV");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
}
