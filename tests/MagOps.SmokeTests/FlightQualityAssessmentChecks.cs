using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using MagOps.Core.Domain;
using MagOps.Infrastructure.Services;
using Microsoft.VisualBasic.FileIO;

namespace MagOps.SmokeTests;

internal static class FlightQualityAssessmentChecks
{
    public static void Run()
    {
        const double metre = 1d / 111319.49;
        var start = new DateTimeOffset(2026, 10, 8, 0, 0, 0, TimeSpan.Zero);
        PlannedSurveyLineTask Task(string id, double y, double x0 = 0, double x1 = 200) =>
            new(1, 1, id, "sortie", id, new(x0 * metre, y * metre), new(x1 * metre, y * metre), x1 - x0)
            { SurveyTaskStableId = id };
        UavMagCanonicalSample Point(int i, double x, double y = 10, double? seconds = null) =>
            new(start.AddSeconds(seconds ?? i * .2), x * metre, y * metre, 100 + i * .4, null,
                50000, null, "A", 12, "T", "flight,one.txt", i, []);
        var assigned = Task("assigned", 0);
        var nearer = Task("nearer", 9);
        var zero = Task("zero", 200);
        var extension = Task("extension", 10, -200, -100);
        var samples = Enumerable.Range(0, 126).Select(i => Point(i, i * 1.6)).ToArray();
        var windows = new[] { new MissionTimeWindow("mission", "", start, samples[^1].TimestampUtc)
            { SurveyTaskStableIds = [assigned.SurveyTaskStableId] } };
        var execution = new WholeLegMatcherService().Match([assigned], samples.Select(s =>
            new SurveyTrackPoint(s.TimestampUtc, new(s.Longitude!.Value, s.Latitude!.Value), s.SourceFile, s.SourceRecord)
            { AltitudeM = s.AltitudeM }).ToArray(), windows);
        Check(execution.Passes.Count == 1, "fixture has an assigned offset pass");
        var invalid = Point(1000, 0) with { Longitude = null, Latitude = null, AltitudeM = null };
        var analysis = new FieldMagFolderAnalysis { Fingerprint = "fixture", UavSamples = samples.Append(invalid).ToArray() };
        var request = new AuditProductionRequest("fixture", windows, [], [], true)
        {
            RouteFingerprint = "route", RecognitionVersion = execution.AlgorithmVersion,
            ManualLegAssignments = [new(execution.Legs.Single().FlightLegId, assigned.SurveyTaskStableId, "human fixture")]
        };
        string root = Path.Combine(Path.GetTempPath(), "MagOps-flight-quality-test-" + Guid.NewGuid().ToString("N"));
        var service = new FlightQualityAssessmentService();
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var saved = service.Save(root, analysis, samples, [assigned, nearer, zero, extension], execution, request,
                qualityOptions: new FlightQualityAssessmentOptions(LineSpacingM: 20, DesignedFlightHeightM: 80));
            var points = Points(saved.Directory);
            Check(points.Count == 126 && points.Select(r => (r["SourceFile"], r["SourceRecord"])).Distinct().Count() == 126,
                "all active records survive once with quoted source identity");
            Check(points.All(r => r["ReferenceTaskId"] == "assigned"), "closer parallel lines never replace assigned identity");
            Check(points.All(r => Math.Abs(Number(r["SignedCrossM"])) is > 9.99 and < 10.01), "known 10 m offset is measured");
            Check(points.All(r => Math.Abs(Number(r["Speed1sMps"]) - 8) < .01) &&
                points.All(r => Math.Abs(Number(r["VerticalSpeed1sMps"]) - 2) < .01), "known speed and climb use real elapsed time");
            Check(points.Any(r => r["Longitude"].Contains('.')) && points[0]["AltitudeM"] == "100", "invariant numeric output and original heights");
            using (var summary = JsonDocument.Parse(File.ReadAllText(Path.Combine(saved.Directory, "summary.json"))))
            {
                Check(summary.RootElement.GetProperty("RawSampleCount").GetInt32() == 127 &&
                    summary.RootElement.GetProperty("ActiveSampleCount").GetInt32() == 126, "raw and screened counts stay distinct");
                var surveySpeed = summary.RootElement.GetProperty("Distributions").EnumerateArray().Single(d =>
                    d.GetProperty("Population").GetString() == "SURVEY" && d.GetProperty("Metric").GetString() == "speed_1s_mps");
                Check(Math.Abs(surveySpeed.GetProperty("P50").GetDouble() - 8) < .01, "known empirical median is correct");
            }
            var taskRows = Csv(Path.Combine(saved.Directory, "task-quality.csv"));
            Check(taskRows.Count == 4 && taskRows.Single(r => r["TaskId"] == "zero")["PassCount"] == "0", "zero-candidate tasks remain visible");
            using (var coverage = JsonDocument.Parse(File.ReadAllText(Path.Combine(saved.Directory, "task-coverage.json"))))
            {
                Check(coverage.RootElement.GetProperty("AssessmentIdentity").GetProperty("SourceFingerprint").GetString() == "fixture",
                    "task coverage keeps the source identity used for assessment");
                Check(coverage.RootElement.GetProperty("Recognized").GetArrayLength() == 4 &&
                    coverage.RootElement.GetProperty("Selected").GetProperty("Status").GetString() == "NOT_INCLUDED" &&
                    coverage.RootElement.GetProperty("CsvOutput").GetProperty("Status").GetString() == "NOT_GENERATED",
                    "task coverage separates recognized, selected and CSV output scope");
            }
            var lineRows = Csv(Path.Combine(saved.Directory, "line-flight-quality.csv"));
            Check(lineRows.Count == 1 && Math.Abs(Number(lineRows[0]["MeanOffsetM"]) - 10) < .01 &&
                Math.Abs(Number(lineRows[0]["MaximumOffsetM"]) - 10) < .01, "per-line mean and maximum navigation deviation are reported");
            using (var summary = JsonDocument.Parse(File.ReadAllText(Path.Combine(saved.Directory, "summary.json"))))
            {
                var indicators = summary.RootElement.GetProperty("FlightQualityIndicators");
                Check(indicators.GetProperty("NavigationPositioning").GetProperty("MeanThresholdM").GetDouble() == 4 &&
                    indicators.GetProperty("NavigationPositioning").GetProperty("MaximumThresholdM").GetDouble() > 6.6,
                    "navigation thresholds use the supplied line spacing and preserve one-fifth/one-third rules");
                Check(indicators.GetProperty("FlightHeight").GetProperty("Status").GetString() == "NO_LASER_ALTITUDE",
                    "height quality distinguishes missing AGL evidence from GPS altitude");
                Check(indicators.GetProperty("GpsStaticPositioning").GetProperty("Status").GetString() == "UNAVAILABLE_NO_STATIC_TEST_WINDOW",
                    "GPS static quality is unavailable without an explicit static test window");
            }
            var human = Csv(Path.Combine(saved.Directory, "manual-review-template.csv"));
            Check(human.All(r => r["HumanTaskId"] == "" && r["HumanClassification"] == ""), "human truth is never fabricated");
            using (var snapshot = JsonDocument.Parse(File.ReadAllText(Path.Combine(saved.Directory, "input-and-parameters.json"))))
                Check(snapshot.RootElement.GetProperty("Request").GetProperty("ManualLegAssignments").GetArrayLength() == 1,
                    "manual provenance is retained");
            using (var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(saved.Directory, "manifest.json"))))
            {
                Check(manifest.RootElement.GetProperty("Complete").GetBoolean(), "manifest is complete");
                foreach (var file in manifest.RootElement.GetProperty("Files").EnumerateArray())
                    Check(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(saved.Directory,
                        file.GetProperty("File").GetString()!)))) == file.GetProperty("Sha256").GetString(), "report hashes match");
            }
            var unresolved = execution with { Passes = [], Legs = execution.Legs.Select(l => l with
                { Classification = "UNRESOLVED", SurveyTaskStableId = null }).ToArray() };
            var second = service.Save(root, analysis, samples, [assigned, nearer, zero, extension], unresolved, request);
            Check(second.Directory != saved.Directory, "independent batches never overwrite");
            Check(Points(second.Directory).All(r => r["ReferenceTaskId"] == "nearer" && r["ReferenceKind"] == "DIAGNOSTIC_ONLY"),
                "unmatched finite-line proximity creates hints only, never assignments");
            Check(unresolved.Passes.Count == 0 && execution.Passes.Count == 1 && samples.Length == 126, "export never mutates identity or data");

            SurveyExecutionResult empty = new(execution.AlgorithmVersion, [], [], [], [], []);
            var gap = new[] { Point(0, 0, seconds: 0), Point(1, 24, seconds: 3) };
            var gapReport = service.Save(root, analysis, gap, [], empty, request);
            Check(Points(gapReport.Directory).All(r => r["Speed1sMps"] == "" && r["InstantSpeedMps"] == ""),
                "unobserved gaps do not fabricate motion");
            var streams = new[] { Point(0, 0), Point(1, 1.6) with { SourceFile = "other.txt" } };
            Check(Points(service.Save(root, analysis, streams, [], empty, request).Directory).All(r => r["Speed1sMps"] == ""),
                "motion never crosses source streams");
            var invalidReport = service.Save(root, analysis, [invalid], [], empty, request);
            Check(Points(invalidReport.Directory).Single()["Observations"].Contains("INVALID_COORDINATE") &&
                Points(invalidReport.Directory).Single()["Speed1sMps"] == "", "invalid coordinates survive as missing evidence");
            var staticSamples = Enumerable.Range(0, 4).Select(i => Point(i, 0.001 * i, seconds: i)).Select(s => s with
            { SourceFile = "gps-static.csv", AltitudeM = 100 + s.SourceRecord * .1 }).ToArray();
            var staticAnalysis = new FieldMagFolderAnalysis { Fingerprint = "static", UavSamples = staticSamples };
            var staticReport = service.Save(root, staticAnalysis, [], [], empty, request,
                qualityOptions: new FlightQualityAssessmentOptions(StaticTestSourceFiles: ["gps-static.csv"],
                    StaticTestStartUtc: start, StaticTestEndUtc: start.AddSeconds(4)));
            using (var summary = JsonDocument.Parse(File.ReadAllText(Path.Combine(staticReport.Directory, "summary.json"))))
            {
                var gps = summary.RootElement.GetProperty("FlightQualityIndicators").GetProperty("GpsStaticPositioning");
                Check(gps.GetProperty("Status").GetString() == "AVAILABLE" && gps.GetProperty("SampleCount").GetInt32() == 4 &&
                    Math.Abs(gps.GetProperty("PlanePrecisionM").GetDouble() - Math.Sqrt(5d / 3) * .001) < 1e-7 &&
                    Math.Abs(gps.GetProperty("VerticalPrecisionM").GetDouble() - Math.Sqrt(5d / 3) * .1) < 1e-7,
                    "explicit static GPS test window computes plane and vertical sample precision");
            }
            // Parameter and evidence boundaries cannot silently turn unavailable evidence into a pass.
            var indicatorsService = new FlightQualityIndicatorsService();
            var noOptions = indicatorsService.Build(analysis.UavSamples, samples, [assigned, nearer], execution, new());
            Check(noOptions.StaticSamples.Count == 0 && noOptions.StaticTests.Single().PlanePrecisionM is null &&
                noOptions.Lines.Single().MeanOffsetThresholdM is null, "no inferred spacing or implicit static flight population");
            var aglSamples = samples.Select(s => s with { LaserAltitudeM = 80 }).ToArray();
            var unknownHeight = indicatorsService.Build(aglSamples, aglSamples, [assigned], execution, new(DesignedFlightHeightM: 80));
            Check(unknownHeight.Lines.Single().HeightErrorMeanM is null && unknownHeight.Lines.Single().MeanAboveGroundHeightM is null,
                "unconfirmed laser reference cannot become an AGL pass");
            var confirmedHeight = indicatorsService.Build(aglSamples, aglSamples, [assigned], execution,
                new(DesignedFlightHeightM: 80, LaserAltitudeIsAboveGround: true));
            Check(confirmedHeight.Lines.Single().MeanAboveGroundHeightM == 80 &&
                confirmedHeight.Lines.Single().HeightErrorP95M == 0 && confirmedHeight.Lines.Single().HeightDesignCheck == "P95_WITHIN_DESIGN_TOLERANCE",
                "known confirmed AGL height is compared to design");
            var zeroHeight = aglSamples.Select(s => s with { LaserAltitudeM = 0 }).ToArray();
            Check(indicatorsService.Build(zeroHeight, zeroHeight, [assigned], execution, new(LaserAltitudeIsAboveGround: true))
                .Lines.Single().AltitudePoints == 0, "zero sensor placeholders do not become valid AGL evidence");
            var repeatSamples = Enumerable.Range(0, 3).Select(i => Point(10000 + i, i * 1.6, 20, 100 + i * .2)).ToArray();
            var repeatLeg = execution.Legs.Single() with { FlightLegId = "repeat", PointCount = 3,
                Samples = repeatSamples.Select(s => new FlightLegSample(s.SourceFile, s.SourceRecord, s.TimestampUtc, s.Longitude!.Value, s.Latitude!.Value)).ToArray() };
            var repeated = execution with { Legs = execution.Legs.Append(repeatLeg).ToArray(),
                Passes = execution.Passes.Append(execution.Passes.Single() with { FlightLegId = "repeat", PassId = "repeat/pass" }).ToArray() };
            var repeatedReport = indicatorsService.Build(samples.Concat(repeatSamples).ToArray(), samples.Concat(repeatSamples).ToArray(),
                [assigned, zero], repeated, new(LineSpacingM: 20));
            Check(repeatedReport.Lines.Count == 2 && repeatedReport.TaskLines.Count == 2 &&
                repeatedReport.TaskLines.Single(l => l.TaskId == "assigned").ValidOffsetPoints == 129 &&
                Math.Abs(repeatedReport.TaskLines.Single(l => l.TaskId == "assigned").MeanOffsetM!.Value - 1320d / 129) < .01 &&
                repeatedReport.TaskLines.Single(l => l.TaskId == "zero").MeanOffsetM is null,
                "repeated tasks aggregate by sample count and unmatched tasks retain missing indicators");
            var serializedRequest = JsonSerializer.Serialize(request with { FlightQualityOptions = new(20, 80,
                StaticTestSourceFiles: ["gps-static.csv"], StaticTestStartUtc: start, StaticTestEndUtc: start.AddSeconds(3)) });
            Check(JsonSerializer.Deserialize<AuditProductionRequest>(serializedRequest)!.FlightQualityOptions!.StaticTestSourceFiles!.Single() == "gps-static.csv",
                "assessment settings and static source selection roundtrip with review input");
            var noData = service.Save(root, analysis, [], [], empty, request);
            Check(Points(noData.Directory).Count == 0 && File.Exists(Path.Combine(noData.Directory, "manifest.json")), "empty screening can be reported");

            var reverse = samples.Select((s, i) => s with { Longitude = (200 - i * 1.6) * metre }).ToArray();
            var reverseLegs = execution.Legs.Select(l => l with { Samples = reverse.Select(s => new FlightLegSample(
                s.SourceFile, s.SourceRecord, s.TimestampUtc, s.Longitude!.Value, s.Latitude!.Value, s.AltitudeM)).ToArray() }).ToArray();
            var forwardBasis = execution with { Legs = reverseLegs, Passes = execution.Passes.Select(p => p with { ExecutionDirection = "FORWARD" }).ToArray() };
            Check(Points(service.Save(root, analysis, reverse, [assigned], forwardBasis, request).Directory)
                .All(r => Number(r["DirectedOrAxisErrorDeg"]) > 179), "180 degree reversal cannot collapse to an unsigned zero angle");
            var missingGeometry = service.Save(root, analysis, samples, [], execution, request);
            Check(Points(missingGeometry.Directory).All(r => r["ReferenceTaskId"] == ""), "missing assigned geometry is reported without null-reference failure");
            try { service.Save(root, analysis, [samples[0], samples[0]], [], empty, request); throw new Exception("duplicate identity accepted"); }
            catch (InvalidDataException) { }
            try { service.Save(root, analysis, samples, [assigned], execution with { Legs = execution.Legs.Concat(execution.Legs).ToArray() }, request);
                throw new Exception("duplicate leg membership accepted"); }
            catch (InvalidDataException) { }
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static List<Dictionary<string, string>> Points(string directory)
    {
        using var stream = File.OpenRead(Path.Combine(directory, "point-evidence.csv.gz"));
        using var gzip = new GZipStream(stream, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        return Csv(reader);
    }
    private static List<Dictionary<string, string>> Csv(string path)
    {
        using var reader = File.OpenText(path);
        return Csv(reader);
    }
    private static List<Dictionary<string, string>> Csv(TextReader reader)
    {
        using var parser = new TextFieldParser(reader) { HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = false };
        parser.SetDelimiters(",");
        string[] header = parser.ReadFields()!;
        var rows = new List<Dictionary<string, string>>();
        while (!parser.EndOfData)
        {
            var values = parser.ReadFields()!;
            Check(values.Length == header.Length, "CSV shape is stable");
            rows.Add(header.Zip(values).ToDictionary(p => p.First, p => p.Second));
        }
        return rows;
    }
    private static double Number(string value) => double.Parse(value, CultureInfo.InvariantCulture);
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
