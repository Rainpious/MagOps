using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using MagOps.Core.Coordinates;
using MagOps.Core.Routes;
using MagOps.Core.SurveyPlanning;

namespace MagOps.Infrastructure.Services;

public static class TieLineExport
{
    public static readonly string[] Files = ["check-lines.json", "check-lines.kml", "check-lines.csv", "check-coverage.csv", "check-crossings.csv", "check-sorties.csv", "check-sorties.kml"];
    public static void Write(string directory, TieLineWorkspace context, IReadOnlyList<RouteCheckLine> lines, string status)
    {
        RouteCheckLines.Validate(lines);
        Directory.CreateDirectory(directory);
        var coverage = TieLineDesign.Coverage(context.Mains, lines, context.Version);
        var active = lines.Where(l => l.Design?.Version == context.Version).Select(l => l.Design!).ToArray();
        var sorties = TieFlightPlanner.Build(lines, context.Version, context.MaxSortieM, context.Bearing);
        var totals = new { insideM = active.Sum(d => d.InsideM), extensionM = active.Sum(d => d.ExtensionM), totalM = active.Sum(d => d.TotalM) };
        var json = new
        {
            schemaVersion = 4,
            mode = "projected",
            centralMeridian = context.Meridian,
            context.Datum,
            context.Version,
            context.BoundaryPath,
            context.PendingPartitions,
            status,
            defaultStandard = TieLineDesign.IndustryStandard,
            standards = active.Select(d => d.Standard).Distinct(),
            productionEligible = false,
            lines,
            coverage,
            totals,
            projectMaxSortieM = context.MaxSortieM,
            sorties
        };
        File.WriteAllText(Path.Combine(directory, Files[0]), JsonSerializer.Serialize(json, new JsonSerializerOptions { WriteIndented = true }));
        XNamespace ns = "http://www.opengis.net/kml/2.2"; var doc = new XElement(ns + "Document");
        using var details = Csv(Files[2]); using var net = Csv(Files[3]); using var hits = Csv(Files[4]);
        Row(details, "ID", "线号", "状态", "行类型", "段号", "区内_m", "起端外延_m", "末端外延_m", "外延_m", "总长_m", "外延检查");
        Row(net, "设计版本", "主测线ID", "测线", "不同切割线数", "覆盖检查");
        Row(hits, "切割线ID", "主测线ID", "任务ID", "东坐标_m", "北坐标_m");
        foreach (var line in lines)
        {
            var placemark = new XElement(ns + "Placemark", new XAttribute("id", line.Id), new XElement(ns + "name", line.Name)); doc.Add(placemark);
            if (line.Design is not { } d)
            {
                var pts = new[] { line.Start, line.End }.Select(p => RouteCoordinateMath.ToLonLat(p, line.CoordinateMode, line.CentralMeridian));
                placemark.Add(PathGeometry(pts)); Row(details, line.Id, line.Name, "旧版设计待复核", "线", "", "", "", "", "", "", ""); continue;
            }
            string state = d.Version == context.Version ? status : "来源已变待复核";
            var geom = new XElement(ns + "MultiGeometry"); placemark.Add(new XElement(ns + "description", state + " · " + d.Standard + " " + d.DesignNote), geom);
            var projection = new ProjectionParameters(d.Datum, d.Meridian);
            foreach (var seg in d.Inside.Prepend(d.Approach).Append(d.Departure).Where(s => s.Length > 0))
                geom.Add(PathGeometry(new[] { seg.Start, seg.End }.Select(p => CoordinateProjectionService.Inverse(p.X, p.Y, projection))));
            Row(details, line.Id, line.Name, state, "线", "", d.InsideM, d.Approach.Length, d.Departure.Length, d.ExtensionM, d.TotalM, d.ExtensionsRecommended ? "两端均在3～5倍" : "外延需复核");
            for (int i = 0; i < d.Inside.Length; i++) Row(details, line.Id, line.Name, state, "区内段", i + 1, d.Inside[i].Length, "", "", "", "", "");
            foreach (var h in d.Crossings) Row(hits, line.Id, h.LineId, h.TaskId, h.Point.X, h.Point.Y);
        }
        Row(details, "", "本版切割线合计", status, "合计", "", totals.insideM, active.Sum(d => d.Approach.Length), active.Sum(d => d.Departure.Length), totals.extensionM, totals.totalM, "");
        foreach (var main in context.Mains.DistinctBy(m => m.LineId).Where(m => coverage.ContainsKey(m.LineId)))
            Row(net, context.Version, main.LineId, main.Label, coverage[main.LineId], coverage[main.LineId] >= 2 ? "满足" : "少于2条");
        new XDocument(new XElement(ns + "kml", doc)).Save(Path.Combine(directory, Files[1]));
        using var flightCsv = Csv(Files[5]);
        Row(flightCsv, "架次", "配对母线", "母线ID", "母线号", "执行段ID", "段序", "母线段数", "起始比例", "结束比例", "起点E", "起点N", "终点E", "终点N", "区内_m", "外延_m", "段航程_m", "线间连接_m", "末端直返回程_m", "额外往返预留_m", "架次总航程_m", "工程上限_m", "有效上限_m", "估算_min", "建议起降E", "建议起降N", "起降依据", "起点接入_m", "配对状态", "预计飞行航程_m", "作业KML航程_m");
        var flightKml = new XElement(ns + "Document");
        foreach (var sortie in sorties)
        {
            var distance = context.MeasureWorkFlight(sortie);
            var path = new List<(double Lon, double Lat)>();
            var crs = new ProjectionParameters(context.Datum, context.Meridian);
            var home = CoordinateProjectionService.Inverse(sortie.LaunchRecovery.X, sortie.LaunchRecovery.Y, crs);
            path.Add(home);
            foreach (var leg in sortie.Legs)
            {
                path.Add(CoordinateProjectionService.Inverse(leg.Start.X, leg.Start.Y, crs));
                path.Add(CoordinateProjectionService.Inverse(leg.End.X, leg.End.Y, crs));
                Row(flightCsv, sortie.Name, sortie.Pair, leg.MotherId, leg.MotherName, leg.SegmentId, leg.Sequence, leg.Pieces,
                    leg.FromFraction, leg.ToFraction, leg.Start.X, leg.Start.Y, leg.End.X, leg.End.Y, leg.InsideM, leg.ExtensionM, leg.FlightM,
                    sortie.ConnectionM, sortie.ReturnM, sortie.Budget.AccessAllowanceM, sortie.TotalM, sortie.ProjectMaxM, sortie.LimitM, sortie.Minutes, sortie.LaunchRecovery.X, sortie.LaunchRecovery.Y, sortie.LaunchBasis, sortie.OutboundM, sortie.Pairing, distance.FlightM, distance.WorkM);
            }
            path.Add(home);
            flightKml.Add(new XElement(ns + "Placemark", new XElement(ns + "name", sortie.Name + " 建议起降点"),
                new XElement(ns + "description", sortie.LaunchBasis), new XElement(ns + "Point", new XElement(ns + "coordinates",
                FormattableString.Invariant($"{home.Longitude:R},{home.Latitude:R},0")))));
            flightKml.Add(new XElement(ns + "Placemark", new XElement(ns + "name", sortie.Name + " " + sortie.Pair),
                new XElement(ns + "description", "建议同点起降，航程含接入、执行段、线间连接和返航，另加场地往返预留。"), PathGeometry(path)));
        }
        new XDocument(new XElement(ns + "kml", flightKml)).Save(Path.Combine(directory, Files[6]));

        XElement PathGeometry(IEnumerable<(double Lon, double Lat)> pts) => new(ns + "LineString", new XElement(ns + "coordinates",
            string.Join(" ", pts.Select(p => FormattableString.Invariant($"{p.Lon:R},{p.Lat:R},0")))));
        StreamWriter Csv(string name) => new(Path.Combine(directory, name), false, new UTF8Encoding(true));
    }
    private static void Row(StreamWriter file, params object[] values) =>
        file.WriteLine(string.Join(",", values.Select(x => "\"" + (Convert.ToString(x, CultureInfo.InvariantCulture) ?? "").Replace("\"", "\"\"") + "\"")));
}
