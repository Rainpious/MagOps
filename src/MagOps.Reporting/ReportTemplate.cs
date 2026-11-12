using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace MagOps.Reporting;

/// <summary>Versioned clean template; never reuses old project facts or conclusions.</summary>
public static class ReportTemplate
{
    public const string Version = "magops-summary-template-3";

    public static void Create(string path)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite);
        using var doc = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document);
        var main = doc.AddMainDocumentPart();
        var body = new Body(); main.Document = new Document(body);
        var styles = main.AddNewPart<StyleDefinitionsPart>();
        styles.Styles = new Styles(
            new DocDefaults(new RunPropertiesDefault(new RunPropertiesBaseStyle(
                new RunFonts { Ascii = "Times New Roman", HighAnsi = "Times New Roman", EastAsia = "宋体" },
                new Color { Val = "000000" }, new FontSize { Val = "24" })),
                new ParagraphPropertiesDefault(new ParagraphPropertiesBaseStyle(
                    new SpacingBetweenLines { After = "120", Line = "360", LineRule = LineSpacingRuleValues.Auto }))),
            Style("Title", "Title", "40", true), Style("Heading1", "heading 1", "30", true),
            Style("Heading2", "heading 2", "26", true));
        body.Append(P("{{r.ProjectName}}", "Title"), P("{{r.WorkareaName}}航磁工作小结", "Title"),
            P("报告草稿"), P("编写单位：{{r.Organization}}"), P("编写人：{{r.Author}}"), P("日期：{{r.ReportDate}}"),
            P("本报告依据所选处理用 CSV 及独立保存的资料编制。缺项与质量结论须经人工审核，草稿不代表正式验收。"),
            new Paragraph(new ParagraphProperties(new PageBreakBefore()), new Run(new Text("目录"))),
            new Paragraph(new SimpleField(new Run(new Text("在Word中更新目录域以生成章节与页码。"))) { Instruction = "TOC \\o \"1-2\" \\h \\z \\u" }),
            new Paragraph(new ParagraphProperties(new ParagraphStyleId { Val = "Heading1" }, new PageBreakBefore()),
                new Run(new Text("第一章 工作概况"))), P("项目：{{r.ProjectName}}；工区：{{r.WorkareaName}}。"),
            P("中央子午线：{{r.CentralMeridian}}；数据时间均为 UTC。"),
            P("任务目的", "Heading2"), Region("Objectives"),
            P("工作量与范围", "Heading2"),
            new SdtBlock(new SdtProperties(new Tag { Val = "magops.spatial.Boundary" }), new SdtContentBlock(P("[设计边界待选择]"))),
            Table(["项目", "数值", "单位", "状态", "统计依据"],
                ["{{#r.Facts}}{{.Id}}", "{{.Value}}", "{{.Unit}}", "{{.Status}}", "{{.Method}}{{/r.Facts}}"]),
            P("第二章 工作方法与质量评价", "Heading1"),
            P("技术依据与工作部署", "Heading2"),
            P("待补充本项目适用规范、设计比例尺、测线间距、方向、设计飞高与实际部署；以已确认设计为准。"),
            P("人员设备与作业方法", "Heading2"), Region("Methods"),
            P("飞行质量及磁测质量指标", "Heading2"),
            P("设计偏航、离地高度、静态定位、动态与静态磁噪声、重复线总精度必须分别使用相应依据。海拔高程不能代替离地高度，实测拟合偏差不能代替设计偏航验收。"),
            P("独立评估附件：{{r.AssessmentStatus}}。"),
            P("母测线工作量与质量统计", "Heading2"),
            new SdtBlock(new SdtProperties(new Tag { Val = "magops.survey.Statistics" }), new SdtContentBlock(P("[母测线统计待计算]"))),
            P("第三章 数据处理与图件", "Heading1"),
            P("已有处理记录", "Heading2"),
            new SdtBlock(new SdtProperties(new Tag { Val = "magops.processing.Record" }), new SdtContentBlock(P("[处理记录待核对]"))),
            P("处理说明与外部成图步骤", "Heading2"), Region("ProcessingDescription"),
            P("保留作业轨迹", "Heading2"),
            new SdtBlock(new SdtProperties(new Tag { Val = "magops.figure.TrackMap" }),
                new SdtContentBlock(P("[轨迹图待生成]"))),
            P("图件仅显示所选 CSV 的空间轨迹；为显示而抽稀，数据统计仍使用全部记录。不同 Pass 与时间断续处不连线。"),
            P("已有成果图件", "Heading2"),
            new SdtBlock(new SdtProperties(new Tag { Val = "magops.figure.Results" }), new SdtContentBlock(P("[成果图待选择]"))),
            P("第四章 成果解释", "Heading1"), Region("Interpretation"),
            P("第五章 结论与建议", "Heading1"), Region("Conclusions"),
            P("附录 保留航段统计", "Heading1"),
            P("{{r.LineTableNote}}"),
            Table(["测线", "Pass", "点数", "轨迹长度 km"],
                ["{{#r.Lines}}{{.Name}}", "{{.Pass}}", "{{.Points}}", "{{.LengthKm}}{{/r.Lines}}"]),
            P("编制检查", "Heading1"), Region("Warnings"),
            new SectionProperties(new PageSize { Width = 11906, Height = 16838 },
                new PageMargin { Top = 1440, Bottom = 1440, Left = 1701, Right = 1417, Header = 720, Footer = 720 }));
        var footer = main.AddNewPart<FooterPart>();
        footer.Footer = new Footer(new Paragraph(new ParagraphProperties(new Justification { Val = JustificationValues.Center }),
            new Run(new Text("第 ")), new SimpleField { Instruction = "PAGE" }, new Run(new Text(" 页"))));
        body.Elements<SectionProperties>().Single().PrependChild(new FooterReference { Type = HeaderFooterValues.Default, Id = main.GetIdOfPart(footer) });
        footer.Footer.Save(); main.Document.Save(); styles.Styles.Save();
        styles.Styles.Elements<Style>().Single(s => s.StyleId == "Heading1").StyleParagraphProperties!.Append(new OutlineLevel { Val = 0 });
        styles.Styles.Elements<Style>().Single(s => s.StyleId == "Heading2").StyleParagraphProperties!.Append(new OutlineLevel { Val = 1 });
        styles.Styles.Save();
        var settings = main.AddNewPart<DocumentSettingsPart>();
        settings.Settings = new Settings(new UpdateFieldsOnOpen { Val = true }); settings.Settings.Save();
    }

    private static Style Style(string id, string name, string size, bool bold) => new(
        new StyleName { Val = name }, new StyleParagraphProperties(new KeepNext()),
        new StyleRunProperties(new RunFonts { EastAsia = "黑体", Ascii = "Times New Roman" },
            new Bold { Val = bold }, new Color { Val = "000000" }, new FontSize { Val = size }))
        { Type = StyleValues.Paragraph, StyleId = id };

    internal static Paragraph P(string text, string? style = null) => new(
        style is null ? new ParagraphProperties() : new ParagraphProperties(new ParagraphStyleId { Val = style }),
        new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve }));

    private static SdtBlock Region(string name) => new(new SdtProperties(new Tag { Val = "magops.manual." + name }),
        new SdtContentBlock(P("[人工内容]")));

    internal static Table Table(string[] header, string[] row)
    {
        var border = new TableBorders(
            new TopBorder { Val = BorderValues.Single, Size = 4, Color = "D9D9D9" },
            new LeftBorder { Val = BorderValues.Single, Size = 4, Color = "D9D9D9" },
            new BottomBorder { Val = BorderValues.Single, Size = 4, Color = "D9D9D9" },
            new RightBorder { Val = BorderValues.Single, Size = 4, Color = "D9D9D9" },
            new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4, Color = "D9D9D9" },
            new InsideVerticalBorder { Val = BorderValues.Single, Size = 4, Color = "D9D9D9" });
        int[] widths = header.Length switch
        {
            4 => [3500, 1200, 1700, 2388],
            5 => [1500, 1900, 600, 900, 3888],
            _ => Enumerable.Repeat(8788 / header.Length, header.Length).Select((width, index) =>
                index == header.Length - 1 ? 8788 - (8788 / header.Length) * (header.Length - 1) : width).ToArray()
        };
        var table = new Table(new TableProperties(new TableWidth { Type = TableWidthUnitValues.Dxa, Width = "8788" }, border,
                new TableLayout { Type = TableLayoutValues.Fixed }),
            new TableGrid(widths.Select(w => new GridColumn { Width = w.ToString() })));
        TableRow Row(string[] values, bool head) => new(new OpenXmlElement[] { new TableRowProperties(new CantSplit()) }.Concat(values.Select((value, i) =>
            new TableCell(new TableCellProperties(new TableCellWidth { Width = widths[i].ToString(), Type = TableWidthUnitValues.Dxa },
                new Shading { Val = ShadingPatternValues.Clear, Fill = head ? "E7E6E6" : "FFFFFF" },
                new TableCellMargin(new TopMargin { Width = "90", Type = TableWidthUnitValues.Dxa },
                    new LeftMargin { Width = "90", Type = TableWidthUnitValues.Dxa },
                    new BottomMargin { Width = "90", Type = TableWidthUnitValues.Dxa },
                    new RightMargin { Width = "90", Type = TableWidthUnitValues.Dxa }),
                new TableCellVerticalAlignment { Val = TableVerticalAlignmentValues.Center }),
                new Paragraph(new ParagraphProperties(new SpacingBetweenLines { Before = "0", After = "0", Line = "260", LineRule = LineSpacingRuleValues.Auto }),
                    new Run(new RunProperties(new FontSize { Val = "21" }), new Text(value) { Space = SpaceProcessingModeValues.Preserve }))))));
        var heading = Row(header, true); heading.GetFirstChild<TableRowProperties>()!.Append(new TableHeader());
        table.Append(heading, Row(row, false)); return table;
    }
}
