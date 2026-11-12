using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocxTemplater;
using MagOps.Infrastructure.Services;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using DocumentFormat.OpenXml.Wordprocessing;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;

namespace MagOps.Reporting;

public sealed class ReportCompiler
{
    public ReportBuildResult Build(string root, string workarea, ReportSnapshot snapshot, byte[] trackPng,
        IReadOnlyDictionary<string, byte[]>? qualityFigures = null)
    {
        string reportRoot = ReportInputStore.DirectoryFor(root, workarea);
        Directory.CreateDirectory(reportRoot);
        string id = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture) + "_" + Guid.NewGuid().ToString("N")[..8];
        string staging = Path.Combine(reportRoot, ".building_" + id);
        string output = Path.Combine(reportRoot, "草稿_" + id);
        Directory.CreateDirectory(staging);
        // Failed staging is retained with an explicit name for inspection; it is never advertised as a report.
        try
        {
            foreach (var source in snapshot.Sources)
                if (!File.Exists(source.Path) || !ReportSnapshotBuilder.Hash(source.Path).Equals(source.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("报告来源已变化，请重新检查：" + source.Role);
            string template = Path.Combine(staging, "template.docx");
            if (string.IsNullOrWhiteSpace(snapshot.Inputs.TemplatePath)) ReportTemplate.Create(template);
            else File.Copy(snapshot.Inputs.TemplatePath, template, false);
            string templateHash = ReportSnapshotBuilder.Hash(template);
            ValidateContract(template);
            string document = Path.Combine(staging, "航磁工作小结_草稿.docx");
            using (var processor = DocxTemplate.Open(template))
            {
                processor.BindModel("r", new
                {
                    snapshot.Inputs.ProjectName, snapshot.Inputs.WorkareaName, snapshot.Inputs.Organization,
                    snapshot.Inputs.Author, snapshot.Inputs.ReportDate,
                    CentralMeridian = snapshot.CentralMeridian?.ToString("0.######", CultureInfo.InvariantCulture) ?? "待确认",
                    AssessmentStatus = snapshot.Sources.Any(x => x.Role == "assessment-manifest") ? "已校验内部完整性，尚未建立与生产选择范围的一致性" : "未选择",
                    Facts = snapshot.Facts.Where(f => !f.Id.StartsWith("人工补录 ", StringComparison.Ordinal) && (f.Id != "processing_record" || !string.IsNullOrWhiteSpace(snapshot.Inputs.TemplatePath))).Select(f => new { Id = FactName(f.Id), f.Value, f.Unit, Status = Status(f.Status),
                        Method = f.Status == "UNAVAILABLE" ? "需补充相应独立依据" : f.Method }).ToArray(),
                    LineTableNote = $"共{snapshot.Lines.Count}组任务/Pass，下表列示前20组；完整记录见同目录保留航段统计.csv。长度含重复飞行，不代表独立工作量。",
                    Lines = snapshot.Lines.Take(20).Select(l => new { l.Name, l.Pass, l.Points, LengthKm = l.LengthKm.ToString("0.000", CultureInfo.InvariantCulture) }).ToArray()
                });
                processor.Save(document);
            }
            string figure = Path.Combine(staging, "保留作业轨迹.png"); File.WriteAllBytes(figure, trackPng);
            foreach (var source in snapshot.Sources.Where(s => s.Role == "processing-manifest"))
            {
                string reference = Path.Combine(staging, "processing-reference.json");
                File.Copy(source.Path, reference, false);
                if (!ReportSnapshotBuilder.Hash(reference).Equals(source.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("复制处理参考时来源发生变化。");
            }
            qualityFigures ??= new Dictionary<string, byte[]>();
            if (qualityFigures.Count > 500) throw new InvalidDataException("自动质量图件数量超过500张，请按报告范围分批生成。");
            int figureNumber = 0;
            foreach (var image in qualityFigures.Values) File.WriteAllBytes(Path.Combine(staging, $"质量图_{++figureNumber:00}.png"), image);
            FillManualAndFigure(document, snapshot, trackPng, qualityFigures);
            using (var doc = WordprocessingDocument.Open(document, false))
            {
                var errors = new OpenXmlValidator().Validate(doc).Take(12).ToArray();
                if (errors.Length > 0) throw new InvalidDataException("Word文档结构检查未通过：" + string.Join("；", errors.Select(e => e.Description)));
                if (TextParts(doc).Any(t => Regex.IsMatch(t, @"\{\{[^}]+\}\}")))
                    throw new InvalidDataException("Word文档仍包含未替换的模板变量。");
            }
            foreach (var source in snapshot.Sources)
                if (!ReportSnapshotBuilder.Hash(source.Path).Equals(source.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("生成过程中报告来源已变化：" + source.Role);
            var referenceNames = new[] { "summary.json", "input-and-parameters.json", "flight-quality-report.md", "task-flight-quality.csv", "static-gps-quality.csv", "manifest.json" };
            foreach (var source in snapshot.Sources.Where(s => s.Role.StartsWith("assessment") && referenceNames.Contains(Path.GetFileName(s.Path))))
            {
                string referenceDirectory = Path.Combine(staging, "assessment-reference"); Directory.CreateDirectory(referenceDirectory);
                string target = Path.Combine(referenceDirectory, source.Role == "assessment-manifest" ? "source-manifest.json" : Path.GetFileName(source.Path));
                File.Copy(source.Path, target, false);
                if (!ReportSnapshotBuilder.Hash(target).Equals(source.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("复制评估参考时来源发生变化。");
                File.WriteAllText(Path.Combine(referenceDirectory, "README.txt"), "这里只保留小结所需的评估参考子集，不是完整评估批次。source-manifest.json记录原批次清单，报告manifest.json记录实际打包文件。评估范围尚未与最终CSV选择范围等同。");
            }
            File.WriteAllText(Path.Combine(staging, "snapshot.json"), JsonSerializer.Serialize(snapshot, ReportInputStore.Json));
            ReportSurveyExport.Write(staging, snapshot);
            static string Csv(string text) => "\"" + text.Replace("\"", "\"\"") + "\"";
            File.WriteAllLines(Path.Combine(staging, "保留航段统计.csv"),
                new[] { "LineName,LineTaskId,LinePass,Points,TrackLengthKm" }.Concat(snapshot.Lines.Select(l =>
                    string.Join(",", Csv(l.Name), Csv(l.TaskId), Csv(l.Pass), l.Points.ToString(CultureInfo.InvariantCulture),
                        l.LengthKm.ToString("R", CultureInfo.InvariantCulture)))), new System.Text.UTF8Encoding(true));
            File.WriteAllText(Path.Combine(staging, "checks.json"), JsonSerializer.Serialize(new
            {
                StructuralValidation = "PASSED", LayoutValidation = "REQUIRES_WORD_REVIEW", FormalPublishAllowed = false,
                snapshot.Warnings, MissingFacts = snapshot.Facts.Where(f => f.Status == "UNAVAILABLE").ToArray(),
                ManualEditing = "人工章节以report-inputs.json为源；直接编辑输出Word后须作为独立终稿保存，后续生成不会自动合并。"
            }, ReportInputStore.Json));
            string fingerprint = snapshot.Sources.Single(s => s.Role == "production-csv").Sha256;
            var identityContext = new GeneratedArtifactContext(snapshot.ProjectId, snapshot.WorkareaId);
            var lineage = new GeneratedArtifactLineage([], [], snapshot.Lines.Select(l => l.TaskId).Distinct().ToArray(), [], []);
            foreach (string managed in new[] { document, Path.Combine(staging, "snapshot.json") }.Concat(Directory.GetFiles(staging, "*.csv")))
                GeneratedArtifactIdentity.Write(managed, "report", "report_draft", fingerprint,
                    snapshot.Sources.Select(s => s.Path).ToArray(), qualityStatus: "REVIEW_REQUIRED", context: identityContext, lineage: lineage);
            File.WriteAllText(Path.Combine(staging, "manifest.json"), JsonSerializer.Serialize(new
            {
                SchemaVersion = "magops-report-bundle-1", Complete = true, Draft = true,
                TemplateVersion = string.IsNullOrWhiteSpace(snapshot.Inputs.TemplatePath) ? ReportTemplate.Version : "USER_TEMPLATE",
                TemplateSha256 = templateHash, TemplateEngine = "DocxTemplater 2.9.7", OpenXml = "3.5.1",
                Files = Directory.GetFiles(staging, "*", SearchOption.AllDirectories).Order().Select(p => new { File = Path.GetRelativePath(staging, p), Sha256 = ReportSnapshotBuilder.Hash(p) }).ToArray()
            }, ReportInputStore.Json));
            Directory.Move(staging, output);
            return new(output, Path.Combine(output, Path.GetFileName(document)), Path.Combine(output, "snapshot.json"), Path.Combine(output, "manifest.json"));
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(Path.Combine(staging, "FAILED.txt"), ex.Message); } catch (IOException) { }
            throw;
        }
    }

    private static string Status(string status) => status switch { "AVAILABLE" => "已统计", "REFERENCE_ONLY" => "独立参考", _ => "待补充" };
    private static string FactName(string id) => id switch
    {
        "retained_points" => "所选CSV样点", "retained_lines" => "追溯任务数（非母线条数）", "retained_passes" => "任务与Pass分组",
        "retained_length" => "累计保留轨迹长度", "utc_range" => "数据时间范围", "elevation_range" => "海拔高程范围",
        "assessment_source" => "独立评估批次指纹", "assessment_task_scope" => "评估任务覆盖范围", "processing_record" => "已有处理记录", _ => id
    };

    private static IEnumerable<string> TextParts(WordprocessingDocument doc)
    {
        var main = doc.MainDocumentPart!;
        yield return main.Document?.InnerText ?? "";
        foreach (var p in main.HeaderParts) yield return p.Header?.InnerText ?? "";
        foreach (var p in main.FooterParts) yield return p.Footer?.InnerText ?? "";
        if (main.FootnotesPart is { } foot) yield return foot.Footnotes?.InnerText ?? "";
        if (main.EndnotesPart is { } end) yield return end.Endnotes?.InnerText ?? "";
    }

    private static void ValidateContract(string path)
    {
        using var doc = WordprocessingDocument.Open(path, false);
        if (doc.MainDocumentPart?.Document?.Body is not { } body) throw new InvalidDataException("模板缺少正文。");
        foreach (string name in new[] { "Objectives", "Methods", "ProcessingDescription", "Interpretation", "Conclusions", "Warnings" })
            if (body.Descendants<SdtBlock>().Count(x => x.SdtProperties?.GetFirstChild<Tag>()?.Val?.Value == "magops.manual." + name) != 1)
                throw new InvalidDataException("模板必须包含一个人工内容控件：magops.manual." + name + "。请从内置模板另存后修改排版。");
        if (body.Descendants<SdtBlock>().Count(x => x.SdtProperties?.GetFirstChild<Tag>()?.Val?.Value == "magops.figure.TrackMap") != 1)
            throw new InvalidDataException("模板必须包含一个轨迹图控件：magops.figure.TrackMap。");
        if (!TextParts(doc).Any(x => x.Contains("{{r.ProjectName}}")))
            throw new InvalidDataException("模板缺少项目名变量，请使用MagOps报告模板，而非旧项目成品。");
    }

    private static void FillManualAndFigure(string path, ReportSnapshot snapshot, byte[] png, IReadOnlyDictionary<string, byte[]> qualityFigures)
    {
        using var doc = WordprocessingDocument.Open(path, true);
        var main = doc.MainDocumentPart!;
        if (!main.Document!.Descendants<SdtBlock>().Any(s => s.SdtProperties?.GetFirstChild<Tag>()?.Val?.Value == "magops.survey.Statistics"))
        {
            var control = new SdtBlock(new SdtProperties(new Tag { Val = "magops.survey.Statistics" }), new SdtContentBlock());
            var section = main.Document.Body!.Elements<SectionProperties>().LastOrDefault();
            if (section is null) main.Document.Body.Append(control); else main.Document.Body.InsertBefore(control, section);
        }
        if (snapshot.Sources.Any(s => s.Role == "report-figure") && !main.Document!.Descendants<SdtBlock>().Any(s => s.SdtProperties?.GetFirstChild<Tag>()?.Val?.Value == "magops.figure.Results"))
        {
            var extraFigures = new SdtBlock(new SdtProperties(new Tag { Val = "magops.figure.Results" }), new SdtContentBlock());
            var section = main.Document.Body!.Elements<SectionProperties>().LastOrDefault();
            if (section is null) main.Document.Body.Append(extraFigures);
            else main.Document.Body.InsertBefore(extraFigures, section);
        }
        var values = new Dictionary<string, string>
        {
            ["Objectives"] = snapshot.Inputs.Objectives, ["Methods"] = snapshot.Inputs.Methods,
            ["ProcessingDescription"] = snapshot.Inputs.ProcessingDescription, ["Interpretation"] = snapshot.Inputs.Interpretation,
            ["Conclusions"] = snapshot.Inputs.Conclusions, ["Warnings"] = string.Join("\n", snapshot.Warnings)
        };
        var supplementCategories = new Dictionary<string, string[]>
        {
            ["Objectives"] = ["项目概况", "委托单位", "任务依据", "任务目标", "设计参数"],
            ["Methods"] = ["人员", "设备", "规范", "基站", "现场记录", "试验条件", "作业方法"],
            ["ProcessingDescription"] = ["处理记录", "外部处理", "成果来源"],
            ["Interpretation"] = ["成果解释"], ["Conclusions"] = ["结论建议"]
        };
        foreach (var section in supplementCategories)
        {
            var selected = snapshot.Inputs.Supplements.Where(s => section.Value.Contains(s.Category, StringComparer.Ordinal)).ToArray();
            if (selected.Length == 0) continue;
            values[section.Key] += "\n" + string.Join("\n", selected.Select(s =>
                $"{s.Category} / {s.Name}：{s.Value} {s.Unit}（报告专用人工补录，{(s.Confirmed ? "人工已确认" : "尚未确认")}；依据：{s.Evidence}）"));
        }
        var mappedCategories = supplementCategories.Values.SelectMany(c => c).ToHashSet(StringComparer.Ordinal);
        var unmapped = snapshot.Inputs.Supplements.Where(s => !mappedCategories.Contains(s.Category)).ToArray();
        if (unmapped.Length > 0) values["Warnings"] += "\n以下补录类别未映射正文，仅保留附表：" + string.Join("、", unmapped.Select(s => s.Category + "/" + s.Name));
        foreach (var control in main.Document!.Descendants<SdtBlock>())
        {
            string tag = control.SdtProperties?.GetFirstChild<Tag>()?.Val?.Value ?? "";
            var content = control.GetFirstChild<SdtContentBlock>();
            if (content is null) continue;
            if (tag == "magops.spatial.Boundary") ReportSurveyExport.FillBoundary(content, snapshot);
            if (tag == "magops.survey.Statistics")
            {
                ReportSurveyExport.Fill(content, snapshot);
                foreach (var figure in qualityFigures)
                {
                    var paragraph = ImageParagraph(main, figure.Value, 5486400, 3200400, figure.Key);
                    paragraph.ParagraphProperties = new ParagraphProperties(new KeepNext());
                    content.Append(paragraph);
                    content.Append(ReportTemplate.P(figure.Key));
                }
            }
            if (tag == "magops.processing.Record")
            {
                content.RemoveAllChildren();
                foreach (string line in (snapshot.Facts.FirstOrDefault(f => f.Id == "processing_record")?.Value ?? "未找到唯一匹配的处理记录，请补充处理依据。").Split('\n'))
                    content.Append(ReportTemplate.P(line));
                content.Append(ReportTemplate.P("以上为已有处理记录的转录，不代表质量验收；外部调平、化极及成图步骤需另附实际依据。"));
            }
            if (tag == "magops.figure.Results")
            {
                content.RemoveAllChildren();
                int figureNumber = 0;
                foreach (var source in snapshot.Sources.Where(s => s.Role == "report-figure"))
                {
                    byte[] image = File.ReadAllBytes(source.Path);
                    if (image.Length < 24 || !image.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
                        throw new InvalidDataException("成果图不是有效PNG：" + Path.GetFileName(source.Path));
                    int imageWidth = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(image.AsSpan(16, 4));
                    int imageHeight = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(image.AsSpan(20, 4));
                    if (imageWidth <= 0 || imageHeight <= 0) throw new InvalidDataException("成果PNG尺寸无效。");
                    double scale = Math.Min(5486400d / imageWidth, 6400800d / imageHeight);
                    content.Append(ImageParagraph(main, image, (long)(imageWidth * scale), (long)(imageHeight * scale), Path.GetFileName(source.Path)));
                    content.Append(ReportTemplate.P($"图3-{++figureNumber} {Path.GetFileNameWithoutExtension(source.Path)}"));
                }
                if (figureNumber == 0) content.Append(ReportTemplate.P("尚未选择成果图；请在成果图件页添加已确认的PNG图。"));
            }
            if (tag.StartsWith("magops.manual.") && values.TryGetValue(tag[14..], out string? text))
            {
                content.RemoveAllChildren();
                foreach (string line in (string.IsNullOrWhiteSpace(text) ? "[待人工补充]" : text).Replace("\r", "").Split('\n'))
                {
                    var paragraph = ReportTemplate.P(line);
                    if (tag == "magops.manual.Warnings") paragraph.ParagraphProperties = new ParagraphProperties(
                        new SpacingBetweenLines { Before = "0", After = "0", Line = "260", LineRule = LineSpacingRuleValues.Auto });
                    content.Append(paragraph);
                }
            }
            if (tag != "magops.figure.TrackMap") continue;
            var part = main.AddImagePart(ImagePartType.Png);
            using (var stream = new MemoryStream(png)) part.FeedData(stream);
            const long width = 5486400, height = 3429000;
            uint drawingId = main.Document.Descendants<DW.DocProperties>().Select(p => p.Id?.Value ?? 0).DefaultIfEmpty().Max() + 1;
            var drawing = new Drawing(new DW.Inline(new DW.Extent { Cx = width, Cy = height },
                new DW.DocProperties { Id = drawingId, Name = "保留作业轨迹" },
                new A.Graphic(new A.GraphicData(new PIC.Picture(
                    new PIC.NonVisualPictureProperties(new PIC.NonVisualDrawingProperties { Id = 0, Name = "保留作业轨迹.png" }, new PIC.NonVisualPictureDrawingProperties()),
                    new PIC.BlipFill(new A.Blip { Embed = main.GetIdOfPart(part) }, new A.Stretch(new A.FillRectangle())),
                    new PIC.ShapeProperties(new A.Transform2D(new A.Offset { X = 0, Y = 0 }, new A.Extents { Cx = width, Cy = height }),
                        new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle })))
                    { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" })));
            content.RemoveAllChildren(); content.Append(new Paragraph(new Run(drawing)));
        }
        main.Document.Save();
    }

    private static Paragraph ImageParagraph(MainDocumentPart main, byte[] image, long width, long height, string name)
    {
        var part = main.AddImagePart(ImagePartType.Png);
        using (var stream = new MemoryStream(image)) part.FeedData(stream);
        uint id = main.Document!.Descendants<DW.DocProperties>().Select(p => p.Id?.Value ?? 0).DefaultIfEmpty().Max() + 1;
        return new Paragraph(new Run(new Drawing(new DW.Inline(new DW.Extent { Cx = width, Cy = height },
            new DW.DocProperties { Id = id, Name = name },
            new A.Graphic(new A.GraphicData(new PIC.Picture(
                new PIC.NonVisualPictureProperties(new PIC.NonVisualDrawingProperties { Id = id, Name = name }, new PIC.NonVisualPictureDrawingProperties()),
                new PIC.BlipFill(new A.Blip { Embed = main.GetIdOfPart(part) }, new A.Stretch(new A.FillRectangle())),
                new PIC.ShapeProperties(new A.Transform2D(new A.Offset { X = 0, Y = 0 }, new A.Extents { Cx = width, Cy = height }),
                    new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle })))
                { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" })))));
    }
}
