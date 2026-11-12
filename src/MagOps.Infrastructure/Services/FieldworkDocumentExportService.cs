using System.Text;
using MagOps.Core.Domain;

namespace MagOps.Infrastructure.Services;

public static class FieldworkDocumentExportService
{
    public static string WriteEquipmentTest(
        string destination,
        EquipmentRecord equipment,
        EquipmentTestRecord test)
    {
        return Write(destination, $"设备测试 - {equipment.Model} {equipment.EquipmentId}",
        [
            ("设备型号", equipment.Model),
            ("设备编号", equipment.EquipmentId),
            ("序列号/资产号", equipment.SerialNo),
            ("测试编号", test.TestId),
            ("测试类型", test.TestType),
            ("测试状态", DisplayStatus(test.Status)),
            ("测试时间", test.TestedAt),
            ("说明", test.Note),
            ("替代旧测试", test.SupersedesTestId),
            ("证据", string.Join("；", test.EvidenceArtifacts))
        ]);
    }

    public static string WriteEquipmentTestSummary(
        string destination,
        EquipmentRecord equipment,
        IReadOnlyList<EquipmentTestRecord> tests)
    {
        Dictionary<string, string> checks = EquipmentTestProfiles.For(equipment)
            .ToDictionary(
                x => x.TestType,
                x => x.Check,
                StringComparer.OrdinalIgnoreCase);
        var sb = Header($"设备测试 - {equipment.Model} {equipment.EquipmentId}");
        sb.AppendLine($"- **设备型号**：{equipment.Model}");
        sb.AppendLine($"- **设备编号**：{equipment.EquipmentId}");
        sb.AppendLine($"- **序列号/资产号**：{equipment.SerialNo}");
        sb.AppendLine();
        sb.AppendLine("| 测试项目 | 测试内容 | 状态 | 测试时间 | 说明 |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (EquipmentTestRecord test in tests)
        {
            sb.AppendLine(
                $"| {MarkdownCell(test.TestType)} | {MarkdownCell(checks.GetValueOrDefault(test.TestType, ""))} | " +
                $"{MarkdownCell(DisplayStatus(test.Status))} | " +
                $"{MarkdownCell(test.TestedAt)} | {MarkdownCell(test.Note)} |");
        }
        sb.AppendLine();
        sb.AppendLine("> 同一测试类型再次保存时，系统会保留历史记录，并通过 supersedes_test_id 指向被替代记录。");
        return WriteText(destination, sb.ToString());
    }

    public static string WriteFlight(string destination, FlightBatchRecord row)
    {
        return Write(destination, $"飞行架次 - {row.BatchId}",
        [
            ("日期", row.Date),
            ("工区", row.Workarea),
            ("飞行器", row.Aircraft),
            ("磁力仪", row.Magnetometer),
            ("日变站", row.BaseStation),
            ("航线版本", row.RouteVersion),
            ("操作人", row.Operator),
            ("开始", row.Start),
            ("结束", row.End),
            ("状态", DisplayStatus(row.Status)),
            ("结果", row.Result),
            ("原始资料", string.Join("；", row.RawArtifacts)),
            ("问题", string.Join("；", row.Issues)),
            ("备注", row.Notes),
            ("任务锁提示", row.TaskLockWarning),
            ("更新时间", row.UpdatedAt)
        ]);
    }

    public static string WriteBaseStation(string destination, BaseStationLogRecord row)
    {
        return Write(destination, $"日变站记录 - {row.RecordId}",
        [
            ("日期", row.Date),
            ("站号", row.StationId),
            ("位置", row.Site),
            ("经度", row.Longitude),
            ("纬度", row.Latitude),
            ("高程(m)", row.ElevationM),
            ("坐标系", row.Crs),
            ("时间基准", row.TimeBasis),
            ("采样间隔(s)", row.SamplingIntervalS),
            ("开始", row.Start),
            ("结束", row.End),
            ("状态", DisplayStatus(row.Status)),
            ("资料", string.Join("；", row.Artifacts)),
            ("备注", row.Notes),
            ("任务锁提示", row.TaskLockWarning),
            ("更新时间", row.UpdatedAt)
        ]);
    }

    public static string WriteDailyLog(string destination, DailyLogRecord row)
    {
        return Write(destination, $"每日日志 - {row.Date}",
        [
            ("日期", row.Date),
            ("天气", row.Weather),
            ("地点", row.Site),
            ("完成任务", string.Join("；", row.CompletedTasks)),
            ("新增文件", string.Join("；", row.NewFiles)),
            ("异常", string.Join("；", row.Anomalies)),
            ("下一步", string.Join("；", row.NextActions)),
            ("交接状态", DisplayStatus(row.HandoffStatus)),
            ("备注", row.Notes),
            ("更新时间", row.UpdatedAt)
        ]);
    }

    public static string FormatDayWorkPercent(
        FieldDailyProgressRecord row,
        int? expectedDailySorties = null) =>
        WorkPercent(
            DayWorkPercent(
                row,
                expectedDailySorties));

    public static string BuildDailyProgressPreview(
        FieldDailyProgressRecord row,
        int? expectedDailySorties = null)
    {
        double dayWorkPercent =
            DayWorkPercent(row, expectedDailySorties);
        var sb = new StringBuilder();
        sb.AppendLine($"当天简报｜{Normalize(row.Date)}（{ProjectTime.ShortLabel(row.ProgressTimeZone)}）");
        if (row.SurveyLineMatchingAvailable &&
            row.PlannedSurveyLineKm > 0d)
        {
            sb.AppendLine(
                $"当日完成测线 {Km(row.TodayCompletedSurveyLineKm)}｜" +
                $"当天工作百分比 {WorkPercent(dayWorkPercent)}｜" +
                $"累计完成 {Km(row.CompletedSurveyLineKm)} / {Km(row.PlannedSurveyLineKm)}｜" +
                $"测线里程完成率 {Percent(row.TotalCompletionPercent)}");
            sb.AppendLine(
                $"当日完成架次 {row.TodayCompletedSorties.Count}｜累计完成架次 {row.CompletedSorties.Count}/{row.PlannedSortieCount}");
        }
        else
        {
            sb.AppendLine(
                $"设计 {row.PlannedSortieCount} 架次｜当日完成 {row.TodayCompletedSorties.Count} 架次｜" +
                $"当天工作百分比 {WorkPercent(dayWorkPercent)}");
            sb.AppendLine(
                $"累计完成 {row.CompletedSorties.Count} 架次｜项目总完成率 {Percent(row.TotalCompletionPercent)}｜" +
                $"剩余 {row.RemainingSorties.Count} 架次");
        }
        if (row.DjiFlightCount > 0)
        {
            sb.AppendLine(
                $"DJI 实际飞行 {row.DjiFlightCount} 次｜飞行距离 {Km(row.DjiFlightDistanceKm)}｜" +
                $"时间 {Normalize(row.DjiFlightTimeRangeBeijing)}｜磁测时间轴对应 {row.DjiMagMatchedFlightCount}/{row.DjiFlightCount} 次");
        }
        sb.AppendLine();
        sb.AppendLine($"工作进度：{Normalize(row.ProgressSummary)}");
        sb.AppendLine($"后续安排：{Normalize(row.NextActionSummary)}");
        return sb.ToString().TrimEnd();
    }

    public static string BuildProjectProgressPreview(
        IEnumerable<FieldDailyProgressRecord> sourceRows,
        int? expectedDailySorties = null, string timeZoneBasis = FieldMagDataService.BeijingProgressTimeZone)
    {
        List<FieldDailyProgressRecord> rows = ProjectProgressRows(sourceRows, timeZoneBasis);
        if (rows.Count == 0)
            throw new InvalidOperationException("没有可用于项目总进度简报的北京时间进度记录。");

        FieldDailyProgressRecord latest = rows[^1];
        var sb = new StringBuilder();
        sb.AppendLine($"总进度简报｜截至 {Normalize(latest.Date)}（{ProjectTime.ShortLabel(latest.ProgressTimeZone)}）");
        if (latest.SurveyLineMatchingAvailable &&
            latest.PlannedSurveyLineKm > 0d)
        {
            sb.AppendLine(
                $"累计完成测线 {Km(latest.CompletedSurveyLineKm)} / {Km(latest.PlannedSurveyLineKm)}｜" +
                $"测线里程完成率 {Percent(latest.TotalCompletionPercent)}｜剩余 {Km(latest.RemainingSurveyLineKm)}");
            sb.AppendLine(
                $"累计完成架次 {latest.CompletedSorties.Count}/{latest.PlannedSortieCount}｜剩余架次 {latest.RemainingSorties.Count}");
        }
        else
        {
            sb.AppendLine(
                $"设计 {latest.PlannedSortieCount} 架次｜累计完成 {latest.CompletedSorties.Count} 架次｜" +
                $"项目总完成率 {Percent(latest.TotalCompletionPercent)}｜剩余 {latest.RemainingSorties.Count} 架次");
        }
        if (latest.DjiFlightCount > 0)
        {
            sb.AppendLine(
                $"DJI 最近作业日：{latest.DjiFlightCount} 次 / {Km(latest.DjiFlightDistanceKm)}；" +
                $"磁测时间轴对应 {latest.DjiMagMatchedFlightCount}/{latest.DjiFlightCount} 次");
        }
        sb.AppendLine();
        sb.AppendLine("每日工作进度概述");
        foreach (FieldDailyProgressRecord row in rows)
        {
            if (row.SurveyLineMatchingAvailable &&
                row.PlannedSurveyLineKm > 0d)
            {
                sb.AppendLine(
                    $"{row.Date}  当日 {row.TodayCompletedSorties.Count} 架次；" +
                    $"当日完成测线 {Km(row.TodayCompletedSurveyLineKm)}；" +
                    $"当天工作百分比 {WorkPercent(DayWorkPercent(row, expectedDailySorties))}；" +
                    $"累计 {Km(row.CompletedSurveyLineKm)} / {Km(row.PlannedSurveyLineKm)}；" +
                    $"里程完成率 {Percent(row.TotalCompletionPercent)}" +
                    DjiPreviewSuffix(row));
            }
            else
            {
                sb.AppendLine(
                    $"{row.Date}  当日 {row.TodayCompletedSorties.Count} 架次；" +
                    $"当天工作百分比 {WorkPercent(DayWorkPercent(row, expectedDailySorties))}；" +
                    $"累计 {row.CompletedSorties.Count} 架次 / 总完成率 {Percent(row.TotalCompletionPercent)}；" +
                    $"剩余 {row.RemainingSorties.Count} 架次" +
                    DjiPreviewSuffix(row));
            }
        }

        sb.AppendLine();
        sb.AppendLine($"后续安排：{Normalize(latest.NextActionSummary)}");
        return sb.ToString().TrimEnd();
    }

    public static string WriteDailyProgress(
        string destination,
        FieldDailyProgressRecord row,
        int? expectedDailySorties = null)
    {
        var sb = ProjectProgressHeader($"外业工作进度简报 - {row.Date}", row.ProgressTimeZone);
        sb.AppendLine($"- **工区**：{Normalize(row.Workarea)}");
        sb.AppendLine($"- **作业日期（{ProjectTime.ShortLabel(row.ProgressTimeZone)}）**：{Normalize(row.Date)}");
        sb.AppendLine($"- **设计架次**：{row.PlannedSortieCount}");
        if (row.SurveyLineMatchingAvailable &&
            row.PlannedSurveyLineKm > 0d)
        {
            sb.AppendLine($"- **计划测线里程**：{Km(row.PlannedSurveyLineKm)}");
            sb.AppendLine($"- **当日完成测线**：{Km(row.TodayCompletedSurveyLineKm)}");
            sb.AppendLine($"- **累计完成测线**：{Km(row.CompletedSurveyLineKm)}");
            sb.AppendLine($"- **剩余测线里程**：{Km(row.RemainingSurveyLineKm)}");
            sb.AppendLine($"- **测线里程完成率**：{Percent(row.TotalCompletionPercent)}");
            sb.AppendLine($"- **当日完成架次**：{row.TodayCompletedSorties.Count}");
            sb.AppendLine($"- **累计完成架次**：{row.CompletedSorties.Count}");
        }
        else
        {
            sb.AppendLine($"- **当日完成**：{row.TodayCompletedSorties.Count} 架次");
            sb.AppendLine($"- **累计完成**：{row.CompletedSorties.Count} 架次");
            sb.AppendLine($"- **项目总完成率**：{Percent(row.TotalCompletionPercent)}");
            sb.AppendLine($"- **剩余架次**：{row.RemainingSorties.Count}");
        }
        sb.AppendLine(
            $"- **当天工作百分比**：{WorkPercent(DayWorkPercent(row, expectedDailySorties))}");
        if (row.DjiFlightCount > 0)
        {
            sb.AppendLine($"- **DJI 实际飞行**：{row.DjiFlightCount} 次");
            sb.AppendLine($"- **DJI 飞行距离**：{Km(row.DjiFlightDistanceKm)}");
            sb.AppendLine($"- **DJI 飞行时间（{ProjectTime.ShortLabel(row.ProgressTimeZone)}）**：{Normalize(row.DjiFlightTimeRangeBeijing)}");
            sb.AppendLine($"- **磁测时间轴对应 DJI 飞行**：{row.DjiMagMatchedFlightCount}/{row.DjiFlightCount} 次");
        }
        sb.AppendLine();
        sb.AppendLine("## 工作进度概述");
        sb.AppendLine();
        sb.AppendLine($"- {Normalize(row.ProgressSummary)}");
        sb.AppendLine($"- **后续安排**：{Normalize(row.NextActionSummary)}");
        sb.AppendLine();
        sb.AppendLine($"> 本简报面向项目进度汇报。有测线任务身份时，完成度由设计测线与实际无人机测量轨迹唯一匹配计算，作业日按 {ProjectTime.Label(ProjectTime.FromBasis(row.ProgressTimeZone))} 划分；正式质量验收仍以现场记录和 QC 结论为准。");
        return WriteText(destination, sb.ToString());
    }

    public static string WriteProjectProgress(
        string destination,
        IEnumerable<FieldDailyProgressRecord> sourceRows,
        int? expectedDailySorties = null, string timeZoneBasis = FieldMagDataService.BeijingProgressTimeZone)
    {
        List<FieldDailyProgressRecord> rows =
            ProjectProgressRows(sourceRows, timeZoneBasis);
        if (rows.Count == 0)
            throw new InvalidOperationException("没有可用于项目总进度简报的北京时间进度记录。");

        FieldDailyProgressRecord latest = rows[^1];
        var sb = ProjectProgressHeader(
            $"项目外业总进度简报 - 截至 {latest.Date}", latest.ProgressTimeZone);
        sb.AppendLine($"- **工区**：{Normalize(latest.Workarea)}");
        sb.AppendLine($"- **统计截止（{ProjectTime.ShortLabel(latest.ProgressTimeZone)}）**：{Normalize(latest.Date)}");
        sb.AppendLine($"- **设计架次**：{latest.PlannedSortieCount}");
        if (latest.SurveyLineMatchingAvailable &&
            latest.PlannedSurveyLineKm > 0d)
        {
            sb.AppendLine($"- **计划测线里程**：{Km(latest.PlannedSurveyLineKm)}");
            sb.AppendLine($"- **累计完成测线**：{Km(latest.CompletedSurveyLineKm)}");
            sb.AppendLine($"- **剩余测线里程**：{Km(latest.RemainingSurveyLineKm)}");
            sb.AppendLine($"- **测线里程完成率**：{Percent(latest.TotalCompletionPercent)}");
            sb.AppendLine($"- **累计完成架次**：{latest.CompletedSorties.Count}");
            sb.AppendLine($"- **最近作业日完成测线**：{Km(latest.TodayCompletedSurveyLineKm)}");
            sb.AppendLine($"- **最近作业日完成架次**：{latest.TodayCompletedSorties.Count}");
        }
        else
        {
            sb.AppendLine($"- **累计完成**：{latest.CompletedSorties.Count} 架次");
            sb.AppendLine($"- **项目总完成率**：{Percent(latest.TotalCompletionPercent)}");
            sb.AppendLine($"- **剩余架次**：{latest.RemainingSorties.Count}");
            sb.AppendLine($"- **最近作业日完成**：{latest.TodayCompletedSorties.Count} 架次");
        }
        sb.AppendLine(
            $"- **最近作业日工作百分比**：{FormatDayWorkPercent(latest, expectedDailySorties)}");
        sb.AppendLine();
        sb.AppendLine("## 每日工作进度概述");
        sb.AppendLine();
        if (latest.SurveyLineMatchingAvailable &&
            latest.PlannedSurveyLineKm > 0d)
        {
            sb.AppendLine($"| {ProjectTime.ShortLabel(latest.ProgressTimeZone)}日期 | 当日完成测线 | 当天工作百分比 | 累计完成测线 | 测线里程完成率 | 当日完成架次 | 累计完成架次 | 进度概述 |");
            sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---|");
            foreach (FieldDailyProgressRecord row in rows)
            {
                sb.AppendLine(
                    $"| {MarkdownCell(row.Date)} | {Km(row.TodayCompletedSurveyLineKm)} | " +
                    $"{WorkPercent(DayWorkPercent(row, expectedDailySorties))} | {Km(row.CompletedSurveyLineKm)} | " +
                    $"{Percent(row.TotalCompletionPercent)} | {row.TodayCompletedSorties.Count} | " +
                    $"{row.CompletedSorties.Count} | {MarkdownCell(row.ProgressSummary)} |");
            }
        }
        else
        {
            sb.AppendLine($"| {ProjectTime.ShortLabel(latest.ProgressTimeZone)}日期 | 当日完成 | 当天工作百分比 | 累计完成 | 项目总完成率 | 剩余 | 进度概述 |");
            sb.AppendLine("|---|---:|---:|---:|---:|---:|---|");
            foreach (FieldDailyProgressRecord row in rows)
            {
                sb.AppendLine(
                    $"| {MarkdownCell(row.Date)} | {row.TodayCompletedSorties.Count} | " +
                    $"{WorkPercent(DayWorkPercent(row, expectedDailySorties))} | " +
                    $"{row.CompletedSorties.Count} | " +
                    $"{Percent(row.TotalCompletionPercent)} | {row.RemainingSorties.Count} | " +
                    $"{MarkdownCell(row.ProgressSummary)} |");
            }
        }

        List<FieldDailyProgressRecord> djiRows =
            rows.Where(x => x.DjiFlightCount > 0).ToList();
        if (djiRows.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## DJI 飞行记录对账");
            sb.AppendLine();
            sb.AppendLine($"| {ProjectTime.ShortLabel(latest.ProgressTimeZone)}日期 | DJI 飞行次数 | DJI 飞行距离 | 时间范围 | 磁测时间轴对应 |");
            sb.AppendLine("|---|---:|---:|---|---:|");
            foreach (FieldDailyProgressRecord row in djiRows)
            {
                sb.AppendLine(
                    $"| {MarkdownCell(row.Date)} | {row.DjiFlightCount} | {Km(row.DjiFlightDistanceKm)} | " +
                    $"{MarkdownCell(row.DjiFlightTimeRangeBeijing)} | {row.DjiMagMatchedFlightCount}/{row.DjiFlightCount} |");
            }
        }

        sb.AppendLine();
        sb.AppendLine("## 后续安排");
        sb.AppendLine();
        sb.AppendLine(Normalize(latest.NextActionSummary));
        sb.AppendLine();
        sb.AppendLine("> 本简报用于甲方/项目管理汇报，不展开原始数据文件、数据指纹、GEMLink 分段和 QC 明细；技术细节请在 MagOps“数据识别与质量”中查看。");
        return WriteText(destination, sb.ToString());
    }

    public static string WriteIssue(string destination, IssueRecord row)
    {
        return Write(destination, $"现场问题 - {row.IssueId}",
        [
            ("问题", row.Title),
            ("严重程度", DisplayStatus(row.Severity)),
            ("影响", row.Impact),
            ("临时措施", row.TemporaryMeasure),
            ("负责人", row.Owner),
            ("状态", DisplayStatus(row.Status)),
            ("解决证据", string.Join("；", row.ResolutionEvidence)),
            ("备注", row.Notes),
            ("更新时间", row.UpdatedAt)
        ]);
    }

    public static string WriteDailyQc(
        string destination,
        string date,
        string status,
        IEnumerable<(string Status, string Name, string Detail)> checks)
    {
        var sb = Header($"外业当日 QC - {date}");
        sb.AppendLine($"- **日期**：{date}");
        sb.AppendLine($"- **总体状态**：{DisplayStatus(status)}");
        sb.AppendLine();
        sb.AppendLine("## 检查项");
        sb.AppendLine();
        foreach (var check in checks)
            sb.AppendLine($"- **[{DisplayStatus(check.Status)}] {check.Name}**：{check.Detail}");
        return WriteText(destination, sb.ToString());
    }

    private static List<FieldDailyProgressRecord> ProjectProgressRows(
        IEnumerable<FieldDailyProgressRecord> sourceRows, string timeZoneBasis) =>
        sourceRows
            .Where(x =>
                string.Equals(
                    x.ProgressTimeZone,
                    timeZoneBasis,
                    StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.Date, StringComparer.Ordinal)
            .ToList();

    private static StringBuilder ProjectProgressHeader(string title, string timeZoneBasis)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {title}");
        sb.AppendLine();
        DateTimeOffset projectNow = ProjectTime.Display(DateTimeOffset.UtcNow, ProjectTime.FromBasis(timeZoneBasis));
        sb.AppendLine($"> 生成时间（{ProjectTime.ShortLabel(timeZoneBasis)}）：{projectNow:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine();
        return sb;
    }

    private static double DayWorkPercent(
        FieldDailyProgressRecord row,
        int? expectedDailySorties)
    {
        int targetSorties =
            Math.Max(
                1,
                expectedDailySorties ??
                row.ExpectedDailySorties);

        if (row.SurveyLineMatchingAvailable &&
            row.PlannedSurveyLineKm > 0d &&
            row.PlannedSortieCount > 0)
        {
            // Leadership workload is measured in survey-line kilometres when
            // task identity is available. Until a dedicated daily-km target is
            // configured, derive it from the project's mean planned km/sortie
            // multiplied by the current expected daily sortie target.
            double meanKmPerSortie =
                row.PlannedSurveyLineKm /
                row.PlannedSortieCount;
            double dailyTargetKm =
                meanKmPerSortie *
                targetSorties;
            if (dailyTargetKm <= 0d)
                return 0d;

            return Math.Max(
                0d,
                row.TodayCompletedSurveyLineKm /
                dailyTargetKm *
                100d);
        }

        return Math.Max(
            0d,
            row.TodayCompletedSorties.Count /
            (double)targetSorties *
            100d);
    }

    private static string DjiPreviewSuffix(
        FieldDailyProgressRecord row) =>
        row.DjiFlightCount <= 0
            ? ""
            : $"；DJI {row.DjiFlightCount} 次 / {Km(row.DjiFlightDistanceKm)} / 磁测对应 {row.DjiMagMatchedFlightCount}";

    private static string Percent(double value) =>
        $"{Math.Clamp(value, 0d, 100d):0.#}%";

    private static string WorkPercent(double value) =>
        $"{Math.Max(0d, value):0.#}%";

    private static string Km(double value) =>
        $"{Math.Max(0d, value):0.00} km";

    private static string MarkdownCell(string? value) =>
        (value ?? "")
            .Replace("|", "\\|")
            .Replace("\r", " ")
            .Replace("\n", " ");

    private static string Write(
        string destination,
        string title,
        IEnumerable<(string Label, string Value)> fields)
    {
        var sb = Header(title);
        foreach (var (label, value) in fields)
            sb.AppendLine($"- **{label}**：{Normalize(value)}");
        return WriteText(destination, sb.ToString());
    }

    private static StringBuilder Header(string title)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {title}");
        sb.AppendLine();
        sb.AppendLine($"> 生成时间：{DateTimeOffset.Now:O}");
        sb.AppendLine();
        return sb;
    }

    private static string WriteText(string destination, string text)
    {
        string full = Path.GetFullPath(destination);
        return AtomicFileWriter.Write(full, stream =>
        {
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
            writer.Write(text.TrimEnd());
            writer.Write(Environment.NewLine);
            writer.Flush();
        });
    }

    private static string DisplayStatus(string? value)
    {
        string text = (value ?? "").Trim();
        return text.ToUpperInvariant() switch
        {
            "PENDING" => "待确认",
            "PASSED" => "通过",
            "FAILED" => "失败",
            "REVIEW_REQUIRED" => "需复核",
            "BLOCKED" => "阻断",
            "DEFERRED" => "暂缓",
            "PLANNED" => "计划中",
            "COMPLETED" => "已完成",
            "OPEN" => "未解决",
            "RESOLVED" => "已解决",
            "LOW" => "低",
            "MEDIUM" => "中",
            "HIGH" => "高",
            "CRITICAL" => "严重",
            "DRAFT" => "草稿",
            "READY" => "可交接",
            "HANDED_OFF" => "已交接",
            "RECORDED" => "已记录",
            _ => text
        };
    }

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "—" : value.Replace("\r", " ").Replace("\n", " ").Trim();
}
