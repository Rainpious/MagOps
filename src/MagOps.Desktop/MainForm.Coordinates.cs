using System.Globalization;
using MagOps.Core.Coordinates;
using MagOps.Core.Domain;
using MagOps.Core.Geomagnetics;
using MagOps.Infrastructure.Services;

namespace MagOps.Desktop;

public sealed partial class MainForm
{
    private IgrfModelSet? _igrfModelSet;
    private string _coordinateInputSnapshot = "";
    private string CoordinateInputSignature() => string.Join("|", _coordinateWorkspace.GetInputText(),
        _coordinateWorkspace.GetModeIndex(), _coordinateWorkspace.GetDmsSeparators(), CoordinateDecimalPlaces(), _coordinateWorkspace.GetIncludeZoneNumber());
    private string? _igrfModelSourcePath;
    private readonly IgrfConfigurationService _igrfConfiguration = new();

    private void BuildCoordinateTab()
    {
        RegisterFeatureControl(
            FeatureIds.Coordinates,
            _coordinateWorkspace,
            activated: QueueActivation,
            contextChanged: _ => QueueActivation());

        void QueueActivation() => QueueFeatureActivation(FeatureIds.Coordinates, () =>
            RunUi("读取项目坐标成果", () => { TryAutoLoadCoordinateProjectResult(force: false); RefreshAccessPlanning(); }));
    }

    private sealed record CoordinateProjectSnapshot(
        IReadOnlyList<(double Longitude, double Latitude)> Points,
        IReadOnlyDictionary<string, string> Parameters);

    private void TryAutoLoadCoordinateProjectResult(bool force)
    {
        if (!TryGetManagedOutputContext(
                out _,
                out string workareaPath,
                out _))
        {
            _coordinateWorkspace.SetProjectSourceSummary("项目成果：请选择已初始化项目和当前工区。");
            return;
        }

        string contextKey = string.Join(
            "|",
            Path.GetFullPath(Root),
            workareaPath.Replace('\\', '/'));
        if (!force &&
            contextKey.Equals(
                _coordAutoLoadedContextKey,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _coordAutoLoadedContextKey = contextKey;

        string csvDirectory = ManagedOutputPreview(
            "coordinate_export",
            workareaPath);
        var area = _workspace.GetWorkareas(Root).First(x => x.WorkareaId == CurrentWorkareaId);
        string? bound = area.Boundary is null ? null : WorkspaceService.ResolveBoundBoundary(Root, area);
        string? csv = bound is null ? FindLatestCoordinateProjectFile(
            csvDirectory,
            "*.csv") : null;
        if (!string.IsNullOrWhiteSpace(csv))
        {
            try
            {
                CoordinateProjectSnapshot snapshot = ReadCoordinateProjectCsv(csv);
                ApplyCoordinateProjectSnapshot(snapshot);
                _coordinateWorkspace.SetProjectSourceSummary(
                    $"项目成果：{Path.GetFileName(csv)}（坐标 CSV 优先；旧文件缺省参数按 CGCS2000 / GK / E,N / 假东500000、假北0恢复，须复核）");
                SetStatus($"已读取当前工区坐标成果：{Path.GetFileName(csv)}");
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or FormatException)
            {
                // Fall through to the governed range KML. A stale or manually
                // edited CSV should not prevent reopening the coordinate workbench.
            }
        }

        string kmlDirectory = ManagedOutputPreview(
            "coordinate_kml_range",
            workareaPath);
        string? kml = bound ?? FindLatestCoordinateProjectFile(kmlDirectory, "*.kml");
        if (!string.IsNullOrWhiteSpace(kml))
        {
            try
            {
                var regions = _kmlSurveySubdivision.ReadRegionPolygons(kml);
                if (bound is not null && (regions.Count != 1 || regions[0].InnerRings.Count > 0))
                {
                    ResetCoordinateWorkbenchForContextChange();
                    _coordAutoLoadedContextKey = contextKey;
                    RefreshGlobalSpatialReference(_workspace.ReadWorkareaSpatialContext(Root, workareaPath));
                    _coordinateWorkspace.SetProjectSourceSummary($"当前工区边界：{area.Boundary!.SourceName}，{regions.Count} 个面、{regions.Sum(r => r.InnerRings.Count)} 个孔洞。完整边界已供航线使用。");
                    return;
                }
                SpatialGeometry polygon = _kmlSurveySubdivision.ReadPrimaryPolygon(kml);
                List<GeoCoordinate> points = RemoveClosingCoordinateDuplicate(
                    polygon.Coordinates);
                if (points.Count < 3)
                    throw new InvalidDataException("项目范围 KML 中的有效边界点不足 3 个。");

                _coordinateWorkspace.ResetForContextChange();
                if (area.Boundary?.Projection is { } projection)
                {
                    _coordinateWorkspace.SetProjection(projection.Kind, projection.UtmZone, projection.SouthernHemisphere);
                    _coordinateWorkspace.SelectDatum(projection.Datum);
                    _coordinateWorkspace.SetAxisIndex(projection.AxisOrder == XyAxisOrder.NorthingEasting ? 1 : 0);
                    _coordinateWorkspace.SetFalseEastingText(F(projection.FalseEasting));
                    _coordinateWorkspace.SetFalseNorthingText(F(projection.FalseNorthing));
                }
                else _coordinateWorkspace.SelectDatum("WGS84");
                _coordinateWorkspace.SetModeIndex(1);
                _coordinateWorkspace.SetAutoMeridian(true);
                _coordinateWorkspace.SetInputText(
                    string.Join(
                        Environment.NewLine,
                        points.Select(point =>
                            point.Longitude.ToString("0.########", CultureInfo.InvariantCulture) + "," +
                            point.Latitude.ToString("0.########", CultureInfo.InvariantCulture))));
                ConvertCoordinateInput(restoring: true);
                _coordinateWorkspace.SetProjectSourceSummary(
                    $"工区边界：{area.Boundary?.SourceName ?? Path.GetFileName(kml)}");
                SetStatus($"已从当前工区范围 KML 恢复 {points.Count} 个边界点：{Path.GetFileName(kml)}");
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or FormatException)
            {
                // Show an empty project state rather than throwing on page activation.
            }
        }

        _coordinateWorkspace.SetProjectSourceSummary("项目成果：当前工区暂无可读取的坐标 CSV / 范围 KML。");
    }

    private static string? FindLatestCoordinateProjectFile(
        string directory,
        string pattern)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return null;

        return Directory
            .EnumerateFiles(directory, pattern, SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(path => path, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private static CoordinateProjectSnapshot ReadCoordinateProjectCsv(string path)
    {
        using var reader = new StreamReader(path, detectEncodingFromByteOrderMarks: true);
        string? headerLine = reader.ReadLine();
        if (string.IsNullOrWhiteSpace(headerLine))
            throw new InvalidDataException("坐标成果 CSV 没有表头。");

        List<string> headers = ParseCoordinateProjectCsvRow(headerLine);
        int lonIndex = headers.FindIndex(value =>
            value.Equals("经度_十进制度", StringComparison.OrdinalIgnoreCase));
        int latIndex = headers.FindIndex(value =>
            value.Equals("纬度_十进制度", StringComparison.OrdinalIgnoreCase));
        int parameterIndex = headers.FindIndex(value =>
            value.Equals("参数", StringComparison.OrdinalIgnoreCase));
        int parameterValueIndex = headers.FindIndex(value =>
            value.Equals("参数值", StringComparison.OrdinalIgnoreCase));
        if (lonIndex < 0 || latIndex < 0)
            throw new InvalidDataException("不是 MagOps 坐标转换成果 CSV：缺少十进制度经纬度列。");

        var points = new List<(double Longitude, double Latitude)>();
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            List<string> cells = ParseCoordinateProjectCsvRow(line);
            if (lonIndex < cells.Count &&
                latIndex < cells.Count &&
                double.TryParse(
                    cells[lonIndex],
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out double longitude) &&
                double.TryParse(
                    cells[latIndex],
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out double latitude) &&
                double.IsFinite(longitude) &&
                double.IsFinite(latitude))
            {
                points.Add((longitude, latitude));
            }

            if (parameterIndex >= 0 &&
                parameterValueIndex >= 0 &&
                parameterIndex < cells.Count &&
                parameterValueIndex < cells.Count &&
                !string.IsNullOrWhiteSpace(cells[parameterIndex]))
            {
                parameters[cells[parameterIndex].Trim()] =
                    cells[parameterValueIndex].Trim();
            }
        }

        if (points.Count == 0)
            throw new InvalidDataException("坐标成果 CSV 中没有有效的十进制度经纬度。 ");

        return new CoordinateProjectSnapshot(points, parameters);
    }

    private static List<string> ParseCoordinateProjectCsvRow(string line)
    {
        var cells = new List<string>();
        var current = new System.Text.StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char value = line[i];
            if (value == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    quoted = !quoted;
                }
                continue;
            }

            if (value == ',' && !quoted)
            {
                cells.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(value);
        }
        cells.Add(current.ToString());
        return cells;
    }

    private void ApplyCoordinateProjectSnapshot(CoordinateProjectSnapshot snapshot)
    {
        if (snapshot.Points.Count == 0)
            throw new InvalidDataException("坐标成果没有有效点。 ");
        CoordinateProjectionKind kind = CoordinateProjectionKind.GaussKruger;
        if (snapshot.Parameters.TryGetValue("投影代码", out var code))
        {
            if (!Enum.TryParse(code, out kind) || !Enum.IsDefined(kind))
                throw new InvalidDataException("坐标CSV投影代码无效，不能按高斯默认值恢复。");
        }
        int zone = snapshot.Parameters.TryGetValue("UTM带号", out var zoneText) && int.TryParse(zoneText, out var savedZone) ? savedZone : 48;
        bool south = snapshot.Parameters.TryGetValue("UTM半球", out var hemisphere) && hemisphere == "S";
        if (kind == CoordinateProjectionKind.Utm && (zoneText is null || !int.TryParse(zoneText, out _) || zone is < 1 or > 60 || hemisphere is not ("N" or "S")))
            throw new InvalidDataException("UTM坐标CSV必须保存有效带号及N/S半球。");
        if (snapshot.Parameters.TryGetValue("坐标基准", out var savedDatum) && savedDatum is not ("CGCS2000" or "WGS84" or "Kertau (RSO)"))
            throw new InvalidDataException("坐标CSV含未知基准，不能静默改用默认基准。");
        foreach (string parameter in new[] { "中央经线", "假东", "假北" })
            if (snapshot.Parameters.TryGetValue(parameter, out var value) &&
                (!TryParseNumberWithUnit(value, out double number) || parameter == "中央经线" && Math.Abs(number) > 180))
                throw new InvalidDataException("坐标CSV参数无效：" + parameter);
        // Missing legacy parameters use documented defaults, never the previous file/workarea.
        _coordinateWorkspace.ResetForContextChange();
        _coordinateWorkspace.SetProjection(kind, zone, south);

        _coordinateWorkspace.SetModeIndex(1);
        _coordinateWorkspace.SetInputText(
            string.Join(
                Environment.NewLine,
                snapshot.Points.Select(point =>
                    point.Longitude.ToString("0.########", CultureInfo.InvariantCulture) + "," +
                    point.Latitude.ToString("0.########", CultureInfo.InvariantCulture))));

        if (snapshot.Parameters.TryGetValue("坐标基准", out string? datum))
        {
            _coordinateWorkspace.SelectDatum(
                datum);
        }

        if (snapshot.Parameters.TryGetValue("坐标轴顺序", out string? axis))
        {
            _coordinateWorkspace.SetAxisIndex(
                axis.Contains(
                    "X=Northing",
                    StringComparison.OrdinalIgnoreCase)
                    ? 1
                    : 0);
        }

        if (snapshot.Parameters.TryGetValue("中央经线", out string? central) &&
            TryParseNumberWithUnit(central, out double centralValue))
        {
            _coordinateWorkspace.SetAutoMeridian(false);
            _coordinateWorkspace.SetCentralMeridianText(
                centralValue.ToString(
                    "0.########",
                    CultureInfo.InvariantCulture));
        }
        else
        {
            _coordinateWorkspace.SetAutoMeridian(true);
        }

        if (snapshot.Parameters.TryGetValue("假东", out string? falseEasting) &&
            TryParseNumberWithUnit(falseEasting, out double falseEastingValue))
        {
            _coordinateWorkspace.SetFalseEastingText(
                falseEastingValue.ToString(
                    "0.###",
                    CultureInfo.InvariantCulture));
        }
        if (snapshot.Parameters.TryGetValue("假北", out string? falseNorthing) &&
            TryParseNumberWithUnit(falseNorthing, out double falseNorthingValue))
        {
            _coordinateWorkspace.SetFalseNorthingText(
                falseNorthingValue.ToString(
                    "0.###",
                    CultureInfo.InvariantCulture));
        }
        if (snapshot.Parameters.TryGetValue("小数坐标保留位数", out string? decimals) &&
            int.TryParse(
                decimals,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int decimalPlaces))
        {
            _coordinateWorkspace.SetDecimalPlaces(
                decimalPlaces);
        }
        if (snapshot.Parameters.TryGetValue("添加带号", out string? includeZone))
        {
            _coordinateWorkspace.SetIncludeZoneNumber(
                includeZone.StartsWith(
                    "是",
                    StringComparison.OrdinalIgnoreCase));
        }

        ConvertCoordinateInput(restoring: true);
        _coordinateWorkspace.SetAutoMeridian(kind == CoordinateProjectionKind.GaussKruger);
    }

    private static bool TryParseNumberWithUnit(string? value, out double result)
    {
        result = 0d;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        string numeric = new string(
            value
                .Trim()
                .TakeWhile(ch =>
                    char.IsDigit(ch) ||
                    ch is '+' or '-' or '.' or 'e' or 'E')
                .ToArray());
        return double.TryParse(
            numeric,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out result) &&
            double.IsFinite(result);
    }

    private static List<GeoCoordinate> RemoveClosingCoordinateDuplicate(
        IReadOnlyList<GeoCoordinate> source)
    {
        var result = source.ToList();
        if (result.Count >= 2)
        {
            GeoCoordinate first = result[0];
            GeoCoordinate last = result[^1];
            if (Math.Abs(first.Longitude - last.Longitude) <= 1e-10 &&
                Math.Abs(first.Latitude - last.Latitude) <= 1e-10)
            {
                result.RemoveAt(result.Count - 1);
            }
        }
        return result;
    }

    private void ResetCoordinateWorkbenchForContextChange()
    {
        _coordAutoLoadedContextKey = "";
        _lastCoordinateBatch = null;
        _lastCoordinateProjection = null;
        _coordinateWorkspace.ResetForContextChange();
    }

    private void RefreshCoordinateRecords()
    {
        if (!Directory.Exists(Root) ||
            !MagOps.Infrastructure.Storage.ProjectPaths.HasProject(Root))
            return;

        List<CoordinateRegistryGridRow> rows = _coordinateRegistry.List(Root)
            .Select(x => new CoordinateRegistryGridRow(
                x.CoordinateId,
                F(x.LongitudeDeg),
                F(x.LatitudeDeg),
                F(x.ZM),
                UiText.HeightType(x.ZType),
                x.Crs,
                x.SourceArtifact))
            .ToList();

        _coordinateWorkspace.ShowRegistryRows(
            rows);
    }

    private static string F(double? value) =>
        value.HasValue ? value.Value.ToString("0.##########", CultureInfo.InvariantCulture) : "";

    private void ConvertCoordinates() => ConvertCoordinateInput(restoring: false);

    private void ConvertCoordinateInput(bool restoring)
    {
        string rawInput = _coordinateWorkspace.GetInputText();
        if (string.IsNullOrWhiteSpace(rawInput))
            throw new InvalidOperationException("请粘贴或输入坐标。");

        CoordinateInputMode requestedMode = _coordinateWorkspace.GetModeIndex() switch
        {
            0 => CoordinateInputMode.Dms,
            1 => CoordinateInputMode.DecimalDegrees,
            _ => CoordinateInputMode.ProjectedXY
        };

        string dmsSeparators = _coordinateWorkspace.GetDmsSeparators();
        CoordinateInputMode mode = CoordinateBatchParser.ResolveInputMode(
            rawInput,
            requestedMode,
            dmsSeparators);
        bool autoDetectedDms =
            requestedMode != CoordinateInputMode.Dms &&
            mode == CoordinateInputMode.Dms;

        if (autoDetectedDms)
            _coordinateWorkspace.SetModeIndex(0);

        double? central = ParseOptionalDouble(_coordinateWorkspace.GetCentralMeridianText(), "中央经线");
        bool gk = _coordinateWorkspace.GetProjectionKind() == CoordinateProjectionKind.GaussKruger;
        WorkspaceRecord? areaReference = gk && IsProjectInitialized() && CurrentWorkareaRelativePath.Length > 0 ? _workspace.ReadWorkareaSpatialContext(Root, CurrentWorkareaRelativePath) : null;
        if (areaReference?.CentralMeridian is { } projectCm)
        {
            if (!restoring && !_coordinateWorkspace.GetAutoMeridian() && central is { } entered && Math.Abs(entered - projectCm) > 1e-9)
                throw new InvalidDataException("当前坐标中央经线与工区值不同；请通过顶部“中央经线”栏统一修改工区空间基准。");
            central = projectCm;
        }
        double falseEasting = ParseRequiredDouble(_coordinateWorkspace.GetFalseEastingText(), "假东");
        double falseNorthing = ParseRequiredDouble(_coordinateWorkspace.GetFalseNorthingText(), "假北");
        var axis = _coordinateWorkspace.GetAxisIndex() == 1 ? XyAxisOrder.NorthingEasting : XyAxisOrder.EastingNorthing;

        var parameters = _coordinateWorkspace.ReadProjectionParameters(central);

        bool auto = areaReference?.CentralMeridian is null && mode != CoordinateInputMode.ProjectedXY && _coordinateWorkspace.GetAutoMeridian();
        int decimalPlaces = CoordinateDecimalPlaces();
        BatchConversionResult converted = CoordinateService.ConvertBatch(
            rawInput,
            mode,
            parameters,
            auto,
            dmsSeparators,
            decimalPlaces);
        string referenceWarning = "";
        if (!restoring && areaReference?.CentralMeridian is { } currentCm && mode != CoordinateInputMode.ProjectedXY)
        {
            // Check the geographic input against the confirmed workarea basis before replacing it.
            var suggestion = CoordinateService.SuggestBatchGk3CentralMeridian(converted.Results);
            if (Math.Abs(suggestion.CentralMeridian - currentCm) > 1e-9)
            {
                double suggestedCm = suggestion.CentralMeridian;
                string zones = suggestion.PointMeridians.Count > 1
                    ? $"\r\n本批坐标跨越多个建议带：{string.Join("、", suggestion.PointMeridians.Select(x => $"{x:g}°"))}，建议值取经度中位数。"
                    : "";
                DialogResult decision = MessageBox.Show(this,
                    $"工区中央经线为 {currentCm:g}°，实际经纬度的三度带建议值为 {suggestedCm:g}°，两者不一致。{zones}\r\n\r\n" +
                    "是：更新工区中央经线并重新计算本批坐标；已有航线和派生成果需重新计算、复核。\r\n" +
                    "否：保留工区自定义基准，按原值计算并显示差异。\r\n取消：停止本次转换。",
                    "坐标与工区中央经线不一致", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
                if (decision == DialogResult.Cancel)
                    return;
                if (decision == DialogResult.Yes)
                {
                    if (!ConfirmContextSwitch("按实际坐标建议更新工区中央经线"))
                        return;
                    // Finish validation and conversion before changing project state.
                    parameters = parameters with { CentralMeridian = suggestedCm };
                    converted = CoordinateService.ConvertBatch(rawInput, mode, parameters,
                        autoCentralMeridian: false, dmsSeparators, decimalPlaces);
                    areaReference = ApplyWorkareaCentralMeridian(suggestedCm,
                        "USER_COORDINATE_REFERENCE_CORRECTED");
                }
                else
                {
                    referenceWarning = $"  工区采用 {currentCm:g}°，实际坐标三度带建议 {suggestedCm:g}°；本次保留工区基准。";
                }
            }
        }
        if (!restoring && areaReference is not null && !areaReference.CentralMeridian.HasValue)
        {
            double initialCm = converted.CentralMeridian;
            string bands = converted.SuggestedMeridians.Count > 1 ? converted.Warning : "";
            if (MessageBox.Show(this, $"{bands}本次坐标确定中央经线为 {initialCm:0.########}°。确认作为工区空间基准？确认后各界面复用；后续可在顶部修改。",
                    "首次确认工区中央经线", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            var confirmedReference = _workspace.ConfirmWorkareaMeridian(Root, CurrentWorkareaRelativePath, initialCm, "USER_COORDINATE_REFERENCE_CONFIRMED");
            RefreshGlobalSpatialReference(confirmedReference);
            RefreshProject();
            RefreshArtifactFreshnessStatus();
        }

        _lastCoordinateBatch = converted;
        _lastCoordinateProjection = parameters with { CentralMeridian = converted.CentralMeridian };

        _coordinateWorkspace.SetCentralMeridianText(
            _lastCoordinateBatch.CentralMeridian.ToString(
                "0.########",
                CultureInfo.InvariantCulture));
        _coordinateInputSnapshot = CoordinateInputSignature();
        double measuredAreaM2 = CoordinateGeometryMath.PolygonAreaSquareMeters(
            _lastCoordinateBatch.Results);
        string measuredAreaText = _lastCoordinateBatch.Results.Count >= 3
            ? FormatCoordinateArea(measuredAreaM2)
            : "";
        _coordinateWorkspace.SetWarning(
            $"已转换 {_lastCoordinateBatch.Results.Count} 个坐标点。" +
            (autoDetectedDms
                ? "  已检测到 DMS 符号，输入格式已自动切换为“度分秒”。"
                : "") +
            (measuredAreaText.Length > 0
                ? $"  测算面积：{measuredAreaText}。"
                : "") +
            (string.IsNullOrWhiteSpace(_lastCoordinateBatch.Warning)
                ? ""
                : $"  {_lastCoordinateBatch.Warning}") + referenceWarning);

        _coordinateWorkspace.SetParameterSummary(
            $"投影：{CoordinateProjectionService.Label(parameters)} ｜ 基准：{parameters.Datum} ｜ " +
            $"中央子午线：{_lastCoordinateBatch.CentralMeridian:0.###}° ｜ " +
            $"小数位：{decimalPlaces} ｜ " +
            $"带号：{(_coordinateWorkspace.GetIncludeZoneNumber() ? "添加到 Easting" : "不添加")} ｜ " +
            $"假东：{falseEasting:0.###} m ｜ 假北：{falseNorthing:0.###} m ｜ " +
            $"轴序：{_coordinateWorkspace.GetAxisText()}");

        XyAxisOrder outputAxis = CoordinateAxisOrder();
        bool includeZoneNumber = _coordinateWorkspace.GetIncludeZoneNumber();
        List<CoordinateConversionGridRow> viewRows = _lastCoordinateBatch.Results
            .Select((x, i) =>
            {
                (double outputX, double outputY) =
                    CoordinateRegistryService.CoordinateOutputValues(
                        x,
                        outputAxis,
                        _lastCoordinateBatch.CentralMeridian,
                        includeZoneNumber);
                return new CoordinateConversionGridRow(
                    i + 1,
                    x.PointId ?? "",
                    x.LongitudeDecimal,
                    x.LatitudeDecimal,
                    x.LongitudeDms,
                    x.LatitudeDms,
                    outputX.ToString("0.###", CultureInfo.InvariantCulture),
                    outputY.ToString("0.###", CultureInfo.InvariantCulture));
            })
            .ToList();

        _coordinateWorkspace.ShowConversionResults(
            viewRows,
            _lastCoordinateBatch.Results);

        var measurementRows = CoordinateRegistryService.MeasurementParameters(_lastCoordinateBatch, _lastCoordinateProjection);
        string M(string key) => measurementRows.Single(p => p.Name == key).Value;
        string compact = $"测算面积：{M("测算面积_m2")} m² / {M("测算面积_km2")} km²" + Environment.NewLine +
            $"重心 E/N：{M("工区重心_E_m")}, {M("工区重心_N_m")} m" + Environment.NewLine +
            $"重心经纬度：{M("工区重心经度_deg")}, {M("工区重心纬度_deg")}° ({M("重心经纬度基准")})" + Environment.NewLine + M("工区重心类型");
        _coordinateWorkspace.SetParameterSummary(
            $"投影：{CoordinateProjectionService.Label(_lastCoordinateProjection)} ｜ 基准：{parameters.Datum} ｜ " +
            $"中央子午线：{_lastCoordinateBatch.CentralMeridian:0.########}°" + Environment.NewLine +
            $"轴序：{_coordinateWorkspace.GetAxisText()}；假东/北：{parameters.FalseEasting:g} / {parameters.FalseNorthing:g} m；小数位：{decimalPlaces}" + Environment.NewLine +
            (parameters.Kind == CoordinateProjectionKind.KertauRsoMalaya ? CoordinateProjectionService.RsoWarning + Environment.NewLine : "") +
            string.Join(Environment.NewLine, CoordinateProjectionService.DefinitionParameters(parameters).Select(p => p.Name + "：" + p.Value)) + Environment.NewLine +
            string.Join(Environment.NewLine, measurementRows.Select(p => p.Name + "：" + p.Value)) +
            Environment.NewLine + "按输入点序计算，未验证完整工区边界；凹面或多区域的重心可能在边界外。", compact);

        SetStatus(
            $"坐标转换完成：{_lastCoordinateBatch.Results.Count} 点，中央经线 {_lastCoordinateBatch.CentralMeridian:g}°" +
            (measuredAreaText.Length > 0 ? $"，测算面积 {measuredAreaText}" : ""));
    }

    private static string FormatCoordinateArea(double areaM2)
    {
        if (areaM2 >= 1_000_000d)
            return $"{areaM2 / 1_000_000d:0.###} km²（{areaM2 / 10_000d:0.##} ha）";
        if (areaM2 >= 10_000d)
            return $"{areaM2 / 10_000d:0.##} ha（{areaM2:0} m²）";
        return $"{areaM2:0.##} m²";
    }

    private ProjectionParameters RequireCurrentCoordinateProjection()
    {
        var saved = _lastCoordinateProjection ?? throw new InvalidOperationException("请先完成坐标转换。");
        var current = _coordinateWorkspace.ReadProjectionParameters(
            ParseOptionalDouble(_coordinateWorkspace.GetCentralMeridianText(), "中央经线"));
        if (current != saved || _coordinateInputSnapshot != CoordinateInputSignature()) throw new InvalidOperationException("输入、投影、带号、半球或输出设置已改变，请重新转换后再导出。");
        return saved;
    }

    private void ExportCoordinates()
    {
        if (_lastCoordinateBatch is null ||
            _lastCoordinateBatch.Results.Count == 0)
        {
            throw new InvalidOperationException("请先完成坐标转换。");
        }

        CoordinateInputMode mode = _coordinateWorkspace.GetModeIndex() switch
        {
            0 => CoordinateInputMode.Dms,
            1 => CoordinateInputMode.DecimalDegrees,
            _ => CoordinateInputMode.ProjectedXY
        };

        double central = _lastCoordinateBatch.CentralMeridian;
        var parameters = RequireCurrentCoordinateProjection();

        bool managed = TryGetManagedOutputContext(
            out string projectName,
            out _,
            out string workareaName);

        string defaultName = managed
            ? $"{projectName}_{workareaName}_坐标_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
            : $"坐标_{DateTime.Now:yyyyMMdd_HHmmss}.csv";

        var nameValues = PromptDialog.Show(
            this,
            "导出坐标结果",
            [
                new PromptField(
                    "name",
                    "文件名",
                    defaultName,
                    Required: true)
            ],
            note: managed
                ? "当前已选择项目和工区；CSV 将归档到 03_坐标与空间资料/01_坐标转换CSV。"
                : "当前未同时选择项目和工区；本次按临时工具使用，确认文件名后手动选择输出位置。");

        if (nameValues is null)
            return;

        string fileName = Path.GetFileName(nameValues["name"].Trim());
        if (!fileName.EndsWith(
                ".csv",
                StringComparison.OrdinalIgnoreCase))
        {
            fileName += ".csv";
        }

        string outputPath = ResolveToolOutputFilePath(
            "coordinate_export",
            "导出坐标结果",
            fileName,
            "CSV 文件 (*.csv)|*.csv|所有文件 (*.*)|*.*",
            ".csv");

        _coordinateRegistry.ExportConversionCsv(
            outputPath,
            _lastCoordinateBatch,
            mode,
            parameters,
            CoordinateDecimalPlaces(),
            _coordinateWorkspace.GetIncludeZoneNumber());

        RegisterGeneratedBusinessFile(
            outputPath,
            "coordinate_export");

        SetStatus($"坐标已导出：{outputPath}");
        TransientNotice.Show(
            this,
            $"坐标 CSV 已生成：{Path.GetFileName(outputPath)}",
            "文件写入成功");
    }

    private void ExportOasisPly()
    {
        RequireCurrentCoordinateProjection();
        if (_lastCoordinateBatch is null ||
            _lastCoordinateBatch.Results.Count == 0)
        {
            throw new InvalidOperationException("请先完成坐标转换。");
        }

        bool managed = TryGetManagedOutputContext(
            out string projectName,
            out _,
            out string workareaName);

        string defaultName = managed
            ? $"{projectName}_{workareaName}_Oasis航线XY_{DateTime.Now:yyyyMMdd_HHmmss}.ply"
            : $"Oasis航线XY_{DateTime.Now:yyyyMMdd_HHmmss}.ply";

        var values = PromptDialog.Show(
            this,
            "导出 Oasis PLY",
            [
                new PromptField(
                    "name",
                    "文件名",
                    defaultName,
                    Required: true)
            ],
            width: 660,
            note:
                "输出为无表头纯 XY 两列，每行 X<TAB>Y；不写点号、经纬度、参数说明或其它字段。" +
                "为保证绘制航线的平面几何和后续坐标换算稳定，PLY 始终使用无带号 XY。" +
                (managed
                    ? "当前已选择项目和工区；导出成功后只登记 PLY 成果。本次坐标中央子午线只属于当前坐标批次/文件，绝不会写入或覆盖工区空间基准。"
                    : "当前未同时选择项目和工区；本次按临时工具使用，手动选择输出位置，不写入项目登记或工区空间基准。"));

        if (values is null)
            return;

        string fileName = Path.GetFileName(values["name"].Trim());
        if (!fileName.EndsWith(
                ".ply",
                StringComparison.OrdinalIgnoreCase))
        {
            fileName += ".ply";
        }

        string outputPath = ResolveToolOutputFilePath(
            "coordinate_oasis_ply",
            "导出 Oasis 航线设计 PLY",
            fileName,
            "Oasis PLY (*.ply)|*.ply|所有文件 (*.*)|*.*",
            ".ply");

        _coordinateRegistry.ExportCleanXyPly(
            outputPath,
            _lastCoordinateBatch);

        if (managed && IsProjectInitialized())
        {
            RegisterGeneratedBusinessFile(
                outputPath,
                "coordinate_oasis_ply");

            RefreshProject();
            SetStatus(
                $"Oasis PLY 已导出并登记；本次坐标中央子午线 " +
                $"{_lastCoordinateBatch.CentralMeridian:0.########}°E 仅写入成果语义，" +
                "未修改工区空间基准。");
        }
        else
        {
            SetStatus(
                $"Oasis PLY 已临时导出：{outputPath}（未写入项目登记或工区空间基准）");
        }

        TransientNotice.Show(
            this,
            $"Oasis PLY 已生成：{Path.GetFileName(outputPath)}",
            "文件写入成功");

        // Remember the governed PLY as the preferred route input, but do not
        // mutate the route page while the operator is still working here.
        // “绘制航线” resolves project defaults when that page is activated.
        _lastGeneratedRoutePlyPath = Path.GetFullPath(outputPath);
    }

    private void SelectCoordinateKmlTemplate(string? selectedPath = null)
    {
        var fileHistory = FileInteractions.For(this, "coordinate-template", projectLevel: false, global: false);
        string filePath = selectedPath?.Trim() ?? "";
        if (filePath.Length == 0)
        {
            using var dialog = new OpenFileDialog
            {
                Filter = "KML 模板 (*.kml)|*.kml|所有文件 (*.*)|*.*",
                Title = "选择 KML 范围模板"
            };

            fileHistory.Prepare(dialog, current: _coordUploadedKmlTemplatePath);

            if (dialog.ShowDialog(this) != DialogResult.OK)
                throw new OperationCanceledException();
            filePath = dialog.FileName;
        }

        if (!File.Exists(filePath) || !FileDropSupport.HasExtension(filePath, ".kml"))
            throw new InvalidDataException("请选择有效的 KML 模板文件。");

        _ = _kmlRangeExporter.LoadFormatFromFile(filePath);

        _coordUploadedKmlTemplatePath = Path.GetFullPath(filePath);
        _coordinateWorkspace.SetKmlChoice(CoordinateKmlTemplateChoice.Uploaded);
        UpdateCoordinateKmlTemplateHint();
        SetStatus(
            $"已选择 KML 模板：{Path.GetFileName(filePath)}");
        fileHistory.InputSucceeded(filePath);
    }

    private void OnCoordinateKmlChoiceChanged(
        CoordinateKmlTemplateChoice choice)
    {
        UpdateCoordinateKmlTemplateHint();

        if (choice ==
                CoordinateKmlTemplateChoice.Uploaded &&
            string.IsNullOrWhiteSpace(
                _coordUploadedKmlTemplatePath))
        {
            RunUi(
                "选择 KML 模板",
                () => SelectCoordinateKmlTemplate());
            return;
        }

        if (choice ==
            CoordinateKmlTemplateChoice.Custom)
        {
            RunUi(
                "编辑 KML 格式",
                EditCoordinateKmlFormat);
        }
    }

    private void UpdateCoordinateKmlTemplateHint()
    {
        string text =
            _coordinateWorkspace.GetKmlChoice() switch
            {
                CoordinateKmlTemplateChoice.BuiltInSky =>
                    "内置模板：天青边 / 天青填充（DJI 极简兼容；线宽 3，填充约 40%）",
                CoordinateKmlTemplateChoice.BuiltInRedYellow =>
                    "内置模板：红色边 / 黄色填充（DJI 极简兼容；线宽 3，填充约 40%）",
                CoordinateKmlTemplateChoice.Uploaded =>
                    string.IsNullOrWhiteSpace(
                        _coordUploadedKmlTemplatePath)
                        ? "上传模板：尚未选择文件"
                        : $"上传模板：{_coordUploadedKmlTemplatePath}",
                _ =>
                    $"自定义：边缘 {_coordCustomKmlFormat.LineColorHex} / " +
                    $"{_coordCustomKmlFormat.LineWidth:0.##}；填充 " +
                    $"{_coordCustomKmlFormat.FillColorHex} / " +
                    $"{_coordCustomKmlFormat.FillOpacityPercent}%"
            };

        _coordinateWorkspace.SetKmlTemplateHint(
            text);
    }

    private void EditCoordinateKmlFormat()
    {
        if (_coordKmlCustomDialogActive)
            return;

        _coordKmlCustomDialogActive = true;
        try
        {
            KmlRangeFormat current = _coordCustomKmlFormat;

            var values = PromptDialog.Show(
                this,
                "自定义 KML 范围格式",
                [
                    new PromptField(
                        "line_color",
                        "边缘颜色 #RRGGBB",
                        current.LineColorHex,
                        Required: true),
                    new PromptField(
                        "line_width",
                        "线宽",
                        current.LineWidth.ToString(
                            "0.###",
                            CultureInfo.InvariantCulture),
                        Required: true),
                    new PromptField(
                        "fill_color",
                        "填充颜色 #RRGGBB",
                        current.FillColorHex,
                        Required: true),
                    new PromptField(
                        "opacity",
                        "填充透明度 0–100%",
                        current.FillOpacityPercent.ToString(
                            CultureInfo.InvariantCulture),
                        Required: true),
                    new PromptField(
                        "fill",
                        "启用填充",
                        current.Fill ? "是" : "否",
                        ["是", "否"],
                        Required: true),
                    new PromptField(
                        "outline",
                        "启用边缘",
                        current.Outline ? "是" : "否",
                        ["是", "否"],
                        Required: true),
                    new PromptField(
                        "tessellate",
                        "tessellate",
                        current.Tessellate ? "是" : "否",
                        ["是", "否"],
                        Required: true),
                    new PromptField(
                        "altitude",
                        "altitudeMode",
                        current.AltitudeMode,
                        [
                            "clampToGround",
                            "relativeToGround",
                            "absolute"
                        ],
                        Required: true),
                    new PromptField(
                        "decimals",
                        "坐标小数位",
                        current.CoordinateDecimals.ToString(
                            CultureInfo.InvariantCulture),
                        Required: true),
                    new PromptField(
                        "name",
                        "Placemark 名称",
                        current.PlacemarkNamePattern,
                        Required: true)
                ],
                width: 760,
                height: 520,
                note:
                    "DJI 极简兼容模式固定不写 xmlns、Folder、description、ExtendedData 或 styleUrl。" +
                    "Placemark 名称仍可使用 {project}、{workarea}、{source}、{status} 占位符。");

            if (values is null)
                return;

            var updated = new KmlRangeFormat
            {
                LineColorHex =
                    KmlRangeExportService.NormalizeHexColor(
                        values["line_color"]),
                LineWidth = double.Parse(
                    values["line_width"],
                    CultureInfo.InvariantCulture),
                FillColorHex =
                    KmlRangeExportService.NormalizeHexColor(
                        values["fill_color"]),
                FillOpacityPercent = int.Parse(
                    values["opacity"],
                    CultureInfo.InvariantCulture),
                Fill = values["fill"].Equals(
                    "是",
                    StringComparison.OrdinalIgnoreCase),
                Outline = values["outline"].Equals(
                    "是",
                    StringComparison.OrdinalIgnoreCase),
                Tessellate = values["tessellate"].Equals(
                    "是",
                    StringComparison.OrdinalIgnoreCase),
                AltitudeMode = values["altitude"],
                CoordinateDecimals = int.Parse(
                    values["decimals"],
                    CultureInfo.InvariantCulture),
                PlacemarkNamePattern = values["name"].Trim()
            };

            KmlRangeExportService.ValidateFormat(updated);
            _coordCustomKmlFormat = updated;
            _coordinateWorkspace.SetKmlChoice(CoordinateKmlTemplateChoice.Custom);
            UpdateCoordinateKmlTemplateHint();
            SetStatus("自定义 KML 范围格式已更新。");
        }
        finally
        {
            _coordKmlCustomDialogActive = false;
        }
    }

    private KmlRangeFormat ResolveCoordinateKmlFormat()
    {
        CoordinateKmlTemplateChoice choice =
            _coordinateWorkspace.GetKmlChoice();

        if (choice ==
            CoordinateKmlTemplateChoice.BuiltInSky)
        {
            return _kmlRangeExporter.LoadBuiltInFormat(
                KmlRangeExportService.SkyBlueTemplateKey);
        }

        if (choice ==
            CoordinateKmlTemplateChoice.BuiltInRedYellow)
        {
            return _kmlRangeExporter.LoadBuiltInFormat(
                KmlRangeExportService.RedYellowTemplateKey);
        }

        if (choice ==
            CoordinateKmlTemplateChoice.Custom)
        {
            return _coordCustomKmlFormat;
        }

        if (string.IsNullOrWhiteSpace(
                _coordUploadedKmlTemplatePath) ||
            !File.Exists(
                _coordUploadedKmlTemplatePath))
        {
            SelectCoordinateKmlTemplate();
        }

        return _kmlRangeExporter.LoadFormatFromFile(
            _coordUploadedKmlTemplatePath);
    }

    private void ExportCoordinateKmlRange()
    {
        var projection = RequireCurrentCoordinateProjection();
        if (_lastCoordinateBatch is null ||
            _lastCoordinateBatch.Results.Count < 3)
        {
            throw new InvalidOperationException(
                "请先完成至少 3 个坐标点的转换，再导出 KML 范围。");
        }

        KmlRangeFormat format = ResolveCoordinateKmlFormat();
        KmlRangeExportService.ValidateFormat(format);
        string styleSuffix =
            KmlRangeExportService.FormatStyleSuffix(format);

        bool managed = TryGetManagedOutputContext(
            out string projectName,
            out _,
            out string workareaName);

        string fileName = managed
            ? $"{projectName}_{workareaName}_坐标_{styleSuffix}.kml"
            : $"坐标_{styleSuffix}_{DateTime.Now:yyyyMMdd_HHmmss}.kml";

        string outputPath = ResolveToolOutputFilePath(
            "coordinate_kml_range",
            "导出 KML 范围",
            fileName,
            "KML 文件 (*.kml)|*.kml|所有文件 (*.*)|*.*",
            ".kml");

        string metadataProject = managed
            ? projectName
            : "临时";
        string metadataWorkarea = managed
            ? workareaName
            : "未指定工区";

        string source =
            $"MagOps 坐标转换；" +
            $"{CoordinateProjectionService.Label(projection)}；" +
            $"中央子午线 " +
            $"{_lastCoordinateBatch.CentralMeridian:0.########}°";

        List<GeoCoordinate> points =
            _lastCoordinateBatch.Results
                .Select(x => new GeoCoordinate(
                    x.Longitude,
                    x.Latitude,
                    0d))
                .ToList();

        _kmlRangeExporter.WriteRange(
            outputPath,
            points,
            format,
            new KmlRangeExportMetadata(
                metadataProject,
                metadataWorkarea,
                source,
                "待核查",
                _lastCoordinateBatch.CentralMeridian));

        RegisterGeneratedBusinessFile(
            outputPath,
            "coordinate_kml_range");
        if (managed)
            _workspace.BindWorkareaBoundary(Root, CurrentWorkareaRelativePath, outputPath, projection);

        SetStatus(
            managed
                ? $"KML 范围已归档：{outputPath}"
                : $"KML 范围已临时导出：{outputPath}（未写入项目登记）");

        TransientNotice.Show(
            this,
            $"KML 范围已生成：{Path.GetFileName(outputPath)}",
            "文件写入成功");
    }

    private void RegisterCoordinates()
    {
        EnsureInitialized();
        if (_lastCoordinateBatch is null || _lastCoordinateBatch.Results.Count == 0)
            throw new InvalidOperationException("请先完成坐标转换。");

        string sourceCrs =
            CoordinateProjectionService.Label(RequireCurrentCoordinateProjection()) + $" / CM {_lastCoordinateBatch.CentralMeridian:g}°";
        var records = _lastCoordinateBatch.Results.Select((r, i) => new CoordinateRecord
        {
            CoordinateId = $"COORD-{DateTime.Now:yyyyMMddHHmmssfff}-{i + 1:000}",
            LongitudeDeg = r.Longitude,
            LatitudeDeg = r.Latitude,
            SourceValue =
                $"{FormatCoordinateDecimal(r.Longitude)},{FormatCoordinateDecimal(r.Latitude)}",
            XM = r.X,
            YM = r.Y,
            Crs = sourceCrs,
            AxisOrder = _coordinateWorkspace.GetAxisIndex() == 1 ? "y,x" : "x,y",
            Notes = string.IsNullOrWhiteSpace(r.PointId)
                ? r.AxisOrderLabel
                : $"{r.AxisOrderLabel}; InputPointId={r.PointId}"
        }).ToList();

        _coordinateRegistry.Register(Root, records);
        RefreshCoordinateRecords();
        MessageBox.Show(this, $"已登记 {records.Count} 条坐标到 coordinates.json。", "坐标登记",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void ImportCoordinateCsv(string? selectedPath = null)
    {
        var fileHistory = FileInteractions.For(this, "coordinate-registry", projectLevel: true, global: false);
        EnsureInitialized();
        string filePath = selectedPath?.Trim() ?? "";
        if (filePath.Length == 0)
        {
            using var dialog = new OpenFileDialog
            {
                Filter = "CSV 文件 (*.csv)|*.csv|所有文件 (*.*)|*.*",
                Title = "导入坐标 CSV"
            };
            fileHistory.Prepare(dialog);
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            filePath = dialog.FileName;
        }

        if (!File.Exists(filePath) || !FileDropSupport.HasExtension(filePath, ".csv"))
            throw new InvalidDataException("请选择有效的坐标 CSV 文件。");

        var values = PromptDialog.Show(this, "导入坐标 CSV",
        [
            new("crs", "源 CRS", "CGCS2000"),
            new("z_type", "高度类型", "", new[] { "" }.Concat(CoordinateRegistryService.HeightTypes).ToArray()),
            new("source_artifact", "资料引用", Path.GetFileName(filePath))
        ]);
        if (values is null) return;

        var rows = _coordinateRegistry.ReadCoordinateCsv(
            filePath,
            values["crs"],
            values["z_type"],
            values["source_artifact"]);

        if (rows.Count == 0)
            throw new InvalidDataException("CSV 中未识别到可登记坐标。");

        if (MessageBox.Show(this,
            $"识别到 {rows.Count} 条坐标，是否登记到当前项目？",
            "坐标导入",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        _coordinateRegistry.Register(Root, rows, values["source_artifact"]);
        RefreshCoordinateRecords();
        MessageBox.Show(this, $"已登记 {rows.Count} 条坐标。", "坐标导入",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
        fileHistory.InputSucceeded(filePath);
    }

    private void RegisterCrsDefinition()
    {
        EnsureInitialized();
        var values = PromptDialog.Show(this, "登记 CRS",
        [
            new("name", "名称", Required: true),
            new("kind", "类型", "geographic", ["geographic", "projected"]),
            new("epsg", "EPSG / 标识"),
            new("datum", "基准", "CGCS2000"),
            new("central", "中央经线"),
            new("false_easting", "假东(m)", "0"),
            new("false_northing", "假北(m)", "0"),
            new("unit", "单位", "degree", ["degree", "metre"]),
            new("axis_order", "轴序", "lon,lat", ["lon,lat", "lat,lon", "x,y", "y,x"]),
            new("notes", "备注", "", Multiline: true)
        ], height: 650);
        if (values is null) return;

        var definition = new CrsDefinitionRecord
        {
            Name = values["name"],
            Kind = values["kind"],
            Epsg = values["epsg"],
            Datum = values["datum"],
            CentralMeridian = ParseOptionalDouble(values["central"], "中央经线"),
            FalseEastingM = ParseRequiredDouble(values["false_easting"], "假东"),
            FalseNorthingM = ParseRequiredDouble(values["false_northing"], "假北"),
            Unit = values["unit"],
            AxisOrder = values["axis_order"],
            Notes = values["notes"]
        };
        _coordinateRegistry.RegisterCrs(Root, definition);
        MessageBox.Show(this, $"CRS 已登记：{definition.Name}", "CRS",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void ConvertSpatialFile(string? selectedPath = null)
    {
        var fileHistory = FileInteractions.For(this, "spatial-convert", projectLevel: false, global: false);
        string filePath = selectedPath?.Trim() ?? "";
        if (filePath.Length == 0)
        {
            using var open = new OpenFileDialog
            {
                Filter = "空间文件 (*.kml;*.gpx)|*.kml;*.gpx|KML (*.kml)|*.kml|GPX (*.gpx)|*.gpx",
                Title = "选择 KML / GPX"
            };
            fileHistory.Prepare(open);
            if (open.ShowDialog(this) != DialogResult.OK)
                return;
            filePath = open.FileName;
        }

        if (!File.Exists(filePath) || !FileDropSupport.HasExtension(filePath, ".kml", ".gpx"))
            throw new InvalidDataException("请选择有效的 KML 或 GPX 文件。");

        string sourceExt = Path.GetExtension(filePath).ToLowerInvariant();

        bool toGpx = sourceExt == ".kml";
        string outputName =
            Path.GetFileNameWithoutExtension(filePath) +
            (toGpx ? "_converted.gpx" : "_converted.kml");

        string outputPath = ResolveToolOutputFilePath(
            "coordinate_spatial_export",
            "KML/GPX 互转输出",
            outputName,
            toGpx
                ? "GPX 文件 (*.gpx)|*.gpx|所有文件 (*.*)|*.*"
                : "KML 文件 (*.kml)|*.kml|所有文件 (*.*)|*.*",
            toGpx ? ".gpx" : ".kml");

        var audit = _coordinateRegistry.ConvertSpatialFile(
            filePath,
            outputPath);

        string differences = audit.DifferenceCount == 0
            ? "经纬度点序往返检查一致。"
            : string.Join(
                "\r\n",
                audit.Differences.Take(20));

        if (audit.Status == "PASSED")
        {
            RegisterGeneratedBusinessFile(
                outputPath,
                "coordinate_spatial_export");
            TransientNotice.Show(
                this,
                $"空间文件已生成：{Path.GetFileName(outputPath)}",
                "文件写入成功");
        }
        else
        {
            MessageBox.Show(
                this,
                $"转换完成：{UiText.Display(audit.Status)}\r\n" +
                $"差异数：{audit.DifferenceCount}\r\n\r\n" +
                $"{differences}\r\n\r\n{outputPath}",
                "KML/GPX 互转",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
        if (audit.Status == "PASSED") fileHistory.InputSucceeded(filePath);
    }

    private void AuditSpatialFile(string? selectedPath = null)
    {
        var fileHistory = FileInteractions.For(this, "spatial-audit", projectLevel: false, global: false);
        string filePath = selectedPath?.Trim() ?? "";
        if (filePath.Length == 0)
        {
            using var dialog = new OpenFileDialog
            {
                Filter = "空间文件 (*.kml;*.gpx)|*.kml;*.gpx|KML (*.kml)|*.kml|GPX (*.gpx)|*.gpx|所有文件 (*.*)|*.*",
                Title = "选择 KML / GPX 进行快速结构审计"
            };
            fileHistory.Prepare(dialog);
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            filePath = dialog.FileName;
        }

        if (!File.Exists(filePath) || !FileDropSupport.HasExtension(filePath, ".kml", ".gpx"))
            throw new InvalidDataException("请选择有效的 KML 或 GPX 文件。");

        var result = _coordinateRegistry.AuditSpatialFile(filePath);
        MessageBox.Show(this,
            $"状态：{UiText.Display(result.Status)}\r\n几何/点数量：{result.GeometryCount}\r\n\r\n错误：\r\n{string.Join("\r\n", result.Errors)}\r\n\r\n提示：\r\n{string.Join("\r\n", result.Warnings)}",
            "空间文件审计", MessageBoxButtons.OK,
            result.Status == "PASSED" ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        if (result.Status == "PASSED") fileHistory.InputSucceeded(filePath);
    }

    private void CalculateIgrfMainField()
    {
        string longitudeDefault = "";
        string latitudeDefault = "";
        if (_lastCoordinateBatch is { Results.Count: > 0 })
        {
            double minLongitude = _lastCoordinateBatch.Results.Min(x => x.Longitude);
            double maxLongitude = _lastCoordinateBatch.Results.Max(x => x.Longitude);
            double minLatitude = _lastCoordinateBatch.Results.Min(x => x.Latitude);
            double maxLatitude = _lastCoordinateBatch.Results.Max(x => x.Latitude);
            longitudeDefault = ((minLongitude + maxLongitude) / 2d)
                .ToString("0.########", CultureInfo.InvariantCulture);
            latitudeDefault = ((minLatitude + maxLatitude) / 2d)
                .ToString("0.########", CultureInfo.InvariantCulture);
        }

        Dictionary<string, string>? values = PromptDialog.Show(
            this,
            "IGRF 主场计算",
            [
                new PromptField(
                    "longitude",
                    "经度 °E",
                    longitudeDefault,
                    Required: true),
                new PromptField(
                    "latitude",
                    "纬度 °N",
                    latitudeDefault,
                    Required: true),
                new PromptField(
                    "height",
                    "WGS84 椭球高 m",
                    "",
                    Required: true),
                new PromptField(
                    "date",
                    "UTC 日期 yyyy-MM-dd",
                    DateTimeOffset.UtcNow.ToString(
                        "yyyy-MM-dd",
                        CultureInfo.InvariantCulture),
                    Required: true)
            ],
            width: 680,
            height: 360,
            note:
                "IGRF 只描述主磁场与长期变化，不替代 GSM-19T / MagOps 日变改正。" +
                "高度必须是 WGS84 椭球高；不要把 LaserAltitudeM 或相对起飞高度直接填入。" +
                "若当前已有坐标转换结果，经纬度默认取其包络中心。 ");

        if (values is null)
            return;

        if (!double.TryParse(
                values["longitude"],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double longitude) ||
            !double.IsFinite(longitude))
        {
            throw new FormatException("经度必须是有效数字。 ");
        }

        if (!double.TryParse(
                values["latitude"],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double latitude) ||
            !double.IsFinite(latitude) ||
            latitude < -90d ||
            latitude > 90d)
        {
            throw new FormatException("纬度必须是 -90 到 90 之间的有效数字。 ");
        }

        if (!double.TryParse(
                values["height"],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double heightMeters) ||
            !double.IsFinite(heightMeters))
        {
            throw new FormatException("WGS84 椭球高必须是有效数字（米）。 ");
        }

        if (!DateOnly.TryParseExact(
                values["date"],
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateOnly dateUtc))
        {
            throw new FormatException("UTC 日期必须使用 yyyy-MM-dd，例如 2026-09-13。 ");
        }

        IgrfModelSet models = GetIgrfModelSet();
        GeomagneticFieldResult field =
            IgrfSphericalHarmonicEngine.Evaluate(
                models,
                latitude,
                longitude,
                heightMeters,
                dateUtc);

        string sha = string.IsNullOrWhiteSpace(field.CoefficientSourceSha256)
            ? "未知"
            : field.CoefficientSourceSha256;

        MessageBox.Show(
            this,
            string.Join(
                Environment.NewLine,
                $"输入：{longitude:0.########}°E, {latitude:0.########}°N, {heightMeters:0.###} m",
                $"UTC 日期：{dateUtc:yyyy-MM-dd}    Decimal year：{field.DecimalYear:0.########}",
                $"模型历元：{field.ModelEpochYear}    最大阶数：{field.MaxDegree}",
                "",
                $"磁偏角 D：{field.DeclinationDeg:0.######}°",
                $"磁倾角 I：{field.InclinationDeg:0.######}°",
                $"总场 F：{field.TotalNt:0.###} nT",
                $"水平场 H：{field.HorizontalNt:0.###} nT",
                $"X / North：{field.NorthNt:0.###} nT",
                $"Y / East：{field.EastNt:0.###} nT",
                $"Z / Down：{field.DownNt:0.###} nT",
                "",
                $"系数：igrf.dat / SHA-256 {sha}",
                "说明：IGRF 主场与 GSM-19T 日变改正是两套独立计算。 "),
            "IGRF 主场计算结果",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);

        SetStatus(
            $"IGRF {field.ModelEpochYear}：D {field.DeclinationDeg:0.###}°，" +
            $"I {field.InclinationDeg:0.###}°，F {field.TotalNt:0.0} nT。 ");
    }

    private IgrfModelSet GetIgrfModelSet()
    {
        string modelPath = File.Exists(_igrfConfiguration.UserOverridePath)
            ? Path.GetFullPath(_igrfConfiguration.UserOverridePath)
            : "embedded://MagOps.Infrastructure/igrf.dat";
        if (_igrfModelSet is null ||
            !string.Equals(
                _igrfModelSourcePath,
                modelPath,
                StringComparison.OrdinalIgnoreCase))
        {
            _igrfModelSet = _igrfConfiguration.LoadCurrent();
            _igrfModelSourcePath = modelPath;
        }

        return _igrfModelSet;
    }

    private void UpdateIgrfConfiguration()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "选择新的 IGRF 系数文件",
            Filter = "IGRF 系数文件 (igrf.dat)|igrf.dat|数据文件 (*.dat;*.txt)|*.dat;*.txt|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        var fileHistory = FileInteractions.For(this, "igrf-configuration", global: true);
        fileHistory.Prepare(dialog);
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        IgrfConfigurationState state = _igrfConfiguration.UpdateFromFile(dialog.FileName);
        fileHistory.InputSucceeded(dialog.FileName);
        _igrfModelSet = null;
        _igrfModelSourcePath = null;
        MessageBox.Show(
            this,
            $"IGRF 配置已更新并立即生效。\r\n\r\n历元：{state.EarliestEpochYear}–{state.LatestEpochYear}\r\nSHA-256：{state.Sha256}\r\n保存位置：{state.SourcePath}\r\n\r\n更新前的用户配置已保留为同目录 .previous 备份。",
            "程序配置已更新",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private void RestoreEmbeddedIgrfConfiguration()
    {
        if (MessageBox.Show(
                this,
                "恢复程序内置的 IGRF 系数基准？当前用户配置会改名保留，不会删除。",
                "恢复内置 IGRF",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        IgrfConfigurationState state = _igrfConfiguration.RestoreEmbeddedBaseline();
        _igrfModelSet = null;
        _igrfModelSourcePath = null;
        MessageBox.Show(
            this,
            $"已恢复内置 IGRF 基准（{state.EarliestEpochYear}–{state.LatestEpochYear}，SHA-256 {state.Sha256}）。原用户配置已保留为 .disabled 备份。",
            "已恢复内置配置",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private void CopyCoordinateColumns(string mode)
    {
        if (_lastCoordinateBatch is null || _lastCoordinateBatch.Results.Count == 0) return;
        RequireCurrentCoordinateProjection();

        static string WithPointId(CoordinateConversionResult row, string payload) =>
            string.IsNullOrWhiteSpace(row.PointId)
                ? payload
                : $"{row.PointId}\t{payload}";

        string text = mode switch
        {
            "dms" => string.Join(Environment.NewLine,
                _lastCoordinateBatch.Results.Select(x =>
                    WithPointId(x, $"{x.LongitudeDms}\t{x.LatitudeDms}"))),
            "xy" => string.Join(Environment.NewLine,
                _lastCoordinateBatch.Results.Select(x =>
                {
                    (double outputX, double outputY) =
                        CoordinateRegistryService.CoordinateOutputValues(
                            x,
                            CoordinateAxisOrder(),
                            _lastCoordinateBatch.CentralMeridian,
                            _coordinateWorkspace.GetIncludeZoneNumber());
                    return WithPointId(
                        x,
                        $"{outputX.ToString("0.###", CultureInfo.InvariantCulture)}\t" +
                        outputY.ToString("0.###", CultureInfo.InvariantCulture));
                })),
            _ => string.Join(Environment.NewLine,
                _lastCoordinateBatch.Results.Select(x =>
                    WithPointId(
                        x,
                        $"{FormatCoordinateDecimal(x.Longitude)}\t{FormatCoordinateDecimal(x.Latitude)}")))
        };
        Clipboard.SetText(text);
        SetStatus("坐标结果已复制到剪贴板。");
    }

    private int CoordinateDecimalPlaces() =>
        _coordinateWorkspace.GetDecimalPlaces();

    private string FormatCoordinateDecimal(double value) =>
        value.ToString(
            $"F{CoordinateDecimalPlaces()}",
            CultureInfo.InvariantCulture);

    private XyAxisOrder CoordinateAxisOrder() =>
        _coordinateWorkspace.GetAxisIndex() == 1
            ? XyAxisOrder.NorthingEasting
            : XyAxisOrder.EastingNorthing;

    private static double ParseRequiredDouble(string text, string label)
    {
        if (!double.TryParse((text ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            || !double.IsFinite(value))
            throw new FormatException($"{label} 不是有效数字。");
        return value;
    }

    private static double? ParseOptionalDouble(string text, string label)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return ParseRequiredDouble(text, label);
    }
}

internal sealed record CoordinateConversionGridRow(
    int Index,
    string PointId,
    string Longitude,
    string Latitude,
    string LongitudeDms,
    string LatitudeDms,
    string X,
    string Y);

internal sealed record CoordinateRegistryGridRow(
    string CoordinateId,
    string Longitude,
    string Latitude,
    string Z,
    string HeightType,
    string Crs,
    string Source);

