using MagOps.Application;
using System.Globalization;
using MagOps.Core.Analysis;
using MagOps.Core.Domain;
using MagOps.Infrastructure.Services;
using MagOps.Core.Routes;
using MagOps.Core.SurveyPlanning;
using MagOps.Infrastructure.Storage;

namespace MagOps.Desktop;

public sealed partial class MainForm
{
    private string FormatRoutePathForDisplay(
        string value)
    {
        string trimmed =
            value?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(
                trimmed))
        {
            return "";
        }

        try
        {
            string full =
                Path.GetFullPath(
                    trimmed);

            if (TryGetManagedOutputContext(
                    out _,
                    out string workareaRelativePath,
                    out _))
            {
                string workareaRoot =
                    Path.GetFullPath(
                        Path.Combine(
                            Root,
                            workareaRelativePath));
                string relative =
                    Path.GetRelativePath(
                        workareaRoot,
                        full);

                if (IsLocalRelativePath(
                        relative))
                {
                    return NormalizeRouteDisplayPath(
                        relative);
                }
            }

            if (IsProjectInitialized())
            {
                string projectRelative =
                    Path.GetRelativePath(
                        Root,
                        full);
                if (IsLocalRelativePath(
                        projectRelative))
                {
                    return NormalizeRouteDisplayPath(
                        Path.Combine(
                            "项目",
                            projectRelative));
                }
            }

            return CompactRoutePathDisplay(
                full);
        }
        catch
        {
            return CompactRoutePathDisplay(
                trimmed);
        }
    }

    private static bool IsLocalRelativePath(
        string relative)
    {
        if (string.IsNullOrWhiteSpace(
                relative))
        {
            return false;
        }

        if (Path.IsPathRooted(
                relative))
        {
            return false;
        }

        string normalized =
            relative.Replace(
                Path.AltDirectorySeparatorChar,
                Path.DirectorySeparatorChar);

        return !normalized.Equals(
                   "..",
                   StringComparison.Ordinal) &&
               !normalized.StartsWith(
                   ".." +
                   Path.DirectorySeparatorChar,
                   StringComparison.Ordinal);
    }

    private static string NormalizeRouteDisplayPath(
        string value) =>
        value.Replace(
            Path.AltDirectorySeparatorChar,
            Path.DirectorySeparatorChar);

    private static string CompactRoutePathDisplay(
        string value)
    {
        const int MaxDisplayCharacters = 62;

        string normalized =
            value.Replace(
                Path.AltDirectorySeparatorChar,
                Path.DirectorySeparatorChar);

        if (normalized.Length <=
            MaxDisplayCharacters)
        {
            return normalized;
        }

        string[] parts =
            normalized.Split(
                Path.DirectorySeparatorChar,
                StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 0)
            return normalized[^MaxDisplayCharacters..];

        int take =
            Math.Min(
                3,
                parts.Length);
        string tail =
            string.Join(
                Path.DirectorySeparatorChar,
                parts[^take..]);
        string compact =
            "…" +
            Path.DirectorySeparatorChar +
            tail;

        if (compact.Length <=
            MaxDisplayCharacters)
        {
            return compact;
        }

        string fileName =
            parts[^1];
        if (fileName.Length >=
            MaxDisplayCharacters - 2)
        {
            return "…" +
                fileName[
                    ^(MaxDisplayCharacters - 1)..];
        }

        int remaining =
            Math.Max(
                0,
                MaxDisplayCharacters -
                fileName.Length -
                3);
        string parent =
            parts.Length > 1
                ? parts[^2]
                : "";
        if (parent.Length > remaining)
        {
            parent =
                parent[^remaining..];
        }

        return string.IsNullOrWhiteSpace(
                parent)
            ? "…" +
              Path.DirectorySeparatorChar +
              fileName
            : "…" +
              Path.DirectorySeparatorChar +
              parent +
              Path.DirectorySeparatorChar +
              fileName;
    }

    private void BuildRouteTab()
    {
        RegisterFeatureControl(
            FeatureIds.RouteDesign,
            _routeWorkspace,
            activated: QueueActivation,
            contextChanged: _ => QueueActivation());

        void QueueActivation() => QueueFeatureActivation(FeatureIds.RouteDesign, () =>
            RunUi("读取航线项目默认输入", LoadRoutePageDefaultsOnActivation));
    }


    private void LoadRoutePageDefaultsOnActivation()
    {
        // Project-governed defaults are resolved at workbench activation time,
        // not as a side effect of choosing or exporting an individual file.
        TryLoadProjectTerrainForRoute();
        if (string.IsNullOrWhiteSpace(
                _routeWorkspace.GetCentralMeridianText()))
        {
            RefreshRouteCentralMeridianFromProject();
        }
        TryAutoLoadRoutePly();
        TryAutoLoadRouteKml();
        RefreshRouteKmlDirectorySelector();
    }

    private void TryAutoLoadRoutePly()
    {
        // Do not replace a valid file that the operator explicitly selected.
        if (File.Exists(_routeWorkspace.GetPlyPath()))
            return;

        var candidates = new List<(string Path, string Source)>();
        if (File.Exists(_lastGeneratedRoutePlyPath))
        {
            candidates.Add((
                Path.GetFullPath(_lastGeneratedRoutePlyPath),
                "上一步生成的"));
        }

        if (TryGetManagedOutputContext(
                out _,
                out string workareaPath,
                out _))
        {
            string directory = ManagedOutputPreview(
                "coordinate_oasis_ply",
                workareaPath);
            if (Directory.Exists(directory))
            {
                candidates.AddRange(Directory
                    .EnumerateFiles(directory, "*.ply", SearchOption.TopDirectoryOnly)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .Select(path => (Path.GetFullPath(path), "当前工区最近的")));
            }
        }

        foreach (var candidate in candidates
                     .Where(x => File.Exists(x.Path))
                     .DistinctBy(x => x.Path, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                IReadOnlyList<SurveyBoundaryPoint> boundary =
                    _plySurveyLines.ReadBoundary(candidate.Path);
                _routeWorkspace.SetPlyPath(candidate.Path);
                _lastGeneratedRoutePlyPath = candidate.Path;
                if (_routeWorkspace.GetSourceChoice() == RouteSourceChoice.None)
                {
                    _routeWorkspace.SetSourceChoice(
                        RouteSourceChoice.Ply);
                }
                SetStatus(
                    $"已自动载入{candidate.Source} PLY：{Path.GetFileName(candidate.Path)}；" +
                    $"边界点 {boundary.Count} 个。");
                return;
            }
            catch
            {
                // Auto-load must never block entering the route page. Skip an
                // invalid historical PLY and continue to the next candidate.
            }
        }
    }

    private void ChooseRoutePly(string? selectedPath = null)
    {
        var fileHistory = FileInteractions.For(this, "route-ply", projectLevel: false, global: false);
        string filePath = selectedPath?.Trim() ?? "";
        if (filePath.Length == 0)
        {
            using var dialog = new OpenFileDialog
            {
                Filter = "Oasis PLY (*.ply)|*.ply|所有文件 (*.*)|*.*",
                Title = "选择工区边界 PLY",
                InitialDirectory = ResolveRoutePlyInitialDirectory()
            };
            fileHistory.Prepare(dialog, current: _routeWorkspace.GetPlyPath());
            if (dialog.ShowDialog(this) != DialogResult.OK)
                throw new OperationCanceledException();
            filePath = dialog.FileName;
        }

        if (!File.Exists(filePath) ||
            !FileDropSupport.HasExtension(filePath, ".ply"))
        {
            throw new InvalidDataException("请选择有效的 .ply 工区边界文件。");
        }

        IReadOnlyList<SurveyBoundaryPoint> boundary =
            _plySurveyLines.ReadBoundary(filePath);
        _routeWorkspace.SetPlyPath(Path.GetFullPath(filePath));
        _routeWorkspace.SetSourceChoice(RouteSourceChoice.Ply);
        ClearPartitionRouteWorkspace();
        _routeActivePartition = null;
        // PLY and endpoint CSV are alternative source modes. Selecting a new
        // PLY must not leave a previously loaded CSV silently active.
        _routeWorkspace.SetCsvPath("");
        _routeInference = null;
        _routeLoadedFingerprint = "";
        SetStatus(
            $"已选择 PLY 边界：{Path.GetFileName(filePath)}；" +
            $"有效边界点 {boundary.Count} 个；测线按法向投影排序，编号沿主要方向向北或向东递增；PLY 顶点顺序不参与编号。");
        fileHistory.InputSucceeded(filePath);
    }

    private SurveyLinePlanOptions BuildSurveyLinePlanOptions()
    {
        double spacing = ParseRoutePositive(
            _routeWorkspace.GetLineSpacingText(),
            "飞行线间距");
        double azimuth = ParseRouteNumber(
            _routeWorkspace.GetLineAzimuthText(),
            "飞行线方位角");
        int initialLine = ParseRouteInteger(
            _routeWorkspace.GetInitialLineText(),
            "初始飞行线号");
        int lineIncrement = ParseRouteInteger(
            _routeWorkspace.GetLineIncrementText(),
            "飞行线序号增量");
        if (lineIncrement <= 0)
            throw new InvalidOperationException(
                "飞行线序号增量必须大于 0；线号按南/西向北/东递增。");

        if (_routeDesignBasis is null || Math.Abs(_routeDesignBasis.GridAzimuthDegrees - azimuth) > 1e-8)
            _routeDesignBasis = new(azimuth, "人工设置；投影坐标北顺时针");
        return new SurveyLinePlanOptions
        {
            SpacingM = spacing,
            AzimuthDegrees = azimuth,
            InitialLineNumber = initialLine,
            LineNumberIncrement = lineIncrement,
            // 待修改：蛇形奇偶外扩已冻结。只有补充可通行道路/现场进入条件
            // 计算方法并完成验收后，才允许重新启用向测区外增加正式测线。
            ExtendForEvenSnakeCompletion = false
        };
    }

    private void GenerateRoutePlanFromPly()
    {
        GenerateRouteEndpointsFromPlyCore(loadSource: true, showNotice: true);
        GenerateRoutePlan();
    }

    private string GenerateRouteEndpointsFromPlyCore(
        bool loadSource,
        bool showNotice)
    {
        TryLoadProjectTerrainForRoute();

        string source = _routeWorkspace.GetPlyPath();
        if (!File.Exists(source))
        {
            ChooseRoutePly();
            source = _routeWorkspace.GetPlyPath();
        }
        if (!File.Exists(source))
            throw new OperationCanceledException("未选择 PLY 工区边界。");

        SurveyLinePlanOptions options = BuildSurveyLinePlanOptions();
        SurveyLinePlanResult plan = _plySurveyLines.Plan(source, options);
        if (plan.Lines.Count == 0)
            throw new InvalidOperationException("当前 PLY/参数没有生成任何有效测线。");

        string fileName =
            $"{Path.GetFileNameWithoutExtension(source)}_航线端点.csv";
        string outputPath = ResolveToolOutputFilePath(
            "route_endpoint_design",
            "PLY 自动绘制航线端点",
            fileName,
            "航线端点 CSV (*.csv)|*.csv|所有文件 (*.*)|*.*",
            ".csv");

        _plySurveyLines.WriteEndpointCsv(outputPath, plan);

        bool managed = TryGetManagedOutputContext(
            out _,
            out _,
            out _);
        if (managed)
            RegisterGeneratedBusinessFile(outputPath, "route_endpoint_design");

        _routeWorkspace.SetCsvPath(Path.GetFullPath(outputPath));
        _routeCoveragePlyPath = Path.GetFullPath(source);
        _routeWorkspace.SetCoordModeIndex(1); // projected-metre PLY workflow.
        _routeWorkspace.SetInfoText(
            $"PLY 自动绘制完成：{Path.GetFileName(source)}\r\n" +
            $"计算原点：X={plan.Origin.X:0.###}，Y={plan.Origin.Y:0.###}；" +
            $"方位角 {plan.Options.AzimuthDegrees:0.###}°；间距 {plan.Options.SpacingM:0.###} m；" +
            "线号按南/西向北/东递增。\r\n" +
            $"生成正式测线 {plan.Lines.Count} 条 / 端点 {plan.EndpointCount} 个 / 几何段 {plan.SegmentCount} 段；" +
            "蛇形外扩增线已冻结（待修改；补充可通行道路计算方法后启用）；" +
            $"其中 {plan.DisconnectedLineCount} 条被凹形边界切成多段。\r\n" +
            $"端点表：{outputPath}\r\n" +
            (plan.DisconnectedLineCount > 0
                ? "下一步进入现有凹形 CONNECT / SPLIT 处理；不会自动跨越凹口。"
                : "端点将直接进入现有 v25 航线连接流程。"));

        if (showNotice)
        {
            TransientNotice.Show(
                this,
                $"已从 PLY 生成 {plan.Lines.Count} 条母测线：{Path.GetFileName(outputPath)}",
                "PLY 航线几何完成");
        }

        if (loadSource)
            LoadRouteSource();

        return outputPath;
    }

    private bool TryAutoLoadRouteKml(bool force = false)
    {
        if (!force && File.Exists(_routeWorkspace.GetKmlPath()))
        {
            RefreshRouteKmlDirectorySelector();
            return true;
        }

        if (!TryGetManagedOutputContext(
                out _,
                out string workareaPath,
                out _))
        {
            return false;
        }

        string directory = ManagedOutputPreview(
            "coordinate_kml_range",
            workareaPath);
        if (!Directory.Exists(directory))
            return false;

        string? candidate = Directory
            .EnumerateFiles(directory, "*.kml", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(candidate) || !File.Exists(candidate))
            return false;

        try
        {
            SpatialGeometry polygon = _kmlSurveySubdivision.ReadPrimaryPolygon(candidate);
            _routeWorkspace.SetKmlPath(Path.GetFullPath(candidate));
            _routeWorkspace.SetSourceChoice(RouteSourceChoice.Kml);
            _routeSubdivisionPreparation = null;
            _routeSubdivisionPreparedFingerprint = "";
            RefreshRouteKmlDirectorySelector();
            SetStatus(
                $"已自动读取当前工区范围 KML：{Path.GetFileName(candidate)}；" +
                $"Polygon 点数 {polygon.Coordinates.Count}。");
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void RefreshRouteKmlDirectorySelector()
    {
        string current = _routeWorkspace.GetKmlPath();
        string directory = "";

        if (File.Exists(current))
        {
            if (_kmlSurveySubdivision.TryResolveGeneratedChild(
                    current,
                    out KmlSurveySubdivisionResolvedChild? generated) &&
                generated is not null)
            {
                directory = Path.GetDirectoryName(generated.ManifestPath) ?? "";
            }
            else
            {
                directory = Path.GetDirectoryName(Path.GetFullPath(current)) ?? "";
            }
        }
        else if (TryGetManagedOutputContext(
                     out _,
                     out string workareaPath,
                     out _))
        {
            directory = ManagedOutputPreview(
                "coordinate_kml_range",
                workareaPath);
        }

        var paths = new List<string>();
        if (Directory.Exists(directory))
        {
            foreach (string path in Directory
                         .EnumerateFiles(
                             directory,
                             "*.kml",
                             SearchOption.AllDirectories)
                         .OrderBy(
                             path => Path.GetRelativePath(directory, path),
                             StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    _ = _kmlSurveySubdivision.ReadPrimaryPolygon(path);
                    paths.Add(Path.GetFullPath(path));
                }
                catch
                {
                    // Restrict selector to valid Polygon KML ranges.
                }
            }
        }

        _routeWorkspace.SetKmlDirectoryPaths(paths, current);
    }

    private void SelectRouteKmlFromDirectorySelector(
        string selected)
    {
        if (!File.Exists(selected))
        {
            RefreshRouteKmlDirectorySelector();
            throw new FileNotFoundException(
                "所选同目录 KML 已不存在。",
                selected);
        }

        string current =
            _routeWorkspace.GetKmlPath();
        if (File.Exists(current) &&
            Path.GetFullPath(current).Equals(
                Path.GetFullPath(selected),
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        PartitionRouteRuntimeContext? context = _routePartitionContexts.Values
            .FirstOrDefault(item => Path.GetFullPath(item.Child.KmlPath).Equals(
                Path.GetFullPath(selected),
                StringComparison.OrdinalIgnoreCase));
        if (context is not null)
        {
            OpenPartitionFromOverview(context.PartitionCode);
            return;
        }

        ChooseRouteKml(
            selected);
    }


    private bool IsKnownPartitionKml(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        string full = Path.GetFullPath(path);
        return _routePartitionContexts.Values.Any(context =>
            Path.GetFullPath(context.Child.KmlPath).Equals(
                full,
                StringComparison.OrdinalIgnoreCase));
    }

    private void ClearPartitionRouteWorkspace()
    {
        ResetSiteEditor();
        _restoredSubdivisionSnapshot = null;
        _routePartitionContexts.Clear();
        _routeActivePartition = null;
        _routeWorkspace.ClearPartitionOverview();
    }

    private void ChooseRouteKml(string? selectedPath = null)
    {
        var fileHistory = FileInteractions.For(this, "route-boundary", projectLevel: false, global: false);
        string filePath = selectedPath?.Trim() ?? "";
        if (filePath.Length == 0)
        {
            string initial = ResolveRouteKmlInitialDirectory();
            using var dialog = new OpenFileDialog
            {
                Filter = "KML 范围 (*.kml)|*.kml|所有文件 (*.*)|*.*",
                Title = "选择用于绘制航线或分区的 Polygon KML",
                InitialDirectory = initial
            };
            fileHistory.Prepare(dialog, current: _routeWorkspace.GetKmlPath());
            if (dialog.ShowDialog(this) != DialogResult.OK)
                throw new OperationCanceledException();
            filePath = dialog.FileName;
        }

        if (!File.Exists(filePath) ||
            !FileDropSupport.HasExtension(filePath, ".kml"))
        {
            throw new InvalidDataException("请选择有效的 Polygon KML 范围文件。");
        }

        SpatialGeometry polygon = _kmlSurveySubdivision.ReadPrimaryPolygon(filePath);
        bool preservePartitionWorkspace = IsKnownPartitionKml(filePath);
        if (!preservePartitionWorkspace)
            ClearPartitionRouteWorkspace();
        _routeWorkspace.SetKmlPath(Path.GetFullPath(filePath));
        _routeWorkspace.SetSourceChoice(RouteSourceChoice.Kml);
        _routeWorkspace.SetCsvPath("");
        _routeInference = null;
        _routePlan = null;
        _routeActivePartition = null;
        _routeLoadedFingerprint = "";
        _routeCoveragePlyPath = "";
        _routeSubdivisionPreparation = null;
        _routeSubdivisionPreparedFingerprint = "";
        _routeWorkspace.ClearRoutePreview();
        _routeWorkspace.ClearSubdivisionPreview();
        RefreshRouteKmlDirectorySelector();
        SetStatus(
            $"已选择 KML 边界：{Path.GetFileName(filePath)}；" +
            $"Polygon 点数 {polygon.Coordinates.Count}。可直接生成航线，也可先预览/执行分区。");
        fileHistory.InputSucceeded(filePath);
    }

    private string ResolveRouteKmlInitialDirectory()
    {
        if (TryGetManagedOutputContext(
                out _,
                out string workareaPath,
                out _))
        {
            string managed = ManagedOutputPreview(
                "coordinate_kml_range",
                workareaPath);
            if (Directory.Exists(managed))
                return managed;
        }
        return ResolveRouteSourceInitialDirectory();
    }

    private SurveySubdivisionAxis GetRouteSubdivisionAxis() =>
        _routeWorkspace.GetKmlSplitModeIndex() switch
        {
            1 => SurveySubdivisionAxis.ProjectedX,
            2 => SurveySubdivisionAxis.ProjectedY,
            3 => SurveySubdivisionAxis.Custom,
            _ => SurveySubdivisionAxis.AlongSurveyLine
        };

    private KmlSurveySubdivisionPreparation PrepareCurrentRouteKmlSubdivision()
    {
        string source = _routeWorkspace.GetKmlPath();
        if (!File.Exists(source))
        {
            ChooseRouteKml();
            source = _routeWorkspace.GetKmlPath();
        }
        if (!File.Exists(source))
            throw new OperationCanceledException("未选择有效 KML 边界。");

        SurveyLinePlanOptions lineOptions = BuildSurveyLinePlanOptions();
        double maxSortieKm = ParseRoutePositive(
            _routeWorkspace.GetMaxSortieKmText(),
            "最大单架次长度");
        double? requestedCentral = ParseRouteOptionalDouble(
            _routeWorkspace.GetCentralMeridianText(),
            "中央经线");

        string fingerprint = string.Join(
            "|",
            GetRouteSourceFingerprint(source),
            lineOptions.SpacingM.ToString("R", CultureInfo.InvariantCulture),
            lineOptions.AzimuthDegrees.ToString("R", CultureInfo.InvariantCulture),
            lineOptions.InitialLineNumber,
            lineOptions.LineNumberIncrement,
            requestedCentral?.ToString("R", CultureInfo.InvariantCulture) ?? "auto",
            maxSortieKm.ToString("R", CultureInfo.InvariantCulture));

        if (_routeSubdivisionPreparation is not null &&
            fingerprint.Equals(
                _routeSubdivisionPreparedFingerprint,
                StringComparison.Ordinal))
        {
            return _routeSubdivisionPreparation;
        }

        KmlSurveySubdivisionPreparation preparation =
            _kmlSurveySubdivision.Prepare(
                source,
                requestedCentral,
                "WGS84",
                lineOptions,
                maxSortieKm);

        _routeWorkspace.SetCentralMeridianText(
            preparation.CentralMeridian.ToString(
                "0.########",
                CultureInfo.InvariantCulture));
        _routeWorkspace.SetCoordModeIndex(1);
        _routeSubdivisionPreparation = preparation;
        _routeSubdivisionPreparedFingerprint = fingerprint;
        return preparation;
    }

    private void RefreshRouteSubdivisionPreview()
    {
        if (!File.Exists(_routeWorkspace.GetKmlPath()))
        {
            _routeWorkspace.ClearSubdivisionPreview();
            SetStatus("KML 分区预览等待输入：请先进入“绘制航线”读取项目默认范围，或手工选择 KML。");
            return;
        }

        KmlSurveySubdivisionPreparation preparation =
            PrepareCurrentRouteKmlSubdivision();
        _routeWorkspace.SetSurveyTrackReferences((_routePlan?.AccessPlanning ?? LoadAccessPlanning()).Roads,
            preparation.CentralMeridian, preparation.Datum);
        if (_routeWorkspace.GetKmlSplitModeIndex() == 4)
        {
            SurveySubdivisionOperationPlan pin = _kmlSurveySubdivision.CreatePinTemplate(
                preparation, SurveySubdivisionTemplate.PinTopOneBottomTwo);
            SurveyLineSubdivisionResult preview = new SurveyLineSubdivisionPlanner()
                .SubdivideByOperations(preparation.MotherPlan, pin, _routeWorkspace.GetKmlOverlapLengthM());
            _routeWorkspace.SetSubdivisionOperationPreview(
                preparation.MotherPlan, pin);
            _routeWorkspace.ShowSubdivisionPreviewTab();
            SetStatus($"品字三分预览：局部切割树将生成 {preview.Subareas.Count} 个子区；第二条切线只作用于下部子区。执行分区时按 operation-tree 写出。 ");
            return;
        }
        SurveySubdivisionAxis axis = GetRouteSubdivisionAxis();
        IReadOnlyList<SurveySubdivisionCutLine> cuts;

        if (axis == SurveySubdivisionAxis.Custom)
        {
            cuts = ReferenceEquals(
                    _routeWorkspace.GetSubdivisionMotherPlan(),
                    preparation.MotherPlan)
                ? _routeWorkspace.GetSubdivisionCuts()
                : [];
        }
        else
        {
            cuts = _kmlSurveySubdivision.CreateEqualCuts(
                preparation,
                axis,
                _routeWorkspace.GetKmlSplitCount());
        }

        _routeWorkspace.SetSubdivisionPreview(
            preparation.MotherPlan,
            cuts,
            axis,
            manualDrawingEnabled:
                axis == SurveySubdivisionAxis.Custom);
        _routeWorkspace.ShowSubdivisionPreviewTab();

        SetStatus(
            axis == SurveySubdivisionAxis.Custom
                ? "KML 手绘分区预览已打开：左键拖绘切分线，拖动已有线可平移，右键删除。"
                : $"KML 分区预览已刷新：{cuts.Count} 条切分线；可在预览中拖动微调后执行。 ");
    }

    private void SplitRouteKmlIntoSubareas()
    {
        KmlSurveySubdivisionPreparation preparation =
            PrepareCurrentRouteKmlSubdivision();
        bool pinTemplate = _routeWorkspace.GetKmlSplitModeIndex() == 4;
        SurveySubdivisionAxis axis = GetRouteSubdivisionAxis();
        int selectedCount = pinTemplate ? 3 : _routeWorkspace.GetKmlSplitCount();
        double maxSortieKm = ParseRoutePositive(
            _routeWorkspace.GetMaxSortieKmText(),
            "最大单架次长度");

        if (!pinTemplate && axis != SurveySubdivisionAxis.Custom &&
            preparation.SuggestedSubareaCount > 1 &&
            selectedCount < preparation.SuggestedSubareaCount)
        {
            DialogResult answer = MessageBox.Show(
                this,
                $"按母区最长测线和当前 {maxSortieKm:0.###} km 架次上限估算，" +
                $"建议至少分为 {preparation.SuggestedSubareaCount} 个子区。\r\n\r\n" +
                $"当前选择 {selectedCount} 个，仍可能存在完整来回超长。\r\n" +
                "是否仍按当前数量继续？",
                "KML 分区数量偏少",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes)
            {
                _routeWorkspace.SetKmlSplitCount(
                    Math.Max(
                        2,
                        preparation.SuggestedSubareaCount));
                throw new OperationCanceledException();
            }
        }

        IReadOnlyList<SurveySubdivisionCutLine> cuts;
        if (ReferenceEquals(
                _routeWorkspace.GetSubdivisionMotherPlan(),
                preparation.MotherPlan) &&
            _routeWorkspace.GetSubdivisionAxis() == axis &&
            _routeWorkspace.GetSubdivisionCuts().Count > 0)
        {
            cuts = _routeWorkspace.GetSubdivisionCuts();
        }
        else if (axis == SurveySubdivisionAxis.Custom)
        {
            throw new InvalidOperationException(
                "手绘分区至少需要 1 条切分线。请先点击“预览切分”，在 KML 分区预览中左键拖绘切分线。");
        }
        else
        {
            cuts = _kmlSurveySubdivision.CreateEqualCuts(
                preparation,
                axis,
                selectedCount);
        }

        string output = _routeWorkspace.GetKmlSubdivisionOutputDirectory(EnsureManagedOutputDirectory(
            "route_kml_subdivision",
            "KML 分区固定归入当前工区 02_KML分区"));

        KmlSurveySubdivisionBundle bundle = pinTemplate
            ? _kmlSurveySubdivision.WriteSubdivision(
                preparation,
                _kmlSurveySubdivision.CreatePinTemplate(
                    preparation, SurveySubdivisionTemplate.PinTopOneBottomTwo),
                output, _routeWorkspace.GetKmlOverlapLengthM())
            : _kmlSurveySubdivision.WriteSubdivision(
                preparation, cuts, axis, output, _routeWorkspace.GetKmlOverlapLengthM());

        RegisterGeneratedBusinessFiles(
            bundle.OutputPaths,
            "route_kml_subdivision");

        PartitionRouteWorkspaceBuildSummary workspaceSummary =
            BuildPartitionRouteWorkspace(
                bundle,
                preparation,
                maxSortieKm);
        _routeWorkspace.RemoveSubdivisionPreviewTab();
        _routeWorkspace.ClearSubdivisionPreview();
        RefreshRouteKmlDirectorySelector();

        double motherMax = preparation.MotherPlan.Lines
            .SelectMany(line => line.Segments)
            .Select(segment => segment.Length)
            .DefaultIfEmpty(0d)
            .Max();
        double childMax = bundle.Children
            .Select(child => child.MaxSegmentLengthM)
            .DefaultIfEmpty(0d)
            .Max();
        string modeText = axis switch
        {
            SurveySubdivisionAxis.ProjectedX => "X/Easting（经度向）",
            SurveySubdivisionAxis.ProjectedY => "Y/Northing（纬度向）",
            SurveySubdivisionAxis.Custom => "用户手绘切分线",
            _ => "沿测线方向"
        };

        _routeWorkspace.SetInfoText(
            $"KML 母区已按“{modeText}”分为 {bundle.Children.Count} 个子区。\r\n" +
            $"中央经线 {preparation.CentralMeridian:0.########}°；" +
            $"线距 {preparation.MotherPlan.Options.SpacingM:0.###} m；" +
            $"方位角 {preparation.MotherPlan.Options.AzimuthDegrees:0.###}°。\r\n" +
            $"母区最长测线 {motherMax / 1000d:0.###} km；" +
            $"分区后最长子段 {childMax / 1000d:0.###} km。\r\n" +
            $"分区总览：已生成 {workspaceSummary.GeneratedCount}/{bundle.Children.Count} 个独立 RoutePlan；" +
            $"{workspaceSummary.PendingCount} 个分区需要人工确认后再生成。\r\n" +
            "关键规则：母测线只编号一次，P01/P02…只裁切并继承同一 L 号；" +
            "起始侧先决定首条外侧测线与蛇形遍历方向，再按架次长度切分；不会在架次切分后再翻转起点。\r\n" +
            "蛇形外扩增线继续冻结；总览中的起始侧 / 角点偏好 / 架次上限均为每个分区独立参数，修改只重算该分区。\r\n" +
            $"分区清单：{bundle.ManifestPath}");

        _routeWorkspace.ShowPartitionOverviewTab();
        string status = workspaceSummary.PendingCount == 0
            ? $"KML 分区完成：{bundle.Children.Count} 个分区已建立独立航线上下文，并进入分区总览。"
            : $"KML 分区完成：{bundle.Children.Count} 个分区已进入总览；{workspaceSummary.PendingCount} 个分区需确认凹形断线规则。";
        SetStatus(status);
        TransientNotice.Show(
            this,
            workspaceSummary.PendingCount == 0
                ? $"KML 已分为 {bundle.Children.Count} 个对齐分区；全部子区航线已进入统一总览。"
                : $"KML 已分为 {bundle.Children.Count} 个对齐分区；总览已建立，{workspaceSummary.PendingCount} 个子区等待断线规则确认。",
            "KML 分区完成");
    }

    private sealed record PartitionRouteWorkspaceBuildSummary(
        int GeneratedCount,
        int PendingCount);

    private PartitionRouteWorkspaceBuildSummary BuildPartitionRouteWorkspace(
        KmlSurveySubdivisionBundle bundle,
        KmlSurveySubdivisionPreparation preparation,
        double maxSortieKm)
    {
        _restoredSubdivisionSnapshot = new RouteSubdivisionWorkspace
        {
            ManifestPath = bundle.ManifestPath, ManifestJson = File.ReadAllText(bundle.ManifestPath),
            MotherKmlPath = preparation.SourceKml, MotherKmlContent = File.ReadAllText(preparation.SourceKml)
        };
        _routePartitionContexts.Clear();
        _routeActivePartition = null;
        _routePlan = null;
        _routeInference = null;
        _routeStartMap.Clear();
        _routeEditedSorties.Clear();
        _routeReversedSorties.Clear();
        _routeBoundaryAdjustedSorties.Clear();
        SetRouteWorkspaceDirty(false);
        _routeWorkspace.ClearRoutePreview();

        var startDirection = (RouteStartDirection)Math.Clamp(
            _routeWorkspace.GetStartDirectionIndex(),
            0,
            3);
        double aglM = ParseRouteNumber(
            _routeWorkspace.GetAglText(),
            "离地高程 AGL");
        var motherReference = PartitionFlightConsistency.Capture(_routePlanner.BuildFromCsv(
            bundle.MotherEndpointCsvPath, new RoutePlanOptions { CoordinateMode = RouteCoordinateMode.Projected,
                CentralMeridian = preparation.CentralMeridian, MaxSortieKm = 1000000, StartDirection = startDirection }));
        int generated = 0;
        int pending = 0;

        foreach (KmlSurveySubdivisionChildFile childFile in bundle.Children
                     .OrderBy(child => child.Index))
        {
            SurveyLineSubdivisionBlock? block = bundle.Subdivision.Subareas
                .FirstOrDefault(item => item.Index == childFile.Index);
            KmlSurveySubdivisionResolvedChild resolved;
            if (!_kmlSurveySubdivision.TryResolveGeneratedChild(
                    childFile.KmlPath,
                    out KmlSurveySubdivisionResolvedChild? resolvedFromManifest) ||
                resolvedFromManifest is null)
            {
                // WriteSubdivision has just produced this child, so the overview
                // must never silently drop it merely because manifest discovery
                // failed. Reconstruct the same lineage from the in-memory bundle
                // and preparation; detailed editing still uses the generated files.
                resolved = new KmlSurveySubdivisionResolvedChild(
                    Path.GetFullPath(childFile.KmlPath),
                    Path.GetFullPath(childFile.EndpointCsvPath),
                    Path.GetFullPath(bundle.ManifestPath),
                    childFile.Index,
                    childFile.PartitionCode,
                    preparation.CentralMeridian,
                    preparation.Datum,
                    bundle.Subdivision.Cuts,
                    ContainsFrozenSnakeExtension: false);
            }
            else
            {
                resolved = resolvedFromManifest;
            }

            var context = new PartitionRouteRuntimeContext
            {
                PartitionCode = resolved.PartitionCode,
                Index = resolved.Index,
                Child = resolved,
                Boundary = block?.Boundary.ToArray() ?? [],
                StartSide = RoutePartitionStartSide.Automatic,
                StartDirection = startDirection,
                TakeoffAnchorMode = _routeWorkspace.GetTakeoffAnchorMode(),
                FlightDirection = _routeWorkspace.GetFlightDirection(),
                TakeoffAnchorRef = _routeWorkspace.GetTakeoffAnchorRef(),
                MaxSortieKm = maxSortieKm,
                AglM = aglM,
                MotherFlightDirections = motherReference,
                RouteSourcePath = resolved.EndpointCsvPath
            };

            try
            {
                RebuildPartitionRouteContext(context);
                if (context.Plan is null)
                    pending++;
                else
                    generated++;
            }
            catch (Exception ex)
            {
                context.Plan = null;
                context.Status = "生成失败：" + ex.Message;
                pending++;
            }

            _routePartitionContexts[context.PartitionCode] = context;
        }

        IReadOnlyList<PartitionRouteOverviewEntry> entries =
            _routePartitionContexts.Values
                .OrderBy(context => context.Index)
                .Select(context => context.ToOverviewEntry())
                .ToArray();
        _routeWorkspace.SetPartitionOverviewEntries(entries);
        if (IsProjectInitialized())
            _userContext.SavePartitionState(
                Root,
                bundle.ManifestPath,
                "",
                overviewActive: true);
        QueueSiteSuggestions();
        return new PartitionRouteWorkspaceBuildSummary(
            generated,
            pending);
    }

    private void RebuildPartitionRouteContext(
        PartitionRouteRuntimeContext context)
    {
        string endpointCsv = !string.IsNullOrWhiteSpace(context.RouteSourcePath) &&
            File.Exists(context.RouteSourcePath)
                ? context.RouteSourcePath
                : context.Child.EndpointCsvPath;
        if (!File.Exists(endpointCsv))
        {
            context.Plan = null;
            context.Status = "端点文件缺失";
            return;
        }

        if (_concaveLineService.TryInspectConcaveInputBeforeNumericParse(
                endpointCsv,
                out ConcaveLineInspection? inspection) &&
            inspection is not null &&
            inspection.DisconnectedLines.Count > 0)
        {
            context.Plan = null;
            context.Status = $"需确认凹形断线（{inspection.DisconnectedLines.Count} 条）";
            return;
        }

        RawRoutePoint[] raw = DelimitedTextReader
            .ReadRawRouteCsv(
                endpointCsv,
                _routeWorkspace.GetHeaderlessOrderText())
            .ToArray();
        RouteLineInference inference =
            _routePlanner.InferSpatialLines(raw);
        const string mode = "projected";

        RouteEndpoint start = ResolvePartitionTraversalStart(
            inference,
            context.Child,
            context.StartSide,
            context.StartDirection,
            context.TakeoffAnchorMode,
            context.TakeoffAnchorRef,
            mode);

        var options = new RoutePlanOptions
        {
            CoordinateMode = RouteCoordinateMode.Projected,
            HeaderlessOrder = _routeWorkspace.GetHeaderlessOrderText(),
            CentralMeridian = context.Child.CentralMeridian,
            MaxSortieKm = context.MaxSortieKm,
            StartDirection = context.StartDirection,
                PartitionStartSide = context.StartSide,
                TakeoffAnchorMode = context.TakeoffAnchorMode,
                FlightDirection = context.FlightDirection,
                TakeoffAnchorRef = context.TakeoffAnchorRef,
            SelectedStartPid = start.Pid,
            KmzAglM = context.AglM
        };

        RoutePlanResult plan = _routePlanningWorkflow.Generate(
            new RoutePlanningRequest(
                endpointCsv,
                options,
                null,
                context.PartitionCode,
                context.Child.ManifestPath));

        EnsureRoutePlanMatchesProjectSpatialReference(
            plan.CentralMeridian);
        if (context.Boundary.Count >= 3)
        {
            RouteBoundarySafetyService.EnsurePlanInsideBoundary(
                plan,
                context.Boundary,
                $"{context.PartitionCode} 有效边界");
        }

        if (context.TakeoffAnchorMode == RouteTakeoffAnchorMode.Automatic &&
            context.StartSide == RoutePartitionStartSide.Automatic && context.FlightDirection == RouteFlightDirection.Automatic)
            PartitionFlightConsistency.Align(plan, context.MotherFlightDirections);
        plan.DesignBasis = context.Plan?.DesignBasis ?? _routeDesignBasis;
        plan.CheckLines = context.Plan?.CheckLines.ToList() ?? [];
        plan.AccessPlanning = context.Plan?.AccessPlanning ?? LoadAccessPlanning();
        context.Plan = plan;
        context.RouteSourcePath = plan.SourcePath;
        context.SourceSha256 = GeneratedArtifactIdentity.Hash(plan.SourcePath);
        context.EditedSorties.Clear();
        context.ReversedSorties.Clear();
        context.BoundaryAdjustedSorties.Clear();
        context.Dirty = false;
        context.Status = plan.HasOverLimitSorties
            ? "存在超限架次"
            : "自动生成";
    }

    private static RouteEndpoint ResolvePartitionTraversalStart(
        RouteLineInference inference,
        KmlSurveySubdivisionResolvedChild child,
        RoutePartitionStartSide side,
        RouteStartDirection direction,
        RouteTakeoffAnchorMode anchorMode,
        string anchorRef,
        string mode)
    {
        if (anchorMode == RouteTakeoffAnchorMode.Boundary ||
            (anchorMode == RouteTakeoffAnchorMode.Automatic && side != RoutePartitionStartSide.Automatic))
        {
            return RoutePlanner.GetTraversalStartPointFromSide(
                inference.Grouped,
                inference.OrderedLines,
                side,
                direction,
                mode);
        }

        if (anchorMode == RouteTakeoffAnchorMode.CutLine ||
            (anchorMode == RouteTakeoffAnchorMode.Automatic && child.Cuts.Count > 0))
        {
            const double cutToleranceM = 0.25d;
            // Cut lines currently have no persisted identity; the reference is
            // retained for future per-cut selection and automatic matching.
            IEnumerable<SurveySubdivisionCutLine> cutSource = child.Cuts;
            RouteEndpoint[] candidates = inference.Endpoints
                .Where(point => cutSource.Any(cut =>
                    DistancePointToSegment(
                        point.X,
                        point.Y,
                        cut.Start.X,
                        cut.Start.Y,
                        cut.End.X,
                        cut.End.Y) <= cutToleranceM))
                .ToArray();
            if (candidates.Length > 0)
            {
                return RoutePlanner.GetTraversalStartPointFromCandidates(
                    inference.Grouped,
                    inference.OrderedLines,
                    candidates,
                    direction,
                    mode);
            }
        }

        return RoutePlanner.GetStartPointFromCorner(
            inference.Grouped,
            inference.OrderedLines,
            direction,
            mode);
    }

    private void ApplyPartitionOverviewSettings(
        PartitionRouteOverviewEdit edit)
    {
        if (!_routePartitionContexts.TryGetValue(
                edit.PartitionCode,
                out PartitionRouteRuntimeContext? context))
        {
            return;
        }

        if (!ResolveSiteDraft("重新生成航线")) return;
        bool hasManualEdits =
            context.EditedSorties.Count > 0 ||
            context.ReversedSorties.Count > 0 ||
            context.BoundaryAdjustedSorties.Count > 0;
        if (hasManualEdits)
        {
            DialogResult answer = MessageBox.Show(
                this,
                $"{context.PartitionCode} 存在人工架次/航点修改。\r\n\r\n" +
                "修改起始侧、角点偏好或架次上限会重新自动生成该分区，并放弃这些人工修改。\r\n\r\n是否继续？",
                "重新生成分区航线",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes)
            {
                _routeWorkspace.UpdatePartitionOverviewEntry(
                    context.ToOverviewEntry());
                return;
            }
        }

        context.StartSide = edit.StartSide;
        context.StartDirection = edit.StartDirection;
        context.MaxSortieKm = edit.MaxSortieKm;
        RebuildPartitionRouteContext(context);
        context.Dirty = true;
        _dirtyStateCoordinator.NotifyChanged();
        if (context.Plan is not null)
        {
            context.Status = context.Plan.HasOverLimitSorties
                ? "已重算 / 存在超限"
                : "已重算";
        }
        _routeWorkspace.UpdatePartitionOverviewEntry(
            context.ToOverviewEntry());

        if (string.Equals(
                _routeActivePartition?.PartitionCode,
                context.PartitionCode,
                StringComparison.OrdinalIgnoreCase))
        {
            ActivatePartitionRouteContext(context);
        }
        else QueueSiteSuggestions();

        SetStatus(
            $"{context.PartitionCode} 已按新的起始侧/角点偏好/架次上限独立重算；其他分区未改变。");
    }

    private void OpenPartitionFromOverview(
        string partitionCode)
    {
        SyncActivePartitionRouteContext();
        if (!_routePartitionContexts.TryGetValue(
                partitionCode,
                out PartitionRouteRuntimeContext? context))
        {
            throw new InvalidOperationException(
                $"未找到分区航线上下文：{partitionCode}");
        }

        if (context.Plan is null)
        {
            _routeWorkspace.SetKmlPath(context.Child.KmlPath);
            _routeWorkspace.SetSourceChoice(RouteSourceChoice.Kml);
            _routeWorkspace.SelectPartitionStartSideText(
                PartitionRouteOverviewPanel.DisplayStartSide(context.StartSide));
            _routeWorkspace.SelectStartDirectionText(
                PartitionRouteOverviewPanel.DisplayStartDirection(context.StartDirection));
            _routeWorkspace.SetMaxSortieKmText(
                context.MaxSortieKm.ToString("0.###", CultureInfo.InvariantCulture));
            _routeWorkspace.SetAglText(
                context.AglM.ToString("0.###", CultureInfo.InvariantCulture));
            GenerateRoutePlanFromKml();
            SyncActivePartitionRouteContext();
            return;
        }

        ActivatePartitionRouteContext(context);
        SetStatus(
            $"已打开 {context.PartitionCode} 精细编辑；其他分区的 RoutePlan 与人工编辑状态保持独立。");
    }

    private void ActivatePartitionRouteContext(
        PartitionRouteRuntimeContext context)
    {
        if (!ResolveSiteDraft("切换分区")) return;
        if (context.Plan is null)
            return;

        RoutePlanResult plan = context.Plan;
        _routeWorkspace.SetKmlPath(context.Child.KmlPath);
        _routeWorkspace.SetCsvPath(plan.SourcePath);
        _routeWorkspace.SetSourceChoice(RouteSourceChoice.Kml);
        _routeWorkspace.SetCoordModeIndex(1);
        _routeWorkspace.SetCentralMeridianText(
            plan.CentralMeridian.ToString(
                "0.########",
                CultureInfo.InvariantCulture));
        _routeWorkspace.SelectPartitionStartSideText(
            PartitionRouteOverviewPanel.DisplayStartSide(context.StartSide));
        _routeWorkspace.SelectStartDirectionText(
            PartitionRouteOverviewPanel.DisplayStartDirection(context.StartDirection));
        _routeWorkspace.SetMaxSortieKmText(
            context.MaxSortieKm.ToString("0.###", CultureInfo.InvariantCulture));
        _routeWorkspace.SetAglText(
            context.AglM.ToString("0.###", CultureInfo.InvariantCulture));
        _routeWorkspace.SetTakeoffAnchorSettings(
            context.TakeoffAnchorMode, context.FlightDirection, context.TakeoffAnchorRef);

        _routeActivePartition = context.Child;
        if (IsProjectInitialized())
            _userContext.SavePartitionState(
                Root,
                context.Child.ManifestPath,
                context.PartitionCode,
                overviewActive: false);
        _routePlan = plan;
        _routeDesignBasis = plan.DesignBasis;
        if (plan.DesignBasis is { } basis) _routeWorkspace.SetLineAzimuth(basis.GridAzimuthDegrees);
        _routeInference = plan.Inference;
        _routeDetectedMode = plan.Mode;
        _routeLoadedFingerprint = File.Exists(plan.SourcePath)
            ? GetRouteSourceFingerprint(plan.SourcePath)
            : "";

        _routeStartMap.Clear();
        var choices = new List<string>();
        foreach (RouteEndpoint point in plan.Inference.Endpoints
                     .OrderBy(point => string.IsNullOrWhiteSpace(point.Label) ? 1 : 0)
                     .ThenBy(point => point.Label.Trim() + point.Pid, StringComparer.OrdinalIgnoreCase))
        {
            _routeStartMap[point.DisplayLabel] = point;
            choices.Add(point.DisplayLabel);
        }
        _routeWorkspace.SetStartPointChoices(choices);
        _routeWorkspace.SetStartPointText(plan.StartPoint.DisplayLabel);

        _routeEditedSorties.Clear();
        _routeEditedSorties.UnionWith(context.EditedSorties);
        _routeReversedSorties.Clear();
        _routeReversedSorties.UnionWith(context.ReversedSorties);
        _routeBoundaryAdjustedSorties.Clear();
        _routeBoundaryAdjustedSorties.UnionWith(context.BoundaryAdjustedSorties);
        SetRouteWorkspaceDirty(context.Dirty);

        RefreshRouteKmlDirectorySelector();
        RefreshRoutePlanUi();
        _routeWorkspace.ShowRoutePreviewTab();
        _routeWorkspace.InvalidateRoutePreview();
    }

    private void SyncActivePartitionRouteContext()
    {
        string code = _routeActivePartition?.PartitionCode
            ?? _routePlan?.PartitionCode
            ?? "";
        if (code.Length == 0 ||
            !_routePartitionContexts.TryGetValue(
                code,
                out PartitionRouteRuntimeContext? context) ||
            _routePlan is null)
        {
            return;
        }

        if (!ReferenceEquals(context.Plan, _routePlan) && File.Exists(_routePlan.SourcePath))
            context.SourceSha256 = GeneratedArtifactIdentity.Hash(_routePlan.SourcePath);
        context.Plan = _routePlan;
        context.RouteSourcePath = _routePlan.SourcePath;
        context.StartSide = (RoutePartitionStartSide)Math.Clamp(
            _routeWorkspace.GetPartitionStartSideIndex(),
            0,
            4);
        context.StartDirection = (RouteStartDirection)Math.Clamp(
            _routeWorkspace.GetStartDirectionIndex(),
            0,
            3);
        if (double.TryParse(
                _routeWorkspace.GetMaxSortieKmText(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double maxSortieKm) &&
            maxSortieKm > 0)
        {
            context.MaxSortieKm = maxSortieKm;
        }
        if (double.TryParse(
                _routeWorkspace.GetAglText(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double aglM) &&
            aglM >= 0)
        {
            context.AglM = aglM;
        }

        context.EditedSorties.Clear();
        context.EditedSorties.UnionWith(_routeEditedSorties);
        context.ReversedSorties.Clear();
        context.ReversedSorties.UnionWith(_routeReversedSorties);
        context.BoundaryAdjustedSorties.Clear();
        context.BoundaryAdjustedSorties.UnionWith(_routeBoundaryAdjustedSorties);
        context.Dirty = _routeWorkspaceDirty;
        context.Status = _routePlan.HasOverLimitSorties
            ? "存在超限架次"
            : context.Dirty
                ? "已修改"
                : "正常";
        _routeWorkspace.UpdatePartitionOverviewEntry(
            context.ToOverviewEntry());
    }

    private void ApplyRouteAdvancedSettings()
    {
        ApplyRouteCornerStart();
        if (_routeActivePartition is null ||
            _routePlan is null)
        {
            return;
        }

        GenerateRoutePlan();
        SyncActivePartitionRouteContext();
    }

    private void GenerateRouteFromSelectedSource()
    {
        // Managed generation writes endpoints and artifact records. Reject an
        // unavailable project lock before any output is generated, while leaving
        // the existing per-operation locks responsible for the actual writes.
        if (IsProjectInitialized())
        {
            using var permissionCheck = WorkspaceFileLock.AcquireForRoot(Root);
        }

        // Once a mother KML has been subdivided, generation must be scoped to
        // an explicitly opened child context. Falling back to the mother KML
        // here silently discards the user's subdivision decision.
        if (_routePartitionContexts.Count > 0 &&
            _routeActivePartition is null &&
            _routeWorkspace.GetSourceChoice() == RouteSourceChoice.Kml)
        {
            _routeWorkspace.ShowPartitionOverviewTab();
            throw new InvalidOperationException(
                "当前 KML 已完成分区，但尚未选择具体分区。请先在“分区总览”中打开 P01/P02 等分区，再生成该分区航线；" +
                "不会按未分区母区静默生成。");
        }

        RouteSourceChoice sourceChoice =
            _routeWorkspace.GetSourceChoice();

        if (sourceChoice == RouteSourceChoice.None)
        {
            if (File.Exists(
                    _routeWorkspace.GetKmlPath()))
            {
                sourceChoice =
                    RouteSourceChoice.Kml;
            }
            else if (File.Exists(
                         _routeWorkspace.GetPlyPath()))
            {
                sourceChoice =
                    RouteSourceChoice.Ply;
            }
            else if (File.Exists(
                         _routeWorkspace.GetCsvPath()))
            {
                sourceChoice =
                    RouteSourceChoice.Csv;
            }

            if (sourceChoice !=
                RouteSourceChoice.None)
            {
                _routeWorkspace.SetSourceChoice(
                    sourceChoice);
            }
        }

        if (sourceChoice ==
            RouteSourceChoice.Kml)
        {
            GenerateRoutePlanFromKml();
            return;
        }

        if (sourceChoice ==
            RouteSourceChoice.Ply)
        {
            GenerateRoutePlanFromPly();
            return;
        }

        if (sourceChoice ==
            RouteSourceChoice.Csv)
        {
            if (!File.Exists(
                    _routeWorkspace.GetCsvPath()))
            {
                ChooseRouteCsv();
            }

            GenerateRoutePlan();
            return;
        }

        throw new InvalidOperationException(
            "请选择一种航线输入：PLY 边界、KML 边界或已有端点 CSV。");
    }


    private void GenerateRoutePlanFromKml()
    {
        TryLoadProjectTerrainForRoute();
        string source = _routeWorkspace.GetKmlPath();
        if (!File.Exists(source))
        {
            ChooseRouteKml();
            source = _routeWorkspace.GetKmlPath();
        }
        if (!File.Exists(source))
            throw new OperationCanceledException("未选择 KML 边界。");

        string rawEndpointPath = PrepareKmlRawEndpointCsv(
            source,
            out string operationKey,
            out string sourceNote);

        string routeReady = PrepareKmlRouteReadyEndpointCsv(
            rawEndpointPath,
            operationKey);
        _routeWorkspace.SetCsvPath(routeReady);
        _routeCoveragePlyPath = "";
        _routeWorkspace.SetCoordModeIndex(1);
        _routeWorkspace.SetSourceChoice(RouteSourceChoice.Kml);
        LoadRouteSourceCore(confirmCentralMeridian: true);
        ApplyPartitionBoundaryStartIfNeeded();
        GenerateRoutePlan();

        SetStatus(
            $"KML 航线已生成：{Path.GetFileName(source)}。{sourceNote}");
    }

    private string PrepareKmlRawEndpointCsv(
        string source,
        out string operationKey,
        out string sourceNote)
    {
        string rawEndpointPath;

        if (_kmlSurveySubdivision.TryResolveGeneratedChild(
                source,
                out KmlSurveySubdivisionResolvedChild? child) &&
            child is not null)
        {
            if (child.ContainsFrozenSnakeExtension)
            {
                throw new InvalidDataException(
                    "该分区由已冻结的蛇形外扩规则生成，并实际包含工区外增加的正式测线。" +
                    "v0.9.2 不再允许该结果继续进入生产航线。请从母区 KML 重新执行分区/生成，" +
                    "新结果不会为了凑偶数向工区外增加测线。");
            }

            _routeActivePartition = child;
            // Critical alignment rule: a MagOps-generated child KML reuses the
            // endpoint geometry clipped from its mother line grid. It is never
            // independently re-planned from the child polygon.
            rawEndpointPath = child.EndpointCsvPath;
            operationKey = "route_kml_subdivision";
            if (double.IsFinite(child.CentralMeridian))
            {
                _routeWorkspace.SetCentralMeridianText(
                    child.CentralMeridian.ToString(
                        "0.########",
                        CultureInfo.InvariantCulture));
            }
            sourceNote =
                $"识别为 {child.PartitionCode}；复用母测线裁切端点，线号保持不变；分区起始侧可在“高级参数”中选择自动/北/南/西/东。";
            return rawEndpointPath;
        }

        _routeActivePartition = null;
        string stem = Path.GetFileNameWithoutExtension(source);
        if (System.Text.RegularExpressions.Regex.IsMatch(
                stem,
                @"(?:_子区\d+|P\d{2})$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            throw new InvalidDataException(
                "该 KML 看起来是 MagOps 分区子区，但未找到对应分区清单/母区裁切端点。" +
                "为避免对子区重新起算而破坏跨区航线对齐，请恢复完整 KML 分区目录，" +
                "或从母区 KML 重新执行分区。 ");
        }

        KmlSurveySubdivisionPreparation preparation =
            PrepareCurrentRouteKmlSubdivision();
        string fileName =
            $"{Path.GetFileNameWithoutExtension(source)}_KML母测线端点.csv";
        rawEndpointPath = ResolveToolOutputFilePath(
            "route_endpoint_design",
            "KML 自动绘制航线端点",
            fileName,
            "航线端点 CSV (*.csv)|*.csv|所有文件 (*.*)|*.*",
            ".csv");
        _plySurveyLines.WriteEndpointCsv(
            rawEndpointPath,
            preparation.MotherPlan);
        if (TryGetManagedOutputContext(out _, out _, out _))
        {
            RegisterGeneratedBusinessFile(
                rawEndpointPath,
                "route_endpoint_design");
        }

        operationKey = "route_endpoint_design";
        sourceNote =
            $"普通 Polygon KML 按当前线距/方位角生成 {preparation.MotherPlan.Lines.Count} 条母测线。";
        return rawEndpointPath;
    }

    private string PrepareKmlRouteReadyEndpointCsv(
        string rawEndpointPath,
        string operationKey)
    {
        if (!_concaveLineService.TryInspectConcaveInputBeforeNumericParse(
                rawEndpointPath,
                out ConcaveLineInspection? inspection) ||
            inspection is null ||
            inspection.DisconnectedLines.Count == 0)
        {
            return Path.GetFullPath(rawEndpointPath);
        }

        string directory =
            Path.GetDirectoryName(
                Path.GetFullPath(rawEndpointPath))
            ?? Environment.CurrentDirectory;
        string output =
            Path.Combine(
                directory,
                Path.GetFileNameWithoutExtension(rawEndpointPath) +
                "_航线可用.csv");

        if (!TryProcessConcaveEndpointTable(
                rawEndpointPath,
                inspection,
                output,
                operationKey,
            // FR-022: KML concave gaps use the safe default SPLIT. Users can
            // still change any row to CONNECT in the repair dialog.
            defaultMissingToSplit: true,
                out ConcaveLineProcessResult? result) ||
            result is null)
        {
            SetStatus(
                $"KML 航线生成已取消：{inspection.DisconnectedLines.Count} 条凹形边界断开测线尚未确认连接/分割规则。");
            throw new OperationCanceledException(
                "已取消凹形边界断开测线的连接/分割处理。");
        }

        SetStatus(
            $"凹形断线规则已应用：连接 {result.ConnectedLineCount} 条，" +
            $"分割 {result.SplitLineCount} 条；继续进入 v25 航线生成。");

        return result.OutputPath;
    }

    private string ResolveManagedWorkareaId(
        string workareaPath)
    {
        if (!IsProjectInitialized())
            return "";

        WorkareaRecord? workarea = _workspace
            .GetWorkareas(Root)
            .FirstOrDefault(item => item.Path.Equals(
                workareaPath,
                StringComparison.OrdinalIgnoreCase));
        return workarea?.WorkareaId?.Trim() ?? "";
    }

    private bool TryResolveCurrentRouteSafetyBoundary(
        out IReadOnlyList<SurveyBoundaryPoint> boundary,
        out string boundaryLabel)
    {
        boundary = [];
        boundaryLabel = "有效工区边界";

        if (_routeActivePartition is not null &&
            File.Exists(_routeActivePartition.KmlPath))
        {
            boundary = _kmlSurveySubdivision.ReadProjectedBoundary(
                _routeActivePartition.KmlPath,
                _routeActivePartition.CentralMeridian,
                _routeActivePartition.Datum);
            boundaryLabel = $"{_routeActivePartition.PartitionCode} 有效边界";
            return boundary.Count >= 3;
        }

        string ply = _routeCoveragePlyPath;
        if (string.IsNullOrWhiteSpace(ply))
            ply = _routeWorkspace.GetPlyPath();
        if (_routeWorkspace.GetSourceChoice() == RouteSourceChoice.Ply &&
            File.Exists(ply))
        {
            boundary = _plySurveyLines.ReadBoundary(ply);
            boundaryLabel = "当前 PLY 工区边界";
            return boundary.Count >= 3;
        }

        string kml = _routeWorkspace.GetKmlPath();
        if (_routeWorkspace.GetSourceChoice() == RouteSourceChoice.Kml &&
            File.Exists(kml))
        {
            double? centralMeridian = null;
            if (double.TryParse(
                    _routeWorkspace.GetCentralMeridianText(),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out double parsedCentralMeridian) &&
                double.IsFinite(parsedCentralMeridian))
            {
                centralMeridian = parsedCentralMeridian;
            }

            boundary = _kmlSurveySubdivision.ReadProjectedBoundary(
                kml,
                centralMeridian,
                "WGS84");
            boundaryLabel = "当前 KML 工区边界";
            return boundary.Count >= 3;
        }

        return false;
    }

    private void EnsureCurrentRoutePlanBoundarySafe(
        RoutePlanResult plan)
    {
        var partition = _routePartitionContexts.Values.FirstOrDefault(c => ReferenceEquals(c.Plan, plan));
        if (partition is not null)
        {
            if (!File.Exists(plan.SourcePath) || partition.SourceSha256.Length == 0 ||
                !GeneratedArtifactIdentity.Hash(plan.SourcePath).Equals(partition.SourceSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"{partition.PartitionCode} 的端点来源缺失或已变化，请核对并重新生成该分区，再导出或保存正式基准。");
            RouteBoundarySafetyService.EnsurePlanInsideBoundary(plan, partition.Boundary, $"{partition.PartitionCode} 有效边界");
            return;
        }
        if (!TryResolveCurrentRouteSafetyBoundary(
                out IReadOnlyList<SurveyBoundaryPoint> boundary,
                out string boundaryLabel))
        {
            return;
        }

        RouteBoundarySafetyService.EnsurePlanInsideBoundary(
            plan,
            boundary,
            boundaryLabel);
    }

    private bool TryProcessConcaveEndpointTable(
        string source,
        ConcaveLineInspection inspection,
        string outputPath,
        string registrationOperationKey,
        bool defaultMissingToSplit,
        out ConcaveLineProcessResult? result)
    {
        result = null;

        bool managed = TryGetManagedOutputContext(
            out _,
            out string workareaPath,
            out string workareaLabel);

        if (!managed)
        {
            workareaPath = "";
            workareaLabel = "临时工具（未绑定项目/工区）";
        }

        string workareaId = managed
            ? ResolveManagedWorkareaId(workareaPath)
            : "";
        string partitionCode = _routeActivePartition?.PartitionCode ?? "";
        string sourceGeometryFingerprint =
            inspection.SourceGeometryFingerprint;

        ConcaveLineRuleProfile profile = managed
            ? _concaveLineRules.Load(
                Root,
                workareaId,
                workareaPath,
                partitionCode,
                sourceGeometryFingerprint)
            : new ConcaveLineRuleProfile
            {
                WorkareaId = workareaId,
                WorkareaPath = workareaPath,
                PartitionCode = partitionCode,
                SourceGeometryFingerprint = sourceGeometryFingerprint
            };

        Dictionary<string, string> geometryByLine =
            inspection.DisconnectedLines
                .ToDictionary(
                    item => item.SourceLine,
                    item => item.GeometryFingerprint,
                    StringComparer.OrdinalIgnoreCase);
        List<ConcaveLineRule> seedRules =
            (profile.Rules ?? [])
                .Where(rule =>
                    geometryByLine.TryGetValue(
                        rule.SourceLine ?? "",
                        out string? currentFingerprint) &&
                    string.Equals(
                        rule.GeometryFingerprint,
                        currentFingerprint,
                        StringComparison.OrdinalIgnoreCase))
                .Select(rule => rule.Clone())
                .ToList();

        if (defaultMissingToSplit)
        {
            HashSet<string> existing =
                seedRules
                    .Select(rule => rule.SourceLine ?? "")
                    .ToHashSet(
                        StringComparer.OrdinalIgnoreCase);

            foreach (ConcaveLineInspectionItem item in
                     inspection.DisconnectedLines)
            {
                if (existing.Contains(item.SourceLine))
                    continue;

                seedRules.Add(
                    new ConcaveLineRule
                    {
                        SourceLine = item.SourceLine,
                        Action = ConcaveLineActions.Split,
                        SecondOutputLine = "",
                        Notes =
                            "默认安全分割：避免航线跨越凹形边界外断口。",
                        GeometryFingerprint =
                            item.GeometryFingerprint
                    });
            }
        }

        using var rulesDialog =
            new ConcaveLineRepairDialog(
                workareaLabel,
                inspection,
                seedRules);

        if (rulesDialog.ShowDialog(this) !=
            DialogResult.OK)
        {
            return false;
        }

        IReadOnlyList<ConcaveLineRule> currentRules =
            rulesDialog.GetRules()
                .Select(rule =>
                {
                    ConcaveLineRule scoped = rule.Clone();
                    if (geometryByLine.TryGetValue(
                            scoped.SourceLine,
                            out string? fingerprint))
                    {
                        scoped.GeometryFingerprint = fingerprint;
                    }
                    return scoped;
                })
                .ToArray();

        IReadOnlyList<SurveyBoundaryPoint>? allowedBoundary = null;
        string boundaryLabel = "有效工区边界";
        if (TryResolveCurrentRouteSafetyBoundary(
                out IReadOnlyList<SurveyBoundaryPoint> resolvedBoundary,
                out string resolvedBoundaryLabel))
        {
            allowedBoundary = resolvedBoundary;
            boundaryLabel = resolvedBoundaryLabel;
        }

        result =
            _concaveLineService.Process(
                source,
                outputPath,
                currentRules,
                allowedBoundary,
                boundaryLabel,
                allowExplicitOutsideConnections: true);

        string splitContextPath =
            _concaveSplitRoutePlans.WriteContext(
                result.OutputPath,
                result);

        if (managed)
        {
            _concaveLineRules.Save(
                Root,
                workareaId,
                workareaPath,
                partitionCode,
                sourceGeometryFingerprint,
                currentRules);

            var generated =
                new List<string>
                {
                    result.OutputPath
                };
            string connectRecord = ExplicitRouteConnectionStore.Sidecar(result.OutputPath);
            if (File.Exists(connectRecord)) generated.Add(connectRecord);
            if (!string.IsNullOrWhiteSpace(
                    splitContextPath) &&
                File.Exists(splitContextPath))
            {
                generated.Add(
                    splitContextPath);
            }

            RegisterGeneratedBusinessFiles(
                generated,
                registrationOperationKey);
        }

        return true;
    }

    private void ChooseRouteCsv(string? selectedPath = null)
    {
        var fileHistory = FileInteractions.For(this, "route-endpoints", projectLevel: false, global: false);
        string filePath = selectedPath?.Trim() ?? "";
        if (filePath.Length == 0)
        {
            using var dialog = new OpenFileDialog
            {
                Filter = "CSV/TXT (*.csv;*.txt;*.tsv)|*.csv;*.txt;*.tsv|所有文件 (*.*)|*.*",
                Title = "选择原始航线端点 CSV",
                InitialDirectory = ResolveRouteSourceInitialDirectory()
            };
            fileHistory.Prepare(dialog, current: _routeWorkspace.GetCsvPath());
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            filePath = dialog.FileName;
        }

        if (!File.Exists(filePath) ||
            !FileDropSupport.HasExtension(filePath, ".csv", ".txt", ".tsv"))
        {
            throw new InvalidDataException("请选择有效的航线端点 CSV/TXT/TSV 文件。");
        }

        _routeWorkspace.SetCsvPath(Path.GetFullPath(filePath));
        _routeWorkspace.SetSourceChoice(RouteSourceChoice.Csv);
        ClearPartitionRouteWorkspace();
        _routeActivePartition = null;
        _routeCoveragePlyPath = "";
        // Keep the output field empty until an actual write/export is requested.
        // RequireRouteOutput() will then lazily resolve and create the managed
        // workarea 04_航线设计与任务文件/05_设计航线 directory.
        LoadRouteSource();
        fileHistory.InputSucceeded(filePath);
    }

    private string ResolveRoutePlyInitialDirectory() =>
        ResolveRouteInputInitialDirectory("coordinate_oasis_ply");

    private string ResolveRouteSourceInitialDirectory() =>
        ResolveRouteInputInitialDirectory("route_endpoint_design");

    private string ResolveRouteInputInitialDirectory(string operationKey)
    {
        try
        {
            if (TryGetManagedOutputContext(
                    out _,
                    out string workareaPath,
                    out _))
            {
                string preferred = ManagedOutputPreview(
                    operationKey,
                    workareaPath);

                if (Directory.Exists(preferred))
                    return preferred;

                string? current = preferred;
                while (!string.IsNullOrWhiteSpace(current))
                {
                    current = Path.GetDirectoryName(current);
                    if (!string.IsNullOrWhiteSpace(current) &&
                        Directory.Exists(current))
                    {
                        return current;
                    }
                }
            }

            return Directory.Exists(Root)
                ? Root
                : Environment.GetFolderPath(
                    Environment.SpecialFolder.MyDocuments);
        }
        catch
        {
            return Directory.Exists(Root)
                ? Root
                : Environment.GetFolderPath(
                    Environment.SpecialFolder.MyDocuments);
        }
    }

    private void ChooseRouteOutput()
    {
        if (TryGetManagedOutputContext(
                out _,
                out _,
                out _))
        {
            string managed = ResolveToolOutputDirectory(
                "route_export",
                "航线 KML/CSV 输出");
            _routeWorkspace.SetOutputPath(managed);
            TransientNotice.Show(
                this,
                "当前已选择项目和工区，航线导出固定归档到项目文件树。",
                "使用项目归档目录");
            return;
        }

        using var dialog = new FolderBrowserDialog
        {
            Description = "当前未选择完整项目/工区上下文；请选择临时统一输出根目录（KML/CSV 将分别进入 01_KML/02_CSV）",
            ShowNewFolderButton = true
        };
        FileInteractions.For(this, "route_export", "output").Prepare(dialog, current: _routeWorkspace.GetOutputPath());
        if (dialog.ShowDialog(this) == DialogResult.OK)
            _routeWorkspace.SetOutputPath(dialog.SelectedPath);
    }

    private void ChooseRouteTerrainDem(string? selectedPath = null)
    {
        var fileHistory = FileInteractions.For(this, "terrain-import", projectLevel: false, global: false);
        if(IsProjectInitialized() && string.IsNullOrWhiteSpace(CurrentWorkareaRelativePath))
            throw new InvalidOperationException("请先选择工区，再选择或导入地形。");
        string filePath = selectedPath?.Trim() ?? "";
        string[] chosenSources = filePath.Length == 0 ? [] : [filePath];
        if (filePath.Length == 0)
        {
            if (IsProjectInitialized()) EnsureDefaultTerrainLibrary();
            using var dialog = new OpenFileDialog {
                Filter = "地形瓦片或集合|*.tif;*.tiff;*.terrain.json", Multiselect = true,
                Title = "导入公共地形库并绑定当前工区（可多选，按列表顺序优先）"
            };
            fileHistory.Prepare(dialog, business: new TerrainLibraryService().Settings().DefaultDirectory);
            if (dialog.ShowDialog(this) != DialogResult.OK) throw new OperationCanceledException();
            chosenSources = dialog.FileNames;
            if (dialog.FileNames.Length > 1)
            {
                if (!IsProjectInitialized() || string.IsNullOrWhiteSpace(CurrentWorkareaRelativePath))
                    throw new InvalidOperationException("多瓦片绑定请先选择项目与工区；临时工具可选择已有瓦片集合。");
                var library = new TerrainLibraryService();
                if(dialog.FileNames.Any(p=>p.EndsWith(".json",StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("集合文件请单独选择；多选仅支持GeoTIFF瓦片。");
                using var tileDialog = new TerrainTileSelectionDialog(library.Import(dialog.FileNames));
                if (tileDialog.ShowDialog(this) != DialogResult.OK) throw new OperationCanceledException();
                var selection = library.Select(tileDialog.SelectedAssets, overlapConfirmed: true);
                filePath = new WorkareaTerrainService().Bind(Root,CurrentWorkareaRelativePath,selection);
            }
            else filePath = dialog.FileName;
        }
        if (IsProjectInitialized()) EnsureDefaultTerrainLibrary();

        if (!File.Exists(filePath) ||
            !FileDropSupport.HasExtension(filePath, ".tif", ".tiff", ".json"))
        {
            throw new InvalidDataException("请选择有效的 GeoTIFF 地形文件。");
        }

        // Validate before archiving so a bad DEM never replaces the active
        // project terrain file.
        var sourceDem = new TerrainSetSampler(filePath);
        string effectivePath = Path.GetFullPath(filePath);
        bool archived = false;

        if (IsProjectInitialized())
        {
            effectivePath = new WorkareaTerrainService().Archive(Root, CurrentWorkareaRelativePath, effectivePath, _folderLayout);
            archived = true;
            string relative = Path.GetRelativePath(Root, effectivePath).Replace('\\', '/');
            RegisterGeneratedBusinessFile(effectivePath, "workarea_terrain");
            _artifacts.Update(Root, relative, role: "registry", processingStage: "coordinate",
                notes: "工区地形瓦片引用；原始瓦片位于公共资料库，项目只保存映射。",
                workareaRef: CurrentWorkareaId, workareaAssignmentStatus: "ASSIGNED");
            _routeTerrainLoadedProjectRoot = TerrainContextKey();
            if (_artifactWorkspace.HasGridColumns())
                RefreshArtifacts();
        }

        var dem = archived
            ? new TerrainSetSampler(effectivePath)
            : sourceDem;
        _routeWorkspace.SetTerrainDemPath(effectivePath);
        _routeWorkspace.SetUseTerrain(true);

        if (_routePlan is not null &&
            !RouteTerrainCoversCurrentWorkarea(dem, out string coverageMessage))
        {
            MessageBox.Show(
                this,
                coverageMessage,
                "地形范围不足",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }

        SetStatus(
            $"已选择{(IsProjectInitialized() ? "并保存工区引用" : "")}地形：{Path.GetFileName(effectivePath)}；" +
            $"EPSG:{dem.Info.Epsg}，覆盖 " +
            $"{dem.Info.MinLongitude:F3}–{dem.Info.MaxLongitude:F3}°E / " +
            $"{dem.Info.MinLatitude:F3}–{dem.Info.MaxLatitude:F3}°N");
        fileHistory.InputSucceeded(chosenSources);
    }

    private string? TryLoadProjectTerrainForRoute(bool updateStatus = false)
    {
        string current = _routeWorkspace.GetTerrainDemPath();
        if (!IsProjectInitialized())
            return File.Exists(current) ? Path.GetFullPath(current) : null;

        string rootFull = TerrainContextKey();
        if (!string.IsNullOrWhiteSpace(_routeTerrainLoadedProjectRoot) &&
            !rootFull.Equals(
                _routeTerrainLoadedProjectRoot,
                StringComparison.OrdinalIgnoreCase))
        {
            _routeWorkspace.SetTerrainDemPath("");
            current = "";
            _routeTerrainLoadedProjectRoot = "";
        }

        _routeTerrainLoadedProjectRoot = rootFull;
        try
        {
            string? bound = new WorkareaTerrainService().Resolve(Root, CurrentWorkareaRelativePath, out bool hasBinding);
            if (hasBinding)
            {
                _routeWorkspace.SetTerrainDemPath(bound!);
                _routeTerrainLoadedProjectRoot = rootFull;
                return bound;
            }
        }
        catch (InvalidDataException ex)
        {
            _routeWorkspace.SetTerrainDemPath("");
            _routeTerrainLoadedProjectRoot = rootFull;
            SetStatus(ex.Message);
            return null;
        }
        string terrainDirectory = _folderLayout.ResolveOperationDirectory(
            Root,
            "project_terrain");
        if (!Directory.Exists(terrainDirectory))
            return File.Exists(current) ? Path.GetFullPath(current) : null;

        string[] candidates = Directory
            .EnumerateFiles(terrainDirectory, "*", SearchOption.TopDirectoryOnly)
            .Where(path => FileDropSupport.HasExtension(path, ".tif", ".tiff"))
            .OrderByDescending(path =>
                Path.GetFileNameWithoutExtension(path)
                    .Equals("项目地形_WGS84", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(File.GetLastWriteTimeUtc)
            .ToArray();

        foreach (string candidate in candidates)
        {
            try
            {
                var dem = new TerrainSetSampler(candidate);
                _routeWorkspace.SetTerrainDemPath(Path.GetFullPath(candidate));
                _routeTerrainLoadedProjectRoot = rootFull;
                if (updateStatus)
                {
                    SetStatus(
                        $"已读取旧项目共享地形（未绑定工区）：{Path.GetFileName(candidate)}；" +
                        $"EPSG:{dem.Info.Epsg}，覆盖 " +
                        $"{dem.Info.MinLongitude:F3}–{dem.Info.MaxLongitude:F3}°E / " +
                        $"{dem.Info.MinLatitude:F3}–{dem.Info.MaxLatitude:F3}°N");
                }
                return Path.GetFullPath(candidate);
            }
            catch
            {
                // A stale/unsupported project file should not prevent entering
                // the route workspace. The explicit terrain chooser will show
                // the detailed validation error when the user selects it.
            }
        }

        return File.Exists(current) ? Path.GetFullPath(current) : null;
    }

    private string RequireRouteKmlTerrain()
    {
        string? projectTerrain = TryLoadProjectTerrainForRoute();
        string path = projectTerrain ?? _routeWorkspace.GetTerrainDemPath();
        if (!File.Exists(path))
        {
            ChooseRouteTerrainDem();
            path = _routeWorkspace.GetTerrainDemPath();
        }

        if (!File.Exists(path))
            throw new OperationCanceledException("未选择航线 KML 地形 GeoTIFF。");

        // A manually typed/external DEM is archived before a project-bound
        // elevated export, so the route result never depends on an unmanaged
        // terrain path outside the project document tree.
        if (IsProjectInitialized() && !IsCurrentProjectTerrainPath(path))
        {
            ChooseRouteTerrainDem(path);
            path = _routeWorkspace.GetTerrainDemPath();
        }

        var dem = new TerrainSetSampler(path);
        if (_routePlan is not null &&
            !RouteTerrainCoversCurrentWorkarea(dem, out string coverageMessage))
        {
            throw new InvalidOperationException(
                coverageMessage +
                "\r\n\r\n当前已选择“使用地形高程”，因此不会继续生成可能缺高程的半套 KML。" +
                "请更换覆盖完整的项目地形，或取消“使用地形高程”后导出无高程 KML。");
        }

        return Path.GetFullPath(path);
    }


    private bool IsCurrentProjectTerrainPath(string path)
    {
        if (!IsProjectInitialized() || !File.Exists(path))
            return false;

        string? bound = new WorkareaTerrainService().Resolve(Root, CurrentWorkareaRelativePath, out bool hasBinding);
        return hasBinding && string.Equals(Path.GetFullPath(path), bound, StringComparison.OrdinalIgnoreCase);
    }

    private string TerrainContextKey() => Path.GetFullPath(Root) + "|" + CurrentWorkareaRelativePath;

    private bool RouteTerrainCoversCurrentWorkarea(
        TerrainSetSampler dem,
        out string message)
    {
        message = "";
        if (_routePlan is null)
            return true;

        IReadOnlyList<(double Lon, double Lat)> points =
            GetCurrentRouteWorkareaLonLat();
        if (points.Count == 0)
            return true;

        var outside = points
            .Where(point => !dem.CanSampleBilinear(point.Lon, point.Lat))
            .ToArray();
        if (outside.Length == 0)
            return true;

        double minLon = points.Min(x => x.Lon);
        double maxLon = points.Max(x => x.Lon);
        double minLat = points.Min(x => x.Lat);
        double maxLat = points.Max(x => x.Lat);
        message =
            $"当前项目地形未完整覆盖工区范围。\r\n\r\n" +
            $"工区约：{minLon:F6}–{maxLon:F6}°E / {minLat:F6}–{maxLat:F6}°N\r\n" +
            $"DEM 约：{dem.Info.MinLongitude:F6}–{dem.Info.MaxLongitude:F6}°E / " +
            $"{dem.Info.MinLatitude:F6}–{dem.Info.MaxLatitude:F6}°N\r\n" +
            $"范围检查未覆盖点：{outside.Length} / {points.Count}。";
        return false;
    }

    private IReadOnlyList<(double Lon, double Lat)> GetCurrentRouteWorkareaLonLat()
    {
        if (_routePlan is null)
            return Array.Empty<(double Lon, double Lat)>();

        if (File.Exists(_routeCoveragePlyPath))
        {
            try
            {
                IReadOnlyList<SurveyBoundaryPoint> boundary =
                    _plySurveyLines.ReadBoundary(_routeCoveragePlyPath);
                return boundary
                    .Select(point =>
                    {
                        var en = RouteCoordinateMath.ResolveProjectedEN(
                            point.X,
                            point.Y);
                        return RouteCoordinateMath.GkToWgs84(
                            en.E,
                            en.N,
                            _routePlan.CentralMeridian);
                    })
                    .ToArray();
            }
            catch
            {
                // Fall back to the loaded route extent if the historical PLY is
                // no longer available or no longer valid.
            }
        }

        return _routePlan.Inference.Endpoints
            .Select(point => RouteCoordinateMath.ToLonLat(
                point,
                _routePlan.Mode,
                _routePlan.CentralMeridian))
            .ToArray();
    }

    private void WarnIfRouteTerrainCoverageIsInsufficient()
    {
        if (!_routeWorkspace.GetUseTerrain() || _routePlan is null)
            return;

        string? terrain = TryLoadProjectTerrainForRoute();
        if (!File.Exists(terrain))
            return;

        var dem = new TerrainSetSampler(terrain);
        if (RouteTerrainCoversCurrentWorkarea(dem, out string message))
            return;

        MessageBox.Show(
            this,
            message +
            "\r\n\r\n请更换覆盖完整的项目地形；如果本次只需要平面航线，可取消“KML 使用地形高程”。",
            "地形范围不足",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);
    }

    private void RunConcaveBoundaryLineRepairFromPath(string? path) =>
        _ = TryRunConcaveBoundaryLineRepair(path);

    private void RunConcaveBoundaryLineRepair() =>
        _ = TryRunConcaveBoundaryLineRepair();

    private bool TryRunConcaveBoundaryLineRepair(string? inputPath = null)
    {
        var fileHistory = FileInteractions.For(this, "route-concave-source");
        string source = string.IsNullOrWhiteSpace(inputPath) ? _routeWorkspace.GetCsvPath() : Path.GetFullPath(inputPath);
        if (!File.Exists(source))
        {
            using var dialog = new OpenFileDialog
            {
                Filter = "航线端点表 (*.csv;*.txt;*.tsv)|*.csv;*.txt;*.tsv|所有文件 (*.*)|*.*",
                Title = "选择凹形边界切割后的航线端点表",
                InitialDirectory = ResolveRouteSourceInitialDirectory()
            };

            fileHistory.Prepare(dialog, current: source);
            if (dialog.ShowDialog(this) != DialogResult.OK)
                throw new OperationCanceledException();

            source = dialog.FileName;
        }

        if (!FileDropSupport.HasExtension(source, ".csv", ".txt", ".tsv"))
            throw new InvalidDataException("凹边修复需要 CSV/TXT/TSV 端点表。");
        ConcaveLineInspection inspection =
            _concaveLineService.Inspect(source);
        fileHistory.InputSucceeded(source);

        if (inspection.DisconnectedLines.Count == 0)
        {
            MessageBox.Show(
                this,
                $"未检测到断开测线。\r\n\r\n源测线：{inspection.SourceLineCount} 条",
                "凹形边界断线处理",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return false;
        }

        string extension =
            Path.GetExtension(source)
                .ToLowerInvariant();
        if (extension is not ".csv" and
            not ".txt" and
            not ".tsv")
        {
            extension = ".csv";
        }

        string outputName =
            $"{Path.GetFileNameWithoutExtension(source)}_" +
            $"凹形断线处理_{DateTime.Now:yyyyMMdd_HHmmss}{extension}";

        string filter = extension switch
        {
            ".txt" =>
                "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
            ".tsv" =>
                "TSV 文件 (*.tsv)|*.tsv|所有文件 (*.*)|*.*",
            _ =>
                "CSV 文件 (*.csv)|*.csv|所有文件 (*.*)|*.*"
        };

        string outputPath =
            ResolveToolOutputFilePath(
                "route_preprocess",
                "凹形边界断线处理输出",
                outputName,
                filter,
                extension);

        if (!TryProcessConcaveEndpointTable(
                source,
                inspection,
                outputPath,
                "route_concave_preprocess",
                defaultMissingToSplit: false,
                out ConcaveLineProcessResult? result) ||
            result is null)
        {
            return false;
        }

        _routeWorkspace.SetSourceChoice(RouteSourceChoice.Csv);
        _routeWorkspace.SetCsvPath(
            result.OutputPath);
        _routeInference = null;
        _routePlan = null;
        _routeLoadedFingerprint = "";

        SetStatus(
            $"凹形断线处理完成：连接 {result.ConnectedLineCount} 条，" +
            $"分割 {result.SplitLineCount} 条；输出 {result.OutputLineCount} 条测线。" +
            $"{Path.GetFileName(result.OutputPath)}");

        TransientNotice.Show(
            this,
            $"凹形断线处理文件已生成：{Path.GetFileName(result.OutputPath)}",
            "文件写入成功");

        // The cleaned table now enters the unchanged v25 L-number pairing pipeline.
        LoadRouteSource();
        return true;
    }

    private void LoadRouteSource() => LoadRouteSourceCore(confirmCentralMeridian: true);

    private void LoadRouteSourceCore(bool confirmCentralMeridian, bool restoringCachedProject = false)
    {
        string source = _routeWorkspace.GetCsvPath();
        if (!File.Exists(source))
            throw new FileNotFoundException("请选择有效的原始航线端点 CSV。", source);

        if (string.IsNullOrWhiteSpace(_routeWorkspace.GetCentralMeridianText()))
            RefreshRouteCentralMeridianFromProject();

        if (_concaveLineService.TryInspectConcaveInputBeforeNumericParse(
                source,
                out ConcaveLineInspection? endpointInspection) &&
            endpointInspection is not null &&
            endpointInspection.DisconnectedLines.Count > 0)
        {
            int disconnected = endpointInspection.DisconnectedLines.Count;
            DialogResult answer = MessageBox.Show(
                this,
                $"检测到 {disconnected} 条凹形边界断开测线。\r\n\r\n" +
                "原始表包含同一 L 号的多段端点（包括 X/Y 同时为空的断段分隔行），" +
                "不能直接交给 v25 的普通双端点配对，否则空坐标会被当成无效数字。\r\n\r\n" +
                "是否现在打开“凹形断线处理”？处理完成后会自动载入生成的干净端点表。",
                "检测到断开测线",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Information);

            if (answer == DialogResult.Yes)
            {
                if (!TryRunConcaveBoundaryLineRepair())
                    throw new OperationCanceledException();
            }
            else
            {
                SetStatus($"航线载入已暂停：{disconnected} 条断开测线尚未处理。");
                throw new OperationCanceledException();
            }

            return;
        }

        string order = _routeWorkspace.GetHeaderlessOrderText();
        var raw = DelimitedTextReader.ReadRawRouteCsv(source, order).ToArray();

        bool needsCentralMeridian = _routeWorkspace.GetCoordModeIndex() switch
        {
            1 => true,
            2 => false,
            _ => !RouteCoordinateMath.IsGeographic(raw)
        };
        if (confirmCentralMeridian && needsCentralMeridian)
            ConfirmRouteCentralMeridian(source, raw, restoringCachedProject);

        _routeInference = _routePlanner.InferSpatialLines(raw);
        _routeLoadedFingerprint = GetRouteSourceFingerprint(source);

        _routeDetectedMode = _routeWorkspace.GetCoordModeIndex() switch
        {
            1 => "projected",
            2 => "geographic",
            _ => RouteCoordinateMath.IsGeographic(_routeInference.Endpoints) ? "geographic" : "projected"
        };

        if (_routeDetectedMode == "projected" && !confirmCentralMeridian)
        {
            double? inferred = RouteCoordinateMath.InferCentralMeridian(raw, source);
            if (string.IsNullOrWhiteSpace(_routeWorkspace.GetCentralMeridianText()) && inferred.HasValue)
                _routeWorkspace.SetCentralMeridianText(inferred.Value.ToString("0.########", CultureInfo.InvariantCulture));
        }

        _routeStartMap.Clear();
        var startPointChoices =
            new List<string>();
        foreach (RouteEndpoint p in _routeInference.Endpoints
                     .OrderBy(p => string.IsNullOrWhiteSpace(p.Label) ? 1 : 0)
                     .ThenBy(p => (p.Label.Trim() + p.Pid), StringComparer.OrdinalIgnoreCase))
        {
            _routeStartMap[p.DisplayLabel] = p;
            startPointChoices.Add(
                p.DisplayLabel);
        }
        _routeWorkspace.SetStartPointChoices(
            startPointChoices);
        ApplyRouteCornerStart();

        var diag = RoutePlanner.GetLineGeometryDiagnostic(_routeInference, _routeDetectedMode);
        string pairing = _routeInference.MatchMode == "L"
            ? $"按 L 线号配对 {_routeInference.OrderedLines.Count} 条测线，归并重复记录 {_routeInference.Merged} 条；L 数字不参与航线顺序。"
            : $"未检测到可用 L 线号，按 XY 几何识别 {_routeInference.OrderedLines.Count} 条测线。";

        _routeWorkspace.SetInfoText(
            $"已读取：{source}\r\n{pairing}\r\n" +
            $"坐标模式：{UiText.Display(_routeDetectedMode)}；中央经线：{_routeWorkspace.GetCentralMeridianText()}。\r\n" +
            $"单条测线方向：{diag.LineDirection}（方位角约 {diag.AzimuthDegrees:F1}°）；" +
            $"架次推进：{diag.ProgressDirection}；单条测线中位长度：{diag.MedianLineKm:F3} km。\r\n" +
            (_routeActivePartition is null
                ? "起始方向按真实东南西北选择；具体起点可手动覆盖。"
                : $"当前分区 {_routeActivePartition.PartitionCode}：可在“高级参数 → 分区起始侧”选择自动/北/南/西/东；" +
                  "相邻子区可分别选择相对方向，使两边从共同边界侧开始。起始方向继续用于所选边界侧上的角点偏好。"));
        SetStatus("航线端点已载入并完成空间识别。");
    }

    private void RefreshRouteCentralMeridianFromProject()
    {
        if (string.IsNullOrWhiteSpace(Root) ||
            !Directory.Exists(Root) ||
            !ProjectPaths.HasProject(Root))
        {
            return;
        }

        WorkspaceRecord workspace =
            _workspace.InvalidateLegacyCoordinateDerivedCentralMeridian(Root);
        if (!workspace.CentralMeridian.HasValue)
        {
            _routeWorkspace.SetCentralMeridianText("");
            return;
        }

        _routeWorkspace.SetCentralMeridianText(
            workspace.CentralMeridian.Value.ToString(
                "0.########",
                CultureInfo.InvariantCulture));
    }

    private void ConfirmRouteCentralMeridian(
        string sourcePath,
        IReadOnlyList<RawRoutePoint> raw,
        bool restoringCachedProject = false)
    {
        WorkspaceRecord? workspace = null;
        if (!string.IsNullOrWhiteSpace(Root) &&
            Directory.Exists(Root) &&
            ProjectPaths.HasProject(Root))
        {
            workspace =
                _workspace.InvalidateLegacyCoordinateDerivedCentralMeridian(Root);
        }

        double? reusable = RouteCentralMeridianPolicy.ReuseForRouteLoad(
            workspace, _routeWorkspace.GetCentralMeridianText().Trim(), restoringCachedProject);
        if (reusable.HasValue)
        {
            _routeWorkspace.SetCentralMeridianText(
                reusable.Value.ToString("0.########", CultureInfo.InvariantCulture));
            return;
        }

        double? projectCentralMeridian = workspace?.CentralMeridian;
        double? inferred =
            RouteCoordinateMath.InferCentralMeridian(
                raw,
                sourcePath);

        string localCandidate =
            _routeWorkspace.GetCentralMeridianText().Trim();
        string current = projectCentralMeridian
            ?.ToString(
                "0.########",
                CultureInfo.InvariantCulture)
            ?? inferred
                ?.ToString(
                    "0.########",
                    CultureInfo.InvariantCulture)
            ?? localCandidate;

        while (true)
        {
            var noteParts = new List<string>();

            if (projectCentralMeridian.HasValue)
            {
                noteParts.Add(
                    $"项目信息已记录中央子午线 " +
                    $"{projectCentralMeridian.Value:0.########}°E" +
                    (string.IsNullOrWhiteSpace(
                        workspace?.CentralMeridianSource)
                        ? "。"
                        : $"；来源：{workspace!.CentralMeridianSource}。"));
            }

            if (inferred.HasValue)
            {
                noteParts.Add(
                    $"当前端点/文件规则自动推断为 " +
                    $"{inferred.Value:0.########}°E。");
            }

            if (projectCentralMeridian.HasValue &&
                inferred.HasValue &&
                Math.Abs(
                    projectCentralMeridian.Value -
                    inferred.Value) > 1e-9)
            {
                noteParts.Add(
                    "项目信息与当前文件自动推断值不同；" +
                    "默认保留项目信息值，请结合反算位置确认。");
            }

            var values = PromptDialog.Show(
                this,
                "确认中央子午线",
                [
                    new PromptField(
                        "cm",
                        "中央子午线 °E",
                        current,
                        Required: true)
                ],
                width: 650,
                note: string.Join(
                    Environment.NewLine,
                    noteParts));

            if (values is null ||
                string.IsNullOrWhiteSpace(values["cm"]))
            {
                throw new OperationCanceledException(
                    "已取消坐标文件载入：尚未确认中央子午线。");
            }

            double cm = ParseRouteNumber(
                values["cm"],
                "中央子午线");

            if (cm is < -180 or > 180)
            {
                MessageBox.Show(
                    this,
                    "中央子午线必须位于 -180° 到 180° 之间。",
                    "中央子午线无效",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                current = values["cm"].Trim();
                continue;
            }

            string fileName = Path.GetFileName(sourcePath);
            string preview =
                $"已选择坐标文件：{fileName}\r\n" +
                $"中央子午线：{cm.ToString("0.########", CultureInfo.InvariantCulture)}°E\r\n";


            if (raw.Count > 0 &&
                !RouteCoordinateMath.IsGeographic(raw))
            {
                RawRoutePoint p0 = raw[0];
                var en =
                    RouteCoordinateMath.ResolveProjectedEN(
                        p0.X,
                        p0.Y);
                var ll =
                    RouteCoordinateMath.GkToWgs84(
                        en.E,
                        en.N,
                        cm);

                preview +=
                    $"坐标轴顺序参数：{_routeWorkspace.GetHeaderlessOrderText()}\r\n" +
                    $"自动解释：{en.Axis}\r\n" +
                    $"示例原坐标：X={p0.X.ToString("0.###", CultureInfo.InvariantCulture)}，" +
                    $"Y={p0.Y.ToString("0.###", CultureInfo.InvariantCulture)}\r\n" +
                    $"示例反算位置：经度 {ll.Lon.ToString("0.##########", CultureInfo.InvariantCulture)}，" +
                    $"纬度 {ll.Lat.ToString("0.##########", CultureInfo.InvariantCulture)}\r\n";

                var extent = raw
                    .Select(point =>
                    {
                        var resolved =
                            RouteCoordinateMath.ResolveProjectedEN(
                                point.X,
                                point.Y);
                        return RouteCoordinateMath.GkToWgs84(
                            resolved.E,
                            resolved.N,
                            cm);
                    })
                    .Where(point =>
                        double.IsFinite(point.Lon) &&
                        double.IsFinite(point.Lat) &&
                        point.Lon is >= -180 and <= 180 &&
                        point.Lat is >= -90 and <= 90)
                    .ToArray();

                if (extent.Length > 0)
                {
                    preview +=
                        $"当前端点反算范围：经度 " +
                        $"{extent.Min(x => x.Lon).ToString("0.##########", CultureInfo.InvariantCulture)}～" +
                        $"{extent.Max(x => x.Lon).ToString("0.##########", CultureInfo.InvariantCulture)}，纬度 " +
                        $"{extent.Min(x => x.Lat).ToString("0.##########", CultureInfo.InvariantCulture)}～" +
                        $"{extent.Max(x => x.Lat).ToString("0.##########", CultureInfo.InvariantCulture)}\r\n";
                }
            }


            preview +=
                "\r\n确认使用这个中央子午线和当前反算范围吗？";

            DialogResult answer = MessageBox.Show(
                this,
                preview,
                "确认反算位置",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (answer == DialogResult.Yes)
            {
                if (workspace is not null && IsProjectInitialized())
                {
                    if (workspace.CentralMeridian.HasValue &&
                        Math.Abs(workspace.CentralMeridian.Value - cm) > 1e-9)
                    {
                        DialogResult updateProject = MessageBox.Show(
                            this,
                            $"当前项目空间基准为 {workspace.CentralMeridian.Value:0.########}°E，" +
                            $"你刚确认的航线中央子午线为 {cm:0.########}°E。\r\n\r\n" +
                            "是否将这个已确认航线值提交为新的项目空间基准？\r\n" +
                            "选择“否”将保留原项目值，并返回中央子午线确认。",
                            "修改项目空间基准",
                            MessageBoxButtons.YesNo,
                            MessageBoxIcon.Warning);

                        if (updateProject != DialogResult.Yes)
                        {
                            current = workspace.CentralMeridian.Value.ToString(
                                "0.########",
                                CultureInfo.InvariantCulture);
                            continue;
                        }
                    }

                    workspace =
                        _workspace.ConfirmProjectCentralMeridian(
                            Root,
                            cm,
                            $"ROUTE_CONFIRMED: {Path.GetFileName(sourcePath)}");
                    projectCentralMeridian = workspace.CentralMeridian;
                    RefreshProject();
                    RefreshArtifactFreshnessStatus();
                }

                _routeWorkspace.SetCentralMeridianText(
                    cm.ToString(
                        "0.########",
                        CultureInfo.InvariantCulture));
                return;
            }

            current = cm.ToString(
                "0.########",
                CultureInfo.InvariantCulture);
        }
    }

    private void EnsureRoutePlanMatchesProjectSpatialReference(
        double routeCentralMeridian)
    {
        if (string.IsNullOrWhiteSpace(Root) ||
            !Directory.Exists(Root) ||
            !ProjectPaths.HasProject(Root))
        {
            return;
        }

        WorkspaceRecord workspace =
            _workspace.InvalidateLegacyCoordinateDerivedCentralMeridian(Root);
        if (!workspace.CentralMeridian.HasValue)
        {
            throw new InvalidOperationException(
                "项目空间基准尚未确认。请重新载入航线输入，并在反算位置确认窗口中显式确认中央子午线；" +
                "坐标工具不会替项目填写该参数。");
        }

        if (Math.Abs(
                workspace.CentralMeridian.Value -
                routeCentralMeridian) > 1e-9)
        {
            throw new InvalidOperationException(
                $"当前航线中央子午线 {routeCentralMeridian:0.########}°E 与项目空间基准 " +
                $"{workspace.CentralMeridian.Value:0.########}°E 不一致。" +
                "请重新载入航线输入并显式确认项目空间基准，禁止使用局部航线参数静默分叉。");
        }
    }

    private void ApplyRouteCornerStart()
    {
        if (_routeInference is null || _routeInference.OrderedLines.Count == 0)
            return;

        var partitionStartSide = (RoutePartitionStartSide)Math.Clamp(
            _routeWorkspace.GetPartitionStartSideIndex(),
            0,
            4);
        bool hasPartitionContext =
            _routeActivePartition is not null ||
            !string.IsNullOrWhiteSpace(_routePlan?.PartitionCode);
        if (hasPartitionContext &&
            partitionStartSide != RoutePartitionStartSide.Automatic)
        {
            var explicitDirection =
                (RouteStartDirection)Math.Clamp(
                    _routeWorkspace.GetStartDirectionIndex(),
                    0,
                    3);
            RouteEndpoint explicitSideStart = RoutePlanner.GetTraversalStartPointFromSide(
                _routeInference.Grouped,
                _routeInference.OrderedLines,
                partitionStartSide,
                explicitDirection,
                _routeDetectedMode);
            _routeWorkspace.SetStartPointText(
                explicitSideStart.DisplayLabel);
            return;
        }

        if (_routeActivePartition is not null)
        {
            ApplyPartitionBoundaryStartIfNeeded();
            return;
        }

        var direction =
            (RouteStartDirection)Math.Clamp(
                _routeWorkspace.GetStartDirectionIndex(),
                0,
                3);
        RouteEndpoint p = RoutePlanner.GetStartPointFromCorner(
            _routeInference.Grouped,
            _routeInference.OrderedLines,
            direction,
            _routeDetectedMode);
        _routeWorkspace.SetStartPointText(
            p.DisplayLabel);
    }

    private void ApplyPartitionBoundaryStartIfNeeded()
    {
        if (_routeActivePartition is null ||
            _routeInference is null ||
            _routeInference.Endpoints.Count == 0)
        {
            return;
        }

        var direction = (RouteStartDirection)Math.Clamp(
            _routeWorkspace.GetStartDirectionIndex(),
            0,
            3);
        var partitionStartSide = (RoutePartitionStartSide)Math.Clamp(
            _routeWorkspace.GetPartitionStartSideIndex(),
            0,
            4);

        if (partitionStartSide != RoutePartitionStartSide.Automatic)
        {
            RouteEndpoint explicitSideStart = RoutePlanner.GetTraversalStartPointFromSide(
                _routeInference.Grouped,
                _routeInference.OrderedLines,
                partitionStartSide,
                direction,
                _routeDetectedMode);
            _routeWorkspace.SetStartPointText(explicitSideStart.DisplayLabel);
            return;
        }

        if (_routeActivePartition.Cuts.Count == 0)
        {
            RouteEndpoint fallback = RoutePlanner.GetStartPointFromCorner(
                _routeInference.Grouped,
                _routeInference.OrderedLines,
                direction,
                _routeDetectedMode);
            _routeWorkspace.SetStartPointText(fallback.DisplayLabel);
            return;
        }

        const double cutToleranceM = 0.25d;
        List<RouteEndpoint> candidates = _routeInference.Endpoints
            .Where(point => _routeActivePartition.Cuts.Any(cut =>
                DistancePointToSegment(
                    point.X,
                    point.Y,
                    cut.Start.X,
                    cut.Start.Y,
                    cut.End.X,
                    cut.End.Y) <= cutToleranceM))
            .ToList();

        RouteEndpoint start = candidates.Count > 0
            ? RoutePlanner.GetTraversalStartPointFromCandidates(
                _routeInference.Grouped,
                _routeInference.OrderedLines,
                candidates,
                direction,
                _routeDetectedMode)
            : RoutePlanner.GetStartPointFromCorner(
                _routeInference.Grouped,
                _routeInference.OrderedLines,
                direction,
                _routeDetectedMode);
        _routeWorkspace.SetStartPointText(start.DisplayLabel);
    }

    private static double DistancePointToSegment(
        double x,
        double y,
        double ax,
        double ay,
        double bx,
        double by)
    {
        double dx = bx - ax;
        double dy = by - ay;
        double lengthSquared = dx * dx + dy * dy;
        if (lengthSquared <= 1e-18)
            return Math.Sqrt((x - ax) * (x - ax) + (y - ay) * (y - ay));

        double t = ((x - ax) * dx + (y - ay) * dy) / lengthSquared;
        t = Math.Clamp(t, 0d, 1d);
        double closestX = ax + t * dx;
        double closestY = ay + t * dy;
        double ex = x - closestX;
        double ey = y - closestY;
        return Math.Sqrt(ex * ex + ey * ey);
    }

    private void GenerateRoutePlan()
    {
        if (!ResolveSiteDraft("重新生成航线")) return;
        bool hasManualEdits =
            _routeEditedSorties.Count > 0 ||
            _routeReversedSorties.Count > 0 ||
            _routeBoundaryAdjustedSorties.Count > 0;
        if (_routePlan is not null &&
            hasManualEdits &&
            !ConfirmContextSwitch(
                "重新生成航线并替换当前人工修改"))
        {
            return;
        }

        TryLoadProjectTerrainForRoute();

        string source = _routeWorkspace.GetCsvPath();
        if (!File.Exists(source) &&
            _routeWorkspace.GetSourceChoice() ==
                RouteSourceChoice.Ply &&
            File.Exists(_routeWorkspace.GetPlyPath()))
        {
            source = GenerateRouteEndpointsFromPlyCore(
                loadSource: true,
                showNotice: false);
        }

        if (_routeInference is null ||
            !File.Exists(source) ||
            !string.Equals(
                _routeLoadedFingerprint,
                GetRouteSourceFingerprint(source),
                StringComparison.Ordinal))
        {
            LoadRouteSource();
            source = _routeWorkspace.GetCsvPath();
        }

        double aglM = ParseRouteNumber(_routeWorkspace.GetAglText(), "离地高程 AGL");
        if (!double.IsFinite(aglM) || aglM < 0)
            throw new InvalidOperationException("离地高程 AGL 必须是大于或等于 0 的有限数字。");

        string startPid = "";
        string startPointText =
            _routeWorkspace.GetStartPointText();
        if (_routeStartMap.TryGetValue(
                startPointText,
                out RouteEndpoint? p))
        {
            startPid = p.Pid;
        }
        else if (!string.IsNullOrWhiteSpace(
                     startPointText))
        {
            var matches = _routeStartMap
                .Where(x =>
                    x.Key.Contains(
                        startPointText,
                        StringComparison.OrdinalIgnoreCase))
                .Select(x => x.Value)
                .DistinctBy(x => x.Pid)
                .ToArray();
            if (matches.Length == 1)
            {
                startPid =
                    matches[0].Pid;
            }
            else
            {
                throw new InvalidOperationException(
                    "请选择一个明确的起始空间点；可以输入点名字符进行唯一匹配。");
            }
        }

        var options = new RoutePlanOptions
        {
            CoordinateMode = _routeWorkspace.GetCoordModeIndex() switch
            {
                1 => RouteCoordinateMode.Projected,
                2 => RouteCoordinateMode.Geographic,
                _ => RouteCoordinateMode.Automatic
            },
            HeaderlessOrder = _routeWorkspace.GetHeaderlessOrderText(),
            CentralMeridian = ParseRouteOptionalDouble(_routeWorkspace.GetCentralMeridianText(), "中央经线"),
            MaxSortieKm = ParseRoutePositive(_routeWorkspace.GetMaxSortieKmText(), "最大单架次长度"),
            StartDirection =
                (RouteStartDirection)Math.Clamp(
                    _routeWorkspace.GetStartDirectionIndex(),
                    0,
                    3),
            PartitionStartSide =
                (RoutePartitionStartSide)Math.Clamp(
                    _routeWorkspace.GetPartitionStartSideIndex(),
                    0,
                    4),
            SelectedStartPid = startPid,
            TakeoffAnchorMode = _routeWorkspace.GetTakeoffAnchorMode(),
            FlightDirection = _routeWorkspace.GetFlightDirection(),
            TakeoffAnchorRef = _routeWorkspace.GetTakeoffAnchorRef(),
            KmzAglM = aglM
            // KmzSpeedMps is intentionally not read here. Flight speed is
            // owned by the frozen real-time terrain-following KMZ workflow,
            // not by ordinary route generation.
        };

        _concaveSplitRoutePlans.TryLoadContext(
            _routeWorkspace.GetCsvPath(),
            out ConcaveSplitRouteContext? splitContext);
        string partitionCode = _routeActivePartition?.PartitionCode ?? "";
        var checkSnapshot = _routePlan?.CheckLines.ToList() ?? [];
        var accessSnapshot = _routePlan?.AccessPlanning ?? LoadAccessPlanning();
        _routePlan = _routePlanningWorkflow.Generate(
            new RoutePlanningRequest(
                _routeWorkspace.GetCsvPath(),
                options,
                splitContext,
                partitionCode,
                _routeActivePartition?.ManifestPath ?? ""));

        _routePlan.DesignBasis = _routeDesignBasis;
        _routePlan.CheckLines = checkSnapshot;
        _routePlan.AccessPlanning = accessSnapshot;
        EnsureRoutePlanMatchesProjectSpatialReference(
            _routePlan.CentralMeridian);
        if (_routeActivePartition is { } active && _routePartitionContexts.TryGetValue(active.PartitionCode, out var directionContext) &&
            options.TakeoffAnchorMode == RouteTakeoffAnchorMode.Automatic && options.PartitionStartSide == RoutePartitionStartSide.Automatic &&
            options.FlightDirection == RouteFlightDirection.Automatic)
            PartitionFlightConsistency.Align(_routePlan, directionContext.MotherFlightDirections);
        EnsureCurrentRoutePlanBoundarySafe(_routePlan);

        _routeEditedSorties.Clear();
        _routeReversedSorties.Clear();
        _routeBoundaryAdjustedSorties.Clear();

        _routeWorkspace.SetCentralMeridianText(
            _routePlan.CentralMeridian.ToString(
                "0.########",
                CultureInfo.InvariantCulture));
        RefreshRoutePlanUi();
        // KML subdivision preview may have been the active tab immediately before
        // child-01 auto generation. Once a route plan exists, return to the actual
        // route preview so users do not see an intentionally cleared subdivision canvas.
        _routeWorkspace.ShowRoutePreviewTab();
        _routeWorkspace.InvalidateRoutePreview();

        var over = _routePlan.Sorties
            .Select((s, i) => new
            {
                Index = i + 1,
                Length = RouteCoordinateMath.RouteLength(
                    s.Points, _routePlan.Mode, _routePlan.CentralMeridian)
            })
            .Where(x => x.Length > options.MaxSortieKm * 1000.0)
            .ToArray();

        string partitionStartSummary = partitionCode.Length > 0
            ? $"分区起始侧：{_routeWorkspace.GetPartitionStartSideText()}；起始角点偏好：{_routeWorkspace.GetStartDirectionText()}。\r\n"
            : "";
        string text =
            $"已生成 {_routePlan.Sorties.Count} 个架次；起点：{_routePlan.StartPoint.Label} / {_routePlan.StartPoint.Pid}；" +
            $"最大单架次 {options.MaxSortieKm:0.###} km；离地高程 AGL {aglM:0.###} m。\r\n" +
            partitionStartSummary +
            $"几何诊断：单条测线={_routePlan.GeometryDiagnostic.LineDirection}，" +
            $"架次推进={_routePlan.GeometryDiagnostic.ProgressDirection}，" +
            $"方位角约={_routePlan.GeometryDiagnostic.AzimuthDegrees:F1}°。\r\n";
        if (_routePlan.Warnings.Count > 0)
            text += string.Join("\r\n", _routePlan.Warnings);
        _routeWorkspace.SetInfoText(
            text);
        WarnIfRouteTerrainCoverageIsInsufficient();

        if (over.Length > 0)
        {
            string examples = string.Join("；",
                _routePlan.OverLimitRoundTrips.Take(3)
                    .Select(x => $"{x.First} / {x.Second}: {x.LengthM / 1000.0:F3} km"));
            MessageBox.Show(this,
                $"检测到最终生成的架次超过最大单架次长度。\r\n\r\n" +
                $"设定上限：{options.MaxSortieKm:0.###} km\r\n" +
                $"超限架次：{over.Length} 个\r\n" +
                $"最长架次：{over.Max(x => x.Length) / 1000.0:F3} km\r\n" +
                (examples.Length > 0 ? $"完整来回超限示例：{examples}\r\n" : "") +
                "\r\n建议增加作业分区或调整架次编排；不得删除正式测线来缩小测区。",
                "架次长度超过上限",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }

        SetRouteWorkspaceDirty(true);
        SyncActivePartitionRouteContext();
        SetStatus($"航线已生成：{_routePlan.Sorties.Count} 个架次；编辑工程尚未保存。");
    }

    private void RefreshRoutePlanUi(int selected = 0) => RenderRoutePlanUi(selected, false);

    private void RenderRoutePlanUi(int selected, bool previewOnly)
    {
        if (_routePlan is null)
            return;

        _routePlan.AccessPlanning ??= LoadAccessPlanning();
        var accessRows = LaunchSitePlanning.Enabled ? (_routeWorkspace.SiteMap.DraftRows ?? RouteAccessPlanner.Assess(_routePlan)) : null;
        if (!previewOnly && LaunchSitePlanning.Enabled)
        {
            _routeWorkspace.SiteMap.AssessedPlan = _routePlan;
            _routeWorkspace.SiteMap.ConfirmedRows = accessRows;
            _routePlan.RoundTripDistanceM = accessRows!.All(a => a.TotalM.HasValue) ? accessRows!.Sum(a => a.TotalM!.Value) : null;
        }
        List<RouteSortieViewRow> rows =
            _routePlan.Sorties
                .Select((s, i) =>
                {
                    double len =
                        RouteCoordinateMath.RouteLength(
                            s.Points,
                            _routePlan.Mode,
                            _routePlan.CentralMeridian);
                    return new RouteSortieViewRow
                    {
                        Index = i + 1,
                        Name = string.IsNullOrWhiteSpace(s.Name)
                            ? $"A{i + 1:D2}"
                            : s.Name,
                        LineCount =
                            s.SourceLines.Count > 0
                                ? s.SourceLines.Count
                                : s.Points
                                    .Select(p => p.Line)
                                    .Where(line =>
                                        !string.IsNullOrWhiteSpace(
                                            line))
                                    .Distinct(
                                        StringComparer.OrdinalIgnoreCase)
                                    .Count(),
                        PointCount =
                            s.Points.Count,
                        LengthKm =
                            len / 1000.0,
                        SiteName = accessRows?[i].SiteName ?? "",
                        OutboundKm = accessRows?[i].OutboundM / 1000,
                        ReturnKm = accessRows?[i].ReturnM / 1000,
                        TotalKm = accessRows?[i].TotalM / 1000,
                        Status =
                            LaunchSitePlanning.Enabled && accessRows?[i].Status is "OK" or "OVER_LIMIT" ? accessRows![i].Status :
                            len >
                            _routePlan.Options.MaxSortieKm *
                            1000.0
                                ? "OVER_LIMIT"
                                : "OK",
                        EditState =
                            GetRouteSortieEditState(
                                s)
                    };
                })
                .ToList();

        _routeWorkspace.ShowSortieRows(
            rows,
            selected);
        RefreshRoutePointGrid();
        if (!previewOnly) { SyncActivePartitionRouteContext(); QueueSiteSuggestions(); }
    }

    private void SplitOverlongRouteLine()
    {
        if (_routePlan is null || _routePlan.Sorties.Count == 0)
            throw new InvalidOperationException("请先生成航线，再切分超长测线。");

        double maxLenM = _routePlan.Options.MaxSortieKm * 1000d;
        string[] candidates = _routePlan.Sorties
            .Where(sortie => RouteCoordinateMath.RouteLength(
                sortie.Points, _routePlan.Mode, _routePlan.CentralMeridian) > maxLenM)
            .SelectMany(sortie => sortie.SourceLines)
            .Where(line => _routePlan.Inference.Grouped.ContainsKey(line))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (candidates.Length == 0)
            throw new InvalidOperationException("当前没有超过最大架次长度的测线或架次；切分仅对超限对象开放。");

        Dictionary<string, string>? values = PromptDialog.Show(
            this,
            "切分超长测线为执行段",
            [
                new PromptField("line", "切分对象", candidates[0], ["全部超限测线", "全部穿过测线", .. candidates], Required: true),
                new PromptField("mode", "切割线模式", "自由绘制", ["自由绘制", "固定 X（南北向）", "固定 Y（东西向）", "边界延长线"], Required: true)
            ],
            width: 560,
            height: 220,
            note: "切分后生成 Lxxx.S1 / Lxxx.S2 执行段，继承母测线身份；默认各自独立成架次，不与未切分测线连接。当前首版支持单条母线一次切分。");
        if (values is null) return;

        string line = values["line"];
        _pendingExecutionSegmentLine = line;
        _pendingExecutionCutMode = values["mode"];
        _routeWorkspace.BeginCutLineDrawing(line);
        SetStatus($"请按“{_pendingExecutionCutMode}”在右侧预览图拖出切割线；该线将批量作用于相交的超限测线。重新绘制即可调整切点。");
        return;
    }

    private string _pendingExecutionSegmentLine = "";
    private string _pendingExecutionCutMode = "自由绘制";

    private void ApplyDrawnExecutionCut(string line, PointF start, PointF end)
    {
        if (_routePlan is null) return;
        line = string.IsNullOrWhiteSpace(line) ? _pendingExecutionSegmentLine : line;
        var a = _routeWorkspace.WorldPoint(start);
        var b = _routeWorkspace.WorldPoint(end);
        if (!a.HasValue || !b.HasValue || string.IsNullOrWhiteSpace(line))
            throw new InvalidOperationException("无法解析手绘切割线，请重新绘制。");
        (double MinX, double MaxX, double MinY, double MaxY) bounds =
            _routeWorkspace.WorldBounds() ?? throw new InvalidOperationException("当前预览没有有效测线范围。");
        double ax = a.Value.X, ay = a.Value.Y, bx = b.Value.X, by = b.Value.Y;
        if (_pendingExecutionCutMode.StartsWith("固定 X", StringComparison.Ordinal))
        {
            double x = (ax + bx) / 2d;
            (ax, ay, bx, by) = (x, bounds.MinY, x, bounds.MaxY);
        }
        else if (_pendingExecutionCutMode.StartsWith("固定 Y", StringComparison.Ordinal))
        {
            double y = (ay + by) / 2d;
            (ax, ay, bx, by) = (bounds.MinX, y, bounds.MaxX, y);
        }
        else if (_pendingExecutionCutMode == "边界延长线")
        {
            (ax, ay, bx, by) = ExtendLineToBounds(ax, ay, bx, by, bounds);
        }
        IEnumerable<string> targets = line.Equals("全部超限测线", StringComparison.OrdinalIgnoreCase)
            ? _routePlan.Sorties.Where(s => RouteCoordinateMath.RouteLength(s.Points, _routePlan.Mode, _routePlan.CentralMeridian) > _routePlan.Options.MaxSortieKm * 1000d).SelectMany(s => s.SourceLines)
            : line.Equals("全部穿过测线", StringComparison.OrdinalIgnoreCase)
                ? _routePlan.Inference.OrderedLines
                : [line];
        RouteExecutionSegmentPlan result =
            _routePlanningWorkflow.ApplyCutLine(_routePlan, targets, ax, ay, bx, by);
        result.Plan.DesignBasis = _routePlan.DesignBasis;
        result.Plan.CheckLines = _routePlan.CheckLines.ToList();
        _routePlan = result.Plan;
        EnsureCurrentRoutePlanBoundarySafe(_routePlan);
        SetRouteWorkspaceDirty(true);
        RefreshRoutePlanUi();
        _routeWorkspace.InvalidateRoutePreview();
        SetStatus($"切割线已处理 {result.Segments.Count / 2} 条测线，生成 {result.Segments.Count} 个执行段；如需调整切点，请重新绘制切割线。保存编辑工程后再导出。");
    }

    private static (double X1, double Y1, double X2, double Y2) ExtendLineToBounds(
        double x1, double y1, double x2, double y2,
        (double MinX, double MaxX, double MinY, double MaxY) bounds)
    {
        double dx = x2 - x1, dy = y2 - y1;
        if (Math.Abs(dx) + Math.Abs(dy) < 1e-12)
            throw new InvalidOperationException("边界延长线需要两个不同的绘制点。");
        var hits = new List<(double T, double X, double Y)>();
        if (Math.Abs(dx) > 1e-12)
        {
            foreach (double x in new[] { bounds.MinX, bounds.MaxX })
            {
                double t = (x - x1) / dx, y = y1 + t * dy;
                if (y >= bounds.MinY - 1e-9 && y <= bounds.MaxY + 1e-9) hits.Add((t, x, y));
            }
        }
        if (Math.Abs(dy) > 1e-12)
        {
            foreach (double y in new[] { bounds.MinY, bounds.MaxY })
            {
                double t = (y - y1) / dy, x = x1 + t * dx;
                if (x >= bounds.MinX - 1e-9 && x <= bounds.MaxX + 1e-9) hits.Add((t, x, y));
            }
        }
        var distinct = hits.DistinctBy(x => (Math.Round(x.X, 9), Math.Round(x.Y, 9)))
            .OrderBy(x => x.T).ToArray();
        if (distinct.Length < 2)
            throw new InvalidOperationException("绘制方向未能穿过当前工区范围包络。");
        return (distinct[0].X, distinct[0].Y, distinct[^1].X, distinct[^1].Y);
    }

    private void RefreshRoutePointGrid()
    {
        if (_routePlan is null)
            return;

        int index =
            _routeWorkspace.GetSelectedSortieIndex();
        if (index < 0 ||
            index >= _routePlan.Sorties.Count)
        {
            return;
        }

        RouteSortie sortie =
            _routePlan.Sorties[index];
        List<RoutePointViewRow> rows =
            sortie.Points
                .Select((p, i) =>
                    new RoutePointViewRow
                    {
                        Sequence = i + 1,
                        PointId = p.Pid,
                        Line = p.Line,
                        Side = p.Endpoint,
                        X = p.X,
                        Y = p.Y,
                        SourceLabel = p.Label
                    })
                .ToList();

        _routeWorkspace.ShowPointRows(
            rows,
            _routePlan,
            index,
            GetRouteSortieEditState(
                sortie));
    }


    private string GetRouteSortieEditState(RouteSortie sortie)
    {
        var states = new List<string>();
        if (_routeBoundaryAdjustedSorties.Contains(sortie))
            states.Add("边界调整");
        if (_routeEditedSorties.Contains(sortie))
            states.Add("已编辑");
        if (_routeReversedSorties.Contains(sortie))
            states.Add("已反转");

        if (states.Count == 0 && sortie.IsManuallyEdited)
            return "手工修改（旧工程/未知类型）";

        return states.Count == 0
            ? "自动生成"
            : string.Join(" + ", states);
    }

    private int SelectedRouteSortieIndex()
    {
        if (_routePlan is null || _routePlan.Sorties.Count == 0)
            throw new InvalidOperationException("尚未生成架次航线。");
        return _routeWorkspace.GetSelectedSortieIndex();
    }

    private void MoveLastTwoRouteLines()
    {
        int i = SelectedRouteSortieIndex();

        if (TryGetCurrentConcaveSplitContext(
                out ConcaveSplitRouteContext? splitContext) &&
            splitContext is not null &&
            i >= 0 &&
            i < _routePlan!.Sorties.Count - 1)
        {
            RouteSortie current = _routePlan.Sorties[i];
            RouteSortie next = _routePlan.Sorties[i + 1];
            List<string> moved = current.SourceLines.TakeLast(2).ToList();
            List<string> proposedNext = moved
                .Concat(next.SourceLines)
                .ToList();

            if (_concaveSplitRoutePlans.MixesProtectedBranches(
                    proposedNext,
                    splitContext))
            {
                throw new InvalidOperationException(
                    "当前航线使用凹形 SPLIT 安全分支。不能把测线移动到另一侧分支，" +
                    "否则会重新产生跨凹口连接。");
            }
        }

        RouteSortie currentSnapshot = CloneRouteSortieState(_routePlan!.Sorties[i]);
        RouteSortie nextSnapshot = CloneRouteSortieState(_routePlan.Sorties[i + 1]);
        RoutePlanner.MoveLastTwoToNext(
            _routePlan.Sorties,
            i,
            _routePlan.Inference.Grouped,
            _routePlan.ActiveStartEndpoint);
        try
        {
            EnsureCurrentRoutePlanBoundarySafe(_routePlan);
        }
        catch
        {
            RestoreRouteSortieState(_routePlan.Sorties[i], currentSnapshot);
            RestoreRouteSortieState(_routePlan.Sorties[i + 1], nextSnapshot);
            throw;
        }
        _routeBoundaryAdjustedSorties.Add(_routePlan.Sorties[i]);
        _routeBoundaryAdjustedSorties.Add(_routePlan.Sorties[i + 1]);
        SetRouteWorkspaceDirty(true);
        RefreshRoutePlanUi(i);
    }

    private void MoveFirstTwoRouteLines()
    {
        int i = SelectedRouteSortieIndex();

        if (TryGetCurrentConcaveSplitContext(
                out ConcaveSplitRouteContext? splitContext) &&
            splitContext is not null &&
            i >= 0 &&
            i < _routePlan!.Sorties.Count - 1)
        {
            RouteSortie current = _routePlan.Sorties[i];
            RouteSortie next = _routePlan.Sorties[i + 1];
            List<string> moved = next.SourceLines.Take(2).ToList();
            List<string> proposedCurrent = current.SourceLines
                .Concat(moved)
                .ToList();

            if (_concaveSplitRoutePlans.MixesProtectedBranches(
                    proposedCurrent,
                    splitContext))
            {
                throw new InvalidOperationException(
                    "当前航线使用凹形 SPLIT 安全分支。不能把测线移动到另一侧分支，" +
                    "否则会重新产生跨凹口连接。");
            }
        }

        RouteSortie currentSnapshot = CloneRouteSortieState(_routePlan!.Sorties[i]);
        RouteSortie nextSnapshot = CloneRouteSortieState(_routePlan.Sorties[i + 1]);
        RoutePlanner.MoveFirstTwoFromNext(
            _routePlan.Sorties,
            i,
            _routePlan.Inference.Grouped,
            _routePlan.ActiveStartEndpoint);
        try
        {
            EnsureCurrentRoutePlanBoundarySafe(_routePlan);
        }
        catch
        {
            RestoreRouteSortieState(_routePlan.Sorties[i], currentSnapshot);
            RestoreRouteSortieState(_routePlan.Sorties[i + 1], nextSnapshot);
            throw;
        }
        _routeBoundaryAdjustedSorties.Add(_routePlan.Sorties[i]);
        _routeBoundaryAdjustedSorties.Add(_routePlan.Sorties[i + 1]);
        SetRouteWorkspaceDirty(true);
        RefreshRoutePlanUi(i);
    }

    private bool TryGetCurrentConcaveSplitContext(
        out ConcaveSplitRouteContext? context)
    {
        context = null;
        string source = _routeWorkspace.GetCsvPath();
        return File.Exists(source) &&
               _concaveSplitRoutePlans.TryLoadContext(
                   source,
                   out context) &&
               context is not null;
    }

    private void ReverseCurrentRouteSortie()
    {
        int i = SelectedRouteSortieIndex();
        RouteSortie sortie = _routePlan!.Sorties[i];
        RoutePlanner.ReverseSortie(sortie);
        if (!_routeReversedSorties.Remove(sortie))
            _routeReversedSorties.Add(sortie);
        SetRouteWorkspaceDirty(true);
        RefreshRoutePlanUi(i);
    }

    private void EditCurrentRouteSortie()
    {
        int i = SelectedRouteSortieIndex();
        RouteSortie sortie = _routePlan!.Sorties[i];
        RouteSortie snapshot = CloneRouteSortieState(sortie);
        using var editor = new RouteEditorDialog(this, sortie, _routePlan.Inference.Endpoints);
        editor.ShowDialog(this);
        if (editor.WasEdited || editor.ReverseParityChanged)
        {
            try
            {
                EnsureCurrentRoutePlanBoundarySafe(_routePlan);
            }
            catch
            {
                RestoreRouteSortieState(sortie, snapshot);
                RefreshRoutePlanUi(i);
                throw;
            }
        }
        if (editor.WasEdited)
        {
            _routeEditedSorties.Add(sortie);
            SetRouteWorkspaceDirty(true);
        }
        if (editor.ReverseParityChanged)
        {
            if (!_routeReversedSorties.Remove(sortie))
                _routeReversedSorties.Add(sortie);
            SetRouteWorkspaceDirty(true);
        }
        RefreshRoutePlanUi(i);
    }

    private static RouteSortie CloneRouteSortieState(RouteSortie source) => new()
    {
        Name = source.Name,
        Points = source.Points.Select(point => point.Clone()).ToList(),
        SourceLines = source.SourceLines.ToList(),
        TaskReferences = source.TaskReferences.ToList()
    };

    private static void RestoreRouteSortieState(
        RouteSortie target,
        RouteSortie snapshot)
    {
        target.Name = snapshot.Name;
        target.Points = snapshot.Points.Select(point => point.Clone()).ToList();
        target.SourceLines = snapshot.SourceLines.ToList();
        target.TaskReferences = snapshot.TaskReferences.ToList();
    }

    private void ExportRouteSubset(bool kml, bool csv, bool kmz)
    {
        if (kmz)
            throw new InvalidOperationException(
                "KMZ 导出当前已冻结：实际测试发现未解决错误，等待后续专项修正后再恢复。");

        if (_routePlan is null && _routePartitionContexts.Count > 0)
        {
            ExportPartitionRouteSubset(kml, csv);
            return;
        }
        if (_routePlan is null)
            GenerateRoutePlan();
        EnsureCurrentRoutePlanBoundarySafe(_routePlan!);

        string output = RequireRouteOutput();
        string workareaName = CurrentRouteWorkareaName();
        bool kmlIncludeElevation = kml && _routeWorkspace.GetUseTerrain();
        string? kmlTerrain = kmlIncludeElevation ? RequireRouteKmlTerrain() : null;
        double kmlAglM = kmlIncludeElevation
            ? ParseRouteNumber(_routeWorkspace.GetAglText(), "目标 AGL")
            : 0.0;

        IReadOnlyList<string> planned =
            _routeExporter.GetSeparatedOutputPaths(
                _routePlan!,
                output,
                workareaName,
                kml,
                csv,
                kmz);
        ConfirmOverwriteFilesIfNeeded(
            planned,
            "航线导出");

        RouteWorkflowExportResult routeExport =
            _routePlanningWorkflow.Export(
                _routePlan!,
                new RouteExportRequest(
                    Root,
                    output,
                    workareaName,
                    kml,
                    csv,
                    new GeneratedArtifactContext(
                        _workspace.GetWorkspace(Root).ProjectId,
                        CurrentWorkareaId),
                    kmlTerrain,
                    kmlAglM,
                    kmlIncludeElevation));
        RouteExportBundle bundle = routeExport.Bundle;
        if (routeExport.RegisteredArtifacts.Count > 0 &&
            _artifactWorkspace.HasGridColumns())
        {
            ApplyArtifactChanges(routeExport.RegisteredArtifacts);
        }
        AutoSaveRouteProjectSnapshot(_routePlan!, output, workareaName);
        FileInteractions.For(this, "route_export", "output").OutputSucceeded(output, directory: true);

        string kmlMode = kml
            ? (kmlIncludeElevation ? "KML=DEM+AGL 高程" : "KML=无高程")
            : "";
        SetStatus(
            $"航线导出完成：{bundle.OutputPaths.Count} 个文件 → " +
            $"{bundle.OutputDirectory}（KML/CSV 分别归档到 01_KML/02_CSV）" +
            (kmlMode.Length > 0 ? $"；{kmlMode}" : ""));
        TransientNotice.Show(this, $"航线文件已生成：{bundle.OutputPaths.Count} 个文件", "文件写入成功");
    }

    private void ExportCurrentRouteSubset(bool kml, bool csv, bool kmz)
    {
        if (kmz)
            throw new InvalidOperationException(
                "KMZ 导出当前已冻结：实际测试发现未解决错误，等待后续专项修正后再恢复。");

        if (_routePlan is null)
            GenerateRoutePlan();
        EnsureCurrentRoutePlanBoundarySafe(_routePlan!);

        int index = SelectedRouteSortieIndex();
        int number = index + 1;
        RouteSortie sortie = _routePlan!.Sorties[index];
        string sortieStem = RouteExportService.GetSortieFileStem(
            sortie,
            number);

        string output = RequireRouteOutput();
        string workareaName = CurrentRouteWorkareaName();
        bool kmlIncludeElevation = kml && _routeWorkspace.GetUseTerrain();
        string? kmlTerrain = kmlIncludeElevation ? RequireRouteKmlTerrain() : null;
        double kmlAglM = kmlIncludeElevation
            ? ParseRouteNumber(_routeWorkspace.GetAglText(), "目标 AGL")
            : 0.0;
        RouteExportDirectoryLayout plannedLayout =
            _routeExporter.GetSeparatedOutputLayout(
                output,
                workareaName);

        var plannedPaths = new List<string>();
        if (kml)
        {
            plannedPaths.Add(Path.Combine(
                plannedLayout.KmlDirectory,
                sortieStem + ".kml"));
        }
        if (csv)
        {
            plannedPaths.Add(Path.Combine(
                plannedLayout.CsvDirectory,
                sortieStem + ".csv"));
        }
        if (kmz)
        {
            plannedPaths.Add(Path.Combine(
                plannedLayout.KmzDirectory,
                sortieStem + "_实时仿地.kmz"));
        }
        ConfirmOverwriteFilesIfNeeded(
            plannedPaths,
            "当前架次导出");

        RouteExportDirectoryLayout layout =
            _routeExporter.PrepareSeparatedOutputDirectories(
                output,
                workareaName,
                kml,
                csv,
                kmz);

        var paths = new List<string>();

        if (kml)
        {
            string kmlPath = Path.Combine(
                layout.KmlDirectory,
                sortieStem + ".kml");
            paths.Add(kmlIncludeElevation
                ? _routeExporter.WriteSortieKml(
                    kmlPath,
                    sortie,
                    number,
                    _routePlan.Mode,
                    _routePlan.CentralMeridian,
                    kmlTerrain!,
                    kmlAglM)
                : _routeExporter.WriteSortieKmlWithoutElevation(
                    kmlPath,
                    sortie,
                    number,
                    _routePlan.Mode,
                    _routePlan.CentralMeridian));
        }

        if (csv)
        {
            paths.Add(_routeExporter.WriteSortieCsv(
                Path.Combine(
                    layout.CsvDirectory,
                    sortieStem + ".csv"),
                sortie,
                number,
                _routePlan.Mode,
                _routePlan.CentralMeridian));
        }

        if (kmz)
        {
            paths.Add(_routeExporter.WriteDjiKmz(
                Path.Combine(
                    layout.KmzDirectory,
                    sortieStem + "_实时仿地.kmz"),
                sortie,
                _routePlan.Mode,
                _routePlan.CentralMeridian,
                _routePlan.Options.KmzAglM,
                _routePlan.Options.KmzSpeedMps));
        }

        RegisterGeneratedBusinessFiles(
            paths,
            "route_export");
        AutoSaveRouteProjectSnapshot(_routePlan!, output, workareaName);
        FileInteractions.For(this, "route_export", "output").OutputSucceeded(layout.BaseDirectory, directory: true);

        SetStatus(
            $"当前架次导出完成：{sortieStem}，{paths.Count} 个文件 → " +
            $"{layout.BaseDirectory}" +
            (kml ? $"；KML={(kmlIncludeElevation ? "DEM+AGL 高程" : "无高程")}" : ""));
        TransientNotice.Show(this, $"当前架次文件已生成：{paths.Count} 个文件", "文件写入成功");
    }

    private void ExportRouteAllAndRegister()
    {
        if (_routePlan is null && _routePartitionContexts.Count > 0)
        {
            ExportPartitionRouteSubset(kml: true, csv: true);
            return;
        }
        if (_routePlan is null)
            GenerateRoutePlan();
        EnsureCurrentRoutePlanBoundarySafe(_routePlan!);

        bool managed = TryGetManagedOutputContext(
            out _,
            out _,
            out _);

        string output = RequireRouteOutput();
        string workareaName = CurrentRouteWorkareaName();
        bool kmlIncludeElevation = _routeWorkspace.GetUseTerrain();
        string? kmlTerrain = kmlIncludeElevation ? RequireRouteKmlTerrain() : null;
        double kmlAglM = kmlIncludeElevation
            ? ParseRouteNumber(_routeWorkspace.GetAglText(), "目标 AGL")
            : 0.0;

        var planned = _routeExporter
            .GetSeparatedOutputPaths(
                _routePlan!,
                output,
                workareaName,
                true,
                true,
                false)
            .ToList();

        if (managed && IsProjectInitialized())
        {
            planned.Add(
                Path.Combine(
                    output,
                    $"{WorkspaceService.Slug(workareaName)}_" +
                    "航线结果清单_route-v25.json"));
        }

        ConfirmOverwriteFilesIfNeeded(
            planned,
            "统一导出 KML + CSV");

        RouteExportBundle bundle;
        RouteWorkflowExportResult? routeExport = null;
        if (managed && IsProjectInitialized())
        {
            routeExport =
                _routePlanningWorkflow.Export(
                    _routePlan!,
                    new RouteExportRequest(
                        Root,
                        output,
                        workareaName,
                        true,
                        true,
                        new GeneratedArtifactContext(
                            _workspace.GetWorkspace(Root).ProjectId,
                            CurrentWorkareaId),
                        kmlTerrain,
                        kmlAglM,
                        kmlIncludeElevation));
            bundle = routeExport.Bundle;
            if (routeExport.RegisteredArtifacts.Count > 0 &&
                _artifactWorkspace.HasGridColumns())
                ApplyArtifactChanges(routeExport.RegisteredArtifacts);
        }
        else
        {
            // Temporary tool mode has no project/workarea authority and is intentionally
            // outside formal managed production registration.
            bundle = _routeExporter.ExportAllSeparated(
                _routePlan!, output, workareaName, true, true, false,
                kmlTerrain, kmlAglM, kmlIncludeElevation);
        }

        string taskBaselinePath = "";
        if (managed && IsProjectInitialized())
        {
            taskBaselinePath =
                SaveManagedRouteTaskBaseline(
                    output);
        }

        if (!managed || !IsProjectInitialized())
        {
            SetStatus(
                $"KML/CSV 已临时导出：{bundle.OutputPaths.Count} 个文件；" +
                $"KML={(kmlIncludeElevation ? "DEM+AGL 高程" : "无高程")}；" +
                "未写入项目结果登记。");

            TransientNotice.Show(
                this,
                $"航线文件已生成：{bundle.OutputPaths.Count} 个文件",
                "文件写入成功");
            return;
        }

        WorkspaceRecord workspace =
            _workspace.GetWorkspace(Root);

        RouteWorkflowFinalizeResult finalized =
            _routePlanningWorkflow.FinalizeManagedExport(
                _routePlan!,
                routeExport!,
                Root,
                Path.Combine(
                    output,
                    $"{WorkspaceService.Slug(workareaName)}_" +
                    "航线结果清单_route-v25.json"),
                new GeneratedArtifactContext(
                    workspace.ProjectId,
                    CurrentWorkareaId));
        string manifest = finalized.ManifestPath;
        var imported = finalized.ImportedResult;

        RefreshArtifacts();
        RefreshProject();

        SetStatus(
            $"KML/CSV 已统一导出并登记：{bundle.OutputPaths.Count} 个文件；" +
            $"测线任务基准已保存：{Path.GetFileName(taskBaselinePath)}；" +
            $"KML={(kmlIncludeElevation ? "DEM+AGL 高程" : "无高程")}；" +
            $"结果状态 {UiText.Display(imported.Status)}。");

        if (imported.Status == "PASSED")
        {
            TransientNotice.Show(
                this,
                $"航线 KML/CSV 与结果清单已生成：" +
                $"{bundle.OutputPaths.Count + 1} 个文件",
                "文件写入成功");
        }
        else
        {
            MessageBox.Show(
                this,
                $"航线文件已经写入，但管理平台结果状态为：" +
                $"{UiText.Display(imported.Status)}\r\n" +
                $"结果清单：{manifest}",
                "航线结果需要复核",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void ExportPartitionRouteSubset(bool kml, bool csv)
    {
        if (_routePartitionContexts.Count == 0)
            throw new InvalidOperationException("当前没有可导出的分区航线。");

        RoutePlanResult[] plans = _routePartitionContexts.Values
            .OrderBy(context => context.Index)
            .Where(context => context.Plan is not null)
            .Select(context => context.Plan!)
            .ToArray();
        if (plans.Length == 0)
            throw new InvalidOperationException("分区航线尚未生成完成，暂时没有可导出的子区。");
        foreach (var plan in plans) EnsureCurrentRoutePlanBoundarySafe(plan);

        string output = RequireRouteOutput();
        string workareaName = CurrentRouteWorkareaName();
        bool kmlIncludeElevation = kml && _routeWorkspace.GetUseTerrain();
        string? kmlTerrain = kmlIncludeElevation ? RequireRouteKmlTerrain() : null;
        double kmlAglM = kmlIncludeElevation
            ? ParseRouteNumber(_routeWorkspace.GetAglText(), "目标 AGL")
            : 0.0;

        var planned = plans
            .SelectMany(plan => _routeExporter.GetSeparatedOutputPaths(
                plan, output, workareaName, kml, csv, false))
            .ToList();
        ConfirmOverwriteFilesIfNeeded(planned, "分区航线导出");

        var outputs = new List<string>();
        foreach (RoutePlanResult plan in plans)
        {
            EnsureCurrentRoutePlanBoundarySafe(plan);
            RouteWorkflowExportResult routeExport =
                _routePlanningWorkflow.Export(
                    plan,
                    new RouteExportRequest(
                        Root,
                        output,
                        workareaName,
                        kml,
                        csv,
                        new GeneratedArtifactContext(
                            _workspace.GetWorkspace(Root).ProjectId,
                            CurrentWorkareaId),
                        kmlTerrain,
                        kmlAglM,
                        kmlIncludeElevation));
            outputs.AddRange(routeExport.Bundle.OutputPaths);
            if (routeExport.RegisteredArtifacts.Count > 0 &&
                _artifactWorkspace.HasGridColumns())
                ApplyArtifactChanges(routeExport.RegisteredArtifacts);
            AutoSaveRouteProjectSnapshot(plan, output, workareaName);
        }
        SetStatus($"分区航线导出完成：{plans.Length} 个子区，共 {outputs.Count} 个 CSV/KML 文件；未回退到完整工区。");
        TransientNotice.Show(this, $"已导出 {plans.Length} 个切分子区的 CSV/KML", "分区航线导出完成");
    }

    private string AutoSaveRouteProjectSnapshot(
        RoutePlanResult plan,
        string routeOutput,
        string workareaName)
    {
        if (!IsProjectInitialized() || string.IsNullOrWhiteSpace(plan.SourcePath))
            return "";

        string relativeWorkarea = CurrentWorkareaRelativePath;
        if (string.IsNullOrWhiteSpace(relativeWorkarea))
            return "";

        string directory = _folderLayout.EnsureOperationDirectory(
            Root,
            "route_project",
            relativeWorkarea);
        string suffix = string.IsNullOrWhiteSpace(plan.PartitionCode)
            ? "自动恢复"
            : $"{plan.PartitionCode}_自动恢复";
        string path = Path.Combine(
            directory,
            $"{WorkspaceService.Slug(workareaName)}_{suffix}_外业测线任务基准.json");

        _routePlanningWorkflow.SaveProject(
            path,
            plan,
            routeOutput,
            _routeWorkspace.GetHeaderlessOrderText(),
            _routeWorkspace.GetCoordModeText(),
            _routeWorkspace.GetStartDirectionText(),
            _routeWorkspace.GetPartitionStartSideText(),
            CaptureRouteSubdivisionWorkspace(plan));
        _userContext.SaveLastRouteProject(Root, path);
        RegisterGeneratedBusinessFiles([path], "route_project");
        return path;
    }

    private string SaveManagedRouteTaskBaseline(
        string routeOutput)
    {
        if (_routePlan is null)
            throw new InvalidOperationException("尚未生成航线计划。");
        foreach (var context in _routePartitionContexts.Values.Where(c => c.Plan is not null))
            EnsureCurrentRoutePlanBoundarySafe(context.Plan!);

        if (!TryGetManagedOutputContext(
                out string projectName,
                out string workareaRelativePath,
                out string workareaName))
        {
            return "";
        }

        string directory =
            _folderLayout.EnsureOperationDirectory(
                Root,
                "route_project",
                workareaRelativePath);
        string path =
            Path.Combine(
                directory,
                $"{projectName}_{workareaName}" +
                (string.IsNullOrWhiteSpace(_routePlan.PartitionCode)
                    ? ""
                    : $"_{_routePlan.PartitionCode}") +
                "_外业测线任务基准.json");

        _routePlanningWorkflow.SaveProject(
            path,
            _routePlan,
            routeOutput,
            _routeWorkspace.GetHeaderlessOrderText(),
            _routeWorkspace.GetCoordModeText(),
            _routeWorkspace.GetStartDirectionText(),
            _routeWorkspace.GetPartitionStartSideText(),
            CaptureRouteSubdivisionWorkspace(_routePlan));

        if (IsProjectInitialized())
            _userContext.SaveLastRouteProject(Root, path);

        RegisterGeneratedBusinessFiles(
            [path],
            "route_project");
        return path;
    }

    private void SaveRouteProject()
    {
        RoutePlanResult? savePlan = _routePlan ?? _routePartitionContexts.Values.OrderBy(c => c.Index)
            .Select(c => c.Plan).FirstOrDefault(p => p is not null);
        if (savePlan is null && _routePartitionContexts.Values.FirstOrDefault() is { } pending)
            savePlan = new RoutePlanResult { AccessPlanning = LoadAccessPlanning(), SourcePath = pending.RouteSourcePath,
                PartitionCode = pending.PartitionCode, PartitionManifestPath = pending.Child.ManifestPath,
                CentralMeridian = pending.Child.CentralMeridian, Options = new() { MaxSortieKm = pending.MaxSortieKm } };
        if (savePlan is null)
        {
            GenerateRoutePlan();
            savePlan = _routePlan!;
        }
        // Editing snapshots preserve pending/stale sources. Formal route exports
        // and task baselines retain their source and boundary safety gates.
        if (_routePartitionContexts.Count == 0)
            EnsureCurrentRoutePlanBoundarySafe(savePlan);

        bool managed = TryGetManagedOutputContext(
            out string projectName,
            out _,
            out string workareaName);

        string partitionSuffix = !string.IsNullOrWhiteSpace(savePlan.PartitionCode)
                ? $"_{savePlan.PartitionCode}"
                : "";
        string defaultName = managed
            ? $"{projectName}_{workareaName}{partitionSuffix}_绘制航线工程.json"
            : $"绘制航线工程{partitionSuffix}.json";

        var values = PromptDialog.Show(
            this,
            "保存绘制航线编辑工程",
            [
                new PromptField(
                    "name",
                    "文件名",
                    defaultName,
                    Required: true)
            ],
            note: managed
                ? "当前已选择项目和工区；工程文件写入受管目录。"
                : "当前未同时选择项目和工区；工程文件将按临时工具使用，手动选择保存位置。");

        if (values is null)
            return;

        string fileName =
            Path.GetFileName(values["name"].Trim());

        if (!fileName.EndsWith(
                ".json",
                StringComparison.OrdinalIgnoreCase))
        {
            fileName += ".json";
        }

        string path = ResolveToolOutputFilePath(
            "route_project",
            "保存绘制航线编辑工程",
            fileName,
            "JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*",
            ".json");

        string routeOutput = RequireRouteOutput();
        _routeWorkspace.SetOutputPath(
            routeOutput);

        _routePlanningWorkflow.SaveProject(
            path,
            savePlan,
            routeOutput,
            _routeWorkspace.GetHeaderlessOrderText(),
            _routeWorkspace.GetCoordModeText(),
            _routeWorkspace.GetStartDirectionText(),
            _routeWorkspace.GetPartitionStartSideText(),
            CaptureRouteSubdivisionWorkspace(savePlan));

        if (IsProjectInitialized())
            _userContext.SaveLastRouteProject(Root, path);

        RegisterGeneratedBusinessFiles(
            [path],
            "route_project");

        SetRouteWorkspaceDirty(false);
        foreach (var context in _routePartitionContexts.Values)
        {
            context.Dirty = false;
            _routeWorkspace.UpdatePartitionOverviewEntry(context.ToOverviewEntry());
        }
        _dirtyStateCoordinator.NotifyChanged();

        SetStatus(
            managed
                ? $"航线工程已保存：{path}"
                : $"航线工程已临时保存：{path}（未写入项目登记）");

        TransientNotice.Show(
            this,
            $"航线工程已生成：{Path.GetFileName(path)}",
            "文件写入成功");
    }

    private void LoadRouteProject(string? selectedPath = null, bool restoringCachedProject = false)
    {
        var fileHistory = FileInteractions.For(this, "route-project", projectLevel: false, global: false);
        string filePath = selectedPath?.Trim() ?? "";
        if (filePath.Length == 0)
        {
            using var dialog = new OpenFileDialog
            {
                Filter = "JSON (*.json)|*.json|所有文件 (*.*)|*.*",
                Title = "载入绘制航线工程"
            };
            fileHistory.Prepare(dialog);
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            filePath = dialog.FileName;
        }

        if (!File.Exists(filePath) || !FileDropSupport.HasExtension(filePath, ".json"))
            throw new InvalidDataException("请选择有效的航线工程 JSON。");

        if (!ResolveSiteDraft("载入工程")) return;
        if (RouteWorkspaceHasUnsavedChanges &&
            !ConfirmContextSwitch(
                "载入航线工程并替换当前未保存航线"))
        {
            return;
        }

        void RememberOpenedProject()
        {
            fileHistory.InputSucceeded(filePath);
            if (IsProjectInitialized()) _userContext.SaveLastRouteProject(Root, filePath);
        }
        RouteProjectFile project = _routePlanningWorkflow.LoadProject(filePath);
        // Startup cache restoration must never solicit a new spatial-reference
        // decision or promote the cached route value to project authority.
        if (restoringCachedProject && RouteCentralMeridianPolicy.ConfirmedProjectValue(
                _workspace.InvalidateLegacyCoordinateDerivedCentralMeridian(Root)) is null)
        {
            SetStatus("项目中央子午线尚未确认，已跳过航线工程自动恢复；可手动载入航线工程完成首次确认。");
            return;
        }
        if (project.SubdivisionWorkspace is { } subdivision)
        {
            EnsureRoutePlanMatchesProjectSpatialReference(project.CentralMeridian);
            _routeWorkspace.SetOutputPath(project.OutputDirectory);
            _routeWorkspace.SelectHeaderlessOrderText(project.HeaderlessOrder);
            RestoreRouteSubdivisionWorkspace(subdivision);
            if (!restoringCachedProject) RememberOpenedProject();
            SetStatus($"已恢复完整分区工程：{subdivision.Partitions.Count} 个子区（保留各自人工编辑）；来源变化见分区状态。");
            return;
        }
        if (!File.Exists(project.CsvPath))
            throw new FileNotFoundException("工程引用的原始端点 CSV 已不存在，无法恢复可编辑航线。", project.CsvPath);
        ClearPartitionRouteWorkspace();
        // Loading a saved route project must not inherit the partition context
        // from whichever generated child KML happened to be active in the UI.
        _routeActivePartition = null;
        bool sourceChanged = !string.IsNullOrWhiteSpace(project.CsvLastWriteUtc) &&
            !string.Equals(
                project.CsvLastWriteUtc,
                File.GetLastWriteTimeUtc(project.CsvPath).ToString("O"),
                StringComparison.Ordinal);

        _routeWorkspace.SetCsvPath(
            project.CsvPath);
        _routeCoveragePlyPath = "";
        _routeWorkspace.SelectCoordModeText(
            project.CoordMode);
        _routeWorkspace.SelectHeaderlessOrderText(
            project.HeaderlessOrder);
        _routeWorkspace.SetCentralMeridianText(
            project.CentralMeridian.ToString(
                "0.########",
                CultureInfo.InvariantCulture));
        if (restoringCachedProject)
            RefreshRouteCentralMeridianFromProject();
        _routeWorkspace.SetMaxSortieKmText(
            project.MaxSortieKm.ToString(
                "0.###",
                CultureInfo.InvariantCulture));
        _routeWorkspace.SetOutputPath(
            project.OutputDirectory);
        _routeWorkspace.SelectStartDirectionText(
            project.StartDirection);
        _routeWorkspace.SelectPartitionStartSideText(
            project.PartitionStartSide);
        _routeWorkspace.SetTakeoffAnchorSettings(
            Enum.TryParse<RouteTakeoffAnchorMode>(project.TakeoffAnchorMode, true, out var loadedAnchorMode) ? loadedAnchorMode : RouteTakeoffAnchorMode.Automatic,
            Enum.TryParse<RouteFlightDirection>(project.FlightDirection, true, out var loadedFlightDirection) ? loadedFlightDirection : RouteFlightDirection.Automatic,
            project.TakeoffAnchorRef);
        _routeWorkspace.SetAglText(
            project.KmzAglM.ToString(
                "0.###",
                CultureInfo.InvariantCulture));
        _routeWorkspace.SetSpeedText(
            project.KmzSpeedMps.ToString(
                "0.###",
                CultureInfo.InvariantCulture));

        LoadRouteSourceCore(confirmCentralMeridian: true, restoringCachedProject: restoringCachedProject);
        var start = _routeInference!.Endpoints.FirstOrDefault(
            x => x.Pid.Equals(project.SelectedStartPid, StringComparison.OrdinalIgnoreCase));
        if (start is not null)
        {
            _routeWorkspace.SetStartPointText(
                start.DisplayLabel);
        }
        GenerateRoutePlan();
        _routePlan!.Options.TakeoffAnchorMode = Enum.TryParse<RouteTakeoffAnchorMode>(project.TakeoffAnchorMode, true, out var anchorMode) ? anchorMode : RouteTakeoffAnchorMode.Automatic;
        _routePlan.Options.FlightDirection = Enum.TryParse<RouteFlightDirection>(project.FlightDirection, true, out var flightDirection) ? flightDirection : RouteFlightDirection.Automatic;
        _routePlan.Options.TakeoffAnchorRef = project.TakeoffAnchorRef ?? "";
        _routePlan.Options.CustomTakeoffX = project.CustomTakeoffX;
        _routePlan.Options.CustomTakeoffY = project.CustomTakeoffY;
        _routePlan!.Options.TakeoffX = project.TakeoffX;
        _routePlan.AccessPlanning = project.AccessPlanning;
        _routePlan.CheckLines = project.CheckLines;
        _routePlan.DesignBasis = project.DesignBasis;
        _routeDesignBasis = project.DesignBasis;
        if (project.DesignBasis is { } basis) _routeWorkspace.SetLineAzimuth(basis.GridAzimuthDegrees);
        _routePlan.Options.TakeoffY = project.TakeoffY;
        if (LaunchSitePlanning.Enabled && project.TakeoffX.HasValue && project.TakeoffY.HasValue)
            _routePlan.RoundTripDistanceM = RouteCoordinateMath.RoundTripDistance(
                project.TakeoffX.Value, project.TakeoffY.Value, _routePlan.Sorties,
                _routePlan.Mode, _routePlan.CentralMeridian);
        _routePlan!.PartitionCode = project.PartitionCode ?? "";
        _routePlan.PartitionManifestPath = project.PartitionManifestPath ?? "";
        _routePlan.ExecutionSegments = project.ExecutionSegments ?? [];

        // Preserve legacy/frozen KMZ metadata from an existing Route Project
        // without feeding it into ordinary route generation.
        _routePlan!.Options.KmzSpeedMps =
            project.KmzSpeedMps;

        _routePlan.Sorties.Clear();
        _routePlan.Sorties.AddRange(project.Sorties);
        RefreshRoutePlanUi();
        SetRouteWorkspaceDirty(false);
        SetStatus(sourceChanged
            ? $"航线工程已载入，但原始端点 CSV 已发生变化，请重新生成并核对：{filePath}"
            : $"航线工程已载入：{filePath}");
        if (!restoringCachedProject) RememberOpenedProject();
    }

    private string GetRouteSourceFingerprint(string source)
    {
        string full = Path.GetFullPath(source);
        long ticks = File.Exists(full) ? File.GetLastWriteTimeUtc(full).Ticks : 0;
        string order = _routeWorkspace.GetHeaderlessOrderText();
        return $"{full}|{ticks}|{order}|{_routeWorkspace.GetCoordModeIndex()}";
    }

    private string RequireRouteOutput()
    {
        string output;
        if (TryGetManagedOutputContext(
                out _,
                out _,
                out _))
        {
            output = ResolveToolOutputDirectory(
                "route_export",
                "航线 KML/CSV 输出");
        }
        else
        {
            output = _routeWorkspace.GetOutputPath();
            if (output.Length == 0)
            {
                output = ResolveToolOutputDirectory(
                    "route_export",
                    "航线 KML/CSV 输出");
            }
        }

        string partitionCode = _routePlan?.PartitionCode ??
            _routeActivePartition?.PartitionCode ?? "";
        string outputLeaf =
            Path.GetFileName(Path.TrimEndingDirectorySeparator(output)) ?? "";
        if (!string.IsNullOrWhiteSpace(partitionCode) &&
            !outputLeaf.Equals(
                partitionCode,
                StringComparison.OrdinalIgnoreCase))
        {
            output = Path.Combine(output, partitionCode);
        }

        Directory.CreateDirectory(output);
        _routeWorkspace.SetOutputPath(output);
        return output;
    }

    private string CurrentRouteWorkareaName()
    {
        return TryGetManagedOutputContext(
            out _,
            out _,
            out string workareaName)
            ? workareaName
            : "临时航线";
    }

    private static double ParseRouteNumber(string text, string label) =>
        DelimitedTextReader.ParseNumber(text.Trim(), label);

    private static int ParseRouteInteger(string text, string label)
    {
        if (!int.TryParse(
                text.Trim(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int value))
        {
            throw new InvalidOperationException($"{label}必须为整数。");
        }
        return value;
    }

    private static double ParseRoutePositive(string text, string label)
    {
        double value = ParseRouteNumber(text, label);
        if (value <= 0) throw new InvalidOperationException($"{label}必须大于 0。");
        return value;
    }

    private static double? ParseRouteOptionalDouble(string text, string label) =>
        string.IsNullOrWhiteSpace(text) ? null : ParseRouteNumber(text, label);

}

internal sealed class RouteSortieViewRow
{
    public string SiteName { get; set; } = "";
    public double? OutboundKm { get; set; }
    public double? ReturnKm { get; set; }
    public double? TotalKm { get; set; }
    public int Index { get; set; }
    public string Name { get; set; } = "";
    public int LineCount { get; set; }
    public int PointCount { get; set; }
    public double LengthKm { get; set; }
    public string Status { get; set; } = "";
    public string EditState { get; set; } = "";
}
