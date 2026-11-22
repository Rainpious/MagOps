using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.VisualBasic.FileIO;

namespace MagOps.Reporting;

public sealed record OffsetFigureLine(string Name, string Mother, string Task, string Sortie, int Input,
    int Valid, int Outside, int Invalid, double? Mean);
public sealed record OffsetFigureFile(string Path, string Sha256);
public sealed record TarmanOffsetBatch(string Design, string Generated, string Authority, string Status,
    int Passed, OffsetFigureFile[] Files, OffsetFigureLine[] Lines)
{
    public IEnumerable<OffsetFigureLine> Plotted => Lines.Where(l => l.Valid > 0);
    public double Mean => Plotted.Average(l => l.Mean!.Value);
    public OffsetFigureLine Maximum => Plotted.MaxBy(l => l.Mean)!;
    public string Summary => $"{Generated} · {Status} · {Plotted.Count()}/{Lines.Length} 条线 · {Lines.Sum(l => l.Valid)} 有效 / {Lines.Sum(l => l.Input)} 输入 · {Lines.Sum(l => l.Outside)} 端外 · {Lines.Sum(l => l.Invalid)} 无效坐标 · 质量通过 {Passed} Pass";

    private sealed class Line(string name, string mother, string task, string sortie, ReportRouteGeometry geometry)
    {
        public readonly string Name = name;
        public int Count, Outside, Invalid, Valid;
        public double Sum;
        public void Add(string lonText,string latText)
        {
            Count++;
            if (!Coordinate(lonText,180,out double lon) || !Coordinate(latText,90,out double lat)) { Invalid++; return; }
            var hit = geometry.EvaluateHorizontal(lon,lat);
            if (hit.Status == "END_OUTSIDE") Outside++;
            else if (hit.Offset is double distance) { Sum += distance; Valid++; }
            else throw new InvalidDataException("设计几何不可计算：" + Name);
        }
        public OffsetFigureLine Finish() => new(Name,mother,task,sortie,Count,Valid,Outside,Invalid,Valid==0?null:Sum/Valid);
    }

    private static bool Coordinate(string s,int max,out double value) =>
        double.TryParse(s,NumberStyles.Float,CultureInfo.InvariantCulture,out value) && double.IsFinite(value) && Math.Abs(value)<=max;
    private static string Text(JsonElement element,string key) =>
        element.TryGetProperty(key,out var value) && value.ValueKind==JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
        ? value.GetString()! : throw new InvalidDataException("来源缺少字段：" + key);

    private static string Child(string folder,string relative)
    {
        string root=Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
        string path=Path.GetFullPath(relative,root);
        if(Path.IsPathRooted(relative) || !path.StartsWith(root,StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("来源清单路径越界。");
        return path;
    }
    private static TextFieldParser Csv(byte[] bytes)
    {
        var reader = new TextFieldParser(new MemoryStream(bytes),System.Text.Encoding.UTF8,true);
        reader.SetDelimiters(","); return reader;
    }
    public static TarmanOffsetBatch Read(string routePath,string batchPath,string project,string area)
    {
        var files=new List<OffsetFigureFile>();
        byte[] ReadFile(string path,string? expected=null)
        {
            byte[] bytes=File.ReadAllBytes(path);string hash=Convert.ToHexString(SHA256.HashData(bytes));
            if(expected!=null && !hash.Equals(expected,StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("来源文件与清单哈希不符：" + Path.GetFileName(path));
            files.Add(new(Path.GetFullPath(path),hash));return bytes;
        }
        JsonElement Json(string path)
        {
            using var stream=new StreamReader(new MemoryStream(ReadFile(path)));
            using var doc=JsonDocument.Parse(stream.ReadToEnd());return doc.RootElement.Clone();
        }
        void CheckContext(JsonElement obj)
        {
            if(Text(obj,"project_id")!=project || Text(obj,"workarea_id")!=area) throw new InvalidDataException("来源与工作稿的项目或工区身份不符。");
        }
        var route=Json(routePath);var batch=Json(batchPath);
        CheckContext(route);CheckContext(batch.GetProperty("artifact_context"));
        if(Text(route,"status")!="PASSED") throw new InvalidDataException("航线设计清单未通过。");
        string routeDir=Path.GetDirectoryName(Path.GetFullPath(routePath))!;
        var endpoints=new Dictionary<(string,string),List<ReportRouteGeometry.Vertex>>();
        foreach(var file in route.GetProperty("output_sha256").EnumerateObject().Where(p=>p.Name.EndsWith(".csv",StringComparison.OrdinalIgnoreCase)))
        {
            using var parser=Csv(ReadFile(Child(routeDir,file.Name),file.Value.GetString()));
            while(!parser.EndOfData)
            {
                var row=parser.ReadFields()!;
                if(row.Length!=3 || !Coordinate(row[1],90,out var lat) || !Coordinate(row[2],180,out var lon)) throw new InvalidDataException("设计端点 CSV 应为线号、纬度、经度。");
                var key=(Path.GetFileNameWithoutExtension(file.Name),row[0]);
                if(!endpoints.TryGetValue(key,out var points)) endpoints[key]=points=[];
                points.Add(new(lon,lat,null));
            }
        }
        var tasks=new Dictionary<string,Line>();var mothers=new HashSet<string>();var ordered=new List<Line>();
        foreach(var item in route.GetProperty("survey_task_references").EnumerateArray())
        {
            string task=Text(item,"survey_task_stable_id"),name=Text(item,"line_name"),sortie=Text(item,"sortie_name"),mother=Text(item,"mother_line_stable_id");
            if(!endpoints.TryGetValue((sortie,name),out var points) || points.Count!=2 || points[0]==points[1] || !mothers.Add(mother) || Text(item,"survey_purpose")!="NORMAL")
                throw new InvalidDataException("请核实逐母线的设计端点、分段或非普通测线：" + name);
            var line=new Line(name,mother,task,sortie,new(){Vertices=points.ToArray(),AltitudeMode=""});
            if(!tasks.TryAdd(task,line))throw new InvalidDataException("设计任务身份重复：" + name);
            ordered.Add(line);
        }
        string csv=Text(batch.GetProperty("storage"),"processing_output");
        var entry=batch.GetProperty("artifact_identities").EnumerateArray().Single(e=>Text(e,"file")==csv);
        CheckContext(entry);
        if(Text(entry,"role")!="processed_magnetic") throw new InvalidDataException("不是处理点集。");
        string dir=Path.GetDirectoryName(Path.GetFullPath(batchPath))!;
        var metadata=Json(Child(dir,Text(entry,"metadata_file")));CheckContext(metadata);
        string hash=Text(entry,"content_sha256");
        if(!hash.Equals(Text(metadata,"content_sha256"),StringComparison.OrdinalIgnoreCase) || Text(metadata,"artifact_id")!=Text(entry,"artifact_id"))
            throw new InvalidDataException("批次清单与点集元数据不符。");
        using var input=Csv(ReadFile(Child(dir,csv),hash));
        var header=input.ReadFields() ?? throw new InvalidDataException("处理 CSV 为空。");
        int Column(string key) => header.Count(k=>k==key)==1 ? Array.IndexOf(header,key) : throw new InvalidDataException("处理点集缺少唯一列：" + key);
        int ti=Column("LineTaskId"),ni=Column("LineName"),xi=Column("Longitude"),yi=Column("Latitude");
        while(!input.EndOfData)
        {
            var row=input.ReadFields()!;
            if(row.Length!=header.Length || !tasks.TryGetValue(row[ti],out var line) || line.Name!=row[ni])
                throw new InvalidDataException("实飞任务 ID 或线号与设计不符；请核对选用的设计版本。");
            line.Add(row[xi],row[yi]);
        }
        var lines=ordered.Select(l=>l.Finish()).ToArray();
        if(!lines.Any(l=>l.Valid>0))throw new InvalidDataException("当前批次没有可计算偏航距的测线。");
        var review=batch.GetProperty("production_review");
        if(lines.Sum(l=>l.Input)!=review.GetProperty("selected_samples").GetInt32())throw new InvalidDataException("处理点数与清单选点数不符。");
        return new(Text(route,"route_id"),Text(batch,"generated_at_utc"),Text(batch,"production_authority"),Text(batch,"production_status"),
            review.GetProperty("quality_passed_passes").GetInt32(),files.ToArray(),lines);
    }
}
