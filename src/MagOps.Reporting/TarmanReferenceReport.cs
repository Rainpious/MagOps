using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using static MagOps.Reporting.WordWorkfilePackage;

namespace MagOps.Reporting;

internal sealed record AssemblyEvidence(string Position,string Summary,string Part,string PartHash,string[] Rows);
internal sealed record TarmanReferenceReport(string Path,string Hash,Dictionary<string,string> Cover,List<string[]> Staff,AssemblyEvidence[] Evidence)
{
    private static readonly XNamespace W="http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    internal static string Visible(XElement node) => string.Concat(node.Descendants(W+"t").Select(t=>t.Value)).Trim();
    internal static string Compact(string text) => Regex.Replace(text,@"[\s：:（）()]","");

    internal static TarmanReferenceReport Read(string path)
    {
        using var input=ReadSaved(path);
        string hash=Fingerprint(input);
        using var zip=new ZipArchive(input,ZipArchiveMode.Read);
        var body=Xml(zip,"word/document.xml").Element(W+"body") ?? throw new InvalidDataException("原报告正文缺失。");
        var tables=body.Elements(W+"tbl").ToArray();
        var cover=new Dictionary<string,string>();
        var labels=new Dictionary<string,string>{{"编写单位","f2"},{"编写人","f32"},{"项目负责","f33"},{"单位负责","f34"},{"提交时间","f4"}};
        foreach(var table in tables)
        {
            var rows=table.Elements(W+"tr").Select(r=>r.Elements(W+"tc").Select(Visible).ToArray()).ToArray();
            if(!rows.Any(r=>r.Length==2 && Compact(r[0])=="编写单位"))continue;
            foreach(var row in rows)
                if(row.Length==2 && labels.TryGetValue(Compact(row[0]),out var key) && !cover.TryAdd(key,row[1]))
                    throw new InvalidDataException("原报告存在重复署名栏，请核实原件。");
        }
        if(!cover.ContainsKey("f2"))throw new InvalidDataException("找不到兼容的原报告编写单位／署名表。");
        var clients=body.Elements(W+"p").Select(Visible).Select(s=>Regex.Match(s,@"^受([^，。；]{2,100}?)委托[，,]"))
            .Where(m=>m.Success).Select(m=>m.Groups[1].Value).Distinct().ToArray();
        if(clients.Length==1)cover["f3"]=clients[0];
        var names=body.Elements(W+"p").Select(Visible).Select(s=>Regex.Match(s,@"^受.{2,100}?委托[，,].{2,100}?对(.{2,100}?)开展无人机航空磁测工作"))
            .Where(m=>m.Success).Select(m=>m.Groups[1].Value).Distinct().ToArray();
        if(names.Length==1 && body.Elements(W+"p").Take(35).Any(p=>Visible(p)==names[0]))cover["f1"]=names[0];

        string[] required=["姓名","性别","年龄","专业","职称","岗位","备注"];
        var staffTables=tables.Where(t=>t.Elements(W+"tr").FirstOrDefault() is {} head &&
            head.Elements(W+"tc").Select(c=>Compact(Visible(c)).Replace("年龄岁","年龄")).SequenceEqual(required)).ToArray();
        if(staffTables.Length!=1)throw new InvalidDataException("原报告须有一张可辨认的七列人员表。");
        var staff=new List<string[]>();
        foreach(var r in staffTables[0].Elements(W+"tr").Skip(1))
        {
            var v=r.Elements(W+"tc").Select(Visible).ToArray();
            if(v.Length!=7)throw new InvalidDataException("原报告人员行存在合并单元格，请核对。");
            if(v.All(string.IsNullOrWhiteSpace))continue;
            if(v[0].Length==0 || v.Any(s=>s.Contains('«')))throw new InvalidDataException("原报告人员行缺姓名或仍含占位符。");
            staff.Add(v);
        }
        if(staff.Count==0 || staff.GroupBy(r=>r[0]).Any(g=>g.Count()>1))
            throw new InvalidDataException("原报告人员为空或有同名记录，请先区分身份。");
        return new(System.IO.Path.GetFullPath(path),hash,cover,staff,ReadEvidence(zip,body));
    }

    private static AssemblyEvidence[] ReadEvidence(ZipArchive zip,XElement body)
    {
        XNamespace r="http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        XNamespace chart="http://schemas.openxmlformats.org/drawingml/2006/chart";
        XNamespace draw="http://schemas.openxmlformats.org/drawingml/2006/main";
        var relationships=Xml(zip,"word/_rels/document.xml.rels").Elements().ToDictionary(x=>(string)x.Attribute("Id")!);
        var evidence=new List<AssemblyEvidence>();
        foreach(var slot in TarmanReport.AssemblySlots.Where(x=>x.Bookmark!="MagTab4_1"))
        {
            string label=slot.Position.Replace(" ","");
            var titles=body.Elements(W+"p").Where(p=>Regex.IsMatch(Visible(p),@"^"+label[0]+@"\s*"+label[1]+@"\s*[-－—]\s*"+label[3]+@"(?!\d)")).ToArray();
            var title=titles.Length==1?titles[0]:null;
            AssemblyEvidence Missing()=>new(slot.Position,"原报告中未唯一定位","", "",[]);
            if(title==null){evidence.Add(Missing());continue;}
            if(slot.Bookmark.StartsWith("MagTab",StringComparison.Ordinal))
            {
                var table=title.ElementsAfterSelf().FirstOrDefault(e=>e.Name!=W+"p" || Visible(e).Length>0);
                if(table?.Name!=W+"tbl"){evidence.Add(Missing());continue;}
                var rows=table.Elements(W+"tr").Select(tr=>string.Join("｜",tr.Elements(W+"tc").Select(Visible))).ToArray();
                evidence.Add(new(slot.Position,Visible(title)+"；"+(rows.Length-1)+" 行表体","word/document.xml","",rows));continue;
            }
            var previous=title.ElementsBeforeSelf().Reverse().FirstOrDefault(e=>e.Descendants(W+"drawing").Any() || Visible(e).Length>0);
            var objects=previous?.Descendants(slot.Bookmark=="MagFig5_6"?draw+"blip":chart+"chart").Take(2).ToArray();
            if(objects?.Length!=1){evidence.Add(Missing());continue;}
            var obj=objects[0];
            string? id=(string?)obj?.Attribute(slot.Bookmark=="MagFig5_6"?r+"embed":r+"id");
            if(id==null || !relationships.TryGetValue(id,out var rel) || (string?)rel.Attribute("TargetMode")=="External")
            {evidence.Add(Missing());continue;}
            var part=new Uri(new Uri("https://package/word/document.xml"),(string)rel.Attribute("Target")!);
            if(part.Host!="package"){evidence.Add(Missing());continue;}
            string name=Uri.UnescapeDataString(part.AbsolutePath.TrimStart('/'));
            var entry=zip.GetEntry(name);
            if(entry==null){evidence.Add(Missing());continue;}
            using var input=entry.Open();var digest=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(input));
            string summary="原报告内嵌图片";
            if(slot.Bookmark!="MagFig5_6")
            {
                var xml=Xml(zip,name);
                var counts=xml.Descendants(chart+"ser").Select(s=>
                    (s.Element(chart+"val") ?? s.Element(chart+"yVal"))?.Descendants(chart+"pt").Count() ?? 0);
                summary="原图各系列缓存值数："+string.Join("、",counts);
            }
            evidence.Add(new(slot.Position,Visible(title)+"；"+summary,name,digest,[]));
        }
        return evidence.ToArray();
    }
}
