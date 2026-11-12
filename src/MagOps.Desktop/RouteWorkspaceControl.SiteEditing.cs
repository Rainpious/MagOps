using MagOps.Core.Routes;
namespace MagOps.Desktop;

internal sealed partial class RouteWorkspaceControl
{
    internal RoutePreviewPanel SiteMap => _routePreview;
    private readonly Button _editSites = new() { Text = "编辑起降点", AutoSize = true };
    private readonly Button _discardSites = new() { Text = "取消", AutoSize = true, Visible = false };
    private readonly Button _omitSite = new() { Text = "排除选中建议", AutoSize = true, Visible = false };
    private readonly CheckBox _capSiteCount = new() { Text = "限制新增点数", AutoSize = true };
    private readonly NumericUpDown _siteLimit = new() { Minimum = 0, Maximum = 99, Value = 3, Width = 54, Enabled = false };
    private readonly Label _siteNotice = new() { AutoSize = true, Padding = new Padding(4) };
    internal event Action? SiteToggle, SiteCancel, SiteOmit, SiteRetry, SiteLimitChanged;
    internal bool HasEditedFlightLimit => _routeMaxSortieKm.Modified;
    internal void AcceptFlightLimit() => _routeMaxSortieKm.Modified = false;
    internal event Action? FlightLimitEdited;
    internal int SuggestionLimit => _capSiteCount.Checked ? (int)_siteLimit.Value : AccessPlanningData.AutomaticSiteCount;
    private bool _settingLimit;
    internal void SetSuggestionLimit(int n)
    {
        _settingLimit = true;
        try
        {
            _capSiteCount.Checked = n != AccessPlanningData.AutomaticSiteCount;
            if (_capSiteCount.Checked) _siteLimit.Value = Math.Clamp(n, 0, 99);
            _siteLimit.Enabled = _capSiteCount.Enabled && _capSiteCount.Checked;
        }
        finally { _settingLimit = false; }
    }
    private Control BuildSiteEditorBar()
    {
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true };
        var retry = new Button { Text = "重新推荐", AutoSize = true };
        _routeMaxSortieKm.Validated += (_,_) => { if (HasEditedFlightLimit) FlightLimitEdited?.Invoke(); };
        retry.Click += (_,_) => SiteRetry?.Invoke();
        bar.Controls.AddRange([_editSites, _discardSites, _omitSite,
            _capSiteCount, _siteLimit, retry, _siteNotice]);
        bar.ClientSizeChanged += (_,_) => _siteNotice.MaximumSize = new Size(Math.Max(120,bar.ClientSize.Width-12),0);
        _editSites.Click += (_,_) => SiteToggle?.Invoke();
        _discardSites.Click += (_,_) => SiteCancel?.Invoke();
        _omitSite.Click += (_,_) => SiteOmit?.Invoke();
        _capSiteCount.CheckedChanged += (_,_) =>
        {
            _siteLimit.Enabled = _capSiteCount.Enabled && _capSiteCount.Checked;
            if (!_settingLimit) SiteLimitChanged?.Invoke();
        };
        _siteLimit.ValueChanged += (_,_) => { if (!_settingLimit) SiteLimitChanged?.Invoke(); };
        return bar;
    }
    internal void SiteStatus(bool editing, string message)
    {
        _editSites.Text = editing ? "完成并保存" : "编辑起降点";
        _editSites.BackColor = editing ? Color.LightSkyBlue : SystemColors.Control;
        _discardSites.Visible = _omitSite.Visible = editing;
        _capSiteCount.Enabled = !editing;
        _siteLimit.Enabled = !editing && _capSiteCount.Checked;
        _siteNotice.Text = message;
    }
}
