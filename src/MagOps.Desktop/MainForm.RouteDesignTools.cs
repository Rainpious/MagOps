using MagOps.Application;
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
                double bearing = (Math.Atan2(b.X - a.X, b.Y - a.Y) * 180 / Math.PI + 360) % 180;
                edges.Add(($"{label} 边{i + 1}", bearing, length));
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
            double a = Math.Abs(Enumerable.Range(0, ring.Count).Sum(i => (ring[i].X - ring[0].X) * (ring[(i + 1) % ring.Count].Y - ring[0].Y) - (ring[(i + 1) % ring.Count].X - ring[0].X) * (ring[i].Y - ring[0].Y))) / 2e6;
            area = $"当前PLY投影面积 {a:0.###} km²（不代表实际完成面积）";
        }
        double[] lengths = plans.SelectMany(p => p.Sorties.SelectMany(s => Enumerable.Range(0, Math.Min(s.SourceLines.Count, s.Points.Count / 2))
            .Select(i => RouteCoordinateMath.RouteLength([s.Points[2 * i], s.Points[2 * i + 1]], p.Mode, p.CentralMeridian)))).Order().ToArray();
        MessageBox.Show(this, $"已生成子区 {plans.Length}；架次 {plans.Sum(p => p.Sorties.Count)}\r\n设计航线长度（含内部连接）{km:0.###} km\r\n{area}\r\n人工日产能 {capacity:0.###} km/日；估算 {km / capacity:0.##} 作业日，向上取整 {Math.Ceiling(km / capacity)} 日。\r\n" +
            $"最短测段 {(lengths.Length == 0 ? "未知" : lengths[0].ToString("0.###"))} m；待生成子区 {_routePartitionContexts.Values.Count(c => c.Plan is null)}。\r\n长度来自已生成快照，面积来自当前边界；边界更新后请先重新生成。未含天气、转场和起降往返；短段仅提示，不删除。", "设计工作量");
    }

    private string TieDraftPath(TieLineWorkspace context) =>
        Path.Combine(Root, CurrentWorkareaRelativePath, "切割线草稿", context.Version + ".json");

    private static List<RouteCheckLine> ReadTieDraft(string path) =>
        JsonSerializer.Deserialize<List<RouteCheckLine>>(File.ReadAllText(path)) ?? throw new InvalidDataException("切割线草稿为空。");

    private string ExportTieFlightKml(TieLineWorkspace context, IReadOnlyList<TieSortie> flights)
    {
        var plan = new RoutePlanResult
        {
            Mode = "geographic", SourcePath = context.BoundaryPath, CentralMeridian = context.Meridian,
            Options = new() { MaxSortieKm = context.MaxSortieM / 1000 },
            Sorties = flights.Select(context.ToWorkRoute).ToList()
        };
        bool elevated = _routeWorkspace.GetUseTerrain();
        double height = elevated ? ParseRouteNumber(_routeWorkspace.GetAglText(), "目标 AGL") : 0;
        string? terrain = elevated ? RequireTerrainForPlans([plan]) : null;
        string folder = Path.Combine(RequireRouteOutput(), $"切割线作业航线_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}");
        RouteExportBundle bundle;
        if (IsProjectInitialized())
        {
            var result = ExportRouteWithCustomDestination(plan, new RouteExportRequest(Root, folder,
                CurrentRouteWorkareaName(), true, false, new GeneratedArtifactContext(_workspace.GetWorkspace(Root).ProjectId,
                    CurrentWorkareaId), terrain, height, elevated));
            if (result.RegisteredArtifacts.Count > 0 && _artifactWorkspace.HasGridColumns())
                ApplyArtifactChanges(result.RegisteredArtifacts);
            bundle = result.Bundle;
        }
        else
            bundle = _routeExporter.ExportAllSeparated(plan, folder, "", exportKml: true, exportCsv: false,
                exportKmz: false, kmlTerrainGeoTiffPath: terrain, kmlAglM: height, kmlIncludeElevation: elevated);

        string directory = Path.GetDirectoryName(bundle.OutputPaths[0])!;
        FileInteractions.For(this, "check-flight-kml", "output").OutputSucceeded(directory, directory: true);
        SetStatus($"已导出 {bundle.OutputPaths.Count} 个切割线架次 KML：{directory}");
        return directory;
    }

    private void EditBoundaryTieDraft()
    {
        if (!TryGetManagedOutputContext(out _, out _, out _))
            throw new InvalidOperationException("请选择工区并登记边界，或载入已有航线工程。");
        var area = _workspace.GetWorkareas(Root).First(a => a.Path.Equals(CurrentWorkareaRelativePath, StringComparison.OrdinalIgnoreCase));
        string boundary = FindWorkareaBoundary() ?? throw new InvalidOperationException("请先为当前工区登记边界。");
        var cm = _workspace.ReadWorkareaSpatialContext(Root, area.Path).CentralMeridian ?? throw new InvalidOperationException("请先确认工区中央经线。");
        var context = new TieLineWorkspace(boundary, [], area.Boundary?.Projection?.Datum ?? "WGS84",
            projectMaxM: ParseRoutePositive(_routeWorkspace.GetMaxSortieKmText(), "最大单架次长度") * 1000, boundaryMeridian: cm);
        string path = TieDraftPath(context);
        string? initial = File.Exists(path) ? File.ReadAllText(path) : null;
        var lines = initial == null ? [] : ReadTieDraft(path);
        double step = lines.FirstOrDefault()?.Design?.Spacing ?? (double.TryParse(_routeWorkspace.GetLineSpacingText(), out double spacing) && spacing > 0 ? spacing : 100);
        using var editor = new TieLineDesigner(context, lines, step, initial != null, (draft, _) =>
        {
            string folder = Path.Combine(RequireRouteOutput(), "切割线试画_" + Guid.NewGuid().ToString("N"));
            TieLineExport.Write(folder, context, draft, "待载入正式测线");
            SetStatus("切割线草稿已导出：" + folder);
            return folder;
        }, flights => ExportTieFlightKml(context, flights));
        if (editor.ShowDialog(this) != DialogResult.OK || !editor.Changed) return;
        using var guard = MagOps.Infrastructure.Storage.WorkspaceFileLock.AcquireForRoot(Root);
        if ((File.Exists(path) ? File.ReadAllText(path) : null) != initial)
            throw new IOException("其他窗口已保存新的草稿，请重开后再修改。");
        AtomicFileWriter.Write(path, stream => JsonSerializer.Serialize(stream, editor.Lines));
        SetStatus("工区切割线草稿已保存，交会待载入正式测线。");
    }

    private void ManageRouteCheckLines()
    {
        var plan = _routePlan ?? _routePartitionContexts.Values.Select(c => c.Plan).FirstOrDefault(p => p != null);
        if (plan is null)
        {
            EditBoundaryTieDraft();
            return;
        }
        var plans = _routePartitionContexts.Values.Where(c => c.Plan != null).Select(c => c.Plan!).Append(plan).Distinct().ToArray();
        var prior = plan.CheckLines.Select(l => l.Design).FirstOrDefault(d => d is not null);
        string? preferred = _routeSubdivisionPreparation?.SourceKml ?? _restoredSubdivisionSnapshot?.MotherKmlPath ?? prior?.BoundaryPath;
        string? boundary = FindWorkareaBoundary(preferred: preferred);
        if (!TryGetManagedOutputContext(out _, out _, out _))
        {
            boundary ??= new[] { preferred, _routeWorkspace.GetKmlPath(), _routeWorkspace.GetPlyPath(), _routeCoveragePlyPath }
                .FirstOrDefault(p => File.Exists(p) && !IsKnownPartitionKml(p!));
        }
        if (boundary is null)
        {
            using var pick = new OpenFileDialog { Title = "选择整个工区的边界", Filter = "工区边界|*.ply;*.kml", InitialDirectory = ResolveRouteKmlInitialDirectory() };
            if (pick.ShowDialog(this) != DialogResult.OK) return;
            boundary = pick.FileName;
        }
        double activeLimit = plan.Options.MaxSortieKm;
        if (_routeWorkspace.HasEditedFlightLimit)
            activeLimit = ParseRoutePositive(_routeWorkspace.GetMaxSortieKmText(), "最大单架次长度");
        bool limitChanged = activeLimit != plan.Options.MaxSortieKm;
        double networkLimit = plans.Min(p => ReferenceEquals(p, plan) ? activeLimit : p.Options.MaxSortieKm) * 1000;
        var areaDatum = TryGetManagedOutputContext(out _, out string currentArea, out _)
            ? _workspace.GetWorkareas(Root).First(a => a.Path.Equals(currentArea, StringComparison.OrdinalIgnoreCase)).Boundary?.Projection?.Datum : null;
        var context = new TieLineWorkspace(boundary, plans, _routeSubdivisionPreparation?.Datum ?? prior?.Datum ?? _routeActivePartition?.Datum ?? areaDatum ?? "WGS84", _routePartitionContexts.Values.Count(c => c.Plan is null), networkLimit);
        var catalog = plans.SelectMany(p => p.CheckLines).DistinctBy(l => l.Id).ToList();
        bool imported = false;
        if (catalog.All(l => l.Design == null) && TryGetManagedOutputContext(out _, out _, out _))
        {
            var source = new TieLineWorkspace(boundary, [], context.Datum, projectMaxM: networkLimit, boundaryMeridian: context.Meridian);
            string draftPath = TieDraftPath(source);
            if (File.Exists(draftPath) && MessageBox.Show(this, "按当前正式测线复核并带入已保存的工区切割线草稿？", "工区切割线", MessageBoxButtons.YesNo) == DialogResult.Yes)
            {
                foreach (var old in ReadTieDraft(draftPath))
                {
                    var design = old.Design!;
                    var a = design.Approach.End; var b = design.Departure.Start;
                    var line = context.Draw(old.Name, a, Math.Atan2(b.X-a.X,b.Y-a.Y)*180/Math.PI, design.Spacing, design.Approach.Length, design.Departure.Length, old.Id);
                    catalog.Add(line with { Design = line.Design! with { Layout = design.Layout, CutFractions = design.CutFractions,
                        DesignNote = design.DesignNote, Standard = design.Standard, FlightBudget = design.FlightBudget } });
                }
                imported = catalog.Count > 0;
            }
        }
        double step = double.TryParse(_routeWorkspace.GetLineSpacingText(), out var spacing) && spacing > 0 ? spacing : 100;
        using var editor = new TieLineDesigner(context, catalog, prior?.Spacing ?? step, !_routeWorkspaceDirty && !limitChanged && !imported, (lines, state) =>
        {
            if (lines.Count == 0) throw new InvalidOperationException("没有检查线。");
            string root = Path.Combine(RequireRouteOutput(), "检查线_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N")[..6]);
            string staging = root + ".pending";
            try
            {
                TieLineExport.Write(staging, context, lines, state);
                string json = Path.Combine(staging, "check-lines.json");
                foreach (var name in TieLineExport.Files)
                    GeneratedArtifactIdentity.Write(Path.Combine(staging, name), "design", "check_line_design", GeneratedArtifactIdentity.Hash(json),
                        new[] { boundary, plan.SourcePath }.Where(File.Exists).ToArray(), qualityStatus: "REQUIRES_REVIEW",
                        context: IsProjectInitialized() ? new GeneratedArtifactContext(_workspace.GetWorkspace(Root).ProjectId, CurrentWorkareaId) : null);
                Directory.Move(staging, root);
            }
            finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
            RegisterGeneratedBusinessFiles(TieLineExport.Files.Select(n => Path.Combine(root, n)).ToArray(), "check_line_design");
            FileInteractions.For(this, "check-lines", "output").OutputSucceeded(root, directory: true);
            SetStatus("已导出切割线设计：" + root);
            return root;
        }, flights => ExportTieFlightKml(context, flights));
        if (editor.ShowDialog(this) != DialogResult.OK || (!editor.Changed && !limitChanged && !imported)) return;
        if (limitChanged)
        {
            plan.Options.MaxSortieKm = activeLimit;
            if (_routePartitionContexts.TryGetValue(plan.PartitionCode, out var current) && ReferenceEquals(current.Plan, plan))
            {
                current.MaxSortieKm = activeLimit;
                current.Dirty = true;
                _routeWorkspace.UpdatePartitionOverviewEntry(current.ToOverviewEntry());
            }
            _routeWorkspace.AcceptFlightLimit();
        }
        foreach (var part in plans)
        {
            var localIds = part.CheckLines.Select(l => l.Id).ToHashSet();
            part.CheckLines = editor.Lines.Where(l => l.Design != null || localIds.Contains(l.Id)).ToList();
        }
        SetRouteWorkspaceDirty(true);
        SetStatus("切割线已确认到工程，请保存航线工程 JSON。");
    }
}
