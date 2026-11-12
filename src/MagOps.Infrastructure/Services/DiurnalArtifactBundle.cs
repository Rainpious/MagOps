using System.Text.Json;
using System.Text.Json.Nodes;

namespace MagOps.Infrastructure.Services;

internal static class DiurnalArtifactBundle
{
    internal static readonly IReadOnlyDictionary<string, (string Role, string Stage)> Outputs =
        new Dictionary<string, (string, string)>
        {
            ["MagOps_日变改正_完整.csv.gz"] = ("processed_magnetic", "diurnal_audit"),
            ["MagOps_日变磁测_处理用.csv"] = ("processed_magnetic", "prelevel_dtgz"),
            ["调平_T0_UTC日期摘要.csv"] = ("processing_summary", "t0_summary"),
            ["磁测分线_记录分类.csv.gz"] = ("processing_audit", "line_segmentation"),
            ["磁测分线_摘要.csv"] = ("processing_summary", "line_summary"),
            ["重测替换_记录选择.csv.gz"] = ("processing_audit", "repeat_selection"),
            ["重测替换_摘要.csv"] = ("processing_summary", "repeat_summary"),
            ["日变改正_UTC日期摘要.csv"] = ("processing_summary", "diurnal_summary"),
            ["日变改正_QC.md"] = ("quality_report", "diurnal_qc"),
            ["日变改正_manifest.json"] = ("manifest", "diurnal_manifest")
            ,["审计执行与生产选择.json"] = ("execution_ledger", "audit_execution")
            ,["生产异常区间.json"] = ("processing_audit", "production_anomalies")
            ,["生产CSV_逐点追溯.csv.gz"] = ("processing_audit", "production_trace")
            ,["审计轨迹审查.html"] = ("audit_visualization", "audit_visualization")
            ,["待复核_手动编辑.csv"] = ("review_draft", "manual_review")
        };

    public static DiurnalCorrectionExportResult Label(DiurnalCorrectionExportResult result,
        FieldMagFolderAnalysis analysis, IReadOnlyDictionary<string, string> previousIds,
        GeneratedArtifactContext? context = null)
    {
        var names = result.Files.ToDictionary(path => Path.GetFileName(path),
            path => GeneratedArtifactIdentity.TaggedName(Path.GetFileName(path),
                Outputs[Path.GetFileName(path)].Stage + (result.AuditProduction.IsReviewDraft ? "_REVIEW_DRAFT" : "")));
        string Rename(string path) => string.IsNullOrEmpty(path) ? "" : Path.Combine(Path.GetDirectoryName(path)!, names[Path.GetFileName(path)]);
        foreach (string path in result.Files)
            File.Move(path, Rename(path), overwrite: true);

        string manifest = Rename(result.ManifestPath);
        JsonNode document = JsonNode.Parse(File.ReadAllText(manifest))!;
        ReplaceNames(document, names);
        string qc = Rename(result.Files.Single(x => Path.GetExtension(x) == ".md"));
        string text = File.ReadAllText(qc);
        foreach (var pair in names) text = text.Replace(pair.Key, pair.Value, StringComparison.Ordinal);
        File.WriteAllText(qc, text);

        var sources = analysis.UavSamples.Select(x => x.SourceFile).Concat(analysis.BaseSamples.Select(x => x.SourceFile))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        var lineage = new GeneratedArtifactLineage(
            analysis.BaseSamples.Select(x => x.SourceFileStableId)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray(),
            analysis.BaseSamples.Select(x => x.SessionStableId)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray(),
            result.AuditProduction.Execution.Passes
                .Where(pass => result.AuditProduction.Selections
                    .SelectMany(selection => selection.Entries)
                    .Any(entry => entry.PassId.Equals(pass.PassId, StringComparison.Ordinal)))
                .Select(pass => pass.SurveyTaskStableId)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray(),
            [],
            []);
        var files = result.Files.Select(Rename).ToList();
        var metadata = new List<GeneratedArtifactMetadata>();
        foreach (string path in result.Files.Where(x => x != result.ManifestPath))
        {
            var spec = Outputs[Path.GetFileName(path)];
            metadata.Add(GeneratedArtifactIdentity.Write(Rename(path), spec.Role, spec.Stage,
                analysis.Fingerprint, sources, previousIds.GetValueOrDefault(Path.GetFileName(path)),
                result.AuditProduction.IsReviewDraft ? "REVIEW_DRAFT" : "UNREVIEWED", context, lineage,
                result.AuditProduction.ProductionRequestFingerprint, result.AuditProduction.ProductionSnapshotFingerprint));
            files.Add(Rename(path) + GeneratedArtifactIdentity.Suffix);
        }
        if (context is not null)
        {
            document["artifact_context"] = new JsonObject
            {
                ["project_id"] = string.IsNullOrWhiteSpace(context.ProjectId) ? null : context.ProjectId,
                ["workarea_id"] = string.IsNullOrWhiteSpace(context.WorkareaId) ? null : context.WorkareaId,
                ["context_status"] = context.ContextStatus
            };
        }

        document["production_lineage"] = new JsonObject
        {
            ["source_file_stable_ids"] = new JsonArray(lineage.SourceFileStableIds.Select(x => (JsonNode?)x).ToArray()),
            ["gsm_session_stable_ids"] = new JsonArray(lineage.GsmSessionStableIds.Select(x => (JsonNode?)x).ToArray()),
            ["survey_task_stable_ids"] = new JsonArray(lineage.SurveyTaskStableIds.Select(x => (JsonNode?)x).ToArray()),
            ["survey_line_stable_ids"] = new JsonArray(lineage.SurveyLineStableIds.Select(x => (JsonNode?)x).ToArray())
        };

        document["artifact_identities"] = new JsonArray(metadata.Select(x => (JsonNode?)new JsonObject
        {
            ["artifact_id"] = x.ArtifactId,
            ["file"] = x.FileName,
            ["metadata_file"] = x.FileName + GeneratedArtifactIdentity.Suffix,
            ["role"] = x.Role,
            ["processing_stage"] = x.ProcessingStage,
            ["content_sha256"] = x.ContentSha256,
            ["project_id"] = x.ProjectId,
            ["workarea_id"] = x.WorkareaId,
            ["context_status"] = x.ContextStatus
        }).ToArray());
        document["outputs"] = new JsonArray(files.Where(x => x != manifest).Select(x => (JsonNode?)new JsonObject
        {
            ["file"] = Path.GetFileName(x), ["bytes"] = new FileInfo(x).Length
        }).ToArray());
        File.WriteAllText(manifest, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All) }));
        GeneratedArtifactIdentity.Write(manifest, "manifest", "diurnal_manifest", analysis.Fingerprint, sources,
            previousIds.GetValueOrDefault(Path.GetFileName(result.ManifestPath)),
            result.AuditProduction.IsReviewDraft ? "REVIEW_DRAFT" : "UNREVIEWED", context, lineage,
            result.AuditProduction.ProductionRequestFingerprint, result.AuditProduction.ProductionSnapshotFingerprint);
        files.Add(manifest + GeneratedArtifactIdentity.Suffix);
        return result with
        {
            Files = files, CorrectedCsvPath = Rename(result.CorrectedCsvPath), ProcessingCsvPath = Rename(result.ProcessingCsvPath),
            T0SummaryCsvPath = Rename(result.T0SummaryCsvPath), ManifestPath = manifest,
            LineSegmentationAuditPath = Rename(result.LineSegmentationAuditPath),
            LineSegmentationSummaryPath = Rename(result.LineSegmentationSummaryPath),
            RepeatReplacementAuditPath = Rename(result.RepeatReplacementAuditPath),
            RepeatReplacementSummaryPath = Rename(result.RepeatReplacementSummaryPath)
        };
    }

    private static void ReplaceNames(JsonNode? node, IReadOnlyDictionary<string, string> names)
    {
        if (node is JsonObject obj)
            foreach (var pair in obj.ToArray())
            {
                if (pair.Value is JsonValue value && value.TryGetValue<string>(out var text) && names.TryGetValue(text, out var name))
                    obj[pair.Key] = name;
                else ReplaceNames(pair.Value, names);
            }
        else if (node is JsonArray array)
            for (int i = 0; i < array.Count; i++)
            {
                if (array[i] is JsonValue value && value.TryGetValue<string>(out var text) && names.TryGetValue(text, out var name))
                    array[i] = name;
                else ReplaceNames(array[i], names);
            }
    }

    public static DiurnalCorrectionExportResult Rebase(DiurnalCorrectionExportResult result, string stage, string target)
    {
        string Map(string path) => string.IsNullOrEmpty(path) ? "" : Path.Combine(target, Path.GetRelativePath(stage, path));
        return result with { Files = result.Files.Select(Map).ToArray(),
            CorrectedCsvPath = Map(result.CorrectedCsvPath), ProcessingCsvPath = Map(result.ProcessingCsvPath),
            T0SummaryCsvPath = Map(result.T0SummaryCsvPath), ManifestPath = Map(result.ManifestPath),
            LineSegmentationAuditPath = Map(result.LineSegmentationAuditPath), LineSegmentationSummaryPath = Map(result.LineSegmentationSummaryPath),
            RepeatReplacementAuditPath = Map(result.RepeatReplacementAuditPath), RepeatReplacementSummaryPath = Map(result.RepeatReplacementSummaryPath) };
    }
}
