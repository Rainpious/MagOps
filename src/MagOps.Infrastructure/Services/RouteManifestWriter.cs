using System.Text.Json;
using MagOps.Core.Routes;

namespace MagOps.Infrastructure.Services;

public sealed class RouteManifestWriter
{
    private static double? FiniteOrNull(double value) =>
        double.IsFinite(value) ? value : null;

    public string Write(
        string manifestPath,
        RoutePlanResult plan,
        IReadOnlyList<string> outputPaths,
        string projectId = "",
        string workareaId = "",
        bool writeArtifactIdentity = false,
        string? artifactId = null)
    {
        if (outputPaths.Count == 0)
            throw new InvalidOperationException("没有可写入航线结果清单的输出文件。");

        string fullManifest = Path.GetFullPath(manifestPath);
        string manifestDir = Path.GetDirectoryName(fullManifest)!;
        Directory.CreateDirectory(manifestDir);

        bool overLimit = plan.Sorties.Any(s =>
            RouteCoordinateMath.RouteLength(
                s.Points, plan.Mode, plan.CentralMeridian)
            > plan.Options.MaxSortieKm * 1000.0);

        var accessRows = LaunchSitePlanning.Enabled ? RouteAccessPlanner.Assess(plan) : Array.Empty<SortieAccessAssessment>();
        bool accessConfigured = LaunchSitePlanning.Enabled && (plan.AccessPlanning?.Sites.Count > 0 || plan.Options.TakeoffX.HasValue);
        string status = overLimit || (accessConfigured && accessRows.Any(a => a.Status != "OK")) ? "REVIEW_REQUIRED" : "PASSED";
        var warnings = plan.Warnings.ToList();
        if (overLimit && !warnings.Any(x => x.Contains("超过", StringComparison.Ordinal)))
            warnings.Add("最终架次存在超出最大单架次长度的情况，需要人工复核。");

        string source = Path.GetFullPath(plan.SourcePath);
        var outputNames = outputPaths.Select(p =>
        {
            string full = Path.GetFullPath(p);
            string relative = Path.GetRelativePath(manifestDir, full);
            return relative.Replace('\\', '/');
        }).ToList();

        var inputHashes = new Dictionary<string, string>
        {
            [source] = ArtifactService.Sha256(source)
        };
        var outputHashes = new Dictionary<string, string>();
        for (int i = 0; i < outputPaths.Count; i++)
            outputHashes[outputNames[i]] = ArtifactService.Sha256(outputPaths[i]);

        double totalDistance = plan.Sorties.Sum(s =>
            RouteCoordinateMath.RouteLength(s.Points, plan.Mode, plan.CentralMeridian));

        var payload = new
        {
            schema_version = RouteResultService.RouteResultSchemaVersion,
            algorithm_version = RoutePlanner.AlgorithmVersion,
            created_at = DateTimeOffset.UtcNow.ToString("O"),
            command = "magops-route-v25-export",
            status,
            project_id = projectId,
            workarea_id = workareaId,
            subarea_id = string.IsNullOrWhiteSpace(plan.PartitionCode)
                ? workareaId
                : plan.PartitionCode,
            partition_code = plan.PartitionCode,
            partition_manifest_path = plan.PartitionManifestPath,
            route_id = "route-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss"),
            sortie_id = "",
            survey_task_references = plan.Sorties.SelectMany(s =>
                s.TaskReferences.Select(r => new
                {
                    sortie_name = s.Name,
                    line_name = r.LineName,
                    survey_task_stable_id = r.SurveyTaskStableId,
                    survey_line_stable_id = r.SurveyLineStableId,
                    mother_line_stable_id = r.MotherLineStableId,
                    execution_segment_id = r.ExecutionSegmentId,
                    execution_segment_stable_id = r.ExecutionSegmentStableId,
                    protected_execution_segment = r.ProtectedExecutionSegment,
                    geometry_fingerprint = r.GeometryFingerprint,
                    survey_purpose = r.SurveyPurpose,
                    repeat_index = r.RepeatIndex
                })).ToArray(),
            execution_segments = plan.ExecutionSegments.Select(segment => new
            {
                mother_line = segment.MotherLine,
                segment_id = segment.SegmentId,
                segment_stable_id = segment.SegmentStableId,
                cut_operation_id = segment.CutOperationId,
                sequence = segment.Sequence,
                cut_point = new[] { segment.CutX, segment.CutY },
                cut_line = new[] { segment.CutLineStartX, segment.CutLineStartY, segment.CutLineEndX, segment.CutLineEndY },
                protected_connection = segment.ProtectedConnection
            }).ToArray(),
            source_paths = new[] { source },
            input_sha256 = inputHashes,
            parameters = new
            {
                coord_mode = plan.Mode,
                headerless_order = plan.Options.HeaderlessOrder,
                central_meridian = plan.CentralMeridian,
                max_sortie_km = plan.Options.MaxSortieKm,
                start_direction = plan.Options.StartDirection.ToString(),
                partition_start_side = plan.Options.PartitionStartSide.ToString(),
                selected_start_pid = plan.StartPoint.Pid,
                partition_start_rule = string.IsNullOrWhiteSpace(plan.PartitionCode)
                    ? "user-selected boundary corner"
                    : plan.Options.PartitionStartSide == RoutePartitionStartSide.Automatic
                        ? "automatic shared partition-cut boundary"
                        : $"explicit cardinal partition side: {plan.Options.PartitionStartSide}",
                snake_even_completion_status =
                    "FROZEN_PENDING_PASSABLE_ROAD_CALCULATION",
                active_start_ep = plan.ActiveStartEndpoint,
                kmz_agl_m = plan.Options.KmzAglM,
                kmz_speed_mps = plan.Options.KmzSpeedMps
            },
            access_assessments = accessRows,
            access_planning_status = LaunchSitePlanning.Enabled ? "ACTIVE" : "FROZEN",
            access_distance_method = LaunchSitePlanning.Enabled ? "WGS84 surface distance; no terrain, obstacle or automatic sortie repartition" : null,
            summary = new
            {
                match_mode = plan.Inference.MatchMode,
                merged_duplicate_rows = plan.Inference.Merged,
                line_count = plan.OrderedLines.Count,
                sortie_count = plan.Sorties.Count,
                total_distance_km = totalDistance / 1000.0,
                access_assessed_sortie_count = accessRows.Count(a => a.TotalM.HasValue),
                access_over_limit_sortie_count = accessRows.Count(a => a.Status == "OVER_LIMIT"),
                full_distance_km = LaunchSitePlanning.Enabled && accessRows.All(a => a.TotalM.HasValue) ? (double?)(accessRows.Sum(a => a.TotalM!.Value) / 1000.0) : null,
                over_limit_sortie_count = plan.Sorties.Count(s =>
                    RouteCoordinateMath.RouteLength(
                        s.Points, plan.Mode, plan.CentralMeridian)
                    > plan.Options.MaxSortieKm * 1000.0),
                over_limit_round_trip_count = plan.OverLimitRoundTrips.Count,
                line_direction = plan.GeometryDiagnostic.LineDirection,
                progress_direction = plan.GeometryDiagnostic.ProgressDirection,
                line_azimuth_deg = FiniteOrNull(plan.GeometryDiagnostic.AzimuthDegrees),
                median_line_km = FiniteOrNull(plan.GeometryDiagnostic.MedianLineKm)
            },
            warnings,
            errors = Array.Empty<string>(),
            output_paths = outputNames,
            output_sha256 = outputHashes,
            confirmed_by = "",
            confirmed_at = ""
        };

        File.WriteAllText(fullManifest, JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true
        }));

        if (writeArtifactIdentity)
        {
            if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(workareaId))
                throw new InvalidOperationException(
                    "正式航线 manifest 身份写入必须提供 canonical ProjectId + WorkareaId。");
            var lineage = new GeneratedArtifactLineage(
                [],
                [],
                plan.Sorties.SelectMany(sortie => sortie.TaskReferences)
                    .Select(reference => reference.SurveyTaskStableId)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                plan.Sorties.SelectMany(sortie => sortie.TaskReferences)
                    .Select(reference => reference.SurveyLineStableId)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                plan.ExecutionSegments.Select(segment => segment.SegmentStableId)
                    .Concat(plan.Sorties.SelectMany(sortie => sortie.TaskReferences)
                        .Select(reference => reference.ExecutionSegmentStableId))
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                plan.PartitionCode ?? "",
                plan.PartitionManifestPath ?? "");
            GeneratedArtifactIdentity.Write(
                fullManifest, "manifest", "route_manifest",
                ArtifactService.Sha256(source),
                [source],
                artifactId: artifactId,
                qualityStatus: status,
                context: new GeneratedArtifactContext(projectId, workareaId),
                lineage: lineage);
        }
        return fullManifest;
    }
}
