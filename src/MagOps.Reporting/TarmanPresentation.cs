using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MagOps.Reporting;

public sealed partial class TarmanReport
{
    // Keep catalogue values and evidence verbatim. Format only known report fields for the reader.
    private static string ReportText(string key, string text)
    {
        if (text == "待核（缺该型号已核参数）" || text == "待核（缺该型号已核资料）") return "待核";
        return key switch
        {
            "f96" => text.Replace("（项目采用口径）", "").Replace("待核（缺该型号已核参数）", "待核"),
            "f98" => text.Replace("，按原稿及厂家标称）", "）"),
            "f104" when text.EndsWith("（塔尔曼原稿配置）", StringComparison.Ordinal) =>
                "参考配置：" + text[..^"（塔尔曼原稿配置）".Length],
            "f107" when text.EndsWith("（原稿标称）", StringComparison.Ordinal) =>
                "标称 " + text[..^"（原稿标称）".Length],
            "station_instrument" => Regex.Replace(text,
                @"(\d{4} 年 \d{1,2} 月 [\d、]+ 日)三份基站原始记录中，相邻读数间隔均为 (\d+(?:\.\d+)? s)", "$1的观测间隔为 $2"),
            "station_method" when text == "日变站与航空磁力仪的精度一致性、两仪器的实际对时方式及同步结果待核。" =>
                "日变站与航空磁力仪的精度一致性、对时方式和同步结果待核。",
            _ => text
        };
    }

    private sealed partial class Parts
    {
        // Apply to the data tables and the signature grid. Cover titles and captions are separate paragraphs.
        private bool FormatTables()
        {
            bool changed = false;
            foreach (var cell in document.Descendants(W + "tc"))
            {
                foreach (var p in cell.Elements(W + "p").Concat(cell.Elements(W + "sdt")
                    .SelectMany(s => s.Descendants(W + "p"))))
                    SetSize(Properties(Properties(p, "pPr"), "rPr", paragraphMark: true));

                foreach (var run in cell.Descendants(W + "r").Where(r => r.Elements(W + "t").Any()))
                    SetSize(Properties(run, "rPr"));
                foreach (var control in cell.Descendants(W + "sdtPr").Where(s => s.Element(W + "text") != null))
                    SetSize(Properties(control, "rPr"));
            }
            return changed;

            XElement Properties(XElement parent, string name, bool paragraphMark = false)
            {
                if (parent.Element(W + name) is {} existing) return existing;
                var created = new XElement(W + name);
                if (!paragraphMark) parent.AddFirst(created);
                else if (parent.Elements().FirstOrDefault(e => e.Name == W + "sectPr" || e.Name == W + "pPrChange") is {} next)
                    next.AddBeforeSelf(created);
                else parent.Add(created);
                changed = true;
                return created;
            }

            void SetSize(XElement properties)
            {
                foreach (string key in new[] { "sz", "szCs" })
                {
                    var size = properties.Element(W + key);
                    if (size == null)
                    {
                        size = new XElement(W + key);
                        var next = properties.Elements().FirstOrDefault(e =>
                            (key == "sz" && e.Name == W + "szCs") || e.Name.LocalName is "highlight" or "u" or
                            "effect" or "bdr" or "shd" or "fitText" or "vertAlign" or "rtl" or "cs" or "em" or
                            "lang" or "eastAsianLayout" or "specVanish" or "oMath" or "rPrChange");
                        if (next == null) properties.Add(size); else next.AddBeforeSelf(size);
                    }
                    if ((string?)size.Attribute(W + "val") == "21") continue;
                    size.SetAttributeValue(W + "val", "21");
                    changed = true;
                }
            }
        }

        internal bool NormalizePresentation()
        {
            bool changed = FormatTables();
            if (ProbeHistory is {} probe)
                foreach (var (key, node, _) in ProbeBindings) CleanBound(node, probe.Element(State + key));
            if (StationHistory is {} station)
                foreach (var key in new[] { "instrument", "method" })
                    CleanBound("station_" + key, station.Element(State + key));
            if (SiteHistory != null)
            {
                var site = Controls("tarman.f39").SingleOrDefault()?.Parent?.ElementsAfterSelf().FirstOrDefault();
                if (site?.Name == W + "p" && TarmanReferenceReport.Visible(site) == OldSiteSentence &&
                    site.Elements().All(e => e.Name == W + "pPr" || e.Name == W + "proofErr" || e.Name == W + "r") &&
                    site.Elements(W + "r").SelectMany(r => r.Elements()).All(e => e.Name == W + "rPr" || e.Name == W + "t"))
                {
                    var ts = site.Descendants(W + "t").ToArray();
                    ts[0].Value = SiteSentence;
                    foreach (var t in ts.Skip(1)) t.Value = "";
                    changed = true;
                }
            }
            foreach (var tag in document.Descendants(W + "tag").ToArray())
            {
                string id = (string?)tag.Attribute(W + "val") ?? "";
                if (!id.StartsWith("tarman.assembly.", StringComparison.Ordinal)) continue;
                var control = tag.Parent!.Parent!;
                string original = TarmanReferenceReport.Visible(control);
                if (!NoteRevisions.TryGetValue(original, out var replacement)) continue;
                if (control.Descendants().Any(e => e.Name == W + "ins" || e.Name == W + "del" || e.Name == W + "commentRangeStart")) continue;
                if (replacement.Length == 0) control.Remove();
                else Note(control.Parent!, id["tarman.assembly.".Length..], replacement);
                changed = true;
            }
            return changed;

            void CleanBound(string key, XElement? record)
            {
                var value = data.Element(M + key)?.Value;
                if (value == null || value != (string?)record?.Attribute("written") || (bool?)record?.Attribute("keep") == true) return;
                string revised = ReportText(key, value);
                if (value == revised) return;
                SetBound(key, revised);
                record!.SetAttributeValue("written", revised);
                changed = true;
            }
        }
    }

    private const string OldSiteSentence = "站址环境及十字剖面磁场稳定性待现场选址记录核实。";
    private const string SiteSentence = "站址环境及十字剖面磁场稳定性待核。";
    private static readonly Dictionary<string, string> NoteRevisions = new()
    {
        ["〔历史报告示例 · 署名待本项目确认〕"] = "（历史样稿）",
        ["〔历史报告试填〕"] = "",
        ["〔历史报告示例 · 人员待本项目确认〕"] = "（历史名册）",
        ["〔原模板图示 · 待接工区数据〕"] = "（示例图）",
        ["〔待接本工区重复线统计〕"] = "（待核）",
        ["〔待核工区工作量〕"] = "（待核）"
    };
}
