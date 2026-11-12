namespace MagOps.Desktop;

/// <summary>Display filters only; none of these values changes production decisions.</summary>
internal readonly record struct TrackMapDisplayState(
    bool ShowDesignLines = true, bool ShowAuxiliary = true,
    bool OnlyCandidates = false, bool ShowReferenceEndpoints = false,
    bool ShowRawTrack = true, bool ShowConnectors = true)
{
    internal bool VisibleStyle(byte style) => OnlyCandidates ? style == 4 : style == 6 ? ShowConnectors :
        style is 1 or 4 || ShowAuxiliary;
}
