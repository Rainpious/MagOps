using System.Text.Json;
using MagOps.Core.Domain;
using MagOps.Infrastructure.Services;

namespace MagOps.SmokeTests;

internal static partial class Program
{
    private static void RunTrackOnlySegmentationChecks()
    {
        SegmentationProfileChecks.Run();
        const double metre = 1d / 111320;
        var start = new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.Zero);
        var points = new List<SurveyTrackPoint>();
        int record = 0;
        void Run(int seconds, double y, bool reverse, double x0 = 0)
        {
            for (int i = 0; i <= 200; i++) points.Add(new(start.AddSeconds(seconds).AddTicks(i * 1_000_000L),
                new((x0 + (reverse ? 160 - i * .8 : i * .8)) * metre, y * metre), "raw.uav", ++record));
        }
        Run(0, 0, false); Run(35, 20, true); Run(70, 40, false); Run(105, 0, true); Run(140, 0, false, 300);
        // Short cross-axis connector and a point outside the mission are never lost.
        for (int i = 0; i <= 50; i++) points.Add(new(start.AddSeconds(170).AddTicks(i * 1_000_000L), new(160 * metre, (40 - i * .8) * metre), "raw.uav", ++record));
        points.Add(new(start.AddSeconds(190), new(0, 0), "raw.uav", ++record));
        var windows = new[] { new MissionTimeWindow("flight-a", "", start, start.AddSeconds(180), "flight-a") { TaskScopeSource = "AUTO" } };
        var input = new TrackSegmentationInput { Enabled = true };
        var result = new TrackSegmentationService().Recognize([], points, windows, input);
        Equal(4, result.Tasks.Count);
        Equal(5, result.Execution.Passes.Count);
        Equal(1, result.Execution.Passes.GroupBy(p => p.SurveyTaskStableId).Count(g => g.Count() == 2));
        Equal(2, result.Execution.Passes.Count(p => p.ExecutionDirection == "REVERSE"));
        var retained = result.Execution.Legs.SelectMany(l => l.Samples).ToArray();
        Equal(points.Count, retained.Length);
        Equal(points.Count, retained.Select(p => (p.SourceFile, p.SourceRecord)).Distinct().Count());
        var coarseTime = points.Select(p => p with { TimestampUtc = new DateTimeOffset(p.TimestampUtc.Ticks - p.TimestampUtc.Ticks % TimeSpan.TicksPerSecond, TimeSpan.Zero) }).ToArray();
        var coarseResult = new TrackSegmentationService().Recognize([], coarseTime, windows, input);
        Equal(result.Execution.Passes.Count, coarseResult.Execution.Passes.Count);
        Equal(points.Count, coarseResult.Execution.Legs.Sum(l => l.PointCount));
        True(coarseResult.Execution.Legs.Count < 30, "Second-resolution UTC must not fragment every magnetic record into a leg.");
        True(result.Execution.Legs.Any(l => l.SurveyTaskStableId is null && l.PointCount > 10), "Connector lost or promoted.");
        var confirmedAxis = new TrackSegmentationService().Recognize([], points, windows, input with { AxisDegrees = 90 });
        Equal(result.Execution.Passes.Count, confirmedAxis.Execution.Passes.Count);
        var oblique = Enumerable.Range(0, 201).Select(i => new SurveyTrackPoint(start.AddSeconds(200).AddTicks(i * 1_000_000L),
            new(i * .8 * metre, (200 + i * .8) * metre), "oblique.uav", i)).ToArray();
        var mixed = points.Concat(oblique).ToArray();
        var axisWindows = new[] { windows[0] with { EndUtc = start.AddSeconds(225) } };
        var axisInput = input with { AxisDegrees = 90 };
        Equal(12d, axisInput.RecognitionOptions.AxisToleranceDegrees);
        var tilted = Enumerable.Range(0, 201).Select(i => new SurveyTrackPoint(start.AddTicks(i * 1_000_000L),
            new(i * .8 * Math.Cos(25 * Math.PI / 180) * metre, i * .8 * Math.Sin(25 * Math.PI / 180) * metre), "tilted.uav", i)).ToArray();
        var strictTilt = new TrackSegmentationService().Recognize([], tilted, windows, axisInput);
        var widerInput = axisInput with { RecognitionOptions = axisInput.RecognitionOptions with { AxisToleranceDegrees = 30 } };
        var widerTilt = new TrackSegmentationService().Recognize([], tilted, windows, widerInput);
        Equal(0, strictTilt.Execution.Passes.Count);
        Equal(1, widerTilt.Execution.Passes.Count);
        True(TrackSegmentationReferenceService.Fingerprint(axisInput) != TrackSegmentationReferenceService.Fingerprint(widerInput), "Threshold changes must invalidate old decisions.");
        Equal(30d, JsonSerializer.Deserialize<TrackSegmentationInput>(JsonSerializer.Serialize(widerInput))!.RecognitionOptions.AxisToleranceDegrees);
        Throws<InvalidDataException>(() => (new TrackRecognitionOptions() with { AxisToleranceDegrees = 90 }).Validate());
        var automaticMixed = new TrackSegmentationService().Recognize([], mixed, axisWindows, input);
        var constrained = new TrackSegmentationService().Recognize([], mixed, axisWindows, axisInput);
        True(automaticMixed.Execution.Passes.Any(p => p.SourceFiles.Contains("oblique.uav")), "Fixture lacks oblique identity.");
        Equal(5, constrained.Execution.Passes.Count);
        True(constrained.Execution.Passes.All(p => !p.SourceFiles.Contains("oblique.uav")), "Explicit axis ignored for actual inference.");
        True(constrained.Execution.Legs.Any(l => l.ReviewReason == "EXPLICIT_AXIS_MISMATCH_REVIEW"), "Off-axis evidence lost.");
        Equal(mixed.Length, constrained.Execution.Legs.Sum(l => l.PointCount));
        var combinedAxis = new TrackSegmentationService().Recognize(automaticMixed.Tasks.Select(t => t with { GeometryOrigin = "" }).ToArray(),
            mixed, axisWindows, axisInput with { UseDesignTasks = true });
        True(combinedAxis.Execution.Passes.All(p => !p.SourceFiles.Contains("oblique.uav")), "Explicit axis ignored by design path.");
        Equal(mixed.Length, combinedAxis.Execution.Legs.SelectMany(l => l.Samples).Select(s => (s.SourceFile, s.SourceRecord)).Distinct().Count());
        var tie = new SegmentationReferenceGeometry("explicit-tie", "联络测线", oblique[0].Coordinate, oblique[^1].Coordinate, "TIE", "人工确认联络测线");
        var withTie = new TrackSegmentationService().Recognize([], mixed, axisWindows, axisInput with { ReferenceGeometry = [tie] });
        True(withTie.Execution.Passes.Any(p => p.SurveyTaskStableId == tie.Id), "Explicit tie line suppressed by main axis.");
        var analysis = new FieldMagFolderAnalysis { Fingerprint = "actual-fixture", UavSamples = points.Select(p =>
            new UavMagCanonicalSample(p.TimestampUtc, p.Coordinate.Longitude, p.Coordinate.Latitude, 100, 40, 50000, 20, "3D", 15, "T", p.SourceFile, p.SourceRecord, [])).ToArray() };
        var request = new AuditProductionRequest(analysis.Fingerprint, windows, [], [], true)
        { TrackSegmentation = input, TrackSegmentationFingerprint = TrackSegmentationReferenceService.Fingerprint(input), RequirePassConfirmation = true };
        request = request with { RecognitionVersion = AuditProductionService.RecognitionVersion(request) };
        var audit = new AuditProductionService().Analyze(analysis, [], request);
        Equal(0, audit.SelectedSamples.Count);
        var repeatedId = audit.Execution.Passes.GroupBy(p => p.SurveyTaskStableId).Single(g => g.Count() == 2).Key;
        var reverse = audit.Execution.Passes.First(p => p.ExecutionDirection == "REVERSE" && p.SurveyTaskStableId == repeatedId);
        request = request with { ConfirmedPassIds = [reverse.PassId], ManualIncludedFlightLegIds = [reverse.FlightLegId] };
        var restored = JsonSerializer.Deserialize<AuditProductionRequest>(JsonSerializer.Serialize(request))!;
        var production = new AuditProductionService().Analyze(analysis, [], restored);
        Equal(201, production.SelectedSamples.Count);
        True(production.Execution.Passes.All(p => p.QualityStatus == "PENDING_QC"), "Inclusion must not grant QC PASS.");
        var csvSelection = MagneticSurveyLineSegmentationService.FromAudit(analysis.UavSamples, production.EffectiveTasks!, production);
        True(csvSelection.Assignments.Where(p => p.IncludeInProcessing).All(p => p.LineName.StartsWith("r") && !p.LineName.StartsWith("r0") && p.LinePass.HasValue), "Actual export identity lost.");
        var changed = new AuditProductionService().Analyze(analysis, [], restored with { TrackSegmentation = input with { AxisDegrees = 90 } });
        Equal(0, changed.SelectedSamples.Count);
        var scoped = new TrackSegmentationService().Recognize([], points,
            [windows[0] with { TaskScopeSource = "MANUAL", SurveyTaskStableIds = [reverse.SurveyTaskStableId] }], input);
        Equal(2, scoped.Execution.Passes.Count);
        Equal(points.Count, scoped.Execution.Legs.Sum(l => l.PointCount));
        var stationary = Enumerable.Range(0, 41).Select(i => new SurveyTrackPoint(start.AddTicks(i * 1_000_000L), new(0, 0), "hover.uav", i)
            { AltitudeM = i * .4 }).Concat(Enumerable.Range(1, 200).Select(i => new SurveyTrackPoint(start.AddSeconds(4).AddTicks(i * 1_000_000L),
                new(i * .8 * metre, 0), "hover.uav", 40 + i) { AltitudeM = 16 })).ToArray();
        var hoverResult = new TrackSegmentationService().Recognize([], stationary, [windows[0]], input);
        Equal(stationary.Length, hoverResult.Execution.Legs.Sum(l => l.PointCount));
        Equal(1, hoverResult.Execution.Passes.Count);
        True(hoverResult.Execution.Passes.Single().DurationSec <= 20.1, "Vertical takeoff/hover included in survey.");
        var changingMotion = new List<SurveyTrackPoint>();
        double cx = 0, cy = 0;
        for (int i = 0; i < 1000; i++)
        {
            double angle = (i / 10) * 50 * Math.PI / 180;
            double step = i % 80 < 25 ? .05 : .8;
            cx += Math.Sin(angle) * step; cy += Math.Cos(angle) * step;
            changingMotion.Add(new(start.AddTicks(i * 1_000_000L), new(cx * metre, cy * metre), "changing-motion.uav", i));
        }
        var mixedMotion = new TrackSegmentationService().Recognize([], changingMotion, [windows[0]], input);
        Equal(changingMotion.Count, mixedMotion.Execution.Legs.Sum(l => l.PointCount));
        Equal(changingMotion.Count, mixedMotion.Execution.Legs.SelectMany(l => l.Samples).Select(s => (s.SourceFile, s.SourceRecord)).Distinct().Count());

        string directory = Path.Combine(Path.GetTempPath(), "MagOps_track_reference_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string kml = Path.Combine(directory, "mixed.kml");
            File.WriteAllText(kml, "<kml xmlns='http://www.opengis.net/kml/2.2'><Document><Placemark><name>route</name><LineString><coordinates>0,0 0.002,0 0.002,0.00018 0,0.00018 0,0.00036 0.002,0.00036</coordinates></LineString></Placemark><Placemark><name>联络测线</name><LineString><coordinates>0.001,-0.001 0.001,0.001</coordinates></LineString></Placemark></Document></kml>");
            var imported = TrackSegmentationReferenceService.Import(kml);
            Equal(3, imported.ReferenceGeometry.Count(g => g.Kind == "SURVEY"));
            Equal(2, imported.ReferenceGeometry.Count(g => g.Kind == "CONNECTOR"));
            Equal(1, imported.ReferenceGeometry.Count(g => g.Kind == "TIE"));
            Equal("KML_AUTHORITATIVE", imported.ReferenceAuthority);
            True(imported.ReferenceGeometry.All(g => g.SourcePath.EndsWith("mixed.kml", StringComparison.OrdinalIgnoreCase)), "KML provenance missing");
            var duplicate = imported.ReferenceGeometry.First(g => g.Kind == "SURVEY") with
            { Id = "duplicate", Name = "duplicate", Evidence = "second KML", SourcePath = "height.kml", SourceHash = "height", Versions = [] };
            var consolidated = TrackSegmentationReferenceService.Consolidate(imported.ReferenceGeometry.Append(duplicate));
            Equal(imported.ReferenceGeometry.Count, consolidated.Count);
            True(consolidated.Any(g => g.Versions.Count >= 2), "reference version provenance missing");
            var formal = result.Tasks.Take(1).Select(t => t with { LineName = "正式测线", GeometryOrigin = "" }).ToArray();
            var referenceCopy = new SegmentationReferenceGeometry("same-line", "辅助副本", formal[0].Start, formal[0].End, "SURVEY", "副本");
            var combined = new TrackSegmentationService().Recognize(formal, points, windows, input with { UseDesignTasks = true, ReferenceGeometry = [referenceCopy] });
            True(combined.Tasks.Any(t => t.LineName == "正式测线") && combined.Tasks.All(t => t.SurveyTaskStableId != "same-line"), "Auxiliary reference replaced or duplicated formal identity.");
            string csv = Path.Combine(directory, "endpoints.csv");
            File.WriteAllText(csv, "Line,X,Y\nL1,0,0\nL1,0.002,0\nL2,0,0.00018\nL2,0.002,0.00018\n");
            Equal(2, TrackSegmentationReferenceService.Import(csv).ReferenceGeometry.Count);
            File.WriteAllText(csv, "Line,X,Y\nL1,500000,5200000\nL1,500200,5200000\n");
            Throws<InvalidDataException>(() => TrackSegmentationReferenceService.Import(csv));
            Equal(1, TrackSegmentationReferenceService.Import(csv, 111).ReferenceGeometry.Count);
            var connector = imported.ReferenceGeometry.Where(g => g.Kind == "SURVEY").OrderBy(g => g.Start.Latitude).First() with { Kind = "CONNECTOR" };
            var conflicts = new TrackSegmentationService().Recognize([], points, windows, input with { ReferenceGeometry = [connector], ReferenceAuthority = "ASSISTED" });
            True(conflicts.Execution.Legs.Any(l => l.ReviewReason.Contains("REFERENCE_CONNECTOR_CONFLICT")), "Connector conflict evidence lost: " + string.Join(" | ", conflicts.Execution.Legs.Select(l => l.ReviewReason)));
            Equal(points.Count, conflicts.Execution.Legs.Sum(l => l.PointCount));

            var authoritative = new TrackSegmentationInput { Enabled = true, ReferenceAuthority = "KML_AUTHORITATIVE",
                ReferenceGeometry = [new SegmentationReferenceGeometry("kml-line", "KML测线",
                    new(0, 0), new(.002, 0), "SURVEY", "KML") ] };
            var authoritativeResult = new TrackSegmentationService().Recognize([], points, windows, authoritative);
            True(authoritativeResult.Tasks.Any(t => t.SurveyTaskStableId == "kml-line"), "authoritative KML task missing");
            True(authoritativeResult.Execution.Legs.Any(l => l.SurveyTaskStableId is null &&
                l.ReviewReason.Contains("KML_REFERENCE_UNMATCHED_REVIEW")), "unmatched KML track was promoted to an invented identity");
            True(authoritativeResult.Execution.Warnings.Any(w => w.Contains("KML_AUTHORITATIVE_REFERENCE")), "authoritative KML warning missing");
        }
        finally { Directory.Delete(directory, true); }
    }
}
