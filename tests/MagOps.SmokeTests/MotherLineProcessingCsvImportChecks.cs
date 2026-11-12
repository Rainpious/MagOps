using System.Globalization;
using System.Text.Json;
using MagOps.Core.Coordinates;
using MagOps.Core.SurveyPlanning;
using MagOps.Infrastructure.Services;

namespace MagOps.SmokeTests;

internal static class MotherLineProcessingCsvImportChecks
{
    internal static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "magops-csv-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "processed.csv");
        var origin = CoordinateService.GaussKrugerForward(111, 30, 111);
        var segment = new SurveyLineSegment(new(origin.Easting, origin.Northing), new(origin.Easting + 100, origin.Northing));
        var catalog = new PartitionMotherLineIdentityCatalog(PartitionMotherLineIdentityService.SchemaVersion, "design", 111, "CGCS2000",
            [new("L12", "mother", 12, [segment])], [new("P01", [new("L12", "mother", [segment])])]);
        string Quote(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
        string[] Row(int i, string line = "child,\"A\"")
        {
            var ll = CoordinateService.GaussKrugerInverse(origin.Easting + 100 - i * 30, origin.Northing, 111);
            return [line, "91", "7", "2026-10-30", $"00:00:0{i}.1234567", "task-existing",
                ll.Longitude.ToString("R", CultureInfo.InvariantCulture), ll.Latitude.ToString("R", CultureInfo.InvariantCulture),
                (origin.Easting + 100 - i * 30).ToString("R", CultureInfo.InvariantCulture), origin.Northing.ToString("R", CultureInfo.InvariantCulture),
                "123.000", "", (50000 + i).ToString(CultureInfo.InvariantCulture), "49000", "1000", "50001", "60", "3", "49001", "0"];
        }
        string header = string.Join(',', MagneticPreLevelProcessingService.ProcessingColumns);
        var values = Enumerable.Range(0, 3).Select(i => Row(i)).ToArray();
        string original = header + "\r\n" + string.Join("\r\n", values.Select(v => string.Join(',', v.Select(Quote)))) + "\r\n";
        File.WriteAllText(path, original);
        var input = MotherLineProcessingCsvImportService.Read(path);
        Check(input.Groups.Single().LineName == "child,\"A\"" && input.Rows.Count == 3, "Quoted fields and full records");
        Check(input.Rows[0].TimestampUtc.Ticks % TimeSpan.TicksPerSecond == 1234567, "Subsecond UTC precision retained");
        var bindings = new[] { new MotherLineCsvBinding(input.Groups[0].Key, "P01", "mother") };
        var batch = MotherLineProcessingCsvImportService.Capture("project", "workarea", catalog, input, bindings);
        Check(!batch.OriginalLineageVerified && batch.SourceKind == "PROCESSING_CSV" &&
            batch.Observations.All(o => o.Sample.SourceFile == path && o.Assignment.SurveyTaskStableId == "task-existing"), "Honest CSV lineage and existing identity");
        Check(batch.Observations.Select(o => o.Sample.SourceRecord).SequenceEqual([1, 2, 3]), "CSV data records are not raw record IDs");
        var assembly = MotherLineProductionAssemblyService.Build("project", "workarea", catalog, [batch]);
        Check(!assembly.OriginalLineageVerified && assembly.PointSets.Single().Runs.Single().Reversed, "Lineage and reverse direction preserved");
        Check(assembly.PointSets.Single().Runs.Single().Points.Select(p => p.ProcessingValues[12]).SequenceEqual(["50002", "50001", "50000"]), "Complete rows reverse with magnetic values");
        var result = MotherLineCsvExportService.WriteNew(directory, assembly, batch.SnapshotId);
        var reread = MotherLineProcessingCsvImportService.Read(result.CsvPath);
        Check(reread.Rows.Select(r => string.Join('|', r.Values.Skip(2))).SequenceEqual(values.Reverse().Select(v => string.Join('|', v.Skip(2)))), "All processing columns except explicit mother name/order remain unchanged");
        using (var manifest = JsonDocument.Parse(File.ReadAllText(result.ManifestPath)))
            Check(!manifest.RootElement.GetProperty("original_lineage_verified").GetBoolean() &&
                manifest.RootElement.GetProperty("status").GetString() == "REVIEW_CANDIDATE", "Export never claims raw provenance or approval");
        var unknown = MotherLineProcessingCsvImportService.Capture("project", "workarea", catalog, input, []);
        var unresolved = MotherLineProductionAssemblyService.Build("project", "workarea", catalog, [unknown]);
        Check(unresolved.Unresolved.Count == 3 && unresolved.PointSets.Count == 0, "Unbound groups stay unresolved, never nearest-line assigned");
        var repeat = MotherLineProcessingCsvImportService.Capture("project", "workarea", catalog, input,
            [bindings[0] with { IsQualityRepeat = true }]);
        Check(MotherLineProductionAssemblyService.Build("project", "workarea", catalog, [repeat]).PointSets.Single().IsQualityRepeat,
            "Explicit repeat designation survives");
        var copy = batch with { SnapshotId = "copy" };
        Check(MotherLineProductionAssemblyService.Build("project", "workarea", catalog, [batch, copy]).DuplicateSourceRecords == 3,
            "Duplicate file rows collapse without coordinate deduplication");
        var conflicting = copy with { Observations = copy.Observations.Select(o => o with
            { Assignment = o.Assignment with { LinePass = 8 } }).ToArray() };
        Reject(() => MotherLineProductionAssemblyService.Build("project", "workarea", catalog, [batch, conflicting]),
            "Same source cannot silently move into another Pass");
        var draft = MotherLineProcessingCsvImportService.Capture("project", "workarea", catalog,
            input with { IsReviewDraft = true }, bindings);
        Check(draft.SnapshotId != batch.SnapshotId, "Draft state participates in snapshot identity");
        Reject(() => MotherLineCsvExportService.WriteNew(directory,
            MotherLineProductionAssemblyService.Build("project", "workarea", catalog, [draft]), draft.SnapshotId),
            "Known review draft cannot export");
        var wrongXy = input with { Rows = input.Rows.Select(r => r with
            { Values = r.Values.Select((v, i) => i == 8 ? "0" : v).ToArray() }).ToArray() };
        Reject(() => MotherLineProcessingCsvImportService.Capture("project", "workarea", catalog, wrongXy, bindings),
            "Projection mismatch cannot silently mix sorting and exported coordinates");
        var interleaved = new[] { Row(0), Row(0), Row(1), Row(1) };
        interleaved[1][2] = interleaved[3][2] = "8";
        File.WriteAllText(path, header + "\n" + string.Join("\n", interleaved.Select(v => string.Join(',', v.Select(Quote)))));
        var split = MotherLineProcessingCsvImportService.Read(path);
        Check(split.Groups.Count == 2 && split.Groups.Select(g => g.LinePass).Order().SequenceEqual([7, 8]) &&
            split.Rows.GroupBy(r => r.Run).All(g => g.Count() == 2), "Interleaved passes retain whole runs and separate purposes");
        var mixed = MotherLineProcessingCsvImportService.Capture("project", "workarea", catalog, split,
            split.Groups.Select(g => new MotherLineCsvBinding(g.Key, "P01", "mother", g.LinePass == 8)).ToArray());
        var mixedAssembly = MotherLineProductionAssemblyService.Build("project", "workarea", catalog, [mixed]);
        Check(mixedAssembly.PointSets.Count == 2 && mixedAssembly.PointSets.All(s => s.Runs.Single().Points.Count == 2),
            "Pass-level repeat choices preserve both point sets");
        File.WriteAllText(path, original);
        Reject(() => MotherLineCandidateFileService.ValidateShape(new("project", "workarea", catalog,
            [batch with { Observations = [batch.Observations[0] with { Sample = batch.Observations[0].Sample with { Longitude = 200 } }] }])), "Invalid JSON coordinate");
        Reject(() => MotherLineProcessingCsvImportService.Capture("project", "workarea", catalog, input,
            [bindings[0] with { PartitionCode = "P99" }]), "Wrong partition");
        Reject(() => MotherLineProcessingCsvImportService.Capture("project", "workarea", catalog, input,
            [bindings[0], bindings[0]]), "Duplicate bindings");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        bool cancelled = false;
        try { MotherLineProcessingCsvImportService.Read(path, cancellation.Token); } catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled, "Cancelled read does not produce a snapshot");
        File.AppendAllText(path, "\r\n");
        Reject(() => MotherLineProcessingCsvImportService.Capture("project", "workarea", catalog, input, bindings), "Changed source blocks stale import");
        File.WriteAllText(path, original);
        foreach (var (index, invalid) in new[] { (6, "NaN"), (7, "91"), (12, "Infinity"), (4, "25:00:00"), (2, "0"), (8, "bogus") })
        {
            var broken = Row(0); broken[index] = invalid;
            File.WriteAllText(path, header + "\n" + string.Join(',', broken.Select(Quote)));
            Reject(() => MotherLineProcessingCsvImportService.Read(path), "Invalid " + index);
        }
        File.WriteAllText(path, header.Replace("RawMagNt", "X") + "\n" + string.Join(',', Row(0).Select(Quote)));
        Reject(() => MotherLineProcessingCsvImportService.Read(path), "Duplicate/missing header");
        File.WriteAllText(path, original);
        Check(File.ReadAllText(path) == original, "No input changes by successful operations");
    }

    private static void Check(bool valid, string message) { if (!valid) throw new Exception(message); }
    private static void Reject(Action action, string message)
    {
        try { action(); } catch (InvalidDataException) { return; }
        throw new Exception("Accepted invalid input: " + message);
    }
}
