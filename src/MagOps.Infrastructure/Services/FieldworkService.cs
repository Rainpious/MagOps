using System.Text;
using MagOps.Core;
using MagOps.Core.Domain;
using MagOps.Infrastructure.Storage;

namespace MagOps.Infrastructure.Services;

public sealed record ChecklistView(
    bool Available,
    EquipmentRecord Equipment,
    EquipmentChecklistTemplate? Template,
    IReadOnlyList<EquipmentChecklistItemView> Items,
    string Status,
    int RequiredTotal,
    int RequiredChecked,
    int OptionalTotal,
    int OptionalChecked,
    EquipmentChecklistState? Saved);

public sealed record EquipmentChecklistItemView(
    EquipmentChecklistItem Item,
    bool Checked,
    int? Quantity);

public sealed class FieldworkService
{
    private readonly JsonStore _store;
    private readonly WorkspaceService _workspace;

    public FieldworkService(JsonStore store, WorkspaceService workspace)
    {
        _store = store;
        _workspace = workspace;
    }

    public List<EquipmentRecord> ListEquipment(string root)
    {
        _workspace.GetWorkspace(root);
        var equipment = _store.Read(ProjectPaths.Json(root, "equipment.json"), new List<EquipmentRecord>());
        var tests = _store.Read(ProjectPaths.Json(root, "equipment_tests.json"), new List<EquipmentTestRecord>());
        var checklists = _store.Read(ProjectPaths.Json(root, "equipment_checklists.json"), new List<EquipmentChecklistState>());

        foreach (var row in equipment)
        {
            row.Category = EquipmentCatalogService.NormalizeCategory(row.Category);
            string reference = EquipmentRef(row);
            var template = ResolveTemplate(row);
            var saved = checklists.FirstOrDefault(x => x.EquipmentRef == reference);
            row.ChecklistTemplateId = template?.TemplateId ?? row.ChecklistTemplateId;
            row.ChecklistStatus = template is not null
                ? saved?.Status ?? "PENDING"
                : saved is { CustomItems.Count: > 0 } ? saved.Status : "NOT_REQUIRED";
            row.TestStatus = LatestTestStatus(tests, row);
            row.ReleaseStatus = CombinedReleaseStatus(row.TestStatus, row.ChecklistStatus);
        }
        return equipment;
    }

    public EquipmentRecord AddEquipment(
        string root,
        string model,
        string equipmentId = "",
        string equipmentType = "",
        string serialNo = "",
        string notes = "",
        string category = "")
    {
        using var writeLock = WorkspaceFileLock.AcquireForRoot(root);
        model = (model ?? "").Trim();
        category = EquipmentCatalogService.NormalizeCategory(category);
        if (model.Length == 0) throw new ArgumentException("设备型号不能为空");

        var rows = _store.Read(ProjectPaths.Json(root, "equipment.json"), new List<EquipmentRecord>());
        if (equipmentId.Trim().Length > 0 && rows.Any(x => x.EquipmentId == equipmentId.Trim()))
            throw new InvalidOperationException($"设备编号已存在: {equipmentId}");

        var template = EquipmentTemplates.Find(model);
        string now = WorkspaceService.Now();
        var record = new EquipmentRecord
        {
            RecordId = NextId(rows.Select(x => x.RecordId), "EQUIPMENT"),
            EquipmentId = equipmentId.Trim(),
            Name = model,
            Model = model,
            Category = string.IsNullOrWhiteSpace(category)
                ? (string.IsNullOrWhiteSpace(equipmentType) ? model : equipmentType.Trim())
                : category.Trim(),
            EquipmentType = string.IsNullOrWhiteSpace(equipmentType) ? model : equipmentType.Trim(),
            SerialNo = serialNo.Trim(),
            Notes = notes,
            ChecklistTemplateId = template?.TemplateId ?? "",
            ChecklistStatus = template is null ? "NOT_REQUIRED" : "PENDING",
            TestStatus = "PENDING",
            ReleaseStatus = "PENDING",
            CreatedAt = now,
            UpdatedAt = now
        };
        rows.Add(record);
        _store.Write(ProjectPaths.Json(root, "equipment.json"), rows);
        _store.AppendJsonLine(ProjectPaths.DataDirectory(root), "equipment_added",
            new { record_id = record.RecordId, model = record.Model, equipment_id = record.EquipmentId, checklist_template_id = record.ChecklistTemplateId });
        return record;
    }

    public void DeleteEquipment(string root, string equipmentId)
    {
        using var writeLock = WorkspaceFileLock.AcquireForRoot(root);
        var rows = _store.Read(ProjectPaths.Json(root, "equipment.json"), new List<EquipmentRecord>());
        var target = rows.FirstOrDefault(x => x.RecordId == equipmentId || x.EquipmentId == equipmentId)
            ?? throw new KeyNotFoundException($"未找到设备: {equipmentId}");
        string reference = EquipmentRef(target);

        rows.Remove(target);
        _store.Write(ProjectPaths.Json(root, "equipment.json"), rows);

        var checklists = _store.Read(ProjectPaths.Json(root, "equipment_checklists.json"), new List<EquipmentChecklistState>());
        int removedChecklists = checklists.RemoveAll(x => x.EquipmentRef == reference);
        _store.Write(ProjectPaths.Json(root, "equipment_checklists.json"), checklists);

        var tests = _store.Read(ProjectPaths.Json(root, "equipment_tests.json"), new List<EquipmentTestRecord>());
        int removedTests = tests.RemoveAll(x => x.EquipmentRef == reference);
        _store.Write(ProjectPaths.Json(root, "equipment_tests.json"), tests);

        _store.AppendJsonLine(ProjectPaths.DataDirectory(root), "equipment_deleted",
            new { record_id = target.RecordId, equipment_id = target.EquipmentId, model = target.Model, removed_checklists = removedChecklists, removed_tests = removedTests });
    }

    public ChecklistView GetChecklist(string root, string equipmentId)
    {
        var target = ListEquipment(root)
            .FirstOrDefault(x => x.RecordId == equipmentId || x.EquipmentId == equipmentId)
            ?? throw new KeyNotFoundException($"未找到设备: {equipmentId}");

        string reference = EquipmentRef(target);
        var savedRows = _store.Read(ProjectPaths.Json(root, "equipment_checklists.json"), new List<EquipmentChecklistState>());
        var saved = savedRows.FirstOrDefault(x => x.EquipmentRef == reference);
        var template = ResolveTemplate(target);
        var custom = saved?.CustomItems ?? [];

        if (template is null && custom.Count == 0)
            return new ChecklistView(false, target, null, [], "NOT_REQUIRED", 0, 0, 0, 0, saved);

        var sourceItems = new List<EquipmentChecklistItem>();
        if (template is not null) sourceItems.AddRange(template.Items);
        sourceItems.AddRange(custom);

        var checkedSet = (saved?.CheckedItemIds ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var quantities = saved?.QuantityValues ?? new Dictionary<string, int>();

        int requiredTotal = 0, requiredChecked = 0, optionalTotal = 0, optionalChecked = 0;
        var views = new List<EquipmentChecklistItemView>();

        foreach (var item in sourceItems)
        {
            bool isChecked = checkedSet.Contains(item.ItemId);
            int? quantity = item.QuantityEditable
                ? quantities.GetValueOrDefault(item.ItemId, item.DefaultQuantity)
                : null;
            bool satisfied = isChecked && (!item.QuantityEditable || (quantity ?? 0) >= Math.Max(item.MinQuantity, 1));

            if (item.Requirement == EquipmentTemplates.Required)
            {
                requiredTotal++;
                if (satisfied) requiredChecked++;
            }
            else
            {
                optionalTotal++;
                if (satisfied) optionalChecked++;
            }
            views.Add(new EquipmentChecklistItemView(item, isChecked, quantity));
        }

        string status = requiredTotal == 0 ? "NOT_REQUIRED" :
            requiredChecked == requiredTotal ? "PASSED" : "PENDING";

        return new ChecklistView(true, target, template, views, status,
            requiredTotal, requiredChecked, optionalTotal, optionalChecked, saved);
    }

    public ChecklistView SaveChecklist(
        string root,
        string equipmentId,
        IEnumerable<string> checkedItemIds,
        IReadOnlyDictionary<string, int>? quantities = null)
    {
        using var writeLock = WorkspaceFileLock.AcquireForRoot(root);
        var view = GetChecklist(root, equipmentId);
        if (!view.Available)
            throw new InvalidOperationException($"设备 {view.Equipment.Model} 尚无部件清点模板或自定义清点项");

        string reference = EquipmentRef(view.Equipment);
        var validIds = view.Items.Select(x => x.Item.ItemId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var checkedIds = checkedItemIds.Select(x => x.Trim()).Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var unknown = checkedIds.Where(x => !validIds.Contains(x)).ToArray();
        if (unknown.Length > 0)
            throw new ArgumentException("清点项不存在: " + string.Join(", ", unknown));

        var rows = _store.Read(ProjectPaths.Json(root, "equipment_checklists.json"), new List<EquipmentChecklistState>());
        var state = rows.FirstOrDefault(x => x.EquipmentRef == reference);
        if (state is null)
        {
            state = new EquipmentChecklistState
            {
                ChecklistId = NextId(rows.Select(x => x.ChecklistId), "CHECKLIST"),
                EquipmentRef = reference,
                EquipmentId = view.Equipment.EquipmentId,
                EquipmentName = view.Equipment.Name,
                EquipmentModel = view.Equipment.Model,
                CreatedAt = WorkspaceService.Now()
            };
            rows.Add(state);
        }

        state.EquipmentId = view.Equipment.EquipmentId;
        state.EquipmentName = view.Equipment.Name;
        state.EquipmentModel = view.Equipment.Model;
        state.TemplateId = view.Template?.TemplateId ?? state.TemplateId;
        state.CheckedItemIds = checkedIds;
        foreach (var itemView in view.Items.Where(x => x.Item.QuantityEditable))
        {
            int value = quantities?.GetValueOrDefault(itemView.Item.ItemId)
                ?? state.QuantityValues.GetValueOrDefault(itemView.Item.ItemId, itemView.Item.DefaultQuantity);
            state.QuantityValues[itemView.Item.ItemId] = Math.Max(0, value);
        }

        int requiredTotal = 0, requiredChecked = 0;
        var checkedSet = checkedIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var itemView in view.Items.Where(x => x.Item.Requirement == EquipmentTemplates.Required))
        {
            requiredTotal++;
            var item = itemView.Item;
            bool satisfied = checkedSet.Contains(item.ItemId)
                && (!item.QuantityEditable || state.QuantityValues.GetValueOrDefault(item.ItemId, item.DefaultQuantity) >= Math.Max(item.MinQuantity, 1));
            if (satisfied) requiredChecked++;
        }
        state.RequiredTotal = requiredTotal;
        state.RequiredChecked = requiredChecked;
        state.OptionalTotal = view.Items.Count(x => x.Item.Requirement == EquipmentTemplates.Optional);
        state.OptionalChecked = view.Items.Count(x =>
            x.Item.Requirement == EquipmentTemplates.Optional
            && checkedSet.Contains(x.Item.ItemId)
            && (!x.Item.QuantityEditable
                || state.QuantityValues.GetValueOrDefault(x.Item.ItemId, x.Item.DefaultQuantity) >= Math.Max(x.Item.MinQuantity, 1)));
        state.Status = requiredTotal == 0 ? "NOT_REQUIRED" : requiredChecked == requiredTotal ? "PASSED" : "PENDING";
        state.UpdatedAt = WorkspaceService.Now();
        _store.Write(ProjectPaths.Json(root, "equipment_checklists.json"), rows);

        var equipmentRows = _store.Read(ProjectPaths.Json(root, "equipment.json"), new List<EquipmentRecord>());
        var target = equipmentRows.First(x => x.RecordId == reference || x.EquipmentId == reference);
        target.ChecklistTemplateId = state.TemplateId;
        target.ChecklistStatus = state.Status;
        var tests = _store.Read(ProjectPaths.Json(root, "equipment_tests.json"), new List<EquipmentTestRecord>());
        target.TestStatus = LatestTestStatus(tests, target);
        target.ReleaseStatus = CombinedReleaseStatus(target.TestStatus, target.ChecklistStatus);
        target.UpdatedAt = WorkspaceService.Now();
        _store.Write(ProjectPaths.Json(root, "equipment.json"), equipmentRows);

        _store.AppendJsonLine(ProjectPaths.DataDirectory(root), "equipment_checklist_saved",
            new { equipment_ref = reference, template_id = state.TemplateId, status = state.Status, required_checked = requiredChecked, required_total = requiredTotal, quantity_values = state.QuantityValues });

        return GetChecklist(root, reference);
    }

    public ChecklistView AddCustomChecklistItem(
        string root,
        string equipmentId,
        string name,
        string category = "工具",
        string requirement = EquipmentTemplates.Optional,
        int defaultQuantity = 1,
        string section = "自定义",
        string check = "按现场任务需要确认数量、状态和可用性。")
    {
        using var writeLock = WorkspaceFileLock.AcquireForRoot(root);
        name = (name ?? "").Trim();
        if (name.Length == 0) throw new ArgumentException("自定义工具名称不能为空");
        if (requirement is not EquipmentTemplates.Required and not EquipmentTemplates.Optional)
            throw new ArgumentException("自定义工具必须选择必备或可选");

        var target = ListEquipment(root)
            .FirstOrDefault(x => x.RecordId == equipmentId || x.EquipmentId == equipmentId)
            ?? throw new KeyNotFoundException($"未找到设备: {equipmentId}");
        string reference = EquipmentRef(target);

        var rows = _store.Read(ProjectPaths.Json(root, "equipment_checklists.json"), new List<EquipmentChecklistState>());
        var state = rows.FirstOrDefault(x => x.EquipmentRef == reference);
        if (state is null)
        {
            state = new EquipmentChecklistState
            {
                ChecklistId = NextId(rows.Select(x => x.ChecklistId), "CHECKLIST"),
                EquipmentRef = reference,
                EquipmentId = target.EquipmentId,
                EquipmentName = target.Name,
                EquipmentModel = target.Model,
                CreatedAt = WorkspaceService.Now()
            };
            rows.Add(state);
        }

        string stem = $"custom-{DateTime.Now:yyyyMMddHHmmss}";
        string id = stem;
        int suffix = 2;
        var ids = state.CustomItems.Select(x => x.ItemId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        while (ids.Contains(id)) id = $"{stem}-{suffix++}";

        state.CustomItems.Add(new EquipmentChecklistItem
        {
            ItemId = id,
            Section = string.IsNullOrWhiteSpace(section) ? "自定义" : section.Trim(),
            Category = string.IsNullOrWhiteSpace(category) ? "工具" : category.Trim(),
            Name = name,
            Expected = "项目自定义",
            Requirement = requirement,
            Check = string.IsNullOrWhiteSpace(check)
                ? "按现场任务需要确认数量、状态和可用性。"
                : check.Trim(),
            QuantityEditable = true,
            DefaultQuantity = Math.Max(0, defaultQuantity),
            MinQuantity = 1,
            QuantityUnit = "件",
            IsCustom = true
        });
        state.QuantityValues[id] = Math.Max(0, defaultQuantity);
        state.UpdatedAt = WorkspaceService.Now();
        _store.Write(ProjectPaths.Json(root, "equipment_checklists.json"), rows);
        _store.AppendJsonLine(ProjectPaths.DataDirectory(root), "equipment_custom_item_added",
            new { equipment_ref = reference, item_id = id, name, requirement });

        return SaveChecklist(root, reference, state.CheckedItemIds, state.QuantityValues);
    }

    public EquipmentTestRecord RecordEquipmentTest(
        string root,
        string equipmentId,
        string testType,
        string status,
        string note = "",
        IEnumerable<string>? evidence = null)
    {
        using var writeLock = WorkspaceFileLock.AcquireForRoot(root);
        if (!AppConstants.EquipmentTestStatuses.Contains(status))
            throw new ArgumentException($"无效设备测试状态: {status}");
        if (string.IsNullOrWhiteSpace(testType))
            throw new ArgumentException("设备测试类型不能为空");

        var equipmentRows = ListEquipment(root);
        var target = equipmentRows.FirstOrDefault(x => x.RecordId == equipmentId || x.EquipmentId == equipmentId)
            ?? throw new KeyNotFoundException($"未找到设备: {equipmentId}");
        string reference = EquipmentRef(target);

        var tests = _store.Read(ProjectPaths.Json(root, "equipment_tests.json"), new List<EquipmentTestRecord>());
        var previous = tests.LastOrDefault(x => x.EquipmentRef == reference && x.TestType == testType);
        string now = WorkspaceService.Now();
        var record = new EquipmentTestRecord
        {
            TestId = NextId(tests.Select(x => x.TestId), "EQUIPTEST"),
            EquipmentRef = reference,
            EquipmentId = target.EquipmentId,
            EquipmentName = target.Name,
            TestType = testType.Trim(),
            Status = status,
            Note = note,
            EvidenceArtifacts = (evidence ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).ToList(),
            SupersedesTestId = previous?.TestId ?? "",
            TestedAt = now
        };
        tests.Add(record);
        _store.Write(ProjectPaths.Json(root, "equipment_tests.json"), tests);

        var persistedEquipment = _store.Read(ProjectPaths.Json(root, "equipment.json"), new List<EquipmentRecord>());
        var persisted = persistedEquipment.First(x => x.RecordId == reference || x.EquipmentId == reference);
        persisted.TestStatus = LatestTestStatus(tests, persisted);
        persisted.ChecklistStatus = GetChecklist(root, reference).Status;
        persisted.ReleaseStatus = CombinedReleaseStatus(persisted.TestStatus, persisted.ChecklistStatus);
        foreach (string artifact in record.EvidenceArtifacts)
            if (!persisted.EvidenceArtifacts.Contains(artifact)) persisted.EvidenceArtifacts.Add(artifact);
        persisted.UpdatedAt = now;
        _store.Write(ProjectPaths.Json(root, "equipment.json"), persistedEquipment);

        _store.AppendJsonLine(ProjectPaths.DataDirectory(root), "equipment_test_recorded",
            new { equipment_ref = reference, test_id = record.TestId, status, supersedes_test_id = record.SupersedesTestId, release_recommendation = persisted.ReleaseStatus });
        return record;
    }

    public List<EquipmentTestRecord> ListEquipmentTests(string root, string? equipmentRef = null)
    {
        var rows = _store.Read(ProjectPaths.Json(root, "equipment_tests.json"), new List<EquipmentTestRecord>());
        return string.IsNullOrWhiteSpace(equipmentRef) ? rows : rows.Where(x => x.EquipmentRef == equipmentRef).ToList();
    }

    public string ExportEquipmentChecklistCsv(string root, string equipmentId, string destination)
    {
        var view = GetChecklist(root, equipmentId);
        string reference = EquipmentRef(view.Equipment);
        var tests = ListEquipmentTests(root, reference);
        var latest = tests
            .GroupBy(x => x.TestType, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                x => x.Key,
                x => x.Last(),
                StringComparer.OrdinalIgnoreCase);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        using var writer = new StreamWriter(destination, false, new UTF8Encoding(true));
        writer.WriteLine("设备型号,设备编号,步骤,分区,类型,名称,必备/可选,数量,核验/测试状态,清点内容/测试说明,记录时间");

        foreach (var item in view.Items)
        {
            string quantity = item.Quantity.HasValue ? $"{item.Quantity}{item.Item.QuantityUnit}" : item.Item.Expected;
            WriteCsv(writer,
                view.Equipment.Model, view.Equipment.EquipmentId, "设备清点", item.Item.Section, item.Item.Category,
                item.Item.Name, item.Item.Requirement == EquipmentTemplates.Required ? "必备" : "可选",
                quantity, item.Checked ? "已核验" : "未核验", item.Item.Check, view.Saved?.UpdatedAt ?? "");
        }

        foreach (EquipmentTestDefinition definition in EquipmentTestProfiles.For(view.Equipment))
        {
            latest.TryGetValue(definition.TestType, out var row);
            WriteCsv(writer,
                view.Equipment.Model, view.Equipment.EquipmentId, "设备测试", "", "检测", definition.TestType,
                "必备", "", row?.Status ?? "未记录",
                string.IsNullOrWhiteSpace(row?.Note) ? definition.Check : row.Note,
                row?.TestedAt ?? "");
        }

        _store.AppendJsonLine(ProjectPaths.DataDirectory(root), "equipment_checklist_exported",
            new { equipment_ref = reference, path = destination });
        return destination;
    }

    public List<FlightBatchRecord> ListFlights(string root) =>
        _store.Read(ProjectPaths.Json(root, "flight_batches.json"), new List<FlightBatchRecord>());

    public FlightBatchRecord AddFlight(
        string root, string date, string workarea = "", string aircraft = "", string magnetometer = "",
        string baseStation = "", string routeVersion = "", string operatorName = "", string start = "", string end = "",
        IEnumerable<string>? rawArtifacts = null, string result = "", IEnumerable<string>? issues = null,
        string status = "PLANNED", string notes = "")
    {
        using var writeLock = WorkspaceFileLock.AcquireForRoot(root);
        if (!AppConstants.FlightStatuses.Contains(status)) throw new ArgumentException($"无效飞行批次状态: {status}");
        if (string.IsNullOrWhiteSpace(date)) throw new ArgumentException("飞行日期不能为空");
        var rows = ListFlights(root);
        string now = WorkspaceService.Now();
        var row = new FlightBatchRecord
        {
            BatchId = NextId(rows.Select(x => x.BatchId), "FLIGHT"),
            Date = date.Trim(),
            Workarea = workarea,
            Subarea = workarea,
            Aircraft = aircraft,
            Magnetometer = magnetometer,
            BaseStation = baseStation,
            RouteVersion = routeVersion,
            Operator = operatorName,
            Start = start,
            End = end,
            RawArtifacts = (rawArtifacts ?? []).ToList(),
            Result = result,
            Issues = (issues ?? []).ToList(),
            Status = status,
            Notes = notes,
            CreatedAt = now,
            UpdatedAt = now
        };
        string warning = TaskLockWarning(root);
        row.TaskLockWarning = warning;
        rows.Add(row);
        _store.Write(ProjectPaths.Json(root, "flight_batches.json"), rows);
        _store.AppendJsonLine(ProjectPaths.DataDirectory(root), "flight_batch_added",
            new { batch_id = row.BatchId, status, task_lock_warning = warning });
        return row;
    }

    public FlightBatchRecord UpdateFlight(
        string root, string batchId, string? status = null, string? start = null, string? end = null,
        string? routeVersion = null, IEnumerable<string>? rawArtifacts = null, string? result = null,
        IEnumerable<string>? issues = null, string? notes = null)
    {
        using var writeLock = WorkspaceFileLock.AcquireForRoot(root);
        var rows = ListFlights(root);
        var row = rows.FirstOrDefault(x => x.BatchId == batchId)
            ?? throw new KeyNotFoundException($"未找到飞行批次: {batchId}");
        if (status is not null)
        {
            if (!AppConstants.FlightStatuses.Contains(status)) throw new ArgumentException($"无效飞行批次状态: {status}");
            row.Status = status;
        }
        if (start is not null) row.Start = start;
        if (end is not null) row.End = end;
        if (routeVersion is not null) row.RouteVersion = routeVersion;
        if (rawArtifacts is not null) row.RawArtifacts = rawArtifacts.ToList();
        if (result is not null) row.Result = result;
        if (issues is not null) row.Issues = issues.ToList();
        if (notes is not null) row.Notes = notes;
        row.UpdatedAt = WorkspaceService.Now();
        _store.Write(ProjectPaths.Json(root, "flight_batches.json"), rows);
        _store.AppendJsonLine(ProjectPaths.DataDirectory(root), "flight_batch_updated",
            new { batch_id = batchId, status = row.Status });
        return row;
    }

    public List<BaseStationLogRecord> ListBaseStations(string root) =>
        _store.Read(ProjectPaths.Json(root, "base_station_logs.json"), new List<BaseStationLogRecord>());

    public BaseStationLogRecord AddBaseStation(
        string root, string date, string stationId = "", string site = "", string longitude = "",
        string latitude = "", string elevation = "", string crs = "", string timeBasis = "UTC",
        string samplingIntervalS = "", string start = "", string end = "",
        IEnumerable<string>? artifacts = null, string status = "RECORDED", string notes = "")
    {
        using var writeLock = WorkspaceFileLock.AcquireForRoot(root);
        if (string.IsNullOrWhiteSpace(date)) throw new ArgumentException("日变站记录日期不能为空");
        var rows = ListBaseStations(root);
        string now = WorkspaceService.Now();
        var row = new BaseStationLogRecord
        {
            RecordId = NextId(rows.Select(x => x.RecordId), "BASE"),
            Date = date.Trim(),
            StationId = stationId,
            Site = site,
            Longitude = longitude,
            Latitude = latitude,
            ElevationM = elevation,
            Crs = crs,
            TimeBasis = timeBasis,
            SamplingIntervalS = samplingIntervalS,
            Start = start,
            End = end,
            Artifacts = (artifacts ?? []).ToList(),
            Status = status,
            Notes = notes,
            CreatedAt = now,
            UpdatedAt = now
        };
        string warning = TaskLockWarning(root);
        row.TaskLockWarning = warning;
        rows.Add(row);
        _store.Write(ProjectPaths.Json(root, "base_station_logs.json"), rows);
        _store.AppendJsonLine(ProjectPaths.DataDirectory(root), "base_station_log_added",
            new { record_id = row.RecordId, date = row.Date, task_lock_warning = warning });
        return row;
    }

    public BaseStationLogRecord UpdateBaseStation(
        string root,
        string recordId,
        string date,
        string stationId = "",
        string site = "",
        string longitude = "",
        string latitude = "",
        string elevation = "",
        string crs = "",
        string timeBasis = "UTC",
        string samplingIntervalS = "",
        string start = "",
        string end = "",
        IEnumerable<string>? artifacts = null,
        string status = "RECORDED",
        string notes = "")
    {
        using var writeLock = WorkspaceFileLock.AcquireForRoot(root);
        if (string.IsNullOrWhiteSpace(recordId))
            throw new ArgumentException("日变站记录编号不能为空");
        if (string.IsNullOrWhiteSpace(date))
            throw new ArgumentException("日变站记录日期不能为空");

        List<BaseStationLogRecord> rows =
            ListBaseStations(root);
        BaseStationLogRecord row =
            rows.FirstOrDefault(x =>
                x.RecordId.Equals(
                    recordId,
                    StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"未找到日变站记录：{recordId}");

        row.Date = date.Trim();
        row.StationId = stationId;
        row.Site = site;
        row.Longitude = longitude;
        row.Latitude = latitude;
        row.ElevationM = elevation;
        row.Crs = crs;
        row.TimeBasis = timeBasis;
        row.SamplingIntervalS = samplingIntervalS;
        row.Start = start;
        row.End = end;
        row.Artifacts = (artifacts ?? []).ToList();
        row.Status = status;
        row.Notes = notes;
        row.TaskLockWarning = TaskLockWarning(root);
        row.UpdatedAt = WorkspaceService.Now();

        _store.Write(
            ProjectPaths.Json(
                root,
                "base_station_logs.json"),
            rows);
        _store.AppendJsonLine(
            ProjectPaths.DataDirectory(root),
            "base_station_log_updated",
            new
            {
                record_id = row.RecordId,
                date = row.Date,
                task_lock_warning = row.TaskLockWarning
            });
        return row;
    }

    public List<DailyLogRecord> ListDailyLogs(string root) =>
        _store.Read(ProjectPaths.Json(root, "daily_logs.json"), new List<DailyLogRecord>());

    public DailyLogRecord SaveDailyLog(
        string root, string date, string weather = "", string site = "", string completed = "",
        string newFiles = "", string anomalies = "", string nextActions = "", string handoffStatus = "DRAFT",
        string notes = "")
    {
        using var writeLock = WorkspaceFileLock.AcquireForRoot(root);
        if (string.IsNullOrWhiteSpace(date)) throw new ArgumentException("日志日期不能为空");
        if (!new[] { "DRAFT", "READY", "HANDED_OFF" }.Contains(handoffStatus))
            throw new ArgumentException($"无效交接状态: {handoffStatus}");

        var rows = ListDailyLogs(root);
        var row = rows.FirstOrDefault(x => x.Date == date);
        string now = WorkspaceService.Now();
        if (row is null)
        {
            row = new DailyLogRecord { LogId = NextId(rows.Select(x => x.LogId), "DAILY"), Date = date, CreatedAt = now };
            rows.Add(row);
        }
        row.Weather = weather;
        row.Site = site;
        row.CompletedTasks = SplitList(completed);
        row.NewFiles = SplitList(newFiles);
        row.Anomalies = SplitList(anomalies);
        row.NextActions = SplitList(nextActions);
        row.HandoffStatus = handoffStatus;
        row.Notes = notes;
        row.UpdatedAt = now;
        _store.Write(ProjectPaths.Json(root, "daily_logs.json"), rows);
        _store.AppendJsonLine(ProjectPaths.DataDirectory(root), "daily_log_saved",
            new { log_id = row.LogId, date, handoff_status = handoffStatus });
        return row;
    }

    public List<FieldOperationsSettingsRecord> ListFieldOperationsSettings(string root) =>
        _store.Read(
            ProjectPaths.Json(root, "field_operations_settings.json"),
            new List<FieldOperationsSettingsRecord>());

    public FieldOperationsSettingsRecord? GetFieldOperationsSettings(
        string root,
        string workareaPath)
    {
        string normalized = (workareaPath ?? "")
            .Replace('\\', '/')
            .Trim('/');
        if (normalized.Length == 0)
            return null;

        return ListFieldOperationsSettings(root)
            .FirstOrDefault(x =>
                x.WorkareaPath.Equals(
                    normalized,
                    StringComparison.OrdinalIgnoreCase));
    }

    public FieldOperationsSettingsRecord SaveFieldOperationsSettings(
        string root,
        FieldOperationsSettingsRecord record)
    {
        using var writeLock = WorkspaceFileLock.AcquireForRoot(root);

        string workareaPath = (record.WorkareaPath ?? "")
            .Replace('\\', '/')
            .Trim('/');
        if (workareaPath.Length == 0)
            throw new ArgumentException("外业设置的工区路径不能为空");

        List<FieldOperationsSettingsRecord> rows =
            ListFieldOperationsSettings(root);
        int index = rows.FindIndex(x =>
            x.WorkareaPath.Equals(
                workareaPath,
                StringComparison.OrdinalIgnoreCase));

        record.WorkareaPath = workareaPath;
        record.ExpectedDailySorties =
            Math.Clamp(record.ExpectedDailySorties, 1, 100);
        record.AcceptedRelocations =
            Math.Clamp(record.AcceptedRelocations, 0, 20);
        record.UpdatedAt = WorkspaceService.Now();

        if (index >= 0)
            rows[index] = record;
        else
            rows.Add(record);

        _store.Write(
            ProjectPaths.Json(
                root,
                "field_operations_settings.json"),
            rows);
        _store.AppendJsonLine(
            ProjectPaths.DataDirectory(root),
            "field_operations_settings_saved",
            new
            {
                workarea_path = record.WorkareaPath,
                expected_daily_sorties = record.ExpectedDailySorties,
                accepted_relocations = record.AcceptedRelocations
            });
        return record;
    }

    public List<FieldDailyProgressRecord> ListFieldDailyProgress(string root) =>
        _store.Read(ProjectPaths.Json(root, "field_daily_progress.json"), new List<FieldDailyProgressRecord>());

    public FieldDailyProgressRecord SaveFieldDailyProgress(string root, FieldDailyProgressRecord record)
    {
        using var writeLock = WorkspaceFileLock.AcquireForRoot(root);
        if (string.IsNullOrWhiteSpace(record.Date))
            throw new ArgumentException("外业进度日期不能为空");

        var rows = ListFieldDailyProgress(root);
        FieldDailyProgressRecord? existing = rows.FirstOrDefault(x =>
            x.Date.Equals(record.Date, StringComparison.OrdinalIgnoreCase) &&
            x.Workarea.Equals(record.Workarea, StringComparison.OrdinalIgnoreCase));
        string now = WorkspaceService.Now();
        if (existing is null)
        {
            record.ProgressId = string.IsNullOrWhiteSpace(record.ProgressId)
                ? NextId(rows.Select(x => x.ProgressId), "FIELDPROGRESS")
                : record.ProgressId;
            record.CreatedAt = string.IsNullOrWhiteSpace(record.CreatedAt) ? now : record.CreatedAt;
            record.UpdatedAt = now;
            rows.Add(record);
            existing = record;
        }
        else
        {
            string id = existing.ProgressId;
            string created = existing.CreatedAt;
            int index = rows.IndexOf(existing);
            record.ProgressId = string.IsNullOrWhiteSpace(id) ? record.ProgressId : id;
            record.CreatedAt = string.IsNullOrWhiteSpace(created) ? now : created;
            record.UpdatedAt = now;
            rows[index] = record;
            existing = record;
        }

        _store.Write(ProjectPaths.Json(root, "field_daily_progress.json"), rows);
        _store.AppendJsonLine(ProjectPaths.DataDirectory(root), "field_daily_progress_saved",
            new
            {
                progress_id = existing.ProgressId,
                date = existing.Date,
                workarea = existing.Workarea,
                route_fingerprint = existing.RouteFingerprint,
                today_completed = existing.TodayCompletedSorties.Count,
                cumulative_completed = existing.CompletedSorties.Count,
                remaining = existing.RemainingSorties.Count
            });
        return existing;
    }

    public List<IssueRecord> ListIssues(string root) =>
        _store.Read(ProjectPaths.Json(root, "issues.json"), new List<IssueRecord>());

    public IssueRecord AddIssue(
        string root, string title, string severity = "MEDIUM", string impact = "", string temporaryMeasure = "",
        string owner = "", string notes = "")
    {
        using var writeLock = WorkspaceFileLock.AcquireForRoot(root);
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("问题标题不能为空");
        if (!AppConstants.IssueSeverities.Contains(severity)) throw new ArgumentException($"无效问题严重程度: {severity}");
        var rows = ListIssues(root);
        string now = WorkspaceService.Now();
        var row = new IssueRecord
        {
            IssueId = NextId(rows.Select(x => x.IssueId), "ISSUE"),
            Title = title.Trim(),
            Severity = severity,
            Impact = impact,
            TemporaryMeasure = temporaryMeasure,
            Owner = owner,
            Status = "OPEN",
            Notes = notes,
            CreatedAt = now,
            UpdatedAt = now
        };
        rows.Add(row);
        _store.Write(ProjectPaths.Json(root, "issues.json"), rows);
        _store.AppendJsonLine(ProjectPaths.DataDirectory(root), "issue_added",
            new { issue_id = row.IssueId, severity });
        return row;
    }

    public IssueRecord UpdateIssue(string root, string issueId, string status, string notes = "", string evidence = "")
    {
        using var writeLock = WorkspaceFileLock.AcquireForRoot(root);
        if (!AppConstants.IssueStatuses.Contains(status)) throw new ArgumentException($"无效问题状态: {status}");
        var rows = ListIssues(root);
        var row = rows.FirstOrDefault(x => x.IssueId == issueId)
            ?? throw new KeyNotFoundException($"未找到问题: {issueId}");
        row.Status = status;
        row.Notes = notes;
        row.ResolutionEvidence = SplitList(evidence);
        row.UpdatedAt = WorkspaceService.Now();
        _store.Write(ProjectPaths.Json(root, "issues.json"), rows);
        _store.AppendJsonLine(ProjectPaths.DataDirectory(root), "issue_updated",
            new { issue_id = issueId, status });
        return row;
    }

    private string TaskLockWarning(string root)
    {
        var info = _workspace.ReadTaskLock(root);
        if (info.Status is "" or "未找到" or "未识别") return "";
        string task = string.IsNullOrWhiteSpace(info.TaskId) ? "" : $"，任务编号 {info.TaskId}";
        return $"任务锁状态 {info.Status}{task}；本次记录未覆盖任务锁文件。";
    }

    private EquipmentChecklistTemplate? ResolveTemplate(EquipmentRecord row) =>
        EquipmentTemplates.Get(row.ChecklistTemplateId) ?? EquipmentTemplates.Find(row.Model, row.Name);

    private static string EquipmentRef(EquipmentRecord row) =>
        !string.IsNullOrWhiteSpace(row.RecordId) ? row.RecordId : row.EquipmentId;

    private static string LatestTestStatus(
        IEnumerable<EquipmentTestRecord> tests,
        EquipmentRecord equipment)
    {
        string reference = EquipmentRef(equipment);
        Dictionary<string, EquipmentTestRecord> latest = tests
            .Where(x => x.EquipmentRef == reference)
            .GroupBy(x => x.TestType, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Last(),
                StringComparer.OrdinalIgnoreCase);

        IReadOnlyList<EquipmentTestDefinition> expected =
            EquipmentTestProfiles.For(equipment);

        if (expected.Count == 0)
            return "NOT_REQUIRED";

        var statuses = new List<string>(expected.Count);
        foreach (EquipmentTestDefinition definition in expected)
        {
            statuses.Add(
                latest.TryGetValue(definition.TestType, out EquipmentTestRecord? row)
                    ? row.Status
                    : "PENDING");
        }

        if (statuses.Contains("FAILED", StringComparer.OrdinalIgnoreCase))
            return "BLOCKED";
        if (statuses.Contains("REVIEW_REQUIRED", StringComparer.OrdinalIgnoreCase))
            return "REVIEW_REQUIRED";
        if (statuses.Contains("PENDING", StringComparer.OrdinalIgnoreCase))
            return "PENDING";
        return "PASSED";
    }

    private static string CombinedReleaseStatus(string testStatus, string checklistStatus)
    {
        var statuses = new[] { testStatus ?? "PENDING", checklistStatus ?? "NOT_REQUIRED" };
        if (statuses.Contains("BLOCKED")) return "BLOCKED";
        if (statuses.Contains("REVIEW_REQUIRED")) return "REVIEW_REQUIRED";
        if (statuses.Contains("PENDING")) return "PENDING";
        return "PASSED";
    }

    private static string NextId(IEnumerable<string> ids, string prefix)
    {
        string stem = $"{prefix}-{DateTime.Now:yyyyMMdd}-";
        int seq = ids.Count(x => (x ?? "").StartsWith(stem, StringComparison.OrdinalIgnoreCase)) + 1;
        return $"{stem}{seq:00}";
    }

    private static List<string> SplitList(string value) =>
        (value ?? "").Split([',', '，', ';', '；', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static void WriteCsv(TextWriter writer, params string[] values) =>
        writer.WriteLine(string.Join(",", values.Select(Csv)));

    private static string Csv(string? value)
    {
        string text = value ?? "";
        if (text.Contains('"')) text = text.Replace("\"", "\"\"");
        return text.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? $"\"{text}\"" : text;
    }
}
