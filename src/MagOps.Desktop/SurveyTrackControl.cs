using MagOps.Core.Coordinates;
using MagOps.Core.Routes;
using MagOps.Infrastructure.Services;
namespace MagOps.Desktop;

/// <summary>Survey references only. Historical launch-site fields are carried unchanged.</summary>
internal sealed class SurveyTrackControl : UserControl
{
    private readonly AccessReferenceMap _map = new() { ReferenceOnly = true };
    private readonly ListBox _roads = new() { Dock = DockStyle.Right, Width = 260 };
    private readonly Label _status = new() { Dock = DockStyle.Bottom, AutoSize = true };
    private readonly Action<AccessPlanningData> _save;
    private readonly Func<ProjectionParameters?> _projection;
    private AccessPlanningData _data = new();
    private AccessPoint[] _context = [];
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool BatchPicking { get; set; }
    private readonly List<(FileInteractionSession Session, string Path)> _draftInputs = [];
    internal SurveyTrackControl(Action<AccessPlanningData> save, Func<ProjectionParameters?> projection)
    {
        _save = save; _projection = projection; Dock = DockStyle.Fill;
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true };
        var add = UiComponents.Button("添加踏勘轨迹", (_, _) => Run(() => Import(null)));
        FileDropSupport.EnableSingle(add, path => Run(() => Import(path)),
            path => FileDropSupport.HasExtension(path, ".ovkml", ".ovobj", ".kml", ".gpx", ".csv", ".tsv", ".txt"));
        bar.Controls.Add(add);
        bar.Controls.Add(UiComponents.Button("移除所选轨迹", (_, _) => Run(() =>
        {
            if (_roads.SelectedIndex >= 0)
                Commit(_data with { Roads = _data.Roads.Where((_, i) => i != _roads.SelectedIndex).ToList() });
        })));
        Controls.Add(_map); Controls.Add(_roads); Controls.Add(_status); Controls.Add(bar);
    }
    internal void SetData(AccessPlanningData data, IEnumerable<AccessPoint>? context = null)
    {
        _data = data; _context = context?.ToArray() ?? [];
        _roads.DataSource = data.Roads.Select(r => $"{r.Name} · {r.Segments.Count} 段 / {r.Segments.Sum(s => s.Count)} 点").ToList();
        _map.SetData(data, _context);
        _status.Text = $"踏勘轨迹 {data.Roads.Count} 条，仅供切割工区参考；中键平移，滚轮缩放。";
    }
    private void Commit(AccessPlanningData data) { _save(data); SetData(data, _context); }
    private void Run(Action action) { try { action(); } catch (Exception e) { SetData(_data, _context); _status.Text = e.Message; } }
    internal void FinishInputHistory()
    {
        foreach (var (session, path) in _draftInputs)
            if (_data.Roads.Any(r => string.Equals(r.SourcePath, path, StringComparison.OrdinalIgnoreCase))) session.InputSucceeded(path);
        _draftInputs.Clear();
    }
    private void Import(string? path)
    {
        var files = FileInteractions.For(this, "reference-road");
        if (path is null)
        {
            using var d = new OpenFileDialog { Filter = "踏勘轨迹和道路|*.ovkml;*.ovobj;*.kml;*.gpx;*.csv;*.tsv;*.txt" };
            files.Prepare(d);
            if (d.ShowDialog(this) != DialogResult.OK) return; path = d.FileName;
        }
        ProjectionParameters? projection = null;
        if (Path.GetExtension(path).ToLowerInvariant() is ".csv" or ".tsv" or ".txt")
        {
            var answer = PromptDialog.Show(FindForm()!, "确认道路坐标", [new PromptField("crs", "前两列坐标", "WGS84经纬度", ["WGS84经纬度", "当前工作台投影XY"], Required: true)],
                note: "每行前两列为经度/纬度或XY；空行分段，错误行整批拒绝。不按数值猜测投影。");
            if (answer is null) return;
            if (answer["crs"] != "WGS84经纬度") projection = _projection() ?? throw new InvalidDataException("请先在坐标转换页明确投影参数。");
        }
        var road = AccessPlanningService.Import(path, projection);
        if (road.Crs == "未标注")
        {
            var answer = PromptDialog.Show(FindForm()!, "确认踏勘轨迹坐标系",
                [new PromptField("datum", "原始经纬度基准", "请选择", ["请选择", "CGCS2000", "WGS84"], Required: true)],
                note: "请按奥维导出设置选择。GCJ-02 或无法确认时请取消，改用标注坐标系的 OVKML。");
            if (answer is null || answer["datum"] == "请选择") return;
            road = road with { Crs = answer["datum"] };
        }
        var previous = _data.Roads.Where(r => string.Equals(r.SourcePath,road.SourcePath,StringComparison.OrdinalIgnoreCase)).ToList();
        if (previous.Count > 0 && (previous.Count > 1 || previous[0].Sha256 != road.Sha256 || previous[0].Crs != road.Crs))
        {
            var ids = previous.Select(r => r.Id).ToHashSet();
            var replacement = road with { Id = previous[0].Id, UseForRecommendations = previous.Any(r => r.UseForRecommendations) };
            Commit(_data with { Roads = [.. _data.Roads.Where(r => !ids.Contains(r.Id)), replacement] });
            if (BatchPicking) _draftInputs.Add((files,path)); else files.InputSucceeded(path);
            _status.Text = $"已更新踏勘轨迹：{road.Name}。";
            return;
        }
        bool same = _data.Roads.Any(r => r.Crs == road.Crs &&
            (r.Sha256 == road.Sha256 || (r.Segments.Count == road.Segments.Count &&
                r.Segments.Zip(road.Segments).All(pair => pair.First.SequenceEqual(pair.Second)))));
        if (same)
        {
            if (!BatchPicking) files.InputSucceeded(path);
            _status.Text = "相同轨迹已在地图中。";
            return;
        }
        Commit(_data with { Roads = [.. _data.Roads, road with { UseForRecommendations = false }] });
        if (BatchPicking) _draftInputs.Add((files, path));
        else files.InputSucceeded(path);
    }
}
