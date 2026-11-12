using MagOps.Core.Coordinates;
using MagOps.Core.SurveyPlanning;

namespace MagOps.Infrastructure.Services;

public sealed record BoundaryVertex(int Polygon, int Ring, int Index, double Longitude, double Latitude, double Easting, double Northing);
public sealed record BoundaryMeasurement(double AreaSquareMetres, IReadOnlyList<BoundaryVertex> Vertices)
{
    public double CentroidEasting { get; init; }
    public double CentroidNorthing { get; init; }
}

/// <summary>Confirmed design polygons only; never estimates completed area from flight tracks.</summary>
public static class SpatialBoundaryMeasurementService
{
    public static BoundaryMeasurement MeasureKml(string path, ProjectionParameters projection, bool requireCompleteRings = false)
    {
        projection = CoordinateProjectionService.Normalize(projection);
        if (projection.CentralMeridian is not { } meridian || !double.IsFinite(meridian) || meridian is < -180 or > 180)
            throw new InvalidDataException("边界面积需要明确的投影中央经线。");
        var polygons = SpatialGeometryService.ParseKmlPolygons(path, requireCompleteRings);
        if (polygons.Count == 0) throw new InvalidDataException("边界KML没有有效Polygon，不能用航线或点范围代替设计边界。");
        var vertices = new List<BoundaryVertex>();
        var outerRegions = new List<SurveyRegionGeometry>();
        double totalArea = 0;
        double weightedE = 0, weightedN = 0;
        double? originE = null, originN = null;
        for (int polygon = 0; polygon < polygons.Count; polygon++)
        {
            var item = polygons[polygon];
            List<CoordinateConversionResult> Convert(IReadOnlyList<GeoCoordinate> ring, int ringIndex)
            {
                var closed = ring.ToList();
                if (closed.Count > 0 && closed[0] != closed[^1]) closed.Add(closed[0]);
                var audit = SpatialGeometryService.AuditGeometry(new() { Type = "Polygon", Coordinates = closed });
                if (audit.Errors.Count > 0 || audit.SelfIntersections > 0 || audit.DuplicatePoints > 0)
                    throw new InvalidDataException("边界环未闭合、重复、自交或含无效坐标，请先在坐标与空间修复。");
                if (projection.Kind == CoordinateProjectionKind.GaussKruger && ring.Any(p => Math.Abs(p.Longitude - meridian) > 3)) throw new InvalidDataException("边界超出本次高斯投影适用范围，请重新确认投影基准。");
                var output = closed.Take(closed.Count - 1).Select(p =>
                {
                    var (x, y) = CoordinateProjectionService.Forward(p.Longitude, p.Latitude, projection);
                    return new CoordinateConversionResult(p.Longitude, p.Latitude, "", "", "", "", x, y, x, y, "Easting/Northing", meridian, projection.Datum, ProjectionKind: projection.Kind);
                }).ToList();
                for (int i = 0; i < output.Count; i++) vertices.Add(new(polygon + 1, ringIndex, i + 1, output[i].Longitude, output[i].Latitude, output[i].Easting, output[i].Northing));
                return output;
            }
            var outer = Convert(item.OuterRing, 0);
            double area = CoordinateGeometryMath.PolygonAreaSquareMeters(outer);
            originE ??= outer[0].Easting;
            originN ??= outer[0].Northing;
            void Accumulate(IReadOnlyList<CoordinateConversionResult> ring, double signedArea)
            {
                var (e, n) = RingCentroid(ring);
                weightedE += signedArea * (e - originE.Value);
                weightedN += signedArea * (n - originN.Value);
            }
            Accumulate(outer, area);
            var outerRing = outer.Select(p => new SurveyBoundaryPoint(p.Easting, p.Northing)).ToArray();
            var region = SurveyRegionGeometry.FromSinglePolygon(outerRing);
            foreach (var other in outerRegions)
                if (RingsIntersect(outerRing, other.Polygons[0].OuterRing) || SurveyRegionGeometryMath.ContainsOrOnBoundary(other, outerRing[0]) ||
                    SurveyRegionGeometryMath.ContainsOrOnBoundary(region, other.Polygons[0].OuterRing[0]))
                    throw new InvalidDataException("多区域边界相交或重叠，请先明确有效边界，不能简单叠加面积。");
            outerRegions.Add(region);
            var holes = new List<IReadOnlyList<SurveyBoundaryPoint>>();
            for (int i = 0; i < item.InnerRings.Count; i++)
            {
                var converted = Convert(item.InnerRings[i], i + 1);
                var hole = converted.Select(p => new SurveyBoundaryPoint(p.Easting, p.Northing)).ToArray();
                if (hole.Any(p => !SurveyRegionGeometryMath.ContainsOrOnBoundary(region, p)) || RingsIntersect(outerRing, hole))
                    throw new InvalidDataException("孔洞不在外环内部或与外环相交，请先修复边界。");
                foreach (var other in holes)
                    if (RingsIntersect(hole, other) || SurveyRegionGeometryMath.ContainsOrOnBoundary(SurveyRegionGeometry.FromSinglePolygon(other), hole[0]) ||
                        SurveyRegionGeometryMath.ContainsOrOnBoundary(SurveyRegionGeometry.FromSinglePolygon(hole), other[0]))
                        throw new InvalidDataException("孔洞相交或嵌套，不能重复扣除面积。");
                double holeArea = CoordinateGeometryMath.PolygonAreaSquareMeters(converted);
                Accumulate(converted, -holeArea);
                holes.Add(hole); area -= holeArea;
            }
            if (!double.IsFinite(area) || area <= 0) throw new InvalidDataException("扣除孔洞后的设计面积无效。");
            totalArea += area;
        }
        double centroidE = originE!.Value + weightedE / totalArea;
        double centroidN = originN!.Value + weightedN / totalArea;
        if (!double.IsFinite(centroidE) || !double.IsFinite(centroidN))
            throw new InvalidDataException("边界面积重心无效，请检查投影和边界。");
        return new(totalArea, vertices) { CentroidEasting = centroidE, CentroidNorthing = centroidN };
    }

    private static (double Easting, double Northing) RingCentroid(IReadOnlyList<CoordinateConversionResult> ring)
    {
        double e0 = ring[0].Easting, n0 = ring[0].Northing;
        double area2 = 0, eSum = 0, nSum = 0;
        for (int i = 0; i < ring.Count; i++)
        {
            var p = ring[i]; var q = ring[(i + 1) % ring.Count];
            double pe = p.Easting - e0, pn = p.Northing - n0;
            double qe = q.Easting - e0, qn = q.Northing - n0;
            double cross = pe * qn - qe * pn;
            area2 += cross; eSum += (pe + qe) * cross; nSum += (pn + qn) * cross;
        }
        if (!double.IsFinite(area2) || Math.Abs(area2) < 1e-6)
            throw new InvalidDataException("边界环退化，不能计算面积重心。");
        return (e0 + eSum / (3 * area2), n0 + nSum / (3 * area2));
    }

    private static bool RingsIntersect(IReadOnlyList<SurveyBoundaryPoint> a, IReadOnlyList<SurveyBoundaryPoint> b)
    {
        double Cross(SurveyBoundaryPoint p, SurveyBoundaryPoint q, SurveyBoundaryPoint r) => (q.X - p.X) * (r.Y - p.Y) - (q.Y - p.Y) * (r.X - p.X);
        for (int i = 0; i < a.Count; i++)
            for (int j = 0; j < b.Count; j++)
            {
                var p = a[i]; var q = a[(i + 1) % a.Count]; var r = b[j]; var s = b[(j + 1) % b.Count];
                if (Math.Max(Math.Min(p.X, q.X), Math.Min(r.X, s.X)) > Math.Min(Math.Max(p.X, q.X), Math.Max(r.X, s.X)) + 1e-7 ||
                    Math.Max(Math.Min(p.Y, q.Y), Math.Min(r.Y, s.Y)) > Math.Min(Math.Max(p.Y, q.Y), Math.Max(r.Y, s.Y)) + 1e-7) continue;
                if (Cross(p, q, r) * Cross(p, q, s) <= 0 && Cross(r, s, p) * Cross(r, s, q) <= 0) return true;
            }
        return false;
    }
}
