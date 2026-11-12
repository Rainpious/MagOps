using System.Text.Json;
using MagOps.Core.Domain;
using MagOps.Infrastructure.Services;

namespace MagOps.SmokeTests;

internal static class PartitionProductionLineageChecks
{
    public static void Run()
    {
        var time = new DateTimeOffset(2026, 10, 21, 0, 0, 0, TimeSpan.Zero);
        var tasks = Enumerable.Range(1, 3).Select(i => new PlannedSurveyLineTask(i, i, "legacy-" + i,
            "sortie-" + i, "L1", new(0, 0), new(.001, 0), 111)
        {
            SurveyTaskStableId = "task-" + i, PartitionCode = "P0" + i,
            MotherLineTaskId = "same-legacy-name-hash",
            MotherLineStableId = i == 3 ? "" : "mother-shared",
            SurveyLineStableId = "line-" + i,
            ExecutionSegmentId = "L1.S" + i, ExecutionSegmentStableId = "segment-" + i,
            IsQualityRepeat = i == 2
        }).ToArray();
        FlightLeg Leg(string source, string? task) => new(source, "mission", time, time.AddSeconds(4),
            task is null ? "UNRESOLVED" : "SURVEY", task, 5, "")
        {
            Samples = Enumerable.Range(0, 5).Select(i => new FlightLegSample(source, i, time.AddSeconds(i),
                i * .00025, 0)).ToArray()
        };
        var legs = tasks.Select((t, i) => Leg("source-" + i, t.SurveyTaskStableId)).Append(Leg("unassigned", null)).ToArray();
        var passes = tasks.Select((t, i) => WholeLegMatcherService.CreateManualAssignmentPass(legs[i], t)!).ToArray();
        var samples = legs.SelectMany(l => l.Samples.Select(p => new UavMagCanonicalSample(p.TimestampUtc,
            p.Longitude, p.Latitude, 100, null, 50000, null, "A", 12, "T", p.SourceFile, p.SourceRecord, []))).ToArray();
        var execution = new SurveyExecutionResult("lineage-fixture", [], [], legs, passes, []);
        var selected = passes.SelectMany(p => p.Samples).Where(p => p.SourceRecord != 1)
            .Concat(legs[^1].Samples.Select(p => new ExecutionSampleReference(p.SourceFile, p.SourceRecord, 0))).ToArray();
        var audit = new AuditProductionResult("REVIEW_DRAFT", true, execution, [], selected, [])
            { ManualIncludedFlightLegIds = [legs[^1].FlightLegId] };
        string before = JsonSerializer.Serialize(new { tasks, samples, audit });
        var result = MagneticSurveyLineSegmentationService.FromAudit(samples, tasks, audit);
        var baseline = MagneticSurveyLineSegmentationService.FromAudit(samples, tasks.Select(ClearTask).ToArray(), audit);
        if (!result.Assignments.Select(ClearAssignment).SequenceEqual(baseline.Assignments))
            throw new Exception("Design lineage changed selection, line names, point order or pass splitting");
        if (result.Assignments.Count != samples.Length || result.Assignments.Count(r => r.IncludeInProcessing) != selected.Length)
            throw new Exception("Source conservation changed during lineage adaptation");
        for (int i = 0; i < tasks.Length; i++)
        {
            var task = tasks[i];
            var rows = result.Assignments.Where(a => a.SourceFile == "source-" + i).ToArray();
            if (rows.Length != 5 || rows.All(a => a.IncludeInProcessing) || rows.Any(a =>
                a.PartitionCode != task.PartitionCode || a.MotherLineStableId != task.MotherLineStableId ||
                a.MotherLineTaskId != task.MotherLineTaskId || a.SurveyLineStableId != task.SurveyLineStableId ||
                a.ExecutionSegmentId != task.ExecutionSegmentId || a.ExecutionSegmentStableId != task.ExecutionSegmentStableId ||
                a.IsQualityRepeat != task.IsQualityRepeat || a.SurveyTaskStableId != task.SurveyTaskStableId))
                throw new Exception("Child or excluded-row lineage lost; legacy mother key must never invent a stable identity");
        }
        if (result.Assignments.Where(a => a.SourceFile == "unassigned").Any(a =>
            a.PartitionCode.Length != 0 || a.MotherLineTaskId.Length != 0 || a.MotherLineStableId.Length != 0 ||
            a.SurveyLineStableId.Length != 0 || a.ExecutionSegmentStableId.Length != 0 || a.IsQualityRepeat))
            throw new Exception("Manual unassigned inclusion inherited a neighboring design identity");
        if (before != JsonSerializer.Serialize(new { tasks, samples, audit })) throw new Exception("Lineage mutated input");
    }

    private static PlannedSurveyLineTask ClearTask(PlannedSurveyLineTask t) => t with
    {
        PartitionCode = "", MotherLineTaskId = "", MotherLineStableId = "", SurveyLineStableId = "",
        ExecutionSegmentId = "", ExecutionSegmentStableId = "", IsQualityRepeat = false
    };
    private static MagneticSegmentAssignment ClearAssignment(MagneticSegmentAssignment a) => a with
    {
        PartitionCode = "", MotherLineTaskId = "", MotherLineStableId = "", SurveyLineStableId = "",
        ExecutionSegmentId = "", ExecutionSegmentStableId = "", IsQualityRepeat = false
    };
}
