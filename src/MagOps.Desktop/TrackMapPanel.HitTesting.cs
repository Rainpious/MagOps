namespace MagOps.Desktop;

internal sealed partial class TrackMapPanel
{
    internal IReadOnlyList<string> HitTestLegs(Point point)
    {
        if (!_geometry.HasGeometry || _execution is null || point.Y < _legendHeight) return [];
        return TrackMapHitQuery.Find(_geometry, CurrentViewTransform(), point, 7 * DeviceDpi / 96d,
            _edges, id => VisibleStyle(_acceptedLegIds.Contains(id) ? (byte)1 :
                _legStyles.GetValueOrDefault(id, (byte)5)));
    }

}
