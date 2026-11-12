using System.Text.Json;
using MagOps.Core.Domain;
using MagOps.Infrastructure.Services;

namespace MagOps.SmokeTests;

internal static class KmlContinuationChecks
{
    public static void Run()
    {
        const double metre = 1 / 111319.49;
        var time = new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.Zero);
        void Check(bool condition, string why) { if (!condition) throw new InvalidOperationException(why); }
        GeoCoordinate P(double x, double y = 0) => new(x * metre, y * metre);
        var line = new SegmentationReferenceGeometry("mission-line", "K1", P(0), P(800), "SURVEY", "KML")
        { SourcePath = "mission.kml", SourceHash = "hash", PathId = "route", PathOrder = 0 };
        var next = line with { Id = "next", Start = P(800, 20), End = P(0, 20), PathOrder = 2 };
        var connector = line with { Id = "connector", Kind = "REVIEW", Start = P(800), End = P(800, 20), PathOrder = 1 };
        var duplicate = line with { Id = "height-version", SourcePath = "height.kml", SourceHash = "height", CoordinateCount = 200,
            Start = P(2, 1), End = P(798, 1), PathId = "other" };
        var consolidated = TrackSegmentationReferenceService.Consolidate([duplicate, line, next, connector]);
        Check(consolidated.Count(g => g.Kind == "SURVEY") == 2, "20m neighbours remain separate KML identities");
        Check(consolidated.Single(g => g.Id == line.Id).Versions.Count == 2, "dense version provenance preserved");
        Check(consolidated.Single(g => g.Id == "connector").Kind == "CONNECTOR" &&
            consolidated.Single(g => g.Id == "connector").AdjacentSurveyIds.Count == 2, "20m connector uses path topology");
        var again = TrackSegmentationReferenceService.Consolidate(consolidated);
        Check(again.Single(g => g.Id == line.Id).Versions.Count == 2, "consolidation replay preserves all versions");
        Check(JsonSerializer.Deserialize<TrackSegmentationInput>(JsonSerializer.Serialize(new TrackSegmentationInput
        { ReferenceGeometry = consolidated }))!.ReferenceGeometry.Single(g => g.Id == line.Id).Versions.Count == 2, "immutable reference replay");

        SurveyTrackPoint[] Run(string source, int second, int first, int last) => Enumerable.Range(first, last - first + 1)
            .Select(i => new SurveyTrackPoint(time.AddSeconds(second + (i - first)), P(i * 8), source, i)).ToArray();
        var first = Run("first.uav", 0, 0, 50);
        var second = Run("second.uav", 200, 50, 100);
        var windows = new[] { new MissionTimeWindow("first", "", time, time.AddSeconds(55), "first") { TaskScopeSource = "AUTO" },
            new MissionTimeWindow("second", "", time.AddSeconds(200), time.AddSeconds(255), "second") { TaskScopeSource = "AUTO" } };
        var input = new TrackSegmentationInput { Enabled = true, AxisDegrees = 90, ReferenceAuthority = "KML_AUTHORITATIVE", ReferenceGeometry = [line] };
        var result = new TrackSegmentationService().Recognize([], first.Concat(second).ToArray(), windows, input);
        Check(result.Execution.Passes.Count == 2 && result.Execution.Passes.All(p => p.SurveyTaskStableId == line.Id), "interrupted/resumed flights share one independent identity");
        var total = TaskTrackCoverageService.Summarize(result.Tasks, result.Execution.Passes).Single();
        Check(total.Ratio > .99 && total.PassCount == 2, "coverage union of two flights completes one task");
        Check(result.Execution.Legs.Sum(l => l.Samples.Count) == first.Length + second.Length, "continuation preserves every sample");
        var onlyFirst = TaskTrackCoverageService.Summarize(result.Tasks, [result.Execution.Passes[0]], "SELECTED").Single();
        Check(onlyFirst.Ratio < .6 && onlyFirst.LargestGapM > 300, "unselected resumption cannot complete selected output");
        var gapFirst = Run("gap.uav", 0, 0, 49).Concat(Run("gap.uav", 52, 52, 100)).ToArray();
        var gapLeg = new FlightLeg("gap", "first", time, time.AddSeconds(100), "SURVEY", line.Id, gapFirst.Length, "")
        { Samples = gapFirst.Select(p => new FlightLegSample(p.SourceFile, p.SourceRecord, p.TimestampUtc,
            p.Coordinate.Longitude, p.Coordinate.Latitude)).ToArray() };
        var task = result.Tasks.Single();
        var gapCoverage = ObservedTrackCoverageService.Calculate(gapLeg.Samples, task, new());
        var gapPass = WholeLegMatcherService.CreateManualAssignmentPass(gapLeg, task)!;
        Check(gapCoverage.Count == 2 && TaskTrackCoverageService.Summarize([task], [gapPass]).Single().LargestGapM >= 15,
            "24m missing samples retain an internal gap rather than a fixed10m halo");
        var coarse = gapLeg.Samples.SelectMany(p => Enumerable.Range(0, 10).Select(j => p with { SourceRecord = p.SourceRecord * 10 + j })).ToArray();
        Check(Math.Abs(ObservedTrackCoverageService.Calculate(coarse, task, new()).Sum(i => i.End - i.Start) -
            gapCoverage.Sum(i => i.End - i.Start)) < 1e-8, "coarse repeated UTC coverage is independent of magnetic sample count");
        var shortRun = Run("short.uav", 0, 90, 95);
        var shortResult = new TrackSegmentationService().Recognize([], shortRun, [windows[0]], input);
        Check(shortResult.Execution.Passes.Count == 1, "40m internal resumption keeps KML task identity");
        var reverse = first.Reverse().Select((p, i) => p with { TimestampUtc = time.AddSeconds(i), SourceRecord = i, SourceFile = "reverse.uav" }).ToArray();
        var reverseResult = new TrackSegmentationService().Recognize([], reverse, [windows[0]], input);
        Check(reverseResult.Execution.Passes.Single().ExecutionDirection == "REVERSE", "reverse resurvey identity and directed pass remain separate");
        var outside = first.Select(p => p with { Coordinate = P(p.SourceRecord * 8, 100) }).ToArray();
        var outsideResult = new TrackSegmentationService().Recognize([], outside, [windows[0]], input);
        Check(outsideResult.Tasks.Count == 1 && outsideResult.Execution.Passes.Count == 0 &&
            outsideResult.Execution.Legs.Sum(l => l.Samples.Count) == outside.Length, "unmatched straight return cannot invent KML task");
    }
}
