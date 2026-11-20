using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
using MagOps.Core.Domain;
using MagOps.Infrastructure.Storage;
using static MagOps.Reporting.WordWorkfilePackage;

namespace MagOps.Reporting;

public sealed record TarmanStationPreview(string Fingerprint, string EquipmentId, string Model, string? ProfileId,
    string Specification, string ObservationEvidence, IReadOnlyList<TarmanParameter> Parameters);

public sealed partial class TarmanReport
{
    private sealed record StationParagraph(string Key, string Label, string Prefix, string TemplateText);

    private static readonly StationParagraph[] StationParagraphs =
    [
        new("instrument", "仪器介绍", "日变站", "日变站采用加拿大GEM公司生产的GSM-19T质子旋进磁力仪，记录地磁场随时间的变化，为航空磁测日变改正提供数据。仪器主要标称参数与工作模式如下："),
        new("performance", "标称性能", "1、灵敏度：", "1、灵敏度：0.15 nT（1 s采样间隔），0.05 nT（4 s采样间隔）；梯度容限大于7000 nT/m。"),
        new("sampling", "采样设置", "2、采样设置：", "2、采样设置：基点站采样间隔3～3600 s可选；移动观测等待时间3～10 s可选；步行模式采样间隔0.5～2 s可选，快速步行模式0.2～2 s可选。"),
        new("temperature", "温度特性", "3、温度漂移：", "3、温度漂移：环境温度0～−40 ℃时为0.0025 nT/℃，环境温度0～+55 ℃时为0.0018 nT/℃；工作温度为−40～+55 ℃。"),
        new("memory", "存储容量", "4、存储容量：", "4、存储容量：32 M字节。流动站可存1465623个读数，基点站可存5373951个读数，梯度测量可存1240142个读数，步行磁测可存2686975个读数。"),
        new("size", "尺寸及重量", "5、尺寸及重量：", "5、尺寸及重量：主机223 mm×69 mm×240 mm，重2.1 kg；传感器长170 mm、直径75 mm，重2.2 kg。"),
        new("mobileMode", "流动站工作模式", "6、流动站工作模式：", "6、流动站工作模式：mobile、grad模式采用手动观测，可设置3～10 s同步等待或无等待测量；walk、walkgrad模式按0.5～2 s间隔自动采样，可扩展至0.2 s。输出内容包括点线号、磁场值、信号质量和测量时间，梯度模式增加梯度值，使用内置GPS时输出测点坐标。"),
        new("baseMode", "基点站工作模式", "7、基点站工作模式：", "7、基点站工作模式：按设定间隔自动读取磁场值，输出磁场值、时间和信号质量。输入输出采用RS232接口数字输出，也可选用6针防水插头作模拟输出。"),
        new("method", "精度一致性与对时", "磁日变观测磁力仪", "磁日变观测磁力仪和航空磁力仪同精度。日变站磁力仪采样时间和空中磁力仪采样时间用GPS同步。")
    ];

    private static readonly Lazy<Dictionary<string,string>> GemStationSpecs = new(() =>
    {
        using var file = typeof(TarmanReport).Assembly.GetManifestResourceStream("MagOps.Reporting.gsm-19t-specs.json")!;
        return JsonSerializer.Deserialize<Dictionary<string,string>>(file)!;
    });

    private sealed partial class Parts
    {
        public XElement? StationHistory => History.Element(State + "baseStation");

        public List<Field> StationFields(bool compact = false)
        {
            var body = document.Element(W + "body") ?? throw new InvalidDataException("缺少工作稿正文。");
            static string TextOf(XElement p) => string.Concat(p.Descendants(W + "t").Select(t => t.Value));
            var headings = body.Elements(W + "p").Where(p => TextOf(p) == "（2）日变站仪器").ToArray();
            if (headings.Length != 1) throw new InvalidDataException("日变站仪器小节缺失或重复。");
            var following = headings[0].ElementsAfterSelf().ToList();
            int boundary = following.FindIndex(p => TextOf(p) == "三、高精度磁测工作方法");
            if (boundary < 0) throw new InvalidDataException("日变站仪器小节边界缺失。");
            var instrument = following.Take(boundary).Where(p => p.Name == W + "p").ToArray();
            var method = following.Skip(boundary + 1).SkipWhile(p => TextOf(p) != "1、日变站选址及基值").Skip(1)
                .TakeWhile(p => !TextOf(p).StartsWith("2、", StringComparison.Ordinal)).Where(p => p.Name == W + "p").ToArray();
            bool install = StationHistory == null;
            var history = StationHistory ?? new XElement(State + "baseStation");
            if (install) History.Add(history);
            bool isBrief = (string?)history.Attribute("layout") == "brief";
            var fields = new List<Field>();
            foreach (var spec in StationParagraphs)
            {
                if (isBrief && spec.Key is not ("instrument" or "method")) continue;
                string name = "station_" + spec.Key;
                string tag = "tarman." + name;
                string xpath = "/m:fields[1]/m:" + name + "[1]";
                var scope = spec.Key == "method" ? method : instrument;
                var controls = document.Descendants(W + "tag").Where(t => (string?)t.Attribute(W + "val") == tag).ToArray();
                var nodes = data.Elements(M + name).ToArray();
                XElement node, control, baseline;
                if (install)
                {
                    var paragraphs = scope.Where(p => TextOf(p).StartsWith(spec.Prefix, StringComparison.Ordinal)).ToArray();
                    if (controls.Length != 0 || nodes.Length != 0 || paragraphs.Length != 1)
                        throw new InvalidDataException(spec.Label + "的正文位置不唯一，请核对模板。");
                    var paragraph = paragraphs[0];
                    if (paragraph.Elements().Any(e => e.Name != W + "r" && e.Name != W + "pPr" && e.Name != W + "proofErr") ||
                        paragraph.Elements(W + "r").SelectMany(r => r.Elements()).Any(e => e.Name != W + "rPr" && e.Name != W + "t"))
                        throw new InvalidDataException(spec.Label + "含字段、批注或修订，请先核对该段。");
                    string original = TextOf(paragraph);
                    var runs = paragraph.Elements(W + "r").Select(r => new XElement(r)).ToArray();
                    control = new XElement(W + "sdt", new XElement(W + "sdtPr",
                        new XElement(W + "alias", new XAttribute(W + "val", spec.Label)),
                        new XElement(W + "tag", new XAttribute(W + "val", tag)),
                        new XElement(W + "dataBinding", new XAttribute(W + "prefixMappings", $"xmlns:m='{M}'"),
                            new XAttribute(W + "xpath", xpath), new XAttribute(W + "storeItemID", StoreId)),
                        new XElement(W + "text")), new XElement(W + "sdtContent", runs));
                    paragraph.Elements().Where(e => e.Name != W + "pPr").Remove();
                    paragraph.Add(control);
                    node = new XElement(M + name, original);
                    baseline = new XElement(State + spec.Key);
                    if (original != spec.TemplateText) baseline.SetAttributeValue("keep", true);
                    data.Add(node);
                    history.Add(baseline);
                }
                else
                {
                    if (controls.Length != 1 || nodes.Length != 1) throw new InvalidDataException(spec.Label + "的绑定缺失或重复。");
                    control = controls[0].Parent!.Parent!;
                    node = nodes[0];
                    var content = control.Element(W + "sdtContent");
                    var binding = controls[0].Parent!.Element(W + "dataBinding");
                    if (control.Name != W + "sdt" || !scope.Contains(control.Parent) ||
                        content == null || !content.Descendants(W + "t").Any() || TextOf(content) != node.Value ||
                        content.Descendants().Any(e => e.Name == W + "ins" || e.Name == W + "del" || e.Name == W + "commentRangeStart") ||
                        (string?)binding?.Attribute(W + "xpath") != xpath ||
                        !string.Equals((string?)binding?.Attribute(W + "storeItemID"), StoreId, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException(spec.Label + "的正文、修订或数据绑定需要核对。");
                    baseline = history.Element(State + spec.Key) ?? throw new InvalidDataException(spec.Label + "的来源记录缺失。");
                }
                fields.Add(new(spec.Key, spec.Label, node, control, baseline));
            }
            if (!compact || isBrief) return fields;
            foreach (var field in fields.Where(f => f.Key is not ("instrument" or "method")))
            {
                var paragraph = field.Control.Parent!;
                var template = StationParagraphs.Single(s => s.Key == field.Key);
                string original = (string?)field.History.Attribute("written") ?? template.TemplateText;
                bool untouched = field.Current == original && (bool?)field.History.Attribute("keep") != true;
                var content = field.Control.Element(W + "sdtContent")!;
                // A hand edit, bookmark, revision, drawing or note survives as ordinary Word content.
                bool plain = paragraph.Elements().All(e => e == field.Control || e.Name == W + "pPr" || e.Name == W + "proofErr")
                    && !paragraph.Descendants(W + "sectPr").Any()
                    && content.Elements().All(r => r.Name == W + "r" &&
                        r.Elements().All(e => e.Name == W + "t" || e.Name == W + "rPr"));
                if (untouched && plain) paragraph.Remove();
                else field.Control.ReplaceWith(content.Nodes().ToArray());
                field.Node.Remove();
                field.History.Remove();
            }
            history.SetAttributeValue("layout", "brief");
            return fields.Where(f => f.Key is "instrument" or "method").ToList();
        }
    }

    public EquipmentRecord[] BaseStations() => Devices("日变基站");

    public string? StationSelection(string path)
    {
        using var file = ReadSaved(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Read);
        return (string?)new Parts(zip, projectId, areaId).StationHistory?.Attribute("equipment");
    }

    public static string StationObservationName(string deviceId, string model) => $"日变基站观测记录（{deviceId}，{model}）";

    private TarmanStationPreview StationValues(string hash, string id, List<Field> fields)
    {
        var device = BaseStations().SingleOrDefault(e => PingquanReport.EquipmentKey(e) == id)
            ?? throw new InvalidDataException("所选日变基站已从台账移除或类别已变更。");
        string model = device.Model.Trim();
        var profile = ProjectReportProfile.Load(root).Profile.Entries.SingleOrDefault(p => p.Section == "baseStation" &&
            string.Equals(model, p.Get("model").Trim(), StringComparison.OrdinalIgnoreCase));
        var specs = profile?.Values ?? (string.Equals(model, GemStationSpecs.Value["model"], StringComparison.OrdinalIgnoreCase)
            ? GemStationSpecs.Value : new Dictionary<string,string>());
        const string pending = "待核（缺该型号已核资料）";
        string Value(string key) => specs.GetValueOrDefault(key)?.Trim() is { Length: > 0 } v ? v : pending;
        var observations = ReportInputStore.Load(root, workarea).Supplements.Where(s => s.Confirmed && s.Category == "设备" &&
            s.Name == StationObservationName(id, model) && !string.IsNullOrWhiteSpace(s.Value)).ToArray();
        string actual = observations.Length > 0
            ? string.Join("。", observations.Select(s => s.Value.Trim().TrimEnd('。')))
            : "本工区实际记录间隔待核";
        string evidence = string.Join("\n", observations.Select(s => s.Evidence + " " + s.SourcePath));
        string resolution = specs.GetValueOrDefault("resolution")?.Trim() ?? "";
        if (resolution.Length == 0)
        {
            // Older profiles and the bundled specification store resolution inside the performance entry.
            var labels = Value("performance").Split(['；', ';', '，', '。'], StringSplitOptions.TrimEntries);
            resolution = labels.FirstOrDefault(v => v.StartsWith("分辨率", StringComparison.Ordinal))
                ?["分辨率".Length..].TrimStart(' ', '：', ':') ?? "";
        }
        string summary = $"日变站采用 {model}，记录地磁场变化，供航空磁测日变改正使用。" +
            (resolution.Length == 0 ? "仪器分辨率待核。" : "仪器分辨率为 " + resolution.TrimEnd('。') + "。") +
            actual + "。";
        var values = fields.Select(f => f.Compare(f.Key == "instrument" ? summary :
            "日变站与航空磁力仪的精度一致性、两仪器的实际对时方式及同步结果待核。")).ToArray();
        return new(hash, id, model, profile?.Id, Value("source"), evidence, values);
    }

    public TarmanStationPreview PreviewStation(string path, string id)
    {
        using var file = ReadSaved(path);
        var hash = Fingerprint(file);
        using var zip = new ZipArchive(file, ZipArchiveMode.Read);
        return StationValues(hash, id, new Parts(zip, projectId, areaId).StationFields(compact: true));
    }

    public void UpdateStation(string path, TarmanStationPreview preview, string[] useSource)
    {
        using var guard = WorkspaceFileLock.AcquireForRoot(root);
        WordWorkfilePackage.Update(path, preview.Fingerprint, zip =>
        {
            var parts = new Parts(zip, projectId, areaId);
            string? previousModel = (string?)parts.StationHistory?.Attribute("model");
            var fields = parts.StationFields(compact: true);
            var fresh = StationValues(preview.Fingerprint, preview.EquipmentId, fields);
            if (fresh.Model != preview.Model || fresh.ProfileId != preview.ProfileId || fresh.Specification != preview.Specification ||
                fresh.ObservationEvidence != preview.ObservationEvidence || !fresh.Parameters.SequenceEqual(preview.Parameters))
                throw new IOException("基站台账、型号资料或工区记录已变化，请重新核对。");
            var selected = useSource.ToHashSet();
            var identity = fresh.Parameters[0];
            if (identity.Current != identity.Source && !selected.Contains(identity.Key) &&
                ((previousModel != null && !string.Equals(previousModel, fresh.Model, StringComparison.OrdinalIgnoreCase)) ||
                 !identity.Current.Contains(fresh.Model, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("仪器介绍与所选台账型号不同，请采用新型号。");
            foreach (var (f, value) in fields.Zip(fresh.Parameters)) f.Apply(value.Source, selected.Contains(f.Key));
            var history = parts.StationHistory!;
            history.SetAttributeValue("equipment", fresh.EquipmentId);
            history.SetAttributeValue("model", fresh.Model);
            history.SetAttributeValue("profile", fresh.ProfileId);
            history.SetAttributeValue("specification", fresh.Specification);
            history.SetElementValue(State + "observationEvidence", fresh.ObservationEvidence);
            parts.Save(zip);
        });
    }
}
