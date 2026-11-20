using System.Xml.Linq;

namespace MagOps.Reporting;

internal static class TarmanProbeNarrative
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string TemplateDescription = "，具有采样率高、体积小、质量轻、低功耗及工作温度范围较宽等特点，便于无人机平台集成与野外携带";

    internal static void Sync(XElement table, XElement data, XElement history, string storeId,
        string adoptedType, string adoptedAtom, bool validateOnly)
    {
        // Use the values adopted in this Word, including a value the user elected to keep.
        string type = adoptedType[(adoptedType.IndexOf('｜') + 1)..].Trim();
        const string sourceNote = "（项目采用口径）";
        if (type.EndsWith(sourceNote, StringComparison.Ordinal)) type = type[..^sourceNote.Length].Trim();
        bool unknownAtom = Unconfirmed(adoptedAtom);
        if (Unconfirmed(type)) type = "磁探头";
        if (unknownAtom && type is "铷光泵磁探头" or "铯光泵磁探头" or "钾光泵磁探头" or "氦光泵磁探头")
            type = "光泵磁探头";
        string sensor = type + (unknownAtom ? "，原子种类待核" : "，原子种类为" + adoptedAtom.Trim());

        bool installed = history.Attribute("narrativeVersion")?.Value == "1";
        if (validateOnly && !installed) return; // Older table-only documents can still be exported.
        var preceding = table.ElementsBeforeSelf().Reverse().Take(3).ToArray();
        if (preceding.Length != 3 || preceding.Any(e => e.Name != W + "p") ||
            !preceding[0].Descendants(W + "bookmarkStart").Any(b => (string?)b.Attribute(W + "name") == "MagTab4_3"))
            throw new InvalidDataException("请核对表 4-3 的题注及前两段位置。");

        Bind(preceding[2], "probeAssemblyName", "磁测装置组成", "磁测装置由", type);
        Bind(preceding[1], "probeSensorDescription", "传感器类别与原子种类", "传感器采用", sensor);
        if (!validateOnly) history.SetAttributeValue("narrativeVersion", "1");

        void Bind(XElement paragraph, string key, string label, string prefix, string value)
        {
            var m = data.Name.Namespace;
            string tag = "tarman." + key;
            string path = "/m:fields[1]/m:" + key + "[1]";
            var document = table.Ancestors().Last();
            var tagged = document.Descendants(W + "tag").Where(t => (string?)t.Attribute(W + "val") == tag).ToArray();
            var nodes = data.Elements(m + key).ToArray();
            if (!installed && tagged.Length == 0 && nodes.Length == 0 && !validateOnly)
            {
                var properties = new XElement(W + "sdtPr",
                    new XElement(W + "alias", new XAttribute(W + "val", label)),
                    new XElement(W + "tag", new XAttribute(W + "val", tag)),
                    new XElement(W + "dataBinding", new XAttribute(W + "prefixMappings", $"xmlns:m='{m}'"),
                        new XAttribute(W + "xpath", path), new XAttribute(W + "storeItemID", storeId)),
                    new XElement(W + "text"));
                Install(paragraph, prefix, properties, value);
                data.Add(new XElement(m + key, value));
                return;
            }
            var control = tagged.Length == 1 ? tagged[0].Parent?.Parent : null;
            var binding = control?.Element(W + "sdtPr")?.Element(W + "dataBinding");
            var content = control?.Element(W + "sdtContent");
            var texts = content?.Descendants(W + "t").ToArray() ?? [];
            if (!installed || nodes.Length != 1 || control?.Parent != paragraph || texts.Length == 0 ||
                content!.Descendants().Any(e => e.Name == W + "ins" || e.Name == W + "del") ||
                (string?)binding?.Attribute(W + "xpath") != path ||
                !string.Equals((string?)binding?.Attribute(W + "storeItemID"), storeId, StringComparison.OrdinalIgnoreCase) ||
                string.Concat(texts.Select(t => t.Value)) != nodes[0].Value)
                throw new InvalidDataException(label + "的正文控件或绑定异常，请核对工作稿。");
            if (nodes[0].Value == value) return;
            if (validateOnly) throw new InvalidDataException("正文与表 4-3 的采用值不一致，请更新探头参数后再导出。");
            nodes[0].Value = value;
            texts[0].Value = value;
            foreach (var extra in texts.Skip(1)) extra.Value = "";
        }
    }

    private static bool Unconfirmed(string text) => string.IsNullOrWhiteSpace(text) || text.StartsWith('«') ||
        new[] { "待核", "待确认", "未确认", "待补", "未知", "未核", "待查", "不详" }.Any(text.Contains);

    // Migrate the two plain-text names in existing workfiles. Split only plain runs in the matched span.
    // Word's REF field, bookmarks, run properties and any text outside the span stay in place.
    private static void Install(XElement paragraph, string prefix, XElement properties, string value)
    {
        var texts = paragraph.Descendants(W + "t").ToArray();
        string full = string.Concat(texts.Select(t => t.Value));
        int start = full.IndexOf(prefix, StringComparison.Ordinal);
        if (start < 0 || full.LastIndexOf(prefix, StringComparison.Ordinal) != start)
            throw new InvalidDataException("未能定位表 4-3 前的“" + prefix + "”原句。");
        start += prefix.Length;
        string tail = full[start..];
        string name = tail.StartsWith("铷光泵磁探头", StringComparison.Ordinal) ? "铷光泵磁探头" : "光泵磁探头";
        if (!tail.StartsWith(name, StringComparison.Ordinal))
            throw new InvalidDataException("表 4-3 前的探头名称已修改，请核对原句。");
        int end = start + name.Length;
        if (prefix == "传感器采用" && full[end..].StartsWith(TemplateDescription, StringComparison.Ordinal))
            end += TemplateDescription.Length;

        int offset = 0;
        XElement? control = null;
        foreach (var text in texts)
        {
            int lo = Math.Max(0, start - offset), hi = Math.Min(text.Value.Length, end - offset);
            offset += text.Value.Length;
            if (lo >= hi) continue;
            var run = text.Parent!;
            if (run.Name != W + "r" || run.Parent != paragraph ||
                run.Elements().Any(e => e.Name != W + "rPr" && e.Name != W + "t") ||
                run.Elements(W + "t").Count() != 1)
                throw new InvalidDataException("表 4-3 正文名称包含嵌套字段或修订，请核对原句。");
            XElement Fragment(string fragment) => new(W + "r", run.Attributes(), run.Element(W + "rPr"),
                new XElement(W + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), fragment));
            if (lo > 0) run.AddBeforeSelf(Fragment(text.Value[..lo]));
            if (control == null)
            {
                control = new XElement(W + "sdt", properties, new XElement(W + "sdtContent", Fragment(value)));
                run.AddBeforeSelf(control);
            }
            if (hi < text.Value.Length) run.AddBeforeSelf(Fragment(text.Value[hi..]));
            run.Remove();
        }
    }
}
