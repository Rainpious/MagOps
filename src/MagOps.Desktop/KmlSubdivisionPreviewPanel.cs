using System.ComponentModel;
using System.Drawing.Drawing2D;
using MagOps.Core.SurveyPlanning;

namespace MagOps.Desktop;

internal sealed class KmlSubdivisionPreviewPanel : Panel
{
    private List<List<SurveyBoundaryPoint>> _surveyTracks = [];
    internal void SetSurveyTracks(IEnumerable<MagOps.Core.Routes.ReferenceRoad> roads, double centralMeridian, string datum)
    {
        _surveyTracks = roads.SelectMany(r => r.Segments).Select(segment => segment.Select(point =>
        {
            var xy = MagOps.Core.Coordinates.CoordinateService.GaussKrugerForward(point.Longitude, point.Latitude, centralMeridian, datum);
            return new SurveyBoundaryPoint(xy.Easting, xy.Northing);
        }).ToList()).ToList();
        Invalidate();
    }
    private float UiScale => Math.Max(1f, DeviceDpi / 96f);
    private float PlotLeft => 40f * UiScale;
    private float PlotRight => 24f * UiScale;
    private float PlotTop => 42f * UiScale;
    private float PlotBottom => 54f * UiScale;

    private readonly List<SurveySubdivisionCutLine> _cuts = [];
    private readonly List<SurveyLineSubdivisionBlock> _previewBlocks = [];
    private int _selectedCutIndex = -1;
    private bool _drawing;
    private bool _dragging;
    private SurveyBoundaryPoint? _draftStart;
    private SurveyBoundaryPoint? _draftEnd;
    private SurveyBoundaryPoint? _lastDragWorld;
    private SurveySubdivisionOperationPlan? _operationPlan;
    private double _overlapLengthM;
    private readonly PreviewNavigation _navigation;

    public void SetOverlapLength(double lengthM)
    {
        _overlapLengthM = lengthM;
        RebuildChildPreview();
        Invalidate();
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public SurveyLinePlanResult? MotherPlan { get; private set; }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public SurveySubdivisionAxis Axis { get; private set; } =
        SurveySubdivisionAxis.AlongSurveyLine;

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ManualDrawingEnabled { get; private set; }

    public event Action<IReadOnlyList<SurveySubdivisionCutLine>>? CutsChanged;

    public KmlSubdivisionPreviewPanel()
    {
        SetStyle(ControlStyles.Selectable, true);
        _navigation = new PreviewNavigation(this);
        DoubleBuffered = true;
        BackColor = SystemColors.Window;
        BorderStyle = BorderStyle.FixedSingle;
        Cursor = Cursors.Cross;
        Resize += (_, _) => Invalidate();
    }

    public IReadOnlyList<SurveySubdivisionCutLine> GetCuts() =>
        _cuts.ToArray();

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int PreviewSubareaCount => _previewBlocks.Count;

    public void SetPreview(
        SurveyLinePlanResult motherPlan,
        IReadOnlyList<SurveySubdivisionCutLine> cuts,
        SurveySubdivisionAxis axis,
        bool manualDrawingEnabled)
    {
        _navigation.SetDataKey(new { motherPlan.Boundary });
        MotherPlan = motherPlan;
        _operationPlan = null;
        Axis = axis;
        ManualDrawingEnabled = manualDrawingEnabled;
        _cuts.Clear();
        _cuts.AddRange(cuts.Where(cut => cut.Length > 1e-7));
        _selectedCutIndex = _cuts.Count > 0 ? 0 : -1;
        _drawing = false;
        _dragging = false;
        _draftStart = null;
        _draftEnd = null;
        RebuildChildPreview();
        Invalidate();
    }


    public void SetOperationPreview(
        SurveyLinePlanResult motherPlan,
        SurveySubdivisionOperationPlan operationPlan)
    {
        _navigation.SetDataKey(new { motherPlan.Boundary });
        MotherPlan = motherPlan;
        _operationPlan = operationPlan;
        Axis = SurveySubdivisionAxis.Custom;
        ManualDrawingEnabled = false;
        _cuts.Clear();
        _cuts.AddRange(operationPlan.Operations.Select(x => x.Cut));
        _selectedCutIndex = -1;
        _drawing = false;
        _dragging = false;
        _draftStart = null;
        _draftEnd = null;
        RebuildChildPreview();
        Invalidate();
    }

    public void ClearPreview()
    {
        _navigation.SetDataKey(null);
        MotherPlan = null;
        _surveyTracks.Clear();
        _operationPlan = null;
        _cuts.Clear();
        _previewBlocks.Clear();
        _selectedCutIndex = -1;
        _drawing = false;
        _dragging = false;
        _draftStart = null;
        _draftEnd = null;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var focusCue = _navigation.BeginPaint(e.Graphics);
        if (MotherPlan is null || MotherPlan.Boundary.Count < 3)
        {
            TextRenderer.DrawText(
                e.Graphics,
                "选择 KML 后点击“预览切分”；手绘模式可直接拖出切分线。",
                Font,
                new Rectangle(10, 10, Math.Max(0, Width - 20), 40),
                SystemColors.GrayText,
                TextFormatFlags.WordBreak);
            return;
        }

        if (!TryCreateTransform(out Func<SurveyBoundaryPoint, PointF> screen, out _))
            return;

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

        PointF[] polygon = MotherPlan.Boundary
            .Select(screen)
            .ToArray();
        using (var boundaryPen = new Pen(SystemColors.ControlDarkDark, 2f))
        using (var boundaryBrush = new SolidBrush(SystemColors.ControlLightLight))
        {
            if (polygon.Length >= 3)
                e.Graphics.FillPolygon(boundaryBrush, polygon);
            if (polygon.Length >= 2)
                e.Graphics.DrawPolygon(boundaryPen, polygon);
        }

        using (var surveyPen = new Pen(SystemColors.ControlDark, 1f))
        {
            surveyPen.DashStyle = DashStyle.Dot;
            foreach (SurveyPlannedLine line in MotherPlan.Lines)
            foreach (SurveyLineSegment segment in line.Segments)
                e.Graphics.DrawLine(surveyPen, screen(segment.Start), screen(segment.End));
        }

        using (var childPen = new Pen(SystemColors.ControlDark, 1.4f)
               {
                   DashStyle = DashStyle.Dash
               })
        {
            foreach (SurveyLineSubdivisionBlock block in _previewBlocks)
            {
                if (_overlapLengthM > 0)
                {
                    using var linePen = new Pen(block.Index % 2 == 0 ? Color.RoyalBlue : Color.DarkOrange, 2f);
                    foreach (var segment in block.Lines.SelectMany(line => line.Segments))
                        e.Graphics.DrawLine(linePen, screen(segment.Start), screen(segment.End));
                }
                PointF[] childPolygon = block.Boundary.Select(screen).ToArray();
                if (childPolygon.Length >= 3)
                    e.Graphics.DrawPolygon(childPen, childPolygon);
                if (block.Boundary.Count > 0)
                {
                    var center = new SurveyBoundaryPoint(
                        block.Boundary.Average(point => point.X),
                        block.Boundary.Average(point => point.Y));
                    PointF label = screen(center);
                    TextRenderer.DrawText(
                        e.Graphics,
                        $"子{block.Index}",
                        Font,
                        new Point((int)label.X + 4, (int)label.Y + 4),
                        SystemColors.ControlText,
                        SystemColors.Window,
                        TextFormatFlags.NoPadding);
                }
            }
        }

        using (var trackPen = new Pen(Color.DarkCyan, 2.5f))
            foreach (var segment in _surveyTracks)
                if (segment.Count >= 2) e.Graphics.DrawLines(trackPen, segment.Select(screen).ToArray());
        if (_surveyTracks.Count > 0)
            TextRenderer.DrawText(e.Graphics, "青色：踏勘轨迹参考", Font, new Point(8, 27), Color.DarkCyan);

        for (int i = 0; i < _cuts.Count; i++)
            DrawCut(e.Graphics, screen, _cuts[i], i == _selectedCutIndex, i + 1);

        if (_drawing && _draftStart is not null && _draftEnd is not null)
        {
            using var draftPen = new Pen(SystemColors.Highlight, 2f)
            {
                DashStyle = DashStyle.Dash
            };
            e.Graphics.DrawLine(
                draftPen,
                screen(_draftStart),
                screen(_draftEnd));
        }

        string modeText = Axis switch
        {
            SurveySubdivisionAxis.ProjectedX => "X/Easting（经度向）等分",
            SurveySubdivisionAxis.ProjectedY => "Y/Northing（纬度向）等分",
            SurveySubdivisionAxis.Custom => "手绘切分线",
            _ => "沿测线方向等分"
        };
        string instruction = ManualDrawingEnabled
            ? "左键拖绘新线；拖动已有线可平移；右键删除选中线"
            : "拖动切分线可微调位置；右键删除后可重新预览恢复等分";
        TextRenderer.DrawText(
            e.Graphics,
            $"{modeText}  |  切分线 {_cuts.Count} 条  |  预览子区 {_previewBlocks.Count} 个  |  {instruction}",
            Font,
            new Point(8, 8),
            SystemColors.ControlText,
            SystemColors.Window,
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);

        double metersPerPixel = GetMetersPerPixel();
        PreviewScaleBar.Draw(e.Graphics, this, metersPerPixel);
    }

    private static void DrawCut(
        Graphics graphics,
        Func<SurveyBoundaryPoint, PointF> screen,
        SurveySubdivisionCutLine cut,
        bool selected,
        int number)
    {
        using var pen = new Pen(
            selected ? SystemColors.Highlight : SystemColors.HotTrack,
            selected ? 3f : 2f)
        {
            DashStyle = DashStyle.Dash
        };
        PointF a = screen(cut.Start);
        PointF b = screen(cut.End);
        graphics.DrawLine(pen, a, b);
        PointF mid = new((a.X + b.X) / 2f, (a.Y + b.Y) / 2f);
        using var brush = new SolidBrush(
            selected ? SystemColors.Highlight : SystemColors.ControlDarkDark);
        graphics.FillEllipse(brush, mid.X - 4f, mid.Y - 4f, 8f, 8f);
        TextRenderer.DrawText(
            graphics,
            $"切{number}",
            SystemFonts.MessageBoxFont,
            new Point((int)mid.X + 6, (int)mid.Y + 4),
            SystemColors.ControlText,
            TextFormatFlags.NoPadding);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Middle || MotherPlan is null || !TryCreateTransform(out _, out Func<PointF, SurveyBoundaryPoint> world))
            return;

        if (e.Button == MouseButtons.Right)
        {
            int hit = HitTestCut(e.Location);
            if (hit >= 0)
            {
                _cuts.RemoveAt(hit);
                _selectedCutIndex = Math.Min(hit, _cuts.Count - 1);
                RebuildChildPreview();
                CutsChanged?.Invoke(GetCuts());
                Invalidate();
            }
            return;
        }

        if (e.Button != MouseButtons.Left)
            return;

        int selected = HitTestCut(e.Location);
        if (selected >= 0)
        {
            _selectedCutIndex = selected;
            _dragging = true;
            _lastDragWorld = world(e.Location);
            Capture = true;
            Invalidate();
            return;
        }

        if (!ManualDrawingEnabled)
            return;

        _selectedCutIndex = -1;
        _drawing = true;
        _draftStart = world(e.Location);
        _draftEnd = _draftStart;
        Capture = true;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_navigation.IsDragging || MotherPlan is null || !TryCreateTransform(out _, out Func<PointF, SurveyBoundaryPoint> world))
            return;

        SurveyBoundaryPoint current = world(e.Location);
        if (_drawing && _draftStart is not null)
        {
            _draftEnd = current;
            Invalidate();
            return;
        }

        if (_dragging && _selectedCutIndex >= 0 && _selectedCutIndex < _cuts.Count &&
            _lastDragWorld is not null)
        {
            double dx = current.X - _lastDragWorld.X;
            double dy = current.Y - _lastDragWorld.Y;
            SurveySubdivisionCutLine old = _cuts[_selectedCutIndex];
            _cuts[_selectedCutIndex] = new SurveySubdivisionCutLine(
                new SurveyBoundaryPoint(old.Start.X + dx, old.Start.Y + dy),
                new SurveyBoundaryPoint(old.End.X + dx, old.End.Y + dy));
            _lastDragWorld = current;
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left)
            return;

        if (_drawing)
        {
            _drawing = false;
            Capture = false;
            if (_draftStart is not null && _draftEnd is not null)
            {
                var cut = ExtendAcrossMother(
                    new SurveySubdivisionCutLine(_draftStart, _draftEnd));
                if (cut.Length > 1e-6)
                {
                    _cuts.Add(cut);
                    _selectedCutIndex = _cuts.Count - 1;
                    RebuildChildPreview();
                    CutsChanged?.Invoke(GetCuts());
                }
            }
            _draftStart = null;
            _draftEnd = null;
            Invalidate();
            return;
        }

        if (_dragging)
        {
            _dragging = false;
            _lastDragWorld = null;
            Capture = false;
            RebuildChildPreview();
            CutsChanged?.Invoke(GetCuts());
            Invalidate();
        }
    }

    private void RebuildChildPreview()
    {
        _previewBlocks.Clear();
        if (MotherPlan is null || _cuts.Count == 0)
            return;
        try
        {
            SurveyLineSubdivisionResult result = _operationPlan is null
                ? new SurveyLineSubdivisionPlanner().Subdivide(MotherPlan, _cuts, Axis, _overlapLengthM)
                : new SurveyLineSubdivisionPlanner().SubdivideByOperations(MotherPlan, _operationPlan, _overlapLengthM);
            _previewBlocks.AddRange(result.Subareas);
        }
        catch
        {
            // While a cut line is being edited it can temporarily miss the
            // polygon or create an invalid topology. Keep the edit visible;
            // execution will surface the precise validation error if needed.
        }
    }

    private SurveySubdivisionCutLine ExtendAcrossMother(
        SurveySubdivisionCutLine cut)
    {
        if (MotherPlan is null || cut.Length <= 1e-7)
            return cut;

        double minX = MotherPlan.Boundary.Min(point => point.X);
        double maxX = MotherPlan.Boundary.Max(point => point.X);
        double minY = MotherPlan.Boundary.Min(point => point.Y);
        double maxY = MotherPlan.Boundary.Max(point => point.Y);
        double diagonal = Math.Max(
            Math.Sqrt(Math.Pow(maxX - minX, 2) + Math.Pow(maxY - minY, 2)),
            1d);
        double reach = diagonal * 2d + 100d;
        double dx = cut.End.X - cut.Start.X;
        double dy = cut.End.Y - cut.Start.Y;
        double length = Math.Sqrt(dx * dx + dy * dy);
        double ux = dx / length;
        double uy = dy / length;
        var center = new SurveyBoundaryPoint(
            (cut.Start.X + cut.End.X) / 2d,
            (cut.Start.Y + cut.End.Y) / 2d);
        return new SurveySubdivisionCutLine(
            new SurveyBoundaryPoint(center.X - ux * reach, center.Y - uy * reach),
            new SurveyBoundaryPoint(center.X + ux * reach, center.Y + uy * reach));
    }

    private int HitTestCut(Point location)
    {
        if (MotherPlan is null || _cuts.Count == 0 ||
            !TryCreateTransform(out Func<SurveyBoundaryPoint, PointF> screen, out _))
            return -1;

        double best = 9.0;
        int bestIndex = -1;
        for (int i = 0; i < _cuts.Count; i++)
        {
            PointF a = screen(_cuts[i].Start);
            PointF b = screen(_cuts[i].End);
            double distance = DistancePointToSegment(location, a, b);
            if (distance < best)
            {
                best = distance;
                bestIndex = i;
            }
        }
        return bestIndex;
    }

    private bool TryCreateTransform(
        out Func<SurveyBoundaryPoint, PointF> screen,
        out Func<PointF, SurveyBoundaryPoint> world)
    {
        screen = static _ => PointF.Empty;
        world = static _ => new SurveyBoundaryPoint(0d, 0d);
        if (MotherPlan is null || MotherPlan.Boundary.Count == 0)
            return false;

        double minX = MotherPlan.Boundary.Min(point => point.X);
        double maxX = MotherPlan.Boundary.Max(point => point.X);
        double minY = MotherPlan.Boundary.Min(point => point.Y);
        double maxY = MotherPlan.Boundary.Max(point => point.Y);
        double spanX = Math.Max(maxX - minX, 1e-9);
        double spanY = Math.Max(maxY - minY, 1e-9);
        double plotWidth = Math.Max(1.0, Width - PlotLeft - PlotRight);
        double plotHeight = Math.Max(1.0, Height - PlotTop - PlotBottom);
        double sx = plotWidth / spanX;
        double sy = plotHeight / spanY;
        double scale = Math.Max(1e-12, Math.Min(sx, sy));

        screen = point => _navigation.Apply(new PointF(
            (float)(PlotLeft + (point.X - minX) * scale),
            (float)(Height - PlotBottom - (point.Y - minY) * scale)));
        world = point =>
        {
            PointF basePoint = _navigation.Unapply(point);
            return new SurveyBoundaryPoint(
                minX + (basePoint.X - PlotLeft) / scale,
                minY + (Height - PlotBottom - basePoint.Y) / scale);
        };
        return true;
    }

    private double GetMetersPerPixel()
    {
        if (MotherPlan is null || MotherPlan.Boundary.Count == 0)
            return double.NaN;

        double minX = MotherPlan.Boundary.Min(point => point.X);
        double maxX = MotherPlan.Boundary.Max(point => point.X);
        double minY = MotherPlan.Boundary.Min(point => point.Y);
        double maxY = MotherPlan.Boundary.Max(point => point.Y);
        double spanX = Math.Max(maxX - minX, 1e-9);
        double spanY = Math.Max(maxY - minY, 1e-9);
        double plotWidth = Math.Max(1.0, Width - PlotLeft - PlotRight);
        double plotHeight = Math.Max(1.0, Height - PlotTop - PlotBottom);
        double sx = plotWidth / spanX;
        double sy = plotHeight / spanY;
        double pixelsPerMeter = Math.Max(1e-12, Math.Min(sx, sy));
        return 1d / (pixelsPerMeter * _navigation.Zoom);
    }

    private static double DistancePointToSegment(Point p, PointF a, PointF b)
    {
        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        double denominator = dx * dx + dy * dy;
        if (denominator <= 1e-12)
            return Math.Sqrt(Math.Pow(p.X - a.X, 2) + Math.Pow(p.Y - a.Y, 2));
        double t = Math.Clamp(
            ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / denominator,
            0d,
            1d);
        double x = a.X + dx * t;
        double y = a.Y + dy * t;
        return Math.Sqrt(Math.Pow(p.X - x, 2) + Math.Pow(p.Y - y, 2));
    }
}
