using System.Text.Json;
using MagOps.Application;
using MagOps.Infrastructure.Services;

internal static class ReviewSessionChecks
{
    internal static void Run()
    {
        var decisions = new ReviewDecisionState();
        var session = new ReviewAnalysisSession(decisions);
        var analysis = new FieldMagFolderAnalysis { Fingerprint = "session-fixture", UavSamples = [] };
        var windows = new List<MissionTimeWindow>();
        var request = new AuditProductionRequest(analysis.Fingerprint, windows, [], [], true)
            { RouteFingerprint = "route", DefaultMatchedSelectionsApplied = true };
        request = request with { RecognitionVersion = AuditProductionService.RecognitionVersion(request) };
        var first = session.Prepare(analysis, [], request, "route", false);
        var result = session.Calculate(first);
        if (first.Plan != ReviewRecalculation.Recognition || !result.IsCurrent || session.Service.RecognitionRuns != 1)
            throw new InvalidOperationException("Initial session must run recognition");
        var same = session.Prepare(analysis, [], request, "route", false);
        var sameResult = session.Calculate(same);
        if (same.Plan != ReviewRecalculation.ReuseCurrent || !ReferenceEquals(result.Result, sameResult.Result) ||
            session.Service.RecognitionRuns != 1 || session.Service.RecognitionReuses != 0)
            throw new InvalidOperationException("Identical request must avoid selection recomputation");
        var quality = session.Prepare(analysis, [], request with { FlightQualityOptions = new(20, 100) }, "route", false);
        var qualityResult = session.Calculate(quality);
        if (quality.Plan != ReviewRecalculation.SelectionOverlay || !qualityResult.IsCurrent || session.Service.RecognitionRuns != 1)
            throw new InvalidOperationException("Report/decision changes must keep valid recognition");
        var force = session.Prepare(analysis, [], request, "route", true);
        session.Calculate(force);
        if (force.Plan != ReviewRecalculation.Recognition || session.Service.RecognitionRuns != 2)
            throw new InvalidOperationException("Explicit reanalysis must force recognition");
        var stale = session.Prepare(analysis, [], request, "route", false);
        decisions.Set(new("edited", Included: true));
        if (session.Calculate(stale).IsCurrent) throw new InvalidOperationException("Old result overwrites newer decisions");
        var superseded = session.Prepare(analysis, [], request, "route", false);
        var latest = session.Prepare(analysis, [], request with { ProjectCentralMeridian = 90 }, "route", false);
        if (session.Calculate(superseded).IsCurrent || latest.Plan != ReviewRecalculation.Recognition)
            throw new InvalidOperationException("Context change must invalidate old results and recognition");
        session.Invalidate();
        if (session.Calculate(latest).IsCurrent) throw new InvalidOperationException("Invalidated session result committed");
        var captured = session.Prepare(analysis, [], request, "route", true);
        windows.Add(new("later", "", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddSeconds(10)));
        var isolated = session.Calculate(captured);
        if (isolated.Result.Execution.Missions.Count != 0) throw new InvalidOperationException("Worker request aliases mutable UI windows");
        var production = session.AnalyzeForProduction(analysis, [], request with { MissionWindows = [] }, "route");
        var full = new AuditProductionService().Analyze(analysis, [], request with { MissionWindows = [] }, "route");
        if (JsonSerializer.Serialize(production) != JsonSerializer.Serialize(full))
            throw new InvalidOperationException("Serial session production differs from independent full computation");
        var failed = session.Prepare(analysis, [], request with
        { SpatialScreening = new(null, new(BufferM: -1)) }, "route", false);
        try { session.Calculate(failed); throw new InvalidOperationException("Invalid screening accepted"); }
        catch (ArgumentException) { }
        var retry = session.Prepare(analysis, [], request with { MissionWindows = [] }, "route", false);
        if (!session.Calculate(retry).IsCurrent) throw new InvalidOperationException("Failed calculation blocks session retry");
    }
}
