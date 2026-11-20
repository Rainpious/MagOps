using System.IO.Compression;
using static MagOps.Reporting.WordWorkfilePackage;
using System.Xml.Linq;
using MagOps.Core.Domain;
using MagOps.Infrastructure.Storage;

namespace MagOps.Reporting;

public sealed record PingquanChange(string Key, string Label, string Previous, string Current, string Source,
    string DeviceId, bool CanWrite, bool Suggested, string Reason);
public sealed record PingquanPreview(string Fingerprint, IReadOnlyList<PingquanChange> Fields);

// Each working document carries its own source selection and merge baseline in its mapped custom XML part.
public sealed class PingquanReport
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace M = "urn:magops:word:survey-report:v1";
    private static readonly XNamespace State = "urn:magops:pingquan:workfile:1";
    public static readonly IReadOnlyDictionary<string, string> Fields = new Dictionary<string, string>
    {
        ["info.Project"] = "项目名称",
        ["data.Aircraft"] = "飞行平台型号",
        ["data.Magnetometer"] = "航空磁力仪型号",
        ["data.BaseInstrument"] = "基站仪器型号"
    };
    private readonly string root;
    private readonly string projectId;
    private readonly string areaId;
    public string ProjectName { get; }
    public string DirectoryPath { get; }

    public PingquanReport(string root, string area, string areaId)
    {
        (projectId, ProjectName) = Context(root, area, areaId);
        this.root = root;
        this.areaId = areaId;
        DirectoryPath = ReportInputStore.DirectoryFor(root, area);
    }

    public EquipmentRecord[] Equipment() => new JsonStore().Read(ProjectPaths.Json(root, "equipment.json"), new List<EquipmentRecord>()).ToArray();
    public static string EquipmentKey(EquipmentRecord e) => string.IsNullOrWhiteSpace(e.RecordId) ? e.EquipmentId : e.RecordId;

    private sealed class Parts
    {
        public readonly XElement Document, Data, Baseline;
        public readonly string DataPath;
        public Parts(ZipArchive z, string projectId, string areaId, bool create)
        {
            Document = Xml(z, "word/document.xml");
            foreach (var entry in z.Entries.Where(e => e.FullName.StartsWith("customXml/item", StringComparison.Ordinal)
                && !e.FullName.Contains("Props") && e.FullName.EndsWith(".xml")))
            {
                var data = Xml(z, entry.FullName);
                if (data.Name != M + "report") continue;
                Data = data; DataPath = entry.FullName; break;
            }
            if (Data == null || DataPath == null) throw new InvalidDataException("选择的文件缺少平泉工作稿数据。");
            if (create)
            {
                Baseline = new XElement(State + "work", new XAttribute("project", projectId), new XAttribute("area", areaId),
                    new XAttribute("id", Guid.NewGuid().ToString("N")));
                Data.Add(Baseline);
                foreach (var key in Fields.Keys) Baseline.Add(new XElement(State + "field", new XAttribute("key", key)));
            }
            else Baseline = Data.Element(State + "work") ?? throw new InvalidDataException("请选择从 MagOps 新建的平泉工作稿。");
            if ((string?)Baseline.Attribute("project") != projectId || (string?)Baseline.Attribute("area") != areaId)
                throw new InvalidDataException("工作稿与当前项目、工区不一致。");
        }
        public XElement Field(string key) => Baseline.Elements(State + "field").Single(e => (string?)e.Attribute("key") == key);
        public XElement[] Controls(string key) => Document.Descendants(W + "sdt").Where(e =>
            (string?)e.Element(W + "sdtPr")?.Element(W + "tag")?.Attribute(W + "val") == key).ToArray();
        public XElement Node(string key) => Data.Element(M + key.Split('.')[1]) ?? throw new InvalidDataException("工作稿缺少字段 " + key);
        public void Save(ZipArchive z)
        {
            Store(z, DataPath, Data); Store(z, "word/document.xml", Document);
        }
    }

    private Parts ReadParts(ZipArchive z) => new(z, projectId, areaId, false);
    public Dictionary<string, string> Selection(string path)
    {
        using var file = ReadSaved(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Read);
        var parts = ReadParts(zip);
        return Fields.Keys.Where(k => k != "info.Project").ToDictionary(k => k, k => (string?)parts.Field(k).Attribute("device") ?? "");
    }

    private PingquanChange[] Compare(Parts p, IReadOnlyDictionary<string, string> selection, string projectName)
    {
        var devices = Equipment().ToLookup(EquipmentKey, StringComparer.OrdinalIgnoreCase);
        var changes = new List<PingquanChange>();
        foreach (var (key, label) in Fields)
        {
            var field = p.Field(key);
            string device = selection.GetValueOrDefault(key) ?? "";
            string source = string.IsNullOrWhiteSpace(projectName) ? ProjectName : projectName.Trim();
            string reason = "";
            if (key != "info.Project")
            {
                var records = devices[device].ToArray();
                source = device.Length > 0 && records.Length == 1 ? records[0].Model.Trim() : "";
                if (source.Length == 0) reason = device.Length == 0 ? "未选用设备" : "设备缺失、身份重复或未登记型号";
            }
            var controls = p.Controls(key);
            string current = p.Node(key).Value;
            string previous = (string?)field.Attribute("source") ?? "";
            string lastWritten = (string?)field.Attribute("written") ?? "";
            string nodeName = key.Split('.')[1];
            foreach (var control in controls)
            {
                var pr = control.Element(W + "sdtPr")!;
                var binding = pr.Element(W + "dataBinding");
                var content = control.Element(W + "sdtContent");
                string shown = pr.Element(W + "showingPlcHdr") == null ? string.Concat(content!.Descendants(W + "t").Select(t => t.Value)) : "";
                if (shown != current || !string.Equals((string?)binding?.Attribute(W + "storeItemID"), "{15A47E34-494F-4284-ACF3-3C469A2AD8D8}", StringComparison.OrdinalIgnoreCase) ||
                    (string?)binding?.Attribute(W + "xpath") != "/m:report[1]/m:" + nodeName + "[1]" ||
                    content!.Descendants().Any(e => e.Name == W + "ins" || e.Name == W + "del" || e.Name == W + "commentRangeStart" || e.Name == W + "commentRangeEnd"))
                    reason = "控件绑定、显示或修订需要人工核对";
            }
            if (controls.Length == 0) reason = "字段已移除";
            bool manual = current != lastWritten || (string?)field.Attribute("keep") == "1";
            bool writable = reason.Length == 0 && current != source;
            bool suggested = writable && !manual;
            if (reason.Length == 0) reason = current == source ? "值一致" : manual ?
                (source != previous ? "来源和 Word 均有变化" : "保留 Word 取值") : "来源可更新";
            changes.Add(new(key, label, previous, current, source, device, writable, suggested, reason));
        }
        return changes.ToArray();
    }

    private static void ApplyFields(Parts p, IEnumerable<PingquanChange> changes, ISet<string> useSource)
    {
        foreach (var c in changes)
        {
            var field = p.Field(c.Key);
            field.SetAttributeValue("device", c.DeviceId);
            if (!c.CanWrite && c.Reason != "值一致") continue;
            field.SetAttributeValue("source", c.Source);
            if (c.CanWrite && !useSource.Contains(c.Key)) { field.SetAttributeValue("keep", "1"); continue; }
            field.SetAttributeValue("written", c.Source);
            field.SetAttributeValue("keep", null);
            if (!c.CanWrite) continue;
            p.Node(c.Key).Value = c.Source;
            foreach (var control in p.Controls(c.Key))
            {
                control.Element(W + "sdtPr")!.Element(W + "showingPlcHdr")?.Remove();
                var text = control.Element(W + "sdtContent")!.Descendants(W + "t").ToArray();
                if (text.Length == 0) throw new InvalidDataException("字段缺少可更新的文字：" + c.Label);
                text[0].Value = c.Source;
                foreach (var t in text.Skip(1)) t.Value = "";
            }
        }
    }

    public void Create(string path, Dictionary<string, string> selection, string projectName)
    {
        using var source = typeof(PingquanReport).Assembly.GetManifestResourceStream("MagOps.Pingquan.dotx")!;
        WordWorkfilePackage.Create(path, source, zip =>
        {
            var parts = new Parts(zip, projectId, areaId, true);
            var fields = Compare(parts, selection, projectName);
            ApplyFields(parts, fields, fields.Where(c => c.Suggested).Select(c => c.Key).ToHashSet());
            parts.Save(zip);
        });
    }

    public PingquanPreview Preview(string path, Dictionary<string, string> selection, string projectName)
    {
        using var file = ReadSaved(path);
        string hash = Fingerprint(file);
        using var zip = new ZipArchive(file, ZipArchiveMode.Read);
        return new(hash, Compare(ReadParts(zip), selection, projectName));
    }

    public void Update(string path, PingquanPreview preview, string[] useSource, string projectName)
    {
        WordWorkfilePackage.Update(path, preview.Fingerprint, zip =>
        {
            var parts = ReadParts(zip);
            var current = Compare(parts, preview.Fields.ToDictionary(c => c.Key, c => c.DeviceId), projectName);
            if (!current.SequenceEqual(preview.Fields)) throw new IOException("设备来源已变化，请重新检查差异。");
            ApplyFields(parts, current, useSource.ToHashSet());
            parts.Save(zip);
        });
    }

    public void Export(string path, string destination) =>
        WordWorkfilePackage.Export(path, destination, zip => _ = ReadParts(zip));
}
