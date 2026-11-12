namespace MagOps.Core.Analysis;

/// <summary>
/// Sand is an experimental source tree. These statuses intentionally prevent
/// unfinished or superseded logic from being presented as production-ready.
/// </summary>
public static class SandMigrationStatus
{
    public const string RouteConnector = "RETIRED_SUPERSEDED_BY_V25_BAT";
    public const string ElevationCsvToKml = "PORTED_CORE_AWAITING_GOLDEN_SAMPLE";
    public const string GroupAverage = "PORTED_TEXT_TABLE_CORE_AWAITING_GOLDEN_SAMPLE";
    public const string TableSplit = "PORTED_TEXT_TABLE_CORE_AWAITING_GOLDEN_SAMPLE";
    public const string TableMergeAndLineRenumber = "PORTED_CSV_CORE_AWAITING_GOLDEN_SAMPLE";
    public const string DynamicNoise = "PORTED_CORE_AWAITING_GOLDEN_SAMPLE";
    public const string FlightEvaluation = "DEFERRED_REQUIRES_PYPROJ_EQUIVALENCE_AND_GOLDEN_SAMPLE";
    public const string ExcelXlsxIo = "DEFERRED_REQUIRES_REAL_WORKBOOK_ACCEPTANCE";
}
