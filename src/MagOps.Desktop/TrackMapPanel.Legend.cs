namespace MagOps.Desktop;

internal sealed partial class TrackMapPanel
{
    private float _legendHeight = 76;
    internal bool ShowInlineLegend = true;
    private void DrawLegend(Graphics graphics)
    {
        if (!ShowInlineLegend) { _legendHeight = 0; return; }
        _legendHeight = TrackMapRenderer.DrawLegend(graphics,
        _displayState, Size, Font, DeviceDpi, _includedCount, _candidateCount, _selectedLegs.Count,
        _execution is not null, _tasks.Any(t => t.GeometryOrigin != "ACTUAL_TRACK"), _highlightRoles.Count > 0);
    }
}
