using System.Diagnostics;
using MagOps.Reporting;
using MagOps.Infrastructure.Services;

namespace MagOps.Desktop;

public sealed partial class MainForm
{
    private ReportWorkspaceControl? _reportWorkspace;
    private string _reportContext = "";
    private bool _reportLoadFailed;

    private void PrepareReportFormalSource()
    {
        if (!ProcessingReviewIsCurrent()) throw new InvalidOperationException("请先载入当前工区分线核验，确认右侧正式测线选择。");
        var selection = _processingReview!.CaptureReportSelection();
        string directory = EnsureManagedOutputDirectory("processing_diurnal", "报告正式测线");
        var context = new GeneratedArtifactContext(_workspace.GetWorkspace(Root).ProjectId, CurrentWorkareaId);
        string csv = BusyOperationDialog.Run(this, "准备报告正式测线数据", "正在按核验选择导出正式测线原始磁通道与逐点追溯；无需日变或调平…",
            () => ReportFormalSourceWriter.Write(directory, selection.Audit, selection.Samples, selection.Tasks, context, selection.SelectionFile));
        RegisterGeneratedBusinessFile(csv, "report_formal_source");
        LoadReportContext();
        if (_reportLoadFailed || _reportWorkspace is null) throw new InvalidOperationException("正式测线数据已生成，但原报告资料读取失败，请修复后手动选择：" + csv);
        var inputs = _reportWorkspace.ReadInputs() with { ProductionCsv = csv, AutoProductionCsv = "" };
        _reportWorkspace.LoadInputs(inputs, "已选用当前核验的正式测线原始磁数据：" + csv, changed: true);
        NavigateFeature(FeatureIds.ReportCompilation);
        SetStatus("报告正式测线数据已准备；请引用并确认母线设计和KML高度依据，然后检查与统计。");
    }

    private void BuildReportTab()
    {
        _reportWorkspace = new ReportWorkspaceControl(
            () => RunUi("保存报告资料", () => SaveReportInputs()),
            () => RunUi("检查报告资料", () => BuildReport(false)),
            () => RunUi("生成报告草稿", () => BuildReport(true)),
            () => RunUi("另存报告模板", ExportReportTemplate),
            () => RunUi("打开报告目录", OpenReportOutputDirectory),
            () => RunUi("重新识别已有成果", () => DiscoverReportSources(true)),
            () => RunUi("引用当前设计身份", ImportReportDesign),
            () => NavigateFeature(FeatureIds.QualityChecks));
        _reportWorkspace.Changed += (_, _) => UpdateDirtyStateIndicator();
        _dirtyStateCoordinator.Register(new DirtyFeatureRegistration(FeatureIds.ReportCompilation, "报告编写",
            () => _reportWorkspace.HasChanges, () => "报告资料或人工章节尚未保存", SaveReportInputs, () => LoadReportContext(true)));
        RegisterFeatureControl(FeatureIds.ReportCompilation, _reportWorkspace, activated: () => LoadReportContext(),
            contextChanged: _ => LoadReportContext(true));
    }

    private void OpenReportOutputDirectory()
    {
        string directory = ReportInputStore.DirectoryFor(Root, CurrentWorkareaRelativePath);
        if (!Directory.Exists(directory))
        {
            SetStatus("当前工区尚无报告目录；保存报告资料或生成 Word 草稿后可从此处打开。");
            return;
        }
        Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
    }

    private void ImportReportDesign()
    {
        var route = ResolveFieldOperationsRouteSnapshot() ?? throw new InvalidOperationException("当前工区没有可引用的设计，请先载入航线或在母测线资料中人工映射。");
        var sourceFiles = route.Sorties.Select(s => s.SourceFile).Append(route.SourcePath).Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var sources = sourceFiles.Select(p => new ReportSource("survey-design", Path.GetFullPath(p), ReportSnapshotBuilder.Hash(p))).ToArray();
        _reportWorkspace?.AddDesignTasks(route.SurveyLines.Where(t => !string.IsNullOrWhiteSpace(t.SurveyTaskStableId) && t.GeometryOrigin != "ACTUAL_TRACK").Select(t => new ReportSurveyTask
        {
            TaskId = t.SurveyTaskStableId, MotherId = !string.IsNullOrWhiteSpace(t.MotherLineStableId) ? t.MotherLineStableId : t.MotherLineTaskId,
            MotherName = t.LineName, Role = t.IsQualityRepeat ? "CHECK" : "SURVEY",
            DesignStartLon = t.Start.Longitude, DesignStartLat = t.Start.Latitude, DesignEndLon = t.End.Longitude, DesignEndLat = t.End.Latitude,
            Evidence = route.SourceLabel + "；设计指纹 " + route.Fingerprint + "；待人工核对母线身份和用途", DesignSources = sources
        }));
        SetStatus("已引用当前设计稳定身份与几何作为候选，请核对并确认；实际飞高仍需对应KML及设计AGL依据。");
    }

    private void LoadReportContext(bool force = false)
    {
        if (_reportWorkspace is null) return;
        string key = Root + "|" + CurrentWorkareaId + "|" + CurrentWorkareaRelativePath;
        if (!force && key == _reportContext) { DiscoverReportSources(false); return; }
        _reportContext = key;
        _reportLoadFailed = false;
        if (string.IsNullOrWhiteSpace(Root) || string.IsNullOrWhiteSpace(CurrentWorkareaRelativePath))
        { _reportWorkspace.LoadInputs(new(), "请先选择项目和工区。"); return; }
        try
        {
            var inputs = ReportInputStore.Load(Root, CurrentWorkareaRelativePath);
            var discovered = ReportSourceDiscovery.Resolve(Root, CurrentWorkareaRelativePath, CurrentWorkareaId, inputs);
            _reportWorkspace.LoadInputs(discovered.Inputs, "当前工区：" + CurrentWorkareaRelativePath + Environment.NewLine + discovered.Explanation);
            RefreshReportFreshness(discovered.Inputs);
        }
        catch (Exception ex)
        { _reportLoadFailed = true; _reportWorkspace.LoadInputs(new(), "无法读取报告资料：" + ex.Message); }
    }

    private void DiscoverReportSources(bool showStatus)
    {
        if (_reportWorkspace is null || _reportLoadFailed || string.IsNullOrWhiteSpace(Root) || string.IsNullOrWhiteSpace(CurrentWorkareaRelativePath)) return;
        var current = _reportWorkspace.ReadInputs();
        var discovered = ReportSourceDiscovery.Resolve(Root, CurrentWorkareaRelativePath, CurrentWorkareaId, current);
        bool changed = _reportWorkspace.HasChanges || current != discovered.Inputs;
        _reportWorkspace.LoadInputs(discovered.Inputs, "当前工区：" + CurrentWorkareaRelativePath + Environment.NewLine + discovered.Explanation, changed);
        RefreshReportFreshness(discovered.Inputs);
        if (showStatus) SetStatus("已重新识别报告资料、处理成果和飞行评估来源；请检查后保存资料。");
    }

    private void RefreshReportFreshness(ReportInputs inputs)
    {
        string root = Root, area = CurrentWorkareaRelativePath;
        string message = BusyOperationDialog.Run(this, "核对已有报告", "正在核对最近报告的选用来源；旧报告保持不变…",
            () => ReportFreshness.CheckLatest(root, area, inputs));
        _reportWorkspace?.ShowFreshness(message);
    }

    private bool SaveReportInputs()
    {
        if (_reportWorkspace is null) return false;
        if (_reportLoadFailed) throw new InvalidOperationException("原报告资料读取失败，已禁止覆盖；请先检查report-inputs.json并重新打开工区。");
        ReportInputStore.Save(Root, CurrentWorkareaRelativePath, _reportWorkspace.ReadInputs());
        RememberLatestGeneratedBusinessFile(Path.Combine(ReportInputStore.DirectoryFor(Root, CurrentWorkareaRelativePath), "report-inputs.json"));
        _reportWorkspace.MarkSaved(); SetStatus("报告资料与人工章节已保存。");
        RefreshReportFreshness(_reportWorkspace.ReadInputs());
        return true;
    }

    private void BuildReport(bool generate)
    {
        if (_reportWorkspace is null) return;
        string root = Root, area = CurrentWorkareaRelativePath, areaId = CurrentWorkareaId;
        ReportInputs inputs;
        ReportSnapshot snapshot;
        _reportWorkspace.ShowChecks("正在检查当前报告资料与统计来源…");
        try
        {
            inputs = _reportWorkspace.ReadInputs();
            RefreshReportFreshness(inputs);
            snapshot = BusyOperationDialog.Run(this, "检查报告资料", "正在逐行统计最终 CSV，并核对所选评估批次的完整性…",
                () => new ReportSnapshotBuilder().Build(root, area, areaId, inputs));
        }
        catch (Exception ex)
        {
            _reportWorkspace.ShowChecks("本次报告检查未完成：" + ex.Message + Environment.NewLine + "请修正资料或来源后重新执行检查与统计。");
            throw;
        }
        _reportWorkspace.ShowChecks(string.Join(Environment.NewLine,
            snapshot.Facts.Select(f => $"{f.Id}：{f.Value} {f.Unit} [{f.Status}] — {f.Method}")) +
            Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, snapshot.Warnings));
        if (!generate) { SetStatus("报告数据检查完成；缺项见报告编写的检查结果。"); return; }
        SaveReportInputs();
        var result = BusyOperationDialog.Run(this, "生成 Word 草稿", "正在填充模板、生成轨迹图并检查文档结构…",
            () => new ReportCompiler().Build(root, area, snapshot, ReportTrackFigure.Render(snapshot.Track), ReportQualityFigures.Render(snapshot)));
        RememberLatestGeneratedBusinessFile(result.DocumentPath);
        string registration = "";
        try { RegisterGeneratedBusinessFile(result.DocumentPath, "generated_report"); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        { registration = Environment.NewLine + "草稿已生成，但成果登记未完成，可通过资料与归档重新扫描：" + ex.Message; }
        _reportWorkspace.ShowChecks("草稿已保存：" + result.DocumentPath + Environment.NewLine +
            "请用 Word 检查中文字体、分页、表格和图件。当前为草稿，尚未通过正式报告验收。" + Environment.NewLine +
            string.Join(Environment.NewLine, snapshot.Warnings) + registration);
        SetStatus("Word 草稿已生成：" + result.DocumentPath);
        RefreshReportFreshness(inputs);
        Process.Start(new ProcessStartInfo(result.Directory) { UseShellExecute = true });
    }

    private void ExportReportTemplate()
    {
        using var dialog = new SaveFileDialog { Filter = "Word模板|*.docx", FileName = "MagOps_小结模板.docx", OverwritePrompt = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        string temporary = dialog.FileName + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { ReportTemplate.Create(temporary); File.Move(temporary, dialog.FileName, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        RememberLatestGeneratedBusinessFile(dialog.FileName);
    }
}
