using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Xml.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using Xls = DocumentFormat.OpenXml.Spreadsheet;
using static MagOps.Reporting.WordWorkfilePackage;

namespace MagOps.Reporting;

internal sealed class TarmanOffsetChart
{
    private static readonly XNamespace C="http://schemas.openxmlformats.org/drawingml/2006/chart";
    private static readonly XNamespace A="http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace R="http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private readonly ZipArchive package;
    private readonly string path,relsPath,bookPath;
    private readonly XElement chart,rels,series,bookRel;
    internal string Description => $"{series.Element(C+"val")!.Descendants(C+"pt").Count()} 根柱 · " +
        ((string?)bookRel.Attribute("TargetMode")=="External" ? "外链数据" : "嵌入数据");
    internal string Fingerprint()
    {
        // Track the data a Word author can change, including edits made in the embedded worksheet.
        var data=string.Join("|",series.Elements().Where(e=>e.Name==C+"cat" || e.Name==C+"val")
            .SelectMany(e=>e.Descendants(C+"pt")).Select(pt=>pt.Value));
        string bookHash="";
        if ((string?)bookRel.Attribute("TargetMode")!="External")
        {
            using var input=(package.GetEntry(bookPath) ?? throw new InvalidDataException("图 5-1 数据簿缺失。")).Open();
            bookHash=Convert.ToHexString(SHA256.HashData(input));
        }
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(data+"|"+bookHash)));
    }
    private static string Resolve(string owner,string target)
    {
        var uri=new Uri(new Uri("https://package/"+owner),target);
        if(uri.Host!="package" || uri.Scheme!="https")throw new InvalidDataException("图表部件路径无效。");
        return Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
    }
    internal TarmanOffsetChart(ZipArchive zip,XElement figure)
    {
        package=zip;
        var id=(string?)figure.Descendants(C+"chart").Single().Attribute(R+"id");
        var rel=Xml(zip,"word/_rels/document.xml.rels").Elements().Single(e=>(string?)e.Attribute("Id")==id);
        path=Resolve("word/document.xml",(string)rel.Attribute("Target")!);
        relsPath=path[..(path.LastIndexOf('/')+1)]+"_rels/"+Path.GetFileName(path)+".rels";
        chart=Xml(zip,path);rels=Xml(zip,relsPath);
        series=chart.Descendants(C+"barChart").Single().Elements(C+"ser").Single();
        string bookId=(string)chart.Descendants(C+"externalData").Single().Attribute(R+"id")!;
        bookRel=rels.Elements().Single(e=>(string?)e.Attribute("Id")==bookId);
        bookPath=(string?)bookRel.Attribute("TargetMode")=="External" ? "word/embeddings/MagOpsOffset51.xlsx" : Resolve(path,(string)bookRel.Attribute("Target")!);
    }
    internal void Write(TarmanOffsetBatch batch)
    {
        var points=batch.Plotted.ToArray();int last=points.Length+1;
        // Leave the chart style and drawing/anchor intact; change its series and local data package.
        var values=series.Element(C+"val") ?? throw new InvalidDataException("图 5-1 数据系列缺失。");
        series.Element(C+"cat")?.Remove();
        XElement Cache(string name,IEnumerable<string> items)
        {
            var array=items.ToArray();
            return new XElement(C+name,name=="numCache"?new XElement(C+"formatCode","0.000"):null,
                new XElement(C+"ptCount",new XAttribute("val",array.Length)),
                array.Select((v,i)=>new XElement(C+"pt",new XAttribute("idx",i),new XElement(C+"v",v))));
        }
        values.AddBeforeSelf(new XElement(C+"cat",new XElement(C+"strRef",new XElement(C+"f",$"Sheet1!$A$2:$A${last}"),Cache("strCache",points.Select(l=>l.Name)))));
        values.ReplaceNodes(new XElement(C+"numRef",new XElement(C+"f",$"Sheet1!$B$2:$B${last}"),
            Cache("numCache",points.Select(l=>l.Mean!.Value.ToString("R",CultureInfo.InvariantCulture)))));
        var seriesName=series.Element(C+"tx")?.Descendants(C+"v").SingleOrDefault();
        if(seriesName!=null)seriesName.Value="平均偏航距（m）";
        var title=chart.Descendants(C+"title").FirstOrDefault()?.Descendants(A+"t").ToArray();
        if(title is {Length:>0})
        {
            title[0].Value="逐线平均偏航距（m）· 审阅试算";
            foreach(var t in title.Skip(1)) t.Value="";
        }
        var axis=chart.Descendants(C+"catAx").Single();
        void AxisTitle(XElement ax,string label)
        {
            var node=new XElement(C+"title",new XElement(C+"tx",new XElement(C+"rich",
                new XElement(A+"bodyPr"),new XElement(A+"lstStyle"),
                new XElement(A+"p",new XElement(A+"r",new XElement(A+"rPr",new XAttribute("lang","zh-CN"),new XAttribute("sz","850")),new XElement(A+"t",label))))),
                new XElement(C+"overlay",new XAttribute("val","0")));
            if(ax.Element(C+"title") is {} existingTitle)existingTitle.ReplaceWith(node);
            else ax.Element(C+"axPos")!.AddAfterSelf(node);
        }
        AxisTitle(axis,"测线号");AxisTitle(chart.Descendants(C+"valAx").Single(),"逐线平均偏航距（m）");
        var props=axis.Element(C+"txPr");
        props?.Element(A+"bodyPr")?.SetAttributeValue("rot",-5400000);
        foreach(var font in props?.Descendants(A+"defRPr") ?? [])font.SetAttributeValue("sz",700);
        axis.Elements(C+"tickLblSkip").Remove();axis.Elements(C+"tickMarkSkip").Remove();
        var tail=axis.Elements().FirstOrDefault(e=>e.Name==C+"noMultiLvlLbl" || e.Name==C+"extLst");
        var ticks=new[]{new XElement(C+"tickLblSkip",new XAttribute("val",1)),new XElement(C+"tickMarkSkip",new XAttribute("val",1))};
        if(tail!=null)tail.AddBeforeSelf(ticks);else axis.Add(ticks);
        var existing=package.GetEntry(bookPath);
        if(existing!=null && (string?)bookRel.Attribute("TargetMode")=="External")throw new InvalidDataException("图数据文件名已占用。");
        existing?.Delete();
        using(var memory=new MemoryStream())
        {
            using(var doc=SpreadsheetDocument.Create(memory,SpreadsheetDocumentType.Workbook))
            {
                var wb=doc.AddWorkbookPart();wb.Workbook=new Xls.Workbook();
                var ws=wb.AddNewPart<WorksheetPart>();var rows=new Xls.SheetData();ws.Worksheet=new Xls.Worksheet(rows);
                static Xls.Cell Str(string s)=>new(){DataType=Xls.CellValues.InlineString,InlineString=new Xls.InlineString(new Xls.Text(s))};
                static Xls.Cell Num(double n)=>new(){CellValue=new Xls.CellValue(n)};
                rows.Append(new Xls.Row(new[]{"测线","平均偏航距（m）","设计架次","输入点数","有效点数","端外点数","无效坐标","任务 ID","母线 ID","批次 UTC","采用状态"}.Select(Str)));
                foreach(var p in points) rows.Append(new Xls.Row(Str(p.Name),Num(p.Mean!.Value),Str(p.Sortie),Num(p.Input),
                    Num(p.Valid),Num(p.Outside),Num(p.Invalid),Str(p.Task),Str(p.Mother),Str(batch.Generated),Str("审阅试算 · "+batch.Status)));
                wb.Workbook.Append(new Xls.Sheets(new Xls.Sheet{Id=wb.GetIdOfPart(ws),SheetId=1,Name="Sheet1"}));ws.Worksheet.Save();wb.Workbook.Save();
            }
            using var output=package.CreateEntry(bookPath,CompressionLevel.Optimal).Open();memory.Position=0;memory.CopyTo(output);
        }
        bookRel.Attribute("TargetMode")?.Remove();
        bookRel.SetAttributeValue("Type",R.NamespaceName+"/package");
        bookRel.SetAttributeValue("Target",new Uri("https://package/"+path).MakeRelativeUri(new Uri("https://package/"+bookPath)).ToString());
        var types=Xml(package,"[Content_Types].xml");var ns=types.Name.Namespace;
        if(!types.Elements().Any(e=>(string?)e.Attribute("Extension")=="xlsx" || (string?)e.Attribute("PartName")=="/"+bookPath))
        {
            types.Add(new XElement(ns+"Override",new XAttribute("PartName","/"+bookPath),new XAttribute("ContentType","application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")));
            Store(package,"[Content_Types].xml",types);
        }
        Store(package,path,chart);Store(package,relsPath,rels);
    }
}
