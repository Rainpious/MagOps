using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;
using MagOps.Core.Domain;
using MagOps.Infrastructure.Storage;
using static MagOps.Reporting.WordWorkfilePackage;

namespace MagOps.Reporting;

public sealed record TarmanSiteValue(TarmanParameter Parameter, string Evidence);
public sealed record TarmanSitePreview(string Fingerprint, IReadOnlyList<TarmanSiteValue> Values);

public sealed partial class TarmanReport
{
    private sealed record SiteColumn(string Key, string Node, string Label, string Unit);
    private static readonly SiteColumn[] SiteColumns =
    [
        new("longitude", "f39", "日变站经度", "°"), new("latitude", "f40", "日变站纬度", "°"),
        new("elevation", "f41", "日变站高程", "m"), new("t0", "f42", "日变站基本磁场值", "nT")
    ];

    private sealed partial class Parts
    {
        public XElement? SiteHistory => History.Element(State + "stationSite");

        public List<Field> SiteFields()
        {
            static string TextOf(XElement e) => string.Concat(e.Descendants(W + "t").Select(t => t.Value));
            var body = document.Element(W + "body")!;
            var headings = body.Elements(W + "p").Where(p => TextOf(p) == "1、日变站选址及基值").ToArray();
            if (headings.Length != 1) throw new InvalidDataException("日变站选址及基值标题缺失或重复。");
            var section = headings[0].ElementsAfterSelf().TakeWhile(p => !TextOf(p).StartsWith("2、")).ToHashSet();
            var tags = document.Descendants(W + "tag").ToLookup(t => (string?)t.Attribute(W + "val"));
            bool install = SiteHistory == null;
            var history = SiteHistory ?? new XElement(State + "stationSite");
            if (install) History.Add(history);
            var fields = new List<Field>();
            foreach (var col in SiteColumns)
            {
                var hits = tags["tarman." + col.Node].ToArray();
                var nodes = data.Elements(M + col.Node).ToArray();
                var control = hits.Length == 1 ? hits[0].Parent?.Parent : null;
                var binding = control?.Element(W + "sdtPr")?.Element(W + "dataBinding");
                var content = control?.Element(W + "sdtContent");
                if (nodes.Length != 1 || control?.Name != W + "sdt" || !section.Contains(control.Parent!) ||
                    (string?)binding?.Attribute(W + "xpath") != "/m:fields[1]/m:" + col.Node + "[1]" ||
                    !string.Equals((string?)binding?.Attribute(W + "storeItemID"), StoreId, StringComparison.OrdinalIgnoreCase) ||
                    content == null || !content.Descendants(W + "t").Any() || TextOf(content) != nodes[0].Value ||
                    content.Descendants().Any(e => e.Name == W + "ins" || e.Name == W + "del" || e.Name == W + "commentRangeStart"))
                    throw new InvalidDataException(col.Label + "的正文位置或绑定需要核对。");
                if (install)
                {
                    var initial = new XElement(State + col.Key);
                    if (nodes[0].Value != "«" + col.Label + "»" && nodes[0].Value != "待核")
                        initial.SetAttributeValue("keep", true);
                    history.Add(initial);
                }
                var baseline = history.Element(State + col.Key) ?? throw new InvalidDataException(col.Label + "缺少绑定记录。");
                fields.Add(new(col.Key, col.Label, nodes[0], control, baseline));
            }
            if (fields.Take(3).Select(f => f.Control.Parent).Distinct().Count() != 1 ||
                fields[3].Control.Parent == fields[0].Control.Parent ||
                !fields[0].Control.Parent!.ElementsAfterSelf().Contains(fields[3].Control.Parent))
                throw new InvalidDataException("请核对日变站坐标段和基本磁场值段的位置。");
            return fields;
        }

        public static void NormalizeSiteText(List<Field> fields)
        {
            // Replace only exact template prose and leave custom sentences, fields and formatting in place.
            var location = fields[0].Control.Parent!;
            var baseline = fields[3].Control.Parent!;
            var cross = location.ElementsAfterSelf().FirstOrDefault();
            if (cross?.Name == W + "p" && cross.ElementsAfterSelf().FirstOrDefault() == baseline)
                PlainEdit(cross.Elements(), "以日变站为中心，进行了10m长的十字剖面磁测，在2m半径范围内磁场变化小于2nT。",
                    SiteSentence)?.Invoke();
            ApplyTogether(
                PlainEdit(fields[0].Control.ElementsBeforeSelf(), "日变站选在磁场平稳地段，且开阔平坦、周围无人文干扰，工作便利，坐标为：经度: ", "日变站经度（°）："),
                PlainEdit(fields[0].Control.ElementsAfterSelf().TakeWhile(e => e != fields[1].Control), "° 纬度: ", "，纬度（°）："),
                PlainEdit(fields[1].Control.ElementsAfterSelf().TakeWhile(e => e != fields[2].Control), "° H: ", "，高程（m）："),
                PlainEdit(fields[2].Control.ElementsAfterSelf(), " m。", "。"));
            ApplyTogether(
                PlainEdit(fields[3].Control.ElementsBeforeSelf(),
                    "在日变站连续24小时观测地磁场，在地磁变化平稳时间段，通过算术平均求取日变站基本磁场值T0＝", "日变站基本磁场值 T0（nT）："),
                PlainEdit(fields[3].Control.ElementsAfterSelf(), " nT。", "。"));

            static void ApplyTogether(params Action?[] edits)
            {
                if (edits.All(e => e != null)) foreach (var edit in edits) edit!();
            }

            static Action? PlainEdit(IEnumerable<XElement> segment, string original, string replacement)
            {
                var parts = segment.ToArray();
                if (parts.Any(e => e.Name != W + "pPr" && e.Name != W + "r" && e.Name != W + "proofErr")) return null;
                var runs = parts.Where(e => e.Name == W + "r").ToArray();
                if (runs.SelectMany(r => r.Elements()).Any(e => e.Name != W + "rPr" && e.Name != W + "t")) return null;
                var texts = runs.SelectMany(r => r.Elements(W + "t")).ToArray();
                if (string.Concat(texts.Select(t => t.Value)) != original) return null;
                return () =>
                {
                    texts[0].Value = replacement;
                    foreach (var t in texts.Skip(1)) t.Value = "";
                };
            }
        }
    }

    private (BaseStationLogRecord? Log, string Evidence) ReviewedSiteLog(ReportInputs inputs)
    {
        const string prefix = "base_station_logs.json#";
        var chosen = inputs.SharedItems.Where(s => s.Category == "基站" && s.Source.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        if (!inputs.SharedSelectionReviewed || chosen.Length == 0) return (null, "本工区未选定基站日志");
        if (chosen.Select(s => s.Source).Distinct().Count() != 1) return (null, "多份基站日志待核对采用站点");
        var live = ReportSharedSources.Read(root, workarea, areaId).ToLookup(s => s.Key);
        var store = new JsonStore();
        var batches = store.Read(ProjectPaths.Json(root, "flight_batches.json"), new List<FlightBatchRecord>())
            .ToLookup(b => "flight/" + b.BatchId);
        var logs = store.Read(ProjectPaths.Json(root, "base_station_logs.json"), new List<BaseStationLogRecord>());
        string reference = chosen[0].Source;
        var rows = logs.Where(s => s.RecordId == reference[prefix.Length..]).ToArray();
        if (rows.Length != 1 || string.IsNullOrWhiteSpace(rows[0].StationId)) return (null, "基站日志缺失、重复或未填站号");
        var log = rows[0];
        foreach (var item in chosen)
        {
            var current = live[item.Key].ToArray();
            if (item.RetainedAfterLoss || current.Length != 1 || current[0].Source != item.Source ||
                current[0].Value != item.Value || current[0].Label != item.Label)
                return (null, "所选基站日志或工区关联已变化，请重新核对");
            var parents = batches[current[0].Parent].ToArray();
            if (parents.Length != 1 || parents[0].Date != log.Date ||
                !string.Equals(parents[0].BaseStation, log.StationId, StringComparison.OrdinalIgnoreCase))
                return (null, "基站日志与工区飞行批次的站号或日期不一致");
        }
        return (log, (reference + " " + log.Crs).Trim());
    }

    private List<TarmanSiteValue> SiteValues(List<Field> fields)
    {
        var inputs = ReportInputStore.Load(root, workarea);
        var (log, logSource) = ReviewedSiteLog(inputs);
        var values = new List<TarmanSiteValue>();
        foreach (var (col, field) in SiteColumns.Zip(fields))
        {
            var entries = inputs.Supplements.Where(s => s.Confirmed && s.Category == "基站" && s.Name.Trim() == col.Label).ToArray();
            string? text = null;
            string evidence;
            if (entries.Length == 1)
            {
                var row = entries[0];
                evidence = string.Join(" ", new[] { row.Evidence, row.SourcePath }.Where(s => !string.IsNullOrWhiteSpace(s)));
                if (evidence.Length == 0) evidence = "已确认补录缺少来源说明";
                else if (row.Unit.Trim() is "" || row.Unit.Trim().Equals(col.Unit, StringComparison.OrdinalIgnoreCase) ||
                    (col.Unit == "°" && row.Unit.Trim() == "度") || (col.Unit == "m" && row.Unit.Trim() == "米"))
                    text = row.Value;
                else evidence += "（单位需核对）";
            }
            else if (entries.Length > 1) evidence = "本工区有多条已确认的同名补录，请核对采用值";
            else if (col.Key == "t0") evidence = "本工区缺少已核定的站点基本磁场值";
            else
            {
                evidence = logSource;
                text = col.Key switch { "longitude" => log?.Longitude, "latitude" => log?.Latitude, _ => log?.ElevationM };
            }
            string value = "待核";
            if (!string.IsNullOrWhiteSpace(text))
            {
                if (decimal.TryParse(text.Trim(), NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture, out var number) && col.Key switch
                    { "longitude" => number is >= -180 and <= 180, "latitude" => number is >= -90 and <= 90, "t0" => number > 0, _ => true })
                    value = text.Trim();
                else evidence += "（数值或范围需核对）";
            }
            values.Add(new(field.Compare(value), evidence));
        }
        return values;
    }

    public TarmanSitePreview PreviewSite(string path)
    {
        using var file = ReadSaved(path);
        var hash = Fingerprint(file);
        using var zip = new ZipArchive(file, ZipArchiveMode.Read);
        return new(hash, SiteValues(new Parts(zip, projectId, areaId).SiteFields()));
    }

    public void UpdateSite(string path, TarmanSitePreview preview, string[] useSource)
    {
        using var guard = WorkspaceFileLock.AcquireForRoot(root);
        WordWorkfilePackage.Update(path, preview.Fingerprint, zip =>
        {
            var parts = new Parts(zip, projectId, areaId);
            bool migrateTemplate = parts.SiteHistory == null;
            var fields = parts.SiteFields();
            var fresh = SiteValues(fields);
            if (!fresh.SequenceEqual(preview.Values)) throw new IOException("站点来源已变化，请重新核对。");
            var selected = useSource.ToHashSet();
            foreach (var (field, source) in fields.Zip(fresh))
            {
                field.Apply(source.Parameter.Source, selected.Contains(field.Key));
                field.History.SetAttributeValue("sourceRef", source.Evidence);
            }
            if (migrateTemplate) Parts.NormalizeSiteText(fields);
            parts.Save(zip);
        });
    }
}
