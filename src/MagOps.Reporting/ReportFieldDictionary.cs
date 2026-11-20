using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace MagOps.Reporting;

public sealed record ReportField(string Id, string Chapter, string Name, string Kind, string Tag,
    string Source, string Unit, string Condition, string Missing, string Rule, string Format, string Categories);

public static class ReportFieldDictionary
{
    public const string Version = "2";
    public static IReadOnlyList<ReportField> Fields { get; } = Load();
    private static readonly Dictionary<string, ReportField> ById = Fields.ToDictionary(f => f.Id);

    private static IReadOnlyList<ReportField> Load()
    {
        using var data = typeof(ReportFieldDictionary).Assembly.GetManifestResourceStream("MagOps.Reporting.report-fields.json")
            ?? throw new InvalidDataException("报告字段字典资源缺失。");
        var existing = JsonSerializer.Deserialize<ReportField[]>(data) ?? throw new InvalidDataException("报告字段字典为空。");
        return Array.AsReadOnly(existing.Concat(ProjectReportProfile.FieldDefinitions()).ToArray());
    }

    internal static string TagFor(string id) => ById[id].Tag;
    internal static string Token(string id) => "{{" + TagFor(id) + "}}";
    internal static string ModelRoot => TagFor("ProjectName").Split('.')[0];
    internal static string DisplayName(string factId) => ById.TryGetValue("fact." + factId, out var f) ? f.Name : factId;

    internal static Dictionary<string,string[]> SectionCategories(bool hasDeployment)
    {
        var result = Fields.Where(f => f.Categories.Length > 0)
            .ToDictionary(f => f.Id, f => f.Categories.Split('|'));
        if (!hasDeployment)
        {
            result.Remove("Deployment");
            result["Methods"] = [..result["Methods"], "规范"];
            result["Objectives"] = [..result["Objectives"], "设计参数"];
        }
        return result;
    }

    public static void Validate(string path)
    {
        using var doc = WordprocessingDocument.Open(path, false);
        var body = doc.MainDocumentPart?.Document?.Body ?? throw new InvalidDataException("模板缺少正文。");
        var tags = body.Descendants<SdtBlock>().Select(x => x.SdtProperties?.GetFirstChild<Tag>()?.Val?.Value).ToLookup(s => s);
        var text = ReportCompiler.TextParts(doc).ToArray();
        var errors = new List<string>();
        foreach (var field in Fields)
        {
            if (field.Rule == "恰好一次" && tags[field.Tag].Count() != 1)
                errors.Add($"{field.Chapter}「{field.Name}」标签 {field.Tag} 应出现一次，当前 {tags[field.Tag].Count()} 次。");
            if (field.Rule == "必含，可重复" && !text.Any(t => t.Contains("{{" + field.Tag + "}}", StringComparison.Ordinal)))
                errors.Add($"{field.Chapter}「{field.Name}」缺少变量 {{{{{field.Tag}}}}}。");
        }
        // Inspect simple MagOps bindings. Leave DocxTemplater formatters and custom expressions to its parser.
        var fields = Fields.Where(f => f.Kind is "变量" or "循环").ToDictionary(f => f.Tag);
        foreach (var part in text)
        {
            foreach (System.Text.RegularExpressions.Match token in System.Text.RegularExpressions.Regex.Matches(part,
                @"\{\{[#/]?(r\.[A-Za-z_][A-Za-z0-9_]*)\}\}"))
                if (!fields.ContainsKey(token.Groups[1].Value))
                    errors.Add("未知报告变量：" + token.Groups[1].Value);
        }
        foreach (var loop in Fields.Where(f => f.Kind == "循环"))
        {
            string open = "{{#" + loop.Tag + "}}", close = "{{/" + loop.Tag + "}}";
            var columns = Fields.Where(f => f.Kind == "循环列" && f.Id.StartsWith(loop.Id + ".", StringComparison.Ordinal))
                .Select(f => f.Id[(loop.Id.Length + 1)..]).ToHashSet();
            foreach (string part in text)
            {
                int cursor = 0;
                while (cursor < part.Length)
                {
                    int start = part.IndexOf(open, cursor, StringComparison.Ordinal);
                    int end = part.IndexOf(close, cursor, StringComparison.Ordinal);
                    if (start < 0 && end < 0) break;
                    if (start < 0 || end < start)
                    {
                        errors.Add($"{loop.Chapter}「{loop.Name}」循环 {loop.Tag} 缺少配对的开始或结束标签。");
                        break;
                    }
                    string contents = part[(start + open.Length)..end];
                    foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(contents, @"\{\{\.([A-Za-z_]\w*)\}\}"))
                        if (!columns.Contains(m.Groups[1].Value))
                            errors.Add($"{loop.Chapter}「{loop.Name}」未知表列 {m.Value}。");
                    cursor = end + close.Length;
                }
            }
        }
        if (errors.Count > 0) throw new InvalidDataException(string.Join(Environment.NewLine, errors.Distinct()));
    }
}
