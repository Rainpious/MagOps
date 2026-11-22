using MagOps.Core.Coordinates;
using MagOps.Core.Routes;
using MagOps.Infrastructure.Services;

namespace MagOps.Desktop;

public sealed partial class MainForm
{
    private void BindCoordinateBoundary()
    {
        RequireBoundaryWorkarea();
        var projection = RequireCurrentCoordinateProjection();
        var points = _lastCoordinateBatch!.Results.Select(p => new GeoCoordinate(p.Longitude, p.Latitude));
        BindBoundaryPoints(points, projection, "坐标拐点.kml");
        SetStatus("当前点序已保存为工区边界。");
    }

    private void RequireBoundaryWorkarea()
    {
        if (!IsProjectInitialized() || string.IsNullOrEmpty(CurrentWorkareaRelativePath))
            throw new InvalidOperationException("请先选择项目和工区。");
    }

    private void BindBoundaryPoints(IEnumerable<GeoCoordinate> points, ProjectionParameters projection, string sourceName)
    {
        string staging = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".kml");
        try
        {
            _kmlRangeExporter.WriteRange(staging, points, _kmlRangeExporter.LoadBuiltInFormat(KmlRangeExportService.SkyBlueTemplateKey),
                new(_workspace.GetWorkspaceReadOnly(Root).DisplayName, CurrentWorkareaId, sourceName, "工区边界", projection.CentralMeridian));
            _workspace.BindWorkareaBoundary(Root, CurrentWorkareaRelativePath, staging, projection, sourceName);
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }

    private void BindRouteBoundary(string path)
    {
        RequireBoundaryWorkarea();
        if (!File.Exists(path)) throw new InvalidOperationException("请先载入工区边界文件。");
        double? cm = _workspace.ReadWorkareaSpatialContext(Root, CurrentWorkareaRelativePath).CentralMeridian;
        if (Path.GetExtension(path).Equals(".kml", StringComparison.OrdinalIgnoreCase))
        {
            if (IsKnownPartitionKml(path) || _kmlSurveySubdivision.TryResolveGeneratedChild(path, out _))
                throw new InvalidDataException("请选择完整工区边界，当前文件是子分区。");
            if (cm is null)
            {
                var longitudes = _kmlSurveySubdivision.ReadRegionPolygons(path).SelectMany(p => p.OuterRing)
                    .Select(p => p.Longitude).Distinct().Order().ToArray();
                var zones = longitudes.Select(CoordinateService.SuggestGk3CentralMeridian).Distinct().ToArray();
                if (zones.Length != 1)
                    throw new InvalidDataException($"边界跨越多个三度带（{string.Join("、", zones)}°），请先在顶部确认本工区中央子午线。");
                cm = zones[0];
            }
            _workspace.BindWorkareaBoundary(Root, CurrentWorkareaRelativePath, path, new("WGS84", cm));
        }
        else if (Path.GetExtension(path).Equals(".ply", StringComparison.OrdinalIgnoreCase))
        {
            if (cm is null) throw new InvalidOperationException("请先确认用于反算 PLY 的工区中央子午线。");
            var points = _plySurveyLines.ReadBoundary(path).Select(p =>
            {
                var en = RouteCoordinateMath.ResolveProjectedEN(p.X, p.Y);
                var ll = RouteCoordinateMath.GkToWgs84(en.E, en.N, cm.Value);
                return new GeoCoordinate(ll.Lon, ll.Lat);
            });
            BindBoundaryPoints(points, new("WGS84", cm), Path.GetFileName(path));
        }
        else throw new InvalidDataException("请选择工区面 KML 或高斯三度带 PLY。");
        RefreshProject();
        SetStatus("已保存当前工区边界：" + Path.GetFileName(path));
    }
}
