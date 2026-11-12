using System.Text.Json;
using MagOps.Infrastructure.Storage;
using MagOps.Infrastructure.Services;

namespace MagOps.Reporting;

public static class ReportInputStore
{
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    // Resolve against the selected workarea and reject escapes before any writes.
    public static string DirectoryFor(string root, string workarea)
    {
        root = Path.GetFullPath(root);
        if (!ProjectPaths.HasProject(root)) throw new InvalidOperationException("请先打开有效的 MagOps 项目。");
        if (string.IsNullOrWhiteSpace(workarea) || Path.IsPathRooted(workarea))
            throw new InvalidOperationException("请选择当前项目中的工区。");
        string area = Path.GetFullPath(Path.Combine(root, workarea));
        if (!area.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("报告工区路径超出项目目录。");
        return Path.Combine(new FolderLayoutService().ResolveOperationDirectory(root, "report_export", workarea), "MagOps报告");
    }

    public static ReportInputs Load(string root, string workarea)
    {
        string path = Path.Combine(DirectoryFor(root, workarea), "report-inputs.json");
        if (!File.Exists(path)) return new();
        var value = JsonSerializer.Deserialize<ReportInputs>(File.ReadAllText(path), Json)
            ?? throw new InvalidDataException("报告资料为空。");
        if (value.SchemaVersion != 1) throw new InvalidDataException("报告资料版本不受支持，请保留原文件后升级。");
        return value;
    }

    public static void Save(string root, string workarea, ReportInputs inputs)
    {
        string directory = DirectoryFor(root, workarea);
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "report-inputs.json");
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(inputs, Json));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
