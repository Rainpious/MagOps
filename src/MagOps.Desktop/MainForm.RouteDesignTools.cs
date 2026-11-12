using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using MagOps.Core.Routes;
using MagOps.Core.SurveyPlanning;
using MagOps.Infrastructure.Services;

namespace MagOps.Desktop;

public sealed partial class MainForm
{
    private RouteDesignBasis? _routeDesignBasis;
    private void AlignRouteToBoundary()
    {
        var edges = new List<(string Label, double Azimuth, double Length)>();
        void AddRing(string label, IReadOnlyList<SurveyBoundaryPoint> ring)
        {
            for (int i = 0; i < ring.Count; i++)
            {
                var a = ring[i]; var b = ring[(i + 1) % ring.Count];
                double length = new SurveyLineSegment(a, b).Length;
                if (length <= .001) continue;
                double bearing = (Math.Atan2(b.X-a.X, b.Y-a.Y)*180/Math.PI+360)%180;
                edges.Add(($"{label} 边{i+1}", bearing, length));
            }
        }
        if (_routeWorkspace.GetSourceChoice() == RouteSourceChoice.Ply)
            AddRing("PLY", _plySurveyLines.ReadBoundary(_routeWorkspace.GetPlyPath()));
        else
        {
            var preparation = PrepareCurrentRouteKmlSubdivision();
            var measurement = SpatialBoundaryMeasurementService.MeasureKml(preparation.SourceKml,
                new(preparation.Datum, preparation.CentralMeridian));
            foreach (var ring in measurement.Vertices.GroupBy(v => (v.Polygon, v.Ring)))
                AddRing($"区域{ring.Key.Polygon}/环{ring.Key.Ring}", ring.Select(v => new SurveyBoundaryPoint(v.Easting, v.Northing)).ToArray());
        }
        using var form = new Form { Text = "选边定向（投影坐标北顺时针）", Width = 650, Height = 170, StartPosition = FormStartPosition.CenterParent };
        var choices = new ComboBox { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList };
        choices.Items.AddRange(edges.Select(e => (object)$"{e.Label}：{e.Length:0.##} m；方位 {e.Azimuth:0.######}°").ToArray());
        if (edges.Count == 0) throw new InvalidOperationException("边界没有有效边。");
        choices.SelectedIndex = 0;
        var ok = new Button { Text = "应用方位", Dock = DockStyle.Bottom, DialogResult = DialogResult.OK };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(8) };
        layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.Controls.Add(choices, 0, 0);
        layout.Controls.Add(new Label { Text = "按当前投影反算，0°为坐标北、90°为坐标东。仅修改设计方位；生成后才更新航线。", Dock = DockStyle.Fill }, 0, 1);
        layout.Controls.Add(ok, 0, 2); form.Controls.Add(layout);
        if (form.ShowDialog(this) != DialogResult.OK) return;
        _routeWorkspace.SetLineAzimuth(edges[choices.SelectedIndex].Azimuth);
        _routeDesignBasis = new(edges[choices.SelectedIndex].Azimuth, $"{edges[choices.SelectedIndex].Label}；投影坐标北顺时针");
        SetStatus("已按选中边设置坐标北方位；请重新生成并核对短段，短段不会自动删除。");
    }

    private void ShowRouteWorkEstimate()
    {
        var plans = _routePartitionContexts.Values.Where(c => c.Plan is not null).Select(c => c.Plan!).ToArray();
        if (plans.Length == 0 && _routePlan is not null) plans = [_routePlan];
        if (plans.Length == 0) throw new InvalidOperationException("请先生成航线。");
        double km = plans.Sum(p => p.Sorties.Sum(s => RouteCoordinateMath.RouteLength(s.Points, p.Mode, p.CentralMeridian))) / 1000;
        var values = PromptDialog.Show(this, "设计工作量估算", [new("capacity", "人工日产能（航线 km/日，不含起降往返）", Required: true)]);
        if (values is null) return;
        double capacity = ParseRoutePositive(values["capacity"], "人工日产能");
        foreach (var p in plans) p.DesignBasis = (p.DesignBasis ?? new RouteDesignBasis(p.GeometryDiagnostic.AzimuthDegrees, "已生成航线几何")) with { DailyCapacityKm = capacity };
        SetRouteWorkspaceDirty(true);
        string area = "无可用边界，面积未计";
        if (_routeSubdivisionPreparation is { } prep && File.Exists(prep.SourceKml))
        {
            double a = SpatialBoundaryMeasurementService.MeasureKml(prep.SourceKml, new(prep.Datum, prep.CentralMeridian)).AreaSquareMetres / 1e6;
            area = $"当前母区净投影面积 {a:0.###} km²（多区域汇总、扣除孔洞；不代表实际完成面积）";
        }
        else if (_routeWorkspace.GetSourceChoice() == RouteSourceChoice.Ply && File.Exists(_routeWorkspace.GetPlyPath()))
        {
            var ring = _plySurveyLines.ReadBoundary(_routeWorkspace.GetPlyPath());
            double a = Math.Abs(Enumerable.Range(0, ring.Count).Sum(i => (ring[i].X-ring[0].X)*(ring[(i+1)%ring.Count].Y-ring[0].Y)-(ring[(i+1)%ring.Count].X-ring[0].X)*(ring[i].Y-ring[0].Y))) / 2e6;
            area = $"当前PLY投影面积 {a:0.###} km²（不代表实际完成面积）";
        }
        double[] lengths = plans.SelectMany(p => p.Sorties.SelectMany(s => Enumerable.Range(0, Math.Min(s.SourceLines.Count, s.Points.Count / 2))
            .Select(i => RouteCoordinateMath.RouteLength([s.Points[2 * i], s.Points[2 * i + 1]], p.Mode, p.CentralMeridian)))).Order().ToArray();
        MessageBox.Show(this, $"已生成子区 {plans.Length}；架次 {plans.Sum(p => p.Sorties.Count)}\r\n设计航线长度（含内部连接）{km:0.###} km\r\n{area}\r\n人工日产能 {capacity:0.###} km/日；估算 {km / capacity:0.##} 作业日，向上取整 {Math.Ceiling(km / capacity)} 日。\r\n" +
            $"最短测段 {(lengths.Length == 0 ? "未知" : lengths[0].ToString("0.###"))} m；待生成子区 {_routePartitionContexts.Values.Count(c => c.Plan is null)}。\r\n长度来自已生成快照，面积来自当前边界；边界更新后请先重新生成。未含天气、转场和起降往返；短段仅提示，不删除。", "设计工作量");
    }

    private void ManageRouteCheckLines()
    {
        var plan = _routePlan ?? throw new InvalidOperationException("请先打开一个已生成子区/航线。");
        RouteIdentityService.EnsureReferences(plan.Sorties);
        var draft = plan.CheckLines.ToList();
        using var form = new Form { Text = "检查线设计（复飞 / 交叉分别管理）", Width = 950, Height = 560, StartPosition = FormStartPosition.CenterParent };
        var list = new ListBox { Dock = DockStyle.Fill, HorizontalScrollbar = true };
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true };
        var source = new ComboBox { Width = 350, DropDownStyle = ComboBoxStyle.DropDownList };
        var originals = plan.Sorties.SelectMany(s => Enumerable.Range(0, Math.Min(s.SourceLines.Count, s.Points.Count / 2)).Select(i =>
            new { Sortie = s, Index = i, Reference = s.TaskReferences.SingleOrDefault(r => r.GeometryFingerprint == RouteIdentityService.Fingerprint(s.Points[i * 2], s.Points[i * 2 + 1]) && (r.LineName == s.SourceLines[i] || r.ExecutionSegmentId == s.SourceLines[i])) }))
            .Where(x => x.Reference is not null).ToArray();
        source.Items.AddRange(originals.Select(x => (object)$"{x.Sortie.Name} / {x.Sortie.SourceLines[x.Index]}").ToArray());
        if (source.Items.Count > 0) source.SelectedIndex = 0;
        actions.Controls.Add(source);
        actions.Controls.Add(Button("添加复飞检查", (_, _) => RunUi("复飞检查", () =>
        {
            if (source.SelectedIndex < 0) throw new InvalidOperationException("无可关联的原测线身份。");
            var o = originals[source.SelectedIndex]; var r = o.Reference!;
            draft.Add(new("check-" + Guid.NewGuid().ToString("N"), RouteCheckLines.Repeat,
                "复飞_" + o.Sortie.SourceLines[o.Index] + "_" + (draft.Count + 1), r.SurveyTaskStableId, r.SurveyLineStableId,
                r.GeometryFingerprint, o.Sortie.Points[o.Index * 2].Clone(), o.Sortie.Points[o.Index * 2 + 1].Clone()) { CoordinateMode = plan.Mode, CentralMeridian = plan.CentralMeridian }); Refresh();
        })));
        actions.Controls.Add(Button("添加交叉检查", (_, _) => RunUi("交叉检查", () =>
        {
            var values = PromptDialog.Show(form, "交叉检查端点（沿用当前航线坐标模式 " + plan.Mode + "）",
                [new("name", "名称", Required: true), new("x1", "起点 X", Required: true), new("y1", "起点 Y", Required: true),
                 new("x2", "终点 X", Required: true), new("y2", "终点 Y", Required: true)]);
            if (values is null) return;
            double N(string key) { double v = ParseRouteNumber(values[key], key); if (!double.IsFinite(v)) throw new ArgumentException("端点必须为有限数。"); return v; }
            var line = new RouteCheckLine("check-" + Guid.NewGuid().ToString("N"), RouteCheckLines.Cross, values["name"], "", "", "",
                new() { X = N("x1"), Y = N("y1") }, new() { X = N("x2"), Y = N("y2") }) { CoordinateMode = plan.Mode, CentralMeridian = plan.CentralMeridian };
            RouteCheckLines.Validate([line]);
            if (plan.Mode == "geographic" && new[] { line.Start, line.End }.Any(p => Math.Abs(p.X) > 180 || Math.Abs(p.Y) > 90))
                throw new ArgumentException("经纬度超出有效范围。");
            bool Crosses(RouteEndpoint a, RouteEndpoint b, RouteEndpoint c, RouteEndpoint d)
            {
                double Side(RouteEndpoint p, RouteEndpoint q, RouteEndpoint r) => (q.X-p.X)*(r.Y-p.Y)-(q.Y-p.Y)*(r.X-p.X);
                return Side(a,b,c)*Side(a,b,d) < 0 && Side(c,d,a)*Side(c,d,b) < 0;
            }
            if (!originals.Any(o => Crosses(line.Start, line.End, o.Sortie.Points[o.Index*2], o.Sortie.Points[o.Index*2+1])))
                throw new ArgumentException("交叉检查线必须穿过至少一条当前测线；当前端点未形成交叉，请核对坐标模式与位置。");
            draft.Add(line); Refresh();
        })));
        actions.Controls.Add(Button("移除选中", (_, _) => { if (list.SelectedIndex >= 0) draft.RemoveAt(list.SelectedIndex); Refresh(); }));
        var save = Button("确认到工程", (_, _) => { RouteCheckLines.Validate(draft); form.DialogResult = DialogResult.OK; }); actions.Controls.Add(save);
        actions.Controls.Add(Button("导出检查线", (_, _) => RunUi("导出检查线", () => ExportRouteCheckLines(plan, draft))));
        form.Controls.Add(list); form.Controls.Add(actions);
        form.Controls.Add(new Label { Dock = DockStyle.Top, AutoSize = true,
            Text = "复飞关联原任务与几何；交叉检查独立身份。此清单不计普通任务进度，不自动纳入生产。确认后需保存航线工程 JSON。" });
        Refresh();
        if (form.ShowDialog(this) == DialogResult.OK) { plan.CheckLines = draft; SetRouteWorkspaceDirty(true); }
        void Refresh()
        {
            list.Items.Clear();
            foreach (var l in draft)
            {
                bool linked = l.Kind == RouteCheckLines.Cross || originals.Any(o => o.Reference!.SurveyTaskStableId == l.SourceTaskId && o.Reference.GeometryFingerprint == l.SourceGeometryFingerprint);
                list.Items.Add($"{l.Kind} | {l.Name} | {l.Id} | {(linked ? "设计快照" : "原任务缺失/几何已变，需复核")}");
            }
        }
    }

    private void ExportRouteCheckLines(RoutePlanResult plan, IReadOnlyList<RouteCheckLine> lines)
    {
        RouteCheckLines.Validate(lines);
        if (lines.Count == 0) throw new InvalidOperationException("没有检查线。");
        string root = Path.Combine(RequireRouteOutput(), "检查线_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N")[..6]);
        string staging = root + ".pending";
        Directory.CreateDirectory(staging);
        try
        {
        string json = Path.Combine(staging, "check-lines.json");
        File.WriteAllText(json, JsonSerializer.Serialize(new { schemaVersion = 1, mode = plan.Mode, plan.CentralMeridian, plan.PartitionCode, productionEligible = false, lines }, new JsonSerializerOptions { WriteIndented = true }));
        XNamespace ns = "http://www.opengis.net/kml/2.2";
        var doc = new XElement(ns + "Document", lines.Select(l =>
        {
            var a = RouteCoordinateMath.ToLonLat(l.Start, l.CoordinateMode, l.CentralMeridian); var b = RouteCoordinateMath.ToLonLat(l.End, l.CoordinateMode, l.CentralMeridian);
            return new XElement(ns + "Placemark", new XAttribute("id", l.Id), new XElement(ns + "name", l.Kind + " / " + l.Name),
                new XElement(ns + "description", $"检查设计；原任务={l.SourceTaskId}；几何快照={l.SourceGeometryFingerprint}；需复核"),
                new XElement(ns + "LineString", new XElement(ns + "coordinates", FormattableString.Invariant($"{a.Lon:R},{a.Lat:R},0 {b.Lon:R},{b.Lat:R},0"))));
        }));
        string kml = Path.Combine(staging, "check-lines.kml"); new XDocument(new XElement(ns + "kml", doc)).Save(kml);
        foreach (string path in new[] { json, kml })
            GeneratedArtifactIdentity.Write(path, "design", "check_line_design", GeneratedArtifactIdentity.Hash(json), File.Exists(plan.SourcePath) ? [plan.SourcePath] : [], qualityStatus: "REQUIRES_REVIEW",
                context: IsProjectInitialized() ? new GeneratedArtifactContext(_workspace.GetWorkspace(Root).ProjectId, CurrentWorkareaId) : null);
        Directory.Move(staging, root);
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
        RegisterGeneratedBusinessFiles([Path.Combine(root, "check-lines.json"), Path.Combine(root, "check-lines.kml")], "check_line_design");
        FileInteractions.For(this, "check-lines", "output").OutputSucceeded(root, directory: true);
    }
}
