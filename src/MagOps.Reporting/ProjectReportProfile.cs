using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using MagOps.Infrastructure.Services;
using MagOps.Infrastructure.Storage;

namespace MagOps.Reporting;

public sealed record ProfileColumn(string Key, string Name, string Unit = "");
public sealed record ProfileSection(string Key, string Name, ProfileColumn[] Columns);
public sealed class ProfileEntry
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public required string Section { get; init; }
    public Dictionary<string, string> Values { get; init; } = [];
    public string Get(string key) => Values.GetValueOrDefault(key, "");
}

public sealed class ProjectReportProfile
{
    public int Version { get; init; } = 1;
    public List<ProfileEntry> Entries { get; init; } = [];
    public const string FileName = "report-profile.json";
    public static readonly ProfileSection[] Sections =
    [
        new("project", "项目单位", [new("client","委托单位全称"), new("organization","编写单位全称")]),
        new("person", "人员名册", [new("name","姓名"), new("sex","性别"), new("birth","出生年份或日期"),
            new("unit","所属单位"), new("specialty","专业"), new("title","职称"), new("office","单位职务"),
            new("duty","本项目岗位"), new("signature","本报告署名角色")]),
        new("platform", "飞行平台", [new("model","平台型号"), new("positioning","定位配置"), new("speed","最大飞行速度","m/s 或 km/h"),
            new("endurance","标称续航时间","min"), new("wind","抗风能力","m/s 或风级"), new("range","通信距离","km"), new("payload","载荷","kg")]),
        new("baseStation", "日变基站", [new("model","基站型号"), new("instrument","厂家与仪器类型"),
            new("performance","磁测性能"), new("resolution","磁场分辨率","nT"), new("sampling","标称采样设置"), new("temperature","温度特性"),
            new("memory","存储容量"), new("size","尺寸及重量"), new("mobileMode","流动站工作模式"),
            new("baseMode","基站工作模式与接口"), new("source","规格来源及页码")]),
        new("probe", "磁测探头", [new("model","探头型号"), new("type","探头类别"), new("atom","原子种类"),
            new("sensitivity","灵敏度","按型号资料"), new("sampleRate","最高采样率","Hz"), new("range","量程","nT"), new("gradient","梯度容限","nT/cm"),
            new("deadZone","死区与航向误差","°、nT"), new("temperature","工作温度","℃"), new("recorder","收录器"),
            new("power","收录器供电","V、A"), new("positioning","定位与姿态"), new("distance","无线传输距离","km")])
    ];

    public static IEnumerable<ReportField> FieldDefinitions()
    {
        foreach (var section in Sections)
        {
            foreach (var field in section.Columns.Where(c => c.Key != "birth"))
                yield return Definition(section, field);
            if (section.Key == "person") yield return Definition(section, new("age", "年龄", "岁"));
        }
    }

    private static ReportField Definition(ProfileSection section, ProfileColumn field) =>
        new($"profile.{section.Key}.{field.Key}", section.Key == "project" ? "卷首、第一章" : "卷首、第四章",
            field.Name, "资料属性", $"profile.{section.Key}.{field.Key}", "出发整备／" + section.Name, field.Unit,
            field.Key == "age" ? "报告年份减出生年份" : "由报告选择本项目记录", "缺值留空", "", "采用登记值及单位", "");

    public static (ProjectReportProfile Profile, string Revision) Load(string root)
    {
        if (!ProjectPaths.HasProject(root)) throw new InvalidDataException("请选择 MagOps 项目根目录。");
        string path = ProjectPaths.Json(root, FileName);
        if (!File.Exists(path)) return (new(), "");
        byte[] bytes = File.ReadAllBytes(path);
        var profile = JsonSerializer.Deserialize<ProjectReportProfile>(bytes, new JsonStore().Options)
            ?? throw new InvalidDataException("报告资料字典为空。");
        profile.Validate();
        return (profile, Convert.ToHexString(SHA256.HashData(bytes)));
    }

    public void Save(string root, string revision)
    {
        Validate();
        using var guard = WorkspaceFileLock.AcquireForRoot(root);
        if (Load(root).Revision != revision) throw new IOException("报告资料已在另一窗口更新，请重开后合并本次修改。");
        new JsonStore().Write(ProjectPaths.Json(root, FileName), this);
    }

    private static int? BirthYear(string text)
    {
        if (text.Length == 4 && int.TryParse(text, out int year) && year is >= 1000 and <= 9999) return year;
        return DateOnly.TryParseExact(text, ["yyyy-MM-dd","yyyy-M-d"], CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var date) ? date.Year : null;
    }

    private void Validate()
    {
        if (Version != 1) throw new InvalidDataException("报告资料字典版本无法读取。");
        var ids = new HashSet<string>();
        var models = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Entries.Count(x => x.Section == "project") > 1) throw new InvalidDataException("项目单位应只有一行。");
        foreach (var entry in Entries)
        {
            var section = Sections.SingleOrDefault(x => x.Key == entry.Section);
            if (section is null || !Guid.TryParse(entry.Id, out _) || !ids.Add(entry.Id))
                throw new InvalidDataException("资料类别或记录标识无效。");
            if (entry.Values.Any(p => p.Value is null || !section.Columns.Any(c => c.Key == p.Key)))
                throw new InvalidDataException("资料属性无法识别。");
            if (entry.Section != "project" && string.IsNullOrWhiteSpace(entry.Get(section.Columns[0].Key)))
                throw new InvalidDataException("请填写姓名或型号。");
            if (entry.Section == "person" && entry.Get("birth") is { Length: > 0 } birth && BirthYear(birth) is null)
                throw new InvalidDataException("出生资料请填写四位年份或 yyyy-MM-dd 日期。");
            if (entry.Section is "platform" or "probe" or "baseStation" && !models.Add(entry.Section + "/" + EquipmentCatalogService.NormalizeModel(entry.Get("model"))))
                throw new InvalidDataException("同类设备的型号重复，请合并参数。");
        }
    }

    public void Reuse(ProjectReportProfile source)
    {
        foreach (var entry in source.Entries)
        {
            if (entry.Section == "project" || Entries.Any(x => x.Id == entry.Id)) continue;
            if (entry.Section is "platform" or "probe" or "baseStation" && Entries.Any(x => x.Section == entry.Section &&
                string.Equals(EquipmentCatalogService.NormalizeModel(x.Get("model")), EquipmentCatalogService.NormalizeModel(entry.Get("model")), StringComparison.OrdinalIgnoreCase))) continue;
            var copy = new Dictionary<string,string>(entry.Values);
            copy.Remove("duty"); copy.Remove("signature");
            Entries.Add(new() { Id = entry.Id, Section = entry.Section, Values = copy });
        }
    }

    public IEnumerable<ReportSharedItem> Candidates(string? reportDate)
    {
        string dateText = reportDate?.Trim() ?? "";
        int? year = BirthYear(dateText);
        if (year is null && DateOnly.TryParseExact(dateText, ["yyyy-MM", "yyyy年M月", "yyyy年M月d日"],
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var month)) year = month.Year;
        foreach (var entry in Entries)
        {
            var section = Sections.Single(s => s.Key == entry.Section);
            string label = entry.Section == "project" ? "项目" : entry.Get(section.Columns[0].Key);
            if (entry.Section == "person" && entry.Get("unit").Length > 0) label += "（" + entry.Get("unit") + "）";
            if (entry.Section == "person" && Entries.Count(p => p.Section == "person" && p.Get("name") == entry.Get("name")) > 1)
                label += " [" + entry.Id[..8] + "]";
            string category = entry.Section is "platform" or "probe" or "baseStation" ? "设备" : "项目概况";
            ReportSharedItem Item(string key, string name, string value) => new(
                $"profile/{entry.Section}/{entry.Id}/{key}", category, label + " · " + name, value,
                $"{FileName}#{entry.Id}/{key}");
            foreach (var col in section.Columns)
            {
                string value = entry.Get(col.Key);
                if (col.Key == "birth")
                {
                    if (year is int y && BirthYear(value) is int born && y >= born)
                        yield return Item("age","年龄", (y - born).ToString(CultureInfo.InvariantCulture) + " 岁");
                }
                else if (value.Length > 0) yield return Item(col.Key, col.Name, value);
            }
        }
    }
}
