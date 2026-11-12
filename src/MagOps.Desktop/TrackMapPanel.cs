using MagOps.Core.Domain;
using MagOps.Infrastructure.Services;

namespace MagOps.Desktop;

internal sealed partial class TrackMapPanel : Panel
{
    private IReadOnlyList<PlannedSurveyLineTask> _tasks = [];
    private SurveyExecutionResult? _execution;
    private FieldMagFolderAnalysis? _analysis;
    private IReadOnlyList<FlightLeg> _selectedLegs = [];
    private string? _selectedTaskId;
    private string? _inspectedLegId;
    private IReadOnlyDictionary<string, string> _highlightRoles = new Dictionary<string, string>();
    private readonly ToolTip _identityTip = new();
    private TrackMapGeometrySnapshot _geometry = TrackMapGeometrySnapshot.Empty;
    private TrackMapEdgeIndex _edges = TrackMapEdgeIndex.Empty;
    private byte[][] _segmentStyles = [];
    private readonly Dictionary<FlightLeg, (double X, double Y)[]> _selectedGeometry = new(ReferenceEqualityComparer.Instance);
    private readonly TrackMapImageCache _imageCache = new();
    internal Size DetailImageSize => _imageCache.Detail?.Size ?? Size.Empty;
    internal long CachedPixelBytes => _imageCache.PixelBytes;
    internal int OverviewImageBuilds { get; private set; }
    private int _mapRevision;
    internal int GeometryBuilds { get; private set; }
    internal int BaseImageBuilds { get; private set; }
    internal int InteractionImageReuses { get; private set; }
    private readonly Dictionary<(string, int), string> _states = [];
    private IReadOnlySet<string> _acceptedLegIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string, int), string> _sampleLegIds = [];
    private readonly Dictionary<string, IReadOnlyList<CoverageInterval>> _coverage = [];
    private TrackMapDisplayState _displayState = new(true, true, false, false);
    private readonly PreviewNavigation _navigation;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool ShowDesignLines { get => _displayState.ShowDesignLines; set { if (_displayState.ShowDesignLines == value) return; _displayState = _displayState with { ShowDesignLines = value }; _mapRevision++; Invalidate(); } }
    public TrackMapPanel() { SetStyle(ControlStyles.Selectable, true); DoubleBuffered = true; BackColor = Color.White; _navigation = new PreviewNavigation(this, allowPrimaryDrag: true); ConfigureLayerMenu(); Disposed += (_, _) => _identityTip.Dispose(); }

    internal void InspectLeg(string? id) { _inspectedLegId = id; Invalidate(); }
    public event Action<IReadOnlyList<string>, Point>? LegsClicked;
    private Point? _clickStart;
    private bool _clickMoved;

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left) { _clickStart = e.Location; _clickMoved = false; }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_clickStart is { } start && (Math.Abs(e.X - start.X) > 4 || Math.Abs(e.Y - start.Y) > 4)) _clickMoved = true;
        if (_clickStart is null && _highlightRoles.Count > 0)
        {
            var hits = HitTestLegs(e.Location);
            var text = string.Join("\n", hits.Take(4).Select(id =>
            {
                var leg = _execution?.Legs.FirstOrDefault(l => l.FlightLegId == id);
                var task = _tasks.FirstOrDefault(t => t.SurveyTaskStableId == leg?.SurveyTaskStableId);
                return $"{_highlightRoles.GetValueOrDefault(id, "航段")} · {task?.LineName ?? "未归属"} · {id} · {(_acceptedLegIds.Contains(id) ? "已纳入" : "未纳入")}";
            }));
            if (_identityTip.GetToolTip(this) != text) _identityTip.SetToolTip(this, text);
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        var start = _clickStart;
        bool click = e.Button == MouseButtons.Left && start.HasValue && !_clickMoved &&
            Math.Abs(e.X - start.Value.X) <= 4 && Math.Abs(e.Y - start.Value.Y) <= 4;
        _clickStart = null;
        base.OnMouseUp(e);
        if (click)
        {
            var hits = HitTestLegs(e.Location);
            if (hits.Count > 0) LegsClicked?.Invoke(hits, e.Location);
        }
    }

    public void SetData(FieldMagFolderAnalysis analysis, IReadOnlyList<PlannedSurveyLineTask> tasks,
        SurveyExecutionResult? execution, FlightLeg? selected, string? selectedTaskId = null,
        IReadOnlyList<FlightLeg>? selectedLegs = null, IReadOnlySet<string>? acceptedLegIds = null,
        bool preserveView = false, IReadOnlyDictionary<string, string>? highlightRoles = null)
    {
        _inspectedLegId = null;
        _highlightRoles = highlightRoles ?? new Dictionary<string, string>();
        bool dataChanged = !ReferenceEquals(analysis, _analysis);
        bool geometryChanged = dataChanged || !ReferenceEquals(tasks, _tasks);
        if (geometryChanged)
        {
            if (!preserveView) _navigation.SetDataKey(new { analysis.Fingerprint, Tasks = tasks.Select(t => new { t.SurveyTaskStableId, t.Start, t.End }).ToArray() });
            _geometry = TrackMapGeometrySnapshot.Build(analysis, tasks, dataChanged ? null : _geometry);
            GeometryBuilds++; _mapRevision++;
            _selectedGeometry.Clear();
        }
        bool executionChanged = !ReferenceEquals(execution, _execution) || dataChanged;
        if (executionChanged)
        {
            _selectedGeometry.Clear();
            _mapRevision++;
            _states.Clear();
            _sampleLegIds.Clear();
            _coverage.Clear();
            _legStyles.Clear();
            if (execution is not null)
            {
                foreach (var group in execution.Passes.GroupBy(p => p.SurveyTaskStableId))
                    _coverage[group.Key] = SurveyCoverage.Union(group.SelectMany(p => p.Coverage));
                foreach (var leg in execution.Legs)
                {
                    _legStyles[leg.FlightLegId] = LegStyle(leg);
                    foreach (var sample in leg.Samples)
                    {
                        _sampleLegIds[(sample.SourceFile, sample.SourceRecord)] = leg.FlightLegId;
                        _states[(sample.SourceFile, sample.SourceRecord)] = leg.ReviewReason == "AMBIGUOUS_LINE" ? "CONFLICT" :
                            leg.ReviewReason.Contains("REVIEW") ? "REVIEW" : leg.Classification;
                    }
                }
            }
        }
        _analysis = analysis; _tasks = tasks; _execution = execution;
        if (geometryChanged || executionChanged) _edges = TrackMapEdgeIndex.Build(_geometry, _sampleLegIds);
        var accepted = acceptedLegIds ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool acceptanceChanged = !_acceptedLegIds.SetEquals(accepted);
        if (acceptanceChanged) { _acceptedLegIds = accepted.ToHashSet(StringComparer.OrdinalIgnoreCase); _mapRevision++; }
        if (geometryChanged || executionChanged || acceptanceChanged)
        {
            _includedCount = execution?.Legs.Count(l => _acceptedLegIds.Contains(l.FlightLegId)) ?? 0;
            _candidateCount = execution?.Legs.Count(l => !_acceptedLegIds.Contains(l.FlightLegId) && LegStyle(l) == 4) ?? 0;
            _segmentStyles = Enumerable.Range(0, _geometry.StreamCount).Select(streamIndex =>
            {
                var stream = _geometry.Samples(streamIndex);
                var styles = new byte[stream.Length];
                for (int i = 1; i < stream.Length; i++)
                {
                    var a = (stream[i - 1].SourceFile, stream[i - 1].SourceRecord);
                    var b = (stream[i].SourceFile, stream[i].SourceRecord);
                    if (!_edges.IsContinuous(streamIndex, i)) continue;
                    string before = _states.GetValueOrDefault(a, ""), after = _states.GetValueOrDefault(b, "");
                    styles[i] = _acceptedLegIds.Contains(_sampleLegIds.GetValueOrDefault(a, "")) &&
                        _acceptedLegIds.Contains(_sampleLegIds.GetValueOrDefault(b, "")) ? (byte)1 :
                        (_legStyles.GetValueOrDefault(_sampleLegIds.GetValueOrDefault(a, "")) == 6 || _legStyles.GetValueOrDefault(_sampleLegIds.GetValueOrDefault(b, "")) == 6) ? (byte)6 :
                        before == "CONFLICT" || after == "CONFLICT" ? (byte)2 :
                        before == "REVIEW" || after == "REVIEW" ? (byte)3 :
                        before == "SURVEY" && after == "SURVEY" ? (byte)4 : (byte)5;
                }
                return styles;
            }).ToArray();
        }
        _selectedLegs = selectedLegs ?? (selected is null ? [] : [selected]);
        _selectedTaskId = selectedTaskId; Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _imageCache.Dispose();
        base.Dispose(disposing);
    }

    private TrackMapViewTransform CurrentViewTransform() => TrackMapViewTransform.Create(
        ClientSize, _geometry.MinX, _geometry.MaxX, _geometry.MinY, _geometry.MaxY,
        _navigation.Zoom, _navigation.Apply(PointF.Empty));

}

