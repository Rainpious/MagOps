using System.Net;
using System.Text;

namespace MagOps.Infrastructure.Services;

/// <summary>
/// Writes a self-contained, read-only SVG/HTML view of an audit execution.
/// The view is evidence for human review; it never changes production selection.
/// </summary>
public static class AuditTrackVisualizationService
{
    private static readonly string[] Colors = ["#16803c", "#7c3aed", "#c2410c", "#0369a1", "#be123c", "#0f766e"];

    public static void Write(string path, FieldMagFolderAnalysis analysis,
        IReadOnlyList<PlannedSurveyLineTask> tasks, AuditProductionResult audit)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, Build(analysis, tasks, audit), new UTF8Encoding(true));
    }

    private static string Build(FieldMagFolderAnalysis analysis,
        IReadOnlyList<PlannedSurveyLineTask> tasks, AuditProductionResult audit)
    {
        var samples = analysis.UavSamples.Where(x => x.Longitude.HasValue && x.Latitude.HasValue)
            .OrderBy(x => x.TimestampUtc).ThenBy(x => x.SourceFile, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.SourceRecord).ToArray();
        var coordinates = samples.ToDictionary(x => (x.SourceFile, x.SourceRecord),
            x => (Lon: x.Longitude!.Value, Lat: x.Latitude!.Value));
        var all = samples.Select(x => (x.Longitude!.Value, x.Latitude!.Value)).ToArray();
        foreach (var task in tasks)
            all = all.Concat([(task.Start.Longitude, task.Start.Latitude), (task.End.Longitude, task.End.Latitude)]).ToArray();
        if (all.Length == 0) all = [(0d, 0d), (1d, 1d)];
        double minLon = all.Min(x => x.Item1), maxLon = all.Max(x => x.Item1);
        double minLat = all.Min(x => x.Item2), maxLat = all.Max(x => x.Item2);
        double lonSpan = Math.Max(maxLon - minLon, 1e-9), latSpan = Math.Max(maxLat - minLat, 1e-9);
        const double width = 1200, height = 720, margin = 42;
        string Point(double lon, double lat) =>
            $"{margin + (lon - minLon) / lonSpan * (width - 2 * margin):0.###},{height - margin - (lat - minLat) / latSpan * (height - 2 * margin):0.###}";
        static string E(string value) => WebUtility.HtmlEncode(value);

        var sb = new StringBuilder();
        sb.AppendLine("<!doctype html><html lang=\"zh-CN\"><meta charset=\"utf-8\">");
        sb.AppendLine("<title>MagOps 审计轨迹审查</title><style>body{font:14px system-ui;margin:20px;color:#17202a}svg{width:100%;height:auto;border:1px solid #ccd6dd;background:#f8fafc}.planned{stroke:#2563eb;stroke-width:2;stroke-dasharray:7 5}.track{stroke:#64748b;stroke-width:1.4;fill:none;opacity:.65}.pass{fill:none;stroke-width:4;stroke-linecap:round}.unresolved{fill:#dc2626;opacity:.8}.legend span{display:inline-block;margin:5px 14px 5px 0}.swatch{display:inline-block;width:14px;height:4px;vertical-align:middle;margin-right:4px}.warn{background:#fff7ed;border:1px solid #fdba74;padding:10px}.meta{display:grid;grid-template-columns:repeat(4,minmax(130px,1fr));gap:8px;margin:12px 0}.card{background:#f1f5f9;padding:9px;border-radius:5px}table{border-collapse:collapse;margin-top:12px;width:100%}td,th{border:1px solid #cbd5e1;padding:5px;text-align:left}</style>");
        sb.AppendLine($"<h1>审计轨迹审查</h1><div class=\"warn\">只读证据视图：图示不自动确认 Mission、测线身份、QC 或生产区间。</div>");
        sb.AppendLine($"<div class=\"meta\"><div class=\"card\">状态：<b>{E(audit.Status)}</b></div><div class=\"card\">算法：{E(audit.Execution.AlgorithmVersion)}</div><div class=\"card\">轨迹点：{samples.Length}</div><div class=\"card\">Pass：{audit.Execution.Passes.Count}</div></div>");
        sb.AppendLine("<div class=\"legend\"><span><i class=\"swatch\" style=\"background:#64748b\"></i>原始轨迹</span><span><i class=\"swatch\" style=\"background:#2563eb\"></i>规划测线</span><span><i class=\"swatch\" style=\"background:#16803c\"></i>匹配 Pass</span><span><i class=\"swatch\" style=\"background:#dc2626\"></i>未解析点</span></div>");
        sb.AppendLine($"<svg viewBox=\"0 0 {width} {height}\" role=\"img\" aria-label=\"航磁审计轨迹图\">");
        if (samples.Length > 1) sb.AppendLine($"<polyline class=\"track\" points=\"{string.Join(" ", samples.Select(x => Point(x.Longitude!.Value, x.Latitude!.Value)))}\"/>");
        foreach (var task in tasks)
        {
            string start = Point(task.Start.Longitude, task.Start.Latitude);
            string end = Point(task.End.Longitude, task.End.Latitude);
            sb.AppendLine($"<line class=\"planned\" x1=\"{start.Split(',')[0]}\" y1=\"{start.Split(',')[1]}\" x2=\"{end.Split(',')[0]}\" y2=\"{end.Split(',')[1]}\"/>");
            sb.AppendLine($"<text x=\"{start.Split(',')[0]}\" y=\"{start.Split(',')[1]}\" font-size=\"11\">{E(task.LineName)}</text>");
        }
        var matched = new HashSet<(string, int)>();
        int colorIndex = 0;
        foreach (var pass in audit.Execution.Passes)
        {
            var points = pass.Samples.Where(x => coordinates.ContainsKey((x.SourceFile, x.SourceRecord))).Select(x =>
            {
                matched.Add((x.SourceFile, x.SourceRecord));
                return coordinates[(x.SourceFile, x.SourceRecord)];
            }).ToArray();
            if (points.Length > 1)
                sb.AppendLine($"<polyline class=\"pass\" style=\"stroke:{Colors[colorIndex++ % Colors.Length]}\" points=\"{string.Join(" ", points.Select(x => Point(x.Lon, x.Lat)))}\"/>");
        }
        foreach (var sample in samples.Where(x => !matched.Contains((x.SourceFile, x.SourceRecord))))
        {
            string[] xy = Point(sample.Longitude!.Value, sample.Latitude!.Value).Split(',');
            sb.AppendLine($"<circle class=\"unresolved\" cx=\"{xy[0]}\" cy=\"{xy[1]}\" r=\"2.5\"/>");
        }
        sb.AppendLine("</svg>");
        if (audit.BlockedReasons.Count > 0)
            sb.AppendLine($"<h2>阻断与待确认事项</h2><ul>{string.Join("", audit.BlockedReasons.Select(x => $"<li>{E(x)}</li>"))}</ul>");
        sb.AppendLine("<h2>Pass 证据</h2><table><tr><th>Pass</th><th>任务</th><th>Mission</th><th>置信度</th><th>P95 横向偏差(m)</th><th>单调性</th><th>QC</th></tr>");
        foreach (var pass in audit.Execution.Passes)
            sb.AppendLine($"<tr><td>{E(pass.PassId)}</td><td>{E(pass.SurveyTaskStableId)}</td><td>{E(pass.MissionExecutionId)}</td><td>{pass.MatchConfidence:0.000}</td><td>{pass.P95CrossTrackM:0.###}</td><td>{pass.AlongTrackMonotonicity:0.000}</td><td>{E(pass.QualityStatus)}</td></tr>");
        sb.AppendLine("</table></html>");
        return sb.ToString();
    }
}
