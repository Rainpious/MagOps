using MagOps.Core.Domain;
using MagOps.Infrastructure.Services;

namespace MagOps.Desktop;

internal sealed partial class AutomaticSegmentationReviewDialog
{
    private DemHeightSnapshot? _demHeightReference;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool SkipInitialRecognition { get; set; }
    private TabPage? _demHeightPage;
    private Dictionary<(string Source, int Record, DateTimeOffset Time), DemHeightPoint>? _demHeightLookup;
    internal void ConfigureDemHeight(DemHeightOptions options, DemHeightSnapshot? snapshot, string outputDirectory,
        Action<DemHeightOptions, DemHeightSnapshot> completed, Action<DemHeightOptions> changed, Action<string> exported, Action downloadTerrain)
    {
        if (_spatialOnly || _demHeightPage is not null) return;
        var workspace = new DemHeightWorkspaceControl(_analysis, options, snapshot, downloadTerrain) { OutputDirectory = outputDirectory };
        _demHeightPage = new TabPage("离地高度") { Name = "DemHeightPage" };
        _demHeightPage.Controls.Add(workspace);
        UiTheme.Apply(workspace);
        _reviewWorkspaces!.TabPages.Add(_demHeightPage);
        workspace.CalculationCompleted += (settings, result) => { SetDemHeightReference(result); completed(settings, result); };
        workspace.OptionsChanged += settings => { SetDemHeightReference(null); changed(settings); };
        workspace.ExportCompleted += exported;
        _reviewWorkspaces.SelectedIndexChanged += async (_, _) => await workspace.VerifyCurrentAsync();
        SetDemHeightReference(snapshot);
    }
    internal void ShowDemHeightPage()
    { if (_demHeightPage is not null) _reviewWorkspaces!.SelectedTab = _demHeightPage; }
    private void SetDemHeightReference(DemHeightSnapshot? snapshot)
    {
        _demHeightReference = snapshot?.SourceFingerprint == _analysis.Fingerprint ? snapshot : null;
        _demHeightLookup = null;
        RefreshDemHeightLegDetails();
    }
    private void RefreshDemHeightLegDetails()
    {
        const string marker = "\r\nDEM 离地高度参考：";
        int previous = _legDetails.Text.IndexOf(marker, StringComparison.Ordinal);
        if (previous >= 0) _legDetails.Text = _legDetails.Text[..previous];
        if (_legGrid.CurrentRow?.Tag is not ReviewLegRow row) return;
        if (_demHeightReference is null)
        { _legDetails.AppendText(marker + "尚未计算或设置已变化；可在“离地高度”页计算，不影响当前空间识别。"); return; }
        if (_demHeightLookup is null)
        {
            _demHeightLookup = new();
            foreach (var p in _demHeightReference.Points)
                _demHeightLookup.TryAdd((p.Source.SourceFile, p.Source.SourceRecord, p.Source.TimestampUtc), p);
        }
        var points = row.Leg.Samples.Select(s => _demHeightLookup.GetValueOrDefault((s.SourceFile, s.SourceRecord, s.TimestampUtc)))
            .Where(p => p is not null).Cast<DemHeightPoint>().ToArray();
        var summary = DemHeightService.Summarize(points);
        string F(double? h) => h?.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) ?? "—";
        _legDetails.AppendText(marker + $"{summary.ValidCount}/{row.Leg.Samples.Count} 点可计算；均值 {F(summary.MeanM)} m，" +
            $"范围 {F(summary.MinM)}–{F(summary.MaxM)} m，负值 {summary.NegativeCount} 点；仅作参考，未参与自动归属或排除。");
    }
}
