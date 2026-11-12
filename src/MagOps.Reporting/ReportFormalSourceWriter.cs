using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using MagOps.Infrastructure.Services;

namespace MagOps.Reporting;

/// <summary>Export the existing audit selection for reporting, without diurnal/IGRF/levelling prerequisites.</summary>
public static class ReportFormalSourceWriter
{
    public static string Write(string outputRoot, AuditProductionResult audit, IReadOnlyList<UavMagCanonicalSample> samples,
        IReadOnlyList<PlannedSurveyLineTask> tasks, GeneratedArtifactContext context, string? selectionFile = null)
    {
        if (string.IsNullOrWhiteSpace(audit.ProductionSnapshotFingerprint))
            throw new InvalidOperationException("分线选择缺少选择指纹，请先完成核验。");
        var assignments = MagneticSurveyLineSegmentationService.FromAudit(samples, tasks, audit).Assignments
            .Where(a => a.IncludeInProcessing && a.SegmentClass == MagneticSegmentClass.SurveyLine &&
                !string.IsNullOrWhiteSpace(a.SurveyTaskStableId) && a.LinePass is not null &&
                a.Longitude is >= -180 and <= 180 && a.Latitude is >= -90 and <= 90)
            .OrderBy(a => a.LineOrder).ThenBy(a => a.LinePass).ThenBy(a => a.TimestampUtc).ThenBy(a => a.SourceFile, StringComparer.Ordinal).ThenBy(a => a.SourceRecord).ToArray();
        if (assignments.Length == 0) throw new InvalidOperationException("尚无具有正式测线身份的有效选中点，未归属及往返/连接段不进入报告数据。");
        var sourceLookup = samples.ToDictionary(p => (p.SourceFile, p.SourceRecord));
        var sourceIds = assignments.Select(a => a.SourceFile).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Select((p, i) => (Path: p, Id: "S" + (i + 1).ToString("D5", CultureInfo.InvariantCulture))).ToDictionary(p => p.Path, p => p.Id, StringComparer.Ordinal);
        string suffix = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture) + "_" + Guid.NewGuid().ToString("N")[..8];
        string staging = Path.Combine(outputRoot, ".building_report_source_" + suffix), output = Path.Combine(outputRoot, "报告正式测线_" + suffix);
        Directory.CreateDirectory(staging);
        try
        {
            string csv = Path.Combine(staging, "MagOps_报告正式测线.csv"), trace = Path.Combine(staging, "生产CSV_逐点追溯.csv.gz");
            string Cell(object? value) => "\"" + (Convert.ToString(value, CultureInfo.InvariantCulture) ?? "").Replace("\"", "\"\"") + "\"";
            using (var writer = new StreamWriter(csv, false, new UTF8Encoding(true)))
            using (var traceFile = File.Create(trace))
            using (var gzip = new GZipStream(traceFile, CompressionMode.Compress))
            using (var traceWriter = new StreamWriter(gzip, new UTF8Encoding(true)))
            {
                writer.WriteLine("LineName,LineTaskId,LinePass,DateUtc,TimeUtc,Longitude,Latitude,AltitudeM,RawMagNt");
                traceWriter.WriteLine("SourceId,SourceRecord,ProductionStatus,CsvRow,DateUtc,TimeUtc,LinePass,SnapshotFingerprint");
                for (int i = 0; i < assignments.Length; i++)
                {
                    var a = assignments[i]; var sample = sourceLookup[(a.SourceFile, a.SourceRecord)];
                    string date = a.TimestampUtc.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), time = a.TimestampUtc.UtcDateTime.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture);
                    double? Finite(double? v) => v is { } n && double.IsFinite(n) ? n : null;
                    writer.WriteLine(string.Join(",", new object?[] { a.LineName, a.SurveyTaskStableId, a.LinePass, date, time,
                        a.Longitude, a.Latitude, Finite(sample.AltitudeM), Finite(sample.MagneticFieldNt) }.Select(Cell)));
                    traceWriter.WriteLine(string.Join(",", new object?[] { sourceIds[a.SourceFile], a.SourceRecord, "EXPORTED", i + 2,
                        date, time, a.LinePass, audit.ProductionSnapshotFingerprint }.Select(Cell)));
                }
            }
            var references = sourceIds.Keys.Where(File.Exists).Select(p => new ReportSource("formal-raw-source", Path.GetFullPath(p), ReportSnapshotBuilder.Hash(p)))
                .Concat(string.IsNullOrWhiteSpace(selectionFile) ? [] : new[] { new ReportSource("production-selection-state", Path.GetFullPath(selectionFile), ReportSnapshotBuilder.Hash(selectionFile)) }).ToArray();
            foreach (string file in new[] { csv, trace })
                GeneratedArtifactIdentity.Write(file, "report_source", "formal_survey_raw", audit.ProductionSnapshotFingerprint,
                    sourceIds.Keys.ToArray(), qualityStatus: "REVIEW_REQUIRED", context: context);
            File.WriteAllText(Path.Combine(staging, "报告正式测线_manifest.json"), JsonSerializer.Serialize(new
            {
                schema_version = "magops-report-formal-source-1", Complete = true, production_status = "REVIEW_REQUIRED",
                generated_at_utc = DateTimeOffset.UtcNow, algorithm_version = MagneticSurveyLineSegmentationService.ImplementationVersion,
                production_snapshot_fingerprint = audit.ProductionSnapshotFingerprint, production_request_fingerprint = audit.ProductionRequestFingerprint,
                blocked_reasons = audit.BlockedReasons,
                production_trace_file = Path.GetFileName(trace), production_trace_exported_rows = assignments.Length,
                primary_diurnal_formula = "未执行日变、IGRF、T0或调平；直接使用核验选择及异常扣除后的正式测线原始磁通道",
                source_files = sourceIds.Select(p => new { source_id = p.Value, original_source = p.Key }),
                report_source_references = references,
                artifact_identities = new[] { csv, trace }.Select(p => new { file = Path.GetFileName(p), content_sha256 = ReportSnapshotBuilder.Hash(p) })
            }, ReportInputStore.Json));
            foreach (var reference in references)
                if (ReportSnapshotBuilder.Hash(reference.Path) != reference.Sha256) throw new IOException("导出时分线核验状态发生变化。");
            Directory.Move(staging, output);
            return Path.Combine(output, Path.GetFileName(csv));
        }
        catch (Exception ex)
        { File.WriteAllText(Path.Combine(staging, "FAILED.txt"), ex.Message); throw; }
    }
}
