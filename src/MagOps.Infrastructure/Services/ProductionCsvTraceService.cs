using System.Globalization;

namespace MagOps.Infrastructure.Services;

internal static class ProductionCsvTraceService
{
    public static void Write(string path, IReadOnlyList<UavMagCanonicalSample> source,
        AuditProductionResult audit,
        IReadOnlyList<(UavMagCanonicalSample Sample, MagneticSegmentAssignment? Assignment)> exported,
        MagDataSourceCatalog catalog, MagneticSurveyLineSegmentationResult segmentation)
    {
        var chosen = audit.SelectedSamples.Select(s => (s.SourceFile, s.SourceRecord)).ToHashSet();
        var assignments = segmentation.Assignments.ToDictionary(a => (a.SourceFile, a.SourceRecord));
        var output = exported.Select((r, i) => (r, Row: i + 2)).ToDictionary(x => (x.r.Sample.SourceFile, x.r.Sample.SourceRecord));
        var anomalies = audit.ProductionAnomalies.SelectMany(a => a.Samples.Select(s => (s, a)))
            .GroupBy(x => (x.s.SourceFile, x.s.SourceRecord)).ToDictionary(g => g.Key, g => g.Select(x => x.a).ToArray());
        var legs = audit.Execution.Legs.SelectMany(l => l.Samples.Select(s => (s, l.FlightLegId)))
            .ToDictionary(x => (x.s.SourceFile, x.s.SourceRecord), x => x.FlightLegId);
        var passes = audit.Execution.Passes.SelectMany(p => p.Samples.Select(s => (s, p.PassId)))
            .GroupBy(x => (x.s.SourceFile, x.s.SourceRecord)).ToDictionary(g => g.Key, g => string.Join(";", g.Select(x => x.PassId)));
        MagDataCompactExport.WriteGzipCsv(path, writer =>
        {
            writer.WriteLine("SourceId,SourceRecord,DateUtc,TimeUtc,ProductionStatus,CsvRow,LineName,LineOrder,LinePass,AnomalyIds,SnapshotFingerprint,FlightLegId,RecognizedPassId,SurveyTaskStableId,PartitionCode,MotherLineTaskId,MotherLineStableId,SurveyLineStableId,ExecutionSegmentId,ExecutionSegmentStableId,IsQualityRepeat");
            foreach (var sample in source.OrderBy(s => s.TimestampUtc).ThenBy(s => s.SourceFile, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.SourceRecord))
            {
                var key = (sample.SourceFile, sample.SourceRecord);
                bool written = output.TryGetValue(key, out var row);
                var intervals = anomalies.GetValueOrDefault(key) ?? [];
                string status = written ? "EXPORTED" : intervals.Any(a => a.ProductionDecision is "AUTO_EXCLUDE" or "USER_EXCLUDE")
                    && !intervals.Any(a => a.ProductionDecision == "USER_RESTORE")
                    ? "ANOMALY_EXCLUDED" : chosen.Contains(key) ? "SELECTED_WITHOUT_VALID_CORRECTION_OR_COORDINATE" : "NOT_SELECTED";
                var a = row.r.Assignment;
                if (a is null && assignments.TryGetValue(key, out var recognized)) a = recognized;
                writer.WriteLine(string.Join(",", catalog.GetSourceId(sample.SourceFile), sample.SourceRecord,
                    sample.TimestampUtc.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    sample.TimestampUtc.UtcDateTime.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture), status,
                    written ? row.Row.ToString(CultureInfo.InvariantCulture) : "", Csv(a?.LineName ?? ""),
                    a?.LineOrder?.ToString(CultureInfo.InvariantCulture) ?? "", a?.LinePass?.ToString(CultureInfo.InvariantCulture) ?? "",
                    Csv(string.Join(";", intervals.Select(i => i.AnomalyId))), audit.ProductionSnapshotFingerprint,
                    Csv(legs.GetValueOrDefault(key) ?? ""), Csv(passes.GetValueOrDefault(key) ?? ""),
                    Csv(a?.SurveyTaskStableId ?? ""), Csv(a?.PartitionCode ?? ""), Csv(a?.MotherLineTaskId ?? ""),
                    Csv(a?.MotherLineStableId ?? ""), Csv(a?.SurveyLineStableId ?? ""),
                    Csv(a?.ExecutionSegmentId ?? ""), Csv(a?.ExecutionSegmentStableId ?? ""),
                    a.HasValue ? a.Value.IsQualityRepeat ? "YES" : "NO" : ""));
            }
        });
    }
    private static string Csv(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
}
