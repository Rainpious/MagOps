using MagOps.Core.Coordinates;
using MagOps.Core.Routes;
using MagOps.Infrastructure.Services;
namespace MagOps.Desktop;

public sealed partial class MainForm
{
    private readonly AccessPlanningService _accessService = new();
    private AccessPlanningData _temporaryAccess = new();
    private AccessPlanningData LoadAccessPlanning()
        => IsProjectInitialized() && !string.IsNullOrWhiteSpace(CurrentWorkareaRelativePath)
            ? _accessService.Load(Root, CurrentWorkareaRelativePath) : IsProjectInitialized() ? new() : _temporaryAccess;
    private void SaveAccessPlanning(AccessPlanningData data)
    {
        if (IsProjectInitialized())
        {
            if (string.IsNullOrWhiteSpace(CurrentWorkareaRelativePath)) throw new InvalidOperationException("请选择工区后保存踏勘轨迹。");
            _accessService.Save(Root, CurrentWorkareaRelativePath, data);
        }
        else _temporaryAccess = data;
        foreach (var context in _routePartitionContexts.Values)
            if (context.Plan is not null) { context.Plan.AccessPlanning = LaunchSitePlanning.Enabled ? data : (context.Plan.AccessPlanning ?? data) with { Roads = data.Roads }; context.Dirty = true; }
        if (_routePlan is not null) { _routePlan.AccessPlanning = LaunchSitePlanning.Enabled ? data : (_routePlan.AccessPlanning ?? data) with { Roads = data.Roads }; SetRouteWorkspaceDirty(true); RefreshRoutePlanUi(Math.Max(0, _routeWorkspace.GetSelectedSortieIndex())); }
        else QueueSiteSuggestions();
        if (_routeSubdivisionPreparation is { } preparation)
            _routeWorkspace.SetSurveyTrackReferences(data.Roads, preparation.CentralMeridian, preparation.Datum);
    }
    private AccessPoint[] AccessContext() => _lastCoordinateBatch?.Results.Select(p => new AccessPoint(p.Longitude, p.Latitude)).ToArray()
        ?? (_routePlan is null ? [] : _routePlan.Inference.Endpoints.Select(p => RouteAccessPlanner.Geographic(p, _routePlan)).ToArray());
    private void RefreshAccessPlanning()
        => _coordinateWorkspace.AccessPlanning.SetData(_routePlan?.AccessPlanning ?? LoadAccessPlanning(), AccessContext());
    private void AddExportedCoordinateKmlReference(string path)
    {
        var road = AccessPlanningService.Import(path, includePolygonBoundaries: true);
        var data = _routePlan?.AccessPlanning ?? LoadAccessPlanning();
        if (!data.Roads.Any(r => r.Sha256 == road.Sha256 && r.Crs == road.Crs))
            SaveAccessPlanning(data with { Roads = [.. data.Roads, road] });
        RefreshAccessPlanning();
    }
    private void OpenAccessPlanning()
    {
        var original = _routePlan?.AccessPlanning ?? LoadAccessPlanning();
        var draft = original;
        using var dialog = new Form { Text = "踏勘轨迹参考", StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(1050, 680), MinimumSize = new Size(660, 460), ShowInTaskbar = false };
        var editor = new SurveyTrackControl(value => draft = value, AccessCsvProjection) { BatchPicking = true };
        var context = _routePlan is null ? AccessContext() :
            _routePlan.Inference.Endpoints.Select(p => RouteAccessPlanner.Geographic(p, _routePlan)).ToArray();
        editor.SetData(original, context);
        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true };
        var save = UiComponents.Button("保存并返回航线", (_, _) =>
        {
            try
            {
                if (!ReferenceEquals(draft, original)) SaveAccessPlanning(draft);
                editor.FinishInputHistory();
                dialog.DialogResult = DialogResult.OK;
            }
            catch (Exception ex) { MessageBox.Show(dialog, ex.Message, "保存失败"); }
        });
        var cancel = UiComponents.Button("取消", (_, _) => dialog.DialogResult = DialogResult.Cancel);
        dialog.CancelButton = cancel;
        footer.Controls.Add(save); footer.Controls.Add(cancel);
        dialog.Controls.Add(editor); dialog.Controls.Add(footer);
        if (dialog.ShowDialog(this) == DialogResult.OK) RefreshAccessPlanning();
    }
    private ProjectionParameters? AccessCsvProjection()
    {
        double? cm = ParseOptionalDouble(_coordinateWorkspace.GetCentralMeridianText(), "中央经线");
        var p = _coordinateWorkspace.ReadProjectionParameters(cm);
        if (!p.CentralMeridian.HasValue) throw new InvalidOperationException("道路XY输入需要明确中央经线或UTM/RSO参数。");
        return p;
    }
    private void LockCurrentSortieSite()
    {
        if (!LaunchSitePlanning.Enabled) return;
        if (_routePlan is null || _routePlan.Sorties.Count == 0) throw new InvalidOperationException("请先生成航线。");
        int index = _routeWorkspace.GetSelectedSortieIndex(); if (index < 0) index = 0;
        var plan = _routePlan; var data = plan.AccessPlanning ?? LoadAccessPlanning();
        var choices = new[] { "自动推荐" }.Concat(data.Sites.Where(s => s.Enabled).Select(s => $"{s.Name} [{s.Id}]")).ToArray();
        var a = PromptDialog.Show(this, "当前架次起降点", [new PromptField("site", "起降点", choices[0], choices, Required: true)],
            note: "按去程加返程最短推荐同一个点。锁定点被停用/删除时显示不可用，不静默改用其他点；编辑架次几何后需重新确认锁定。");
        if (a is null) return;
        var locks = new Dictionary<string, string>(data.SortieSiteLocks); string key = RouteAccessPlanner.SortieKey(plan.Sorties[index]);
        if (a["site"] == "自动推荐") locks.Remove(key);
        else locks[key] = data.Sites.Single(s => $"{s.Name} [{s.Id}]" == a["site"]).Id;
        SaveAccessPlanning(data with { SortieSiteLocks = locks });
    }
    private void ExportAccessAssessment()
    {
        if (!LaunchSitePlanning.Enabled) return;
        if (_routePlan is null) throw new InvalidOperationException("请先生成航线。");
        using var d = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = "起降往返航程评估.csv", OverwritePrompt = true };
        var files = FileInteractions.For(this, "access-assessment", "output"); files.Prepare(d);
        if (d.ShowDialog(this) != DialogResult.OK) return;
        AccessPlanningService.ExportAssessment(d.FileName, _routePlan);
        files.OutputSucceeded(d.FileName);
        SetStatus("已导出独立起降航程评估；包含逐架次作业/去程/返程/完整航程，不修改测线端点。");
    }
}
