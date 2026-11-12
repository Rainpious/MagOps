using MagOps.Core.Routes;

namespace MagOps.Desktop;

public sealed partial class MainForm
{
    private CancellationTokenSource? _siteJob;
    private AccessPlanningData? _siteBase;
    private IReadOnlyList<SortieAccessAssessment> _siteBefore = [];
    private string _siteImpact = "";
    private bool _siteSaving;
    private bool _siteComputing;

    private void WireSiteEditor()
    {
        if (!LaunchSitePlanning.Enabled) return;
        _routeWorkspace.SiteRetry += () => SiteAction(RefreshEditedSiteInputs);
        _routeWorkspace.FlightLimitEdited += () => SiteAction(RefreshEditedSiteInputs);
        _routeWorkspace.SiteToggle += () => SiteAction(ToggleSiteEditor);
        _routeWorkspace.SiteCancel += () => SiteAction(CancelSiteEdit);
        _routeWorkspace.SiteOmit += () => SiteAction(OmitSite);
        _routeWorkspace.SiteLimitChanged += QueueSiteSuggestions;
        _routeWorkspace.SiteMap.PlaceSite += (id,p) => SiteAction(() => PlaceDraftSite(id,p));
        _routeWorkspace.SiteMap.SiteSelectionChanged += UpdateSiteSelectionNotice;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape && _routeWorkspace.SiteMap.SiteDraft != null)
            { e.Handled = e.SuppressKeyPress = true; CancelSiteEdit(); } };
        Disposed += (_,_) => _siteJob?.Cancel();
    }
    private void SiteAction(Action action)
    {
        try { action(); }
        catch (Exception e) { SetStatus(e.Message); MessageBox.Show(this,e.Message,"候选起降点"); }
    }
    private void RefreshEditedSiteInputs()
    {
        if (!LaunchSitePlanning.Enabled) return;
        if (!ResolveSiteDraft("按当前条件重新推荐")) return;
        if (_routePlan is null || !_routeWorkspace.HasEditedFlightLimit) { QueueSiteSuggestions(); return; }
        if (!double.TryParse(_routeWorkspace.GetMaxSortieKmText(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double limit) || !double.IsFinite(limit) || limit <= 0)
        {
            _siteJob?.Cancel();
            _routeWorkspace.SiteMap.Suggestions = [];
            _routeWorkspace.SiteMap.Invalidate();
            _routeWorkspace.SiteStatus(false,"请填写大于 0 的有效航程上限，再重新推荐。");
            return;
        }
        _routeWorkspace.AcceptFlightLimit();
        if (_routePlan.Options.MaxSortieKm == limit) { QueueSiteSuggestions(); return; }
        _routePlan.Options.MaxSortieKm = limit;
        if (_routePartitionContexts.TryGetValue(_routePlan.PartitionCode, out var current) && ReferenceEquals(current.Plan,_routePlan))
        {
            current.MaxSortieKm = limit;
            current.Dirty = true;
            _routeWorkspace.UpdatePartitionOverviewEntry(current.ToOverviewEntry());
        }
        SetRouteWorkspaceDirty(true);
        RefreshRoutePlanUi(Math.Max(0, _routeWorkspace.GetSelectedSortieIndex()));
    }

    private RoutePlanResult[] SitePlans() => _routePartitionContexts.Count == 0
        ? (_routePlan is null ? [] : [_routePlan])
        : _routePartitionContexts.Values.OrderBy(c => c.Index).Select(c => c.Plan)
            .OfType<RoutePlanResult>().Distinct().ToArray();

    private async void QueueSiteSuggestions()
    {
        if (!LaunchSitePlanning.Enabled) return;
        var map = _routeWorkspace.SiteMap;
        if (map.SiteDraft != null || _siteSaving || IsDisposed || Disposing) return;
        _siteJob?.Cancel(); _siteJob = null; _siteComputing = false;
        map.Suggestions = []; map.Invalidate();
        var plans = SitePlans();
        if (plans.Length == 0) return;
        var data = (_routePlan ?? plans[0]).AccessPlanning ?? LoadAccessPlanning();
        if (!ReferenceEquals(_siteBase,data) && (_siteBase is null || _siteBase.MaxSuggestedSites != data.MaxSuggestedSites))
            _routeWorkspace.SetSuggestionLimit(data.MaxSuggestedSites);
        _siteBase = data;
        int total = plans.Sum(p => p.Sorties.Count);
        int pending = _routePartitionContexts.Values.Count(c => c.Plan is null);
        string currentLimit = _routePlan is null ? "" : $"，当前上限 {_routePlan.Options.MaxSortieKm:0.###} km";
        string scope = $"推荐用轨迹 {data.Roads.Count(r => r.UseForRecommendations)} 条{currentLimit}；已纳入 {total} 架次，待生成子区 {pending}";
        if (!data.Roads.Any(r => r.UseForRecommendations) || _routeWorkspace.SuggestionLimit == 0)
        {
            _routeWorkspace.SiteStatus(false, scope + "；可手工选点或选择推荐用轨迹。"); return;
        }
        var job = new CancellationTokenSource(); _siteJob = job; _siteComputing = true;
        try
        {
            var tasks = new List<RouteSiteSuggestions.Task>();
            var protectLegacy = new List<RouteSiteSuggestions.Task>();
            int ok = 0, workOverLimit = 0, invalidLocks = 0;
            foreach (var plan in plans)
            foreach (var row in RouteAccessPlanner.Assess(plan,data))
            {
                if (row.Status == "OK") ok++;
                double budget = plan.Options.MaxSortieKm*1000 - row.WorkM;
                if (budget < 0) workOverLimit++;
                if (row.Status == "LOCK_UNAVAILABLE") invalidLocks++;
                if (row.Locked || budget < 0 || row.First is null || row.Last is null) continue;
                var task = new RouteSiteSuggestions.Task(row.First,row.Last,budget);
                if (row.Status != "OK") tasks.Add(task);
                else if (row.SiteId == "legacy") protectLegacy.Add(task);
            }
            _routeWorkspace.SiteStatus(false,"正在沿踏勘轨迹计算候选点…");
            var settings = data with { MaxSuggestedSites = _routeWorkspace.SuggestionLimit };
            var suggestions = await System.Threading.Tasks.Task.Run(() => RouteSiteSuggestions.Recommend(tasks,settings,job.Token,protectLegacy),job.Token);
            if (job.IsCancellationRequested || IsDisposed || Disposing || !ReferenceEquals(_siteJob,job)) return;
            map.Suggestions = suggestions;
            int predicted = plans.Sum(p => RouteAccessPlanner.Assess(p,data with { Sites = [..data.Sites,..suggestions] }).Count(a => a.Status == "OK"));
            _routeWorkspace.SiteStatus(false,$"{scope}；建议 {suggestions.Count} 点，达标 {ok} → {predicted}，其余 {total-predicted}（作业段已超限 {workOverLimit}，失效锁定 {invalidLocks}）。");
            map.Invalidate();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!job.IsCancellationRequested && !IsDisposed) _routeWorkspace.SiteStatus(false,"推荐未完成："+ex.Message); }
        finally
        {
            if (ReferenceEquals(_siteJob,job)) { _siteJob = null; _siteComputing = false; }
            job.Dispose();
        }
    }
    private void ResetSiteEditor()
    {
        _siteJob?.Cancel(); _siteJob = null; _siteComputing = false; _siteBase = null;
        var map = _routeWorkspace.SiteMap;
        map.SiteDraft = null; map.Suggestions = []; map.DraftRows = null; map.DraftNewIds.Clear(); map.ClearSiteGesture();
        _routeWorkspace.SiteStatus(false,""); map.Invalidate();
    }
    private void ToggleSiteEditor()
    {
        if (!LaunchSitePlanning.Enabled) return;
        var map = _routeWorkspace.SiteMap;
        if (map.SiteDraft is { } draft)
        {
            _siteSaving = true;
            try
            {
                // Drop the overlay only after the business write succeeds.
                SaveAccessPlanning(draft);
                ResetSiteEditor();
                RefreshAccessPlanning();
            }
            finally { _siteSaving = false; }
            RefreshRoutePlanUi(Math.Max(0,_routeWorkspace.GetSelectedSortieIndex()));
            return;
        }
        if (_routePlan is null) { SetStatus("请先生成航线；已有分区时请打开要查看的子区。"); return; }
        if (_siteComputing || map.DrawingCut) { SetStatus(_siteComputing ? "请等待建议点计算完成。" : "请先完成切割线绘制。"); return; }
        _siteBase = _routePlan.AccessPlanning ?? LoadAccessPlanning();
        _siteBefore = RouteAccessPlanner.Assess(_routePlan,_siteBase);
        map.DraftNewIds = map.Suggestions.Select(s => s.Id).ToHashSet();
        map.SiteDraft = _siteBase with { Sites = [.._siteBase.Sites,..map.Suggestions], MaxSuggestedSites = _routeWorkspace.SuggestionLimit };
        UpdateSiteDraft();
    }
    private void PlaceDraftSite(string? id, AccessPoint p)
    {
        var map = _routeWorkspace.SiteMap;
        if (map.SiteDraft is not { } data) return;
        var site = data.Sites.FirstOrDefault(s => s.Id == id);
        if (site is null)
        {
            int n = data.Sites.Count+1;
            while (data.Sites.Any(s => s.Name == $"起降点{n}")) n++;
            site = new LaunchSite(Guid.NewGuid().ToString("N"),$"起降点{n}",p.Longitude,p.Latitude);
            map.DraftNewIds.Add(site.Id);
        }
        var snap = data.SnapEnabled ? RouteAccessPlanner.Snap(p,data.Roads,data.SnapToleranceM) : null;
        p = snap?.Point ?? p;
        var updated = site with { Longitude=p.Longitude, Latitude=p.Latitude, RoadId=snap?.RoadId, SegmentIndex=snap?.SegmentIndex,
            EdgeIndex=snap?.EdgeIndex, EdgeFraction=snap?.Fraction, SnapDistanceM=snap?.DistanceM };
        var sites = data.Sites.ToList();
        int index = sites.FindIndex(s=>s.Id==site.Id);
        if (index<0) sites.Add(updated); else sites[index]=updated;
        map.SiteDraft=data with { Sites=sites }; map.SelectedSite=site.Id;
        UpdateSiteDraft();
    }
    private void OmitSite()
    {
        var map = _routeWorkspace.SiteMap;
        if (map.SiteDraft is not {} d || map.SelectedSite is not {} id || !map.DraftNewIds.Remove(id))
        { SetStatus("请先选中本次新增的点。"); return; }
        map.SiteDraft = d with { Sites = d.Sites.Where(s=>s.Id!=id).ToList() }; map.SelectedSite=null; UpdateSiteDraft();
    }
    private void UpdateSiteDraft()
    {
        var map = _routeWorkspace.SiteMap;
        if (_routePlan is null || map.SiteDraft is not {} data) return;
        map.DraftRows = RouteAccessPlanner.Assess(_routePlan,data);
        int affected=0, locked=0, ok=0, total=0;
        foreach (var p in SitePlans())
        {
            var before=RouteAccessPlanner.Assess(p,_siteBase);
            var after=ReferenceEquals(p,_routePlan)?map.DraftRows:RouteAccessPlanner.Assess(p,data);
            for(int i=0;i<after.Count;i++)
            {
                total++; if(after[i].Status=="OK") ok++;
                if(before[i].SiteId!=after[i].SiteId || before[i].Site!=after[i].Site)
                { affected++; if(after[i].Locked) locked++; }
            }
        }
        _siteImpact=$"全区达标 {ok}/{total}，影响 {affected} 架次（锁定 {locked}）";
        RenderRoutePlanUi(Math.Max(0,_routeWorkspace.GetSelectedSortieIndex()),true);
        UpdateSiteSelectionNotice(); map.Invalidate();
    }
    private void UpdateSiteSelectionNotice()
    {
        var map = _routeWorkspace.SiteMap;
        if (map.SiteDraft is null || map.DraftRows is not {} rows) return;
        int i=Math.Max(0,_routeWorkspace.GetSelectedSortieIndex());
        string Km(double? m) => m.HasValue ? $"{m/1000:0.###}" : "—";
        string current=i<rows.Count && i<_siteBefore.Count ? $"{rows[i].SortieName} · {rows[i].SiteName}　往返 {Km(_siteBefore[i].OutboundM+_siteBefore[i].ReturnM)} → {Km(rows[i].OutboundM+rows[i].ReturnM)} km，总长 {Km(rows[i].TotalM)} / {_routePlan!.Options.MaxSortieKm:0.###} km" : "";
        _routeWorkspace.SetAccessSummary("草稿试算　"+_siteImpact+"　"+current);
        _routeWorkspace.SiteStatus(true, "点空白处新增 · 拖动移点 · Esc 取消　"+current);
    }
    private void CancelSiteEdit()
    {
        if (_siteBase is not null) _routeWorkspace.SetSuggestionLimit(_siteBase.MaxSuggestedSites);
        ResetSiteEditor();
        _siteSaving = true;
        try { if (_routePlan is not null) RefreshRoutePlanUi(Math.Max(0,_routeWorkspace.GetSelectedSortieIndex())); }
        finally { _siteSaving = false; }
        _routeWorkspace.SiteMap.Invalidate();
        _routeWorkspace.SiteStatus(false,"已恢复已保存点位。可重新推荐。");
    }
    private bool ResolveSiteDraft(string action)
    {
        if (!LaunchSitePlanning.Enabled) return true;
        if (_siteSaving || _routeWorkspace?.SiteMap.SiteDraft is not { } draft) return true;
        if (_siteBase is not null && draft.Sites.SequenceEqual(_siteBase.Sites) && draft.MaxSuggestedSites == _siteBase.MaxSuggestedSites)
        { CancelSiteEdit(); return true; }
        var decision=MessageBox.Show(this,$"即将{action}。是否保存本轮起降点修改？","候选起降点",
            MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if(decision==DialogResult.Cancel) return false;
        if(decision==DialogResult.No) { CancelSiteEdit(); return true; }
        try { ToggleSiteEditor(); return true; }
        catch(Exception ex) { MessageBox.Show(this,ex.Message,"保存失败"); return false; }
    }
}
