using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MagOps.Infrastructure.Services;

public sealed record MotherLineCsvExportResult(string CsvPath, string TracePath, string? QualityRepeatCsvPath,
    string ManifestPath, string ContentSha256, int Rows);

/// <summary>Write a separate Oasis-shaped candidate CSV. Never replaces the ordinary production CSV.</summary>
public static class MotherLineCsvExportService
{
    private const string Schema = "magops-mother-line-csv-candidate-1";
    public static MotherLineCsvExportResult WriteNew(string outputDirectory, MotherLineProductionAssembly assembly,
        string productionSnapshotFingerprint, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (assembly.ContainsReviewDraft) throw new InvalidDataException("来源含待复核草稿，不能导出母线 CSV 候选。");
        if (string.IsNullOrWhiteSpace(productionSnapshotFingerprint)) throw new InvalidDataException("缺少生产快照身份。");
        var runs = assembly.PointSets.Where(s => !s.IsQualityRepeat).SelectMany(s =>
            s.Runs.Select(r => (Set: s, Run: r))).ToArray();
        var repeatRuns = assembly.PointSets.Where(s => s.IsQualityRepeat).SelectMany(s => s.Runs.Select(r => (Set: s, Run: r))).ToArray();
        if (runs.Length == 0 || runs.Concat(repeatRuns).Any(x => x.Run.Points.Count == 0 ||
            (!x.Set.IsQualityRepeat && x.Run.DirectionUndetermined) ||
            x.Run.Points.Any(p => p.ProcessingValues.Count != MagneticPreLevelProcessingService.ProcessingColumns.Count)))
            throw new InvalidDataException("没有具备明确方向及完整处理列的正式测线候选；未写文件。");
        string directory = Path.GetFullPath(outputDirectory);
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("所选母线成果目录不存在；未创建目录。");
        string id = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture) + "_" + Guid.NewGuid().ToString("N")[..8];
        string stem = "母线正式测线_" + id;
        string stage = Path.Combine(directory, ".building_" + id), final = Path.Combine(directory, stem);
        Directory.CreateDirectory(stage);
        try
        {
            string csv = Path.Combine(stage, stem + ".csv"), trace = Path.Combine(stage, stem + "_逐点追溯.csv");
            string? repeatCsv = repeatRuns.Length == 0 ? null : Path.Combine(stage, stem + "_质量重复线.csv");
            string? repeatTrace = repeatCsv is null ? null : Path.Combine(stage, stem + "_质量重复线_逐点追溯.csv");
            using (var writer = new StreamWriter(csv, false, new UTF8Encoding(true)))
            using (var traceWriter = new StreamWriter(trace, false, new UTF8Encoding(true)))
            using (var repeatTraceWriter = repeatTrace is null ? null : new StreamWriter(repeatTrace, false, new UTF8Encoding(true)))
            {
                writer.WriteLine(string.Join(",", MagneticPreLevelProcessingService.ProcessingColumns));
                traceWriter.WriteLine("CsvRow,LineName,LineOrder,ProcessingLinePass,ProcessingLineTaskId,PointOrder,SourceSha256,SourceFile,SourceRecord,SnapshotId,PartitionCode,RecognizedPassId,SurveyTaskStableId,ExecutionSegmentStableId,OriginalLinePass,Reversed,QualityRepeat,RawMagNt,OutputKind");
                repeatTraceWriter?.WriteLine("CsvRow,LineName,LineOrder,ProcessingLinePass,ProcessingLineTaskId,PointOrder,SourceSha256,SourceFile,SourceRecord,SnapshotId,PartitionCode,RecognizedPassId,SurveyTaskStableId,ExecutionSegmentStableId,OriginalLinePass,Reversed,QualityRepeat,RawMagNt,OutputKind");
                int csvRow = 2, repeatRow = 2;
                var repeatLines = new StringBuilder();
                foreach (var (set, run) in runs.Concat(repeatRuns).OrderBy(x => x.Set.LineOrder).ThenBy(x => x.Set.IsQualityRepeat)
                    .ThenBy(x => x.Set.RepeatPassKey, StringComparer.Ordinal).ThenBy(x => x.Run.Points[0].PointOrder).Select(x => (x.Set, x.Run)))
                foreach (var point in run.Points)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string lineName = set.LineName;
                    var values = point.ProcessingValues.ToArray();
                    values[0] = lineName; values[1] = set.LineOrder.ToString(CultureInfo.InvariantCulture);
                    string row = string.Join(",", values.Select(Csv));
                    if (set.IsQualityRepeat)
                    {
                        repeatLines.AppendLine(string.Join(",", values.Concat([
                        point.PointOrder.ToString(CultureInfo.InvariantCulture),
                        run.RecognizedPassId,
                        "YES", run.Reversed ? "YES" : "NO", run.DirectionUndetermined ? "YES" : "NO"]).Select(Csv)));
                        repeatRow++;
                    }
                    else
                    {
                        writer.WriteLine(row); csvRow++;
                    }
                    foreach (var provenance in point.Provenance)
                        (set.IsQualityRepeat ? repeatTraceWriter! : traceWriter).WriteLine(string.Join(",", new object?[] { (set.IsQualityRepeat ? repeatRow : csvRow) - 1, lineName, set.LineOrder,
                            values[2], values[5], point.PointOrder, point.SourceSha256, provenance.SourceFile, point.Sample.SourceRecord,
                            provenance.SnapshotId, provenance.PartitionCode, provenance.RecognizedPassId, provenance.SurveyTaskStableId,
                            provenance.ExecutionSegmentStableId, provenance.OriginalLinePass, run.Reversed ? "YES" : "NO",
                            set.IsQualityRepeat ? "YES" : "NO", point.Sample.MagneticFieldNt,
                            set.IsQualityRepeat ? "QUALITY_REPEAT_CSV" : "FORMAL_LINE_CANDIDATE_CSV" }
                            .Select(x => Csv(Convert.ToString(x, CultureInfo.InvariantCulture) ?? ""))));
                }
                if (repeatCsv is not null)
                {
                    using var repeatWriter = new StreamWriter(repeatCsv, false, new UTF8Encoding(true));
                    repeatWriter.WriteLine(string.Join(",", MagneticPreLevelProcessingService.ProcessingColumns.Concat(
                        ["PointOrder", "RecognizedPassId", "QualityRepeat", "Reversed", "DirectionUndetermined"])));
                    repeatWriter.Write(repeatLines.ToString());
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            string hash;
            using (var stream = File.OpenRead(csv))
            using (var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                byte[] buffer = new byte[1024 * 1024]; int count;
                while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
                { cancellationToken.ThrowIfCancellationRequested(); digest.AppendData(buffer, 0, count); }
                hash = Convert.ToHexString(digest.GetHashAndReset());
            }
            var downstreamIdentityAudit = assembly.PointSets.Where(s => !s.IsQualityRepeat).OrderBy(s => s.LineOrder)
                .Select(s =>
                {
                    var points = s.Runs.SelectMany(r => r.Points).ToArray();
                    var taskIds = points.Select(p => p.ProcessingValues[5]).Where(x => !string.IsNullOrWhiteSpace(x))
                        .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                    var linePasses = points.Select(p => p.ProcessingValues[2]).Where(x => !string.IsNullOrWhiteSpace(x))
                        .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                    return new
                    {
                        line_name = s.LineName,
                        line_order = s.LineOrder,
                        formal_records = points.Length,
                        distinct_processing_line_task_ids = taskIds,
                        rows_missing_processing_line_task_id = points.Count(p => string.IsNullOrWhiteSpace(p.ProcessingValues[5])),
                        distinct_processing_line_pass_values = linePasses,
                        rows_missing_processing_line_pass = points.Count(p => string.IsNullOrWhiteSpace(p.ProcessingValues[2]))
                    };
                }).ToArray();
            File.WriteAllText(Path.Combine(stage, Path.GetFileNameWithoutExtension(csv) + "_manifest.json"),
                JsonSerializer.Serialize(new { schema = Schema, status = "REVIEW_CANDIDATE", project_id = assembly.ProjectId,
                    workarea_id = assembly.WorkareaId, design_id = assembly.DesignId, production_snapshot_fingerprint = productionSnapshotFingerprint,
                    source_sha256 = assembly.PointSets.SelectMany(s => s.Runs).SelectMany(r => r.Points).Select(p => p.SourceSha256).Distinct(),
                    source_kinds = assembly.SourceKinds, original_lineage_verified = assembly.OriginalLineageVerified,
                    processing_csv_original_project_design_approval_verified = false,
                    review_draft_detection = "known path markers only; absence does not prove production approval",
                    assembly_fingerprint = productionSnapshotFingerprint,
                    source_record_basis = "1-based data record in SourceFile; PROCESSING_CSV is not a raw instrument source",
                    rows = runs.Where(x => !x.Set.IsQualityRepeat).Sum(x => x.Run.Points.Count), csv_sha256 = hash,
                    headers = MagneticPreLevelProcessingService.ProcessingColumns,
                    downstream_identity_audit = downstreamIdentityAudit,
                    trace = Path.GetFileName(trace), quality_repeat_csv = repeatCsv is null ? null : Path.GetFileName(repeatCsv),
                    quality_repeat_trace = repeatTrace is null ? null : Path.GetFileName(repeatTrace),
                    quality_repeat_sets = assembly.PointSets.Count(s => s.IsQualityRepeat),
                    unresolved_records = assembly.Unresolved.Count, excluded_records = assembly.ExcludedRecords,
                    exact_duplicate_records = assembly.DuplicateSourceRecords, contains_review_draft = assembly.ContainsReviewDraft }, new JsonSerializerOptions { WriteIndented = true }));
            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(final)) throw new IOException("候选输出目标已存在。");
            Directory.Move(stage, final);
            string finalCsv = Path.Combine(final, Path.GetFileName(csv));
            return new(finalCsv, Path.Combine(final, Path.GetFileName(trace)), repeatCsv is null ? null : Path.Combine(final, Path.GetFileName(repeatCsv)),
                Path.Combine(final, Path.GetFileNameWithoutExtension(csv) + "_manifest.json"), hash,
                runs.Where(x => !x.Set.IsQualityRepeat).Sum(x => x.Run.Points.Count));
        }
        catch
        {
            try { if (Directory.Exists(stage)) Directory.Delete(stage, true); } catch { }
            throw;
        }
    }

    private static string Csv(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
}
