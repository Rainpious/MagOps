using System.Drawing.Drawing2D;
using MagOps.Core.Domain;
using MagOps.Infrastructure.Services;

namespace MagOps.Desktop;

internal sealed class TrackTimelinePanel : Panel
{
    private SurveyExecutionResult? _execution;
    private FlightLeg? _selected;
    private TimeZoneInfo _timeZone = ProjectTime.Beijing;
    private readonly PreviewNavigation _navigation;
    private double _startSeconds;
    private double _endSeconds = 1;
    private double _displayStartSeconds;
    private double _displayEndSeconds = 1;

    public TrackTimelinePanel()
    {
        SetStyle(ControlStyles.Selectable, true);
        DoubleBuffered = true;
        BackColor = Color.White;
        _navigation = new PreviewNavigation(this, horizontalOnly: true);
        AccessibleName = "航段时间轴";
    }
    public void SetExecution(SurveyExecutionResult? execution, FlightLeg? selected, TimeZoneInfo? timeZone = null)
    {
        _execution = execution;
        _selected = selected;
        _timeZone = timeZone ?? ProjectTime.Beijing;
        if (execution is { Legs.Count: > 0 })
        {
            DateTimeOffset start = execution.Legs.Min(x => x.StartUtc);
            DateTimeOffset end = execution.Legs.Max(x => x.EndUtc);
            _startSeconds = start.UtcDateTime.Ticks / (double)TimeSpan.TicksPerSecond;
            _endSeconds = Math.Max(_startSeconds + 1d, end.UtcDateTime.Ticks / (double)TimeSpan.TicksPerSecond);
            _navigation.SetDataKey(new { execution.AlgorithmVersion, Start = _startSeconds, End = _endSeconds,
                Legs = execution.Legs.Select(x => x.FlightLegId).ToArray() });
        }
        else
        {
            _startSeconds = 0; _endSeconds = 1;
            _navigation.SetDataKey(null);
        }
        _displayStartSeconds = _startSeconds;
        _displayEndSeconds = _endSeconds;
        Invalidate();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var focusCue = _navigation.BeginPaint(e.Graphics);
        if (_execution is null || _execution.Legs.Count == 0) return;
        DateTimeOffset start = _execution.Legs.Min(x => x.StartUtc), end = _execution.Legs.Max(x => x.EndUtc);
        double fullStart = start.UtcDateTime.Ticks / (double)TimeSpan.TicksPerSecond;
        double fullEnd = Math.Max(fullStart + 1d, end.UtcDateTime.Ticks / (double)TimeSpan.TicksPerSecond);
        float left = 12, width = Math.Max(1, ClientSize.Width - 24), y = ClientSize.Height / 2f - 10;
        PointF Map(double seconds) => _navigation.Apply(new(
            (float)(left + (seconds - fullStart) / (fullEnd - fullStart) * width), 0));
        var visibleStart = _navigation.Unapply(new(left, 0));
        var visibleEnd = _navigation.Unapply(new(left + width, 0));
        _displayStartSeconds = fullStart + (visibleStart.X - left) / width * (fullEnd - fullStart);
        _displayEndSeconds = fullStart + (visibleEnd.X - left) / width * (fullEnd - fullStart);
        if (_displayEndSeconds < _displayStartSeconds) (_displayStartSeconds, _displayEndSeconds) = (_displayEndSeconds, _displayStartSeconds);
        GraphicsState clip = e.Graphics.Save();
        e.Graphics.SetClip(new RectangleF(left, 0, width, ClientSize.Height));
        foreach (FlightLeg leg in _execution.Legs)
        {
            float x1 = Map(leg.StartUtc.UtcDateTime.Ticks / (double)TimeSpan.TicksPerSecond).X;
            float x2 = Map(leg.EndUtc.UtcDateTime.Ticks / (double)TimeSpan.TicksPerSecond).X;
            float x = Math.Min(x1, x2);
            float w = Math.Max(3, Math.Abs(x2 - x1));
            Color color = leg.Classification == "SURVEY" ? Color.SeaGreen :
                leg.Classification == "UNRESOLVED" ? Color.IndianRed : Color.DarkGray;
            using var brush = new SolidBrush(color);
            e.Graphics.FillRectangle(brush, x, y, w, 20);
            if (ReferenceEquals(leg, _selected) || leg.FlightLegId == _selected?.FlightLegId)
                e.Graphics.DrawRectangle(Pens.Black, x, y, w, 20);
        }
        e.Graphics.Restore(clip);
        DateTimeOffset At(double seconds) => new((long)Math.Clamp(seconds * TimeSpan.TicksPerSecond,
            DateTime.MinValue.Ticks, DateTime.MaxValue.Ticks), TimeSpan.Zero);
        var displayStart = At(_displayStartSeconds);
        var displayEnd = At(_displayEndSeconds);
        e.Graphics.DrawString($"{ProjectTime.Display(displayStart, _timeZone):MM-dd HH:mm:ss}", Font, Brushes.Black, left, 5);
        e.Graphics.DrawString($"{ProjectTime.Display(displayEnd, _timeZone):MM-dd HH:mm:ss}", Font, Brushes.Black, Math.Max(left, left + width - 135), 5);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _navigation.Dispose();
        base.Dispose(disposing);
    }
}
