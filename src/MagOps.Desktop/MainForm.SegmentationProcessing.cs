using MagOps.Core.Domain;
using MagOps.Core.Diurnal;
using MagOps.Infrastructure.Services;
using MagOps.Infrastructure.Storage;

namespace MagOps.Desktop;

public sealed partial class MainForm
{
    private readonly Panel _processingReviewHost = new() { Dock = DockStyle.Fill, AutoScroll = true };
    private readonly Label _processingContextStatus = new() { Dock = DockStyle.Fill, AutoEllipsis = true, Height = 30 };
    private AutomaticSegmentationReviewDialog? _processingReview;
    private string _processingReviewContext = "";
    private bool _preserveProcessingReview;
    private IntermediateCsvSnapshot? _processingCsvInput;
    private readonly List<Control> _rawProcessingActions = [];
    private Button? _processingCsvExport;
    private Button? _processingLoadRaw;
    private readonly TableLayoutPanel _processingIdleActions = new() { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, RowCount = 2 };
    private FlowLayoutPanel? _processingSourceActions;
    private FlowLayoutPanel? _processingOutputActions;

    private void BuildSegmentationProcessingTab()
    {
        _processingReviewHost.SizeChanged += (_, _) =>
        {
            if (_processingReview is { IsDisposed: false } review)
                review.Height = Math.Max(650, _processingReviewHost.ClientSize.Height);
        };
        var page = new TabPage("分线与处理");
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4,
            Padding = UiTheme.PagePadding };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(UiComponents.PageHeader("分线与处理",
            "选择数据来源 → 航段核验与母线整理 → 导出；中间 CSV 仅进行空间筛选。"), 0, 0);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        _processingSourceActions = actions;
        _processingLoadRaw = Button("载入原始数据核验", (_, _) => RunUi("载入分线核验", BeginProcessingReview), UiButtonRole.Primary);
        actions.Controls.Add(_processingLoadRaw);
        var demHeight = Button("DEM 离地高度", (_, _) => RunUi("离地高度", ShowProcessingDemHeight));
        actions.Controls.Add(demHeight); _rawProcessingActions.Add(demHeight);
        actions.Controls.Add(Button("从中间 CSV 分线…", (_, _) => RunUi("中间 CSV 空间分线", BeginIntermediateCsvReview)));
        var scan = Button("扫描 / 核对数据", (_, _) => RunUiBusy("核对数据",
            "正在核对当前工区数据，更新后重新载入分线核验…", () =>
            {
                AnalyzeSelectedFieldDataAndProgress();
            }));
        actions.Controls.Add(scan); _rawProcessingActions.Add(scan);
        actions.Controls.Add(Button("外业数据与日变", (_, _) => NavigateFeature(FeatureIds.FieldOperations), UiButtonRole.Subtle));
        var quality = Button("查看日变质量", (_, _) => ShowDiurnalQuality(), UiButtonRole.Subtle);
        actions.Controls.Add(quality); _rawProcessingActions.Add(quality);
        var outputActions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        _processingOutputActions = outputActions;
        var report = Button("准备报告正式测线数据", (_, _) => RunUi("准备报告正式测线数据", PrepareReportFormalSource));
        outputActions.Controls.Add(report); _rawProcessingActions.Add(report);
        _fieldProcessingT0Mode.DropDownStyle = ComboBoxStyle.DropDownList;
        _fieldProcessingT0Mode.Width = 154;
        _fieldProcessingT0Mode.Items.AddRange(new object[]
        {
            "全项目自动 T0",
            "按 UTC 日自动 T0（旧成果复现）",
            "手工固定 T0"
        });
        _fieldProcessingT0Mode.SelectedIndex = 0;
        _fieldProcessingT0Mode.SelectedIndexChanged += (_, _) =>
            _fieldProcessingT0Nt.Enabled = _processingCsvInput is null && _fieldProcessingT0Mode.SelectedIndex == 2;
        var t0ModeLabel = FieldToolbarLabel("T0 模式");
        outputActions.Controls.Add(t0ModeLabel); _rawProcessingActions.Add(t0ModeLabel);
        outputActions.Controls.Add(_fieldProcessingT0Mode);

        _fieldProcessingT0Nt.Minimum = -1000000m;
        _fieldProcessingT0Nt.Maximum = 1000000m;
        _fieldProcessingT0Nt.DecimalPlaces = 3;
        _fieldProcessingT0Nt.Increment = 1m;
        _fieldProcessingT0Nt.Value = 0m;
        _fieldProcessingT0Nt.Width = 96;
        _fieldProcessingT0Nt.Enabled = false;
        var t0ValueLabel = FieldToolbarLabel("固定 T0 nT");
        outputActions.Controls.Add(t0ValueLabel); _rawProcessingActions.Add(t0ValueLabel);
        outputActions.Controls.Add(_fieldProcessingT0Nt);
        _rawProcessingActions.AddRange([_fieldProcessingT0Mode, _fieldProcessingT0Nt]);

        _fieldSurveyLineSegmentation.AutoSize = true;
        _fieldSurveyLineSegmentation.Text = "自动分线核验";
        _fieldSurveyLineSegmentation.Checked = true;
        _fieldSurveyLineSegmentation.Margin = new Padding(10, 8, 4, 0);
        _toolTip.SetToolTip(
            _fieldSurveyLineSegmentation,
            "生成飞行窗口与整段测线匹配建议，并在图形界面中确认归属和 QC。未确认项会提醒但不阻止继续；未确认轨迹默认不进入生产选择，可在核验界面明确选择纳入待复核草稿。 ");
        _fieldSurveyLineSegmentation.Visible = false;

        var replacement = Button(
            "重测段替换…",
            (_, _) => RunUi(
                "配置重测段替换",
                ConfigureMagneticRepeatReplacement),
            UiButtonRole.Subtle);
        outputActions.Controls.Add(replacement); _rawProcessingActions.Add(replacement);
        var export = Button("生成处理用 CSV（IGRF + dTgz）", (_, _) => RunUiBusy(
            "生成处理用 CSV", "正在按右侧航段选择核对日变、IGRF、T0、dTgz 和投影坐标…",
            ExportNativeDiurnalFieldData), UiButtonRole.Primary);
        outputActions.Controls.Add(export); _rawProcessingActions.Add(export);
        _processingCsvExport = Button("导出空间分线 CSV", (_, _) => RunUi("导出空间分线 CSV", () => ExportCurrentIntermediateCsv(true)), UiButtonRole.Primary);
        outputActions.Controls.Add(_processingCsvExport);
        _processingIdleActions.ColumnStyles.Add(new(SizeType.Percent, 100));
        _processingIdleActions.RowStyles.Add(new(SizeType.AutoSize));
        _processingIdleActions.RowStyles.Add(new(SizeType.AutoSize));
        _processingIdleActions.Controls.Add(actions, 0, 0);
        _processingIdleActions.Controls.Add(outputActions, 0, 1);
        root.Controls.Add(_processingIdleActions, 0, 1);
        root.Controls.Add(_processingContextStatus, 0, 2);
        root.Controls.Add(_processingReviewHost, 0, 3);
        page.Controls.Add(root);
        _dirtyStateCoordinator.Register(new DirtyFeatureRegistration(
            FeatureIds.SegmentationProcessing, "分线与处理",
            () => _processingReview?.HasUnsavedWorkspaceReview == true,
            () => "当前航段选择、识别参数或飞行窗口尚未保存",
            () => _processingReview?.SaveWorkspaceReview() ?? true, ResetProcessingReview));
        ResetProcessingReview();
        RegisterFeaturePage(FeatureIds.SegmentationProcessing, page,
            activated: QueueActivation,
            contextChanged: _ =>
            {
                ResetProcessingReview();
                QueueActivation();
            });

        void QueueActivation() => QueueFeatureActivation(FeatureIds.SegmentationProcessing, () =>
        {
            if (_processingCsvInput is null) AutoAnalyzeFieldOperationsOnActivation();
            UpdateProcessingContextStatus();
        });
    }

    private string ProcessingContextKey() => string.Join("|", Root, CurrentWorkareaRelativePath,
        (_fieldMagRawAnalysis ?? _fieldMagDataAnalysis)?.Fingerprint ?? "",
        ResolveFieldOperationsRouteSnapshot()?.Fingerprint ?? "",
        _workspace.GetWorkspace(Root).CentralMeridian);
    private bool ProcessingReviewIsCurrent() => _processingCsvInput is null && _processingReview is not null &&
        !_processingReview.IsDisposed && _processingReviewContext == ProcessingContextKey();

    private void UpdateProcessingContextStatus()
    {
        bool spatial = _processingCsvInput is not null;
        if (_processingLoadRaw is not null) UiComponents.SetButtonRole(_processingLoadRaw, spatial ? UiButtonRole.Neutral : UiButtonRole.Primary);
        foreach (var action in _rawProcessingActions)
        { action.Enabled = !spatial; action.Visible = !spatial; }
        if (_processingCsvExport is not null)
        { _processingCsvExport.Visible = spatial; _processingCsvExport.Enabled = spatial; }
        _fieldProcessingT0Nt.Enabled = !spatial && _fieldProcessingT0Mode.SelectedIndex == 2;
        _processingContextStatus.Text = _processingCsvInput is { } input
            ? $"工区：{CurrentWorkareaRelativePath}；中间 CSV：{Path.GetFileName(input.Path)}；有效点 {input.Analysis.UavSamples.Count:N0}。仅按空间与时间关系筛选，其他字段原样保留。"
            : $"工区：{CurrentWorkareaRelativePath}；" +
                (_fieldMagDataAnalysis is { } data ? $"UAV {data.UavSamples.Count:N0} 点 / Base {data.BaseSamples.Count:N0} 点。" : "尚未扫描数据。") +
                " 日变使用当前数据重新核对；仅右侧纳入航段参与处理。";
        string step = _processingReview?.WorkspaceStage ?? "选择数据来源";
        string state = _processingReview?.WorkspaceState ?? "尚未载入核验";
        _processingContextStatus.Text = $"步骤：{step}；{state}。" + _processingContextStatus.Text;
        _toolTip.SetToolTip(_processingContextStatus, _processingCsvInput is { } csv
            ? csv.Path + Environment.NewLine + _processingContextStatus.Text : _processingContextStatus.Text);
    }

    private void ResetProcessingReview()
    {
        // Shared action controls belong to the main workspace, never to a disposable review session.
        if (_processingSourceActions is not null) _processingIdleActions.Controls.Add(_processingSourceActions, 0, 0);
        if (_processingOutputActions is not null) _processingIdleActions.Controls.Add(_processingOutputActions, 0, 1);
        if (_processingReview is not null)
        {
            _processingReview.WorkspaceStateChanged -= UpdateProcessingContextStatus;
            _processingReview.Dispose();
        }
        _processingReview = null;
        _processingReviewContext = "";
        _processingCsvInput = null;
        _processingReviewHost.Controls.Clear();
        _processingReviewHost.Controls.Add(new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
            Text = "点击“载入原始数据核验”或“从中间 CSV 分线…”打开航段。\r\n核验结果随项目、工区与数据批次变化重新加载。" });
        UpdateProcessingContextStatus();
    }

    private void BeginProcessingReview() => BeginProcessingReviewCore(false);

    private void BeginProcessingReviewCore(bool demFirst)
    {
        EnsureInitialized();
        if (ProcessingReviewIsCurrent()) { _processingReview!.ActivateReviewWorkspace(); return; }
        if (_processingReview?.HasUnsavedWorkspaceReview == true &&
            !ConfirmContextSwitch("更新分线核验")) return;
        if (_fieldMagDataAnalysis is null)
        {
            EnsureDefaultFieldDataFolder();
            AnalyzeFieldDataFolder(force: true, quietMissing: false);
        }
        var analysis = _fieldMagRawAnalysis ?? _fieldMagDataAnalysis ??
            throw new InvalidOperationException("请先扫描当前工区数据。");
        if (ProcessingReviewIsCurrent()) { _processingReview!.ActivateReviewWorkspace(); return; }
        string output = EnsureManagedOutputDirectory("processing_diurnal", "分线与处理");
        var route = ResolveFieldOperationsRouteSnapshot();
        var input = ResolveWorkareaScreeningInput(analysis);
        var request = new AuditProductionRequest(analysis.Fingerprint, [], [], [], true)
            { RouteFingerprint = route?.Fingerprint ?? "", SpatialScreening = input,
                ScreeningFingerprint = new WorkareaSpatialService().Screen(analysis, input).Fingerprint };
        string defaults = SegmentationParameterStore.DefaultsPath(Root, CurrentWorkareaRelativePath);
        AuditProductionRequest? snapshot = null;
        string? snapshotPath = null;
        try
        {
            request = request with { TrackSegmentation = SegmentationParameterStore.LoadDefaults(defaults) };
            snapshotPath = SegmentationReviewStore.LatestPath(output);
            snapshot = snapshotPath is null ? null : SegmentationReviewStore.LoadFile(snapshotPath);
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidDataException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, "原保存文件保留，本次重新核验：" + ex.Message, "核验记录", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        var projection = ResolveFieldProductionCentralMeridian(
            _workspace.InvalidateLegacyCoordinateDerivedCentralMeridian(Root), CurrentWorkareaRelativePath);
        var review = new AutomaticSegmentationReviewDialog(analysis, route?.SurveyLines ?? [], request,
            route?.Fingerprint ?? "", _projectTimeZone, projection.Value, deferInitialAnalysis: true)
            { EmbeddedWorkspace = true, ReviewOutputDirectory = output, ParameterDefaultsPath = defaults,
                SkipInitialRecognition = demFirst };
        review.ProcessingRequested += () => RunUiBusy("生成处理用 CSV",
            "正在使用当前右侧航段核对日变、IGRF 和 dTgz…", ExportNativeDiurnalFieldData);
        AttachProcessingReview(review, ProcessingContextKey());
        ConfigureProcessingDemHeight(review, output);
        if (snapshot is not null) review.OfferSavedSnapshot(snapshot, snapshotPath!);
    }

    private void AttachProcessingReview(AutomaticSegmentationReviewDialog review, string context,
        IntermediateCsvSnapshot? csvInput = null)
    {
        review.TopLevel = false;
        review.FormBorderStyle = FormBorderStyle.None;
        review.WindowState = FormWindowState.Normal;
        review.MinimumSize = Size.Empty;
        review.Dock = DockStyle.Top;
        review.Height = Math.Max(650, _processingReviewHost.ClientSize.Height);
        foreach (var button in DescendantButtons(review))
            if (button.DialogResult != DialogResult.None)
            { button.DialogResult = DialogResult.None; if (button.Text == "取消") button.Visible = false; }
        ResetProcessingReview();
        // Reset disposes the old session only after the replacement is constructed.
        foreach (Control empty in _processingReviewHost.Controls.Cast<Control>().ToArray()) empty.Dispose();
        _processingReviewHost.Controls.Clear();
        _processingReviewHost.Controls.Add(review);
        review.SetAssessmentOutputDirectory(Path.Combine(_folderLayout.ResolveWorkareaFolder(Root, CurrentWorkareaRelativePath, "workarea.processing"), "质量"));
        _processingReview = review;
        _processingReviewContext = context;
        _processingCsvInput = csvInput;
        review.WorkspaceStateChanged += UpdateProcessingContextStatus;
        review.ConfigureProcessingStages(_processingSourceActions!, _processingOutputActions!);
        review.Show();
        UpdateProcessingContextStatus();
    }

    private static IEnumerable<Button> DescendantButtons(Control control)
    {
        foreach (Control child in control.Controls)
        {
            if (child is Button button) yield return button;
            foreach (var nested in DescendantButtons(child)) yield return nested;
        }
    }

    private void ExportDiurnalOnlyFieldData()
    {
        EnsureInitialized();
        if (_fieldMagDataAnalysis is null)
        {
            EnsureDefaultFieldDataFolder();
            AnalyzeFieldDataFolder(force: true, quietMissing: false);
        }
        if (_fieldDiurnalDatumNt.Value != 0 && !_fieldDiurnalDatumConfirmed.Checked)
            throw new InvalidOperationException("请确认非零 Datum，或恢复为 0。");
        var analysis = _fieldMagRawAnalysis ?? _fieldMagDataAnalysis ??
            throw new InvalidOperationException("请先扫描当前工区数据。");
        string output = EnsureManagedOutputDirectory("processing_diurnal", "生成完整日变");
        InvalidateDiurnalQuality();
        var exported = _diurnalCorrectionExport.WriteDiurnalOnlyWithQuality(output, analysis,
            new DiurnalCorrectionOptions(DatumNt: (double)_fieldDiurnalDatumNt.Value,
                GemLinkTenthsCompatibility: true, MaxInterpolationGapSeconds: 999,
                RejectInterpolationAcrossLargeGap: true),
            new GeneratedArtifactContext(_workspace.GetWorkspace(Root).ProjectId,
                ResolveManagedWorkareaId(CurrentWorkareaRelativePath)), Root);
        string path = exported.Path;
        ApplyDiurnalQuality(analysis,exported.Correction,"完整日变输入");
        RegisterGeneratedBusinessFiles(Directory.GetFiles(Path.GetDirectoryName(path)!)
            .Where(p => !p.EndsWith(GeneratedArtifactIdentity.Suffix)), "processing_diurnal");
        SetStatus("完整日变已生成：" + path);
        TransientNotice.Show(this, path, "日变生成完成");
    }
}

