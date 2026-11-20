using MagOps.Core.Domain;
using MagOps.Infrastructure.Services;
using MagOps.Infrastructure.Storage;

namespace MagOps.Reporting;

public sealed record ReportSharedItem(string Key, string Category, string Label, string Value,
    string Source, string Exclusive = "", string Parent = "", string Conflict = "")
{
    // The accepted wording and the last reviewed source value can differ.
    public bool RetainedAfterLoss { get; init; }
    public string? ReviewedValue { get; init; }
}

public static class ReportSharedSources
{
    public static List<ReportSharedItem> Read(string root, string area, string areaId, string? reportDate = null)
    {
        _ = ReportInputStore.DirectoryFor(root, area);
        var store = new JsonStore();
        List<T> Rows<T>(string file) => store.Read(ProjectPaths.Json(root, file), new List<T>());
        var areas = new WorkspaceService(store).GetWorkareas(root);
        static string Norm(string value) => value.Trim().Replace('\\', '/').TrimEnd('/');
        bool Matches(WorkareaRecord a, string value) => value.Length > 0 &&
            new[] { a.WorkareaId, a.SubareaId, a.Path, a.DisplayName }.Concat(a.PreviousDisplayNames)
                .Any(s => string.Equals(Norm(s), Norm(value), StringComparison.OrdinalIgnoreCase));
        bool Here(string value) => areas.Any(a => a.WorkareaId == areaId && Matches(a, value));
        bool Ambiguous(string value) => areas.Count(a => Matches(a, value)) > 1;
        static string Join(params string[] fields) => string.Join("，", fields.Where(s => !string.IsNullOrWhiteSpace(s)));
        static string Field(string name, string value) => string.IsNullOrWhiteSpace(value) ? "" : name + " " + value;
        var items = new List<ReportSharedItem>();
        var devices = Rows<EquipmentRecord>("equipment.json");
        var tests = Rows<EquipmentTestRecord>("equipment_tests.json");
        var stations = Rows<BaseStationLogRecord>("base_station_logs.json");
        var selectedDays = new HashSet<string>();
        foreach (var f in Rows<FlightBatchRecord>("flight_batches.json").Where(f => Here(f.Workarea) || Here(f.Subarea)))
        {
            string key = "flight/" + f.BatchId;
            string label = f.Date + " " + f.BatchId;
            selectedDays.Add(f.Date);
            bool conflict = new[] { f.Workarea, f.Subarea }.Any(v => v.Length > 0 && (!Here(v) || Ambiguous(v)));
            items.Add(new(key, "现场记录", label, Join(Field("操作员", f.Operator), Field("开始", f.Start),
                Field("结束", f.End), Field("航线版本", f.RouteVersion), Field("状态", f.Status),
                Field("飞行器", f.Aircraft), Field("磁力仪", f.Magnetometer), Field("基站", f.BaseStation),
                f.Result, f.Notes, string.Join("、", f.Issues)), "flight_batches.json#" + f.BatchId,
                Conflict: conflict ? "工区归属冲突，请确认" : "核对本批次属于报告范围"));
            foreach (var (role, reference) in new[] { ("飞行器", f.Aircraft), ("磁力仪", f.Magnetometer), ("基站设备", f.BaseStation) })
            {
                if (string.IsNullOrWhiteSpace(reference)) continue;
                var matched = devices.Where(d => new[] {d.RecordId, d.EquipmentId, d.Name, d.Model, d.SerialNo}
                    .Any(s => string.Equals(s.Trim(), reference.Trim(), StringComparison.OrdinalIgnoreCase))).ToList();
                // Unbound historical free text requires a deliberate choice from the register.
                foreach (var d in matched.Count == 0 ? devices : matched)
                {
                    string id = d.RecordId.Length > 0 ? d.RecordId : d.EquipmentId;
                    string group = key + "/" + role;
                    string deviceKey = group + "/" + id;
                    items.Add(new(deviceKey, "设备", label + " " + role, Join(d.Name, d.Model, Field("编号",d.EquipmentId),
                        Field("序列号",d.SerialNo), d.Category), "equipment.json#" + id, group, key,
                        matched.Count == 1 ? "" : "批次记载「" + reference + "」，请选择设备"));
                    var applicable = tests.Where(t => (t.EquipmentRef == id || (t.EquipmentRef.Length == 0 &&
                        t.EquipmentId == d.EquipmentId && devices.Count(x => x.EquipmentId == d.EquipmentId) == 1)) &&
                        DateTime.TryParse(t.TestedAt, out var tested) && DateTime.TryParse(f.Date, out var date) &&
                        tested.Date <= date.Date).ToList();
                    foreach (var t in applicable.Where(t => !applicable.Any(n => n.SupersedesTestId == t.TestId)))
                        items.Add(new(deviceKey + "/" + t.TestId, "试验条件", label + " " + d.Name,
                            Join(t.TestType,t.TestedAt,t.Status,t.Note,string.Join("、",t.EvidenceArtifacts)),
                            "equipment_tests.json#" + t.TestId, Parent:deviceKey, Conflict:"确认试验适用时段"));
                }
            }
            var dayStations = stations.Where(s => s.Date == f.Date).ToList();
            bool CoversFlight(BaseStationLogRecord station) =>
                string.Equals(station.StationId, f.BaseStation, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(f.BaseStation) &&
                // Batch times carry no time basis. Confirm temporal coverage in the review.
                dayStations.Count(s => s.StationId == station.StationId) == 1;
            foreach (var b in dayStations.OrderByDescending(CoversFlight))
                items.Add(new(key + "/station/" + b.RecordId, "基站", label + " 基站", Join(b.StationId,b.Site,
                    Field("经度",b.Longitude),Field("纬度",b.Latitude),Field("高程(m)",b.ElevationM),b.Crs,b.TimeBasis,
                    Field("采样间隔(s)",b.SamplingIntervalS),Field("开始",b.Start),Field("结束",b.End)),
                    "base_station_logs.json#" + b.RecordId, Parent:key, Conflict:CoversFlight(b) ? "站号唯一匹配，请核对使用时段" : "核对站号及实际使用时段"));
        }
        foreach (var a in Rows<ArtifactRecord>("artifacts.json").Where(a => a.WorkareaRef == areaId))
        {
            string full = Path.GetFullPath(Path.Combine(root, a.Path));
            if (!Path.GetRelativePath(root, full).StartsWith(".." + Path.DirectorySeparatorChar) &&
                !Path.IsPathRooted(Path.GetRelativePath(root, full)) && File.Exists(full))
            {
                if (Path.GetExtension(full).Equals(".png", StringComparison.OrdinalIgnoreCase))
                    items.Add(new("figure/" + a.ArtifactId, "成果图", a.Name, full,
                        "artifacts.json#" + a.ArtifactId, Conflict:"选择报告采用的成果图"));
                else if (Path.GetExtension(full).Equals(".json", StringComparison.OrdinalIgnoreCase) &&
                    (a.ProcessingStage == "route_project" || a.Role == "route_project" || a.Name.EndsWith("外业测线任务基准.json")))
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(full));
                    var data = doc.RootElement;
                    if (data.TryGetProperty("schema_version", out var ver) &&
                        ver.ToString().StartsWith("magops-route-project-", StringComparison.Ordinal))
                    {
                        var fields = new List<string>();
                        if (data.TryGetProperty("DesignBasis", out var basis) && basis.ValueKind == System.Text.Json.JsonValueKind.Object &&
                            basis.TryGetProperty("GridAzimuthDegrees", out var angle)) fields.Add("设计网格方位角 " + angle + "°");
                        if (data.TryGetProperty("partition_code", out var part) && part.ToString().Length > 0) fields.Add("分区 " + part);
                        if (fields.Count > 0) items.Add(new("design/" + a.ArtifactId, "设计参数", a.Name, string.Join("，", fields),
                            a.Path + "#" + a.ArtifactId, Conflict:"核对采用的设计版本"));
                    }
                }
            }
        }
        var progress = Rows<FieldDailyProgressRecord>("field_daily_progress.json").Where(p => Here(p.Workarea));
        foreach (var p in progress)
        {
            selectedDays.Add(p.Date);
            items.Add(new("progress/" + p.ProgressId,"项目概况",p.Date + " 工区进度",
                $"航线 {p.RouteSource}（{p.RouteFingerprint}），计划 {p.PlannedSortieCount} 架次，当日完成 {p.TodayCompletedSorties.Count}，当日复飞 {p.TodayRepeatedSorties.Count}，截至当日累计完成 {p.CompletedSorties.Count}",
                "field_daily_progress.json#" + p.ProgressId, Conflict:"核对工区、设计版本及统计日期"));
        }
        foreach (var l in Rows<DailyLogRecord>("daily_logs.json"))
            items.Add(new("log/" + l.LogId,"现场记录",l.Date + " 项目日志",Join(l.Site,l.Weather,
                Field("完成",string.Join("、",l.CompletedTasks)),Field("异常",string.Join("、",l.Anomalies)),l.Notes),
                "daily_logs.json#" + l.LogId, Conflict:selectedDays.Contains(l.Date) ? "同日项目记录，请确认工区" : "项目记录，请确认日期和工区"));
        items.AddRange(ProjectReportProfile.Load(root).Profile.Candidates(reportDate));
        return items;
    }

    public static ReportInputs Prefill(string root, string area, string areaId, ReportInputs inputs)
    {
        if (inputs.SharedSelectionReviewed || inputs.SharedItems.Length > 0 || !File.Exists(inputs.ProductionCsv)) return inputs;
        var identity = GeneratedArtifactIdentity.Inspect(inputs.ProductionCsv);
        if (identity.Status != "VERIFIED" || identity.IsProductionStale || identity.Metadata is not { } metadata ||
            metadata.ProjectId != new WorkspaceService(new JsonStore()).GetWorkspaceReadOnly(root).ProjectId ||
            metadata.WorkareaId != areaId) return inputs;
        var sourceFiles = metadata.SourceFiles.Where(Path.IsPathFullyQualified)
            .Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (sourceFiles.Count == 0) return inputs;
        var store = new JsonStore();
        var flights = store.Read(ProjectPaths.Json(root, "flight_batches.json"), new List<FlightBatchRecord>());
        var counts = flights.SelectMany(f => f.RawArtifacts.Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => Path.GetFullPath(p, root)).Distinct(StringComparer.OrdinalIgnoreCase))
            .ToLookup(p => p, StringComparer.OrdinalIgnoreCase);
        var matching = flights.Where(f => f.Status == "COMPLETED" && f.RawArtifacts.Count > 0 &&
            f.RawArtifacts.All(p => !string.IsNullOrWhiteSpace(p) && sourceFiles.Contains(Path.GetFullPath(p, root)) && counts[Path.GetFullPath(p, root)].Count() == 1))
            .Select(f => "flight/" + f.BatchId).ToHashSet();
        if (matching.Count == 0) return inputs;
        var rows = Read(root, area, areaId, inputs.ReportDate);
        var unique = rows.GroupBy(r => r.Key).Where(g => g.Count() == 1).Select(g => g.Single()).ToArray();
        var accepted = unique.Where(i => matching.Contains(i.Key) && i.Conflict == "核对本批次属于报告范围").ToList();
        var parents = accepted.Select(i => i.Key).ToHashSet();
        accepted.AddRange(unique.Where(i => parents.Contains(i.Parent) && i.Conflict.Length == 0));
        return accepted.Count == 0 ? inputs : inputs with { SharedItems = accepted.ToArray() };
    }

    public static List<string> Changes(string root, string area, string areaId, ReportSharedItem[] saved, string? reportDate = null)
    {
        if (saved.Length == 0) return [];
        var live = Read(root, area, areaId, reportDate).ToLookup(x => x.Key);
        var changes = new List<string>();
        foreach (var item in saved)
        {
            var rows = live[item.Key].ToArray();
            if (rows.Length == 0 && item.RetainedAfterLoss) continue;
            if (item.RetainedAfterLoss || rows.Length != 1 || rows[0].Value != (item.ReviewedValue ?? item.Value) || rows[0].Label != item.Label)
                changes.Add(item.Label + "（" + item.Source + "）");
        }
        return changes;
    }
}
