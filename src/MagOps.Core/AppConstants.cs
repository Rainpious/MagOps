namespace MagOps.Core;

public static class AppConstants
{
    public const string SchemaVersion = "0.4";
    public const string AppVersion = "0.9.59";

    public static readonly string[] ProjectStatuses =
    [
        "PRECHECK", "ACTIVE", "PAUSED", "REVIEW_REQUIRED", "BLOCKED", "RELEASED"
    ];

    public static readonly string[] TaskStatuses =
    [
        "TODO", "IN_PROGRESS", "BLOCKED", "DONE"
    ];

    public static readonly string[] GateStatuses =
    [
        "PENDING", "PASSED", "REVIEW_REQUIRED", "BLOCKED", "DEFERRED"
    ];

    public static readonly string[] GateNames =
    [
        "source", "coordinate", "equipment", "route", "field", "processing", "report"
    ];

    public static readonly string[] EquipmentTestStatuses =
    [
        "PENDING", "PASSED", "FAILED", "REVIEW_REQUIRED"
    ];

    public static readonly string[] EquipmentTestTypes =
    [
        "开机检查", "存储检查", "数据导出", "静态一致性", "连接检查", "现场磁环境检查"
    ];

    public static readonly string[] FlightStatuses =
    [
        "PLANNED", "IN_PROGRESS", "COMPLETED", "REVIEW_REQUIRED", "BLOCKED"
    ];

    public static readonly string[] IssueStatuses =
    [
        "OPEN", "IN_PROGRESS", "RESOLVED", "DEFERRED"
    ];

    public static readonly string[] IssueSeverities =
    [
        "LOW", "MEDIUM", "HIGH", "CRITICAL"
    ];

    public static readonly HashSet<string> ExcludedDirectories =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".magops", ".git", ".venv", "__pycache__", ".pytest_cache"
        };
}
