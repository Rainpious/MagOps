using System.Text.Json.Nodes;
using MagOps.Core.Domain;
using MagOps.Infrastructure.Storage;

namespace MagOps.Infrastructure.Services;

public sealed record QcCheck(string Name, string Status, string Detail, IReadOnlyList<string> Evidence);

public sealed record DailyQcOutcome(
    string QcId,
    string Date,
    string Status,
    IReadOnlyList<QcCheck> Checks,
    int FlightCount,
    int BaseStationCount,
    IReadOnlyList<string> ReflyCandidates,
    string ConfirmedBy,
    string CreatedAt);

public sealed class QualityService
{
    private readonly JsonStore _store;
    private readonly WorkspaceService _workspace;
    private readonly FieldworkService _fieldwork;
    private readonly ArtifactService _artifacts;
    private readonly RouteResultService _routes;

    public QualityService(
        JsonStore store,
        WorkspaceService workspace,
        FieldworkService fieldwork,
        ArtifactService artifacts,
        RouteResultService routes)
    {
        _store = store;
        _workspace = workspace;
        _fieldwork = fieldwork;
        _artifacts = artifacts;
        _routes = routes;
    }

    public DailyQcOutcome RunDailyQc(
        string root,
        string date,
        IEnumerable<string>? requiredRouteChecks = null,
        bool confirmGate = false,
        string confirmedBy = "")
    {
        if (string.IsNullOrWhiteSpace(date))
            throw new ArgumentException("QC 日期不能为空");

        using var writeLock = WorkspaceFileLock.AcquireForRoot(root);
        _workspace.GetWorkspace(root);

        var flights = _fieldwork.ListFlights(root).Where(x => x.Date == date).ToList();
        var stations = _fieldwork.ListBaseStations(root).Where(x => x.Date == date).ToList();
        var equipment = _fieldwork.ListEquipment(root);
        var issues = _fieldwork.ListIssues(root);
        var artifacts = _artifacts.List(root);
        var routeResults = _routes.List(root);

        var checks = new List<QcCheck>
        {
            Check("flight_batches", flights.Count > 0 ? "PASSED" : "BLOCKED", $"飞行批次 {flights.Count} 个"),
            Check("base_station_coverage",
                stations.Count > 0 ? "PASSED" : flights.Count > 0 ? "BLOCKED" : "REVIEW_REQUIRED",
                $"同日日变站记录 {stations.Count} 个")
        };

        var incompleteFiles = new List<string>();
        var equipmentProblems = new List<string>();
        var missingRoutes = new List<string>();

        var equipmentIndex = new Dictionary<string, EquipmentRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in equipment)
        {
            foreach (string key in new[] { row.RecordId, row.EquipmentId, row.Name, row.Model }
                         .Where(x => !string.IsNullOrWhiteSpace(x)))
                equipmentIndex[key] = row;
        }

        foreach (var flight in flights)
        {
            if (flight.Status == "COMPLETED" && flight.RawArtifacts.Count == 0)
                incompleteFiles.Add($"{flight.BatchId}: 未登记原始文件");

            foreach (string reference in flight.RawArtifacts)
                if (!ArtifactExists(root, reference, artifacts))
                    incompleteFiles.Add($"{flight.BatchId}: {reference}");

            if (string.IsNullOrWhiteSpace(flight.RouteVersion))
                missingRoutes.Add(flight.BatchId);

            foreach (var (field, reference) in new[]
            {
                ("aircraft", flight.Aircraft),
                ("magnetometer", flight.Magnetometer),
                ("base_station", flight.BaseStation)
            })
            {
                if (!string.IsNullOrWhiteSpace(reference)
                    && equipmentIndex.TryGetValue(reference, out var eq)
                    && eq.ReleaseStatus != "PASSED")
                    equipmentProblems.Add($"{flight.BatchId} {field}={reference}({eq.ReleaseStatus})");
            }
        }

        checks.Add(Check(
            "raw_file_completeness",
            incompleteFiles.Count > 0 ? "BLOCKED" : "PASSED",
            incompleteFiles.Count > 0 ? string.Join("；", incompleteFiles) : "已登记文件均可找到",
            incompleteFiles));
        checks.Add(Check(
            "equipment_release",
            equipmentProblems.Count > 0 ? "BLOCKED" : "PASSED",
            equipmentProblems.Count > 0 ? string.Join("；", equipmentProblems) : "关联设备未发现阻断状态"));
        checks.Add(Check(
            "route_version",
            missingRoutes.Count > 0 ? "REVIEW_REQUIRED" : "PASSED",
            missingRoutes.Count > 0 ? "未登记航线版本: " + string.Join(", ", missingRoutes) : "飞行批次已登记航线版本"));

        var openSevere = issues
            .Where(x => x.Status != "RESOLVED"
                && x.Status != "DEFERRED"
                && (x.Severity == "HIGH" || x.Severity == "CRITICAL"))
            .ToList();
        checks.Add(Check(
            "open_high_issues",
            openSevere.Count > 0 ? "BLOCKED" : "PASSED",
            $"未关闭高风险问题 {openSevere.Count} 个",
            openSevere.Select(x => x.IssueId)));

        var required = (requiredRouteChecks ?? [])
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var latestByCommand = routeResults
            .Where(x => !string.IsNullOrWhiteSpace(x.Command))
            .GroupBy(x => x.Command, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Last(), StringComparer.OrdinalIgnoreCase);
        var missingChecks = required.Where(x => !latestByCommand.ContainsKey(x)).ToList();
        var blockedChecks = required
            .Where(x => latestByCommand.TryGetValue(x, out var r) && r.Status != "PASSED")
            .ToList();
        string routeStatus = blockedChecks.Count > 0 ? "BLOCKED"
            : missingChecks.Count > 0 ? "REVIEW_REQUIRED" : "PASSED";
        checks.Add(Check(
            "required_route_qc",
            routeStatus,
            missingChecks.Count > 0 || blockedChecks.Count > 0
                ? $"缺少 [{string.Join(", ", missingChecks)}]；未通过 [{string.Join(", ", blockedChecks)}]"
                : "所需航线/QC 结果均通过"));

        bool blocked = checks.Any(x => x.Status == "BLOCKED");
        bool review = checks.Any(x => x.Status == "REVIEW_REQUIRED");
        string status = blocked ? "BLOCKED" : review ? "REVIEW_REQUIRED" : "PASSED";

        if (confirmGate && status != "PASSED")
            throw new InvalidOperationException($"只有 PASSED 每日 QC 可以确认 field 门禁，当前状态: {status}");
        if (confirmGate && string.IsNullOrWhiteSpace(confirmedBy))
            throw new InvalidOperationException("确认 field 门禁必须填写确认人");

        string qcId = $"daily-qc-{date}";
        string createdAt = DateTimeOffset.UtcNow.ToString("O");
        var record = new JsonObject
        {
            ["qc_id"] = qcId,
            ["date"] = date,
            ["status"] = status,
            ["checks"] = new JsonArray(checks.Select(ToJson).ToArray()),
            ["summary"] = new JsonObject
            {
                ["flight_count"] = flights.Count,
                ["base_station_count"] = stations.Count,
                ["blocked_checks"] = checks.Count(x => x.Status == "BLOCKED"),
                ["review_checks"] = checks.Count(x => x.Status == "REVIEW_REQUIRED")
            },
            ["refly_candidates"] = new JsonArray(flights
                .Where(x => x.Status is "BLOCKED" or "REVIEW_REQUIRED")
                .Select(x => (JsonNode?)JsonValue.Create(x.BatchId))
                .ToArray()),
            ["created_at"] = createdAt,
            ["confirmed_by"] = confirmedBy.Trim()
        };

        string path = ProjectPaths.Json(root, "qc_results.json");
        JsonArray rows = ReadArray(path);
        for (int i = rows.Count - 1; i >= 0; i--)
            if (rows[i]?["qc_id"]?.GetValue<string>() == qcId)
                rows.RemoveAt(i);
        rows.Add(record);
        _store.Write(path, rows);

        if (confirmGate)
            _workspace.SetGate(
                root,
                "field",
                "PASSED",
                $"每日 QC {date} 已通过",
                [qcId],
                confirmedBy.Trim());

        _store.AppendJsonLine(ProjectPaths.DataDirectory(root), "daily_qc_completed",
            new { qc_id = qcId, status, confirmed = confirmGate });

        return new DailyQcOutcome(
            qcId,
            date,
            status,
            checks,
            flights.Count,
            stations.Count,
            flights.Where(x => x.Status is "BLOCKED" or "REVIEW_REQUIRED").Select(x => x.BatchId).ToList(),
            confirmedBy.Trim(),
            createdAt);
    }

    private static QcCheck Check(string name, string status, string detail, IEnumerable<string>? evidence = null) =>
        new(name, status, detail, (evidence ?? []).ToList());

    private static JsonObject ToJson(QcCheck check) => new()
    {
        ["name"] = check.Name,
        ["status"] = check.Status,
        ["detail"] = check.Detail,
        ["evidence"] = new JsonArray(check.Evidence.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray())
    };

    private static bool ArtifactExists(string root, string reference, IReadOnlyList<ArtifactRecord> artifacts)
    {
        var row = artifacts.FirstOrDefault(x =>
            string.Equals(x.ArtifactId, reference, StringComparison.OrdinalIgnoreCase)
            || string.Equals(x.Path, reference.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));
        if (row is not null) return row.ChangeStatus != "MISSING";

        string candidate = Path.IsPathRooted(reference) ? reference : Path.Combine(root, reference);
        return File.Exists(candidate);
    }

    private static JsonArray ReadArray(string path)
    {
        if (!File.Exists(path)) return new JsonArray();
        return JsonNode.Parse(File.ReadAllText(path)) as JsonArray ?? new JsonArray();
    }
}
