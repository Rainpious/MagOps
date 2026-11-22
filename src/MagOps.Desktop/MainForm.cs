using MagOps.Application;
using System.ComponentModel;
using MagOps.Core.Coordinates;
using MagOps.Core.Domain;
using MagOps.Core.Routes;
using MagOps.Core.SurveyPlanning;
using MagOps.Core.Analysis;
using MagOps.Infrastructure.Services;
using MagOps.Infrastructure.Storage;

namespace MagOps.Desktop;

public sealed partial class MainForm : Form
{
    private readonly JsonStore _store = new();
    private readonly WorkspaceService _workspace;
    private readonly ArtifactService _artifacts;
    private readonly FieldworkService _fieldwork;
    private readonly RouteResultService _routes;
    private readonly CoordinateRegistryService _coordinateRegistry;
    private readonly QualityService _quality;
    private readonly MigrationService _migration;
    private readonly SnapshotService _snapshots = new();
    private readonly EquipmentCatalogService _catalog;
    private readonly FolderLayoutService _folderLayout;
    private readonly RawMaterialImportService _rawImport;
    private readonly UserContextService _userContext;
    private readonly ConcaveLineRuleProfileService _concaveLineRules;
    private readonly CoordinateService _coordinateService = new();
    private readonly RoutePlanner _routePlanner = new();
    private readonly RouteExportService _routeExporter = new();
    private readonly PlySurveyLineService _plySurveyLines = new();
    private readonly KmlSurveySubdivisionService _kmlSurveySubdivision = new();
    private readonly RouteProjectService _routeProjects = new();
    private readonly RouteManifestWriter _routeManifestWriter = new();
    private readonly TextTableAnalysisService _tableAnalysis = new();
    private readonly SameHeaderTableMergeService _sameHeaderTableMerge = new();
    private readonly OasisGdbService _oasisGdb = new();
    private readonly ConcaveBoundaryLineService _concaveLineService = new();
    private readonly ConcaveSplitRoutePlanService _concaveSplitRoutePlans = new();
    private readonly DynamicNoiseService _dynamicNoise = new();
    private readonly ElevationProfileService _elevation = new();
    private readonly KmlRangeExportService _kmlRangeExporter = new();
    private readonly FieldOperationsAnalysisService _fieldOperationsAnalysis = new();
    private readonly RouteKmlImportService _routeKmlImport = new();
    private readonly FieldMagDataService _fieldMagDataService = new();
    private readonly DiurnalCorrectionExportService _diurnalCorrectionExport = new();
    private readonly FieldProductionWorkflow _fieldProductionWorkflow;
    private readonly ProcessingRunService _processingRuns;
    private readonly RoutePlanningWorkflow _routePlanningWorkflow;
    private readonly MagneticRepeatReplacementService _magneticRepeatReplacements;
    private readonly FieldDataImportService _fieldDataImport;
    private readonly DjiPilotFlightRecordService _djiFlightRecords;


    // _rootBox remains the single backing store for the selected project root.
    // It is intentionally not placed on-screen: users switch projects through
    // the compact selector below, so long paths can no longer drive toolbar width.
    private readonly TextBox _rootBox = new();
    private readonly ComboBox _projectSelector = new();
    private readonly ComboBox _headerWorkareaSelector = new();
    private readonly Label _statusLabel = new();
    private readonly TabControl _mainTabs = new();
    private readonly ToolTip _toolTip = new();
    private readonly ToolStripStatusLabel _dirtyStateStatus = new();
    private readonly Button _globalCentralMeridian = new();
    private readonly Button _openLatestGeneratedArtifact = new();
    private readonly ToolStripMenuItem _openCurrentWorkareaDirectory = new("打开当前工区目录");
    private string _lastGeneratedBusinessFile = "";
    private readonly ToolStripStatusLabel _centralMeridianStatus = new();
    private readonly ProjectWorkareaContext _projectWorkareaContext = new();
    private readonly DirtyStateCoordinator _dirtyStateCoordinator = new();
    private readonly FeatureHost _featureHost;

    private readonly Label _projectNameValue = new();
    private readonly Label _projectStatusValue = new();
    private readonly Label _projectRootValue = new();
    private readonly Label _projectCentralMeridianValue = new();
    private readonly Label _projectStatsValue = new();
    private readonly Label _projectCurrentWorkareaValue = new();
    private readonly Label _projectWorkareaCountValue = new();
    private readonly Label _projectOpenTaskCountValue = new();
    private readonly Label _projectArtifactCountValue = new();
    private readonly Label _projectIssueCountValue = new();
    private readonly Label _projectNextActionValue = new();
    private readonly Label _projectGateValue = new(); // retained for compatibility; gate UI uses panel below
    private readonly FlowLayoutPanel _projectGatePanel = new();
    private readonly Dictionary<string, Label> _projectGateLabels = new(StringComparer.OrdinalIgnoreCase);
    private readonly DataGridView _workareaGrid = GridFactory.Create();
    private readonly DataGridView _taskGrid = GridFactory.Create();
    private SplitContainer? _projectDataSplit;
    private SplitContainer? _projectDashboardSplit;
    private readonly DataGridView _equipmentGrid = GridFactory.Create();
    private readonly DataGridView _flightGrid = GridFactory.Create();
    private readonly DataGridView _baseGrid = GridFactory.Create();
    private readonly DataGridView _dailyGrid = GridFactory.Create();
    private readonly DataGridView _issueGrid = GridFactory.Create();

    private TabPage? _fieldOperationsPage;
    // Backing date used by existing daily-progress/output services. The visible
    // date is now chosen from timestamps discovered in project field data.
    private readonly DateTimePicker _fieldOperationsDate = new();
    private readonly ComboBox _fieldDateSelector = new();
    private readonly DataGridView _fieldDateGrid = GridFactory.Create();
    private readonly Label _fieldCoverageStatus = new();
    private readonly FieldMagCoveragePreviewPanel _fieldCoveragePreview = new();
    private IReadOnlyList<FieldMagDailyCoverageSummary> _fieldMagCoverage = Array.Empty<FieldMagDailyCoverageSummary>();
    private readonly NumericUpDown _fieldExpectedDailySorties = new();
    private readonly NumericUpDown _fieldAcceptedRelocations = new();
    private readonly NumericUpDown _fieldDiurnalDatumNt = new();
    private readonly CheckBox _fieldDiurnalDatumConfirmed = new();
    private readonly NumericUpDown _fieldProcessingT0Nt = new();
    private readonly ComboBox _fieldProcessingT0Mode = new();
    private readonly CheckBox _fieldSurveyLineSegmentation = new();
    private readonly TextBox _fieldDataFolder = new();
    private readonly CheckBox _fieldDataImportRecursive = new();
    private readonly Label _fieldDataFolderStatus = new();
    private readonly Label _fieldDataFingerprintStatus = new();
    private readonly DataGridView _fieldDataFileGrid = GridFactory.Create();
    private readonly DataGridView _fieldDataQcGrid = GridFactory.Create();
    private readonly RichTextBox _fieldDataSummary = new();
    private readonly Label _fieldRouteSourceStatus = new();
    private readonly Label _fieldGsm19TStatus = new();
    private readonly Label _fieldUavMagStatus = new();
    private readonly Label _fieldDjiFlightStatus = new();
    private readonly Label _fieldBaseStationStatus = new();
    private readonly Label _fieldPlannedValue = new();
    private readonly Label _fieldTodayValue = new();
    private readonly Label _fieldWorkPercentValue = new();
    private readonly Label _fieldCumulativeValue = new();
    private readonly Label _fieldTotalPercentValue = new();
    private readonly Label _fieldRemainingValue = new();
    private readonly DataGridView _fieldProgressGrid = GridFactory.Create();
    private readonly DataGridView _fieldTakeoffGrid = GridFactory.Create();
    private readonly FieldOperationsPreviewPanel _fieldOperationsPreview = new();
    private readonly RichTextBox _fieldReportSummary = new();
    private readonly RadioButton _fieldDailyReportMode = new();
    private readonly RadioButton _fieldProjectReportMode = new();
    private readonly BindingList<FieldTakeoffGridRow> _fieldTakeoffRows = new();
    private readonly List<string> _fieldManualGsm19TFiles = [];
    private readonly List<string> _fieldManualUavFiles = [];
    private readonly Dictionary<string, Form> _fieldRecordWindows = new(StringComparer.OrdinalIgnoreCase);
    private FieldMagFolderAnalysis? _fieldMagDataAnalysis;
    private FieldMagFolderAnalysis? _fieldMagRawAnalysis;
    private WorkareaScreeningInput? _fieldSpatialScreening;
    private FieldDailyAnalysisResult? _fieldDailyAnalysis;
    private FieldDailyProgressRecord? _fieldDailyProgressRecord;
    private string _fieldDataAnalysisContextKey = "";
    private string _fieldAutoAnalysisContextKey = "";
    private string _fieldProjectTimelineContextKey = "";
    private string _fieldPreferredRouteImportDirectory = "";
    private string _fieldDataFolderFullPath = "";
    private bool _updatingFieldDateSelection;
    private bool _loadingFieldOperationsSettings;

    private readonly ArtifactWorkspaceControl _artifactWorkspace;
    private List<ArtifactRecord> _artifactCache = [];
    private string _artifactBrowseScope = "";
    private string _artifactPreparedRoot = "";
    private string _lastRawImportDirectory = "";

    private readonly CoordinateWorkspaceControl _coordinateWorkspace;
    private string _coordUploadedKmlTemplatePath = "";
    private KmlRangeFormat _coordCustomKmlFormat = new();
    private bool _coordKmlCustomDialogActive;
    private string _coordAutoLoadedContextKey = "";
    private BatchConversionResult? _lastCoordinateBatch;
    private MagOps.Core.Coordinates.ProjectionParameters? _lastCoordinateProjection;

    private readonly RouteWorkspaceControl _routeWorkspace;
    private readonly HashSet<RouteSortie> _routeEditedSorties = [];
    private readonly HashSet<RouteSortie> _routeReversedSorties = [];
    private readonly HashSet<RouteSortie> _routeBoundaryAdjustedSorties = [];
    private bool _routeWorkspaceDirty;
    private readonly Dictionary<string, RouteEndpoint> _routeStartMap = new(StringComparer.OrdinalIgnoreCase);
    private RouteLineInference? _routeInference;
    private string _routeDetectedMode = "projected";
    private RoutePlanResult? _routePlan;
    private string _routeLoadedFingerprint = "";
    private string _lastGeneratedRoutePlyPath = "";
    private string _routeCoveragePlyPath = "";
    private string _routeTerrainLoadedProjectRoot = "";
    private KmlSurveySubdivisionPreparation? _routeSubdivisionPreparation;
    private string _routeSubdivisionPreparedFingerprint = "";
    private KmlSurveySubdivisionResolvedChild? _routeActivePartition;
    private readonly Dictionary<string, PartitionRouteRuntimeContext> _routePartitionContexts =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ExcelWorkspaceControl _excelWorkspace;

    private readonly TextBox _oasisGdbA = new();
    private readonly TextBox _oasisGdbB = new();
    private readonly TextBox _oasisPython = new();
    private readonly TextBox _oasisOutputName = new();
    private readonly Label _oasisStatus = new();
    private readonly DataGridView _oasisHeaderGrid = GridFactory.Create();
    private readonly TextBox _oasisInfo = new();
    private OasisGdbInspection? _oasisInspectionA;
    private OasisGdbInspection? _oasisInspectionB;
    private OasisGdbMappingPlan? _oasisMappingPlan;
    private Dictionary<string, string?> _oasisManualMappings = new(StringComparer.OrdinalIgnoreCase);

    private readonly AnalysisWorkspaceControl _analysisWorkspace;
    private string _workareaContextLoadedRoot = "";
    private readonly List<FlowLayoutPanel> _workareaContextSelectors = [];
    private bool _syncingWorkareaContexts;
    private bool _syncingProjectSelector;
    private bool _syncingHeaderWorkareaSelector;
    private string _lastUiLayoutAuditSignature = "";

    public MainForm()
    {
        _workspace = new WorkspaceService(_store);
        _artifacts = new ArtifactService(_store, _workspace);
        _processingRuns = new ProcessingRunService(_store, _workspace, _artifacts);
        _fieldProductionWorkflow = new FieldProductionWorkflow(
            new AuditProductionService(),
            new FieldProductionOutputAdapter(_diurnalCorrectionExport),
            new FieldProductionArtifactsAdapter(_artifacts),
            new FieldProductionRunsAdapter(_processingRuns));
        _routes = new RouteResultService(_store, _workspace, _artifacts);
        _routePlanningWorkflow = new RoutePlanningWorkflow(
            _routePlanner,
            _concaveSplitRoutePlans,
            new RouteExecutionSegmentPlanner(),
            _routeProjects,
            _routeExporter,
            _artifacts,
            _routeManifestWriter,
            _routes,
            _processingRuns);
        _fieldwork = new FieldworkService(_store, _workspace);
        _fieldDataImport = new FieldDataImportService(_store);
        _magneticRepeatReplacements = new MagneticRepeatReplacementService(_store);
        _djiFlightRecords = new DjiPilotFlightRecordService(_store);
        _coordinateRegistry = new CoordinateRegistryService(_store, _workspace);
        _quality = new QualityService(_store, _workspace, _fieldwork, _artifacts, _routes);
        _migration = new MigrationService(_store);
        _catalog = new EquipmentCatalogService(_store);
        _folderLayout = new FolderLayoutService();
        _rawImport = new RawMaterialImportService(_folderLayout);
        _userContext = new UserContextService(_store);
        _concaveLineRules = new ConcaveLineRuleProfileService(_store);
        _featureHost = new FeatureHost(_mainTabs);
        InitializeUiArchitecture();
        _analysisWorkspace = new AnalysisWorkspaceControl(
            new AnalysisWorkspaceActions(
                RunGroupAverage: path =>
                    RunUi("分组平均", () => RunGroupAverage(path)),
                RunTableSplit: path =>
                    RunUi("拆分表格", () => RunTableSplit(path)),
                RunCsvMerge: paths =>
                    RunUi("合并 CSV", () => RunCsvMerge(paths)),
                RunDynamicNoise: path =>
                    RunUi("动态噪声", () => RunDynamicNoise(path)),
                RunElevationKml: path =>
                    RunUi("高程转 KML", () => RunElevationKml(path))));
        _excelWorkspace = new ExcelWorkspaceControl(
            sourceSelected: (slot, path) =>
                RunUi(
                    slot == ExcelMergeSourceSlot.First
                        ? "选择表 A"
                        : "选择表 B",
                    () => SetExcelMergeSource(
                        slot,
                        path)),
            checkHeaders: () =>
                RunUi(
                    "检查表头",
                    RefreshExcelMergeComparison),
            merge: () =>
                RunUiAsync(
                    "合并表格",
                    MergeExcelTablesAsync));

        _artifactWorkspace = new ArtifactWorkspaceControl(
            new ArtifactWorkspaceActions(
                ChooseAndImportRawMaterials: () =>
                    RunUiAsync(
                        "归档原始资料",
                        ChooseAndImportRawMaterialsAsync),
                ImportRawMaterialPaths: paths =>
                    RunUiAsync(
                        "归档原始资料",
                        () => ImportRawMaterialPathsAsync(paths)),
                OpenLastRawImportFolder: () =>
                    RunUi(
                        "打开最近归档",
                        OpenLastRawImportFolder),
                InspectPartialImports: () =>
                    RunUi(
                        "检查未完成导入",
                        InspectAndCleanupPartialImports),
                OpenFolderLayout: () =>
                    RunUi(
                        "打开目录规则",
                        OpenFolderLayoutConfiguration),
                ApplyFilters: ApplyArtifactFilters,
                RefreshArtifacts: () =>
                    RunUi(
                        "刷新资料台账",
                        RefreshArtifacts),
                EditCurrentArtifact: () =>
                    RunUi(
                        "编辑资料元数据",
                        EditArtifact),
                ShowCurrentDetail: () =>
                    RunUi(
                        "查看资料详情",
                        () => ShowArtifactDetailDialog(CurrentArtifact())),
                RevealCurrentArtifact: () =>
                    RunUi(
                        "在资源管理器中定位资料",
                        RevealCurrentArtifact),
                RefreshScopeList: RefreshArtifactScopeList,
                ScopeChanged: key =>
                {
                    _artifactBrowseScope = key;
                    ApplyArtifactFilters();
                },
                SetBrowseWorkareaAsCurrent: () =>
                    RunUi(
                        "设为当前工作工区",
                        SetBrowseWorkareaAsCurrent),
                EditCurrentNotes: () =>
                    RunUi(
                        "编辑资料备注",
                        () =>
                        {
                            ArtifactRecord row = CurrentArtifact();
                            EditArtifactProperty(
                                row,
                                nameof(ArtifactRecord.Notes));
                        }),
                AssignCurrentWorkarea: () =>
                    RunUi(
                        "设置资料工区归属",
                        AssignArtifactWorkarea),
                SelectionChanged: RefreshArtifactDetail,
                RevealArtifact: row =>
                    RunUi(
                        "在资源管理器中定位资料",
                        () => RevealArtifactInExplorer(row)),
                ToggleSelected: row =>
                    RunUi(
                        "切换资料选用",
                        () => ToggleArtifactSelected(row)),
                EditProperty: (row, property) =>
                    RunUi(
                        "编辑资料属性",
                        () => EditArtifactProperty(row, property)),
                ShowDetail: row =>
                    RunUi(
                        "查看资料详情",
                        () => ShowArtifactDetailDialog(row))));

        _coordinateWorkspace = new CoordinateWorkspaceControl(
            new CoordinateWorkspaceActions(
                ReloadProjectResult: () =>
                    RunUi(
                        "读取项目坐标成果",
                        () => TryAutoLoadCoordinateProjectResult(
                            force: true)),
                ConvertCoordinates: () =>
                    RunUi(
                        "坐标转换",
                        ConvertCoordinates),
                BindBoundary: () => RunUi("确认工区边界", BindCoordinateBoundary),
                UseWorkareaRange: () => RunUi("设置当前工区范围", UseCoordinateWorkareaRange),
                SelectKmlTemplate: path =>
                    RunUi(
                        "选择 KML 模板",
                        () => SelectCoordinateKmlTemplate(
                            path)),
                EditKmlFormat: () =>
                    RunUi(
                        "编辑 KML 格式",
                        EditCoordinateKmlFormat),
                CopyCoordinateColumns: mode =>
                    CopyCoordinateColumns(
                        mode),
                ExportCoordinates: () =>
                    RunUi(
                        "导出坐标",
                        ExportCoordinates),
                ExportKmlRange: () =>
                    RunUi(
                        "导出 KML 范围",
                        ExportCoordinateKmlRange),
                ExportOasisPly: () =>
                    RunUi(
                        "导出 Oasis PLY",
                        ExportOasisPly),
                RefreshCoordinateRecords: () =>
                    RunUi(
                        "刷新坐标记录",
                        RefreshCoordinateRecords),
                RegisterCoordinates: () =>
                    RunUi(
                        "登记坐标",
                        RegisterCoordinates),
                ImportCoordinateCsv: path =>
                    RunUi(
                        "导入坐标 CSV",
                        () => ImportCoordinateCsv(
                            path)),
                RegisterCrsDefinition: () =>
                    RunUi(
                        "登记 CRS",
                        RegisterCrsDefinition),
                AuditSpatialFile: path =>
                    RunUi(
                        "空间文件审计",
                        () => AuditSpatialFile(
                            path)),
                ConvertSpatialFile: path =>
                    RunUi(
                        "KML/GPX 互转",
                        () => ConvertSpatialFile(
                            path)),
                CalculateIgrfField: () =>
                    RunUi(
                        "IGRF 主场计算",
                        CalculateIgrfMainField),
                KmlChoiceChanged: choice =>
                    OnCoordinateKmlChoiceChanged(
                        choice)), SaveAccessPlanning, AccessCsvProjection);

        _routeWorkspace = new RouteWorkspaceControl(
            new RouteWorkspaceActions(
                ChoosePly: path =>
                    RunUi(
                        "选择 PLY 边界",
                        () => ChooseRoutePly(
                            path)),
                RefreshKmlDirectory: () =>
                    RefreshRouteKmlDirectorySelector(),
                SelectKmlFromDirectory: path =>
                    RunUi(
                        "切换同目录 KML",
                        () => SelectRouteKmlFromDirectorySelector(
                            path)),
                ChooseKml: path =>
                    RunUi(
                        "选择 KML 边界",
                        () => ChooseRouteKml(
                            path)),
                BindBoundary: path => RunUi("确认工区边界", () => BindRouteBoundary(path)),
                LoadProjectKml: () =>
                    RunUi(
                        "读取项目 KML",
                        () => TryAutoLoadRouteKml(
                            force: true)),
                ChooseCsv: path =>
                    RunUi(
                        "航线载入",
                        () => ChooseRouteCsv(
                            path)),
                RunConcaveRepair: path =>
                    RunUi(
                        "凹形边界断线处理",
                        () => RunConcaveBoundaryLineRepairFromPath(
                            path)),
                RefreshSubdivisionPreview: () =>
                    RunUi(
                        "KML 分区预览",
                        RefreshRouteSubdivisionPreview),
                SplitKml: () =>
                    RunUi(
                        "KML 分区",
                        SplitRouteKmlIntoSubareas),
                ApplyCornerStart: () =>
                    ApplyRouteCornerStart(),
                ApplyAdvancedSettings: () =>
                    RunUi(
                        "应用航线高级参数",
                        ApplyRouteAdvancedSettings),
                PartitionOverviewSettingsChanged: edit =>
                    RunUi(
                        "调整分区航线参数",
                        () => ApplyPartitionOverviewSettings(edit)),
                OpenPartitionFromOverview: code =>
                    RunUi(
                        "打开分区精细编辑",
                        () => OpenPartitionFromOverview(code)),
                GenerateFromSelectedSource: () =>
                    RunUi(
                        "生成航线",
                        GenerateRouteFromSelectedSource),
                SplitOverlongExecutionSegment: () =>
                    RunUi(
                        "切分超长测线",
                        SplitOverlongRouteLine),
                CutLineDrawn: (line, start, end) =>
                    RunUi(
                        "应用手绘切割线",
                        () => ApplyDrawnExecutionCut(line, start, end)),
                MoveLastTwoLines: () =>
                    RunUi(
                        "调整架次边界",
                        MoveLastTwoRouteLines),
                MoveFirstTwoLines: () =>
                    RunUi(
                        "调整架次边界",
                        MoveFirstTwoRouteLines),
                EditCurrentSortie: () =>
                    RunUi(
                        "编辑航点",
                        EditCurrentRouteSortie),
                ReverseCurrentSortie: () =>
                    RunUi(
                        "更换测线方向",
                        ReverseCurrentRouteSortie),
                Regenerate: () =>
                    RunUi(
                        "生成航线",
                        () => GenerateRoutePlanCore(checkTerrain: true)),
                ChooseOutput: () =>
                    ChooseRouteOutput(),
                ChooseTerrain: path =>
                    RunUi(
                        "选择 KML 地形",
                        () => ChooseRouteTerrainDem(
                            path)),
                DownloadTerrain: () => RunUi("下载地形", DownloadAw3d30Terrain),
                ManageTerrainLibrary: () => RunUi("设置默认地形库", ConfigureDefaultTerrainLibrary),
                MatchTerrainTiles: () => RunUi("匹配地形瓦片", MatchRouteTerrainTiles),
                PackageTerrainTiles: () => RunUi("打包地形瓦片", PackageRouteTerrainTiles),
                ExportAllAndRegister: () =>
                    RunUi(
                        "统一导出航线",
                        ExportRouteAllAndRegister),
                ExportSubset: (kml, csv, kmz) =>
                    RunUi(
                        kml
                            ? "导出全部 KML"
                            : csv
                                ? "导出全部 CSV"
                                : "导出航线",
                        () => ExportRouteSubset(
                            kml,
                            csv,
                            kmz)),
                ExportCurrentSubset: (kml, csv, kmz) =>
                    RunUi(
                        kml
                            ? "导出当前 KML"
                            : csv
                                ? "导出当前 CSV"
                                : "导出当前航线",
                        () => ExportCurrentRouteSubset(
                            kml,
                            csv,
                            kmz)),
                SaveProject: () =>
                    RunUi(
                        "保存航线工程",
                        SaveRouteProject),
                LoadProject: path =>
                    RunUi(
                        "载入航线工程",
                        () => LoadRouteProject(
                            path)),
                SortieSelectionChanged: () => { RefreshRoutePointGrid(); UpdateSiteSelectionNotice(); },
                ResolveSortieState: sortie =>
                    GetRouteSortieEditState(
                        sortie),
                EndpointClicked: endpoint =>
                    SetStatus(
                        $"已选择起始点：{endpoint.Label} / {endpoint.Pid}"),
                MapPointClicked: (x, y) =>
                {
                    if (!LaunchSitePlanning.Enabled || _routePlan is null) return;
                    if (_routePlan.AccessPlanning?.Sites.Count > 0)
                    {
                        SetStatus("当前使用候选起降点；请在“起降航程 → 管理道路与起降点”中移动或新增，再指定当前架次。");
                        return;
                    }
                    _routePlan.Options.TakeoffX = x;
                    _routePlan.Options.TakeoffY = y;
                    _routePlan.RoundTripDistanceM = RouteCoordinateMath.RoundTripDistance(
                        x, y, _routePlan.Sorties, _routePlan.Mode, _routePlan.CentralMeridian);
                    SetRouteWorkspaceDirty(true);
                    RefreshRoutePlanUi();
                    _routeWorkspace!.InvalidateRoutePreview();
                    SetStatus($"已选择自由起飞点：X={x:0.###}，Y={y:0.###}；往返总距离 {_routePlan.RoundTripDistanceM.Value / 1000d:0.###} km");
                },
                ManageAccessPlanning: () => RunUi("踏勘轨迹", OpenAccessPlanning),
                LockSortieSite: () => RunUi("指定架次起降点", LockCurrentSortieSite),
                ExportAccessAssessment: () => RunUi("导出起降航程评估", ExportAccessAssessment),
                SubdivisionCutsChanged: (cuts, previewCount) =>
                    SetStatus(
                        $"KML 分区预览：当前 {cuts.Count} 条切分线，" +
                        $"可形成 {previewCount} 个预览子区；" +
                        "可继续拖动调整后执行分区。"),
                FormatPathForDisplay: path =>
                    FormatRoutePathForDisplay(
                        path)));

        WireSiteEditor();
        _routeWorkspace.AlignBoundaryRequested += () => RunUi("沿边定向", AlignRouteToBoundary);
        _routeWorkspace.EstimateRequested += () => RunUi("设计工作量", ShowRouteWorkEstimate);
        _routeWorkspace.CheckLinesRequested += () => RunUi("检查线管理", ManageRouteCheckLines);
        Text = $"航磁项目管理平台 / MagOps {MagOps.Core.AppConstants.AppVersion}";
        Width = 1440;
        Height = 900;
        MinimumSize = new Size(640, 480);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        KeyPreview = true;

        BuildShell();
        BuildProjectTab();
        BuildArtifactTab();
        BuildFieldworkTab();
        BuildCoordinateTab();
        BuildRouteTab();
        BuildFieldOperationsTab();
        BuildSegmentationProcessingTab();
        BuildReportTab();
        BuildQualityCheckTab();
        BuildExcelTab();
        BuildOasisTab();
        BuildAnalysisTab();
        UiTheme.Apply(this);

        Shown += (_, _) =>
        {
            UiLayout.FitFormToWorkingArea(this);
            WindowState = FormWindowState.Maximized;
            BeginInvoke((Action)(() =>
            {
                NormalizeUiLayout();
                RefreshProjectSelector();
                TryRestoreLastProjectAndRoute();
                _projectSelector.Focus();
                _toolTip.SetToolTip(_projectRootValue, Root);
                RefreshWorkareaContextSelectors();
            }));
        };
        DpiChanged += (_, _) => BeginInvoke((Action)NormalizeUiLayout);
        SizeChanged += (_, _) =>
        {
            if (WindowState != FormWindowState.Minimized)
                NormalizeUiLayout();
        };
        FormClosing += (_, e) =>
        {
            if (e.CloseReason is CloseReason.UserClosing or CloseReason.MdiFormClosing &&
                !ConfirmContextSwitch("关闭 MagOps"))
            {
                e.Cancel = true;
            }
        };
        KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.F5 && ProjectPaths.HasProject(Root))
            {
                e.SuppressKeyPress = true;
                RunUi("刷新", RefreshAll);
            }
            else if (e.Control && e.KeyCode == Keys.O)
            {
                e.SuppressKeyPress = true;
                ChooseProject();
            }
            else if (e.Control && e.Shift && e.KeyCode == Keys.S &&
                     ProjectPaths.HasProject(Root))
            {
                e.SuppressKeyPress = true;
                await RunUiAsync("扫描资料", () => ScanArtifactsAsync(false));
            }
        };
    }

    private string Root => _rootBox.Text.Trim();

    private void BuildShell()
    {
        var shell = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // Project and current-workarea context share the top row.
        // Feature pages therefore do not need to repeat long absolute paths.
        var projectBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            BackColor = UiTheme.Surface,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = true,
            AutoScroll = false,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = Padding.Empty,
            Padding = new Padding(8, 6, 8, 4)
        };
        projectBar.Controls.Add(new Label
        {
            Text = "MagOps",
            AutoSize = true,
            Font = new Font(SystemFonts.MessageBoxFont?.FontFamily ?? FontFamily.GenericSansSerif, 12F, FontStyle.Bold),
            ForeColor = UiTheme.Accent,
            Margin = new Padding(3, 6, 18, 3)
        });
        projectBar.Controls.Add(new Label
        {
            Text = "项目",
            AutoSize = true,
            Margin = new Padding(3, 8, 8, 3)
        });

        _projectSelector.DropDownStyle = ComboBoxStyle.DropDownList;
        _projectSelector.Width = 250;
        _projectSelector.MinimumSize = new Size(220, 0);
        _projectSelector.Margin = new Padding(0, 3, 6, 3);
        _projectSelector.SelectedIndexChanged += (_, _) =>
        {
            if (_syncingProjectSelector ||
                _projectSelector.SelectedItem is not ProjectChoice choice ||
                string.IsNullOrWhiteSpace(choice.Root))
            {
                return;
            }

            RunUi(
                "切换项目",
                () =>
                {
                    if (!OpenProjectRoot(
                            choice.Root,
                            remember: true))
                    {
                        RefreshProjectSelector();
                    }
                });
        };
        projectBar.Controls.Add(_projectSelector);

        var newProject = Button("新建项目", (_, _) => RunUi("新建项目", CreateNewProject));
        UiComponents.SetButtonRole(newProject, UiButtonRole.Primary);
        newProject.Margin = new Padding(0, 2, 12, 2);
        projectBar.Controls.Add(newProject);
        projectBar.Controls.Add(Button("工作目录…", (_, _) => RunUi("工作目录设置", () => {
            using var dialog = new WorkspaceSettingsDialog(); dialog.ShowDialog(this);
        })));
        var saveElsewhere = new CheckBox { Text = "另存到…", AutoSize = true, Margin = new Padding(3, 8, 8, 3) };
        saveElsewhere.CheckedChanged += (_, _) => _saveElsewhere = saveElsewhere.Checked;
        _toolTip.SetToolTip(saveElsewhere, "勾选后导出时选择自定义位置；取消恢复自动归档。草稿仍保存在所属工程。");
        projectBar.Controls.Add(saveElsewhere);

        projectBar.Controls.Add(new Label
        {
            Text = "当前工区",
            AutoSize = true,
            Margin = new Padding(3, 8, 8, 3)
        });

        _headerWorkareaSelector.DropDownStyle = ComboBoxStyle.DropDownList;
        _headerWorkareaSelector.Width = 235;
        _headerWorkareaSelector.MinimumSize = new Size(205, 0);
        _headerWorkareaSelector.Margin = new Padding(0, 3, 6, 3);
        _headerWorkareaSelector.SelectedIndexChanged += (_, _) =>
        {
            if (_syncingHeaderWorkareaSelector ||
                _headerWorkareaSelector.SelectedItem is not WorkareaChoice choice)
            {
                return;
            }

            if (!SetCurrentWorkareaRelativePath(choice.Path))
                return;

            if (ProjectPaths.HasProject(Root))
                ApplyArtifactFilters();
        };
        projectBar.Controls.Add(_headerWorkareaSelector);

        var newWorkarea = Button("新建工区", (_, _) => RunUi("新建工区", CreateNewWorkarea));
        UiComponents.SetButtonRole(newWorkarea, UiButtonRole.Primary);
        newWorkarea.Margin = new Padding(0, 2, 6, 2);
        projectBar.Controls.Add(newWorkarea);

        var openWorkarea = Button(
            "打开工区文件夹…",
            (_, _) => RunUi("打开工区文件夹", OpenExistingWorkareaFolder),
            UiButtonRole.Subtle);
        openWorkarea.Margin = new Padding(0, 2, 12, 2);
        _toolTip.SetToolTip(
            openWorkarea,
            "直接选择磁盘上的工区文件夹。可自动向上识别项目根目录，并用于项目/上级目录改名后的重新定位。");
        projectBar.Controls.Add(openWorkarea);

        var browseProject = Button("选择其他项目…", (_, _) => ChooseProject());
        UiComponents.SetButtonRole(browseProject, UiButtonRole.Subtle);
        browseProject.Margin = new Padding(0, 2, 6, 2);
        projectBar.Controls.Add(browseProject);

        var refreshProject = Button("刷新", (_, _) => RunUi("刷新", RefreshAll), UiButtonRole.Subtle);
        refreshProject.Margin = new Padding(0, 2, 6, 2);
        projectBar.Controls.Add(refreshProject);

        var scanArtifacts = Button("扫描资料", async (_, _) =>
            await RunUiAsync("扫描资料", () => ScanArtifactsAsync(false)));
        scanArtifacts.Margin = new Padding(0, 2, 6, 2);
        projectBar.Controls.Add(scanArtifacts);

        _openLatestGeneratedArtifact.Text = "定位最近成果";
        _openLatestGeneratedArtifact.AutoSize = true;
        _openLatestGeneratedArtifact.Enabled = false;
        _openLatestGeneratedArtifact.Margin = new Padding(0, 2, 6, 2);
        _openLatestGeneratedArtifact.Click += (_, _) =>
            RunUi("定位最近成果", OpenLatestGeneratedArtifact);
        UiComponents.SetButtonRole(_openLatestGeneratedArtifact, UiButtonRole.Subtle);
        projectBar.Controls.Add(_openLatestGeneratedArtifact);

        var projectToolsMenu = new ContextMenuStrip();
        _openCurrentWorkareaDirectory.Enabled = false;
        _openCurrentWorkareaDirectory.ToolTipText = "请先选择当前工区。";
        _openCurrentWorkareaDirectory.Click += (_, _) =>
            RunUi("打开当前工区目录", OpenCurrentWorkareaDirectory);
        projectToolsMenu.Items.Add(_openCurrentWorkareaDirectory);
        projectToolsMenu.Items.Add(new ToolStripSeparator());
        projectToolsMenu.Items.Add("初始化当前目录", null, (_, _) =>
            RunUi("初始化项目", InitializeProject));
        projectToolsMenu.Items.Add("完整哈希扫描", null, async (_, _) =>
            await RunUiAsync("完整哈希扫描", () => ScanArtifactsAsync(true)));
        projectToolsMenu.Items.Add(new ToolStripSeparator());
        projectToolsMenu.Items.Add("修改项目显示名称…", null, (_, _) =>
            RunUi("修改项目显示名称", RenameCurrentProjectDisplayName));
        projectToolsMenu.Items.Add("修改当前工区显示名称…", null, (_, _) =>
            RunUi("修改工区显示名称", RenameCurrentWorkareaDisplayName));
        projectToolsMenu.Items.Add("项目与工区目录管理…", null, (_, _) => RunUi("目录管理", () =>
        {
            if (NavigateFeature(FeatureIds.ProjectDashboard)) _projectWorkspaceTabs.SelectedIndex = 1;
        }));
        projectToolsMenu.Items.Add(new ToolStripSeparator());
        projectToolsMenu.Items.Add("导入航线结果…", null, (_, _) =>
            RunUi("导入航线结果", () => ImportRouteResult()));
        projectToolsMenu.Items.Add("生成交接摘要", null, (_, _) =>
            RunUi("生成交接摘要", WriteHandoff));

        var projectTools = Button("项目工具 ▾", (_, _) => { }, UiButtonRole.Subtle);
        projectTools.Margin = new Padding(0, 2, 6, 2);
        projectTools.ContextMenuStrip = projectToolsMenu;
        projectTools.Click += (_, _) =>
            projectToolsMenu.Show(projectTools, new Point(0, projectTools.Height));
        FileDropSupport.EnableSingle(
            projectTools,
            path => RunUi("导入航线结果", () => ImportRouteResult(path)),
            path => FileDropSupport.HasExtension(path, ".json"));
        _toolTip.SetToolTip(projectTools,
            "低频项目操作；也可将航线结果 JSON 拖放到此按钮导入。");
        projectBar.Controls.Add(projectTools);
        _globalCentralMeridian.Text = "中央经线：未确认 · 设置…";
        _globalCentralMeridian.AutoSize = true;
        _globalCentralMeridian.Margin = new Padding(0, 2, 6, 2);
        _globalCentralMeridian.Click += (_, _) => RunUi("修改工区中央经线", EditWorkareaCentralMeridian);
        projectBar.Controls.Add(_globalCentralMeridian);


        _mainTabs.Dock = DockStyle.Fill;
        _mainTabs.Margin = Padding.Empty;
        _mainTabs.Multiline = false;
        _mainTabs.SizeMode = TabSizeMode.Normal;

        shell.Controls.Add(projectBar, 0, 0);
        shell.Controls.Add(_mainTabs, 0, 1);

        var status = new StatusStrip
        {
            Dock = DockStyle.Fill,
            SizingGrip = false
        };
        status.Items.Add(new ToolStripStatusLabel("状态："));
        var host = new ToolStripControlHost(_statusLabel)
        {
            AutoSize = true,
            Margin = new Padding(0, 3, 0, 0)
        };
        status.Items.Add(host);
        status.Items.Add(new ToolStripStatusLabel
        {
            Spring = true
        });
        _dirtyStateStatus.Text = "";
        _dirtyStateStatus.Visible = false;
        _dirtyStateStatus.ForeColor = UiTheme.Warning;
        status.Items.Add(_dirtyStateStatus);
        status.Items.Add(_centralMeridianStatus);
        shell.Controls.Add(status, 0, 2);

        Controls.Add(shell);
    }

    private void NormalizeUiLayout()
    {
        _mainTabs.Dock = DockStyle.Fill;
        _mainTabs.BringToFront();

        foreach (TabPage page in _mainTabs.TabPages)
        {
            if (page.Controls.Count == 1)
            {
                Control root = page.Controls[0];
                if (root.Dock == DockStyle.None)
                    root.Dock = DockStyle.Fill;
            }
        }

        _routeWorkspace.NormalizeLayout();
        NormalizeSplitter(_projectDataSplit, vertical: true, preferredRatio: 0.46);
        NormalizeSplitter(_projectDashboardSplit, vertical: false, preferredRatio: 0.80);
        _artifactWorkspace.NormalizeLayout();

        UiLayout.Normalize(this);
        PerformLayout();

        // Runtime regression guard: layout problems are recorded once per unique
        // signature instead of silently reappearing in later versions/DPI settings.
        IReadOnlyList<string> issues = UiLayout.Audit(this);
        string signature = string.Join("\n", issues);
        if (issues.Count > 0 && !signature.Equals(_lastUiLayoutAuditSignature, StringComparison.Ordinal))
        {
            _lastUiLayoutAuditSignature = signature;
            UiLayout.AuditAndLog(
                this,
                Directory.Exists(Root) ? Root : null,
                "UI_LAYOUT_GUARD");
        }
    }

    private static void NormalizeSplitter(
        SplitContainer? splitter,
        bool vertical,
        double preferredRatio)
    {
        if (splitter is null || splitter.IsDisposed)
            return;

        int available = vertical
            ? splitter.ClientSize.Width - splitter.SplitterWidth
            : splitter.ClientSize.Height - splitter.SplitterWidth;
        if (available <= 0)
            return;

        int min1 = vertical
            ? Math.Min(180, Math.Max(100, available / 5))
            : Math.Min(140, Math.Max(70, available / 4));
        int min2 = vertical
            ? Math.Min(300, Math.Max(140, available / 4))
            : Math.Min(120, Math.Max(70, available / 4));
        if (available <= min1 + min2)
            return;

        int desired = (int)Math.Round(available * preferredRatio);
        int max = available - min2;
        desired = Math.Clamp(desired, min1, max);

        int current = splitter.SplitterDistance;
        if (current < min1 || current > max ||
            current < available * 0.16 || current > available * 0.72)
        {
            splitter.SplitterDistance = desired;
        }
    }

    private void ChooseProject()
    {
        ChooseExistingProject();
    }

    private void InitializeProject()
    {
        RequireRootDirectory();
        var values = PromptDialog.Show(this, "初始化项目",
        [
            new("alias", "项目显示名称", new DirectoryInfo(Root).Name)
        ],
        note: "初始化只创建 00_项目管理/magops 管理数据；01～10/99 业务目录在首次实际写文件时按需建立。");
        if (values is null)
            return;

        WorkspaceRecord workspace = _workspace.Initialize(Root, values["alias"]);
        RememberProject(Root, workspace.DisplayName);
        RefreshProjectSelector();
        RefreshAll();
        SetStatus("项目管理数据已初始化。");
    }

    private void RefreshAll()
    {
        EnsureInitialized();
        RefreshProject();
        RefreshArtifacts();
        RefreshFieldwork();
        RefreshCoordinateRecords();
        RefreshWorkareaContextSelectors();
        RefreshProjectSelector();
        RefreshRouteCentralMeridianFromProject();
        TryLoadProjectTerrainForRoute();
        SetStatus($"已刷新：{DateTime.Now:HH:mm:ss}");
    }

    private async Task ScanArtifactsAsync(bool withHash)
    {
        EnsureInitialized();
        string projectRoot = Root;
        long revision = _projectWorkareaContext.Current.Revision;
        bool wasEnabled = Enabled;
        bool wasWaitCursor = UseWaitCursor;
        Enabled = false;
        UseWaitCursor = true;
        SetStatus(withHash ? "正在执行完整哈希扫描…" : "正在扫描资料…");
        try
        {
            ArtifactScanResult result = await Task.Run(() =>
                _artifacts.Scan(projectRoot, withHash));
            if (IsDisposed || Disposing || _projectWorkareaContext.Current.Revision != revision ||
                !Root.Equals(projectRoot, StringComparison.OrdinalIgnoreCase)) return;
            RefreshArtifacts();
            RefreshProject();
            SetStatus(
                $"资料扫描完成：{result.ScannedCount} 个现存文件，" +
                $"{result.MissingCount} 个缺失记录。");
        }
        finally
        {
            if (!IsDisposed)
            {
                UseWaitCursor = wasWaitCursor;
                Enabled = wasEnabled;
            }
        }
    }

    private void ImportRouteResult(string? selectedPath = null)
    {
        EnsureInitialized();
        var fileHistory = FileInteractions.For(this, "route-manifest");
        string filePath = selectedPath?.Trim() ?? "";
        if (filePath.Length == 0)
        {
            using var dialog = new OpenFileDialog
            {
                Filter = "航线结果清单 (*.json)|*.json|所有文件 (*.*)|*.*",
                Title = "选择航线工具结果清单 JSON"
            };
            fileHistory.Prepare(dialog);
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            filePath = dialog.FileName;
        }

        if (!File.Exists(filePath))
            throw new FileNotFoundException("航线结果清单不存在。", filePath);
        if (!FileDropSupport.HasExtension(filePath, ".json"))
            throw new InvalidOperationException("航线结果清单必须是 JSON 文件。");

        var confirm = MessageBox.Show(this,
            "如果清单校验通过，是否同时将 route 门禁设为 PASSED？\r\n选择“否”只导入和校验结果。",
            "航线结果导入", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (confirm == DialogResult.Cancel) return;

        string confirmer = "";
        bool setGate = confirm == DialogResult.Yes;
        if (setGate)
        {
            var values = PromptDialog.Show(this, "确认 route 门禁",
            [
                new("confirmed_by", "确认人", Required: true)
            ]);
            if (values is null) return;
            confirmer = values["confirmed_by"];
        }

        var result = _routes.Import(Root, filePath, setGate, confirmer);
        if (result.Status == "PASSED") fileHistory.InputSucceeded(filePath);
        RefreshProject();
        RefreshArtifacts();
        MessageBox.Show(this,
            result.Status == "PASSED"
                ? "航线结果清单校验通过并已导入。"
                : $"航线结果状态：{result.Status}\r\n{string.Join("\r\n", result.Errors)}",
            "航线结果", MessageBoxButtons.OK,
            result.Status == "PASSED" ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }

    private void WriteHandoff()
    {
        EnsureInitialized();
        string path = _workspace.WriteHandoff(Root, _artifacts, _fieldwork);
        SetStatus($"交接摘要已生成：{path}");
        TransientNotice.Show(this, $"交接摘要已生成：{Path.GetFileName(path)}", "文件写入成功");
    }

    private void RunUiBusy(
        string context,
        string message,
        Action action)
    {
        RunUi(context, () => ExecuteWithBusyPrompt(context, message, action));
    }

    private void ExecuteWithBusyPrompt(
        string title,
        string message,
        Action action)
    {
        bool previousWaitCursor = UseWaitCursor;
        UseWaitCursor = true;
        SetStatus(message);
        Control centerSurface =
            _featureHost.ActivePage is not null
                ? _featureHost.ActivePage
                : _mainTabs;
        using BusyOperationDialog dialog =
            BusyOperationDialog.ShowFor(
                this,
                centerSurface,
                title,
                message);
        try
        {
            action();
        }
        finally
        {
            UseWaitCursor = previousWaitCursor;
        }
    }

    private void RunUi(string context, Action action)
    {
        try
        {
            if (!ResolveSiteDraft(context)) return;
            action();
        }
        catch (OperationCanceledException)
        {
            SetStatus("操作已取消。");
        }
        catch (Exception ex)
        {
            string log = LogService.LogException(Directory.Exists(Root) ? Root : null, ex, context);
            SetStatus($"操作失败：{ex.Message}");
            MessageBox.Show(this,
                $"{ex.Message}\r\n\r\n{DescribeErrorLog(log)}",
                context, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task RunUiAsync(string context, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
            SetStatus("操作已取消。");
        }
        catch (Exception ex)
        {
            string log = LogService.LogException(
                Directory.Exists(Root) ? Root : null,
                ex,
                context);
            SetStatus($"操作失败：{ex.Message}");
            MessageBox.Show(
                this,
                $"{ex.Message}\r\n\r\n{DescribeErrorLog(log)}",
                context,
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private static string DescribeErrorLog(string log) => string.IsNullOrWhiteSpace(log)
        ? "错误日志未能保存（项目及本机日志目录不可写）；请保留本错误信息。"
        : $"错误日志：{log}";

    private void RequireRootDirectory()
    {
        if (string.IsNullOrWhiteSpace(Root) || !Directory.Exists(Root))
            throw new DirectoryNotFoundException("请先选择有效的项目根目录。");
    }

    private void EnsureInitialized()
    {
        RequireRootDirectory();
        if (!ProjectPaths.HasProject(Root))
            throw new InvalidOperationException("该目录尚未初始化 MagOps 管理数据，请先点击“初始化”。");
    }

    private void SetStatus(string text) => _statusLabel.Text = UiText.LocalizeStatusSentence(text);

    private static Button Button(string text, EventHandler handler) =>
        UiComponents.Button(text, handler);

    private static Button Button(string text, EventHandler handler, UiButtonRole role) =>
        UiComponents.Button(text, handler, role);
}

internal static class GridFactory
{
    public static DataGridView Create() => new EmptyStateDataGridView
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AllowUserToOrderColumns = false,
        AllowUserToResizeColumns = false,
        AllowUserToResizeRows = false,
        AutoGenerateColumns = true,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = false,
        RowHeadersVisible = false,
        ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
        RowHeadersWidthSizeMode = DataGridViewRowHeadersWidthSizeMode.DisableResizing,
        BackgroundColor = SystemColors.Window,
        ShowCellToolTips = true,
        ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableAlwaysIncludeHeaderText
    };

    public static void SetEmptyText(DataGridView grid, string text)
    {
        if (grid is EmptyStateDataGridView empty)
        {
            empty.EmptyText = text;
            empty.Invalidate();
        }
    }
}
