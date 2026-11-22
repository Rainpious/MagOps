using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;
using static MagOps.Reporting.WordWorkfilePackage;

namespace MagOps.Reporting;

public sealed record OffsetFigurePreview(string Fingerprint,string RoutePath,string BatchPath,TarmanOffsetBatch Batch,TarmanParameter[] Changes);
public sealed partial class TarmanReport
{
    private sealed partial class Parts
    {
        internal XElement FigureHistory => History.Element(State+"offsetFigure") ?? AddHistory();
        private XElement AddHistory()
        {
            var section=new XElement(State+"offsetFigure");History.Add(section);return section;
        }
        internal XElement FigureControl() => Controls("tarman.chart.5.1").Single();
        private XElement[] Controls(string tag) => document.Descendants(W+"sdt").Where(c=>
            (string?)c.Element(W+"sdtPr")?.Element(W+"tag")?.Attribute(W+"val")==tag).ToArray();

        internal (Field Field,XElement[] Controls) FigureField(string node,string label,int expected)
        {
            var cs=Controls("tarman."+node);
            var value=data.Elements(M+node).Single();
            if(cs.Length!=expected || cs.Any(c=>
            {
                var bind=c.Element(W+"sdtPr")?.Element(W+"dataBinding");
                var content=c.Element(W+"sdtContent");
                return !string.Equals((string?)bind?.Attribute(W+"storeItemID"),StoreId,StringComparison.OrdinalIgnoreCase)
                    || (string?)bind?.Attribute(W+"xpath")!="/m:fields[1]/m:"+node+"[1]"
                    || content==null || !content.Descendants(W+"t").Any()
                    || content.Descendants().Any(e=>e.Name==W+"ins" || e.Name==W+"del" || e.Name==W+"commentRangeStart")
                    || string.Concat(content.Descendants(W+"t").Select(t=>t.Value))!=value.Value;
            })) throw new InvalidDataException(label+"的正文、交叉引用或 Word 绑定不一致，请核对。");
            var history=FigureHistory.Element(State+node);
            if(history==null){history=new XElement(State+node);FigureHistory.Add(history);}
            return (new Field(node,label,value,cs[0],history),cs);
        }
        internal void Caption()
        {
            var cap=FigureControl().ElementsAfterSelf().FirstOrDefault();
            if(cap?.Name!=W+"p")throw new InvalidDataException("图 5-1 题注位置需核对。");
            var title=cap.Descendants(W+"t").SingleOrDefault(t=>t.Value=="测区测线偏航距统计直方图");
            if(title!=null)title.Value="测区逐线平均偏航距柱状图";
        }
    }
    private static (string Key,string Label,int Count,string Value)[] Numbers(TarmanOffsetBatch b) =>
        [("f21","逐线均值的均值（第五章、章末）",2,b.Mean.ToString("0.000",CultureInfo.InvariantCulture)),
         ("f22","逐线均值的最大值（第五章）",1,b.Maximum.Mean!.Value.ToString("0.000",CultureInfo.InvariantCulture))];

    public OffsetFigurePreview PreviewOffsetFigure(string path,string routePath,string batchPath)
    {
        var batch=TarmanOffsetBatch.Read(routePath,batchPath,projectId,areaId);
        using var file=ReadSaved(path);string fp=Fingerprint(file);
        using var zip=new ZipArchive(file,ZipArchiveMode.Read);
        var parts=new Parts(zip,projectId,areaId);
        var chart=new TarmanOffsetChart(zip,parts.FigureControl());var h=parts.FigureHistory;
        string previous=(string?)h.Attribute("summary") ?? "";
        string oldHash=(string?)h.Attribute("chartHash") ?? "";
        var changes=new List<TarmanParameter>{new("chart","图 5-1 及采用批次",previous,chart.Description,batch.Summary,oldHash=="" || oldHash==chart.Fingerprint())};
        foreach(var n in Numbers(batch))
        {
            var field=parts.FigureField(n.Key,n.Label,n.Count).Field;var change=field.Compare(n.Value);
            if(field.History.Attribute("written")==null && !field.Current.StartsWith('«'))change=change with {Suggested=false};
            changes.Add(change);
        }
        return new(fp,routePath,batchPath,batch,changes.ToArray());
    }
    public void UpdateOffsetFigure(string path,OffsetFigurePreview preview,string[] selected)
    {
        if(!selected.Contains("chart"))throw new InvalidOperationException("未采用新图与批次，整组保持当前内容。");
        var batch=TarmanOffsetBatch.Read(preview.RoutePath,preview.BatchPath,projectId,areaId);
        if(!batch.Files.SequenceEqual(preview.Batch.Files))throw new IOException("预览后来源发生变化，请重新核对。");
        WordWorkfilePackage.Update(path,preview.Fingerprint,zip=>
        {
            var parts=new Parts(zip,projectId,areaId);var chart=new TarmanOffsetChart(zip,parts.FigureControl());
            var h=parts.FigureHistory;var last=h.Element(State+"sourceFiles");
            var files=new XElement(State+"sourceFiles",batch.Files.Select(f=>new XElement(State+"file",new XAttribute("path",f.Path),new XAttribute("sha256",f.Sha256))));
            if(!XNode.DeepEquals(last,files) || (string?)h.Attribute("chartHash")!=chart.Fingerprint())
            {
                chart.Write(batch);h.SetAttributeValue("chartHash",chart.Fingerprint());
            }
            parts.Caption();
            foreach(var n in Numbers(batch))
            {
                var (field,cs)=parts.FigureField(n.Key,n.Label,n.Count);
                field.Apply(n.Value,selected.Contains(n.Key));
                if(selected.Contains(n.Key))
                    foreach(var c in cs.Skip(1))
                    {
                        var ts=c.Element(W+"sdtContent")!.Descendants(W+"t").ToArray();ts[0].Value=n.Value;
                        foreach(var t in ts.Skip(1))t.Value="";
                        c.Element(W+"sdtPr")!.Element(W+"showingPlcHdr")?.Remove();
                    }
            }
            h.SetAttributeValue("design",batch.Design);h.SetAttributeValue("batch",batch.Generated);h.SetAttributeValue("authority",batch.Authority);
            h.SetAttributeValue("status",batch.Status);h.SetAttributeValue("adoption","审阅试算");h.SetAttributeValue("summary",batch.Summary);
            h.SetAttributeValue("algorithm","MagOps finite horizontal design segment / equal line means");
            last?.Remove();h.Add(files);h.Element(State+"lines")?.Remove();
            h.Add(new XElement(State+"lines",batch.Lines.Select(l=>new XElement(State+"line",
                new XAttribute("name",l.Name),new XAttribute("mother",l.Mother),new XAttribute("task",l.Task),new XAttribute("sortie",l.Sortie),
                new XAttribute("input",l.Input),new XAttribute("valid",l.Valid),new XAttribute("outside",l.Outside),
                new XAttribute("invalid",l.Invalid),l.Mean.HasValue?new XAttribute("meanM",l.Mean.Value):null))));
            parts.Save(zip);
        });
    }
}
