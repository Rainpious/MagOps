using MagOps.Infrastructure.Services;

namespace MagOps.Reporting;

public sealed record QualityOffsetVertex(double Longitude, double Latitude);
public sealed record QualityOffsetReference(string Identity, string Name, QualityOffsetVertex[] Vertices,
    string Origin, string Evidence);

/// <summary>Embedded reference snapshots, never a production identity or a QC approval.</summary>
public static class QualityOffsetReferences
{
    public static QualityOffsetReference[] FromReview(AuditProductionRequest request)
    {
        var input = request.TrackSegmentation ?? throw new InvalidDataException("核验输入没有分线参考几何。");
        SegmentationParameterStore.Validate(input);
        var references = input.ReferenceGeometry.Where(g => g.Kind is "SURVEY" or "TIE").ToArray();
        if (references.Length == 0) throw new InvalidDataException("分线仅有实测推断，没有设计参考；请选择实测拟合偏离模式。");
        // Preserve individual reference segments. Never join unrelated paths by line name.
        return references.Select(g => new QualityOffsetReference(g.Id, g.Name,
            [new(g.Start.Longitude, g.Start.Latitude), new(g.End.Longitude, g.End.Latitude)],
            "SEGMENTATION_REFERENCE", $"嵌入分线参考；{g.SourcePath}；SHA256={g.SourceHash}；{g.Evidence}")).ToArray();
    }

    public static QualityOffsetReference[] FromTasks(IReadOnlyList<PlannedSurveyLineTask> tasks)
    {
        var references = tasks.Where(t => t.GeometryOrigin != "ACTUAL_TRACK" && t.GeometryOrigin != "ACTUAL_FIT" &&
            !string.IsNullOrWhiteSpace(t.SurveyTaskStableId)).Select(t => new QualityOffsetReference(t.SurveyTaskStableId, t.LineName,
                [new(t.Start.Longitude, t.Start.Latitude), new(t.End.Longitude, t.End.Latitude)],
                "SEGMENTATION_REFERENCE", "当前分线设计任务快照；" + t.GeometryOrigin)).ToArray();
        if (references.Length == 0) throw new InvalidDataException("当前分线没有独立设计参考；可选择实测拟合偏离模式。");
        return references;
    }

    internal static QualityOffsetReference Resolve(QualityOffsetReference[] references, string identity)
    {
        var matches = references.Where(r => r.Identity == identity).ToArray();
        if (matches.Length != 1) throw new InvalidDataException("该母线须明确选择唯一分线参考，不能凭同名或距离自动合并。");
        var reference = matches[0];
        if (reference.Origin != "SEGMENTATION_REFERENCE") throw new InvalidDataException("实测推断几何不能标成设计参考。");
        return reference;
    }

    internal static ReportRouteGeometry Geometry(QualityOffsetReference reference)
    {
        if (reference.Vertices is null || reference.Vertices.Length != 2 || reference.Vertices.Any(v => v is null ||
            !double.IsFinite(v.Longitude) || !double.IsFinite(v.Latitude) || v.Longitude is < -180 or > 180 || v.Latitude is <= -90 or >= 90))
            throw new InvalidDataException("嵌入参考仅支持两个有效端点的直线段，请核对分线参考。");
        var geometry = new ReportRouteGeometry { Vertices = reference.Vertices.Select(v => new ReportRouteGeometry.Vertex(v.Longitude, v.Latitude, null)).ToArray(), AltitudeMode = "clampToGround" };
        if (geometry.LengthM < .01) throw new InvalidDataException("参考线没有有效长度。");
        return geometry;
    }

    internal static QualityOffsetReference Fit(string identity, IEnumerable<(double? Lon, double? Lat)> input)
    {
        var points = input.Where(p => p.Lon is >= -180 and <= 180 && p.Lat is > -90 and < 90).Select(p => (Lon: p.Lon!.Value, Lat: p.Lat!.Value)).ToArray();
        if (points.Length < 3) throw new InvalidDataException("实测拟合至少需要3个有效坐标点。");
        double Wrap(double x) => x - 360 * Math.Floor((x + 180) / 360);
        double lon0 = points[0].Lon, lat0 = points.Average(p => p.Lat);
        double unit = Math.PI / 180 * 6371008.8, east = unit * Math.Cos(lat0 * Math.PI / 180);
        var xy = points.Select(p => (X: Wrap(p.Lon - lon0) * east, Y: (p.Lat - lat0) * unit)).ToArray();
        if (xy.Max(p => Math.Sqrt(p.X * p.X + p.Y * p.Y)) > 100000)
            throw new InvalidDataException("实测拟合仅支持100km以内局部线组，请先核对分线及范围。");
        double mx = xy.Average(p => p.X), my = xy.Average(p => p.Y);
        double xx = xy.Sum(p => (p.X - mx) * (p.X - mx)), yy = xy.Sum(p => (p.Y - my) * (p.Y - my));
        double cross = xy.Sum(p => (p.X - mx) * (p.Y - my));
        double gap = Math.Sqrt((xx - yy) * (xx - yy) + 4 * cross * cross);
        if (xx + yy < .0001 || gap <= (xx + yy) * 1e-8)
            throw new InvalidDataException("点集退化或无唯一主方向，无法拟合参考线。");
        double angle = .5 * Math.Atan2(2 * cross, xx - yy), dx = Math.Cos(angle), dy = Math.Sin(angle);
        var along = xy.Select(p => (p.X - mx) * dx + (p.Y - my) * dy).ToArray();
        // Tiny padding only avoids rounding at fitted endpoints; no extrapolation of the line.
        QualityOffsetVertex Vertex(double t) => new(Wrap(lon0 + (mx + t * dx) / east), lat0 + (my + t * dy) / unit);
        return new(identity, "实测拟合参考线", [Vertex(along.Min() - .001), Vertex(along.Max() + .001)],
            "ACTUAL_FIT", $"局部米制正交最小二乘/PCA；{points.Length}点等权，不剔除异常；拟合与统计同源，不能评估设计位置偏差");
    }
}

public sealed partial class QualityCheckService
{
    public static string OffsetBasis(QualityCheckRequest request) => request.OffsetReferenceMode switch
    {
        "ACTUAL_FIT" => "实测拟合偏离（非设计偏航距）",
        "SEGMENTATION_REFERENCE" => "相对已明确对应的嵌入分线参考",
        _ => "相对设计KML的偏航距"
    };
}
