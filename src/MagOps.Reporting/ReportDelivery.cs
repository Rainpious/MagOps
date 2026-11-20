using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using DocumentFormat.OpenXml.Wordprocessing;
using MagOps.Infrastructure.Services;

namespace MagOps.Reporting;

public static class ReportDelivery
{
    internal static bool Include(ReportFact f) => f.Status != "UNAVAILABLE" && f.Id != "processing_record" &&
        !f.Id.StartsWith("人工补录 ") && !f.Id.StartsWith("assessment_") && !string.IsNullOrWhiteSpace(f.Value);

    public static void PrepareTemplate(string template, ReportSnapshot s)
    {
        using var doc = WordprocessingDocument.Open(template,true);
        var body = doc.MainDocumentPart!.Document!.Body!;
        void RemoveSection(string tag, string heading)
        {
            foreach (var control in body.Descendants<SdtBlock>().Where(x=>x.SdtProperties?.GetFirstChild<Tag>()?.Val == tag).ToArray())
            {
                if (control.PreviousSibling() is Paragraph p && p.InnerText == heading) p.Remove();
                control.Remove();
            }
        }
        RemoveSection("magops.manual.Warnings","编制检查");
        if (!s.Sources.Any(x=>x.Role=="report-figure")) RemoveSection("magops.figure.Results","已有成果图件");
        if (s.BoundaryVertices.Count==0) RemoveSection("magops.spatial.Boundary","");
        if (s.Survey?.Mothers.Count is not >0 && s.StaticTrials.Count==0 && s.QualityReference==null)
            RemoveSection("magops.survey.Statistics","母测线工作量与质量统计");
        if (!s.Facts.Any(f=>f.Id=="processing_record")) RemoveSection("magops.processing.Record","已有处理记录");
        foreach (var p in body.Elements<Paragraph>().ToArray())
        {
            // Match the shipped template before user-authored paragraphs are bound.
            if ((p.InnerText=="飞行质量及磁测质量指标" && !s.Sources.Any(x=>x.Role=="assessment-manifest"))
                || p.InnerText is "报告草稿" or "本报告依据所选处理用 CSV 及独立保存的资料编制。缺项与质量结论须经人工审核，草稿不代表正式验收。"
                or "设计偏航、离地高度、静态定位、动态与静态磁噪声、重复线总精度必须分别使用相应依据。海拔高程不能代替离地高度，实测拟合偏差不能代替设计偏航验收。"
                || (p.InnerText=="独立评估附件：{{r.AssessmentStatus}}。" && !s.Sources.Any(x=>x.Role=="assessment-manifest")))
                p.Remove();
        }
        doc.MainDocumentPart.Document.Save();
    }

    internal static void WriteChecklist(string dir, ReportSnapshot s)
    {
        var items = new List<string>(s.Warnings);
        foreach (var (label,value) in new[]{("编写单位",s.Inputs.Organization),("编写人",s.Inputs.Author),
            ("任务目的",s.Inputs.Objectives),("方法",s.Inputs.Methods),("处理说明",s.Inputs.ProcessingDescription),
            ("成果解释",s.Inputs.Interpretation),("结论",s.Inputs.Conclusions)})
            if (string.IsNullOrWhiteSpace(value)) items.Add(label+"尚未填写。");
        items.AddRange(s.Facts.Where(f=>f.Status=="UNAVAILABLE").Select(f=>f.Id+"："+f.Method));
        File.WriteAllLines(Path.Combine(dir,"编制检查清单.txt"),
            new[]{s.Inputs.ProjectName+" / "+s.Inputs.WorkareaName,"采用快照时间 "+s.CreatedUtc.ToString("O"),"需要核对的事项"}
            .Concat(items.Distinct()).Concat(new[]{"采用的来源"}).Concat(s.Sources.Select(x=>x.Role+" "+x.Path+" "+x.Sha256)));
    }

    public static string ExportArchive(string batch, string destination)
    {
        string source = Path.Combine(batch,"交付");
        string target = Path.Combine(destination,Path.GetFileName(batch) + "_交付.zip");
        string tmp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            System.IO.Compression.ZipFile.CreateFromDirectory(source,tmp);
            using (var archive = System.IO.Compression.ZipFile.OpenRead(tmp))
            {
                foreach (var entry in archive.Entries)
                {
                    using var stream = entry.Open();
                    string digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
                    if (digest != ReportSnapshotBuilder.Hash(Path.Combine(source,entry.FullName)))
                        throw new IOException("交付附件复制期间变化：" + entry.FullName);
                }
            }
            File.Move(tmp,target);
            return target;
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }

    internal static void ValidateWord(string path)
    {
        using var doc = WordprocessingDocument.Open(path,false);
        var error=new OpenXmlValidator().Validate(doc).FirstOrDefault();
        if(error!=null) throw new InvalidDataException(error.Description);
        if(ReportCompiler.TextParts(doc).Any(t=>t.Contains("{{"))) throw new InvalidDataException("交付文档有未替换的变量。");
    }
}
