using System.Text.Json;
using System.Text.Json.Serialization;

namespace MagOps.Core.Domain;

public abstract class ExtensibleRecord
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class WorkspaceRecord : ExtensibleRecord
{
    [JsonPropertyName("schema_version")] public string SchemaVersion { get; set; } = AppConstants.SchemaVersion;
    [JsonPropertyName("entity_type")] public string EntityType { get; set; } = "project";
    [JsonPropertyName("project_id")] public string ProjectId { get; set; } = "";
    [JsonPropertyName("workspace_id")] public string WorkspaceId { get; set; } = "";
    [JsonPropertyName("display_name")] public string DisplayName { get; set; } = "";
    [JsonPropertyName("root")] public string Root { get; set; } = "";
    [JsonPropertyName("parent_workspace_id")] public string? ParentWorkspaceId { get; set; }
    [JsonPropertyName("workarea_root")] public string WorkareaRoot { get; set; } = "10_工区";
    [JsonPropertyName("central_meridian")] public double? CentralMeridian { get; set; }
    [JsonPropertyName("time_zone_id")] public string TimeZoneId { get; set; } = "Asia/Shanghai";
    [JsonPropertyName("central_meridian_source")] public string CentralMeridianSource { get; set; } = "";
    [JsonPropertyName("central_meridian_confirmed_at")] public string CentralMeridianConfirmedAt { get; set; } = "";
    [JsonPropertyName("status")] public string Status { get; set; } = "PRECHECK";
    [JsonPropertyName("created_at")] public string CreatedAt { get; set; } = "";
    [JsonPropertyName("updated_at")] public string UpdatedAt { get; set; } = "";
    [JsonPropertyName("gates")] public Dictionary<string, string> Gates { get; set; } = new();
    [JsonPropertyName("gate_records")] public Dictionary<string, GateRecord> GateRecords { get; set; } = new();
    [JsonPropertyName("notes")] public List<string> Notes { get; set; } = [];
}

public sealed class GateRecord : ExtensibleRecord
{
    [JsonPropertyName("gate")] public string Gate { get; set; } = "";
    [JsonPropertyName("from")] public string From { get; set; } = "PENDING";
    [JsonPropertyName("status")] public string Status { get; set; } = "PENDING";
    [JsonPropertyName("note")] public string Note { get; set; } = "";
    [JsonPropertyName("evidence_artifacts")] public List<string> EvidenceArtifacts { get; set; } = [];
    [JsonPropertyName("confirmed_by")] public string ConfirmedBy { get; set; } = "";
    [JsonPropertyName("confirmed_at")] public string ConfirmedAt { get; set; } = "";
}

public sealed class WorkareaRecord : ExtensibleRecord
{
    [JsonPropertyName("previous_display_names")] public List<string> PreviousDisplayNames { get; set; } = [];
    [JsonPropertyName("entity_type")] public string EntityType { get; set; } = "workarea";
    [JsonPropertyName("workarea_id")] public string WorkareaId { get; set; } = "";
    [JsonPropertyName("subarea_id")] public string SubareaId { get; set; } = "";
    [JsonPropertyName("display_name")] public string DisplayName { get; set; } = "";
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("status")] public string Status { get; set; } = "PRECHECK";
    [JsonPropertyName("gates")] public Dictionary<string, string> Gates { get; set; } = new();
    [JsonPropertyName("notes")] public string Notes { get; set; } = "";
    [JsonPropertyName("updated_at")] public string UpdatedAt { get; set; } = "";
}

public sealed class TaskRecord : ExtensibleRecord
{
    [JsonPropertyName("task_id")] public string TaskId { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("workarea")] public string Workarea { get; set; } = "";
    [JsonPropertyName("subarea")] public string Subarea { get; set; } = "";
    [JsonPropertyName("owner")] public string Owner { get; set; } = "";
    [JsonPropertyName("due")] public string Due { get; set; } = "";
    [JsonPropertyName("priority")] public string Priority { get; set; } = "P1";
    [JsonPropertyName("status")] public string Status { get; set; } = "TODO";
    [JsonPropertyName("notes")] public string Notes { get; set; } = "";
    [JsonPropertyName("created_at")] public string CreatedAt { get; set; } = "";
    [JsonPropertyName("updated_at")] public string UpdatedAt { get; set; } = "";
}

public sealed class ArtifactRecord : ExtensibleRecord
{
    [JsonPropertyName("artifact_id")] public string ArtifactId { get; set; } = "";
    [JsonPropertyName("workarea_ref")] public string WorkareaRef { get; set; } = "";
    [JsonPropertyName("workarea_assignment_status")] public string WorkareaAssignmentStatus { get; set; } = "";
    [JsonPropertyName("previous_paths")] public List<string> PreviousPaths { get; set; } = [];
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("role")] public string Role { get; set; } = "other";
    [JsonPropertyName("source_kind")] public string SourceKind { get; set; } = "directory_scan";
    [JsonPropertyName("source_ref")] public string SourceRef { get; set; } = "";
    [JsonPropertyName("sha256")] public string? Sha256 { get; set; }
    [JsonPropertyName("hash_status")] public string HashStatus { get; set; } = "NOT_COMPUTED";
    [JsonPropertyName("size_bytes")] public long SizeBytes { get; set; }
    [JsonPropertyName("modified_at")] public string ModifiedAt { get; set; } = "";
    [JsonPropertyName("processing_stage")] public string ProcessingStage { get; set; } = "unclassified";
    [JsonPropertyName("version_family")] public string VersionFamily { get; set; } = "";
    [JsonPropertyName("version_label")] public string VersionLabel { get; set; } = "original";
    [JsonPropertyName("selected")] public bool? Selected { get; set; }
    [JsonPropertyName("quality_status")] public string QualityStatus { get; set; } = "UNREVIEWED";
    [JsonPropertyName("spatial_reference_status")] public string SpatialReferenceStatus { get; set; } = "UNVERIFIED";
    [JsonPropertyName("source_freshness_status")] public string SourceFreshnessStatus { get; set; } = "UNVERIFIED";
    [JsonPropertyName("freshness_reason")] public string FreshnessReason { get; set; } = "";
    [JsonIgnore]
    public string FreshnessStatus =>
        SpatialReferenceStatus.Equals("STALE", StringComparison.OrdinalIgnoreCase) ||
        SourceFreshnessStatus.Equals("STALE", StringComparison.OrdinalIgnoreCase) ? "STALE" :
        SpatialReferenceStatus.Equals("UNVERIFIED", StringComparison.OrdinalIgnoreCase) ||
        SourceFreshnessStatus.Equals("UNVERIFIED", StringComparison.OrdinalIgnoreCase) ? "UNVERIFIED" :
        SpatialReferenceStatus.Equals("CURRENT", StringComparison.OrdinalIgnoreCase) ||
        SourceFreshnessStatus.Equals("CURRENT", StringComparison.OrdinalIgnoreCase) ? "CURRENT" :
        "NOT_APPLICABLE";
    [JsonPropertyName("derived_from")] public List<string> DerivedFrom { get; set; } = [];
    [JsonPropertyName("source_file_stable_ids")] public List<string> SourceFileStableIds { get; set; } = [];
    [JsonPropertyName("gsm_session_stable_ids")] public List<string> GsmSessionStableIds { get; set; } = [];
    [JsonPropertyName("survey_task_stable_ids")] public List<string> SurveyTaskStableIds { get; set; } = [];
    [JsonPropertyName("survey_line_stable_ids")] public List<string> SurveyLineStableIds { get; set; } = [];
    [JsonPropertyName("execution_segment_stable_ids")] public List<string> ExecutionSegmentStableIds { get; set; } = [];
    [JsonPropertyName("processing_run_id")] public string ProcessingRunId { get; set; } = "";
    [JsonPropertyName("notes")] public string Notes { get; set; } = "";
    [JsonPropertyName("external_origin")] public string ExternalOrigin { get; set; } = "";
    [JsonPropertyName("lineage_relation")] public string LineageRelation { get; set; } = "";
    [JsonPropertyName("metadata_origin")] public string MetadataOrigin { get; set; } = "scan";
    [JsonPropertyName("change_status")] public string ChangeStatus { get; set; } = "NEW";
    [JsonPropertyName("scan_status")] public string ScanStatus { get; set; } = "SCANNED";
    [JsonPropertyName("last_scanned_at")] public string LastScannedAt { get; set; } = "";
}

public sealed class ProcessingRunRecord : ExtensibleRecord
{
    [JsonPropertyName("processing_run_id")] public string ProcessingRunId { get; set; } = "";
    [JsonPropertyName("project_id")] public string ProjectId { get; set; } = "";
    [JsonPropertyName("workarea_id")] public string WorkareaId { get; set; } = "";
    [JsonPropertyName("run_kind")] public string RunKind { get; set; } = "";
    [JsonPropertyName("status")] public string Status { get; set; } = "STARTED";
    [JsonPropertyName("started_at")] public string StartedAt { get; set; } = "";
    [JsonPropertyName("completed_at")] public string CompletedAt { get; set; } = "";
    [JsonPropertyName("artifact_ids")] public List<string> ArtifactIds { get; set; } = [];
    [JsonPropertyName("failure_code")] public string FailureCode { get; set; } = "";
    [JsonPropertyName("failure_message")] public string FailureMessage { get; set; } = "";
}

public sealed class EquipmentRecord : ExtensibleRecord
{
    [JsonPropertyName("record_id")] public string RecordId { get; set; } = "";
    [JsonPropertyName("equipment_id")] public string EquipmentId { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("model")] public string Model { get; set; } = "";
    [JsonPropertyName("category")] public string Category { get; set; } = "";
    [JsonPropertyName("equipment_type")] public string EquipmentType { get; set; } = "";
    [JsonPropertyName("serial_no")] public string SerialNo { get; set; } = "";
    [JsonPropertyName("notes")] public string Notes { get; set; } = "";
    [JsonPropertyName("checklist_template_id")] public string ChecklistTemplateId { get; set; } = "";
    [JsonPropertyName("evidence_artifacts")] public List<string> EvidenceArtifacts { get; set; } = [];
    [JsonPropertyName("checklist_status")] public string ChecklistStatus { get; set; } = "NOT_REQUIRED";
    [JsonPropertyName("test_status")] public string TestStatus { get; set; } = "PENDING";
    [JsonPropertyName("release_status")] public string ReleaseStatus { get; set; } = "PENDING";
    [JsonPropertyName("created_at")] public string CreatedAt { get; set; } = "";
    [JsonPropertyName("updated_at")] public string UpdatedAt { get; set; } = "";
}

public sealed class EquipmentChecklistState : ExtensibleRecord
{
    [JsonPropertyName("checklist_id")] public string ChecklistId { get; set; } = "";
    [JsonPropertyName("equipment_ref")] public string EquipmentRef { get; set; } = "";
    [JsonPropertyName("equipment_id")] public string EquipmentId { get; set; } = "";
    [JsonPropertyName("equipment_name")] public string EquipmentName { get; set; } = "";
    [JsonPropertyName("equipment_model")] public string EquipmentModel { get; set; } = "";
    [JsonPropertyName("template_id")] public string TemplateId { get; set; } = "";
    [JsonPropertyName("checked_item_ids")] public List<string> CheckedItemIds { get; set; } = [];
    [JsonPropertyName("quantity_values")] public Dictionary<string, int> QuantityValues { get; set; } = new();
    [JsonPropertyName("custom_items")] public List<EquipmentChecklistItem> CustomItems { get; set; } = [];
    [JsonPropertyName("status")] public string Status { get; set; } = "PENDING";
    [JsonPropertyName("required_total")] public int RequiredTotal { get; set; }
    [JsonPropertyName("required_checked")] public int RequiredChecked { get; set; }
    [JsonPropertyName("optional_total")] public int OptionalTotal { get; set; }
    [JsonPropertyName("optional_checked")] public int OptionalChecked { get; set; }
    [JsonPropertyName("created_at")] public string CreatedAt { get; set; } = "";
    [JsonPropertyName("updated_at")] public string UpdatedAt { get; set; } = "";
}

public sealed class EquipmentChecklistItem : ExtensibleRecord
{
    [JsonPropertyName("item_id")] public string ItemId { get; set; } = "";
    [JsonPropertyName("section")] public string Section { get; set; } = "";
    [JsonPropertyName("category")] public string Category { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("expected")] public string Expected { get; set; } = "";
    [JsonPropertyName("requirement")] public string Requirement { get; set; } = "OPTIONAL";
    [JsonPropertyName("source_attribute")] public string SourceAttribute { get; set; } = "";
    [JsonPropertyName("check")] public string Check { get; set; } = "";
    [JsonPropertyName("quantity_editable")] public bool QuantityEditable { get; set; }
    [JsonPropertyName("default_quantity")] public int DefaultQuantity { get; set; }
    [JsonPropertyName("min_quantity")] public int MinQuantity { get; set; }
    [JsonPropertyName("quantity_unit")] public string QuantityUnit { get; set; } = "";
    [JsonPropertyName("is_custom")] public bool IsCustom { get; set; }
}

public sealed class EquipmentTestRecord : ExtensibleRecord
{
    [JsonPropertyName("test_id")] public string TestId { get; set; } = "";
    [JsonPropertyName("equipment_ref")] public string EquipmentRef { get; set; } = "";
    [JsonPropertyName("equipment_id")] public string EquipmentId { get; set; } = "";
    [JsonPropertyName("equipment_name")] public string EquipmentName { get; set; } = "";
    [JsonPropertyName("test_type")] public string TestType { get; set; } = "";
    [JsonPropertyName("status")] public string Status { get; set; } = "PENDING";
    [JsonPropertyName("note")] public string Note { get; set; } = "";
    [JsonPropertyName("evidence_artifacts")] public List<string> EvidenceArtifacts { get; set; } = [];
    [JsonPropertyName("supersedes_test_id")] public string SupersedesTestId { get; set; } = "";
    [JsonPropertyName("tested_at")] public string TestedAt { get; set; } = "";
}

public sealed class FlightBatchRecord : ExtensibleRecord
{
    [JsonPropertyName("batch_id")] public string BatchId { get; set; } = "";
    [JsonPropertyName("date")] public string Date { get; set; } = "";
    [JsonPropertyName("workarea")] public string Workarea { get; set; } = "";
    [JsonPropertyName("subarea")] public string Subarea { get; set; } = "";
    [JsonPropertyName("aircraft")] public string Aircraft { get; set; } = "";
    [JsonPropertyName("magnetometer")] public string Magnetometer { get; set; } = "";
    [JsonPropertyName("base_station")] public string BaseStation { get; set; } = "";
    [JsonPropertyName("route_version")] public string RouteVersion { get; set; } = "";
    [JsonPropertyName("operator")] public string Operator { get; set; } = "";
    [JsonPropertyName("start")] public string Start { get; set; } = "";
    [JsonPropertyName("end")] public string End { get; set; } = "";
    [JsonPropertyName("raw_artifacts")] public List<string> RawArtifacts { get; set; } = [];
    [JsonPropertyName("result")] public string Result { get; set; } = "";
    [JsonPropertyName("issues")] public List<string> Issues { get; set; } = [];
    [JsonPropertyName("status")] public string Status { get; set; } = "PLANNED";
    [JsonPropertyName("notes")] public string Notes { get; set; } = "";
    [JsonPropertyName("task_lock_warning")] public string TaskLockWarning { get; set; } = "";
    [JsonPropertyName("created_at")] public string CreatedAt { get; set; } = "";
    [JsonPropertyName("updated_at")] public string UpdatedAt { get; set; } = "";
}

public sealed class BaseStationLogRecord : ExtensibleRecord
{
    [JsonPropertyName("record_id")] public string RecordId { get; set; } = "";
    [JsonPropertyName("date")] public string Date { get; set; } = "";
    [JsonPropertyName("station_id")] public string StationId { get; set; } = "";
    [JsonPropertyName("site")] public string Site { get; set; } = "";
    [JsonPropertyName("longitude")] public string Longitude { get; set; } = "";
    [JsonPropertyName("latitude")] public string Latitude { get; set; } = "";
    [JsonPropertyName("elevation_m")] public string ElevationM { get; set; } = "";
    [JsonPropertyName("crs")] public string Crs { get; set; } = "";
    [JsonPropertyName("time_basis")] public string TimeBasis { get; set; } = "UTC";
    [JsonPropertyName("sampling_interval_s")] public string SamplingIntervalS { get; set; } = "";
    [JsonPropertyName("start")] public string Start { get; set; } = "";
    [JsonPropertyName("end")] public string End { get; set; } = "";
    [JsonPropertyName("artifacts")] public List<string> Artifacts { get; set; } = [];
    [JsonPropertyName("status")] public string Status { get; set; } = "RECORDED";
    [JsonPropertyName("notes")] public string Notes { get; set; } = "";
    [JsonPropertyName("task_lock_warning")] public string TaskLockWarning { get; set; } = "";
    [JsonPropertyName("created_at")] public string CreatedAt { get; set; } = "";
    [JsonPropertyName("updated_at")] public string UpdatedAt { get; set; } = "";
}

public sealed class DailyLogRecord : ExtensibleRecord
{
    [JsonPropertyName("log_id")] public string LogId { get; set; } = "";
    [JsonPropertyName("date")] public string Date { get; set; } = "";
    [JsonPropertyName("weather")] public string Weather { get; set; } = "";
    [JsonPropertyName("site")] public string Site { get; set; } = "";
    [JsonPropertyName("completed_tasks")] public List<string> CompletedTasks { get; set; } = [];
    [JsonPropertyName("new_files")] public List<string> NewFiles { get; set; } = [];
    [JsonPropertyName("anomalies")] public List<string> Anomalies { get; set; } = [];
    [JsonPropertyName("next_actions")] public List<string> NextActions { get; set; } = [];
    [JsonPropertyName("handoff_status")] public string HandoffStatus { get; set; } = "DRAFT";
    [JsonPropertyName("notes")] public string Notes { get; set; } = "";
    [JsonPropertyName("created_at")] public string CreatedAt { get; set; } = "";
    [JsonPropertyName("updated_at")] public string UpdatedAt { get; set; } = "";
}



public sealed class FieldDataCatalogRecord : ExtensibleRecord
{
    [JsonPropertyName("file_id")] public string FileId { get; set; } = "";
    [JsonPropertyName("workarea_path")] public string WorkareaPath { get; set; } = "";
    [JsonPropertyName("relative_path")] public string RelativePath { get; set; } = "";
    [JsonPropertyName("original_file_name")] public string OriginalFileName { get; set; } = "";
    [JsonPropertyName("source_path_hint")] public string SourcePathHint { get; set; } = "";
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    [JsonPropertyName("size_bytes")] public long SizeBytes { get; set; }
    [JsonPropertyName("source_last_write_utc")] public string SourceLastWriteUtc { get; set; } = "";
    [JsonPropertyName("import_batch_id")] public string ImportBatchId { get; set; } = "";
    [JsonPropertyName("imported_at")] public string ImportedAt { get; set; } = "";
    [JsonPropertyName("sensor_family")] public string SensorFamily { get; set; } = "";
    [JsonPropertyName("data_role")] public string DataRole { get; set; } = "";
    [JsonPropertyName("record_count")] public int RecordCount { get; set; }
    [JsonPropertyName("first_time_utc")] public string FirstTimeUtc { get; set; } = "";
    [JsonPropertyName("last_time_utc")] public string LastTimeUtc { get; set; } = "";
    [JsonPropertyName("beijing_dates")] public List<string> BeijingDates { get; set; } = [];
    [JsonPropertyName("parser_version")] public string ParserVersion { get; set; } = "";
    [JsonPropertyName("status")] public string Status { get; set; } = "IMPORTED";
}

public sealed class DjiFlightSessionRecord : ExtensibleRecord
{
    [JsonPropertyName("session_id")] public string SessionId { get; set; } = "";
    [JsonPropertyName("workarea_path")] public string WorkareaPath { get; set; } = "";
    [JsonPropertyName("source_file")] public string SourceFile { get; set; } = "";
    [JsonPropertyName("source_file_name")] public string SourceFileName { get; set; } = "";
    [JsonPropertyName("source_sha256")] public string SourceSha256 { get; set; } = "";
    [JsonPropertyName("format_version")] public int FormatVersion { get; set; }
    [JsonPropertyName("internal_start_utc")] public string InternalStartUtc { get; set; } = "";
    [JsonPropertyName("internal_end_utc")] public string InternalEndUtc { get; set; } = "";
    [JsonPropertyName("beijing_date")] public string BeijingDate { get; set; } = "";
    [JsonPropertyName("duration_seconds")] public double DurationSeconds { get; set; }
    [JsonPropertyName("total_distance_km")] public double TotalDistanceKm { get; set; }
    [JsonPropertyName("start_longitude")] public double? StartLongitude { get; set; }
    [JsonPropertyName("start_latitude")] public double? StartLatitude { get; set; }
    [JsonPropertyName("max_height_m")] public double MaxHeightM { get; set; }
    [JsonPropertyName("max_horizontal_speed_mps")] public double MaxHorizontalSpeedMps { get; set; }
    [JsonPropertyName("max_vertical_speed_mps")] public double MaxVerticalSpeedMps { get; set; }
    [JsonPropertyName("aircraft_name")] public string AircraftName { get; set; } = "";
    [JsonPropertyName("app_version")] public string AppVersion { get; set; } = "";
    [JsonPropertyName("filename_time_text")] public string FilenameTimeText { get; set; } = "";
    [JsonPropertyName("parser_version")] public string ParserVersion { get; set; } = "";
    [JsonPropertyName("import_batch_id")] public string ImportBatchId { get; set; } = "";
    [JsonPropertyName("imported_at")] public string ImportedAt { get; set; } = "";
}

public sealed class DjiMcdatCatalogRecord : ExtensibleRecord
{
    [JsonPropertyName("mcdat_id")] public string McdatId { get; set; } = "";
    [JsonPropertyName("workarea_path")] public string WorkareaPath { get; set; } = "";
    [JsonPropertyName("source_file_name")] public string SourceFileName { get; set; } = "";
    [JsonPropertyName("source_path_hint")] public string SourcePathHint { get; set; } = "";
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    [JsonPropertyName("size_bytes")] public long SizeBytes { get; set; }
    [JsonPropertyName("filename_time_text")] public string FilenameTimeText { get; set; } = "";
    [JsonPropertyName("linked_session_id")] public string LinkedSessionId { get; set; } = "";
    [JsonPropertyName("registered_at")] public string RegisteredAt { get; set; } = "";
}

public sealed class FieldOperationsSettingsRecord : ExtensibleRecord
{
    [JsonPropertyName("dem_height")] public DemHeightOptions? DemHeight { get; set; }
    [JsonPropertyName("spatial_screening")] public WorkareaScreeningInput? SpatialScreening { get; set; }
    [JsonPropertyName("workarea_path")] public string WorkareaPath { get; set; } = "";
    [JsonPropertyName("field_data_folder")] public string FieldDataFolder { get; set; } = "";
    [JsonPropertyName("expected_daily_sorties")] public int ExpectedDailySorties { get; set; } = 8;
    [JsonPropertyName("accepted_relocations")] public int AcceptedRelocations { get; set; } = 1;
    [JsonPropertyName("updated_at")] public string UpdatedAt { get; set; } = "";
}

public sealed class FieldTakeoffRecommendationRecord : ExtensibleRecord
{
    [JsonPropertyName("plan_day")] public int PlanDay { get; set; } = 1;
    [JsonPropertyName("recommendation_id")] public string RecommendationId { get; set; } = "";
    [JsonPropertyName("longitude")] public double Longitude { get; set; }
    [JsonPropertyName("latitude")] public double Latitude { get; set; }
    [JsonPropertyName("sortie_names")] public List<string> SortieNames { get; set; } = [];
    [JsonPropertyName("mean_distance_m")] public double MeanDistanceM { get; set; }
    [JsonPropertyName("max_distance_m")] public double MaxDistanceM { get; set; }
}

public sealed class FieldDailyProgressRecord : ExtensibleRecord
{
    [JsonPropertyName("progress_id")] public string ProgressId { get; set; } = "";
    [JsonPropertyName("date")] public string Date { get; set; } = "";
    [JsonPropertyName("workarea")] public string Workarea { get; set; } = "";
    [JsonPropertyName("route_source")] public string RouteSource { get; set; } = "";
    [JsonPropertyName("route_fingerprint")] public string RouteFingerprint { get; set; } = "";
    [JsonPropertyName("planned_sortie_count")] public int PlannedSortieCount { get; set; }
    [JsonPropertyName("today_completed_sorties")] public List<string> TodayCompletedSorties { get; set; } = [];
    [JsonPropertyName("completed_sorties")] public List<string> CompletedSorties { get; set; } = [];
    [JsonPropertyName("remaining_sorties")] public List<string> RemainingSorties { get; set; } = [];
    [JsonPropertyName("today_repeated_sorties")] public List<string> TodayRepeatedSorties { get; set; } = [];
    [JsonPropertyName("repeated_sorties")] public List<string> RepeatedSorties { get; set; } = [];
    [JsonPropertyName("gsm19t_files")] public List<string> Gsm19TFiles { get; set; } = [];
    [JsonPropertyName("uav_mag_files")] public List<string> UavMagFiles { get; set; } = [];
    [JsonPropertyName("gsm19t_sample_count")] public int Gsm19TSampleCount { get; set; }
    [JsonPropertyName("uav_mag_sample_count")] public int UavMagSampleCount { get; set; }
    [JsonPropertyName("uav_track_point_count")] public int UavTrackPointCount { get; set; }
    [JsonPropertyName("field_data_folder")] public string FieldDataFolder { get; set; } = "";
    [JsonPropertyName("field_data_fingerprint")] public string FieldDataFingerprint { get; set; } = "";
    [JsonPropertyName("uav_canonical_sample_count")] public int UavCanonicalSampleCount { get; set; }
    [JsonPropertyName("base_canonical_sample_count")] public int BaseCanonicalSampleCount { get; set; }
    [JsonPropertyName("field_data_quality_issue_count")] public int FieldDataQualityIssueCount { get; set; }
    [JsonPropertyName("gemlink_utc_dates")] public List<string> GemLinkUtcDates { get; set; } = [];
    [JsonPropertyName("expected_daily_sorties")] public int ExpectedDailySorties { get; set; } = 8;
    [JsonPropertyName("accepted_relocations")] public int AcceptedRelocations { get; set; } = 1;
    [JsonPropertyName("dji_flight_count")] public int DjiFlightCount { get; set; }
    [JsonPropertyName("dji_flight_distance_km")] public double DjiFlightDistanceKm { get; set; }
    [JsonPropertyName("dji_flight_duration_seconds")] public double DjiFlightDurationSeconds { get; set; }
    [JsonPropertyName("dji_mag_matched_flight_count")] public int DjiMagMatchedFlightCount { get; set; }
    [JsonPropertyName("dji_flight_time_range_beijing")] public string DjiFlightTimeRangeBeijing { get; set; } = "";
    [JsonPropertyName("dji_flight_session_ids")] public List<string> DjiFlightSessionIds { get; set; } = [];
    [JsonPropertyName("progress_algorithm_version")] public string ProgressAlgorithmVersion { get; set; } = "";
    [JsonPropertyName("survey_line_matching_available")] public bool SurveyLineMatchingAvailable { get; set; }
    [JsonPropertyName("planned_survey_line_km")] public double PlannedSurveyLineKm { get; set; }
    [JsonPropertyName("today_completed_survey_line_km")] public double TodayCompletedSurveyLineKm { get; set; }
    [JsonPropertyName("completed_survey_line_km")] public double CompletedSurveyLineKm { get; set; }
    [JsonPropertyName("remaining_survey_line_km")] public double RemainingSurveyLineKm { get; set; }
    [JsonPropertyName("today_completed_survey_line_task_ids")] public List<string> TodayCompletedSurveyLineTaskIds { get; set; } = [];
    [JsonPropertyName("completed_survey_line_task_ids")] public List<string> CompletedSurveyLineTaskIds { get; set; } = [];
    [JsonPropertyName("progress_time_zone")] public string ProgressTimeZone { get; set; } = "";
    [JsonPropertyName("daily_completion_percent")] public double DailyCompletionPercent { get; set; }
    [JsonPropertyName("total_completion_percent")] public double TotalCompletionPercent { get; set; }
    [JsonPropertyName("progress_summary")] public string ProgressSummary { get; set; } = "";
    [JsonPropertyName("data_summary")] public string DataSummary { get; set; } = "";
    [JsonPropertyName("next_action_summary")] public string NextActionSummary { get; set; } = "";
    [JsonPropertyName("warnings")] public List<string> Warnings { get; set; } = [];
    [JsonPropertyName("recommendations")] public List<FieldTakeoffRecommendationRecord> Recommendations { get; set; } = [];
    [JsonPropertyName("created_at")] public string CreatedAt { get; set; } = "";
    [JsonPropertyName("updated_at")] public string UpdatedAt { get; set; } = "";
}

public sealed class IssueRecord : ExtensibleRecord
{
    [JsonPropertyName("issue_id")] public string IssueId { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("severity")] public string Severity { get; set; } = "MEDIUM";
    [JsonPropertyName("impact")] public string Impact { get; set; } = "";
    [JsonPropertyName("temporary_measure")] public string TemporaryMeasure { get; set; } = "";
    [JsonPropertyName("owner")] public string Owner { get; set; } = "";
    [JsonPropertyName("resolution_evidence")] public List<string> ResolutionEvidence { get; set; } = [];
    [JsonPropertyName("status")] public string Status { get; set; } = "OPEN";
    [JsonPropertyName("notes")] public string Notes { get; set; } = "";
    [JsonPropertyName("created_at")] public string CreatedAt { get; set; } = "";
    [JsonPropertyName("updated_at")] public string UpdatedAt { get; set; } = "";
}

public sealed class CrsDefinitionRecord : ExtensibleRecord
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("kind")] public string Kind { get; set; } = "geographic";
    [JsonPropertyName("epsg")] public string Epsg { get; set; } = "";
    [JsonPropertyName("datum")] public string Datum { get; set; } = "WGS84";
    [JsonPropertyName("central_meridian")] public double? CentralMeridian { get; set; }
    [JsonPropertyName("false_easting_m")] public double FalseEastingM { get; set; }
    [JsonPropertyName("false_northing_m")] public double FalseNorthingM { get; set; }
    [JsonPropertyName("unit")] public string Unit { get; set; } = "degree";
    [JsonPropertyName("axis_order")] public string AxisOrder { get; set; } = "lon,lat";
    [JsonPropertyName("conversion_version")] public string ConversionVersion { get; set; } = "M4-1";
    [JsonPropertyName("notes")] public string Notes { get; set; } = "";
}

public sealed class CoordinateRecord : ExtensibleRecord
{
    [JsonPropertyName("coordinate_id")] public string CoordinateId { get; set; } = "";
    [JsonPropertyName("source_value")] public string SourceValue { get; set; } = "";
    [JsonPropertyName("lon_deg")] public double? LongitudeDeg { get; set; }
    [JsonPropertyName("lat_deg")] public double? LatitudeDeg { get; set; }
    [JsonPropertyName("x_m")] public double? XM { get; set; }
    [JsonPropertyName("y_m")] public double? YM { get; set; }
    [JsonPropertyName("z_m")] public double? ZM { get; set; }
    [JsonPropertyName("z_type")] public string ZType { get; set; } = "";
    [JsonPropertyName("crs")] public string Crs { get; set; } = "";
    [JsonPropertyName("source_artifact")] public string SourceArtifact { get; set; } = "";
    [JsonPropertyName("source_row")] public int? SourceRow { get; set; }
    [JsonPropertyName("axis_order")] public string AxisOrder { get; set; } = "lon,lat";
    [JsonPropertyName("notes")] public string Notes { get; set; } = "";
}

public sealed class RouteResultRecord : ExtensibleRecord
{
    [JsonPropertyName("result_id")] public string ResultId { get; set; } = "";
    [JsonPropertyName("manifest_path")] public string ManifestPath { get; set; } = "";
    [JsonPropertyName("manifest_sha256")] public string ManifestSha256 { get; set; } = "";
    [JsonPropertyName("schema_version")] public string SchemaVersion { get; set; } = "";
    [JsonPropertyName("algorithm_version")] public string AlgorithmVersion { get; set; } = "";
    [JsonPropertyName("created_at")] public string CreatedAt { get; set; } = "";
    [JsonPropertyName("command")] public string Command { get; set; } = "";
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("project_id")] public string ProjectId { get; set; } = "";
    [JsonPropertyName("workarea_id")] public string WorkareaId { get; set; } = "";
    [JsonPropertyName("subarea_id")] public string SubareaId { get; set; } = "";
    [JsonPropertyName("route_id")] public string RouteId { get; set; } = "";
    [JsonPropertyName("sortie_id")] public string SortieId { get; set; } = "";
    [JsonPropertyName("source_paths")] public List<string> SourcePaths { get; set; } = [];
    [JsonPropertyName("input_sha256")] public Dictionary<string, string> InputSha256 { get; set; } = new();
    [JsonPropertyName("parameters")] public Dictionary<string, JsonElement> Parameters { get; set; } = new();
    [JsonPropertyName("summary")] public Dictionary<string, JsonElement> Summary { get; set; } = new();
    [JsonPropertyName("warnings")] public List<string> Warnings { get; set; } = [];
    [JsonPropertyName("errors")] public List<string> Errors { get; set; } = [];
    [JsonPropertyName("output_paths")] public List<string> OutputPaths { get; set; } = [];
    [JsonPropertyName("output_sha256")] public Dictionary<string, string> OutputSha256 { get; set; } = new();
    [JsonPropertyName("confirmed_by")] public string ConfirmedBy { get; set; } = "";
    [JsonPropertyName("confirmed_at")] public string ConfirmedAt { get; set; } = "";
    [JsonPropertyName("imported_at")] public string ImportedAt { get; set; } = "";
}

public sealed record TaskLockInfo(
    string Status,
    string TaskId,
    string DialogueLine,
    string DialogueId,
    string AllowedWriteScope,
    string HandoffStatus,
    string Path
);
