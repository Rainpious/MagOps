namespace MagOps.Desktop;

internal static class TrackMapPalette
{
    // Paul Tol bright categorical colours, with neutral reference geometry.
    internal static readonly Color IncludedColor = Color.FromArgb(68, 119, 170);
    internal static readonly Color CandidateColor = Color.FromArgb(34, 136, 51);
    internal static readonly Color ReviewColor = Color.FromArgb(170, 51, 119);
    internal static readonly Color SelectedColor = Color.FromArgb(238, 102, 119);
    internal static readonly Color MissingColor = Color.FromArgb(204, 187, 68);
    internal static readonly Color UnassignedColor = Color.FromArgb(153, 153, 153);
    internal static readonly Color ReferenceColor = Color.FromArgb(221, 221, 221);
    internal const float IncludedWidth = 3f, CandidateWidth = 2f, AuxiliaryWidth = .8f;
}
