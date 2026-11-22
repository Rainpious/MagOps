using System.IO.Compression;
using System.Xml.Linq;
using static MagOps.Reporting.WordWorkfilePackage;

namespace MagOps.Reporting;

public sealed record AssemblySlot(string Position,string Input,string Bookmark,string[] Tags);
public sealed record TarmanAssemblyPreview(string Fingerprint,string? SourcePath,string SourceHash,TarmanParameter[] Changes,string[] HistoricalReferences);
public sealed partial class TarmanReport
{
    public static IReadOnlyList<AssemblySlot> AssemblySlots { get; } =
    [
        new("表 4-1","人员名册、岗位；扉页署名单独采用","MagTab4_1",["f77","f82"]),
        new("表 1-3","分项设计量、成图 KMZ 有效覆盖、设计段长度","MagTab1_3",["f7","f6","f8","f13","f14"]),
        new("图 5-2","逐线有效飞高、单位、高程基准和处理批次","MagFig5_2",["chart.5.2","f23"]),
        new("图 5-3","逐线动态噪声、磁通道和统计窗口","MagFig5_3",["chart.5.3","f24","f25"]),
        new("图 5-4","机载系统地面静态试验记录、通道和时段","MagFig5_4",["chart.5.4","f26"]),
        new("图 5-5","地面站静态试验记录、通道和时段","MagFig5_5",["chart.5.5","f27"]),
        new("表 5-1","原线与复测线、设计段、有效配对和剔除依据","MagTab5_1",["f108","f109","f29","f30","f31"]),
        new("图 5-6","与表 5-1 相同的线对、磁通道和改正版本","MagFig5_6",["figure.5.6"])
    ];
    private static readonly (string Key,string Label)[] Front =
        [("f1","卷首项目名称"),("f2","编写单位"),("f3","委托单位"),("f4","报告年月"),("f32","报告编写人"),("f33","项目负责署名"),("f34","单位负责署名")];
    private static readonly string[] StaffColumns=["姓名","性别","年龄","专业","职称","岗位","备注"];

    private sealed partial class Parts
    {
        private XElement AssemblyState => History.Element(State+"assembly") ?? Add(History,new XElement(State+"assembly"));
        private static XElement Add(XElement parent,XElement child){parent.Add(child);return child;}
        private XElement CaptionAt(string bookmark) =>
            document.Descendants(W+"bookmarkStart").SingleOrDefault(b=>(string?)b.Attribute(W+"name")==bookmark)?.Ancestors(W+"p").FirstOrDefault()
            ?? throw new InvalidDataException("工作稿缺少题注书签："+bookmark);

        internal XElement StaffTable()
        {
            var table=CaptionAt("MagTab4_1").ElementsAfterSelf().FirstOrDefault();
            if(table?.Name!=W+"tbl" || !table.Elements(W+"tr").First().Elements(W+"tc")
                .Select(c=>TarmanReferenceReport.Compact(TarmanReferenceReport.Visible(c)).Replace("年龄岁","年龄")).SequenceEqual(StaffColumns))
                throw new InvalidDataException("表 4-1 表位或七列表头已改动。");
            return table;
        }
        internal void InspectAssembly()
        {
            foreach(var slot in AssemblySlots)
            {
                CaptionAt(slot.Bookmark);
                foreach(var tag in slot.Tags)
                    if(Controls("tarman."+tag).Length==0)throw new InvalidDataException(slot.Position+"缺少装配位置 "+tag);
            }
            StaffTable();
        }
        private XElement[] Bound(string key)
        {
            var node=data.Element(M+key) ?? throw new InvalidDataException("绑定数据缺失："+key);
            var cs=Controls("tarman."+key);
            if(cs.Length==0 || cs.Any(c=>
            {
                var bind=c.Element(W+"sdtPr")?.Element(W+"dataBinding");
                return (string?)bind?.Attribute(W+"xpath")!="/m:fields[1]/m:"+key+"[1]" ||
                    !string.Equals((string?)bind?.Attribute(W+"storeItemID"),StoreId,StringComparison.OrdinalIgnoreCase) ||
                    !c.Descendants(W+"t").Any() || string.Concat(c.Descendants(W+"t").Select(t=>t.Value))!=node.Value;
            }))throw new InvalidDataException("显示值或 XML 映射须核对："+key);
            return cs;
        }
        private void SetBound(string key,string value)
        {
            var cs=Bound(key);
            foreach(var c in cs)
            {
                var content=c.Element(W+"sdtContent")!;
                if(content.Descendants().Any(e=>e.Name==W+"ins" || e.Name==W+"del"))throw new InvalidDataException("请先处理字段中的修订："+key);
                var texts=content.Descendants(W+"t").ToArray();texts[0].Value=value;
                foreach(var t in texts.Skip(1))t.Value="";
                c.Element(W+"sdtPr")?.Element(W+"showingPlcHdr")?.Remove();
            }
            data.Element(M+key)!.Value=value;
        }
        private static string[] RowText(XElement row)=>row.Elements(W+"tc").Select(TarmanReferenceReport.Visible).ToArray();
        private static bool Empty(string s)=>s=="" || s.StartsWith('«') || s=="待补录";
        private static string Roster(IEnumerable<string[]> rows)=>string.Join("\n",rows.Select(r=>string.Join("｜",r)));

        private static IEnumerable<string[]?> AlignStaff(List<string[]> current,List<string[]> incoming)
        {
            var byName=incoming.ToDictionary(r=>r[0]);
            var assigned=current.Where(r=>byName.ContainsKey(r[0])).Select(r=>r[0]).ToArray();
            if(assigned.Distinct().Count()!=assigned.Length)throw new InvalidDataException("工作稿存在同名人员，请先核实身份。");
            var remain=new Queue<string[]>(incoming.Where(r=>!assigned.Contains(r[0])));
            foreach(var r in current) yield return byName.GetValueOrDefault(r[0]) ?? (remain.Count>0?remain.Dequeue():null);
            while(remain.Count>0)yield return remain.Dequeue();
        }
        private static string PersonValue(string[]? person,int col) =>
            person==null?"«"+StaffColumns[col]+"»":person[col].Length==0?"待补录":person[col];

        internal TarmanParameter[] BatchChanges(TarmanReferenceReport source)
        {
            var h=AssemblyState;var list=new List<TarmanParameter>();
            foreach(var (key,label) in Front)
            {
                Bound(key);string current=data.Element(M+key)!.Value;
                if(!source.Cover.TryGetValue(key,out var next) || string.IsNullOrWhiteSpace(next) || next.Contains('«'))continue;
                string prev=(string?)h.Element(State+key)?.Attribute("written") ?? "";
                list.Add(new(key,label,prev,current,next,current!=next && (Empty(current) || current==prev)));
            }
            var rows=StaffTable().Elements(W+"tr").Skip(1).Select(RowText).ToArray();
            var planned=AlignStaff(rows.ToList(),source.Staff).Select((person,i)=>
                Enumerable.Range(0,6).Select(c=>PersonValue(person,c)).Append(i<rows.Length && rows[i][6]!="" ? rows[i][6] : person?[6] ?? "").ToArray());
            var actual=Roster(rows);var desired=Roster(planned);string before=(string?)h.Element(State+"staff")?.Element(State+"written") ?? "";
            list.Add(new("staff","表 4-1 人员（年龄按原报告）",before,actual,desired,
                actual!=desired && (actual==before || rows.All(r=>r.Take(6).All(Empty)))));
            return list.ToArray();
        }

        private void Note(XElement paragraph,string id,string value)
        {
            var hit=Controls("tarman.assembly."+id).SingleOrDefault();
            if(hit!=null)
            {
                if(hit.Parent!=paragraph)throw new InvalidDataException("来源标注位置已变更："+id);
                // Word can split a visible note into several runs when an author saves the file.
                if(TarmanReferenceReport.Visible(hit)==value)return;
                var texts=hit.Descendants(W+"t").ToArray();
                if(texts.Length==0)throw new InvalidDataException("来源标注缺少文字："+id);
                texts[0].Value=value;
                foreach(var extra in texts.Skip(1))extra.Value="";
                return;
            }
            paragraph.Add(new XElement(W+"sdt",new XElement(W+"sdtPr",new XElement(W+"tag",new XAttribute(W+"val","tarman.assembly."+id)),new XElement(W+"text")),
                new XElement(W+"sdtContent",new XElement(W+"r",new XElement(W+"rPr",new XElement(W+"color",new XAttribute(W+"val","536A77")),
                    new XElement(W+"sz",new XAttribute(W+"val","18"))),new XElement(W+"t",value)))));
        }
        internal void ValidateAssemblyForExport()
        {
            if(History.Element(State+"assembly") is not {} state)return;
            InspectAssembly();
            if((string?)state.Attribute("version")!="1" || state.Element(State+"slots")?.Elements().Count()!=AssemblySlots.Count)
                throw new InvalidDataException("第二轮装配索引缺失或版本不符。");
            foreach(var f in Front)Bound(f.Key);
            foreach(var tr in StaffTable().Elements(W+"tr").Skip(1))
                foreach(var tc in tr.Elements(W+"tc").Take(6))
                {
                    string tag=(string?)tc.Descendants(W+"tag").SingleOrDefault()?.Attribute(W+"val") ?? "";
                    if(!tag.StartsWith("tarman."))throw new InvalidDataException("表 4-1 人员字段未绑定。");
                    Bound(tag[7..]);
                }
        }
        internal void InstallSlots()
        {
            var h=AssemblyState;h.SetAttributeValue("version","1");
            var slots=new XElement(State+"slots",AssemblySlots.Select(s=>new XElement(State+"slot",new XAttribute("position",s.Position),
                new XAttribute("bookmark",s.Bookmark),new XAttribute("tags",string.Join(" ",s.Tags)),new XAttribute("input",s.Input))));
            h.Element(State+"slots")?.Remove();h.Add(slots);
            foreach(var slot in AssemblySlots.Where(s=>s.Bookmark!="MagTab4_1"))
                Note(CaptionAt(slot.Bookmark),slot.Bookmark,slot.Bookmark.StartsWith("MagFig") ? "（示例图）" : slot.Bookmark=="MagTab5_1" ? "（待核）" : "（待核）");
        }
        internal void MarkFirstBatch(string[] selected)
        {
            var cover=Controls("tarman.f2").First().Ancestors(W+"p").First();
            if(selected.Any(k=>k!="staff"))
            {
                Note(cover,"front","（历史样稿）");
            }
            if(selected.Contains("staff")) Note(CaptionAt("MagTab4_1"),"roster","（历史名册）");
        }
        private XElement NewStaffRow(XElement prototype,int index)
        {
            // Build a fresh row from formatting only. Word bookmarks, IDs and manual comments stay with their own rows.
            var row=new XElement(W+"tr");var props=prototype.Element(W+"trPr");if(props!=null)row.Add(new XElement(props));
            int col=0;
            foreach(var old in prototype.Elements(W+"tc"))
            {
                var cell=new XElement(W+"tc",old.Element(W+"tcPr") is {} cp ? new XElement(cp):null);
                var para=new XElement(W+"p",old.Descendants(W+"pPr").FirstOrDefault() is {} pp ? new XElement(pp):null);
                cell.Add(para);row.Add(cell);
                if(col<6)
                {
                    string key=$"assemblyPerson{index}Col{col}";data.Add(new XElement(M+key,"待补录"));
                    var model=old.Descendants(W+"sdt").First();var pr=new XElement(model.Element(W+"sdtPr")!);
                    pr.Element(W+"id")?.Remove();pr.Element(W+"tag")!.SetAttributeValue(W+"val","tarman."+key);
                    pr.Element(W+"dataBinding")!.SetAttributeValue(W+"xpath","/m:fields[1]/m:"+key+"[1]");
                    var run=new XElement(W+"r",model.Descendants(W+"rPr").FirstOrDefault() is {} rp?new XElement(rp):null,new XElement(W+"t","待补录"));
                    para.Add(new XElement(W+"sdt",pr,new XElement(W+"sdtContent",run)));
                }
                col++;
            }
            return row;
        }
        private void SetStaff(List<string[]> incoming)
        {
            var table=StaffTable();var rows=table.Elements(W+"tr").Skip(1).ToList();
            while(rows.Count<incoming.Count){var row=NewStaffRow(rows[0],rows.Count+1);table.Add(row);rows.Add(row);}
            var assigned=AlignStaff(rows.Select(RowText).ToList(),incoming).ToArray();
            foreach(var (row,person) in rows.Zip(assigned))
            {
                var old=RowText(row);var cells=row.Elements(W+"tc").ToArray();
                if(person?.FirstOrDefault()!=old[0] && !Empty(old[0]) && old[6]!="")
                    throw new InvalidDataException("人员替换涉及已有备注，请先核对："+old[0]);
                for(int i=0;i<6;i++)
                {
                    var tag=(string?)cells[i].Descendants(W+"tag").Single().Attribute(W+"val");
                    if(tag==null || !tag.StartsWith("tarman."))throw new InvalidDataException("人员表字段结构已改变。");
                    SetBound(tag[7..],PersonValue(person,i));
                }
                if(old[6]=="" && person!=null && person[6].Length>0)
                    cells[6].Elements(W+"p").First().Add(new XElement(W+"r",new XElement(W+"t",person[6])));
            }
        }
        internal void ApplyBatch(TarmanReferenceReport source,HashSet<string> selected)
        {
            var h=AssemblyState;
            h.Element(State+"historicalReferences")?.Remove();
            h.Add(new XElement(State+"historicalReferences",new XAttribute("purpose","历史对照"),new XAttribute("file",source.Path),
                new XAttribute("sha256",source.Hash),source.Evidence.Select(e=>new XElement(State+"item",
                    new XAttribute("position",e.Position),new XAttribute("summary",e.Summary),new XAttribute("part",e.Part),
                    new XAttribute("partSha256",e.PartHash),e.Rows.Select(row=>new XElement(State+"row",row))))));
            foreach(var change in BatchChanges(source).Where(c=>selected.Contains(c.Key)))
            {
                var record=h.Element(State+change.Key) ?? Add(h,new XElement(State+change.Key));
                if(change.Key=="staff")
                {
                    SetStaff(source.Staff);
                    record.SetElementValue(State+"written",Roster(StaffTable().Elements(W+"tr").Skip(1).Select(RowText)));
                }
                else {SetBound(change.Key,change.Source);record.SetAttributeValue("written",change.Source);}
                record.SetAttributeValue("file",source.Path);record.SetAttributeValue("sha256",source.Hash);
                record.SetAttributeValue("position",change.Key=="staff"?"项目组人员表":change.Key=="f3"?"第一章委托叙述":change.Key=="f1"?"首页题名与第一章委托叙述":"扉页署名表");
                record.SetAttributeValue("status","历史报告示例");
            }
        }
    }

    public TarmanAssemblyPreview PreviewAssembly(string path,string? sourcePath=null)
    {
        using var input=ReadSaved(path);string hash=Fingerprint(input);
        using var zip=new ZipArchive(input,ZipArchiveMode.Read);
        var parts=new Parts(zip,projectId,areaId);parts.InspectAssembly();
        if(sourcePath!=null && string.Equals(Path.GetFullPath(path),Path.GetFullPath(sourcePath),StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("请选择独立的历史报告作为来源。");
        var source=sourcePath==null?null:TarmanReferenceReport.Read(sourcePath);
        return new(hash,source?.Path,source?.Hash ?? "",source==null?[]:parts.BatchChanges(source),source?.Evidence.Select(e=>e.Position+" · "+e.Summary).ToArray() ?? []);
    }
    public void UpdateAssembly(string path,TarmanAssemblyPreview preview,string[] selected)
    {
        var source=preview.SourcePath==null?null:TarmanReferenceReport.Read(preview.SourcePath);
        if(source!=null && source.Hash!=preview.SourceHash)throw new IOException("来源报告已修改，请重新预览。");
        WordWorkfilePackage.Update(path,preview.Fingerprint,zip=>
        {
            var parts=new Parts(zip,projectId,areaId);parts.InspectAssembly();parts.InstallSlots();
            if(source!=null && selected.Length>0){parts.ApplyBatch(source,selected.ToHashSet());parts.MarkFirstBatch(selected);}
            parts.Save(zip);
        });
    }
}
