using System.Drawing.Drawing2D;

namespace MagOps.Desktop;

internal sealed partial class TrackMapPanel
{
    private int _includedCount, _candidateCount;
    private readonly Dictionary<string, byte> _legStyles = new(StringComparer.OrdinalIgnoreCase);

    private void ConfigureLayerMenu()
    {
        var menu = ContextMenuStrip!;
        void Add(string text, bool initial, Action<bool> apply)
        {
            var item = new ToolStripMenuItem(text) { CheckOnClick = true, Checked = initial };
            item.CheckedChanged += (_, _) => { apply(item.Checked); _mapRevision++; Invalidate(); };
            menu.Items.Add(item);
        }
        menu.Items.Add(new ToolStripSeparator());
        Add("仅看作业候选", false, value => _displayState = _displayState with { OnlyCandidates = value });
        Add("显示原始轨迹背景", true, value => _displayState = _displayState with { ShowRawTrack = value });
        Add("显示连接 / 往返段", true, value => _displayState = _displayState with { ShowConnectors = value });
        Add("显示辅助轨迹", true, value => _displayState = _displayState with { ShowAuxiliary = value });
        Add("显示参考线端点文字", false, value => _displayState = _displayState with { ShowReferenceEndpoints = value });
    }

    internal void ShowLayers(Control anchor) => ContextMenuStrip!.Show(anchor, new Point(0, anchor.Height));

    private static byte LegStyle(MagOps.Core.Domain.FlightLeg leg) =>
        leg.RouteRole is "CONNECTOR" or "TRANSIT" || leg.TransitEvidence.Count > 0 ? (byte)6 :
        leg.ReviewReason == "AMBIGUOUS_LINE" ? (byte)2 :
        leg.ReviewReason.Contains("REVIEW") ? (byte)3 : leg.Classification == "SURVEY" ? (byte)4 : (byte)5;

    private bool VisibleStyle(byte style) => _displayState.VisibleStyle(style);

}
