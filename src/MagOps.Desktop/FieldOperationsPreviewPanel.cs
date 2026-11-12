using System.ComponentModel;
using System.Drawing.Drawing2D;
using MagOps.Infrastructure.Services;

namespace MagOps.Desktop;

internal sealed class FieldOperationsPreviewPanel : Panel
{
    private float UiScale => Math.Max(1f, DeviceDpi / 96f);
    private string _selectedSortieName = "";
    private readonly HashSet<string> _visibleServiceRecommendationIds =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<PointF[]>> _screenSortiePaths =
        new(StringComparer.OrdinalIgnoreCase);
    private GeoCoordinate? _baseStationCoordinate;
    private string _baseStationLabel = "";
    private bool _includeBaseStationInFit;
    private bool _showTakeoffRecommendations;
    private readonly PreviewNavigation _navigation;

    public event Action<string>? SortieClicked;
    public event Action? SelectionCleared;

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string SelectedSortieName
    {
        get => _selectedSortieName;
        set
        {
            string next = (value ?? "").Trim();
            if (_selectedSortieName.Equals(
                    next,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _selectedSortieName = next;
            Invalidate();
        }
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public FieldDailyAnalysisResult? Analysis { get; set; }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShowTakeoffRecommendations
    {
        get => _showTakeoffRecommendations;
        set
        {
            if (_showTakeoffRecommendations == value) return;
            _showTakeoffRecommendations = value;
            Invalidate();
        }
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool HasBaseStation =>
        _baseStationCoordinate is not null;

    public void SetBaseStation(
        GeoCoordinate? coordinate,
        string label)
    {
        _baseStationCoordinate = coordinate;
        _baseStationLabel =
            (label ?? "").Trim();
        _includeBaseStationInFit = false;
        Invalidate();
    }

    public void ShowBaseStationOverview()
    {
        if (_baseStationCoordinate is null)
            return;
        _includeBaseStationInFit = true;
        _navigation.Reset();
        Invalidate();
    }

    public void FitWorkarea()
    {
        _includeBaseStationInFit = false;
        _navigation.Reset();
        Invalidate();
    }

    public void SetVisibleServiceRecommendationIds(
        IEnumerable<string>? recommendationIds)
    {
        HashSet<string> next = (recommendationIds ?? [])
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (_visibleServiceRecommendationIds.SetEquals(next))
            return;

        _visibleServiceRecommendationIds.Clear();
        _visibleServiceRecommendationIds.UnionWith(next);
        Invalidate();
    }

    public FieldOperationsPreviewPanel()
    {
        SetStyle(ControlStyles.Selectable, true);
        _navigation = new PreviewNavigation(this);
        DoubleBuffered = true;
        BackColor = UiTheme.Surface;
        BorderStyle = BorderStyle.FixedSingle;
        Resize += (_, _) => Invalidate();
    }

    internal object ExportViewSnapshot() => new
    {
        renderer = "field-preview-1", width = ClientSize.Width, height = ClientSize.Height, dpi = DeviceDpi,
        selectedSortie = _selectedSortieName, showTakeoffRecommendations = _showTakeoffRecommendations,
        visibleRecommendationIds = _visibleServiceRecommendationIds.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
        baseStation = _baseStationCoordinate, baseStationLabel = _baseStationLabel,
        includeBaseStationInFit = _includeBaseStationInFit, navigation = _navigation.ExportViewSnapshot()
    };

    public Bitmap CaptureBitmap()
    {
        int width = Math.Max(1, ClientSize.Width);
        int height = Math.Max(1, ClientSize.Height);
        var bitmap = new Bitmap(width, height);
        bitmap.SetResolution(DeviceDpi, DeviceDpi);
        DrawToBitmap(bitmap, new Rectangle(0, 0, width, height));
        return bitmap;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var focusCue = _navigation.BeginPaint(e.Graphics);
        _screenSortiePaths.Clear();
        FieldDailyAnalysisResult? analysis = Analysis;
        _navigation.SetDataKey(analysis is null ? null : new { analysis.Route.Fingerprint, analysis.Route.SourcePath, _includeBaseStationInFit, _baseStationCoordinate });
        if (analysis is null || analysis.Route.Sorties.Count == 0)
        {
            DrawEmpty(
                e.Graphics,
                "分析所选日期后在此显示作业进度、设计航线和后续起飞安排。");
            return;
        }

        float ui = UiScale;
        float left = 18f * ui;
        float right = 18f * ui;
        float top = 72f * ui;      // reserved for graphical legend
        float bottom = 52f * ui;   // reserved for metric scale bar

        float width = ClientSize.Width - left - right;
        float height = ClientSize.Height - top - bottom;
        if (width < 80f * ui || height < 70f * ui)
        {
            DrawEmpty(
                e.Graphics,
                "航线预览区域过小；请增大窗口或右侧预览区域。");
            return;
        }

        static bool Valid(GeoCoordinate point) =>
            double.IsFinite(point.Longitude) &&
            double.IsFinite(point.Latitude) &&
            point.Longitude >= -180d &&
            point.Longitude <= 180d &&
            point.Latitude >= -90d &&
            point.Latitude <= 90d;

        List<GeoCoordinate> routePoints = analysis.Route.Sorties
            .SelectMany(x => x.Coordinates)
            .Where(Valid)
            .ToList();
        List<GeoCoordinate> recommendationPoints = ShowTakeoffRecommendations
            ? analysis.Recommendations.Select(x => x.Coordinate).Where(Valid).ToList()
            : [];

        List<GeoCoordinate> points = routePoints
            .Concat(recommendationPoints)
            .ToList();
        if (_includeBaseStationInFit &&
            _baseStationCoordinate is GeoCoordinate fitStation &&
            Valid(fitStation))
        {
            points.Add(fitStation);
        }
        if (routePoints.Count < 2 || points.Count < 2)
        {
            DrawEmpty(
                e.Graphics,
                "当前航线没有足够的有效经纬度点用于预览。");
            return;
        }

        double originLon = Median(points.Select(x => x.Longitude));
        double originLat = Median(points.Select(x => x.Latitude));
        double cosLat = Math.Max(
            0.05d,
            Math.Cos(originLat * Math.PI / 180d));

        (double X, double Y) World(GeoCoordinate point) =>
            ((point.Longitude - originLon) * 111320d * cosLat,
             (point.Latitude - originLat) * 110540d);

        // Only suppress truly extreme display outliers (for example an accidental
        // 0,0 coordinate in an otherwise local project). This affects preview only;
        // route data and progress calculations remain unchanged.
        double[] radial = points
            .Select(p =>
            {
                (double x, double y) = World(p);
                return Math.Sqrt(x * x + y * y);
            })
            .OrderBy(x => x)
            .ToArray();
        double p95 = radial[(int)Math.Clamp(
            Math.Floor((radial.Length - 1) * 0.95d),
            0,
            radial.Length - 1)];
        double displayRadius = Math.Max(
            250_000d,
            Math.Max(1d, p95) * 8d);

        bool Displayable(GeoCoordinate point)
        {
            if (!Valid(point))
                return false;
            (double x, double y) = World(point);
            return Math.Sqrt(x * x + y * y) <= displayRadius;
        }

        List<GeoCoordinate> displayPoints = points
            .Where(Displayable)
            .ToList();
        if (_includeBaseStationInFit &&
            _baseStationCoordinate is GeoCoordinate overviewStation &&
            Valid(overviewStation) &&
            !displayPoints.Contains(overviewStation))
        {
            displayPoints.Add(overviewStation);
        }
        if (displayPoints.Count < 2)
            displayPoints = points;

        var world = displayPoints.Select(World).ToArray();
        double minX = world.Min(x => x.X);
        double maxX = world.Max(x => x.X);
        double minY = world.Min(x => x.Y);
        double maxY = world.Max(x => x.Y);
        double spanX = Math.Max(10d, maxX - minX);
        double spanY = Math.Max(10d, maxY - minY);

        // Fit-to-bounds with 6% visual padding. This is a real metres-to-pixels
        // transform, so the scale bar remains meaningful.
        double scale = Math.Min(width / spanX, height / spanY) * 0.94d;
        if (!double.IsFinite(scale) || scale <= 0d)
        {
            DrawEmpty(e.Graphics, "无法计算当前航线的预览比例。");
            return;
        }

        double drawnWidth = spanX * scale;
        double drawnHeight = spanY * scale;
        double padX = (width - drawnWidth) / 2d;
        double padY = (height - drawnHeight) / 2d;

        PointF Screen(GeoCoordinate point)
        {
            (double x, double y) = World(point);
            return _navigation.Apply(new PointF(
                (float)(left + padX + (x - minX) * scale),
                (float)(top + padY + (maxY - y) * scale)));
        }

        void RememberScreenPath(
            string logicalName,
            PointF[] path)
        {
            if (!_screenSortiePaths.TryGetValue(
                    logicalName,
                    out List<PointF[]>? paths))
            {
                paths = [];
                _screenSortiePaths[logicalName] =
                    paths;
            }
            paths.Add(path);
        }

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        HashSet<string> completed = analysis.CompletedCumulative
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> selectedDay = analysis.CompletedToday
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> repeatedCumulative =
            analysis.RepeatedSortiesCumulative
                .ToHashSet(
                    StringComparer.OrdinalIgnoreCase);
        HashSet<string> repeatedToday =
            analysis.RepeatedSortiesToday
                .ToHashSet(
                    StringComparer.OrdinalIgnoreCase);

        foreach (PlannedFieldSortie sortie in analysis.Route.Sorties)
        {
            PointF[] path = sortie.Coordinates
                .Where(Displayable)
                .Select(Screen)
                .ToArray();
            if (path.Length < 2)
                continue;

            RememberScreenPath(
                sortie.Name,
                path);

            bool isSelectedDay = selectedDay.Contains(sortie.Name);
            bool isDone = completed.Contains(sortie.Name);
            Color routeColor =
                isSelectedDay
                    ? UiTheme.Accent
                    : isDone
                        ? UiTheme.Success
                        : UiTheme.BorderStrong;
            float routeWidth =
                isSelectedDay
                    ? 2.3f * ui
                    : 1.25f * ui;
            using var pen = new Pen(
                routeColor,
                routeWidth)
            {
                LineJoin = LineJoin.Round
            };
            e.Graphics.DrawLines(pen, path);

            if (repeatedCumulative.Contains(
                    sortie.Name))
            {
                bool isRepeatedToday =
                    repeatedToday.Contains(
                        sortie.Name);
                using var repeatPen =
                    new Pen(
                        Color.FromArgb(
                            isRepeatedToday
                                ? 210
                                : 145,
                            UiTheme.RepeatedMeasurement),
                        isRepeatedToday
                            ? 2.0f * ui
                            : 1.5f * ui)
                    {
                        LineJoin = LineJoin.Round,
                        DashStyle = DashStyle.Dash
                    };
                e.Graphics.DrawLines(
                    repeatPen,
                    path);
            }
        }

        Dictionary<string, PlannedFieldSortie> byName =
            analysis.Route.Sorties.ToDictionary(
                x => x.Name,
                StringComparer.OrdinalIgnoreCase);

        // A recommendation's "服务架次" is now rendered as the actual route
        // geometry with a dashed warning overlay, rather than only attempting a
        // takeoff-point-to-first-point connector (which can be zero-length when
        // the medoid is itself a sortie start).
        List<FieldTakeoffRecommendation> visibleServiceRecommendations =
            analysis.Recommendations
                .Where(x =>
                    ShowTakeoffRecommendations && _visibleServiceRecommendationIds.Contains(
                        x.RecommendationId))
                .ToList();
        HashSet<string> serviceSortieNames =
            visibleServiceRecommendations
                .SelectMany(x => x.SortieNames)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        using (var servicePen = new Pen(
            UiTheme.Warning,
            1.8f * ui)
        {
            DashStyle = DashStyle.Dash,
            DashCap = DashCap.Round,
            LineJoin = LineJoin.Round
        })
        {
            foreach (string sortieName in serviceSortieNames)
            {
                if (!byName.TryGetValue(
                        sortieName,
                        out PlannedFieldSortie? sortie))
                {
                    continue;
                }

                PointF[] servicePath = sortie.Coordinates
                    .Where(Displayable)
                    .Select(Screen)
                    .ToArray();
                if (servicePath.Length >= 2)
                    e.Graphics.DrawLines(
                        servicePen,
                        servicePath);
            }
        }

        // Keep a lighter relationship line from each recommended point to the
        // nearest visible point of every served sortie. This is supplementary:
        // the dashed route overlay above is the primary "服务架次" rendering.
        using var linkPen = new Pen(
            Color.FromArgb(
                135,
                UiTheme.Warning),
            1.05f * ui)
        {
            DashStyle = DashStyle.Dot
        };
        foreach (FieldTakeoffRecommendation recommendation in visibleServiceRecommendations)
        {
            if (!Displayable(recommendation.Coordinate))
                continue;

            PointF source = Screen(recommendation.Coordinate);
            foreach (string sortieName in recommendation.SortieNames)
            {
                if (!byName.TryGetValue(
                        sortieName,
                        out PlannedFieldSortie? sortie))
                {
                    continue;
                }

                GeoCoordinate? nearest = sortie.Coordinates
                    .Where(Displayable)
                    .OrderBy(point =>
                    {
                        PointF candidate = Screen(point);
                        double dx = candidate.X - source.X;
                        double dy = candidate.Y - source.Y;
                        return dx * dx + dy * dy;
                    })
                    .FirstOrDefault();
                if (nearest is null)
                    continue;

                PointF target = Screen(nearest);
                double screenDistance =
                    Math.Sqrt(
                        Math.Pow(target.X - source.X, 2) +
                        Math.Pow(target.Y - source.Y, 2));
                if (screenDistance >= 5f * ui)
                {
                    e.Graphics.DrawLine(
                        linkPen,
                        source,
                        target);
                }
            }
        }

        // Clicking a sortie in the task-progress grid drives this explicit map
        // highlight. Draw it after status/service overlays so it always wins.
        if (!string.IsNullOrWhiteSpace(_selectedSortieName) &&
            _screenSortiePaths.TryGetValue(
                _selectedSortieName,
                out List<PointF[]>? selectedPaths) &&
            selectedPaths.Count > 0)
        {
            using var haloPen = new Pen(
                Color.FromArgb(
                    85,
                    SystemColors.Highlight),
                7f * ui)
            {
                LineJoin = LineJoin.Round
            };
            using var selectedPen = new Pen(
                SystemColors.Highlight,
                3.4f * ui)
            {
                LineJoin = LineJoin.Round
            };

            foreach (PointF[] selectedPath in selectedPaths)
            {
                if (selectedPath.Length < 2)
                    continue;

                e.Graphics.DrawLines(
                    haloPen,
                    selectedPath);
                e.Graphics.DrawLines(
                    selectedPen,
                    selectedPath);
            }

            PointF[] labelPath =
                selectedPaths
                    .FirstOrDefault(x => x.Length >= 2)
                ?? [];
            if (labelPath.Length >= 2)
            {
                PointF labelPoint =
                    labelPath[labelPath.Length / 2];
                string label =
                    _selectedSortieName;
                Size labelSize = TextRenderer.MeasureText(
                    label,
                    Font,
                    Size.Empty,
                    TextFormatFlags.NoPadding);
                var labelRect = new Rectangle(
                    (int)(labelPoint.X + 8f * ui),
                    (int)(labelPoint.Y - labelSize.Height - 6f * ui),
                    labelSize.Width + (int)(12f * ui),
                    labelSize.Height + (int)(6f * ui));
                labelRect.X = Math.Clamp(
                    labelRect.X,
                    4,
                    Math.Max(
                        4,
                        ClientSize.Width -
                        labelRect.Width -
                        4));
                labelRect.Y = Math.Clamp(
                    labelRect.Y,
                    (int)top,
                    Math.Max(
                        (int)top,
                        ClientSize.Height -
                        labelRect.Height -
                        (int)bottom));

                using var selectedLabelBrush =
                    new SolidBrush(UiTheme.Surface);
                using var selectedLabelPen =
                    new Pen(SystemColors.Highlight);
                e.Graphics.FillRectangle(
                    selectedLabelBrush,
                    labelRect);
                e.Graphics.DrawRectangle(
                    selectedLabelPen,
                    labelRect);
                TextRenderer.DrawText(
                    e.Graphics,
                    label,
                    Font,
                    Rectangle.Inflate(
                        labelRect,
                        -(int)(5f * ui),
                        -(int)(2f * ui)),
                    SystemColors.Highlight,
                    UiTheme.Surface,
                    TextFormatFlags.NoPadding |
                    TextFormatFlags.SingleLine);
            }
        }

        foreach (PlannedFieldSortie sortie in analysis.Route.Sorties)
        {
            GeoCoordinate? first = sortie.Coordinates
                .FirstOrDefault(Displayable);
            if (first is null)
                continue;

            PointF p = Screen(first);
            bool isDone = completed.Contains(sortie.Name);
            using var brush = new SolidBrush(
                isDone
                    ? UiTheme.Success
                    : Color.FromArgb(95, 108, 123));
            float r = isDone ? 3.6f * ui : 3f * ui;
            e.Graphics.FillEllipse(
                brush,
                p.X - r,
                p.Y - r,
                r * 2f,
                r * 2f);
        }

        using var recommendationBrush =
            new SolidBrush(UiTheme.Warning);
        using var recommendationPen =
            new Pen(UiTheme.Surface, 2f * ui);
        using var labelFont = new Font(
            Font.FontFamily,
            Math.Max(7f, Font.Size - 0.5f),
            FontStyle.Bold);

        foreach (FieldTakeoffRecommendation recommendation in analysis.Recommendations)
        {
            if (!ShowTakeoffRecommendations || !Displayable(recommendation.Coordinate))
                continue;

            PointF p = Screen(recommendation.Coordinate);
            float r = 7f * ui;
            e.Graphics.FillEllipse(
                recommendationBrush,
                p.X - r,
                p.Y - r,
                r * 2f,
                r * 2f);
            e.Graphics.DrawEllipse(
                recommendationPen,
                p.X - r,
                p.Y - r,
                r * 2f,
                r * 2f);
            TextRenderer.DrawText(
                e.Graphics,
                recommendation.RecommendationId,
                labelFont,
                new Point(
                    (int)(p.X + 8f * ui),
                    (int)(p.Y - 8f * ui)),
                UiTheme.TextPrimary,
                UiTheme.Surface,
                TextFormatFlags.NoPadding |
                TextFormatFlags.SingleLine);
        }

        if (_baseStationCoordinate is GeoCoordinate station &&
            Valid(station))
        {
            PointF stationScreen =
                Screen(station);
            var plotRect = new RectangleF(
                left,
                top,
                width,
                height);
            string stationLabel =
                string.IsNullOrWhiteSpace(_baseStationLabel)
                    ? "日变站"
                    : _baseStationLabel;

            if (_includeBaseStationInFit ||
                plotRect.Contains(stationScreen))
            {
                DrawBaseStationMarker(
                    e.Graphics,
                    stationScreen,
                    stationLabel,
                    ui,
                    plotRect);
            }
            else
            {
                GeoCoordinate routeCenter =
                    new(
                        Median(routePoints.Select(x => x.Longitude)),
                        Median(routePoints.Select(x => x.Latitude)));
                PointF centerScreen =
                    Screen(routeCenter);
                double distanceM =
                    DistanceMeters(
                        routeCenter,
                        station);
                DrawBaseStationEdgeIndicator(
                    e.Graphics,
                    centerScreen,
                    stationScreen,
                    stationLabel,
                    distanceM,
                    ui,
                    plotRect);
            }
        }

        int hidden = points.Count - displayPoints.Count;
        using (var legendBrush = new SolidBrush(UiTheme.Surface))
        {
            e.Graphics.FillRectangle(
                legendBrush,
                new RectangleF(
                    0,
                    0,
                    ClientSize.Width,
                    top - 2f * ui));
        }

        DrawLegend(
            e.Graphics,
            ui,
            hidden);

        PreviewScaleBar.Draw(
            e.Graphics,
            this,
            1d / (scale * _navigation.Zoom));
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left ||
            _screenSortiePaths.Count == 0)
        {
            return;
        }

        string? bestName = null;
        double bestDistance = double.MaxValue;
        foreach (KeyValuePair<string, List<PointF[]>> pair in _screenSortiePaths)
        {
            foreach (PointF[] path in pair.Value)
            {
                for (int i = 1; i < path.Length; i++)
                {
                    double distance = DistanceToSegment(
                        e.Location,
                        path[i - 1],
                        path[i]);
                    if (distance >= bestDistance)
                        continue;

                    bestDistance = distance;
                    bestName = pair.Key;
                }
            }
        }

        double tolerance = 10d * UiScale;
        if (bestName is null ||
            bestDistance > tolerance)
        {
            if (!string.IsNullOrWhiteSpace(
                    SelectedSortieName))
            {
                SelectedSortieName = "";
                SelectionCleared?.Invoke();
            }
            return;
        }

        SelectedSortieName = bestName;
        SortieClicked?.Invoke(bestName);
    }

    private static double DistanceToSegment(
        PointF point,
        PointF start,
        PointF end)
    {
        double dx = end.X - start.X;
        double dy = end.Y - start.Y;
        double lengthSquared = dx * dx + dy * dy;
        if (lengthSquared <= double.Epsilon)
        {
            double px = point.X - start.X;
            double py = point.Y - start.Y;
            return Math.Sqrt(px * px + py * py);
        }

        double t =
            ((point.X - start.X) * dx +
             (point.Y - start.Y) * dy) /
            lengthSquared;
        t = Math.Clamp(t, 0d, 1d);
        double nearestX = start.X + t * dx;
        double nearestY = start.Y + t * dy;
        double offsetX = point.X - nearestX;
        double offsetY = point.Y - nearestY;
        return Math.Sqrt(
            offsetX * offsetX +
            offsetY * offsetY);
    }

    private void DrawLegend(
        Graphics graphics,
        float ui,
        int hiddenPointCount)
    {
        float x = 10f * ui;
        float y = 8f * ui;
        float maxX = Math.Max(x, ClientSize.Width - 12f * ui);
        float rowHeight = 22f * ui;

        x = DrawLineLegendItem(
            graphics,
            x,
            y,
            maxX,
            rowHeight,
            ui,
            "未开展",
            UiTheme.BorderStrong,
            dashed: false,
            lineWidth: 1.4f);
        x = DrawLineLegendItem(
            graphics,
            x,
            y,
            maxX,
            rowHeight,
            ui,
            "累计完成",
            UiTheme.Success,
            dashed: false,
            lineWidth: 2.1f);
        x = DrawLineLegendItem(
            graphics,
            x,
            y,
            maxX,
            rowHeight,
            ui,
            "所选日完成",
            UiTheme.Accent,
            dashed: false,
            lineWidth: 2.3f);
        x = DrawLineLegendItem(
            graphics,
            x,
            y,
            maxX,
            rowHeight,
            ui,
            "重复飞行（后测时段）",
            UiTheme.RepeatedMeasurement,
            dashed: true,
            lineWidth: 1.8f);
        x = DrawLineLegendItem(
            graphics,
            x,
            y,
            maxX,
            rowHeight,
            ui,
            "当前选中",
            SystemColors.Highlight,
            dashed: false,
            lineWidth: 3.2f);
        if (ShowTakeoffRecommendations)
        {
            x = DrawPointLegendItem(
                graphics,
                x,
                y,
                maxX,
                rowHeight,
                ui,
                "推荐起飞点",
                UiTheme.Warning);
        }
        x = DrawPointLegendItem(
            graphics,
            x,
            y,
            maxX,
            rowHeight,
            ui,
            "日变站",
            UiTheme.Danger);
        if (ShowTakeoffRecommendations && _visibleServiceRecommendationIds.Count > 0)
        {
            x = DrawLineLegendItem(
                graphics,
                x,
                y,
                maxX,
                rowHeight,
                ui,
                "服务架次",
                UiTheme.Warning,
                dashed: true,
                lineWidth: 1.2f);
        }

        if (hiddenPointCount > 0)
        {
            string note = $"预览忽略 {hiddenPointCount} 个极端异常坐标";
            Size noteSize = TextRenderer.MeasureText(
                graphics,
                note,
                Font,
                new Size(int.MaxValue, int.MaxValue),
                TextFormatFlags.NoPadding |
                TextFormatFlags.SingleLine);
            float noteWidth = noteSize.Width + 12f * ui;
            if (x + noteWidth > maxX)
            {
                x = 10f * ui;
                y += rowHeight;
            }

            TextRenderer.DrawText(
                graphics,
                note,
                Font,
                new Point((int)x, (int)(y + 2f * ui)),
                UiTheme.TextSecondary,
                UiTheme.Surface,
                TextFormatFlags.NoPadding |
                TextFormatFlags.SingleLine);
        }
    }

    private float DrawLineLegendItem(
        Graphics graphics,
        float x,
        float y,
        float maxX,
        float rowHeight,
        float ui,
        string label,
        Color color,
        bool dashed,
        float lineWidth)
    {
        Size labelSize = TextRenderer.MeasureText(
            graphics,
            label,
            Font,
            new Size(int.MaxValue, int.MaxValue),
            TextFormatFlags.NoPadding |
            TextFormatFlags.SingleLine);
        float sampleWidth = 28f * ui;
        float itemWidth =
            sampleWidth +
            5f * ui +
            labelSize.Width +
            18f * ui;
        if (x > 10f * ui &&
            x + itemWidth > maxX)
        {
            x = 10f * ui;
            y += rowHeight;
        }

        using var pen = new Pen(
            color,
            lineWidth * ui)
        {
            DashStyle = dashed
                ? DashStyle.Dash
                : DashStyle.Solid,
            LineJoin = LineJoin.Round
        };
        float centerY = y + 9f * ui;
        graphics.DrawLine(
            pen,
            x,
            centerY,
            x + sampleWidth,
            centerY);

        float textX = x + sampleWidth + 5f * ui;
        TextRenderer.DrawText(
            graphics,
            label,
            Font,
            new Point(
                (int)textX,
                (int)(y + 1f * ui)),
            UiTheme.TextSecondary,
            UiTheme.Surface,
            TextFormatFlags.NoPadding |
            TextFormatFlags.SingleLine);
        return x + itemWidth;
    }

    private float DrawPointLegendItem(
        Graphics graphics,
        float x,
        float y,
        float maxX,
        float rowHeight,
        float ui,
        string label,
        Color color)
    {
        Size labelSize = TextRenderer.MeasureText(
            graphics,
            label,
            Font,
            new Size(int.MaxValue, int.MaxValue),
            TextFormatFlags.NoPadding |
            TextFormatFlags.SingleLine);
        float sampleWidth = 28f * ui;
        float itemWidth =
            sampleWidth +
            5f * ui +
            labelSize.Width +
            18f * ui;
        if (x > 10f * ui &&
            x + itemWidth > maxX)
        {
            x = 10f * ui;
            y += rowHeight;
        }

        float radius = 5.5f * ui;
        float centerX = x + sampleWidth / 2f;
        float centerY = y + 9f * ui;
        using var brush = new SolidBrush(color);
        using var outline = new Pen(UiTheme.Surface, 1.4f * ui);
        graphics.FillEllipse(
            brush,
            centerX - radius,
            centerY - radius,
            radius * 2f,
            radius * 2f);
        graphics.DrawEllipse(
            outline,
            centerX - radius,
            centerY - radius,
            radius * 2f,
            radius * 2f);

        float textX = x + sampleWidth + 5f * ui;
        TextRenderer.DrawText(
            graphics,
            label,
            Font,
            new Point(
                (int)textX,
                (int)(y + 1f * ui)),
            UiTheme.TextSecondary,
            UiTheme.Surface,
            TextFormatFlags.NoPadding |
            TextFormatFlags.SingleLine);
        return x + itemWidth;
    }

    private void DrawBaseStationMarker(
        Graphics graphics,
        PointF point,
        string label,
        float ui,
        RectangleF plotRect)
    {
        float radius = 7f * ui;
        using var fill =
            new SolidBrush(
                Color.FromArgb(
                    230,
                    UiTheme.Danger));
        using var outline =
            new Pen(
                UiTheme.Surface,
                2f * ui);
        using var cross =
            new Pen(
                UiTheme.Surface,
                1.5f * ui);
        graphics.FillEllipse(
            fill,
            point.X - radius,
            point.Y - radius,
            radius * 2f,
            radius * 2f);
        graphics.DrawEllipse(
            outline,
            point.X - radius,
            point.Y - radius,
            radius * 2f,
            radius * 2f);
        graphics.DrawLine(
            cross,
            point.X - radius * 0.55f,
            point.Y,
            point.X + radius * 0.55f,
            point.Y);
        graphics.DrawLine(
            cross,
            point.X,
            point.Y - radius * 0.55f,
            point.X,
            point.Y + radius * 0.55f);

        string text =
            $"日变站 {label}";
        Size size =
            TextRenderer.MeasureText(
                graphics,
                text,
                Font,
                new Size(int.MaxValue, int.MaxValue),
                TextFormatFlags.NoPadding |
                TextFormatFlags.SingleLine);
        int x =
            Math.Clamp(
                (int)(point.X + 9f * ui),
                (int)plotRect.Left,
                Math.Max(
                    (int)plotRect.Left,
                    (int)(plotRect.Right - size.Width - 8f * ui)));
        int y =
            Math.Clamp(
                (int)(point.Y - size.Height - 5f * ui),
                (int)plotRect.Top,
                Math.Max(
                    (int)plotRect.Top,
                    (int)(plotRect.Bottom - size.Height)));
        TextRenderer.DrawText(
            graphics,
            text,
            Font,
            new Point(x, y),
            UiTheme.Danger,
            UiTheme.Surface,
            TextFormatFlags.NoPadding |
            TextFormatFlags.SingleLine);
    }

    private void DrawBaseStationEdgeIndicator(
        Graphics graphics,
        PointF routeCenter,
        PointF stationScreen,
        string label,
        double distanceM,
        float ui,
        RectangleF plotRect)
    {
        double dx =
            stationScreen.X - routeCenter.X;
        double dy =
            stationScreen.Y - routeCenter.Y;
        if (Math.Abs(dx) < 0.001d &&
            Math.Abs(dy) < 0.001d)
        {
            return;
        }

        float centerX =
            Math.Clamp(
                routeCenter.X,
                plotRect.Left,
                plotRect.Right);
        float centerY =
            Math.Clamp(
                routeCenter.Y,
                plotRect.Top,
                plotRect.Bottom);
        double halfW =
            Math.Max(
                1d,
                Math.Min(
                    centerX - plotRect.Left,
                    plotRect.Right - centerX) -
                12f * ui);
        double halfH =
            Math.Max(
                1d,
                Math.Min(
                    centerY - plotRect.Top,
                    plotRect.Bottom - centerY) -
                12f * ui);
        double tx =
            Math.Abs(dx) <= double.Epsilon
                ? double.PositiveInfinity
                : halfW / Math.Abs(dx);
        double ty =
            Math.Abs(dy) <= double.Epsilon
                ? double.PositiveInfinity
                : halfH / Math.Abs(dy);
        double t =
            Math.Min(tx, ty);
        float edgeX =
            Math.Clamp(
                (float)(centerX + dx * t),
                plotRect.Left + 8f * ui,
                plotRect.Right - 8f * ui);
        float edgeY =
            Math.Clamp(
                (float)(centerY + dy * t),
                plotRect.Top + 8f * ui,
                plotRect.Bottom - 8f * ui);

        string arrow =
            DirectionArrow(dx, dy);
        string distanceText =
            distanceM >= 1000d
                ? $"{distanceM / 1000d:0.#} km"
                : $"{distanceM:0} m";
        string text =
            $"{arrow} {label}  {distanceText}";
        Size size =
            TextRenderer.MeasureText(
                graphics,
                text,
                Font,
                new Size(int.MaxValue, int.MaxValue),
                TextFormatFlags.NoPadding |
                TextFormatFlags.SingleLine);
        var rect = new Rectangle(
            (int)(edgeX - size.Width / 2f - 7f * ui),
            (int)(edgeY - size.Height / 2f - 4f * ui),
            size.Width + (int)(14f * ui),
            size.Height + (int)(8f * ui));
        rect.X = Math.Clamp(
            rect.X,
            (int)plotRect.Left,
            Math.Max(
                (int)plotRect.Left,
                (int)plotRect.Right - rect.Width));
        rect.Y = Math.Clamp(
            rect.Y,
            (int)plotRect.Top,
            Math.Max(
                (int)plotRect.Top,
                (int)plotRect.Bottom - rect.Height));

        using var background =
            new SolidBrush(
                Color.FromArgb(
                    238,
                    UiTheme.DangerSoft));
        using var border =
            new Pen(
                UiTheme.Danger,
                1f * ui);
        graphics.FillRectangle(
            background,
            rect);
        graphics.DrawRectangle(
            border,
            rect);
        TextRenderer.DrawText(
            graphics,
            text,
            Font,
            Rectangle.Inflate(
                rect,
                -(int)(6f * ui),
                -(int)(3f * ui)),
            UiTheme.Danger,
            UiTheme.DangerSoft,
            TextFormatFlags.NoPadding |
            TextFormatFlags.SingleLine |
            TextFormatFlags.VerticalCenter);
    }

    private static string DirectionArrow(
        double dx,
        double dy)
    {
        double angle =
            Math.Atan2(-dy, dx) *
            180d /
            Math.PI;
        if (angle < 0d)
            angle += 360d;
        if (angle < 22.5d || angle >= 337.5d)
            return "→";
        if (angle < 67.5d)
            return "↗";
        if (angle < 112.5d)
            return "↑";
        if (angle < 157.5d)
            return "↖";
        if (angle < 202.5d)
            return "←";
        if (angle < 247.5d)
            return "↙";
        if (angle < 292.5d)
            return "↓";
        return "↘";
    }

    private static double DistanceMeters(
        GeoCoordinate a,
        GeoCoordinate b)
    {
        double meanLat =
            (a.Latitude + b.Latitude) *
            0.5d *
            Math.PI /
            180d;
        double dx =
            (b.Longitude - a.Longitude) *
            111320d *
            Math.Max(
                0.05d,
                Math.Cos(meanLat));
        double dy =
            (b.Latitude - a.Latitude) *
            110540d;
        return Math.Sqrt(
            dx * dx +
            dy * dy);
    }

    private static double Median(IEnumerable<double> values)
    {
        double[] ordered = values
            .OrderBy(x => x)
            .ToArray();
        if (ordered.Length == 0)
            return 0d;
        int middle = ordered.Length / 2;
        return ordered.Length % 2 == 0
            ? (ordered[middle - 1] + ordered[middle]) / 2d
            : ordered[middle];
    }

    private void DrawEmpty(
        Graphics graphics,
        string message)
    {
        TextRenderer.DrawText(
            graphics,
            message,
            Font,
            new Rectangle(
                18,
                18,
                Math.Max(0, Width - 36),
                Math.Max(56, Height - 36)),
            UiTheme.TextSecondary,
            TextFormatFlags.WordBreak |
            TextFormatFlags.HorizontalCenter |
            TextFormatFlags.VerticalCenter);
    }
}
