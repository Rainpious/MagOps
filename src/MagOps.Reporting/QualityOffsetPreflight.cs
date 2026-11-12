using MagOps.Core.Analysis;

namespace MagOps.Reporting;

public sealed record QualityOffsetRouteCheck(string Identity, string Status, string Message);

public sealed partial class QualityCheckService
{
    /// <summary>Read-only design checks usable before calculation; issues affect only their line.</summary>
    public static QualityOffsetRouteCheck[] CheckOffsetRoutes(QualityCheckRequest request, TextTable table)
    {
        if (request.OffsetReferenceMode != "DESIGN_KML")
            return table.Rows.Skip(request.FirstRecord - 1).Take((request.LastRecord ?? table.Rows.Count) - request.FirstRecord + 1).Select(r => QualitySpatialIdentity.Key(r, table.Headers.Length, request.Columns)).Distinct()
                .Select(id => new QualityOffsetRouteCheck(id,
                    request.OffsetReferenceMode == "ACTUAL_FIT" || request.OffsetReferences.Count(r => r.Identity == id) == 1 ? "READY" : "UNMAPPED",
                    request.OffsetReferenceMode == "ACTUAL_FIT" ? "计算时按当前范围拟合实测参考线，非设计偏航" : "请逐母线明确选择分线参考")).ToArray();
        var catalog = QualityRouteCatalog.Read(request.KmlPath);
        var identities = table.Rows.Skip(request.FirstRecord - 1).Take((request.LastRecord ?? table.Rows.Count) - request.FirstRecord + 1)
            .Select(row => QualitySpatialIdentity.Key(row, table.Headers.Length, request.Columns)).Distinct(StringComparer.Ordinal).ToArray();
        var mappings = QualityRouteCatalog.Suggest(catalog, identities).ToDictionary(m => m.Segment, StringComparer.Ordinal);
        foreach (var group in request.Routes.GroupBy(r => r.Segment, StringComparer.Ordinal))
            if (group.Count() == 1) mappings[group.Key] = group.Single();
        var duplicates = request.Routes.GroupBy(r => r.Segment, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        return identities.Select(identity =>
        {
            if (identity.Length == 0) return new QualityOffsetRouteCheck(identity, "UNMAPPED", "母线身份缺失");
            if (duplicates.Contains(identity)) return new QualityOffsetRouteCheck(identity, "UNMAPPED", "对应重复，请保留一条");
            if (!mappings.TryGetValue(identity, out var mapping) || mapping.KmlPath.Length == 0 && mapping.Placemark.Length == 0)
                return new QualityOffsetRouteCheck(identity, "UNMAPPED", "没有唯一设计对应，可查找空间候选");
            var candidates = catalog.Entries.Where(e => (mapping.KmlPath.Length == 0 || Path.GetFullPath(mapping.KmlPath).Equals(e.Path, StringComparison.OrdinalIgnoreCase)) &&
                (mapping.Placemark.Length == 0 || e.Placemark == mapping.Placemark)).ToArray();
            if (candidates.Length != 1) return new QualityOffsetRouteCheck(identity, "UNMAPPED", "设计线缺失或重名，请指定文件和线名");
            try
            {
                var geometry = ReportRouteGeometry.Read(candidates[0].Path, candidates[0].Placemark);
                return !geometry.IsStraight && !mapping.GeometryConfirmed
                    ? new QualityOffsetRouteCheck(identity, "GEOMETRY_UNCONFIRMED", "设计路径含弯折/折返，请先核对母线范围")
                    : new QualityOffsetRouteCheck(identity, "READY", "设计对应已具备；点级可计算范围在计算后列示");
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException or System.Xml.XmlException)
            { return new QualityOffsetRouteCheck(identity, "INVALID_GEOMETRY", ex.Message); }
        }).ToArray();
    }
}
