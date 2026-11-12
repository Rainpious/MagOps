using MagOps.Core.Coordinates;
using MagOps.Core.Domain;
using MagOps.Infrastructure.Services;
using MagOps.Infrastructure.Storage;

internal static class SpatialAndTimeChecks
{
    private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    private static readonly DateTimeOffset Start = new(2026, 9, 20, 3, 0, 0, TimeSpan.Zero);
    private static UavMagCanonicalSample Sample(int record, double longitude, string source = "uav.txt", int seconds = 0) =>
        new(Start.AddSeconds(seconds == 0 ? record * 3 : seconds), longitude, 0, 100, null, 50000, null,
            "A", 10, "T", source, record, []);

    public static void Screening()
    {
        var range = new WorkareaSpatialRange("area-A", "fixture", new(CentralMeridian: 0), 500000, 0, 500200, 200, 500100, 100);
        Assert(range.DistanceSquared(500100, 100) == 0, "inside distance must be zero");
        Assert(range.DistanceSquared(520200, 200) == 400000000, "20 km edge threshold must be retained");
        Assert(range.DistanceSquared(500203, 204) == 25, "corner distance must use both axes");
        Assert(range.ContainsBuffered(501200, 1200, 1000), "expanded rectangle includes exact corner boundary");
        Assert(!range.ContainsBuffered(501200.001, 1200, 1000), "strictly outside expanded rectangle is excluded");
        var near = Enumerable.Range(0, 21).Select(i => Sample(i, i * 10d / 111319.49d)).ToArray();
        var far = Enumerable.Range(0, 21).Select(i => Sample(100 + i, 1 + i * 0.00001, seconds: 3600 + i * 3)).ToArray();
        var analysis = new FieldMagFolderAnalysis { Fingerprint = "spatial-v1", UavSamples = near.Concat(far).ToArray(),
            BaseSamples = [new(Start, 50000, null, "base-outside.txt", 0, [])] };
        var service = new WorkareaSpatialService();
        var input = new WorkareaScreeningInput(range, new());
        var result = service.Screen(analysis, input);
        Assert(result.Groups.Count == 2 && result.Groups.Count(g => g.Excluded) == 1, "mixed file must be split by continuous session, not dropped as a file");
        Assert(result.IncludedSamples.Count == 21, "only near session survives full scan");
        Assert(WorkareaSpatialService.Apply(analysis, result).BaseSamples.Count == 1, "Base must not be spatially excluded");
        string remoteId = result.Groups.Single(g => g.Excluded).GroupId;
        input = input with { SourceFingerprint = analysis.Fingerprint, ContextFingerprint = result.ContextFingerprint,
            Decisions = [new(remoteId, "RESTORE", "operator")] };
        Assert(service.Screen(analysis, input).IncludedSamples.Count == 42, "explicit restoration must override automatic remote exclusion");
        var changed = service.Screen(analysis, input with { Options = new(30000, 1000) });
        Assert(changed.IncludedSamples.Count == 21 && changed.Warnings.Count > 0, "parameter change invalidates old restoration");
        var unknown = new FieldMagFolderAnalysis { Fingerprint = "unknown", UavSamples =
            far.Take(3).Select(p => p with { Longitude = null }).ToArray() };
        Assert(service.Screen(unknown, new(range, new())).IncludedSamples.Count == 3, "missing coordinates must remain manually reviewable");
        var mixed = new FieldMagFolderAnalysis { Fingerprint = "mixed", UavSamples = far.Take(3).Select((p, i) =>
            i == 1 ? p with { Longitude = 0.001 } : p).ToArray() };
        Assert(service.Screen(mixed, new(range, new())).IncludedSamples.Count == 3, "one reliable near point must retain a mixed continuous segment");
        var tasks = new[] { new PlannedSurveyLineTask(1, 1, "L1", "A1", "L1", new(0, 0), new(200d / 111319.49d, 0), 200)
            { SurveyTaskStableId = "task-L1" } };
        input = new(range, new()) { SourceFingerprint = analysis.Fingerprint, RouteFingerprint = "route-v1" };
        input = input with { ContextFingerprint = WorkareaSpatialService.ContextFingerprint(input),
            ExcludedSamples = [new("uav.txt", 10)] };
        result = service.Screen(analysis, input);
        var request = new AuditProductionRequest(analysis.Fingerprint, [], [], [], true)
        { RouteFingerprint = "route-v1", SpatialScreening = input, ScreeningFingerprint = result.Fingerprint, IncludeUnconfirmedSamples = true };
        var audit = new AuditProductionService().Analyze(analysis, tasks, request, "route-v1");
        Assert(audit.SelectedSamples.Count == 20 && !audit.SelectedSamples.Any(p => p.SourceRecord == 10 || p.SourceRecord >= 100),
            "spatial exclusion must beat blanket draft inclusion and individual sample exclusion");
        Assert(!new AuditProductionService().Analyze(analysis, tasks, request with { ScreeningFingerprint = "old" }, "route-v1")
            .Execution.Passes.Any(p => p.ReviewConfirmed && request.RequirePassConfirmation), "stale screening must not authorize old confirmations");
    }

    public static void Suggestions()
    {
        var tasks = new[] { new PlannedSurveyLineTask(1, 1, "L1", "A1", "L1", new(0, 0), new(200d / 111319.49d, 0), 200)
            { SurveyTaskStableId = "task-L1" } };
        var samples = Enumerable.Range(0, 21).Select(i => Sample(i, i * 10d / 111319.49d, "drone-A.txt")).ToArray();
        var windows = new FlightWindowInferenceService().Infer(samples).Candidates.Select(c => c.Window).ToArray();
        var suggested = new MissionTaskSuggestionService().Suggest(samples, tasks, windows);
        Assert(suggested.Single().SurveyTaskStableIds.SequenceEqual(new[] { "task-L1" }), "automatic window must contain geometric task suggestion");
        Assert(new MissionTaskSuggestionService().Suggest(samples, tasks,
            [windows[0] with { TaskScopeSource = "MANUAL", SurveyTaskStableIds = [] }]).Single().SurveyTaskStableIds.Count == 0,
            "manual scope must survive automatic reanalysis");
        var other = samples.Select(p => p with { SourceFile = "drone-B.txt", Longitude = p.Longitude + 1 }).ToArray();
        var parallel = new FlightWindowInferenceService().Infer(samples.Concat(other).ToArray()).Candidates.Select(c => c.Window).ToArray();
        Assert(parallel.Length == 2 && parallel.All(w => w.SourceFiles.Count == 1), "simultaneous sources must not form one interleaved flight");
        var parallelSuggestions = new MissionTaskSuggestionService().Suggest(samples.Concat(other).ToArray(), tasks, parallel);
        var points = samples.Concat(other).Select(p => new SurveyTrackPoint(p.TimestampUtc, new(p.Longitude!.Value, p.Latitude!.Value), p.SourceFile, p.SourceRecord));
        var execution = new WholeLegMatcherService().Match(tasks, points, parallelSuggestions);
        Assert(execution.Passes.Single().SourceFiles.SequenceEqual(new[] { "drone-A.txt" }), "time-overlapping missions must match only their own source");
        Assert(parallelSuggestions.Single(w => w.SourceFiles.Contains("drone-B.txt")).SurveyTaskStableIds.Count == 0,
            "no geometry evidence must not authorize all tasks");
    }

    public static void TimeZones()
    {
        var instant = new DateTimeOffset(2026, 9, 20, 16, 30, 0, TimeSpan.Zero);
        var raw = new FieldMagFolderAnalysis { Fingerprint = "utc-facts", UavSamples = [Sample(0, 0) with { TimestampUtc = instant }] };
        Assert(FieldMagDataService.BuildProjectDateSummaries(raw).Single().BeijingDate == new DateTime(2026, 9, 21), "Beijing business day crosses UTC midnight");
        var utc = WorkareaSpatialService.WithTimeZone(raw, "UTC");
        Assert(FieldMagDataService.BuildProjectDateSummaries(utc).Single().BeijingDate == new DateTime(2026, 9, 20), "project UTC business day differs from Beijing");
        Assert(FieldMagDataService.BuildDateSummaries(raw).Single().UtcDate == FieldMagDataService.BuildDateSummaries(utc).Single().UtcDate,
            "project display time zone must not change scientific UTC grouping");
        Assert(FieldMagDataService.SliceByProjectDate(utc, new DateTime(2026, 9, 20)).UavSamples.Single().TimestampUtc == instant,
            "business-day slice retains absolute timestamp");
        var ny = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        Assert((ProjectTime.DayStartUtc(new(2026, 3, 9), ny) - ProjectTime.DayStartUtc(new(2026, 3, 8), ny)).TotalHours == 23,
            "business-day boundary must honor DST instead of adding 24 hours");
        try { ProjectTime.ToUtc(new(2026, 3, 8, 2, 30, 0), ny); throw new Exception("invalid DST time accepted"); }
        catch (FormatException) { }
        try { ProjectTime.ToUtc(new(2026, 11, 1, 1, 30, 0), ny); throw new Exception("ambiguous DST time accepted"); }
        catch (FormatException) { }
        string folder = Directory.CreateTempSubdirectory("MagOps-spatial-time-").FullName;
        try
        {
            var service = new WorkspaceService(new JsonStore()); service.Initialize(folder);
            Assert(service.GetWorkspace(folder).TimeZoneId == "Asia/Shanghai", "new project defaults to Beijing");
            service.SetProjectTimeZone(folder, "UTC");
            Assert(service.GetWorkspace(folder).TimeZoneId == "UTC", "project time zone must persist");
            var record = new FieldDailyProgressRecord { Date = "2026-09-20", ProgressTimeZone = "UTC" };
            string report = FieldworkDocumentExportService.BuildProjectProgressPreview([record], timeZoneBasis: "UTC");
            Assert(report.Contains("2026-09-20") && !report.Contains("北京时间"), "non-Beijing report must use its own business-day basis");
        }
        finally
        {
            if (Path.GetFullPath(folder).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
                Directory.Delete(folder, true);
        }
    }
}
