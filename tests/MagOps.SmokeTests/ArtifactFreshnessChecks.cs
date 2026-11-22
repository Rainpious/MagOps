using System.Security.Cryptography;
using MagOps.Infrastructure.Services;
using MagOps.Infrastructure.Storage;

namespace MagOps.SmokeTests;

internal static class ArtifactFreshnessChecks
{
    internal static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "magops-artifact-freshness-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new JsonStore();
            var workspace = new WorkspaceService(store);
            var project = workspace.Initialize(root, "freshness-test");
            var workarea = workspace.RegisterWorkarea(root, "freshness-area");
            workspace.ConfirmWorkareaMeridian(root, workarea.Path, 111, "TEST_INITIAL");
            string output = Path.Combine(root, workarea.Path, "06_数据处理", "MagOps_处理结果__MagOps_prelevel_dtgz.csv");
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.WriteAllText(output, "LineName,RawMagNt\nL1,50000\n");
            GeneratedArtifactIdentity.Write(output, "processed_magnetic", "prelevel_dtgz", "source-fingerprint", [],
                context: new GeneratedArtifactContext(project.ProjectId, workarea.WorkareaId));

            var artifacts = new ArtifactService(store, workspace);
            string relative = Path.GetRelativePath(root, output).Replace('\\', '/');
            var registered = artifacts.RegisterGeneratedFile(root, output, "test-production-output");
            artifacts.Update(root, relative, qualityStatus: "APPROVED");
            Check(registered.SpatialReferenceStatus == "CURRENT", "new derived artifact starts current");
            Check(!GeneratedArtifactIdentity.Inspect(output).IsProjectSpatialReferenceStale,
                "matching project central meridian starts current");

            var neighbor = workspace.RegisterWorkarea(root, "neighbor");
            workspace.ConfirmWorkareaMeridian(root, neighbor.Path, 120, "TEST_NEIGHBOR");
            Check(GeneratedArtifactIdentity.InspectProjectSpatialReferenceStatus(output) == "CURRENT",
                "changing a neighboring workarea leaves this artifact current");
            workspace.ConfirmWorkareaMeridian(root, workarea.Path, 114, "TEST_CHANGED");
            var stale = GeneratedArtifactIdentity.Inspect(output);
            Check(stale.Status == "VERIFIED" && stale.IsProjectSpatialReferenceStale &&
                  stale.Warning?.Contains("中央经线") == true,
                $"central meridian change invalidates a still hash-valid spatial artifact; status={stale.Status}, spatial={stale.IsProjectSpatialReferenceStale}, warning={stale.Warning}, metadata={System.Text.Json.JsonSerializer.Serialize(stale.Metadata)}");
            var refreshed = artifacts.RefreshProjectSpatialReferenceStatus(root);
            Check(refreshed.Count == 1 && refreshed[0].SpatialReferenceStatus == "STALE",
                "freshness refresh records stale spatial reference status in the project artifact ledger");
            Check(artifacts.List(root).Single(x => x.Path == relative).QualityStatus == "APPROVED",
                "freshness state does not overwrite independent manual quality assessment");

            workspace.ConfirmWorkareaMeridian(root, workarea.Path, 111, "TEST_RESTORED");
            Check(!GeneratedArtifactIdentity.Inspect(output).IsProjectSpatialReferenceStale,
                "restoring the artifact's exact source meridian restores current spatial freshness");
            var restored = artifacts.RefreshProjectSpatialReferenceStatus(root);
            Check(restored.Count == 1 && restored[0].SpatialReferenceStatus == "CURRENT",
                "freshness ledger returns to current when the spatial reference matches again");
            Check(File.ReadAllText(output).Contains("50000", StringComparison.Ordinal),
                "freshness transitions never rewrite generated production content");

            string detached = Path.Combine(Path.GetTempPath(), "magops-detached-artifact-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(detached);
            try
            {
                string detachedOutput = Path.Combine(detached, Path.GetFileName(output));
                File.Copy(output, detachedOutput);
                File.Copy(output + GeneratedArtifactIdentity.Suffix,
                    detachedOutput + GeneratedArtifactIdentity.Suffix);
                Check(GeneratedArtifactIdentity.InspectProjectSpatialReferenceStatus(detachedOutput) == "UNVERIFIED",
                    "a valid sidecar without a locatable project must not be reported current");

                File.AppendAllText(detachedOutput, "tampered\n");
                Check(GeneratedArtifactIdentity.InspectProjectSpatialReferenceStatus(detachedOutput) == "UNVERIFIED",
                    "a content hash mismatch must not retain a verified spatial-reference state");
            }
            finally
            {
                Directory.Delete(detached, recursive: true);
            }

            string unconfirmedRoot = Path.Combine(Path.GetTempPath(), "magops-unconfirmed-spatial-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(unconfirmedRoot);
            try
            {
                var unconfirmedWorkspace = new WorkspaceService(store);
                var unconfirmedProject = unconfirmedWorkspace.Initialize(unconfirmedRoot, "unconfirmed-spatial-test");
                var unconfirmedArea = unconfirmedWorkspace.RegisterWorkarea(unconfirmedRoot, "unconfirmed-area");
                string unconfirmedOutput = Path.Combine(unconfirmedRoot, unconfirmedArea.Path,
                    "06_数据处理", "MagOps_未确认经线__MagOps_prelevel_dtgz.csv");
                Directory.CreateDirectory(Path.GetDirectoryName(unconfirmedOutput)!);
                File.WriteAllText(unconfirmedOutput, "LineName,RawMagNt\nL1,50001\n");
                GeneratedArtifactIdentity.Write(unconfirmedOutput, "processed_magnetic", "prelevel_dtgz",
                    "unconfirmed-source", [], context: new GeneratedArtifactContext(
                        unconfirmedProject.ProjectId, unconfirmedArea.WorkareaId));
                Check(GeneratedArtifactIdentity.InspectProjectSpatialReferenceStatus(unconfirmedOutput) == "UNVERIFIED",
                    "an artifact and project both lacking a confirmed meridian must remain unverified");

                unconfirmedWorkspace.ConfirmWorkareaMeridian(unconfirmedRoot, unconfirmedArea.Path, 117, "TEST_CONFIRM_AFTER_EXPORT");
                Check(GeneratedArtifactIdentity.InspectProjectSpatialReferenceStatus(unconfirmedOutput) == "STALE",
                    "confirming a meridian after an unreferenced export makes the old artifact stale");
            }
            finally
            {
                Directory.Delete(unconfirmedRoot, recursive: true);
            }

            string sourceA = Path.Combine(root, "source-a");
            string sourceB = Path.Combine(root, "source-b");
            Directory.CreateDirectory(sourceA);
            Directory.CreateDirectory(sourceB);
            string sourceFileA = Path.Combine(sourceA, "flight.csv");
            string sourceFileB = Path.Combine(sourceB, "flight.csv");
            File.WriteAllText(sourceFileA, "raw-a");
            File.WriteAllText(sourceFileB, "raw-b");
            FieldMagFolderAnalysis analysisB = Analysis(sourceB, sourceFileB, "analysis-b");

            // A previous release wrote one JSON object. The next analysis upgrades
            // it to a ledger while preserving other source-directory reports.
            string consistencyPath = ProjectPaths.Json(root, "field_data_consistency.json");
            store.Write(consistencyPath, new FieldDataConsistencyReport(sourceA, "analysis-a", 1, [])
            {
                SourceFiles = [sourceFileA],
                SourceFileStableIds = ["stable-a"]
            });
            var importer = new FieldDataImportService(store);
            importer.EnrichFromAnalysis(root, analysisB);
            Check(importer.ListConsistencyReports(root).Count == 2,
                "legacy single report is retained when a second source-directory report is added");
            importer.EnrichFromAnalysis(root, Analysis(sourceA + Path.DirectorySeparatorChar,
                sourceFileA, "analysis-a", "stable-a"));
            Check(importer.ListConsistencyReports(root).Count == 2,
                "normalized path variants update the same report instead of duplicating it");

            var sourceArtifacts = new[]
            {
                (Name: "source-a-output.csv", Source: sourceFileA, Fingerprint: "analysis-a", StableId: "stable-a"),
                (Name: "source-b-output.csv", Source: sourceFileB, Fingerprint: "analysis-b", StableId: "stable-b")
            };
            foreach (var item in sourceArtifacts)
            {
                string artifactPath = Path.Combine(root, workarea.Path, "06_数据处理", item.Name);
                File.WriteAllText(artifactPath, "derived");
                GeneratedArtifactIdentity.Write(artifactPath, "processed_magnetic", "prelevel_dtgz",
                    item.Fingerprint, [item.Source], context: new GeneratedArtifactContext(
                        project.ProjectId, workarea.WorkareaId), lineage: new GeneratedArtifactLineage(
                        [item.StableId], [], [], [], []));
            }
            artifacts.Scan(root, withHash: true);
            var sourceRows = artifacts.List(root);
            var rowA = sourceRows.Single(x => x.Name == "source-a-output.csv");
            var rowB = sourceRows.Single(x => x.Name == "source-b-output.csv");
            Check(rowA.SourceFreshnessStatus == "CURRENT" && rowB.SourceFreshnessStatus == "CURRENT",
                "each source report independently validates its own derived artifact");

            string[] analysisDerivedStages =
            [
                "diurnal_audit", "prelevel_dtgz", "t0_summary", "line_segmentation", "line_summary",
                "repeat_selection", "repeat_summary", "diurnal_summary", "diurnal_qc", "diurnal_manifest",
                "audit_execution", "production_anomalies", "production_trace", "audit_visualization", "manual_review",
                "field_data_integration", "field_gemlink_bridge"
            ];
            var analysisStagePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string stage in analysisDerivedStages)
            {
                string stagePath = Path.Combine(root, workarea.Path, "06_数据处理", "stage-" + stage + ".json");
                analysisStagePaths[stage] = stagePath;
                File.WriteAllText(stagePath, "derived-" + stage);
                GeneratedArtifactIdentity.Write(stagePath, "processing_audit", stage, "analysis-a",
                    [sourceFileA], context: new GeneratedArtifactContext(project.ProjectId, workarea.WorkareaId));
                Check(GeneratedArtifactIdentity.InspectSourceFreshnessStatus(stagePath) == "CURRENT",
                    $"analysis-driven output stage {stage} participates in source freshness validation");
            }
            string unrelatedPath = Path.Combine(root, workarea.Path, "06_数据处理", "unrelated.json");
            File.WriteAllText(unrelatedPath, "unrelated");
            GeneratedArtifactIdentity.Write(unrelatedPath, "processing_audit", "coordinate_import", "other",
                [sourceFileA], context: new GeneratedArtifactContext(project.ProjectId, workarea.WorkareaId));
            Check(GeneratedArtifactIdentity.InspectSourceFreshnessStatus(unrelatedPath) == "NOT_APPLICABLE",
                "non-field-analysis artifact stages stay outside this freshness policy");
            string formalReportPath = Path.Combine(root, workarea.Path, "06_数据处理", "formal-survey.csv");
            File.WriteAllText(formalReportPath, "formal report source");
            GeneratedArtifactIdentity.Write(formalReportPath, "report_source", "formal_survey_raw", "production-snapshot-a",
                [sourceFileA], context: new GeneratedArtifactContext(project.ProjectId, workarea.WorkareaId));
            Check(GeneratedArtifactIdentity.InspectSourceFreshnessStatus(formalReportPath) == "CURRENT",
                "formal raw survey report sources verify file bytes without confusing production and analysis fingerprints");

            File.WriteAllText(sourceFileA, "raw-a-mutated");
            string artifactAPath = Path.Combine(root, rowA.Path.Replace('/', Path.DirectorySeparatorChar));
            Check(GeneratedArtifactIdentity.InspectSourceFreshnessStatus(artifactAPath) == "STALE",
                "source byte changes are detected before a new field-data analysis runs");
            Check(GeneratedArtifactIdentity.InspectSourceFreshnessStatus(formalReportPath) == "STALE",
                "formal report raw-source provenance becomes stale when a source file changes");
            Check(analysisStagePaths.Where(x => x.Key.StartsWith("field_data_", StringComparison.OrdinalIgnoreCase) ||
                                                   x.Key.Equals("field_gemlink_bridge", StringComparison.OrdinalIgnoreCase))
                    .All(x => GeneratedArtifactIdentity.InspectSourceFreshnessStatus(x.Value) == "STALE"),
                "field integration and GEMLink artifacts become stale when source bytes change");
            File.WriteAllText(sourceFileA, "raw-a");

            // Updating A must stale A, without applying A's latest fingerprint to B.
            importer.EnrichFromAnalysis(root, Analysis(sourceA, sourceFileA, "analysis-a-v2", "stable-a"));
            var refreshedSources = artifacts.RefreshFreshnessStatus(root);
            Check(refreshedSources.Any(x => x.Name == "source-a-output.csv" && x.SourceFreshnessStatus == "STALE") &&
                  refreshedSources.All(x => x.Name != "source-b-output.csv" || x.SourceFreshnessStatus == "CURRENT"),
                "a changed analysis fingerprint stales only artifacts from that source directory");
            Check(artifacts.List(root).Single(x => x.Name == "source-a-output.csv").QualityStatus == "UNREVIEWED",
                "source freshness refresh leaves manual quality assessment unchanged");

            string driveRoot = Path.GetPathRoot(root)!;
            var rootScopedReport = new FieldDataConsistencyReport(driveRoot, "root-analysis", 1, [])
            {
                SourceFiles = [sourceFileA],
                SourceFileHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [sourceFileA] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sourceFileA)))
                }
            };
            store.Write(consistencyPath, new FieldDataConsistencyLedger { Reports = [rootScopedReport] });
            string rootScopedArtifact = Path.Combine(root, workarea.Path, "06_数据处理", "root-scope.csv");
            File.WriteAllText(rootScopedArtifact, "root-scoped");
            GeneratedArtifactIdentity.Write(rootScopedArtifact, "processed_magnetic", "prelevel_dtgz",
                "root-analysis", [sourceFileA], context: new GeneratedArtifactContext(project.ProjectId, workarea.WorkareaId));
            Check(GeneratedArtifactIdentity.InspectSourceFreshnessStatus(rootScopedArtifact) == "CURRENT",
                "a drive-root analysis scope matches descendants without a doubled separator");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }

        static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Artifact freshness check failed: " + message);
        }

        static FieldMagFolderAnalysis Analysis(string directory, string file, string fingerprint, string stableId = "") =>
            new()
            {
                SourceDirectory = directory,
                Fingerprint = fingerprint,
                Files = [new FieldMagFileScan(file, FieldMagSensorFamily.UavOpticalPump,
                    FieldMagDataRole.Raw, 1, null, null, "test", [])
                    {
                        SourceFileStableId = stableId,
                        SourceContentSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))
                    }]
            };
    }
}
