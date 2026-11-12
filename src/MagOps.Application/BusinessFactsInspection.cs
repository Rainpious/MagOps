using MagOps.Core.Domain;
using MagOps.Infrastructure.Services;
using MagOps.Infrastructure.Storage;

namespace MagOps.Application;

public sealed record BusinessFactIssue(string Code, string RecordId, string Message);
public sealed record BusinessFactsResult(string ProjectId, IReadOnlyDictionary<string, string> Owners,
    IReadOnlyList<string> RecordedOperators, int EquipmentCount, int FlightCount, int DailyLogCount,
    IReadOnlyList<BusinessFactIssue> Issues)
{
    public bool ReadOnly => true;
}

/// <summary>Audits original facts without merging conflicting history or inventing a personnel registry.</summary>
public sealed class BusinessFactsInspection
{
    public ApplicationResult<BusinessFactsResult> Inspect(string root)
    {
        string op = "op-" + Guid.NewGuid().ToString("N");
        try
        {
            var store = new JsonStore();
            var ws = new WorkspaceService(store).GetWorkspaceReadOnly(root);
            var equipment = store.Read(ProjectPaths.Json(root, "equipment.json"), new List<EquipmentRecord>());
            var flights = store.Read(ProjectPaths.Json(root, "flight_batches.json"), new List<FlightBatchRecord>());
            var logs = store.Read(ProjectPaths.Json(root, "daily_logs.json"), new List<DailyLogRecord>());
            var issues = new List<BusinessFactIssue>();
            foreach (var g in equipment.GroupBy(e => e.EquipmentId, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
                issues.Add(new("DUPLICATE_EQUIPMENT_ID", g.Key, "设备身份重复；保留原记录，需人工区分。"));
            foreach (var g in logs.GroupBy(l => l.Date, StringComparer.Ordinal).Where(g => g.Count() > 1))
                issues.Add(new("MULTIPLE_DAILY_FACTS", g.Key, "同日存在多条项目日志，不能自动以最后一条覆盖。"));
            var ids = equipment.Select(e => e.EquipmentId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var f in flights)
            {
                if (!string.IsNullOrWhiteSpace(f.Workarea) && !string.IsNullOrWhiteSpace(f.Subarea) && f.Workarea != f.Subarea)
                    issues.Add(new("WORKAREA_ALIAS_CONFLICT", f.BatchId, "工区与历史子区别名不一致，未自动归并。"));
                foreach (string reference in new[] { f.Aircraft, f.Magnetometer }.Where(s => !string.IsNullOrWhiteSpace(s)))
                    if (!ids.Contains(reference)) issues.Add(new("UNBOUND_EQUIPMENT_REFERENCE", f.BatchId, "飞行记录设备描述未绑定设备编号：" + reference));
            }
            return ApplicationResult<BusinessFactsResult>.Success(op, null, new(ws.ProjectId,
                new Dictionary<string, string>
                {
                    ["设备身份"] = "equipment.json / EquipmentId；清点和测试为关联记录，不复制主数据",
                    ["人员作业记录"] = "flight_batches.json / Operator；当前无独立人员主表，不按同名自动合并人员",
                    ["现场叙述"] = "daily_logs.json / Date；项目级人工日志",
                    ["定量进度"] = "field_daily_progress.json；工区、设计来源与作业日范围内的计算记录",
                    ["目标日产能"] = "field_operations_settings.json / ExpectedDailySorties；不以人工日志替换计算进度"
                }, flights.Select(f => f.Operator).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                equipment.Count, flights.Count, logs.Count, issues));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { return ApplicationResult<BusinessFactsResult>.Failure(op, new(ex is IOException or UnauthorizedAccessException ? "ENVIRONMENT_ERROR" : "VALIDATION_ERROR", ex.Message)); }
    }
}
