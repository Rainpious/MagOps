using System.Globalization;
using MagOps.Core.Coordinates;
using MagOps.Core.Routes;
using MagOps.Infrastructure.Services;
namespace MagOps.Desktop;

internal sealed class AccessPlanningControl : UserControl
{
    private readonly AccessReferenceMap _map = new();
    private readonly CheckBox _snap = new() { Text = "吸附到参考道路（默认关闭）", AutoSize = true };
    private readonly NumericUpDown _tolerance = new() { Minimum = 1, Maximum = 10000, Value = 50, Width = 80 };
    private readonly DataGridView _sites = GridFactory.Create();
    private readonly ListBox _roads = new() { Dock = DockStyle.Fill };
    private readonly Label _status = new() { AutoSize = true, Dock = DockStyle.Fill };
    private readonly Action<AccessPlanningData> _save;
    private readonly Func<ProjectionParameters?> _projection;
    private AccessPlanningData _data = new();
    private AccessPoint[] _context = [];
    private bool _syncing;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool BatchPicking { get; set; }
    private readonly List<(FileInteractionSession Session, string Path)> _draftInputs = [];

    internal void FinishInputHistory()
    {
        foreach (var (session, path) in _draftInputs)
            if (_data.Roads.Any(r => string.Equals(r.SourcePath, path, StringComparison.OrdinalIgnoreCase)))
                session.InputSucceeded(path);
        _draftInputs.Clear();
    }
    internal AccessPlanningControl(Action<AccessPlanningData> save, Func<ProjectionParameters?> projection)
    {
        _save = save; _projection = projection; Dock = DockStyle.Fill;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.AutoSize));
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        Button B(string text, Action action) => UiComponents.Button(text, (_, _) => Run(action), UiButtonRole.Subtle);
        var import = B("导入踏勘轨迹 / 道路", () => Import(null));
        FileDropSupport.EnableSingle(import, p => Run(() => Import(p)), p => FileDropSupport.HasExtension(p, ".kml", ".gpx", ".csv", ".tsv", ".txt", ".ovobj", ".ovkml"));
        toolbar.Controls.Add(import);
        toolbar.Controls.Add(B("切换所选轨迹参与推荐", () =>
        {
            int index = _roads.SelectedIndex;
            if (index < 0) return;
            Commit(_data with { Roads = _data.Roads.Select((r, i) => i == index ? r with { UseForRecommendations = !r.UseForRecommendations } : r).ToList() });
            _roads.SelectedIndex = index;
        }));
        toolbar.Controls.Add(B("移除所选道路", () => { if (_roads.SelectedIndex >= 0) Commit(_data with { Roads = _data.Roads.Where((_, i) => i != _roads.SelectedIndex).ToList() }); }));
        toolbar.Controls.Add(B("新增起降点（坐标）", () => EditSite(null, null)));
        toolbar.Controls.Add(B("编辑所选点", () => { if (Selected() is { } s) EditSite(s, null); }));
        toolbar.Controls.Add(B("启用 / 停用", () => { if (Selected() is { } s) Commit(_data with { Sites = _data.Sites.Select(v => v.Id == s.Id ? v with { Enabled = !v.Enabled } : v).ToList() }); }));
        toolbar.Controls.Add(B("删除所选点", () => { if (Selected() is { } s) Commit(_data with { Sites = _data.Sites.Where(v => v.Id != s.Id).ToList() }); }));
        toolbar.Controls.Add(_snap);
        var toleranceGroup = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        toleranceGroup.Controls.Add(new Label { Text = "容差(m)", AutoSize = true, Padding = new Padding(0, 7, 0, 0) });
        toleranceGroup.Controls.Add(_tolerance); toolbar.Controls.Add(toleranceGroup);
        _snap.CheckedChanged += (_, _) => { if (!_syncing) Run(() => Commit(_data with { SnapEnabled = _snap.Checked })); };
        _tolerance.ValueChanged += (_, _) => { if (!_syncing) Run(() => Commit(_data with { SnapToleranceM = (double)_tolerance.Value })); };
        _map.PointChosen += (p, id) => Run(() => ChooseMapPoint(p, id));
        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        body.ColumnStyles.Add(new(SizeType.Percent, 65)); body.ColumnStyles.Add(new(SizeType.Percent, 35));
        body.Controls.Add(_map, 0, 0);
        var right = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
        right.RowStyles.Add(new(SizeType.Percent, 25)); right.RowStyles.Add(new(SizeType.Percent, 75));
        right.Controls.Add(_roads, 0, 0); right.Controls.Add(_sites, 0, 1);
        body.Controls.Add(right, 1, 0);
        layout.Controls.Add(toolbar, 0, 0); layout.Controls.Add(body, 0, 1); layout.Controls.Add(_status, 0, 2); Controls.Add(layout);
    }
    internal void SetData(AccessPlanningData data, IEnumerable<AccessPoint>? context = null)
    {
        _data = data; _context = context?.ToArray() ?? []; _syncing = true;
        try { _snap.Checked = data.SnapEnabled; _tolerance.Value = (decimal)Math.Clamp(data.SnapToleranceM, 1, 10000); }
        finally { _syncing = false; }
        _roads.DataSource = data.Roads.Select(r =>  $"{(r.UseForRecommendations ? "推荐用" : "仅参考")} · {r.Name}：{r.Segments.Count}段 / {r.Segments.Sum(s => s.Count)}点").ToList();
        _sites.DataSource = data.Sites.Select(s => new { Id = s.Id, 名称 = s.Name, 经度 = s.Longitude, 纬度 = s.Latitude, 启用 = s.Enabled, 定位 = s.RoadId is null ? "自由点" : "曾吸附（坐标固定）" }).ToList();
        if (_sites.Columns["Id"] is { } idColumn) idColumn.Visible = false;
        foreach (string name in new[] { "经度", "纬度" })
            if (_sites.Columns[name] is { } column) column.DefaultCellStyle.Format = "0.0000000";
        _map.SetData(data, _context);
        _status.Text = $"道路{data.Roads.Count}条，起降点{data.Sites.Count}个；自由选点不需要道路。手工坐标为WGS84经纬度。点击新增，拖动点移动，中键平移；开关不移动已有点。";
    }
    private LaunchSite? Selected() => _sites.CurrentRow?.DataBoundItem is { } item
        ? _data.Sites.FirstOrDefault(s => s.Id == item.GetType().GetProperty("Id")!.GetValue(item)?.ToString()) : null;
    private void Commit(AccessPlanningData data) { RouteAccessPlanner.Validate(data); _save(data); SetData(data, _context); }
    private void Run(Action action) { try { action(); } catch (Exception e) { SetData(_data, _context); _status.Text = e.Message; } }
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
            // Confirmed coordinates and sortie locks survive a change to the original survey file.
            // Old edge indices no longer describe the revised polyline.
            var sites = _data.Sites.Select(s => s.RoadId is not null && ids.Contains(s.RoadId)
                ? s with { RoadId = null, SegmentIndex = null, EdgeIndex = null, EdgeFraction = null, SnapDistanceM = null } : s).ToList();
            Commit(_data with { Roads = [.. _data.Roads.Where(r => !ids.Contains(r.Id)), replacement], Sites = sites });
            if (BatchPicking) _draftInputs.Add((files,path)); else files.InputSucceeded(path);
            _status.Text = $"已更新踏勘轨迹：{road.Name}。已确认点的坐标保留。";
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
        Commit(_data with { Roads = [.. _data.Roads, road with { UseForRecommendations = BatchPicking || Path.GetExtension(path).Equals(".ovobj", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(path).Equals(".ovkml", StringComparison.OrdinalIgnoreCase) }] });
        if (BatchPicking) _draftInputs.Add((files, path));
        else files.InputSucceeded(path);
    }
    private void ChooseMapPoint(AccessPoint point, string? id)
    {
        var existing = _data.Sites.FirstOrDefault(s => s.Id == id);
        if (existing is not null) SetPoint(existing, point);
        else if (BatchPicking)
        {
            int number = _data.Sites.Count + 1;
            while (_data.Sites.Any(s => s.Name == $"起降点{number}")) number++;
            SetPoint(new LaunchSite(Guid.NewGuid().ToString("N"), $"起降点{number}", point.Longitude, point.Latitude), point);
        }
        else EditSite(null, point);
    }
    private void EditSite(LaunchSite? old, AccessPoint? clicked)
    {
        var a = PromptDialog.Show(FindForm()!, old is null ? "新增起降点" : "编辑起降点",
            [new PromptField("name", "名称", old?.Name ?? $"起降点{_data.Sites.Count + 1}", Required: true),
             new PromptField("lon", "WGS84经度", (clicked?.Longitude ?? old?.Longitude)?.ToString("R", CultureInfo.InvariantCulture) ?? "", Required: true),
             new PromptField("lat", "WGS84纬度", (clicked?.Latitude ?? old?.Latitude)?.ToString("R", CultureInfo.InvariantCulture) ?? "", Required: true)],
            note: "吸附关闭时使用输入的真实位置；开启后仅在容差内吸附。已确认位置不随道路更新自动移动。");
        if (a is null) return;
        var p = new AccessPoint(double.Parse(a["lon"], CultureInfo.InvariantCulture), double.Parse(a["lat"], CultureInfo.InvariantCulture));
        SetPoint((old ?? new(Guid.NewGuid().ToString("N"), a["name"], p.Longitude, p.Latitude)) with { Name = a["name"] }, p);
    }
    private void SetPoint(LaunchSite site, AccessPoint p)
    {
        if (!RouteAccessPlanner.Valid(p)) throw new InvalidDataException("起降点需有效WGS84经纬度。");
        var snap = _data.SnapEnabled ? RouteAccessPlanner.Snap(p, _data.Roads, _data.SnapToleranceM) : null;
        var point = snap?.Point ?? p;
        var changed = site with { Longitude = point.Longitude, Latitude = point.Latitude, RoadId = snap?.RoadId,
            SegmentIndex = snap?.SegmentIndex, EdgeIndex = snap?.EdgeIndex, EdgeFraction = snap?.Fraction, SnapDistanceM = snap?.DistanceM };
        Commit(_data with { Sites = [.. _data.Sites.Where(s => s.Id != site.Id), changed] });
        if (_data.SnapEnabled && snap is null) _status.Text = "容差范围内没有道路，已保留自由点坐标。";
    }
}
