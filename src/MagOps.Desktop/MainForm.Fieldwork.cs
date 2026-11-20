using System.Globalization;
using MagOps.Core;
using MagOps.Core.Domain;
using MagOps.Infrastructure.Services;

namespace MagOps.Desktop;

public sealed partial class MainForm
{
    private void BuildFieldworkTab()
    {
        var page = new TabPage("出发整备") { Padding = Padding.Empty };
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = UiTheme.PagePadding,
            BackColor = UiTheme.Workspace
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        root.Controls.Add(UiComponents.PageHeader(
            "出发整备",
            "维护项目、人员和设备报告资料，登记设备并完成清点、测试与放行判断；飞行架次、日变站、每日日志和现场问题位于“外业管理 → 现场记录”。"), 0, 0);

        var card = UiComponents.Card(new Padding(10));
        var equipmentLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        equipmentLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        equipmentLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        equipmentLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        equipmentLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        equipmentLayout.Controls.Add(UiComponents.SectionLabel("1", "设备与放行"), 0, 0);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = true,
            AutoScroll = false,
            Margin = new Padding(0, 0, 0, 8)
        };
        buttons.Controls.Add(Button("新增设备", (_, _) => RunUi("新增设备", AddEquipment), UiButtonRole.Primary));
        buttons.Controls.Add(Button("删除设备", (_, _) => RunUi("删除设备", DeleteEquipment), UiButtonRole.Subtle));
        buttons.Controls.Add(Button("打开设备清点", (_, _) => RunUi("设备清点", OpenChecklist)));
        buttons.Controls.Add(Button("报告资料字典", (_, _) => RunUi("报告资料字典", () =>
        {
            if (string.IsNullOrWhiteSpace(Root)) throw new InvalidOperationException("请先打开项目。");
            using var dialog = new ProjectReportProfileDialog(Root);
            if (dialog.ShowDialog(this) == DialogResult.OK) SetStatus("报告资料字典已保存，可在报告编写中引用。");
        })));
        equipmentLayout.Controls.Add(buttons, 0, 1);

        GridFactory.SetEmptyText(
            _equipmentGrid,
            "尚未登记设备。\r\n点击“新增设备”建立设备档案，并完成出发前清点与测试。");
        _equipmentGrid.DoubleClick += (_, _) => RunUi("设备清点", OpenChecklist);
        equipmentLayout.Controls.Add(_equipmentGrid, 0, 2);
        card.Controls.Add(equipmentLayout);
        root.Controls.Add(card, 0, 1);
        page.Controls.Add(root);
        RegisterFeaturePage(FeatureIds.DepartureReadiness, page);
    }

    private Control BuildFlightWorkspaceControl()
    {
        var outer = StandardGridPage(out FlowLayoutPanel buttons);
        buttons.Controls.Add(Button("新增架次", (_, _) => RunUi("新增飞行架次", AddFlight)));
        buttons.Controls.Add(Button("更新所选架次", (_, _) => RunUi("更新飞行架次", UpdateFlight)));
        GridFactory.SetEmptyText(_flightGrid, "暂无飞行架次记录。");
        outer.Controls.Add(_flightGrid, 0, 1);
        return outer;
    }

    private Control BuildBaseStationWorkspaceControl()
    {
        var outer = StandardGridPage(out FlowLayoutPanel buttons);
        buttons.Controls.Add(Button(
            "刷新记录",
            (_, _) => RunUi("刷新日变站记录", RefreshFieldwork),
            UiButtonRole.Subtle));
        GridFactory.SetEmptyText(
            _baseGrid,
            "暂无日变站记录。\r\n登记/更新入口位于“外业管理”顶部外业数据目录下方。");
        outer.Controls.Add(_baseGrid, 0, 1);
        return outer;
    }

    private Control BuildDailyLogWorkspaceControl()
    {
        var outer = StandardGridPage(out FlowLayoutPanel buttons);
        buttons.Controls.Add(Button("新增/更新日志", (_, _) => RunUi("保存每日日志", SaveDailyLog)));
        buttons.Controls.Add(Button("执行所选日 QC", (_, _) => RunUi("当日 QC", RunDailyQc)));
        GridFactory.SetEmptyText(_dailyGrid, "暂无每日日志。");
        outer.Controls.Add(_dailyGrid, 0, 1);
        return outer;
    }

    private Control BuildIssueWorkspaceControl()
    {
        var outer = StandardGridPage(out FlowLayoutPanel buttons);
        buttons.Controls.Add(Button("新增问题", (_, _) => RunUi("新增问题", AddIssue)));
        buttons.Controls.Add(Button("更新所选问题", (_, _) => RunUi("更新问题", UpdateIssue)));
        GridFactory.SetEmptyText(_issueGrid, "暂无现场问题。");
        outer.Controls.Add(_issueGrid, 0, 1);
        return outer;
    }

    private void ShowFieldRecordWindow(
        string key,
        string title,
        Func<Control> contentFactory)
    {
        EnsureInitialized();
        RefreshFieldwork();

        if (!_fieldRecordWindows.TryGetValue(key, out Form? window) ||
            window.IsDisposed)
        {
            window = new Form
            {
                Text = title,
                StartPosition = FormStartPosition.CenterParent,
                Size = new Size(1180, 720),
                MinimumSize = new Size(640, 420),
                ShowInTaskbar = false,
                AutoScaleMode = AutoScaleMode.Dpi
            };

            Control content = contentFactory();
            content.Dock = DockStyle.Fill;
            window.Controls.Add(content);
            window.FormClosing += (_, e) =>
            {
                if (e.CloseReason == CloseReason.UserClosing)
                {
                    e.Cancel = true;
                    window.Hide();
                }
            };
            Form recordWindow = window;
            recordWindow.Shown += (_, _) =>
            {
                UiLayout.FitFormToWorkingArea(recordWindow, this);
                UiLayout.Normalize(recordWindow);
            };
            recordWindow.DpiChanged += (_, _) =>
                recordWindow.BeginInvoke((Action)(() => UiLayout.Normalize(recordWindow)));
            _fieldRecordWindows[key] = recordWindow;
        }

        if (window.Visible)
        {
            window.BringToFront();
            window.Activate();
        }
        else
        {
            window.Show(this);
        }
    }

    private static TableLayoutPanel StandardGridPage(out FlowLayoutPanel buttons)
    {
        var outer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(8)
        };
        outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, AutoScroll = false };
        outer.Controls.Add(buttons, 0, 0);
        return outer;
    }

    private void RefreshFieldwork()
    {
        if (!MagOps.Infrastructure.Storage.ProjectPaths.HasProject(Root)) return;

        var equipment = _fieldwork.ListEquipment(Root);
        var allTests = _fieldwork.ListEquipmentTests(Root);
        var equipmentRows = new List<EquipmentGridRow>();
        foreach (EquipmentRecord item in equipment)
        {
            string reference = string.IsNullOrWhiteSpace(item.RecordId)
                ? item.EquipmentId
                : item.RecordId;
            List<EquipmentTestRecord> tests = allTests
                .Where(x => x.EquipmentRef == reference)
                .OrderBy(x => x.TestedAt, StringComparer.OrdinalIgnoreCase)
                .ToList();

            string blockReason = "";
            IReadOnlyList<EquipmentTestDefinition> testDefinitions =
                EquipmentTestProfiles.For(item);
            HashSet<string> expectedTestTypes = testDefinitions
                .Select(x => x.TestType)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            List<EquipmentTestRecord> latestByType = tests
                .Where(x => expectedTestTypes.Contains(x.TestType))
                .GroupBy(x => x.TestType, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.Last())
                .ToList();
            EquipmentTestRecord? latest = latestByType
                .OrderBy(x => x.TestedAt, StringComparer.OrdinalIgnoreCase)
                .LastOrDefault();
            string[] failedTypes = latestByType
                .Where(x => x.Status == "FAILED" || x.Status == "BLOCKED")
                .Select(x => x.TestType)
                .ToArray();
            if (failedTypes.Length > 0)
                blockReason = "测试失败：" + string.Join("、", failedTypes);
            else if (testDefinitions.Count > 0)
            {
                int passedTests = latestByType.Count(x =>
                    x.Status.Equals("PASSED", StringComparison.OrdinalIgnoreCase));
                if (passedTests < testDefinitions.Count)
                    blockReason = $"设备测试 {passedTests}/{testDefinitions.Count}";
            }

            if (blockReason.Length == 0)
            {
                ChecklistView checklist = _fieldwork.GetChecklist(Root, reference);
                if (checklist.Available && checklist.RequiredChecked < checklist.RequiredTotal)
                    blockReason = $"必备清点 {checklist.RequiredChecked}/{checklist.RequiredTotal}";
            }

            equipmentRows.Add(new EquipmentGridRow(
                reference,
                item.EquipmentId,
                item.Model,
                item.EquipmentType,
                item.SerialNo,
                item.ChecklistStatus,
                item.TestStatus,
                item.ReleaseStatus,
                FormatUiTime(latest?.TestedAt),
                blockReason,
                item.Notes));
        }

        _equipmentGrid.DataSource = null;
        _equipmentGrid.DataSource = equipmentRows;
        GridHeaders.Apply(_equipmentGrid, new Dictionary<string, string>
        {
            [nameof(EquipmentGridRow.EquipmentId)] = "设备编号",
            [nameof(EquipmentGridRow.Model)] = "型号",
            [nameof(EquipmentGridRow.EquipmentType)] = "类型",
            [nameof(EquipmentGridRow.SerialNo)] = "序列号/资产号",
            [nameof(EquipmentGridRow.ChecklistStatus)] = "清点",
            [nameof(EquipmentGridRow.TestStatus)] = "测试",
            [nameof(EquipmentGridRow.ReleaseStatus)] = "放行建议",
            [nameof(EquipmentGridRow.LatestTestAt)] = "最近测试",
            [nameof(EquipmentGridRow.BlockReason)] = "阻断/待办",
            [nameof(EquipmentGridRow.Notes)] = "备注"
        },
        [nameof(EquipmentGridRow.EquipmentRef)]);

        UiLayout.ApplyFillWeights(_equipmentGrid, new Dictionary<string, float>
        {
            [nameof(EquipmentGridRow.EquipmentId)] = 65,
            [nameof(EquipmentGridRow.Model)] = 85,
            [nameof(EquipmentGridRow.EquipmentType)] = 70,
            [nameof(EquipmentGridRow.SerialNo)] = 85,
            [nameof(EquipmentGridRow.ChecklistStatus)] = 60,
            [nameof(EquipmentGridRow.TestStatus)] = 60,
            [nameof(EquipmentGridRow.ReleaseStatus)] = 65,
            [nameof(EquipmentGridRow.LatestTestAt)] = 100,
            [nameof(EquipmentGridRow.BlockReason)] = 140,
            [nameof(EquipmentGridRow.Notes)] = 120
        }, minimumWidth: 65);

        var flights = _fieldwork.ListFlights(Root);
        _flightGrid.DataSource = null;
        _flightGrid.DataSource = flights;
        GridHeaders.Apply(_flightGrid, new Dictionary<string, string>
        {
            [nameof(FlightBatchRecord.BatchId)] = "架次编号",
            [nameof(FlightBatchRecord.Date)] = "日期",
            [nameof(FlightBatchRecord.Workarea)] = "工区",
            [nameof(FlightBatchRecord.Aircraft)] = "飞行器",
            [nameof(FlightBatchRecord.Magnetometer)] = "磁力仪",
            [nameof(FlightBatchRecord.BaseStation)] = "日变站",
            [nameof(FlightBatchRecord.RouteVersion)] = "航线版本",
            [nameof(FlightBatchRecord.Operator)] = "操作人",
            [nameof(FlightBatchRecord.Status)] = "状态",
            [nameof(FlightBatchRecord.Result)] = "结果",
            [nameof(FlightBatchRecord.Notes)] = "备注"
        },
        [nameof(FlightBatchRecord.Subarea), nameof(FlightBatchRecord.RawArtifacts), nameof(FlightBatchRecord.Issues),
         nameof(FlightBatchRecord.CreatedAt), nameof(FlightBatchRecord.UpdatedAt), nameof(FlightBatchRecord.Extra)]);

        var stations = _fieldwork.ListBaseStations(Root);
        _baseGrid.DataSource = null;
        _baseGrid.DataSource = stations;
        GridHeaders.Apply(_baseGrid, new Dictionary<string, string>
        {
            [nameof(BaseStationLogRecord.RecordId)] = "记录编号",
            [nameof(BaseStationLogRecord.Date)] = "日期",
            [nameof(BaseStationLogRecord.StationId)] = "站号",
            [nameof(BaseStationLogRecord.Site)] = "位置",
            [nameof(BaseStationLogRecord.Longitude)] = "经度",
            [nameof(BaseStationLogRecord.Latitude)] = "纬度",
            [nameof(BaseStationLogRecord.ElevationM)] = "高程(m)",
            [nameof(BaseStationLogRecord.Crs)] = "坐标系",
            [nameof(BaseStationLogRecord.Status)] = "状态",
            [nameof(BaseStationLogRecord.Notes)] = "备注"
        },
        [nameof(BaseStationLogRecord.Artifacts), nameof(BaseStationLogRecord.CreatedAt),
         nameof(BaseStationLogRecord.UpdatedAt), nameof(BaseStationLogRecord.Extra)]);

        var logs = _fieldwork.ListDailyLogs(Root);
        _dailyGrid.DataSource = null;
        _dailyGrid.DataSource = logs;
        GridHeaders.Apply(_dailyGrid, new Dictionary<string, string>
        {
            [nameof(DailyLogRecord.Date)] = "日期",
            [nameof(DailyLogRecord.Weather)] = "天气",
            [nameof(DailyLogRecord.Site)] = "地点",
            [nameof(DailyLogRecord.HandoffStatus)] = "交接状态",
            [nameof(DailyLogRecord.Notes)] = "备注",
            [nameof(DailyLogRecord.UpdatedAt)] = "更新时间"
        },
        [nameof(DailyLogRecord.LogId), nameof(DailyLogRecord.CompletedTasks), nameof(DailyLogRecord.NewFiles),
         nameof(DailyLogRecord.Anomalies), nameof(DailyLogRecord.NextActions),
         nameof(DailyLogRecord.CreatedAt), nameof(DailyLogRecord.Extra)]);

        var issues = _fieldwork.ListIssues(Root);
        _issueGrid.DataSource = null;
        _issueGrid.DataSource = issues;
        GridHeaders.Apply(_issueGrid, new Dictionary<string, string>
        {
            [nameof(IssueRecord.IssueId)] = "问题编号",
            [nameof(IssueRecord.Title)] = "问题",
            [nameof(IssueRecord.Severity)] = "严重程度",
            [nameof(IssueRecord.Impact)] = "影响",
            [nameof(IssueRecord.TemporaryMeasure)] = "临时措施",
            [nameof(IssueRecord.Owner)] = "负责人",
            [nameof(IssueRecord.Status)] = "状态",
            [nameof(IssueRecord.Notes)] = "备注",
            [nameof(IssueRecord.UpdatedAt)] = "更新时间"
        },
        [nameof(IssueRecord.ResolutionEvidence), nameof(IssueRecord.CreatedAt), nameof(IssueRecord.Extra)]);
    }

    private void AddEquipment()
    {
        EnsureInitialized();

        EquipmentCatalog catalog = _catalog.Load(Root);
        using var dialog = new EquipmentRegistrationDialog(catalog);
        if (dialog.ShowDialog(this) != DialogResult.OK ||
            dialog.Result is not EquipmentRegistrationValues values)
        {
            return;
        }

        _fieldwork.AddEquipment(
            Root,
            values.Model,
            values.EquipmentId,
            values.EquipmentType,
            values.SerialNo,
            values.Notes,
            values.Category);

        _catalog.AddModel(
            Root,
            values.Category,
            values.Model);

        RefreshFieldwork();
    }

    private void DeleteEquipment()
    {
        EnsureInitialized();
        if (_equipmentGrid.CurrentRow?.DataBoundItem is not EquipmentGridRow row)
            throw new InvalidOperationException("请先选择设备。");
        if (MessageBox.Show(this, $"确定删除设备 {row.Model} {row.EquipmentId} 及其清点/测试记录？",
                "删除设备", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;
        _fieldwork.DeleteEquipment(Root, row.EquipmentRef);
        RefreshFieldwork();
    }

    private void OpenChecklist()
    {
        EnsureInitialized();
        if (_equipmentGrid.CurrentRow?.DataBoundItem is not EquipmentGridRow row)
            throw new InvalidOperationException("请先选择设备。");
        using var form = new EquipmentChecklistForm(
            _fieldwork,
            Root,
            row.EquipmentRef,
            (operationKey, purpose) => EnsureManagedOutputDirectory(operationKey, purpose),
            path => RegisterGeneratedBusinessFile(path, "fieldwork_export"));
        form.ShowDialog(this);
        RefreshFieldwork();
    }

    private static string FormatUiTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
        return DateTimeOffset.TryParse(value, out DateTimeOffset parsed)
            ? parsed.LocalDateTime.ToString("yyyy-MM-dd HH:mm")
            : value;
    }

    private string FieldworkOutputPath(string operationKey, string purpose, string fileName)
    {
        string directory = EnsureManagedOutputDirectory(operationKey, purpose);
        string safe = string.Concat(
            fileName.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        return Path.Combine(directory, safe);
    }

    private void ExportFlightSnapshot(FlightBatchRecord row)
    {
        if (!string.IsNullOrWhiteSpace(row.Workarea))
            TrySetCurrentWorkareaByDisplayName(row.Workarea);
        string path = FieldworkOutputPath(
            "flight_evidence",
            "保存飞行架次业务记录",
            $"飞行架次_{row.BatchId}_{DateTime.Now:yyyyMMdd_HHmmss}.md");
        if (!ExportProtected(path, "fieldwork_flight",
            [ExportRecord("flight_batches.json", "batch_id", row.BatchId, row, FlightExportFields)],
            new { formatVersion = "field-document-1" },
            staged => FieldworkDocumentExportService.WriteFlight(staged, row))) return;
    }

    private void ExportBaseStationSnapshot(BaseStationLogRecord row)
    {
        string path = FieldworkOutputPath(
            "base_station_evidence",
            "保存日变站业务记录",
            $"日变站_{row.RecordId}_{DateTime.Now:yyyyMMdd_HHmmss}.md");
        if (!ExportProtected(path, "fieldwork_base_station",
            [ExportRecord("base_station_logs.json", "record_id", row.RecordId, row, StationExportFields)],
            new { formatVersion = "field-document-1" },
            staged => FieldworkDocumentExportService.WriteBaseStation(staged, row), projectLevel: true)) return;
    }

    private void ExportDailyLogSnapshot(DailyLogRecord row)
    {
        string path = FieldworkOutputPath(
            "daily_log_export",
            "保存每日日志业务文件",
            $"每日日志_{row.Date}_{DateTime.Now:HHmmss}.md");
        if (!ExportProtected(path, "fieldwork_daily_log",
            [ExportRecord("daily_logs.json", "log_id", row.LogId, row, DailyLogExportFields)],
            new { formatVersion = "field-document-1" },
            staged => FieldworkDocumentExportService.WriteDailyLog(staged, row), projectLevel: true)) return;
    }

    private void ExportIssueSnapshot(IssueRecord row)
    {
        string path = FieldworkOutputPath(
            "issue_evidence",
            "保存现场问题业务记录",
            $"现场问题_{row.IssueId}_{DateTime.Now:yyyyMMdd_HHmmss}.md");
        if (!ExportProtected(path, "fieldwork_issue",
            [ExportRecord("issues.json", "issue_id", row.IssueId, row, IssueExportFields)],
            new { formatVersion = "field-document-1" },
            staged => FieldworkDocumentExportService.WriteIssue(staged, row), projectLevel: true)) return;
    }

    private void AddFlight()
    {
        EnsureInitialized();
        var workareaRows = _workspace.GetWorkareas(Root);
        var workareas = workareaRows.Select(x => x.DisplayName).Prepend("").ToArray();
        string defaultWorkarea = workareaRows
            .FirstOrDefault(x => x.Path.Equals(CurrentWorkareaRelativePath, StringComparison.OrdinalIgnoreCase))
            ?.DisplayName ?? "";
        var values = PromptDialog.Show(this, "新增飞行架次",
        [
            new("date", "日期", _fieldOperationsDate.Value.Date.ToString("yyyy-MM-dd"), Required: true),
            new("workarea", "工区", defaultWorkarea, workareas),
            new("aircraft", "飞行器"),
            new("magnetometer", "磁力仪"),
            new("base_station", "日变站"),
            new("route_version", "航线版本"),
            new("operator", "操作人"),
            new("start", "开始时间"),
            new("end", "结束时间"),
            new("raw_artifacts", "原始资料路径", "", Multiline: true),
            new("issues", "关联问题", "", Multiline: true),
            new("status", "状态", "PLANNED", AppConstants.FlightStatuses),
            new("notes", "备注", "", Multiline: true)
        ], height: 760);
        if (values is null) return;
        FlightBatchRecord saved = _fieldwork.AddFlight(
            Root, values["date"], values["workarea"], values["aircraft"],
            values["magnetometer"], values["base_station"], values["route_version"],
            values["operator"], values["start"], values["end"],
            SplitValues(values["raw_artifacts"]), "", SplitValues(values["issues"]),
            values["status"], values["notes"]);
        ExportFlightSnapshot(saved);
        RefreshFieldwork();
    }

    private void UpdateFlight()
    {
        EnsureInitialized();
        if (_flightGrid.CurrentRow?.DataBoundItem is not FlightBatchRecord row)
            throw new InvalidOperationException("请先选择飞行架次。");
        var values = PromptDialog.Show(this, $"更新架次 - {row.BatchId}",
        [
            new("status", "状态", row.Status, AppConstants.FlightStatuses),
            new("start", "开始时间", row.Start),
            new("end", "结束时间", row.End),
            new("route_version", "航线版本", row.RouteVersion),
            new("raw_artifacts", "原始资料路径", string.Join(Environment.NewLine, row.RawArtifacts), Multiline: true),
            new("result", "结果", row.Result),
            new("issues", "关联问题", string.Join(Environment.NewLine, row.Issues), Multiline: true),
            new("notes", "备注", row.Notes, Multiline: true)
        ], height: 580);
        if (values is null) return;
        FlightBatchRecord saved = _fieldwork.UpdateFlight(
            Root, row.BatchId, values["status"], values["start"], values["end"],
            values["route_version"], SplitValues(values["raw_artifacts"]), values["result"],
            SplitValues(values["issues"]), values["notes"]);
        ExportFlightSnapshot(saved);
        RefreshFieldwork();
    }

    private void RegisterOrUpdateBaseStationForSelectedDate()
    {
        EnsureInitialized();
        string selectedDate =
            _fieldOperationsDate.Value.Date
                .ToString(
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture);
        BaseStationLogRecord? existing =
            _fieldwork.ListBaseStations(Root)
                .Where(x =>
                    x.Date.Equals(
                        selectedDate,
                        StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => x.UpdatedAt, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.RecordId, StringComparer.OrdinalIgnoreCase)
                .LastOrDefault();

        var values = PromptDialog.Show(
            this,
            existing is null
                ? "登记日变站"
                : $"更新日变站 - {existing.RecordId}",
        [
            new("date", "日期", selectedDate, Required: true),
            new("station_id", "站号", existing?.StationId ?? ""),
            new("site", "位置", existing?.Site ?? ""),
            new("longitude", "经度", existing?.Longitude ?? ""),
            new("latitude", "纬度", existing?.Latitude ?? ""),
            new("elevation", "高程(m)", existing?.ElevationM ?? ""),
            new("crs", "坐标系", existing?.Crs ?? "CGCS2000"),
            new("time_basis", "时间基准", existing?.TimeBasis ?? "UTC"),
            new("sampling", "采样间隔(s)", existing?.SamplingIntervalS ?? ""),
            new("start", "开始时间", existing?.Start ?? ""),
            new("end", "结束时间", existing?.End ?? ""),
            new("artifacts", "资料路径", existing is null
                ? ""
                : string.Join(Environment.NewLine, existing.Artifacts), Multiline: true),
            new("status", "状态", existing?.Status ?? "RECORDED", ["RECORDED", "REVIEW_REQUIRED", "BLOCKED"]),
            new("notes", "备注", existing?.Notes ?? "", Multiline: true)
        ], height: 720,
        note: existing is null
            ? "登记后会立即显示在外业管理顶部；有效经纬度会同步到进度预览。"
            : "当前作业日已有记录，本次保存会更新最近一条记录；完整历史可从顶部“历史记录”查看。" );
        if (values is null)
            return;

        BaseStationLogRecord saved =
            existing is null
                ? _fieldwork.AddBaseStation(
                    Root,
                    values["date"],
                    values["station_id"],
                    values["site"],
                    values["longitude"],
                    values["latitude"],
                    values["elevation"],
                    values["crs"],
                    values["time_basis"],
                    values["sampling"],
                    values["start"],
                    values["end"],
                    SplitValues(values["artifacts"]),
                    values["status"],
                    values["notes"])
                : _fieldwork.UpdateBaseStation(
                    Root,
                    existing.RecordId,
                    values["date"],
                    values["station_id"],
                    values["site"],
                    values["longitude"],
                    values["latitude"],
                    values["elevation"],
                    values["crs"],
                    values["time_basis"],
                    values["sampling"],
                    values["start"],
                    values["end"],
                    SplitValues(values["artifacts"]),
                    values["status"],
                    values["notes"]);

        ExportBaseStationSnapshot(saved);
        RefreshFieldwork();
        RefreshFieldBaseStationContext();
    }

    private void SaveDailyLog()
    {
        EnsureInitialized();
        var values = PromptDialog.Show(this, "新增/更新每日日志",
        [
            new("date", "日期", _fieldOperationsDate.Value.Date.ToString("yyyy-MM-dd"), Required: true),
            new("weather", "天气"),
            new("site", "地点"),
            new("completed", "已完成任务", "", Multiline: true),
            new("new_files", "新增文件", "", Multiline: true),
            new("anomalies", "异常", "", Multiline: true),
            new("next_actions", "下一步", "", Multiline: true),
            new("handoff", "交接状态", "DRAFT", ["DRAFT", "READY", "HANDED_OFF"]),
            new("notes", "备注", "", Multiline: true)
        ], height: 760);
        if (values is null) return;
        DailyLogRecord saved = _fieldwork.SaveDailyLog(
            Root, values["date"], values["weather"], values["site"],
            values["completed"], values["new_files"], values["anomalies"], values["next_actions"],
            values["handoff"], values["notes"]);
        ExportDailyLogSnapshot(saved);
        RefreshFieldwork();
    }

    private void RunDailyQc()
    {
        EnsureInitialized();
        var values = PromptDialog.Show(this, "执行当日 QC",
        [
            new("date", "日期", _fieldOperationsDate.Value.Date.ToString("yyyy-MM-dd"), Required: true),
            new("required_route_checks", "必需航线/QC命令", "", Multiline: true),
            new("confirm", "通过后确认 field 门禁", "否", ["否", "是"]),
            new("confirmed_by", "确认人")
        ]);
        if (values is null) return;
        bool confirm = values["confirm"] == "是";
        if (confirm && string.IsNullOrWhiteSpace(values["confirmed_by"]))
            throw new InvalidOperationException("确认 field 门禁时必须填写确认人。");

        string[] sourceCommands = SplitValues(values["required_route_checks"]).ToArray();
        var qcSources = CaptureQcSources(values["date"], sourceCommands).ToList();
        var result = _quality.RunDailyQc(
            Root,
            values["date"],
            SplitValues(values["required_route_checks"]),
            confirmGate: confirm,
            confirmedBy: values["confirmed_by"]);
        RefreshProject();
        string qcPath = FieldworkOutputPath(
            "field_qc_export",
            "保存外业当日 QC 记录",
            $"外业QC_{values["date"]}_{DateTime.Now:HHmmss}.md");
        qcSources.Add(ArtifactDependencies.Query(Root,
            MagOps.Infrastructure.Storage.ProjectPaths.Json(Root, "qc_results.json"),
            new() { ["qc_id"] = "daily-qc-" + values["date"] }, fields: ["date", "status", "checks", "confirmed_by"], minimumCount: 1));
        qcSources.Add(ArtifactDependencies.Object(Root,
            MagOps.Infrastructure.Storage.ProjectPaths.ExistingProject(Root), ["gates.field", "gate_records.field"]));
        if (!ExportProtected(qcPath, "fieldwork_qc", qcSources,
            new { commands = sourceCommands, date = values["date"], confirm, ruleVersion = "daily-qc-1" },
            staged => FieldworkDocumentExportService.WriteDailyQc(staged, values["date"], result.Status,
                result.Checks.Select(x => (x.Status, x.Name, x.Detail))), projectLevel: true)) return;

        string detail = string.Join("\r\n", result.Checks.Select(x => $"[{UiText.Display(x.Status)}] {x.Name}: {x.Detail}"));
        MessageBox.Show(this, $"当日 QC：{UiText.Display(result.Status)}\r\n\r\n{detail}", "外业 QC", MessageBoxButtons.OK,
            result.Status == "PASSED" ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }

    private void AddIssue()
    {
        EnsureInitialized();
        var values = PromptDialog.Show(this, "新增问题",
        [
            new("title", "问题标题", Required: true),
            new("severity", "严重程度", "MEDIUM", AppConstants.IssueSeverities),
            new("impact", "影响", "", Multiline: true),
            new("measure", "临时措施", "", Multiline: true),
            new("owner", "负责人"),
            new("notes", "备注", "", Multiline: true)
        ], height: 620);
        if (values is null) return;
        IssueRecord saved = _fieldwork.AddIssue(
            Root, values["title"], values["severity"], values["impact"],
            values["measure"], values["owner"], values["notes"]);
        ExportIssueSnapshot(saved);
        RefreshFieldwork();
    }

    private void UpdateIssue()
    {
        EnsureInitialized();
        if (_issueGrid.CurrentRow?.DataBoundItem is not IssueRecord row)
            throw new InvalidOperationException("请先选择问题记录。");
        var values = PromptDialog.Show(this, $"更新问题 - {row.IssueId}",
        [
            new("status", "状态", row.Status, AppConstants.IssueStatuses),
            new("evidence", "解决证据", string.Join(Environment.NewLine, row.ResolutionEvidence), Multiline: true),
            new("notes", "备注", row.Notes, Multiline: true)
        ]);
        if (values is null) return;
        IssueRecord saved = _fieldwork.UpdateIssue(
            Root, row.IssueId, values["status"], values["notes"], values["evidence"]);
        ExportIssueSnapshot(saved);
        RefreshFieldwork();
    }
}

internal sealed record EquipmentGridRow(
    string EquipmentRef,
    string EquipmentId,
    string Model,
    string EquipmentType,
    string SerialNo,
    string ChecklistStatus,
    string TestStatus,
    string ReleaseStatus,
    string LatestTestAt,
    string BlockReason,
    string Notes);

