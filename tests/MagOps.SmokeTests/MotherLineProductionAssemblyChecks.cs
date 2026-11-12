using System.Text.Json;
using MagOps.Core.Coordinates;
using MagOps.Core.Domain;
using MagOps.Core.SurveyPlanning;
using MagOps.Infrastructure.Services;

namespace MagOps.SmokeTests;

internal static class MotherLineProductionAssemblyChecks
{
    public static void Run()
    {
        var origin = CoordinateService.GaussKrugerForward(111, 30, 111);
        var segment = new SurveyLineSegment(new(origin.Easting, origin.Northing), new(origin.Easting + 100, origin.Northing));
        var catalog = new PartitionMotherLineIdentityCatalog(PartitionMotherLineIdentityService.SchemaVersion,
            "design", 111, "CGCS2000", [new("L12", "mother", 12, [segment])],
            [new("P01", [new("L12", "mother", [segment])]), new("P02", [new("L12", "mother", [segment])])]);
        DateTimeOffset start = new(2026, 10, 21, 0, 0, 0, TimeSpan.Zero);
        MotherLineProductionObservation Row(int record, double along, string partition, string pass, bool repeat = false, string hash = "A")
        {
            var ll = CoordinateService.GaussKrugerInverse(origin.Easting + along, origin.Northing, 111);
            var sample = new UavMagCanonicalSample(start.AddSeconds(record), ll.Longitude, ll.Latitude, 100, null,
                50000 + record, null, "A", 12, "T", "source-" + hash, record, []);
            var assignment = new MagneticSegmentAssignment(sample.SourceFile, record, sample.TimestampUtc,
                ll.Longitude, ll.Latitude, MagneticSegmentClass.SurveyLine, true, 1, "child", "task-" + partition,
                "renamed-child", 1, 7, null, null, null, null)
            { PartitionCode = partition, MotherLineStableId = "mother", IsQualityRepeat = repeat, ExecutionSegmentStableId = partition + "-segment" };
            return new(new string(hash[0], 64), sample, assignment, pass);
        }
        MotherLineProductionBatch Batch(string id, params MotherLineProductionObservation[] rows) => new("project", "workarea", "design", id, rows);
        var a = Row(0, 0, "P01", "pass-a");
        var duplicate = a with { Sample = a.Sample with { SourceFile = "copied-source" },
            Assignment = a.Assignment with { SourceFile = "copied-source", PartitionCode = "P02", SurveyTaskStableId = "task-P02" } };
        var unresolved = Row(9, 30, "P01", "unknown") with
            { Assignment = Row(9, 30, "P01", "unknown").Assignment with { MotherLineStableId = "" } };
        var excluded = Row(10, 40, "P01", "excluded") with
            { Assignment = Row(10, 40, "P01", "excluded").Assignment with { IncludeInProcessing = false } };
        var batches = new[]
        {
            Batch("a", a, Row(1, 20, "P01", "pass-a"), Row(2, 18, "P01", "pass-a"), Row(3, 50, "P01", "pass-a"), excluded, unresolved),
            Batch("b", Row(4, 100, "P02", "pass-b"), Row(5, 75, "P02", "pass-b"), Row(6, 50, "P02", "pass-b"), duplicate),
            Batch("c", Row(0, 0, "P01", "repeat-1", true, "B"), Row(1, 50, "P01", "repeat-1", true, "B"),
                Row(2, 50, "P01", "repeat-2", true, "B"))
        };
        string before = JsonSerializer.Serialize(new { catalog, batches });
        var result = MotherLineProductionAssemblyService.Build("project", "workarea", catalog, batches);
        var normal = result.PointSets.Single(p => !p.IsQualityRepeat);
        Assert(normal.LineName == "L12" && normal.LineOrder == 12 && normal.Runs.Count == 2, "Original mother name/order and separate acquisition runs");
        Assert(normal.Runs[1].Reversed && normal.Runs[1].Points.Select(p => p.Sample.SourceRecord).SequenceEqual([6, 5, 4]), "Reverse whole opposite-direction run");
        Assert(normal.Runs[1].Points.Select(p => (p.Sample.SourceRecord, p.Sample.MagneticFieldNt,
                p.Sample.TimestampUtc, p.Sample.Longitude, p.Sample.Latitude, p.Sample.AltitudeM, p.Sample.LaserAltitudeM))
            .SequenceEqual(batches[1].Observations.Where(o => o.RecognizedPassId == "pass-b")
                .OrderByDescending(o => o.Sample.TimestampUtc)
                .Select(o => (o.Sample.SourceRecord, o.Sample.MagneticFieldNt, o.Sample.TimestampUtc,
                    o.Sample.Longitude, o.Sample.Latitude, o.Sample.AltitudeM, o.Sample.LaserAltitudeM))),
            "Reverse the complete UAV record, preserving magnetics, time, coordinates and both altitude channels");
        Assert(normal.Runs[0].Points.Select(p => p.Sample.SourceRecord).SequenceEqual([0, 1, 2, 3]) &&
            normal.Runs[0].BacktrackingM > 1.9, "Preserve local jitter and flag backtracking rather than spatially sorting points");
        var points = normal.Runs.SelectMany(r => r.Points).ToArray();
        Assert(points.Select(p => p.PointOrder).SequenceEqual(Enumerable.Range(1, 7)) && points.Count(p => Math.Abs(p.AlongM - 50) < .01) == 2,
            "Sequential point order differs from mother LineOrder; keep independent overlap observations");
        Assert(points[0].Provenance.Count == 2 && result.DuplicateSourceRecords == 1 && result.ExcludedRecords == 1 && result.Unresolved.Count == 1,
            "Deduplicate exact source record only and preserve both child provenance links");
        Assert(result.PointSets.Count(p => p.IsQualityRepeat) == 2 && result.PointSets.Single(p => p.Runs.Any(r => r.RecognizedPassId == "repeat-2")).Runs.Single().DirectionUndetermined,
            "Quality repeat passes stay independent; singleton direction remains undetermined");
        Assert(result.InputRecords == result.ExcludedRecords + result.Unresolved.Count + result.DuplicateSourceRecords +
            result.PointSets.Sum(p => p.Runs.Sum(r => r.Points.Count)), "Source conservation");
        Assert(MotherLineProductionAssemblyService.Build("project", "workarea", catalog,
            [batches[0] with { IsReviewDraft = true }]).ContainsReviewDraft && !result.ContainsReviewDraft,
            "Draft provenance survives assembly without implying formal acceptance");
        Assert(before == JsonSerializer.Serialize(new { catalog, batches }), "Inputs unchanged");
        Assert(JsonSerializer.Serialize(result) == JsonSerializer.Serialize(MotherLineProductionAssemblyService.Build("project", "workarea", catalog, batches.Reverse().ToArray())),
            "Deterministic output irrespective of batch input order");
        void Reject(params MotherLineProductionBatch[] invalid)
        {
            try { MotherLineProductionAssemblyService.Build("project", "workarea", catalog, invalid); }
            catch (InvalidDataException) { return; }
            throw new Exception("Invalid assembly was accepted");
        }
        Reject(batches[0] with { WorkareaId = "other" });
        Reject(batches[0] with { DesignId = "other" });
        Reject(batches[0], batches[0]);
        Reject(Batch("bad-hash", a with { SourceSha256 = "A" }));
        Reject(Batch("bad-mapping", a with { Assignment = a.Assignment with { PartitionCode = "P03" } }));
        Reject(Batch("bad-source", a with { Sample = a.Sample with { MagneticFieldNt = 0 } }), Batch("original", a));
        Reject(Batch("bad-purpose", a with { Assignment = a.Assignment with { IsQualityRepeat = true } }), Batch("original", a));
        Reject(Batch("bad-coordinate", a with { Assignment = a.Assignment with { Longitude = 0 } }));
        Reject(Batch("missing-segment", a with { Assignment = a.Assignment with { ExecutionSegmentStableId = "" } }));
        foreach (var invalidCatalog in new[] { catalog with { CentralMeridian = 999 }, catalog with { Datum = "unknown" } })
        {
            bool rejected = false;
            try { MotherLineProductionAssemblyService.Build("project", "workarea", invalidCatalog, []); }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException) { rejected = true; }
            Assert(rejected, "Invalid projection rejected even with no selected rows");
        }
        var collision = MotherLineProductionAssemblyService.Build("project", "workarea", catalog,
            [Batch("a/b", Row(0, 0, "P01", "c", true, "C")), Batch("a", Row(0, 0, "P01", "b/c", true, "D"))]);
        Assert(collision.PointSets.Count == 2, "Snapshot/pass separators cannot collide");
        var legacy = MotherLineProductionAssemblyService.Build("project", "workarea", catalog,
            [Batch("legacy", a with { Assignment = a.Assignment with { MotherLineStableId = "", MotherLineTaskId = "mother" } })]);
        Assert(legacy.PointSets.Count == 0 && legacy.Unresolved.Count == 1, "Legacy name hashes cannot infer stable mother identity");
        var qualityRepeat = batches[2].Observations[0];
        Reject(Batch("first", qualityRepeat), Batch("conflict", qualityRepeat with { RecognizedPassId = "other-repeat" }));
        var singleton = result.PointSets.Single(p => p.Runs.Any(r => r.RecognizedPassId == "repeat-2")).Runs.Single().Points.Single();
        Assert(singleton.Sample.MagneticFieldNt == 50002 && singleton.Sample.TimestampUtc == start.AddSeconds(2),
            "Raw magnetic channel and time remain unchanged");
        var gap = MotherLineProductionAssemblyService.Build("project", "workarea", catalog,
            [Batch("gap", a, Row(1, 10, "P01", "pass-a") with
                { Assignment = Row(1, 10, "P01", "pass-a").Assignment with { LinePass = 8 } })]);
        Assert(gap.PointSets.Single().Runs.Count == 2, "Production gap boundaries stay distinct within the same recognized Pass");
        CheckFinalSnapshot(a, Row(1, 10, "P01", "pass-a"), unresolved);
        var directedRows = new[] { Row(0, 0, "P01", "forward"), Row(1, 50, "P01", "forward"),
            Row(4, 100, "P02", "reverse", hash: "B"), Row(5, 50, "P02", "reverse", hash: "B") };
        var directedAnalysis = new FieldMagFolderAnalysis
        {
            UavSamples = directedRows.Select(r => r.Sample).ToArray(),
            Files = directedRows.GroupBy(r => r.Sample.SourceFile).Select(g => new FieldMagFileScan(g.Key,
                FieldMagSensorFamily.UavOpticalPump, FieldMagDataRole.Raw, g.Count(), null, null, "fixture", [])
                { SourceContentSha256 = g.First().SourceSha256 }).ToArray()
        };
        var directedPasses = directedRows.GroupBy(r => r.RecognizedPassId).Select(g => new SurveyLinePass(g.Key,
            "leg-" + g.Key, "mission", g.First().Assignment.SurveyTaskStableId, [], 0, 0, 1, 1, 0, 0, 0, 1, 1,
            "OK", false, g.Last().Sample.TimestampUtc, [g.First().Sample.SourceFile])
            { Samples = g.Select(r => new ExecutionSampleReference(r.Sample.SourceFile, r.Sample.SourceRecord, 0)).ToArray() }).ToArray();
        var directedAudit = new AuditProductionResult("OK", false,
            new SurveyExecutionResult("fixture", [], [], [], directedPasses, []), [],
            directedPasses.SelectMany(p => p.Samples).ToArray(), []) { ProductionSnapshotFingerprint = "directed-audit" };
        var directedSnapshot = MotherLineProductionSnapshotService.Capture("project", "workarea", catalog.DesignId,
            directedAnalysis, directedAudit, directedRows.Select(r => (r.Sample, (MagneticSegmentAssignment?)r.Assignment)).ToArray());
        var processingPayload = directedRows.Select((r, i) =>
        {
            var values = Enumerable.Range(0, 20).Select(column => $"record-{i}-column-{column}").ToArray();
            values[2] = r.Assignment.LinePass?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
            values[5] = r.Assignment.LineTaskId;
            values[12] = r.Sample.MagneticFieldNt!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return new FinalProcessingCsvRow(r.Sample.SourceFile, r.Sample.SourceRecord, values);
        }).ToArray();
        var payloadSnapshot = MotherLineProductionSnapshotService.Capture("project", "workarea", catalog.DesignId,
            directedAnalysis, directedAudit, directedRows.Select(r => (r.Sample, (MagneticSegmentAssignment?)r.Assignment)).ToArray(), processingPayload);
        var payloadAssembly = MotherLineProductionAssemblyService.Build("project", "workarea", catalog, [payloadSnapshot]);
        var reversedPayload = payloadAssembly.PointSets.Single().Runs[1].Points;
        Assert(payloadSnapshot.SnapshotId != directedSnapshot.SnapshotId &&
            reversedPayload.Select(p => p.ProcessingValues[19])
                .SequenceEqual(processingPayload.Skip(2).Reverse().Select(p => p.Values[19])) &&
            reversedPayload.Zip(processingPayload.Skip(2).Reverse(), (point, csv) =>
                point.ProcessingValues.SequenceEqual(csv.Values))
                .All(x => x) &&
            reversedPayload.Zip(processingPayload.Skip(2).Reverse(), (point, csv) =>
                point.Sample.SourceRecord == csv.SourceRecord &&
                point.Sample.MagneticFieldNt!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) == csv.Values[12]).All(x => x),
            "Computed channels and raw magnetics participate in snapshot identity and reverse with their complete source observation");
        var repeatPoint = payloadAssembly.PointSets.Single().Runs[0].Points[0] with { PointOrder = 1 };
        var withRepeat = payloadAssembly with { PointSets = payloadAssembly.PointSets.Append(new MotherLineProductionPointSet(
            "mother", "L12", 12, true, "repeat-pass", [new("repeat-snapshot", "repeat-pass", "P01", "task-P01", 0,
                7, false, true, 0, [repeatPoint])])).ToArray() };
        string csvRoot = Path.GetFullPath(Path.Combine("artifacts/ui-work-20261021/mother-csv-smoke", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(csvRoot);
        var csvResult = MotherLineCsvExportService.WriteNew(csvRoot, withRepeat, "production-fingerprint");
        string[][] formalCsvRows;
        using (var csv = new Microsoft.VisualBasic.FileIO.TextFieldParser(csvResult.CsvPath))
        {
            csv.SetDelimiters(","); csv.HasFieldsEnclosedInQuotes = true;
            Assert(csv.ReadFields()!.SequenceEqual(MagneticPreLevelProcessingService.ProcessingColumns),
                "Mother candidate CSV preserves the exact Oasis processing column contract");
            var rows = new List<string[]>();
            string[]? row; while ((row = csv.ReadFields()) is not null) rows.Add(row);
            formalCsvRows = rows.ToArray();
        }
        var formalPointSet = withRepeat.PointSets.Single(p => !p.IsQualityRepeat);
        var expectedFormalPoints = formalPointSet.Runs.OrderBy(r => r.Points[0].PointOrder).SelectMany(r => r.Points).ToArray();
        bool formalRowsMatch = formalCsvRows.Length == expectedFormalPoints.Length && csvResult.Rows == expectedFormalPoints.Length;
        for (int i = 0; formalRowsMatch && i < expectedFormalPoints.Length; i++)
        {
            var point = expectedFormalPoints[i];
            var expectedValues = point.ProcessingValues.ToArray();
            expectedValues[0] = formalPointSet.LineName;
            expectedValues[1] = formalPointSet.LineOrder.ToString(System.Globalization.CultureInfo.InvariantCulture);
            formalRowsMatch = formalCsvRows[i].Length == MagneticPreLevelProcessingService.ProcessingColumns.Count &&
                formalCsvRows[i].SequenceEqual(expectedValues);
        }
        Assert(formalRowsMatch,
            "Every formal row retains the mother LineName/LineOrder and complete source processing payload in mother point order");
        string[][] traceRecords;
        using (var trace = new Microsoft.VisualBasic.FileIO.TextFieldParser(csvResult.TracePath))
        {
            trace.SetDelimiters(","); trace.HasFieldsEnclosedInQuotes = true;
            var traceHeader = trace.ReadFields()!;
            var traceRows = new List<string[]>();
            string[]? row; while ((row = trace.ReadFields()) is not null) traceRows.Add(row);
            traceRecords = traceRows.ToArray();
            Assert(traceHeader[0] == "CsvRow" && traceHeader[3] == "ProcessingLinePass" &&
                traceHeader[4] == "ProcessingLineTaskId" && traceHeader[5] == "PointOrder" && traceRecords
                .GroupBy(r => int.Parse(r[0], System.Globalization.CultureInfo.InvariantCulture))
                .All(g => g.Key is >= 2 && g.Key < 2 + formalCsvRows.Length &&
                    g.All(r => r[1] == formalPointSet.LineName && r[2] == formalPointSet.LineOrder.ToString(System.Globalization.CultureInfo.InvariantCulture) &&
                        r[3] == formalCsvRows[g.Key - 2][2] && r[4] == formalCsvRows[g.Key - 2][5] &&
                        r[5] == expectedFormalPoints[g.Key - 2].PointOrder.ToString(System.Globalization.CultureInfo.InvariantCulture))) &&
                traceRecords.Length == expectedFormalPoints.Length,
                "Per-row provenance maps mother rows to PointOrder and preserves the downstream LinePass/LineTaskId values");
        }
        int repeatRows = csvResult.QualityRepeatCsvPath is null ? -1 : File.ReadAllLines(csvResult.QualityRepeatCsvPath).Length;
        using var manifest = JsonDocument.Parse(File.ReadAllText(csvResult.ManifestPath));
        int repeatSets = manifest.RootElement.GetProperty("quality_repeat_sets").GetInt32();
        var identityAudit = manifest.RootElement.GetProperty("downstream_identity_audit")[0];
        var expectedTaskIds = formalCsvRows.Select(r => r[5]).Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        var expectedPasses = formalCsvRows.Select(r => r[2]).Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        Assert(repeatRows == 2 && traceRecords.Length == expectedFormalPoints.Length && repeatSets == 1,
            $"Quality repeat/provenance counts differ: repeat={repeatRows}, trace={traceRecords.Length}, sets={repeatSets}");
        Assert(identityAudit.GetProperty("line_name").GetString() == formalPointSet.LineName &&
            identityAudit.GetProperty("line_order").GetInt32() == formalPointSet.LineOrder &&
            identityAudit.GetProperty("formal_records").GetInt32() == formalCsvRows.Length &&
            identityAudit.GetProperty("distinct_processing_line_task_ids").EnumerateArray().Select(x => x.GetString()).SequenceEqual(expectedTaskIds) &&
            identityAudit.GetProperty("distinct_processing_line_pass_values").EnumerateArray().Select(x => x.GetString()).SequenceEqual(expectedPasses) &&
            identityAudit.GetProperty("rows_missing_processing_line_task_id").GetInt32() == 0 &&
            identityAudit.GetProperty("rows_missing_processing_line_pass").GetInt32() == 0,
            "Manifest exposes per-mother-line downstream task/pass identities without changing their semantics");
        var missingFormalSet = formalPointSet with
        {
            Runs = formalPointSet.Runs.Select(run => run with
            {
                Points = run.Points.Select(point => point with
                {
                    ProcessingValues = point.ProcessingValues.Select((value, index) => index is 2 or 5 ? "" : value).ToArray()
                }).ToArray()
            }).ToArray()
        };
        string missingRoot = Path.Combine(csvRoot, "missing-identities"); Directory.CreateDirectory(missingRoot);
        var missingResult = MotherLineCsvExportService.WriteNew(missingRoot, withRepeat with
        {
            PointSets = withRepeat.PointSets.Select(set => set.IsQualityRepeat ? set : missingFormalSet).ToArray()
        }, "production-fingerprint");
        using var missingManifest = JsonDocument.Parse(File.ReadAllText(missingResult.ManifestPath));
        var missingAudit = missingManifest.RootElement.GetProperty("downstream_identity_audit")[0];
        Assert(missingAudit.GetProperty("rows_missing_processing_line_task_id").GetInt32() == formalCsvRows.Length &&
            missingAudit.GetProperty("rows_missing_processing_line_pass").GetInt32() == formalCsvRows.Length &&
            missingAudit.GetProperty("distinct_processing_line_task_ids").GetArrayLength() == 0 &&
            missingAudit.GetProperty("distinct_processing_line_pass_values").GetArrayLength() == 0,
            "Manifest reports missing downstream task/pass identities for every affected row");
        int outputDirectoryCount = Directory.GetDirectories(csvRoot).Length;
        bool draftRejected = false;
        try { MotherLineCsvExportService.WriteNew(csvRoot, withRepeat with { ContainsReviewDraft = true }, "production-fingerprint"); }
        catch (InvalidDataException) { draftRejected = true; }
        Assert(draftRejected && Directory.GetDirectories(csvRoot).Length == outputDirectoryCount, "Review-draft assembly cannot create a production CSV candidate");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            bool cancellationObserved = false;
            try { MotherLineCsvExportService.WriteNew(csvRoot, withRepeat, "production-fingerprint", cancelled.Token); }
            catch (OperationCanceledException) { cancellationObserved = true; }
            Assert(cancellationObserved && Directory.GetDirectories(csvRoot).Length == outputDirectoryCount,
                "Cancelled candidate export creates no partial output directory");
        }
        var payloadDuplicate = payloadSnapshot with { SnapshotId = "conflict", Observations = [payloadSnapshot.Observations[0] with
            { ProcessingValues = payloadSnapshot.Observations[0].ProcessingValues.Select((v, i) => i == 19 ? "different-dTgz" : v).ToArray() }] };
        Reject(payloadSnapshot, payloadDuplicate);
        var wrongColumns = payloadSnapshot with { SnapshotId = "wrong-columns", Observations = [payloadSnapshot.Observations[0] with { ProcessingValues = ["incomplete"] }] };
        Reject(wrongColumns);
        var directedAssembly = MotherLineProductionAssemblyService.Build("project", "workarea", catalog, [directedSnapshot]);
        var directedRuns = directedAssembly.PointSets.Single().Runs;
        Assert(directedRuns.Count == 2 && !directedRuns[0].Reversed && directedRuns[1].Reversed &&
            directedRuns[0].Points.Select(p => p.Sample).SequenceEqual(directedRows.Take(2).Select(r => r.Sample)) &&
            directedRuns[1].Points.Select(p => p.Sample).SequenceEqual(directedRows.Skip(2).Reverse().Select(r => r.Sample)),
            "Final-export snapshot to mother assembly aligns opposite flights by reversing complete records only");
    }

    private static void CheckFinalSnapshot(MotherLineProductionObservation first,
        MotherLineProductionObservation second, MotherLineProductionObservation unresolved)
    {
        var observations = new[] { first, second, unresolved };
        var analysis = new FieldMagFolderAnalysis
        {
            UavSamples = observations.Select(r => r.Sample).ToArray(),
            Files = [new(first.Sample.SourceFile, FieldMagSensorFamily.UavOpticalPump, FieldMagDataRole.Raw,
                3, null, null, "fixture", []) { SourceContentSha256 = first.SourceSha256 }]
        };
        var refs = observations.Select(r => new ExecutionSampleReference(r.Sample.SourceFile, r.Sample.SourceRecord, 0)).ToArray();
        var pass = new SurveyLinePass("recognized", "leg", "mission", first.Assignment.SurveyTaskStableId,
            [], 0, 0, 1, 1, 0, 0, 0, 1, 1, "OK", false, first.Sample.TimestampUtc, [first.Sample.SourceFile])
            { Samples = refs.Take(2).ToArray() };
        var audit = new AuditProductionResult("OK", true,
            new SurveyExecutionResult("fixture", [], [], [], [pass], []), [], refs, [])
            { ProductionSnapshotFingerprint = "audit" };
        var final = new (UavMagCanonicalSample Sample, MagneticSegmentAssignment? Assignment)[]
        {
            (first.Sample, first.Assignment),
            (second.Sample, second.Assignment with { LinePass = 99 }),
            (unresolved.Sample, unresolved.Assignment)
        };
        MotherLineProductionBatch Capture() => MotherLineProductionSnapshotService.Capture("p", "w", "d", analysis, audit, final);
        string before = JsonSerializer.Serialize(new { analysis, audit, final = final.Select(r => new { r.Sample, r.Assignment }) });
        var snapshot = Capture();
        Assert(snapshot.IsReviewDraft && snapshot.AuditSnapshotFingerprint == "audit" &&
            snapshot.Observations[1].Assignment.LinePass == 99 && snapshot.Observations[1].RecognizedPassId == "recognized",
            "Snapshot retains draft status, final CSV gap and independent recognized Pass");
        Assert(snapshot.Observations[2].RecognizedPassId == "" && snapshot.Observations[2].Assignment.MotherLineStableId == "",
            "Unresolved selected export never borrows a nearby Pass or mother identity");
        Assert(before == JsonSerializer.Serialize(new { analysis, audit, final = final.Select(r => new { r.Sample, r.Assignment }) }),
            "Snapshot adapter leaves raw values and upstream selection unchanged");
        var subset = MotherLineProductionSnapshotService.Capture("p", "w", "d", analysis, audit, final.Take(1).ToArray());
        Assert(subset.SnapshotId != snapshot.SnapshotId && snapshot.SnapshotId == Capture().SnapshotId,
            "Final CSV subset identifies its own deterministic snapshot");
        var changedGap = final.ToArray();
        changedGap[1] = (second.Sample, second.Assignment with { LinePass = 100 });
        Assert(snapshot.SnapshotId != MotherLineProductionSnapshotService.Capture("p", "w", "d", analysis, audit, changedGap).SnapshotId,
            "Final gap boundaries participate in snapshot identity");
        void Reject(FieldMagFolderAnalysis input, AuditProductionResult decision,
            IReadOnlyList<(UavMagCanonicalSample Sample, MagneticSegmentAssignment? Assignment)> output)
        {
            try { MotherLineProductionSnapshotService.Capture("p", "w", "d", input, decision, output); }
            catch (InvalidDataException) { return; }
            throw new Exception("Invalid production snapshot accepted");
        }
        Reject(analysis, audit, [final[0], final[0]]);
        Reject(analysis, audit with { SelectedSamples = [] }, final);
        Reject(analysis, audit with { ProductionSnapshotFingerprint = "" }, final);
        Reject(analysis, audit, [(first.Sample with { MagneticFieldNt = 0 }, first.Assignment)]);
        Reject(analysis, audit, [(first.Sample, first.Assignment with { IncludeInProcessing = false })]);
        Reject(analysis, audit, [(first.Sample, null)]);
        Reject(new FieldMagFolderAnalysis { UavSamples = analysis.UavSamples, Files = [analysis.Files[0] with { SourceContentSha256 = "" }] }, audit, final);
        Reject(analysis, audit with { Execution = audit.Execution with { Passes = [pass, pass with { PassId = "ambiguous" }] } }, final);
    }
    private static void Assert(bool valid, string message) { if (!valid) throw new Exception(message); }
}
