using System.IO.Compression;
using System.Xml.Linq;
using MagOps.Core.Domain;
using MagOps.Infrastructure.Services;
using MagOps.Infrastructure.Storage;
using static MagOps.Reporting.WordWorkfilePackage;

namespace MagOps.Reporting;

public sealed record TarmanParameter(string Key, string Label, string Previous, string Current, string Source, bool Suggested);
public sealed record TarmanPreview(string Fingerprint, string PlatformId, IReadOnlyList<TarmanParameter> Parameters);

public sealed record TarmanProbePreview(string Fingerprint, string EquipmentId, string Model, string? ProfileId, IReadOnlyList<TarmanParameter> Parameters);

public sealed partial class TarmanReport
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace M = "urn:nolva:magops:tarman:review:1";
    private static readonly XNamespace State = "urn:magops:tarman:workfile:1";
    private const string StoreId = "{F84089D5-A1CA-4D9B-80EE-444BACF22E1C}";
    private static readonly (string Key, string Node, string Label)[] Bindings =
    [
        ("model", "f89", "飞行平台型号"), ("positioning", "f90", "定位配置"),
        ("speed", "f91", "最大飞行速度"), ("endurance", "f92", "标称续航时间"),
        ("wind", "f93", "抗风能力"), ("range", "f94", "通信距离"), ("payload", "f95", "载荷")
    ];

    private static readonly (string Key, string Node, string Label)[] ProbeBindings =
    [
        ("type", "f96", "探头类别"), ("atom", "f97", "原子种类"), ("sensitivity", "f98", "灵敏度"),
        ("sampleRate", "f99", "最高采样率"), ("range", "f100", "量程"), ("gradient", "f101", "梯度容限"),
        ("deadZone", "f102", "死区与航向误差"), ("temperature", "f103", "工作温度"),
        ("recorder", "f104", "收录器"), ("power", "f105", "收录器供电"),
        ("positioning", "f106", "定位与姿态"), ("distance", "f107", "无线传输距离")
    ];

    private readonly string root, projectId, areaId, workarea;
    public string DirectoryPath { get; }

    public TarmanReport(string root, string area, string areaId)
    {
        this.root = root;
        workarea = area;
        this.areaId = areaId;
        projectId = Context(root, area, areaId).ProjectId;
        DirectoryPath = ReportInputStore.DirectoryFor(root, area);
    }

    public ProfileEntry[] Platforms() =>
        ProjectReportProfile.Load(root).Profile.Entries.Where(e => e.Section == "platform").ToArray();

    private ProfileEntry Platform(string id) => Platforms().SingleOrDefault(p => p.Id == id)
        ?? throw new InvalidDataException("平台型号资料缺失，请在出发整备的报告资料字典中核对。");

    private sealed record Field(string Key, string Label, XElement Node, XElement Control, XElement History)
    {
        public string Current => Node.Value;
        public string Source(ProfileEntry platform) => platform.Get(Key).Trim() is { Length: > 0 } value ? value : "待补录";
        public TarmanParameter Compare(string source)
        {
            source = ReportText(Node.Name.LocalName, source);
            string previous = ReportText(Node.Name.LocalName, (string?)History.Attribute("source") ?? "");
            bool manual = (History.Attribute("written") is { } written && Current != written.Value) ||
                (bool?)History.Attribute("keep") == true;
            return new(Key, Label, previous, Current, source, Current != source && (Key == "model" || !manual));
        }

        public void Apply(string source, bool useSource)
        {
            History.SetAttributeValue("source", source);
            source = ReportText(Node.Name.LocalName, source);
            if (!useSource && Current != source)
            {
                History.SetAttributeValue("keep", true);
                return;
            }
            History.SetAttributeValue("written", source);
            History.Attribute("keep")?.Remove();
            if (Current == source) return;
            Node.Value = source;
            var texts = Control.Element(W + "sdtContent")!.Descendants(W + "t").ToArray();
            texts[0].Value = source;
            foreach (var t in texts.Skip(1)) t.Value = "";
            Control.Element(W + "sdtPr")!.Element(W + "showingPlcHdr")?.Remove();
        }
    }

    private sealed partial class Parts
    {
        private readonly XElement document, data;
        private readonly string dataPath;
        public XElement History { get; }
        public List<Field> Fields { get; } = [];
        public XElement? ProbeHistory => History.Element(State + "probe");

        public Parts(ZipArchive zip, string project, string area, bool create = false, bool probe = false)
        {
            document = Xml(zip, "word/document.xml");
            var mapped = zip.Entries.Where(e => e.FullName.StartsWith("customXml/item", StringComparison.Ordinal)
                && !e.Name.Contains("Props") && e.Name.EndsWith(".xml"))
                .Select(e => (e.FullName, Root: Xml(zip, e.FullName))).SingleOrDefault(p => p.Root.Name == M + "fields");
            data = mapped.Root ?? throw new InvalidDataException("文件缺少塔尔曼审阅模板的字段数据。");
            dataPath = mapped.FullName;
            // Word can renumber custom XML parts when saving. Follow the properties relationship to verify its identity.
            var partUri = new Uri("http://package/" + dataPath);
            var rels = Xml(zip, "customXml/_rels/" + Path.GetFileName(dataPath) + ".rels");
            var props = rels.Elements().Single(r => ((string?)r.Attribute("Type"))?.EndsWith("/customXmlProps") == true);
            string propsPath = new Uri(partUri, (string)props.Attribute("Target")!).AbsolutePath.TrimStart('/');
            if (!Xml(zip, propsPath).Attributes().Any(a => a.Name.LocalName == "itemID" &&
                string.Equals(a.Value, StoreId, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("塔尔曼模板的字段数据标识不匹配。");

            if (create)
            {
                if (data.Element(State + "work") != null) throw new InvalidDataException("请选择原始塔尔曼模板。");
                History = new XElement(State + "work", new XAttribute("project", project), new XAttribute("area", area));
                data.Add(History);
            }
            else
            {
                History = data.Element(State + "work") ?? throw new InvalidDataException("请选择由 MagOps 新建的塔尔曼工作稿。");
                if ((string?)History.Attribute("project") != project || (string?)History.Attribute("area") != area)
                    throw new InvalidDataException("工作稿与当前项目、工区不一致。");
            }

            XElement fieldHistory = History;
            bool initializeFields = create;
            if (probe)
            {
                initializeFields = ProbeHistory == null;
                fieldHistory = ProbeHistory ?? new XElement(State + "probe");
                if (initializeFields) History.Add(fieldHistory);
            }
            var controls = document.Descendants(W + "sdt").ToLookup(c =>
                (string?)c.Element(W + "sdtPr")?.Element(W + "tag")?.Attribute(W + "val"));
            XElement? table = null;
            foreach (var (key, nodeName, label) in probe ? ProbeBindings : Bindings)
            {
                var matches = controls["tarman." + nodeName].ToArray();
                var nodes = data.Elements(M + nodeName).ToArray();
                if (matches.Length != 1 || nodes.Length != 1)
                    throw new InvalidDataException(label + "控件或绑定数据缺失或重复。");
                var control = matches[0];
                var cell = control.Ancestors(W + "tc").FirstOrDefault();
                var row = cell?.Parent;
                var currentTable = row?.Parent;
                var binding = control.Element(W + "sdtPr")?.Element(W + "dataBinding");
                var content = control.Element(W + "sdtContent");
                if (currentTable?.Name != W + "tbl" || (table != null && table != currentTable) ||
                    row!.Elements(W + "tc").ElementAtOrDefault(1) != cell ||
                    string.Concat(row.Elements(W + "tc").First().Descendants(W + "t").Select(t => t.Value)).Trim() != label ||
                    !string.Equals((string?)binding?.Attribute(W + "storeItemID"), StoreId, StringComparison.OrdinalIgnoreCase) ||
                    (string?)binding?.Attribute(W + "xpath") != "/m:fields[1]/m:" + nodeName + "[1]" ||
                    content == null || !content.Descendants(W + "t").Any() ||
                    content.Descendants().Any(e => e.Name == W + "ins" || e.Name == W + "del" ||
                        e.Name == W + "commentRangeStart" || e.Name == W + "commentRangeEnd") ||
                    string.Concat(content.Descendants(W + "t").Select(t => t.Value)) != nodes[0].Value)
                    throw new InvalidDataException((probe ? "表 4-3 的" : "表 4-2 的") + label + "表位、显示值或绑定需要核对。");
                table = currentTable;
                if (initializeFields) fieldHistory.Add(new XElement(State + key));
                var history = fieldHistory.Element(State + key) ?? throw new InvalidDataException(label + "缺少来源记录。");
                Fields.Add(new(key, label, nodes[0], control, history));
            }
        }

        public void SyncProbeNarrative(bool validateOnly = false)
        {
            var type = Fields.Single(f => f.Key == "type");
            var atom = Fields.Single(f => f.Key == "atom");
            TarmanProbeNarrative.Sync(type.Control.Ancestors(W + "tbl").First(), data, ProbeHistory!,
                StoreId, type.Current, atom.Current, validateOnly);
        }

        public void Save(ZipArchive zip, string? platformId = null)
        {
            if (platformId != null) History.SetAttributeValue("platform", platformId);
            NormalizePresentation();
            WriteParts(zip);
        }

        internal void WriteParts(ZipArchive zip)
        {
            Store(zip, dataPath, data);
            Store(zip, "word/document.xml", document);
        }
    }

    public void Create(string templatePath, string path, string platformId)
    {
        if (!Path.GetExtension(templatePath).Equals(".dotx", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("请选择塔尔曼 .dotx 审阅模板。");
        var platform = Platform(platformId);
        using var template = ReadSaved(templatePath);
        WordWorkfilePackage.Create(path, template, zip =>
        {
            var parts = new Parts(zip, projectId, areaId, create: true);
            foreach (var field in parts.Fields) field.Apply(field.Source(platform), true);
            parts.Save(zip, platform.Id);
            const string relPath = "word/_rels/settings.xml.rels";
            if (zip.GetEntry(relPath) == null) return;
            var rels = Xml(zip, relPath);
            var links = rels.Elements().Where(e => ((string?)e.Attribute("Type"))?.EndsWith("/attachedTemplate") == true).ToArray();
            if (links.Length == 0) return;
            var settings = Xml(zip, "word/settings.xml");
            settings.Elements(W + "attachedTemplate").Remove();
            foreach (var link in links) link.Remove();
            Store(zip, "word/settings.xml", settings);
            Store(zip, relPath, rels);
        });
    }

    public string Selection(string path)
    {
        using var file = ReadSaved(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Read);
        return (string?)new Parts(zip, projectId, areaId).History.Attribute("platform")
            ?? throw new InvalidDataException("工作稿缺少平台型号来源。");
    }

    public TarmanPreview Preview(string path, string platformId)
    {
        var platform = Platform(platformId);
        using var file = ReadSaved(path);
        string hash = Fingerprint(file);
        using var zip = new ZipArchive(file, ZipArchiveMode.Read);
        var parts = new Parts(zip, projectId, areaId);
        return new(hash, platform.Id, parts.Fields.Select(f => f.Compare(f.Source(platform))).ToArray());
    }

    public void Update(string path, TarmanPreview preview, string[] useSource)
    {
        using var guard = WorkspaceFileLock.AcquireForRoot(root);
        var platform = Platform(preview.PlatformId);
        WordWorkfilePackage.Update(path, preview.Fingerprint, zip =>
        {
            var parts = new Parts(zip, projectId, areaId);
            if (!parts.Fields.Select(f => f.Compare(f.Source(platform))).SequenceEqual(preview.Parameters))
                throw new IOException("型号参数已变化，请重新核对差异。");
            var selected = useSource.ToHashSet();
            foreach (var field in parts.Fields) field.Apply(field.Source(platform), field.Key == "model" || selected.Contains(field.Key));
            parts.Save(zip, platform.Id);
        });
    }

    public EquipmentRecord[] Probes() => Devices(EquipmentCatalogService.MagnetometerSensorCategory);

    private EquipmentRecord[] Devices(string category) => new JsonStore().Read(ProjectPaths.Json(root, "equipment.json"), new List<EquipmentRecord>())
        .Where(e => EquipmentCatalogService.NormalizeCategory(e.Category) == category
            && !string.IsNullOrWhiteSpace(e.Model) && !string.IsNullOrWhiteSpace(PingquanReport.EquipmentKey(e))).ToArray();

    public string? ProbeSelection(string path)
    {
        using var file = ReadSaved(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Read);
        return (string?)new Parts(zip, projectId, areaId).ProbeHistory?.Attribute("equipment");
    }

    private (string Model, ProfileEntry? Profile) ResolveProbe(string equipmentId)
    {
        var device = Probes().SingleOrDefault(e => PingquanReport.EquipmentKey(e) == equipmentId)
            ?? throw new InvalidDataException("探头台账记录缺失，请重新选择。");
        string model = EquipmentCatalogService.NormalizeModel(device.Model);
        var profile = ProjectReportProfile.Load(root).Profile.Entries.SingleOrDefault(p => p.Section == "probe" &&
            string.Equals(EquipmentCatalogService.NormalizeModel(p.Get("model")), model, StringComparison.OrdinalIgnoreCase));
        return (device.Model.Trim(), profile);
    }

    private static string ProbeValue(string key, string model, ProfileEntry? profile)
    {
        string value = profile?.Get(key).Trim() ?? "";
        if (value.Length == 0) value = "待核（缺该型号已核参数）";
        return key == "type" ? model + "｜" + value : value;
    }

    private static TarmanParameter[] CompareProbe(Parts parts, string model, ProfileEntry? profile) =>
        parts.Fields.Select(f => f.Compare(ProbeValue(f.Key, model, profile))).ToArray();

    public TarmanProbePreview PreviewProbe(string path, string equipmentId)
    {
        var (model, profile) = ResolveProbe(equipmentId);
        using var file = ReadSaved(path);
        string hash = Fingerprint(file);
        using var zip = new ZipArchive(file, ZipArchiveMode.Read);
        var parts = new Parts(zip, projectId, areaId, probe: true);
        return new(hash, equipmentId, model, profile?.Id, CompareProbe(parts, model, profile));
    }

    public void UpdateProbe(string path, TarmanProbePreview preview, string[] useSource)
    {
        using var guard = WorkspaceFileLock.AcquireForRoot(root);
        var (model, profile) = ResolveProbe(preview.EquipmentId);
        WordWorkfilePackage.Update(path, preview.Fingerprint, zip =>
        {
            var parts = new Parts(zip, projectId, areaId, probe: true);
            if (model != preview.Model || profile?.Id != preview.ProfileId ||
                !CompareProbe(parts, model, profile).SequenceEqual(preview.Parameters))
                throw new IOException("探头台账或型号参数已变化，请重新核对差异。");
            var selected = useSource.ToHashSet();
            foreach (var f in parts.Fields)
                f.Apply(ProbeValue(f.Key, model, profile), f.Key == "type" || selected.Contains(f.Key));
            parts.SyncProbeNarrative();
            parts.ProbeHistory!.SetAttributeValue("equipment", preview.EquipmentId);
            parts.ProbeHistory.SetAttributeValue("model", model);
            parts.ProbeHistory.SetAttributeValue("profile", profile?.Id);
            parts.Save(zip);
        });
    }

    public void Export(string path, string destination) =>
        WordWorkfilePackage.Export(path, destination, zip =>
        {
            var parts = new Parts(zip, projectId, areaId);
            parts.ValidateAssemblyForExport();
            if (parts.StationHistory != null) parts.StationFields();
            if (parts.SiteHistory != null) parts.SiteFields();
            if (parts.ProbeHistory != null)
                new Parts(zip, projectId, areaId, probe: true).SyncProbeNarrative(validateOnly: true);
        }, zip =>
        {
            var parts = new Parts(zip, projectId, areaId);
            return parts.NormalizePresentation() ? parts.WriteParts : null;
        });
}
