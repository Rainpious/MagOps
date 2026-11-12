using System.Drawing.Drawing2D;
using System.Globalization;
using System.Text.Json;
using MagOps.Core.Domain;
using MagOps.Infrastructure.Services;
using MagOps.Application;

namespace MagOps.Desktop;

/// <summary>Integrated Mission setup, graphical execution review, and explicit pass/QC choices.</summary>
internal sealed partial class AutomaticSegmentationReviewDialog : Form
{
    private sealed class WindowDraft(string id, DateTimeOffset startUtc, DateTimeOffset endUtc)
    {
        public string Id { get; } = id;
        public DateTimeOffset StartUtc { get; set; } = startUtc;
        public DateTimeOffset EndUtc { get; set; } = endUtc;
        public string TaskScopeSource { get; set; } = "AUTO";
        public IReadOnlyList<string> SourceFiles { get; set; } = [];
        public string PhysicalFlightId { get; set; } = "";
        public HashSet<string> TaskIds { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record TaskChoice(PlannedSurveyLineTask Task)
    {
        public override string ToString() => Task.LineName;
    }

    private sealed record ReviewLegRow(FlightLeg Leg, SurveyLinePass? Pass,
        IReadOnlyDictionary<string, string> TaskIdByOption)
    {
        // An inferred axis has no authoritative mission direction. Keep the
        // measured FORWARD/REVERSE value for diagnostics, not as a work veto.
        public bool AxisOnlyDirection { get; init; }
    }

    private readonly FieldMagFolderAnalysis _analysis;
    private IReadOnlyList<PlannedSurveyLineTask> _tasks;
    private readonly IReadOnlyList<PlannedSurveyLineTask> _designTasks;
    private readonly double? _referenceCentralMeridian;
    private bool TrackMode => _seedRequest.TrackSegmentation?.Enabled == true;
    private readonly string _routeFingerprint;
    private readonly TimeZoneInfo _timeZone;
    private FieldMagFolderAnalysis _displayAnalysis;
    private AuditProductionRequest _seedRequest;
    private bool _priorReviewStale;
    private readonly List<WindowDraft> _windows = [];
    private readonly DataGridView _windowGrid = GridFactory.Create();
    private readonly DataGridView _taskList = GridFactory.Create();
    private readonly DataGridView _legGrid = GridFactory.Create();
    private readonly TrackMapPanel _map = new();
    private readonly TrackTimelinePanel _timeline = new();
    private readonly Label _status = new();
    private readonly Label _productionStatus = new();
    private readonly Label _taskSelectionLabel = new();
    private readonly CheckBox _includeUnresolved = new();
    private AuditProductionResult? _audit;
    private readonly AuditProductionService _auditService = new();
    private readonly ReviewAnalysisSession _reviewAnalysisSession;
    private string? _lastAssessmentSaveKey;
    private Task<FlightQualityAssessmentSaved>? _lastAssessmentSave;
    private int _assessmentStatusRevision;
    private int _recognitionBatch;
    private readonly FlightQualityAssessmentService _assessmentService = new();
    private bool _loadingTaskChecks;
    private bool _populatingLegGrid;
    private bool _analysisInputChanged;
    private bool _refreshingWindowGrid;
    private readonly CheckBox _automaticAssessment = new() { Text = "自动保存飞行评估", AutoSize = true, Checked = true };
    private Task _assessmentQueue = Task.CompletedTask;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string? ReviewOutputDirectory { get; set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string? ParameterDefaultsPath { get; set; }
    private bool _suppressAutomaticAssessment;
    private readonly bool _deferInitialAnalysis;
    private readonly bool _spatialOnly;
    // The first recognition pass can seed clearly matched, forward work legs
    // into the right-hand processing list. Once the user moves a leg back,
    // that explicit decision is retained for the remainder of this dialog.
    private bool _defaultMatchedSelectionsApplied;
    private bool _applyingDefaultMatchedSelections;
    private string _flightQualityOutputRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MagOps", "flight-quality-assessments");

    public AutomaticSegmentationReviewDialog(FieldMagFolderAnalysis analysis,
        IReadOnlyList<PlannedSurveyLineTask> tasks, AuditProductionRequest seedRequest,
        string routeFingerprint, TimeZoneInfo? timeZone = null)
        : this(analysis, tasks, seedRequest, routeFingerprint, timeZone, null) { }

    public AutomaticSegmentationReviewDialog(FieldMagFolderAnalysis analysis,
        IReadOnlyList<PlannedSurveyLineTask> tasks, AuditProductionRequest seedRequest,
        string routeFingerprint, TimeZoneInfo? timeZone, double? referenceCentralMeridian)
        : this(analysis, tasks, seedRequest, routeFingerprint, timeZone, referenceCentralMeridian, false) { }

    public AutomaticSegmentationReviewDialog(FieldMagFolderAnalysis analysis,
        IReadOnlyList<PlannedSurveyLineTask> tasks, AuditProductionRequest seedRequest,
        string routeFingerprint, TimeZoneInfo? timeZone, double? referenceCentralMeridian,
        bool deferInitialAnalysis)
        : this(analysis, tasks, seedRequest, routeFingerprint, timeZone, referenceCentralMeridian, deferInitialAnalysis, false) { }

    public AutomaticSegmentationReviewDialog(FieldMagFolderAnalysis analysis,
        IReadOnlyList<PlannedSurveyLineTask> tasks, AuditProductionRequest seedRequest,
        string routeFingerprint, TimeZoneInfo? timeZone, double? referenceCentralMeridian,
        bool deferInitialAnalysis, bool spatialOnly)
    {
        _spatialOnly = spatialOnly;
        if (spatialOnly) _automaticAssessment.Checked = false;
        _reviewAnalysisSession = new(_reviewDecisions, _auditService);
        _analysis = analysis;
        _displayAnalysis = analysis;
        _timeZone = timeZone ?? ProjectTime.Beijing;
        _tasks = tasks;
        _designTasks = tasks;
        _referenceCentralMeridian = referenceCentralMeridian;
        _routeFingerprint = routeFingerprint;
        _deferInitialAnalysis = deferInitialAnalysis;
        if (tasks.Count == 0 && seedRequest.TrackSegmentation is null)
            seedRequest = seedRequest with { TrackSegmentation = new() { Enabled = true } };
        _priorReviewStale = seedRequest.SourceFingerprint != analysis.Fingerprint ||
            seedRequest.RouteFingerprint != routeFingerprint ||
            seedRequest.RecognitionVersion != AuditProductionService.RecognitionVersion(seedRequest) ||
            seedRequest.TrackSegmentationFingerprint != TrackSegmentationReferenceService.Fingerprint(seedRequest.TrackSegmentation) ||
            seedRequest.ProjectCentralMeridian is { } savedCm && referenceCentralMeridian is { } currentCm && Math.Abs(savedCm - currentCm) > 1e-9;
        _seedRequest = _priorReviewStale
            ? seedRequest with
            {
                SourceFingerprint = analysis.Fingerprint,
                RouteFingerprint = routeFingerprint,
                QualityConfirmations = [],
                ConfirmedPassIds = [],
                Overrides = [],
                AnomalyDecisions = [],
                ManualLegAssignments = [],
                ManualIncludedFlightLegIds = [], DefaultMatchedSelectionsApplied = false, MotherLineRestoration = null
            }
            : seedRequest;
        _seedRequest = _seedRequest with { ProjectCentralMeridian = referenceCentralMeridian ?? seedRequest.ProjectCentralMeridian,
            AllowActualLineRegrouping = spatialOnly };
        _defaultMatchedSelectionsApplied = _seedRequest.DefaultMatchedSelectionsApplied ||
            _seedRequest.ConfirmedPassIds.Count > 0 || _seedRequest.ManualIncludedFlightLegIds.Count > 0 ||
            _seedRequest.QualityConfirmations.Count > 0 || _seedRequest.ManualLegAssignments.Count > 0;
        InitializeMotherLineRestoration();
        if (!_deferInitialAnalysis) RefreshScreening();
        Text = spatialOnly ? "中间 CSV 空间分线（源文件时间，时区未确认）" : "自动分线核验";
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        MaximizeBox = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        MinimumSize = new Size(980, 700);
        Size = new Size(1320, 860);

        if (!_deferInitialAnalysis) InitializeWindows();
        BuildUi();
        _map.LegsClicked += LocatePreviewLegs;
        Disposed += (_, _) => _previewLegMenu?.Dispose();
        RefreshWindowGrid();
        UiTheme.Apply(this);
        UiLayout.FitFormToWorkingArea(this);
        WindowState = FormWindowState.Maximized;
        if (!_deferInitialAnalysis) AnalyzeExecution();
        _reviewWorkspaces!.SelectedIndexChanged += (_, _) => NotifyWorkspaceStateChanged();
        _status.TextChanged += (_, _) => NotifyWorkspaceStateChanged();
        _windowGrid.CellValueChanged += (_, _) => NotifyWorkspaceStateChanged();
    }

    public AuditProductionRequest? ReviewedRequest { get; private set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool EmbeddedWorkspace { get; set; }
    public event Action? ProcessingRequested;
    private string? _savedWorkspaceReviewFingerprint;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool HasUnsavedWorkspaceReview => EmbeddedWorkspace && _audit is not null &&
        _savedWorkspaceReviewFingerprint is not null && _savedWorkspaceReviewFingerprint != WorkspaceReviewFingerprint();
    private string WorkspaceReviewFingerprint() => ProductionSnapshotService.RequestFingerprint(BuildReviewedRequest()) +
        System.Text.Json.JsonSerializer.Serialize(_windowGrid.Rows.Cast<DataGridViewRow>()
            .Select(r => r.Cells.Cast<DataGridViewCell>().Select(c => Convert.ToString(c.Value)).ToArray()).ToArray());
    public void MarkWorkspaceReviewSaved()
    {
        _savedWorkspaceReviewFingerprint = WorkspaceReviewFingerprint();
        NotifyWorkspaceStateChanged();
    }
    public bool SaveWorkspaceReview()
    {
        if (_snapshotPending) { _status.Text = "请先恢复或忽略已有核验记录，再保存当前选择。"; return false; }
        if (_taskScopeBackup is not null) { _status.Text = "请先应用或取消任务范围修改，再保存。"; return false; }
        RefreshReviewedExecution();
        if (_audit is null || ReviewOutputDirectory is null) return false;
        SegmentationReviewStore.Save(Path.Combine(ReviewOutputDirectory, SegmentationReviewStore.AutomaticStateName), BuildReviewedRequest());
        MarkWorkspaceReviewSaved();
        return true;
    }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public AuditProductionService ReviewSession => _auditService;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public ReviewAnalysisSession CalculationSession => _reviewAnalysisSession;
    public (AuditProductionResult Audit, IReadOnlyList<UavMagCanonicalSample> Samples,
        IReadOnlyList<PlannedSurveyLineTask> Tasks, string SelectionFile) CaptureReportSelection()
    {
        if (!TryPrepareProcessingRequest() || !SaveWorkspaceReview() || _audit is null || ReviewOutputDirectory is null)
            throw new InvalidOperationException("请先完成当前分线核验并保存有效选择，再准备报告正式测线数据。");
        return (_audit, _analysis.UavSamples, _audit.EffectiveTasks ?? _tasks,
            Path.Combine(ReviewOutputDirectory, SegmentationReviewStore.AutomaticStateName));
    }

    internal MagOps.Reporting.QualityOffsetReference[] CaptureOffsetReferences()
    {
        var request = BuildReviewedRequest();
        if (request.TrackSegmentation?.ReferenceGeometry.Any(g => g.Kind is "SURVEY" or "TIE") == true)
            return MagOps.Reporting.QualityOffsetReferences.FromReview(request);
        return MagOps.Reporting.QualityOffsetReferences.FromTasks(_tasks);
    }

    public (AuditProductionResult Audit, AuditProductionRequest Request)? CaptureSpatialSelection()
    {
        if (!_spatialOnly) throw new InvalidOperationException("该入口仅用于中间 CSV 空间分线。");
        if (!TryPrepareProcessingRequest() || !SaveWorkspaceReview() || _audit is null) return null;
        return (_audit, BuildReviewedRequest());
    }

    public void ShowSpatialSourceSummary(string summary)
    {
        if (!_spatialOnly) return;
        var root = Controls.OfType<TableLayoutPanel>().Single();
        var bar = (FlowLayoutPanel)root.GetControlFromPosition(0, 0)!;
        // A source audit can be long. Keep it readable without allowing it to
        // consume the review grids when a maximized window is restored.
        var label = new Label { Text = summary, AutoSize = false, AutoEllipsis = true,
            Height = Math.Max(28, bar.Font.Height + 10), ForeColor = UiTheme.TextSecondary,
            TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(3, 2, 3, 2) };
        var hint = new ToolTip();
        hint.SetToolTip(label, summary);
        label.Disposed += (_, _) => hint.Dispose();
        void FitSummary() => label.Width = Math.Max(1, bar.ClientSize.Width - bar.Padding.Horizontal - 8);
        bar.SizeChanged += (_, _) => FitSummary();
        FitSummary();
        bar.Controls.Add(label);
    }

    private void RefreshScreening()
    {
        if (_seedRequest.SpatialScreening is not { } input) { _displayAnalysis = _analysis; return; }
        var screening = BusyOperationDialog.Run(this, "工区筛选", "正在核对原始样点与排除范围…",
            () => new WorkareaSpatialService().Screen(_analysis, input));
        bool changed = _seedRequest.ScreeningFingerprint != screening.Fingerprint;
        _seedRequest = _seedRequest with { ScreeningFingerprint = screening.Fingerprint };
        if (changed) ClearReviewDecisions();
        _displayAnalysis = WorkareaSpatialService.Apply(_analysis, screening);
    }

    private void ClearReviewDecisions()
    {
        _reviewDecisions.Reset();
        _reviewAnalysisSession.Invalidate();
        _seedRequest = _seedRequest with { ConfirmedPassIds = [], QualityConfirmations = [], Overrides = [],
            ManualLegAssignments = [], ManualIncludedFlightLegIds = [], AnomalyDecisions = [], DefaultMatchedSelectionsApplied = true,
            MotherLineRestoration = _seedRequest.MotherLineRestoration is { } restore ? new() { Enabled = restore.Enabled, Options = restore.Options } : null };
        _defaultMatchedSelectionsApplied = true;
        _audit = null;
        _priorReviewStale = true;
        RefreshLegSelectionViews();
    }

    private void EditScreening()
    {
        var input = _seedRequest.SpatialScreening ?? new WorkareaScreeningInput(
            WorkareaSpatialService.FromTasks("", _tasks), new());
        using var dialog = new WorkareaScreeningDialog(_analysis, input);
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.ReviewedInput is null) return;
        string oldFingerprint = _seedRequest.ScreeningFingerprint;
        _seedRequest = _seedRequest with { SpatialScreening = dialog.ReviewedInput };
        RefreshScreening();
        if (oldFingerprint != _seedRequest.ScreeningFingerprint)
        {
            _windows.Clear(); _seedRequest = _seedRequest with { MissionWindows = [] };
            InitializeWindows(); RefreshWindowGrid(); AnalyzeExecution();
        }
    }

    private void ExcludeSamples(IEnumerable<UavMagCanonicalSample> samples)
    {
        var input = _seedRequest.SpatialScreening ?? new WorkareaScreeningInput(
            WorkareaSpatialService.FromTasks("", _tasks), new());
        input = input with { RouteFingerprint = _routeFingerprint };
        input = input with { SourceFingerprint = _analysis.Fingerprint,
            ContextFingerprint = WorkareaSpatialService.ContextFingerprint(input),
            ExcludedSamples = input.ExcludedSamples.Concat(samples.Select(p => new WorkareaExcludedSample(p.SourceFile, p.SourceRecord)))
                .Distinct().ToArray() };
        _seedRequest = _seedRequest with { SpatialScreening = input };
        ClearReviewDecisions(); RefreshScreening();
    }

    private void ExcludeWindow()
    {
        var windows = SelectedWindows();
        if (windows.Length == 0) return;
        ExcludeSamples(_displayAnalysis.UavSamples.Where(p => windows.Any(window =>
            p.TimestampUtc >= window.StartUtc && p.TimestampUtc <= window.EndUtc &&
            (window.SourceFiles.Count == 0 || window.SourceFiles.Contains(p.SourceFile, StringComparer.OrdinalIgnoreCase)))));
        _windows.RemoveAll(w => windows.Contains(w)); RefreshWindowGrid(); AnalyzeExecution();
    }

    private void ExcludeLeg()
    {
        var selected = _legGrid.SelectedRows.Cast<DataGridViewRow>().Select(r => r.Tag).OfType<ReviewLegRow>().ToArray();
        if (selected.Length == 0) return;
        var keys = selected.SelectMany(r => r.Leg.Samples).Select(p => (p.SourceFile, p.SourceRecord)).ToHashSet();
        ExcludeSamples(_displayAnalysis.UavSamples.Where(p => keys.Contains((p.SourceFile, p.SourceRecord))).ToArray());
        AnalyzeExecution();
    }

    private void ConfirmWindowSuggestions()
    {
        var windowIds = SelectedWindows().Select(w => w.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (windowIds.Count == 0) return;
        _legGrid.EndEdit();
        int confirmed = 0, skipped = 0;
        foreach (DataGridViewRow row in _legGrid.Rows)
            if (row.Tag is ReviewLegRow leg && leg.Pass is not null && windowIds.Contains(leg.Leg.MissionExecutionId))
            {
                // Do not change prior manual choices for candidates requiring review.
                if (!CanBatchAddLeg(leg))
                {
                    skipped++;
                    continue;
                }
                row.Cells["Confirmed"].Value = true;
                confirmed++;
            }
        _status.Text = $"所选窗口已确认 {confirmed} 条作业候选；跳过 {skipped} 条反向、往返、连接或待复核航段，可检查后手动添加；质量状态保持独立。";
    }

    private void PassSelectedLegQuality()
    {
        _legGrid.EndEdit();
        int passed = 0;
        foreach (DataGridViewRow row in _legGrid.SelectedRows)
            if (row.Tag is ReviewLegRow { Pass: not null } && row.Cells["Confirmed"].Value is true)
            {
                row.Cells["QC"].Value = "通过";
                passed++;
            }
        RefreshProductionStatus();
        _status.Text = $"所选且已确认归属的 {passed} 个航段已按你的操作标记为质量检查通过；未确认归属的航段未改动。";
    }

    private void RefreshProductionStatus()
    {
        var rows = _legGrid.Rows.Cast<DataGridViewRow>().ToArray();
        int accepted = rows.Count(IsAcceptedLegRow);
        int pendingQuality = rows.Count(r => IsAcceptedLegRow(r) && Convert.ToString(r.Cells["QC"].Value) != "通过");
        bool hasChoice = accepted > 0;
        int selectedPoints = _audit?.SelectedSamples.Count ?? 0;
        _productionStatus.Text = $"处理用 CSV：右侧已纳入 {accepted} 段，左侧待选 {rows.Length - accepted} 段；当前生产选择 {selectedPoints:N0} 点；其中 {pendingQuality} 段保留待检查/异常质量状态。" +
            (hasChoice && selectedPoints > 0 ? (_spatialOnly ? "导出将保留原列原值，仅筛选记录。" : "继续即可生成有效改正记录。") :
                hasChoice ? "生产选择为空，生成按钮会阻止空 CSV；请查看核验提示。" : "请将要处理的命中航段添加到右侧。");
        _productionStatus.ForeColor = hasChoice && selectedPoints > 0 ? UiTheme.TextSecondary : Color.DarkRed;
    }

    private void InitializeWindows()
    {
        IReadOnlyList<MissionTimeWindow> initial = _seedRequest.MissionWindows;
        if (initial.Count == 0)
        {
            var samples = _displayAnalysis.UavSamples;
            initial = BusyOperationDialog.Run(this, "飞行窗口", "正在识别连续飞行时间窗口…",
                () => new FlightWindowInferenceService().Infer(samples).Candidates.Select(x => x.Window).ToArray());
        }

        foreach (MissionTimeWindow window in initial)
        {
            var draft = new WindowDraft(window.MissionExecutionId, window.StartUtc, window.EndUtc);
            foreach (string id in window.SurveyTaskStableIds ?? [])
                if (TrackMode || _tasks.Any(t => t.SurveyTaskStableId.Equals(id, StringComparison.OrdinalIgnoreCase)))
                    draft.TaskIds.Add(id);
            draft.TaskScopeSource = window.TaskScopeSource;
            draft.SourceFiles = window.SourceFiles;
            draft.PhysicalFlightId = window.PhysicalFlightId;
            _windows.Add(draft);
        }
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(10) };
        // Bound preferred-size measurement to the viewport before DPI scaling and first maximization.
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 132));

        var modeBar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        modeBar.Controls.Add(UiComponents.Button("识别模式 / KML / CSV / 方向", (_, _) => EditRecognitionMode()));
        modeBar.Controls.Add(UiComponents.Button("所选窗口速度…", (_, _) => EditWindowSpeed()));
        modeBar.Controls.Add(UiComponents.Button("已知起降点…", (_, _) => EditTakeoffPositions()));
        modeBar.Controls.Add(UiComponents.Button("任务范围…", (_, _) => ToggleTaskScope(root)));
        modeBar.Controls.Add(_scopeBadge);
        modeBar.Controls.Add(new Label
        {
            AutoSize = true, Padding = new Padding(0, 8, 0, 0),
            Text = "左侧待选，添加到右侧即整段纳入。未归属可手动添加；Ctrl / Shift 多选。",
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = UiTheme.TextSecondary
        });
        root.Controls.Add(modeBar, 0, 0);

        var setup = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterDistance = 690 };
        setup.SizeChanged += (_, _) => FitSplit(setup, 0.70);
        ConfigureGrid(_windowGrid);
        _windowGrid.ReadOnly = false;
        _windowGrid.AllowUserToAddRows = false;
        _windowGrid.AllowUserToDeleteRows = true;
        _windowGrid.SelectionChanged += (_, _) => LoadTasksForSelectedWindow();
        _windowGrid.UserDeletedRow += (_, e) =>
        {
            if (e.Row.Tag is WindowDraft window)
                _windows.Remove(window);
        };
        _windowGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Mission", HeaderText = "飞行窗口", Width = 110, ReadOnly = true });
        _windowGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Date", HeaderText = "作业日期", Width = 160,
            ToolTipText = "同日：yyyy-MM-dd；跨日：开始日期 → 结束日期。起止时间按各自日期解释。"
        });
        _windowGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Start", HeaderText = "开始时间", Width = 120, ToolTipText = ProjectTime.Label(_timeZone) + "；支持有效小数秒及显式偏移" });
        _windowGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "End", HeaderText = "结束时间", Width = 120, ToolTipText = ProjectTime.Label(_timeZone) + "；跨日请填写两个日期" });
        _windowGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "TaskNames", HeaderText = "任务范围", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, ReadOnly = true });
        setup.Panel1.Controls.Add(_windowGrid);

        var taskPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(8, 0, 0, 0) };
        taskPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        taskPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        taskPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        taskPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _taskSelectionLabel.Dock = DockStyle.Fill;
        _taskSelectionLabel.TextAlign = ContentAlignment.MiddleLeft;
        taskPanel.Controls.Add(_taskSelectionLabel, 0, 0);
        ConfigureGrid(_taskList);
        _taskList.ReadOnly = false;
        _taskList.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Allowed", HeaderText = "允许", Width = 55,
            ThreeState = true, TrueValue = true, FalseValue = false, IndeterminateValue = DBNull.Value });
        _taskList.Columns.Add(new DataGridViewTextBoxColumn { Name = "Task", HeaderText = "测线（灰框表示部分窗口允许）",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, ReadOnly = true });
        _taskList.CurrentCellDirtyStateChanged += (_, _) =>
        { if (_taskList.IsCurrentCellDirty) _taskList.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        _taskList.CellValueChanged += (_, e) =>
        {
            if (_loadingTaskChecks || e.RowIndex < 0 || e.ColumnIndex != 0) return;
            ApplyTaskRows([_taskList.Rows[e.RowIndex]], _taskList.Rows[e.RowIndex].Cells[0].Value is true);
        };
        foreach (PlannedSurveyLineTask task in _tasks.Where(x => !string.IsNullOrWhiteSpace(x.SurveyTaskStableId))
                     .GroupBy(x => x.SurveyTaskStableId, StringComparer.OrdinalIgnoreCase)
                     .Where(g => g.Count() == 1).Select(g => g.Single()))
        {
            int row = _taskList.Rows.Add(false, task.LineName);
            _taskList.Rows[row].Tag = new TaskChoice(task);
        }
        taskPanel.Controls.Add(_taskList, 0, 1);
        var taskActions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        taskActions.Controls.Add(UiComponents.Button("添加选中测线", (_, _) => ApplyTaskRows(_taskList.SelectedRows.Cast<DataGridViewRow>(), true)));
        taskActions.Controls.Add(UiComponents.Button("移除选中测线", (_, _) => ApplyTaskRows(_taskList.SelectedRows.Cast<DataGridViewRow>(), false)));
        taskActions.Controls.Add(UiComponents.Button("允许全部", (_, _) => ApplyTaskRows(_taskList.Rows.Cast<DataGridViewRow>(), true)));
        taskActions.Controls.Add(UiComponents.Button("全部取消", (_, _) => ApplyTaskRows(_taskList.Rows.Cast<DataGridViewRow>(), false)));
        taskPanel.Controls.Add(taskActions, 0, 2);
        setup.Panel2.Controls.Add(taskPanel);
        _taskScopePanel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Visible = false };
        _taskScopePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _taskScopePanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _taskScopePanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _taskScopePanel.Controls.Add(setup, 0, 0);
        var scopeActions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        scopeActions.Controls.Add(UiComponents.Button("应用并重新分析", (_, _) => ApplyTaskScope(root)));
        scopeActions.Controls.Add(UiComponents.Button("取消范围修改", (_, _) => CancelTaskScope(root)));
        scopeActions.Controls.Add(_taskScopeSummary);
        _taskScopePanel.Controls.Add(scopeActions, 0, 1);
        root.Controls.Add(_taskScopePanel, 0, 1);

        var review = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterDistance = 590 };
        review.SizeChanged += (_, _) =>
        {
            if (review.Width < 200 + 200 + review.SplitterWidth) return;
            // Recompute after every resize/restore. A stale wide-window splitter
            // leaves the accepted list unusably narrow when returning to 980 px.
            FitSplit(review, Math.Clamp(review.ClientSize.Width * 0.40, 200,
                review.ClientSize.Width - review.SplitterWidth - 200) / Math.Max(1d, review.ClientSize.Width));
        };
        var visual = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        visual.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        visual.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        visual.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
        _map.Dock = DockStyle.Fill;
        _map.BorderStyle = BorderStyle.FixedSingle;
        _timeline.Dock = DockStyle.Fill;
        _timeline.BorderStyle = BorderStyle.FixedSingle;
        visual.Controls.Add(_map, 0, 0);
        visual.Controls.Add(_timeline, 0, 1);
        review.Panel1.Controls.Add(visual);

        ConfigureGrid(_legGrid);
        _legGrid.AllowUserToAddRows = false;
        _legGrid.ReadOnly = false;
        _legGrid.SelectionChanged += (_, _) => UpdateSelectedLeg();
        _legGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "类别", Width = 85, ReadOnly = true });
        _legGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "建议测线", Width = 80, ReadOnly = true });
        _legGrid.Columns.Add(new DataGridViewComboBoxColumn { Name = "ManualTask", HeaderText = "指定所属测线", Width = 120, FlatStyle = FlatStyle.Flat });
        _legGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "作业时间", Width = 160, ReadOnly = true });
        _legGrid.Columns[3].DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        _legGrid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
        _legGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "点数", Width = 55, ReadOnly = true, Visible = false });
        _legGrid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Confirmed", HeaderText = "归属确认", Width = 80,
            ToolTipText = "确认所属测线；还需要质量检查通过才能进入生产选择。" });
        var qc = new DataGridViewComboBoxColumn { Name = "QC", HeaderText = "质量检查", Width = 90, FlatStyle = FlatStyle.Flat,
            ToolTipText = "待检查或不通过的航段不会自动进入处理用 CSV；通过后仍需勾选归属确认。" };
        qc.Items.AddRange("待检查", "通过", "不通过");
        _legGrid.Columns.Add(qc);
        _legGrid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Include", HeaderText = "纳入草稿", Width = 75,
            ToolTipText = "明确保留本航段进入待复核处理用 CSV，不会把归属或质量检查变为已通过；仍需有效日变改正值。" });
        _legGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "说明", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, ReadOnly = true });
        _legGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Direction", HeaderText = "设计 / 实飞方向", Width = 180, ReadOnly = true });
        _legGrid.Columns.Cast<DataGridViewColumn>().Single(c => c.Name == "Direction").DisplayIndex = 2;
        _legGrid.CellValueChanged += (_, e) =>
        {
            if (e.RowIndex >= 0 && _legGrid.Columns[e.ColumnIndex].Name == "ManualTask")
            {
                string value = Convert.ToString(_legGrid.Rows[e.RowIndex].Cells[e.ColumnIndex].Value, CultureInfo.InvariantCulture) ?? "";
                if (!_populatingLegGrid)
                {
                    // The visible combo is still committing its edit. Do not rebuild
                    // its rows from nested confirmation/quality change events.
                    _populatingLegGrid = true;
                    try
                    {
                        _legGrid.Rows[e.RowIndex].Cells[5].Value = !value.StartsWith("（", StringComparison.Ordinal);
                        _legGrid.Rows[e.RowIndex].Cells[6].Value = "待检查";
                    }
                    finally { _populatingLegGrid = false; }
                    CommitLegDecisionEdit(_legGrid.Rows[e.RowIndex]);
                    UpdateSelectedLeg();
                    if (IsHandleCreated) BeginInvoke((Action)(() => { if (!IsDisposed && !Disposing) RefreshReviewedExecution(); }));
                    return;
                }
            }
            if (e.RowIndex >= 0 && _legGrid.Columns[e.ColumnIndex].Name is "ManualTask" or "Confirmed" or "QC" or "Include")
            {
                if (!_populatingLegGrid) CommitLegDecisionEdit(_legGrid.Rows[e.RowIndex]);
                UpdateSelectedLeg();
                if (!_populatingLegGrid) RefreshLegSelectionViews();
            }
        };
        review.Panel2.Controls.Add(BuildLegSelectionPanel());
        var reviewActions = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true };
        reviewActions.Controls.Add(MenuButton("筛选 / 排除…",
            ("工区筛选 / 恢复…", EditScreening), ("排除选中窗口", ExcludeWindow),
            ("排除选中航段", ExcludeLeg), ("恢复手动排除", RestoreManualExclusions)));
        reviewActions.Controls.Add(MenuButton("窗口候选…", ("添加所选窗口作业候选", AddWindowMatchedLegs)));
        var showDesign = new CheckBox { Text = "显示设计线", AutoSize = true, Checked = true };
        showDesign.CheckedChanged += (_, _) => _map.ShowDesignLines = showDesign.Checked;
        reviewActions.Controls.Add(showDesign);
        var layers = new Button { Text = "图层…", AutoSize = true };
        layers.Click += (_, _) => _map.ShowLayers(layers);
        reviewActions.Controls.Add(layers);
        if (_spatialOnly)
        {
            reviewActions.Controls.Add(_autoRestoreMotherLines);
            _autoRestoreMotherLines.CheckedChanged += (_, _) =>
            {
                if (_updatingRestoration || _seedRequest.MotherLineRestoration is not { } state) return;
                _seedRequest = BuildReviewedRequest() with { MotherLineRestoration = state with { Enabled = _autoRestoreMotherLines.Checked, CheckedKey = "" } };
                RefreshReviewedExecution();
                _status.Text = MotherLineRestorationSummary() + (_autoRestoreMotherLines.Checked ? "" : "已暂停后续自动应用；已有结果可在恢复结果中撤销。");
            };
            reviewActions.Controls.Add(MenuButton("归并母测线…", ("打开母线归并 / 批量核对…", ShowActualLineRegrouping),
                ("重新检查", RecheckMotherLines), ("自动恢复设置…", EditMotherLineRestorationOptions)));
        }
        if (!_spatialOnly) reviewActions.Controls.Add(MenuButton("飞行评估…", ("保存飞行评估…", () => SaveFlightQualityAssessment(true)),
            ("评估设置…", EditFlightQualitySettings)));
        _automaticAssessment.Visible = !_spatialOnly;
        reviewActions.Controls.Add(_automaticAssessment);
        _automaticAssessment.CheckedChanged += (_, _) =>
        { if (_automaticAssessment.Checked && IsHandleCreated) SaveFlightQualityAssessment(false); };
        reviewActions.Controls.Add(new Label { Text = ProjectTime.Label(_timeZone), AutoSize = true });
        review.Panel2.Controls.Add(reviewActions);
        _reviewWorkspaces = new TabControl { Dock = DockStyle.Fill, Name = "ReviewWorkspaces" };
        var reviewPage = new TabPage("航段核验");
        reviewPage.Controls.Add(review);
        _reviewWorkspaces.TabPages.Add(reviewPage);
        if (_spatialOnly)
        {
            _motherLinePage = new TabPage("母线整理");
            _reviewWorkspaces.TabPages.Add(_motherLinePage);
            _reviewWorkspaces.SelectedIndexChanged += (_, _) =>
            {
                if (_reviewWorkspaces.SelectedTab == _motherLinePage) RefreshMotherLineWorkspace();
            };
        }
        root.Controls.Add(_reviewWorkspaces, 0, 2);

        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, RowCount = 3 };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        footer.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        _productionStatus.Dock = DockStyle.Fill;
        _productionStatus.TextAlign = ContentAlignment.MiddleLeft;
        footer.Controls.Add(_productionStatus, 0, 2);
        footer.SetColumnSpan(_productionStatus, 5);
        _status.Dock = DockStyle.Fill;
        _status.AutoEllipsis = true;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        footer.Controls.Add(_status, 0, 0);
        Button addWindow = new() { Text = "添加窗口", AutoSize = true };
        addWindow.Click += (_, _) => AddWindow();
        footer.Controls.Add(addWindow, 1, 0);
        Button load = new() { Text = "载入核验输入…", AutoSize = true };
        load.Click += (_, _) => LoadRequest();
        footer.Controls.Add(load, 2, 0);
        Button analyze = new() { Text = "重新分析", AutoSize = true };
        analyze.Click += (_, _) => AnalyzeExecution();
        footer.Controls.Add(analyze, 3, 0);
        Button save = new() { Text = "保存核验输出", AutoSize = true };
        save.Click += (_, _) => SaveRequest();
        footer.Controls.Add(save, 4, 0);
        _includeUnresolved.Text = "全部未选轨迹也纳入草稿（含转场）";
        _includeUnresolved.AutoSize = true;
        _includeUnresolved.Checked = _seedRequest.IncludeUnconfirmedSamples;
        _includeUnresolved.CheckedChanged += (_, _) =>
        {
            RefreshProductionStatus();
            _status.Text = _includeUnresolved.Checked
                ? "未选轨迹（包括转场和连接段）将按你的选择进入待复核草稿，不会变成已确认测线。"
                : "未确认轨迹不会进入生产选择；仍会保留在审核记录中。";
        };
        _includeUnresolved.Checked = false;
        _includeUnresolved.Visible = false;
        Button cancel = new() { Text = "取消", AutoSize = true, DialogResult = DialogResult.Cancel };
        footer.Controls.Add(cancel, 3, 1);
        // CommitReview owns closing. A modal spatial workspace must remain open after export or validation failure.
        Button proceed = new() { Text = _spatialOnly ? "导出空间分线 CSV" : "生成右侧航段 CSV", AutoSize = true, DialogResult = DialogResult.None };
        proceed.Click += (_, _) => CommitReview();
        footer.Controls.Add(proceed, 4, 1);
        AcceptButton = proceed;
        CancelButton = cancel;
        root.Controls.Add(footer, 0, 3);
        Controls.Add(root);
    }

    private static void FitSplit(SplitContainer split, double leftFraction)
    {
        if (split.ClientSize.Width < 450) return;
        int desired = Math.Clamp((int)(split.ClientSize.Width * leftFraction), 200,
            split.ClientSize.Width - split.SplitterWidth - 200);
        if (split.SplitterDistance != desired) split.SplitterDistance = desired;
    }

    private static void ConfigureGrid(DataGridView grid)
    {
        grid.Dock = DockStyle.Fill;
        grid.AutoGenerateColumns = false;
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        grid.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.False;
        grid.AllowUserToResizeColumns = true;
        grid.AllowUserToAddRows = false;
        grid.AllowUserToDeleteRows = false;
        grid.MultiSelect = true;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.RowHeadersVisible = false;
        grid.BackgroundColor = SystemColors.Window;
    }

    private void RefreshWindowGrid()
    {
        _scopeBadge.Text = $"{_windows.Count} 个窗口 · " + (_windows.Any(w => w.TaskScopeSource == "MANUAL") ? "手动范围" : "自动范围");
        var selectedIds = SelectedWindows().Select(w => w.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string? selectedId = (_windowGrid.CurrentRow?.Tag as WindowDraft)?.Id;
        _refreshingWindowGrid = true;
        try
        {
        _windowGrid.Rows.Clear();
        for (int index = 0; index < _windows.Count; index++)
        {
            WindowDraft window = _windows[index];
            int row = _windowGrid.Rows.Add($"窗口 {index + 1}",
                FormatWindowDates(window.StartUtc, window.EndUtc),
                FormatEditableTime(window.StartUtc),
                FormatEditableTime(window.EndUtc),
                string.Join(", ", _tasks.Where(t => window.TaskIds.Contains(t.SurveyTaskStableId)).Select(t => t.LineName)));
            _windowGrid.Rows[row].Tag = window;
        }
        if (_windowGrid.Rows.Count > 0)
        {
            var selected = _windowGrid.Rows.Cast<DataGridViewRow>().FirstOrDefault(r => (r.Tag as WindowDraft)?.Id == selectedId) ?? _windowGrid.Rows[0];
            _windowGrid.CurrentCell = selected.Cells[0];
            _windowGrid.ClearSelection();
            foreach (DataGridViewRow row in _windowGrid.Rows)
                row.Selected = row.Tag is WindowDraft w && selectedIds.Contains(w.Id);
            if (_windowGrid.SelectedRows.Count == 0) selected.Selected = true;
        }
        }
        finally { _refreshingWindowGrid = false; }
        LoadTasksForSelectedWindow();
    }

    private string FormatEditableTime(DateTimeOffset utc)
    {
        var local = ProjectTime.Display(utc, _timeZone);
        return local.ToString(_timeZone.IsAmbiguousTime(local.DateTime) ? "HH:mm:ss.FFFFFFFzzz" : "HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture);
    }

    private string FormatWindowDates(DateTimeOffset start, DateTimeOffset end)
    {
        string startDate = ProjectTime.Display(start, _timeZone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string endDate = ProjectTime.Display(end, _timeZone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return startDate == endDate ? startDate : $"{startDate} → {endDate}";
    }

    private (DateTimeOffset Start, DateTimeOffset End) ParseWindowTimes(DataGridViewRow row)
    {
        string dates = Convert.ToString(row.Cells["Date"].Value, CultureInfo.InvariantCulture) ?? "";
        string[] parts = dates.Split('→', StringSplitOptions.TrimEntries);
        if (parts.Length is < 1 or > 2)
            throw new FormatException("日期请填写 yyyy-MM-dd；跨日窗口请填写 开始日期 → 结束日期。");
        DateTime startDate = DateTime.ParseExact(parts[0], "yyyy-MM-dd", CultureInfo.InvariantCulture);
        DateTime endDate = parts.Length == 1 ? startDate :
            DateTime.ParseExact(parts[1], "yyyy-MM-dd", CultureInfo.InvariantCulture);
        string[] timeFormats = ["HH:mm", "HH:mm:ss", "HH:mm:ss.FFFFFFF"];
        DateTime ParseTime(string column) => DateTime.ParseExact(
            Convert.ToString(row.Cells[column].Value, CultureInfo.InvariantCulture) ?? "",
            timeFormats, CultureInfo.InvariantCulture, DateTimeStyles.NoCurrentDateDefault);
        DateTimeOffset ParseStamp(DateTime date, string column)
        {
            string value = Convert.ToString(row.Cells[column].Value, CultureInfo.InvariantCulture) ?? "";
            if (value.Contains('+') || value.LastIndexOf('-') > 0)
            {
                var stamp = DateTimeOffset.ParseExact(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " " + value,
                    ["yyyy-MM-dd HH:mm:ss.FFFFFFFzzz", "yyyy-MM-dd HH:mm:sszzz"], CultureInfo.InvariantCulture);
                var local = ProjectTime.Display(stamp, _timeZone);
                if (local.DateTime != stamp.DateTime || local.Offset != stamp.Offset)
                    throw new FormatException("显式偏移与项目时区不一致，请检查夏令时和偏移。");
                return stamp.ToUniversalTime();
            }
            return ProjectTime.ToUtc(date.Date + ParseTime(column).TimeOfDay, _timeZone);
        }
        var start = ParseStamp(startDate, "Start");
        var end = ParseStamp(endDate, "End");
        if (end <= start)
            throw new FormatException("结束时间必须晚于开始时间；跨日窗口请在日期栏明确填写两个日期。");
        return (start, end);
    }

    private WindowDraft[] SelectedWindows()
    {
        var selected = _windowGrid.SelectedRows.Cast<DataGridViewRow>().Select(r => r.Tag).OfType<WindowDraft>().ToArray();
        return selected.Length > 0 ? selected : _windowGrid.CurrentRow?.Tag is WindowDraft current ? [current] : [];
    }

    private void LoadTasksForSelectedWindow()
    {
        if (_refreshingWindowGrid) return;
        var windows = SelectedWindows();
        _taskSelectionLabel.Text = $"所选 {windows.Length} 个窗口允许匹配的测线";
        _loadingTaskChecks = true;
        try
        {
            _taskList.Enabled = windows.Length > 0;
            foreach (DataGridViewRow row in _taskList.Rows)
            {
                string id = ((TaskChoice)row.Tag!).Task.SurveyTaskStableId;
                int count = windows.Count(w => w.TaskIds.Contains(id));
                row.Cells[0].Value = count == 0 ? false : count == windows.Length ? true : DBNull.Value;
            }
        }
        finally { _loadingTaskChecks = false; }
    }

    private void ApplyTaskRows(IEnumerable<DataGridViewRow> rows, bool allow)
    {
        string[] ids = rows.Select(r => r.Tag).OfType<TaskChoice>().Select(c => c.Task.SurveyTaskStableId).ToArray();
        var windows = SelectedWindows();
        foreach (var window in windows)
        {
            foreach (string id in ids)
                if (allow) window.TaskIds.Add(id); else window.TaskIds.Remove(id);
            window.TaskScopeSource = "MANUAL";
            UpdateWindowTaskNames(window);
        }
        LoadTasksForSelectedWindow();
        _status.Text = $"已更新 {windows.Length} 个窗口的任务范围；完成批量选择后点击重新分析。";
    }

    private void RefreshWindowTaskNames(WindowDraft? target = null)
    {
        _taskList.EndEdit();
        foreach (var window in target is null ? SelectedWindows() : [target]) UpdateWindowTaskNames(window);
    }

    private void RestoreManualExclusions()
    {
        if (_seedRequest.SpatialScreening is not { } input) return;
        _seedRequest = _seedRequest with { SpatialScreening = input with
        {
            ExcludedSamples = [],
            Decisions = input.Decisions.Where(d => d.Decision != "EXCLUDE").ToArray()
        }, MissionWindows = [] };
        ClearReviewDecisions(); RefreshScreening();
        _windows.Clear(); InitializeWindows(); RefreshWindowGrid(); AnalyzeExecution();
        _status.Text = "手动排除已恢复；自动工区排除仍生效。请核验重新识别的航段。";
    }

    private void UpdateWindowTaskNames(WindowDraft window)
    {
        DataGridViewRow? row = _windowGrid.Rows.Cast<DataGridViewRow>()
            .FirstOrDefault(x => ReferenceEquals(x.Tag, window));
        if (row is not null)
            row.Cells["TaskNames"].Value = string.Join(", ", _tasks
                .Where(t => window.TaskIds.Contains(t.SurveyTaskStableId)).Select(t => t.LineName));
    }

    private void AddWindow()
    {
        DateTimeOffset[] times = _displayAnalysis.UavSamples.Select(x => x.TimestampUtc).ToArray();
        DateTimeOffset start = times.Length == 0 ? DateTimeOffset.UtcNow : times.Min();
        DateTimeOffset end = times.Length == 0 ? start.AddMinutes(1) : times.Max();
        if (end <= start) end = start.AddSeconds(1);
        _windows.Add(new WindowDraft($"mission-manual-{_windows.Count + 1:D3}", start, end));
        RefreshWindowGrid();
        _windowGrid.CurrentCell = _windowGrid.Rows[_windowGrid.Rows.Count - 1].Cells[0];
    }

    private void AnalyzeExecution() => AnalyzeExecutionCore(false);

    // Recognition-changing actions call AnalyzeExecution; decision/save actions reuse
    // only a fingerprint-validated, session-local automatic recognition snapshot.
    private void RefreshReviewedExecution() => AnalyzeExecutionCore(true);

    private void AnalyzeExecutionCore(bool reuseRecognition)
    {
        if (_spatialOnly && IntermediateSegmentationEvidence.Problem(_seedRequest.TrackSegmentation) is { } basisError)
        { _audit = null; _status.Text = basisError; return; }
        if (_taskScopeBackup is not null && !_applyingTaskScope)
        { _status.Text = "请在任务范围面板中应用或取消修改。"; return; }
        var totalTimer = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            RefreshTrackModeLabels();
            _pendingLegGrid.EndEdit();
            _acceptedLegGrid.EndEdit();
            _legGrid.EndEdit();
            AuditProductionRequest previous = _audit is null ? _seedRequest : BuildReviewedRequest();
            var priorAudit = _audit;
            _windowGrid.EndEdit();
            foreach (DataGridViewRow row in _windowGrid.Rows)
            {
                if (row.Tag is not WindowDraft window) continue;
                (window.StartUtc, window.EndUtc) = ParseWindowTimes(row);
            }
            RefreshWindowTaskNames();
            MissionTimeWindow[] missions = _windows.Select(w => new MissionTimeWindow(
                w.Id, "", w.StartUtc, w.EndUtc, w.PhysicalFlightId)
            { SurveyTaskStableIds = w.TaskIds.ToArray(), SourceFiles = w.SourceFiles,
                TaskScopeSource = w.TaskScopeSource }).ToArray();
            if (reuseRecognition && priorAudit is not null &&
                ProductionSnapshotService.RequestFingerprint(previous with { MissionWindows = missions }) == priorAudit.ProductionRequestFingerprint)
            {
                _analysisInputChanged = false;
                return;
            }
            _audit = null;
            bool sameWindows = SameMissionWindows(previous.MissionWindows, missions);
            _analysisInputChanged = !sameWindows;
            _seedRequest = previous with
            {
                SourceFingerprint = _analysis.Fingerprint,
                RouteFingerprint = _routeFingerprint,
                RecognitionVersion = AuditProductionService.RecognitionVersion(previous),
                TrackSegmentationFingerprint = TrackSegmentationReferenceService.Fingerprint(previous.TrackSegmentation),
                MissionWindows = missions,
                QualityConfirmations = sameWindows ? previous.QualityConfirmations : [],
                ConfirmedPassIds = sameWindows ? previous.ConfirmedPassIds : [],
                Overrides = sameWindows ? previous.Overrides : [],
                ManualLegAssignments = sameWindows ? previous.ManualLegAssignments : [],
                ManualIncludedFlightLegIds = sameWindows ? previous.ManualIncludedFlightLegIds : [],
                AnomalyDecisions = sameWindows ? previous.AnomalyDecisions : [],
                DefaultMatchedSelectionsApplied = sameWindows && previous.DefaultMatchedSelectionsApplied,
                MotherLineRestoration = sameWindows ? previous.MotherLineRestoration :
                    previous.MotherLineRestoration is { } restore ? new() { Enabled = restore.Enabled, Options = restore.Options } : null,
                RequirePassConfirmation = true,
                AllowReviewDraft = true
            };
            AuditProductionRequest analysisRequest = _seedRequest;
            var calculation = _reviewAnalysisSession.Prepare(_analysis, _designTasks, analysisRequest,
                _routeFingerprint, forceRecognition: !reuseRecognition);
            int recognitionRuns = _auditService.RecognitionRuns;
            var completion = BusyOperationDialog.RunWithProgress(this, "自动分线核验",
                reuseRecognition ? "正在复用识别结果并核对当前选择…" : "正在识别航段、精修边界并核对处理范围…",
                progress => _reviewAnalysisSession.Calculate(calculation, progress));
            if (!completion.IsCurrent || !_reviewAnalysisSession.IsCurrent(calculation) || IsDisposed || Disposing)
            {
                _audit = priorAudit;
                _status.Text = "输入或核验决定已变化，本次过期计算结果未应用；请重新分析当前输入。";
                return;
            }
            _audit = completion.Result;
            bool recognized = _auditService.RecognitionRuns != recognitionRuns;
            if (recognized) _recognitionBatch++;
            if (TrackMode)
            {
                _tasks = _audit.EffectiveTasks ?? [];
                RefreshEffectiveTaskList();
            }
            var suggestions = _audit.Execution.Missions.ToDictionary(m => m.MissionExecutionId, StringComparer.OrdinalIgnoreCase);
            foreach (WindowDraft window in _windows)
                if (window.TaskScopeSource == "AUTO" && suggestions.TryGetValue(window.Id, out MissionExecution? mission))
                {
                    window.TaskIds.Clear();
                    foreach (string id in mission.SurveyTaskStableIds) window.TaskIds.Add(id);
                }
            _seedRequest = _seedRequest with { MissionWindows = _seedRequest.MissionWindows.Select(w =>
            {
                if (!suggestions.TryGetValue(w.MissionExecutionId, out MissionExecution? mission)) return w;
                WindowDraft? draft = _windows.FirstOrDefault(x => x.Id.Equals(w.MissionExecutionId, StringComparison.OrdinalIgnoreCase));
                return w with { SurveyTaskStableIds = mission.SurveyTaskStableIds,
                    SourceFiles = draft?.SourceFiles ?? w.SourceFiles, TaskScopeSource = draft?.TaskScopeSource ?? w.TaskScopeSource };
            }).ToArray() };
            PopulateLegGrid(_audit);
            if (!_defaultMatchedSelectionsApplied && !_applyingDefaultMatchedSelections &&
                _seedRequest.ConfirmedPassIds.Count == 0 && _seedRequest.ManualIncludedFlightLegIds.Count == 0)
            {
                int added = ApplyDefaultMatchedSelections();
                if (added > 0)
                {
                    _defaultMatchedSelectionsApplied = true;
                    _applyingDefaultMatchedSelections = true;
                    try
                    {
                        _seedRequest = BuildReviewedRequest();
                        RefreshReviewedExecution();
                    }
                    finally { _applyingDefaultMatchedSelections = false; }
                    return;
                }
                _defaultMatchedSelectionsApplied = true;
            }
            if (RestoreMotherLinesIfNeeded()) return;
            RefreshWindowGrid();
            _map.SetData(_displayAnalysis, _tasks, _audit.Execution, null, acceptedLegIds: CurrentAcceptedLegIds());
            _timeline.SetExecution(_audit.Execution, null, _timeZone);
            UpdateSelectedLeg();
            _status.Text = _displayAnalysis.UavSamples.Count == 0 && _analysis.UavSamples.Count > 0
                ? $"原始 UAV {_analysis.UavSamples.Count:N0} 点，当前全部被筛选排除；可使用恢复手动排除或工区筛选检查。"
                : _priorReviewStale
                ? "数据或航线任务版本已变化，旧核验决定已清除；请检查窗口及归属，将需要处理的航段添加到右侧。"
                : _analysisInputChanged
                ? "飞行窗口或任务范围已变化，结果已重新计算；请检查更新后的航段并再次确认。"
                : _audit.BlockedReasons.Count == 0
                ? $"识别到 {_audit.Execution.Legs.Count} 个航段、{_audit.Execution.Passes.Count} 条候选测线；右侧为本次处理范围。"
                : $"审核报告保留 {_audit.BlockedReasons.Count} 项提示；只有右侧已纳入的航段参与预处理。";
            // Keep an immutable, provenance-rich snapshot after each completed recognition.
            if (TrackMode) _status.Text = $"轨迹模式；主轴 {_audit.InferredAxisDegrees:0.#}°；参考 {_seedRequest.TrackSegmentation!.ReferenceGeometry.Count} 段；" + _status.Text;
            if (_spatialOnly) _status.Text = MotherLineRestorationSummary() + _status.Text;
            try
            {
                string log = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MagOps", "diagnostics", "review-performance-latest.json");
                Directory.CreateDirectory(Path.GetDirectoryName(log)!);
                File.WriteAllText(log, JsonSerializer.Serialize(new
                {
                    TimeUtc = DateTimeOffset.UtcNow, FullRecognition = recognized,
                    _auditService.RecognitionRuns, _auditService.RecognitionReuses,
                    ActivePoints = _displayAnalysis.UavSamples.Count, Legs = _audit.Execution.Legs.Count,
                    Stages = _auditService.LastStages, ReviewAndUiMilliseconds = totalTimer.Elapsed.TotalMilliseconds
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Diagnostics never block production. */ }
            // The report is descriptive only and never changes recognition or production state.
            if (recognized && _automaticAssessment.Checked && !_suppressAutomaticAssessment && IsHandleCreated)
                SaveFlightQualityAssessment(false);
            // The default-inclusion recursion reuses recognition, but still owes one report.
            else if (_applyingDefaultMatchedSelections && _automaticAssessment.Checked &&
                !_suppressAutomaticAssessment && IsHandleCreated) SaveFlightQualityAssessment(false);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidDataException or InvalidOperationException)
        {
            _audit = null;
            _legGrid.Rows.Clear();
            _reviewDecisions.Reset();
            RefreshLegSelectionViews();
            _map.Invalidate();
            _timeline.Invalidate();
            _status.Text = "请修正飞行窗口或任务范围后重新分析；当前结果不能继续。";
            MessageBox.Show(this, ex.Message, "分线分析未完成", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            if (_reviewWorkspaces?.SelectedTab == _motherLinePage && _motherLinePage is not null)
                RefreshMotherLineWorkspace();
            NotifyWorkspaceStateChanged();
        }
    }

    private static bool SameMissionWindows(IReadOnlyList<MissionTimeWindow> left,
        IReadOnlyList<MissionTimeWindow> right)
    {
        if (left.Count != right.Count) return false;
        var a = left.OrderBy(x => x.MissionExecutionId, StringComparer.OrdinalIgnoreCase).ToArray();
        var b = right.OrderBy(x => x.MissionExecutionId, StringComparer.OrdinalIgnoreCase).ToArray();
        for (int i = 0; i < a.Length; i++)
            if (!a[i].MissionExecutionId.Equals(b[i].MissionExecutionId, StringComparison.OrdinalIgnoreCase) ||
                a[i].StartUtc != b[i].StartUtc || a[i].EndUtc != b[i].EndUtc ||
                a[i].TaskScopeSource != b[i].TaskScopeSource || a[i].PhysicalFlightId != b[i].PhysicalFlightId ||
                !a[i].SourceFiles.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(b[i].SourceFiles.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase) ||
                !a[i].SurveyTaskStableIds.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .SequenceEqual(b[i].SurveyTaskStableIds.OrderBy(x => x, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase))
                return false;
        return true;
    }

    private void PopulateLegGrid(AuditProductionResult audit)
    {
        var selectedIds = _legGrid.SelectedRows.Cast<DataGridViewRow>().Select(r => r.Tag).OfType<ReviewLegRow>()
            .Select(r => r.Leg.FlightLegId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string? currentId = (_legGrid.CurrentRow?.Tag as ReviewLegRow)?.Leg.FlightLegId;
        _populatingLegGrid = true;
        try
        {
        _legGrid.Rows.Clear();
        _reviewDecisions.Reset();
        var passes = audit.Execution.Passes.ToDictionary(p => p.FlightLegId, StringComparer.OrdinalIgnoreCase);
        var taskById = _tasks.ToDictionary(t => t.SurveyTaskStableId, StringComparer.OrdinalIgnoreCase);
        var missionById = audit.Execution.Missions.ToDictionary(m => m.MissionExecutionId, StringComparer.OrdinalIgnoreCase);
        var legByPoint = audit.Execution.Legs.SelectMany(l => l.Samples.Select(p => (Key: (p.SourceFile, p.SourceRecord), LegId: l.FlightLegId)))
            .ToDictionary(x => x.Key, x => x.LegId);
        var anomaliesByLeg = new Dictionary<string, List<ProductionAnomalyInterval>>(StringComparer.OrdinalIgnoreCase);
        foreach (var anomaly in audit.ProductionAnomalies)
        foreach (string legId in anomaly.Samples.Select(p => legByPoint.GetValueOrDefault((p.SourceFile, p.SourceRecord)))
            .OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!anomaliesByLeg.TryGetValue(legId, out var list)) anomaliesByLeg[legId] = list = [];
            list.Add(anomaly);
        }
        foreach (FlightLeg leg in audit.Execution.Legs)
        {
            passes.TryGetValue(leg.FlightLegId, out SurveyLinePass? pass);
            PlannedSurveyLineTask? task = pass is null ? null : _tasks.FirstOrDefault(t => t.SurveyTaskStableId.Equals(pass.SurveyTaskStableId, StringComparison.OrdinalIgnoreCase));
            MissionExecution? mission = audit.Execution.Missions.FirstOrDefault(x => x.MissionExecutionId.Equals(leg.MissionExecutionId, StringComparison.OrdinalIgnoreCase));
            var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string keepLabel = task is null ? "（未归属）" : "（建议）";
            options[keepLabel] = "";
            foreach (PlannedSurveyLineTask option in _tasks.Where(t => (_spatialOnly && t.GeometryOrigin == "ACTUAL_TRACK") || mission?.SurveyTaskStableIds.Contains(t.SurveyTaskStableId, StringComparer.OrdinalIgnoreCase) == true)
                         .GroupBy(t => t.SurveyTaskStableId, StringComparer.OrdinalIgnoreCase)
                         .Where(g => g.Count() == 1).Select(g => g.Single()))
                options[$"{option.LineName} · {option.SortieName} · #{option.Order}"] = option.SurveyTaskStableId;
            _reviewDecisions.RestoreLeg(leg.FlightLegId, _seedRequest,
                options.Values.ToHashSet(StringComparer.OrdinalIgnoreCase));
            var decision = _reviewDecisions.Get(leg.FlightLegId);
            string choice = decision.TaskId is { } assignedTask
                ? options.FirstOrDefault(x => x.Value.Equals(assignedTask, StringComparison.OrdinalIgnoreCase)).Key ?? keepLabel
                : keepLabel;
            string classification = leg.Classification switch
            {
                _ when leg.ReviewReason.StartsWith("USER_ASSIGNED", StringComparison.Ordinal) => "人工指定",
                "SURVEY" => "测线候选",
                "SURVEY_CANDIDATE" => "疑似作业段",
                "UNRESOLVED" => "未归属",
                _ => "连接候选"
            };
            string evidence = pass is null ? FriendlyLegReason(leg.ReviewReason) :
                $"覆盖 {pass.PlannedCoverage:P0}；轴向偏差 {pass.HeadingErrorDeg:0.#}°；需确认归属" +
                (pass.MatchMethod switch { "ACTUAL_TRACK" => "；实测轨迹推断（覆盖为相对实测范围）", "ENDPOINT_PAIR" => "；两端点匹配", "SINGLE_ENDPOINT" => "；单端点与主体匹配", "BODY_TRACK" => "；主体匹配", "WHOLE_TRACK_GEOMETRY" => "；整段几何补识别", _ => "；人工指定" }) +
                (pass.FlightQuality is { Warnings.Count: > 0 } q
                    ? $"；飞行质量待核对（3米内 {q.CorePointRatio:P0}，8米外 {q.OutsideCorridorPoints} 点，速度异常 {q.SpeedOutsideRatio:P0}，有符号横距中位 {q.MedianSignedCrossTrackM:+0.0;-0.0;0.0} m" +
                      (q.Warnings.Contains("SYSTEMATIC_LATERAL_OFFSET_REVIEW") ? "，疑似连续侧向偏移" : "") + ")" : "") +
                (leg.ReviewReason.Contains("PHYSICAL_FLIGHT_BOUNDARY_UNRESOLVED", StringComparison.Ordinal)
                    ? "；飞行边界待核对" : "");
            if (leg.CandidateTaskStableIds.Count > 0)
                evidence += "；候选：" + string.Join("、", _tasks.Where(t => leg.CandidateTaskStableIds.Contains(t.SurveyTaskStableId)).Select(t => t.LineName));
            if (leg.LocalHitCandidates.Count > 0)
                evidence += "；局部命中依据：" + string.Join("；", leg.LocalHitCandidates.Select(c =>
                    $"{_tasks.FirstOrDefault(t => t.SurveyTaskStableId.Equals(c.TaskId, StringComparison.OrdinalIgnoreCase))?.LineName ?? c.TaskId}" +
                    $"（连续{c.ContinuousSpanM:0.#}m/{c.ContinuousSeconds:0.#}s，距线中位{c.MedianCrossTrackM:0.#}m，支持{c.SupportedRatio:P0}" +
                    (c.HasReverseSeed ? "，含反向命中依据" : "") + "）"));
            if (leg.ReviewReason.Contains("REFERENCE_CONNECTOR_CONFLICT")) evidence += "；与参考连接线冲突，需要复核";
            if (leg.ReviewReason.Contains("ISOLATED_OR_CROSS_AXIS")) evidence += "；孤立或交叉方向，需核对是否为联络测线/往返段";
            if (leg.TransitEvidence.Count > 0) evidence += "；疑似往返：" + string.Join("；", leg.TransitEvidence);
            if (leg.MotionEvidence.Count > 0) evidence += "；运动特征：" + string.Join("；", leg.MotionEvidence);
            if (leg.ContextEvidence.Count > 0) evidence += "；上下文：" + string.Join("；", leg.ContextEvidence);
            if (leg.RouteRoleEvidence.Count > 0) evidence += "；整体用途：" + string.Join("；", leg.RouteRoleEvidence);
            var legKeys = leg.Samples.Select(s => (s.SourceFile, s.SourceRecord)).ToHashSet();
            var relevantAnomalies = anomaliesByLeg.GetValueOrDefault(leg.FlightLegId)?.ToArray() ?? [];
            if (relevantAnomalies.Length > 0)
            {
                int excluded = relevantAnomalies.Where(a => a.ProductionDecision is "AUTO_EXCLUDE" or "USER_EXCLUDE")
                    .SelectMany(a => a.Samples).Count(s => legKeys.Contains((s.SourceFile, s.SourceRecord)));
                evidence += $"；生产异常：扣除 {excluded} 点／航段 {leg.Samples.Count} 点，" +
                    string.Join("；", relevantAnomalies.Select(a => a.Reason));
            }
            if (_seedRequest.TrackSegmentation?.AxisDegrees is { } requestedAxis && leg.Samples.Count >= 2)
            {
                double? observed = SurveyDirection.Bearing(new(leg.Samples[0].Longitude, leg.Samples[0].Latitude),
                    new(leg.Samples[^1].Longitude, leg.Samples[^1].Latitude));
                evidence += $"；指定轴 {requestedAxis:0.#}°，实飞轴偏差 " + (observed is { } h
                    ? $"{TrackSegmentationReferenceService.AxisDifference(h, requestedAxis):0.#}°" : "待判断");
                if (leg.ReviewReason == "EXPLICIT_AXIS_MISMATCH_REVIEW") evidence += "；偏离指定方向，未自动认定测线";
            }
            int row = _legGrid.Rows.Add();
            _legGrid.Rows[row].Cells[0].Value = classification;
            _legGrid.Rows[row].Cells[1].Value = task?.LineName ?? "未归属";
            var localStart = ProjectTime.Display(leg.StartUtc, _timeZone);
            var localEnd = ProjectTime.Display(leg.EndUtc, _timeZone);
            _legGrid.Rows[row].Cells[3].Value = localStart.Date == localEnd.Date
                ? $"{localStart:yyyy-MM-dd}\n{localStart:HH:mm:ss}–{localEnd:HH:mm:ss}"
                : $"{localStart:yyyy-MM-dd HH:mm:ss}\n{localEnd:yyyy-MM-dd HH:mm:ss}";
            _legGrid.Rows[row].Cells[4].Value = leg.PointCount;
            _legGrid.Rows[row].Cells[5].Value = decision.Confirmed;
            _legGrid.Rows[row].Cells[6].Value = decision.QualityStatus switch
            {
                "PASS" => "通过",
                "FAIL" => "不通过",
                _ => "待检查"
            };
            _legGrid.Rows[row].Cells[7].Value = decision.Included;
            _legGrid.Rows[row].Cells[8].Value = evidence;
            var flightStart = leg.Samples.FirstOrDefault(); var flightEnd = leg.Samples.LastOrDefault();
            double? flightBearing = flightStart is null || flightEnd is null ? null : SurveyDirection.Bearing(
                new(flightStart.Longitude, flightStart.Latitude), new(flightEnd.Longitude, flightEnd.Latitude));
            double? designBearing = task is null ? null : SurveyDirection.Bearing(task.Start, task.End);
            string FormatBearing(double? bearing) => bearing is { } value ? $"{value:0.#}°" : "待判断";
            bool axisOnly = TrackMode && task?.GeometryOrigin == "ACTUAL_TRACK";
            string direction = pass?.ExecutionDirection switch
                { "FORWARD" => axisOnly ? "沿轴正向" : "正向", "REVERSE" => axisOnly ? "沿轴负向" : "反向", _ => "" };
            double? executionBasis = designBearing is { } d ? (d + (pass?.ExecutionDirection == "REVERSE" ? 180 : 0)) % 360 : null;
            string difference = flightBearing is { } f && executionBasis is { } basis
                ? $"；执行偏差 {SurveyDirection.Difference(f, basis):0.#}°" : "";
            _legGrid.Rows[row].Cells["Direction"].Value = $"{FormatBearing(designBearing)} / {FormatBearing(flightBearing)} {direction}";
            _legGrid.Rows[row].Cells["Direction"].ToolTipText = "北为0°，东为90°；参考方向按起点到终点；" + difference +
                (axisOnly ? "；实测拟合仅提供测线轴，双向作业均允许，沿轴负向不代表异常反向。" : "");

            _legGrid.Rows[row].Cells[3].ToolTipText = $"{ProjectTime.Display(leg.StartUtc, _timeZone):yyyy-MM-dd HH:mm:ss.FFFFFFFzzz} → {ProjectTime.Display(leg.EndUtc, _timeZone):yyyy-MM-dd HH:mm:ss.FFFFFFFzzz}";
            _legGrid.Rows[row].Cells[8].ToolTipText = evidence + $"；{leg.PointCount:N0} 个样点";
            if (_legGrid.Rows[row].Cells[2] is DataGridViewComboBoxCell combo)
            {
                combo.Items.AddRange(options.Keys.Cast<object>().ToArray());
                combo.Value = choice;
            }
            _legGrid.Rows[row].Tag = new ReviewLegRow(leg, pass, options) { AxisOnlyDirection = axisOnly };
        }
        if (_legGrid.Rows.Count > 0)
        {
            var current = _legGrid.Rows.Cast<DataGridViewRow>().FirstOrDefault(r => (r.Tag as ReviewLegRow)?.Leg.FlightLegId == currentId)
                ?? _legGrid.Rows[0];
            _legGrid.CurrentCell = current.Cells[0];
            _legGrid.ClearSelection();
            foreach (DataGridViewRow row in _legGrid.Rows)
                row.Selected = row.Tag is ReviewLegRow value && selectedIds.Contains(value.Leg.FlightLegId);
            if (_legGrid.SelectedRows.Count == 0) current.Selected = true;
        }
        }
        finally { _populatingLegGrid = false; }
        RefreshLegSelectionViews();
    }

    private int ApplyDefaultMatchedSelections()
    {
        int added = 0;
        _populatingLegGrid = true;
        try
        {
        foreach (DataGridViewRow row in _legGrid.Rows)
        {
            if (row.Tag is not ReviewLegRow value || !CanBatchAddLeg(value)) continue;
            if (row.Cells["Confirmed"].Value is true && row.Cells["Include"].Value is true) continue;
            row.Cells["Confirmed"].Value = true;
            row.Cells["Include"].Value = true;
            CommitLegDecisionEdit(row);
            added++;
        }
        }
        finally { _populatingLegGrid = false; }
        return added;
    }

    private static string FriendlyLegReason(string reason) => reason switch
    {
        "AMBIGUOUS_LINE" => "存在多个测线候选，需要人工选择",
        "NO_WHOLE_LEG_MATCH" => "未找到可靠测线匹配，可指定所属测线",
        "NO_MISSION_WINDOW" => "航段不在当前飞行时间窗内",
        "ANONYMOUS_STRAIGHT_TRACK" => "连续直飞候选，尚未确定测线身份",
        "NO_ENDPOINT_OR_TRACK_MATCH" => "任务证据不足；轨迹完整保留供核验",
        "EXPLICIT_AXIS_MISMATCH_REVIEW" => "飞行方向不符合指定测线轴，保留轨迹供复核",
        _ => "未能可靠归属，可手动选择测线或保留未归属"
    };

    private void UpdateSelectedLeg()
    {
        if (_refreshingLegViews) return;
        RefreshProductionStatus();
        if (_populatingLegGrid) return;
        if (_legGrid.CurrentRow?.Tag is not ReviewLegRow value)
        { _legDetails.Text = "选择航段查看时间、方向、来源和识别依据。"; return; }
        _legDetails.Text = $"航段：{value.Leg.FlightLegId}；测线：{_legGrid.CurrentRow.Cells[1].Value}\r\n" +
            $"时间：{_legGrid.CurrentRow.Cells[3].Value}；方向：{_legGrid.CurrentRow.Cells["Direction"].Value}\r\n" +
            $"来源：{string.Join("、", value.Leg.Samples.Select(s => s.SourceFile).Distinct())}\r\n" +
            $"质量：{_legGrid.CurrentRow.Cells["QC"].Value}；{_legGrid.CurrentRow.Cells[8].Value}";
        string choice = Convert.ToString(_legGrid.CurrentRow.Cells[2].Value, CultureInfo.InvariantCulture) ?? "";
        string? selectedTaskId = value.TaskIdByOption.GetValueOrDefault(choice);
        if (string.IsNullOrWhiteSpace(selectedTaskId))
            selectedTaskId = value.Pass?.SurveyTaskStableId;
        var selectedLegs = _legGrid.SelectedRows.Cast<DataGridViewRow>().Select(r => r.Tag).OfType<ReviewLegRow>()
            .Select(r => r.Leg).ToArray();
        _map.SetData(_displayAnalysis, _tasks, _audit?.Execution, value.Leg, selectedTaskId, selectedLegs,
            CurrentAcceptedLegIds());
        _timeline.SetExecution(_audit?.Execution, value.Leg, _timeZone);
        RefreshDemHeightLegDetails();
    }

    private void CommitReview()
    {
        if (!TryPrepareProcessingRequest()) return;
        if (EmbeddedWorkspace) { ProcessingRequested?.Invoke(); return; }
        DialogResult = DialogResult.OK;
        Close();
    }

    public bool TryPrepareProcessingRequest()
    {
        if (_snapshotPending) { _status.Text = "请先在“已有核验记录”页恢复或忽略快照，再继续导出。"; return false; }
        if (_taskScopeBackup is not null) { _status.Text = "请先应用或取消任务范围修改，再生成 CSV。"; return false; }
        bool hadCurrentAnalysis = _audit is not null;
        RefreshReviewedExecution();
        if (_audit is null || !hadCurrentAnalysis || _analysisInputChanged)
        {
            DialogResult = DialogResult.None;
            return false;
        }
        if (_audit.SelectedSamples.Count == 0)
        {
            DialogResult = DialogResult.None;
            _status.Text = BuildEmptySelectionDiagnostic();
            RefreshProductionStatus();
            return false;
        }
        if (!CheckIndependentLegExport()) return false;
        ReviewedRequest = BuildReviewedRequest();
        return true;
    }

    private string BuildEmptySelectionDiagnostic()
    {
        int acceptedInUi = _acceptedLegGrid.Rows.Cast<DataGridViewRow>().Count(IsAcceptedLegRow);
        if (_audit is null || acceptedInUi == 0)
            return "尚无记录进入生产选择，已停止空 CSV 导出。请将需要处理的航段添加到右侧；未归属也可手动添加，不需要另行标记质量通过。";

        if (_audit.BlockedReasons.Any(x => x.StartsWith("SPATIAL_REVISION_UNCONFIRMED", StringComparison.Ordinal)))
            return $"右侧显示已纳入 {acceptedInUi} 段，但工区筛选指纹与当前范围不一致，旧纳入决定未参与生产选择；已停止空 CSV 导出。请重新核对筛选范围并纳入航段。";
        if (_audit.BlockedReasons.Any(x => x.StartsWith("SOURCE_REVISION_UNCONFIRMED", StringComparison.Ordinal) ||
            x.StartsWith("ROUTE_REVISION_UNCONFIRMED", StringComparison.Ordinal) ||
            x.StartsWith("RECOGNITION_VERSION_CHANGED", StringComparison.Ordinal)))
            return $"右侧显示已纳入 {acceptedInUi} 段，但输入数据、航线或识别版本与纳入决定不一致，旧决定未参与生产选择；已停止空 CSV 导出。请基于当前版本重新核验并纳入航段。";

        var includedIds = _audit.ManualIncludedFlightLegIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var includedLegs = _audit.Execution.Legs.Where(l => includedIds.Contains(l.FlightLegId)).ToArray();
        int includedPoints = includedLegs.Sum(l => l.Samples.Count);
        int anomalyExcluded = _audit.ProductionAnomalies.Sum(a => a.ExcludedSelectedPoints);
        if (includedIds.Count == 0)
            return $"右侧显示已纳入 {acceptedInUi} 段，但生产校验未接受任何人工纳入决定；已停止空 CSV 导出。请检查下方核验提示中的来源、航线、筛选范围或识别版本变化，确认当前输入后重新添加航段。";
        if (includedPoints == 0)
            return $"已有 {includedIds.Count} 段人工纳入决定通过校验，但这些航段没有可用观测点；已停止空 CSV 导出。请检查飞行窗口、空间筛选和航段边界。";
        if (anomalyExcluded >= includedPoints)
            return $"右侧纳入的航段共有 {includedPoints} 个点，但异常区间规则已将其全部排除；已停止空 CSV 导出。请检查异常区间及其恢复/排除决定。";

        string reasons = string.Join("；", _audit.BlockedReasons.Take(3));
        return $"右侧已纳入 {acceptedInUi} 段，但最终生产选择为 0 点；已停止空 CSV 导出。有效人工纳入航段 {includedIds.Count} 段、含点 {includedPoints} 个，异常扣除 {anomalyExcluded} 个。{(reasons.Length == 0 ? "请检查航段与生产选择范围。" : "核验提示：" + reasons)}";
    }

    private AuditProductionRequest BuildReviewedRequest()
    {
        return _reviewDecisions.ApplyTo(_seedRequest, Environment.UserName) with
        {
            RecognitionVersion = AuditProductionService.RecognitionVersion(_seedRequest),
            TrackSegmentationFingerprint = TrackSegmentationReferenceService.Fingerprint(_seedRequest.TrackSegmentation),
            DefaultMatchedSelectionsApplied = true,
            RequirePassConfirmation = true,
            IncludeUnconfirmedSamples = false,
            AllowActualLineRegrouping = _spatialOnly,
            RenumberActualLines = _spatialOnly,
            AllowReviewDraft = true
        };
    }

    private void SaveRequest()
    {
        if (_snapshotPending) { _status.Text = "请先在“已有核验记录”页恢复或忽略快照，再保存当前核验记录。"; return; }
        if (_taskScopeBackup is not null) { _status.Text = "请先应用或取消任务范围修改，再保存。"; return; }
        RefreshReviewedExecution();
        if (_audit is null) return;
        try
        {
            string directory = ReviewOutputDirectory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MagOps", "segmentation-reviews");
            string path = Path.Combine(directory, "MagOps_分线核验输入.json");
            if (File.Exists(path) && MessageBox.Show(this, $"核验文件已存在，是否覆盖？\n{path}", "覆盖核验输出",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            Directory.CreateDirectory(directory);
            AuditProductionRequest snapshot = BuildReviewedRequest();
            BusyOperationDialog.Run(this, "保存核验输出", "正在保存本次航段选择及核验参数…", () =>
            {
                SegmentationReviewStore.Save(path, snapshot);
                return true;
            });
            _status.Text = $"核验输出已保存：{path}";
            if (EmbeddedWorkspace) MarkWorkspaceReviewSaved();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, ex.Message, "保存失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private bool _initialShownCompleted;
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (_initialShownCompleted) return;
        _initialShownCompleted = true;
        if (_deferInitialAnalysis)
        {
            RefreshScreening();
            InitializeWindows();
            RefreshWindowGrid();
            if (!SkipInitialRecognition) AnalyzeExecution();
        }
        else if (_automaticAssessment.Checked) SaveFlightQualityAssessment(false);
        if (EmbeddedWorkspace) MarkWorkspaceReviewSaved();
    }

    private void EditFlightQualitySettings()
    {
        using var dialog = new FlightQualitySettingsDialog(_seedRequest.FlightQualityOptions ?? new(), _analysis.UavSamples);
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.SelectedOptions is null) return;
        _seedRequest = _seedRequest with { FlightQualityOptions = dialog.SelectedOptions };
        SaveFlightQualityAssessment(false);
    }

    // Capture all UI input before dispatch; reports are serialized in completion order.
    // No continuation reads controls and every completed recognition gets its own batch.
    private Task<FlightQualityAssessmentSaved> SaveFlightAssessmentAsync(string outputRoot)
    {
        if (_audit is null) throw new InvalidOperationException("请先完成分线分析。");
        _legGrid.EndEdit();
        AuditProductionRequest request = BuildReviewedRequest();
        SurveyExecutionResult execution = _audit.Execution;
        var samples = (_audit.SpatialScreening?.IncludedSamples ?? _analysis.UavSamples).ToArray();
        var tasks = _tasks.ToArray();
        string saveKey = Path.GetFullPath(outputRoot) + "|" +
            FlightQualityAssessmentService.SchemaVersion + "|" + _recognitionBatch + "|" + _audit.ProductionSnapshotFingerprint +
            "|" + ProductionSnapshotService.RequestFingerprint(request);
        // Only identical successful/in-flight snapshots are coalesced. Distinct completed
        // recognitions keep their own batches; failures can be retried.
        if (saveKey == _lastAssessmentSaveKey && _lastAssessmentSave is { IsFaulted: false, IsCanceled: false } priorSave &&
            (!priorSave.IsCompletedSuccessfully || Directory.Exists(priorSave.Result.Directory))) return priorSave;
        Task previous = _assessmentQueue;
        Task<FlightQualityAssessmentSaved> save = Task.Run(async () =>
        {
            try { await previous.ConfigureAwait(false); }
            catch { /* A failed batch must not prevent later reports. */ }
            return _assessmentService.Save(outputRoot, _analysis, samples,
                tasks, execution, request);
        });
        _assessmentQueue = save;
        _lastAssessmentSaveKey = saveKey; _lastAssessmentSave = save;
        return save;
    }

    private async void SaveFlightQualityAssessment(bool chooseDirectory)
    {
        if (_audit is null) return;
        _suppressAutomaticAssessment = true;
        try { RefreshReviewedExecution(); }
        finally { _suppressAutomaticAssessment = false; }
        if (_audit is null) return;
        int statusRevision = ++_assessmentStatusRevision;
        string root = _flightQualityOutputRoot;
        if (chooseDirectory)
        {
            using var picker = new FolderBrowserDialog
            {
                Description = "选择飞行评估保存根目录；每次生成独立批次。自动保存位置为 " + _flightQualityOutputRoot,
                UseDescriptionForTitle = true,
                SelectedPath = Directory.Exists(root) ? root : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            };
            if (picker.ShowDialog(this) != DialogResult.OK || string.IsNullOrWhiteSpace(picker.SelectedPath)) return;
            root = picker.SelectedPath;
            _flightQualityOutputRoot = root;
            _suppressAutomaticAssessment = true;
            try { RefreshReviewedExecution(); }
            finally { _suppressAutomaticAssessment = false; }
            if (_audit is null) return;
        }
        try
        {
            _status.Text = "正在后台保存飞行评估；可继续核验…";
            Task<FlightQualityAssessmentSaved> saving = SaveFlightAssessmentAsync(root);
            var saved = chooseDirectory
                ? BusyOperationDialog.Run(this, "保存飞行评估", "正在生成当前批次的飞行质量报告…", () => saving.GetAwaiter().GetResult())
                : await saving;
            if (!IsDisposed && !Disposing && statusRevision == _assessmentStatusRevision)
                _status.Text = $"飞行评估已保存：{saved.Directory}";
        }
        catch (Exception ex)
        {
            // A reporting failure must not discard recognition or prevent continuing review.
            if (IsDisposed || Disposing || statusRevision != _assessmentStatusRevision) return;
            _status.Text = "飞行评估保存失败：" + ex.Message;
            if (chooseDirectory) MessageBox.Show(this, ex.Message, "保存飞行评估失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void LoadRequest()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "载入已有 Mission 与核验决定",
            Filter = "JSON 文件|*.json",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            OfferSavedSnapshot(SegmentationReviewStore.LoadFile(dialog.FileName), dialog.FileName);

        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            MessageBox.Show(this, ex.Message, "载入核验输入失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
    private void ApplyLoadedSnapshot(AuditProductionRequest loaded)
    {
            WorkareaScreeningInput? currentScreening = _seedRequest.SpatialScreening;
            _priorReviewStale = loaded.SourceFingerprint != _analysis.Fingerprint ||
                loaded.RouteFingerprint != _routeFingerprint ||
                loaded.RecognitionVersion != AuditProductionService.RecognitionVersion(loaded) ||
                loaded.TrackSegmentationFingerprint != TrackSegmentationReferenceService.Fingerprint(loaded.TrackSegmentation) ||
                loaded.ProjectCentralMeridian is { } savedCm && _referenceCentralMeridian is { } currentCm && Math.Abs(savedCm - currentCm) > 1e-9;
            _seedRequest = loaded with
            {
                SourceFingerprint = _analysis.Fingerprint,
                RouteFingerprint = _routeFingerprint,
                ProjectCentralMeridian = _referenceCentralMeridian ?? loaded.ProjectCentralMeridian,
                QualityConfirmations = _priorReviewStale ? [] : loaded.QualityConfirmations ?? [],
                ConfirmedPassIds = _priorReviewStale ? [] : loaded.ConfirmedPassIds ?? [],
                Overrides = _priorReviewStale ? [] : loaded.Overrides ?? [],
                AnomalyDecisions = _priorReviewStale ? [] : loaded.AnomalyDecisions ?? [],
                ManualLegAssignments = _priorReviewStale ? [] : loaded.ManualLegAssignments ?? [],
                ManualIncludedFlightLegIds = _priorReviewStale ? [] : loaded.ManualIncludedFlightLegIds ?? [],
                DefaultMatchedSelectionsApplied = !_priorReviewStale && loaded.DefaultMatchedSelectionsApplied,
                MotherLineRestoration = _priorReviewStale ? null : loaded.MotherLineRestoration,
                SpatialScreening = loaded.SpatialScreening is { } savedScreening && currentScreening is not null
                    ? savedScreening with { Range = currentScreening.Range, Options = currentScreening.Options }
                    : currentScreening,
                AllowReviewDraft = true
            };
            _defaultMatchedSelectionsApplied = _seedRequest.DefaultMatchedSelectionsApplied ||
                _seedRequest.ConfirmedPassIds.Count > 0 || _seedRequest.ManualIncludedFlightLegIds.Count > 0 ||
                _seedRequest.QualityConfirmations.Count > 0 || _seedRequest.ManualLegAssignments.Count > 0;
            _audit = null;
            InitializeMotherLineRestoration();
            RefreshScreening();
            _windows.Clear();
            if (_seedRequest.MissionWindows.Count > 0)
            {
                foreach (MissionTimeWindow mission in _seedRequest.MissionWindows)
                {
                    var draft = new WindowDraft(mission.MissionExecutionId, mission.StartUtc, mission.EndUtc);
                    draft.TaskScopeSource = mission.TaskScopeSource;
                    draft.SourceFiles = mission.SourceFiles;
                    draft.PhysicalFlightId = mission.PhysicalFlightId;
                    foreach (string id in mission.SurveyTaskStableIds ?? [])
                        if (TrackMode || _tasks.Any(t => t.SurveyTaskStableId.Equals(id, StringComparison.OrdinalIgnoreCase)))
                            draft.TaskIds.Add(id);
                    _windows.Add(draft);
                }
            }
            if (_windows.Count == 0)
                foreach (MissionTimeWindow mission in new FlightWindowInferenceService().Infer(_displayAnalysis.UavSamples)
                             .Candidates.Select(x => x.Window))
                    _windows.Add(new WindowDraft(mission.MissionExecutionId, mission.StartUtc, mission.EndUtc)
                    { SourceFiles = mission.SourceFiles, TaskScopeSource = "AUTO" });
            RefreshWindowGrid();
            _includeUnresolved.Checked = _seedRequest.IncludeUnconfirmedSamples;
            AnalyzeExecution();
    }

}
