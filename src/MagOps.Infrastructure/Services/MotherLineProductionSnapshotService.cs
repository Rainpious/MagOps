using System.Security.Cryptography;
using System.Text.Json;

namespace MagOps.Infrastructure.Services;

/// <summary>Adapts final WriteProcessingCsv callbacks, not the earlier recognized point set.
/// This is a read-only candidate snapshot; no production selection or file writes occur.</summary>
public static class MotherLineProductionSnapshotService
{
    public static MotherLineProductionBatch Capture(string projectId, string workareaId, string designId,
        FieldMagFolderAnalysis analysis, AuditProductionResult audit,
        IReadOnlyList<(UavMagCanonicalSample Sample, MagneticSegmentAssignment? Assignment)> exported,
        IReadOnlyList<FinalProcessingCsvRow>? processingRows = null)
    {
        Require(!string.IsNullOrWhiteSpace(projectId) && !string.IsNullOrWhiteSpace(workareaId) &&
            !string.IsNullOrWhiteSpace(designId), "缺少项目、工区或设计身份");
        Require(!string.IsNullOrWhiteSpace(audit.ProductionSnapshotFingerprint), "缺少生产审核快照");
        var sources = analysis.Files.GroupBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);
        var samples = analysis.UavSamples.ToDictionary(s => (s.SourceFile, s.SourceRecord));
        var selected = audit.SelectedSamples.Select(s => (s.SourceFile, s.SourceRecord)).ToHashSet();
        var passes = audit.Execution.Passes.SelectMany(p => p.Samples.Select(s => new { s.SourceFile, s.SourceRecord, Pass = p }))
            .GroupBy(x => (x.SourceFile, x.SourceRecord)).ToDictionary(g => g.Key, g => g.Select(x => x.Pass).ToArray());
        var seen = new HashSet<(string, int)>();
        var rows = new List<MotherLineProductionObservation>();
        var processing = processingRows?.GroupBy(r => (r.SourceFile, r.SourceRecord)).ToDictionary(g => g.Key, g => g.ToArray());
        if (processingRows is not null)
            Require(processingRows.Count == exported.Count && processing!.Count == exported.Count &&
                processing.Values.All(r => r.Length == 1 && r[0].Values is not null &&
                    r[0].Values.Count == MagneticPreLevelProcessingService.ProcessingColumns.Count && r[0].Values.All(v => v is not null)),
                "实际处理 CSV 列、点数或来源身份不完整");
        foreach (var (sample, assignment) in exported)
        {
            var key = (sample.SourceFile, sample.SourceRecord);
            Require(seen.Add(key), "最终导出点重复");
            Require(samples.TryGetValue(key, out var original) && SameSample(original, sample), "导出点不属于解析快照或观测值已变化");
            Require(selected.Contains(key), "导出点不在最终审核纳入范围");
            Require(assignment.HasValue && assignment.Value.IncludeInProcessing, "导出点缺少最终纳入归属");
            var a = assignment!.Value;
            Require(a.SourceFile == sample.SourceFile && a.SourceRecord == sample.SourceRecord &&
                a.TimestampUtc == sample.TimestampUtc && a.Longitude == sample.Longitude && a.Latitude == sample.Latitude,
                "导出点与最终归属不一致");
            Require(sources.TryGetValue(sample.SourceFile, out var scans) && scans.Length == 1, "解析来源文件身份不唯一或缺失");
            string hash = scans![0].SourceContentSha256;
            Require(hash.Length == 64 && hash.All(Uri.IsHexDigit), "解析快照缺少来源内容哈希，请重新扫描");
            string passId = "";
            if (a.SegmentClass == MagneticSegmentClass.SurveyLine && !string.IsNullOrWhiteSpace(a.MotherLineStableId))
            {
                var matches = (passes.GetValueOrDefault(key) ?? []).Where(p => p.SurveyTaskStableId == a.SurveyTaskStableId).ToArray();
                Require(matches.Length == 1 && !string.IsNullOrWhiteSpace(matches[0].PassId) &&
                    matches[0].IsQualityRepeat == a.IsQualityRepeat, "测线点的识别 Pass 缺失、歧义或用途不一致");
                passId = matches[0].PassId;
            }
            var values = processing is null ? Array.Empty<string>() : processing.TryGetValue(key, out var payload)
                ? payload[0].Values.ToArray() : throw new InvalidDataException("实际处理 CSV 缺少导出点。");
            rows.Add(new(hash.ToUpperInvariant(), original!, a, passId) { ProcessingValues = values });
        }
        // Final subset and final LinePass belong to the snapshot identity: the audit alone
        // precedes correction eligibility and cannot identify the actual exported point set.
        var ordered = rows.OrderBy(r => r.Sample.SourceFile, StringComparer.Ordinal)
            .ThenBy(r => r.Sample.SourceRecord).ToArray();
        string id = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            Schema = "mother-production-snapshot-1", projectId, workareaId, designId,
            Audit = audit.ProductionSnapshotFingerprint, audit.IsReviewDraft, Rows = ordered
        })));
        return new(projectId, workareaId, designId, id, ordered)
        {
            AuditSnapshotFingerprint = audit.ProductionSnapshotFingerprint,
            IsReviewDraft = audit.IsReviewDraft
        };
    }

    private static bool SameSample(UavMagCanonicalSample a, UavMagCanonicalSample b) =>
        a with { QualityFlags = b.QualityFlags } == b && a.QualityFlags.SequenceEqual(b.QualityFlags);

    private static void Require(bool valid, string message)
    {
        if (!valid) throw new InvalidDataException("母线生产快照生成失败：" + message + "。正式成果未改动。");
    }
}
