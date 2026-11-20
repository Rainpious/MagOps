using System.Text.Json;
using MagOps.Infrastructure.Storage;

namespace MagOps.Infrastructure.Services;

public sealed class GlobalWorkspaceSettings
{
    public string Root { get; set; } = "";
    public bool AutomaticOutput { get; set; } = true;
    public string ProjectsDirectory { get; set; } = "";
    public string StandaloneDirectory { get; set; } = "";
}

/// <summary>Local routing preferences only. Saving never moves projects, libraries or inputs.</summary>
public sealed class GlobalWorkspaceService
{
    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments, Environment.SpecialFolderOption.DoNotVerify), "MagOps");
    public static string? SettingsPathOverride { get; set; }
    public static string SettingsPath => SettingsPathOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MagOps", "workspace-settings.json");
    public GlobalWorkspaceSettings Load() => File.Exists(SettingsPath)
        ? JsonSerializer.Deserialize<GlobalWorkspaceSettings>(File.ReadAllText(SettingsPath))
            ?? throw new InvalidDataException("工作目录配置为空。") : new();
    public void Save(GlobalWorkspaceSettings settings)
    {
        settings.Root = ValidateDirectory(string.IsNullOrWhiteSpace(settings.Root) ? DefaultRoot : settings.Root);
        settings.ProjectsDirectory = OptionalDirectory(settings.ProjectsDirectory);
        settings.StandaloneDirectory = OptionalDirectory(settings.StandaloneDirectory);
        Directory.CreateDirectory(settings.Root);
        // Verify writes before committing preferences; never fall back silently to another root.
        string probe = Path.Combine(settings.Root, ".magops-write-" + Guid.NewGuid().ToString("N"));
        using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
        AtomicFileWriter.Write(SettingsPath, stream => JsonSerializer.Serialize(stream, settings));
    }
    private static string OptionalDirectory(string value) => string.IsNullOrWhiteSpace(value) ? "" : ValidateDirectory(value);
    private static string ValidateDirectory(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
            throw new InvalidDataException("请选择完整的文件夹路径。");
        string path = Path.GetFullPath(value);
        if (File.Exists(path)) throw new IOException("所选路径是文件，不是文件夹。");
        return path;
    }
    public string ProjectParent()
    {
        var s = Load(); RequireRoot(s);
        return s.ProjectsDirectory.Length > 0 ? s.ProjectsDirectory : Path.Combine(s.Root, "项目");
    }
    public string Standalone(string operation)
    {
        var s = Load(); RequireRoot(s);
        string root = s.StandaloneDirectory.Length > 0 ? s.StandaloneDirectory : Path.Combine(s.Root, "临时任务");
        return Path.Combine(root, OperationFolder(operation));
    }
    public static string OperationFolder(string key) => key switch
    {
        "raw_import" => "原始资料", "project_terrain" => "地形",
        "coordinate_export" or "coordinate_kml_range" or "coordinate_spatial_export" => "空间资料",
        "coordinate_oasis_ply" or "route_endpoint_design" or "route_kml_subdivision" or "route_preprocess" or
        "route_export" or "route_kml_import" or "route_project" => "航线",
        "processing_diurnal" => "日变", "processing_excel" => "表格", "processing_oasis" => "外部成果",
        "processing_analysis" => "分析", "field_data_integration" => "数据整理",
        "figure_export" => "图件", "report_export" => "报告", "quality-output" => "质量",
        _ => WorkspaceService.Slug(key)
    };
    public void EnsureDefaultLibrary()
    {
        var s = Load(); if (s.Root.Length == 0) return;
        var library = new TerrainLibraryService();
        if (library.Settings().DefaultDirectory.Length == 0)
            library.SetDefault(Path.Combine(s.Root, "公共库"));
    }
    private static void RequireRoot(GlobalWorkspaceSettings s)
    {
        if (s.Root.Length == 0) throw new InvalidOperationException("请先设置全局工作目录。");
        if (!Directory.Exists(s.Root)) throw new DirectoryNotFoundException("工作目录不可用，请重新连接；不会自动写入其他位置。");
    }
    public static string UniqueFile(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        string parent = Path.GetDirectoryName(path)!;
        for (int i = 2; ; i++)
        {
            string candidate = Path.Combine(parent, Path.GetFileNameWithoutExtension(path) + "_v" + i + Path.GetExtension(path));
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
    }
    public static bool IsCompact(string root) => File.Exists(Path.Combine(root, ".magops", "folder-layout.json"));
    public static void PrepareNewProject(string root)
    {
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            throw new IOException("新项目目录必须为空；已有项目请直接打开。");
        var layout = new FolderLayoutService().Load();
        foreach (var f in layout.ProjectFolders) f.RelativePath = f.Key switch {
            "project.workareas" => "工区", "project.management" => "00_项目管理",
            "project.basis" => "01_公共资料", "project.spatial" => "02_总体空间",
            "project.aggregate" => "03_汇总成果", "project.delivery" => "04_交付",
            "project.pending" => "99_待整理", _ => "00_项目管理" };
        foreach (var f in layout.WorkareaFolders) f.RelativePath = f.Key switch {
            "workarea.management" => ".magops", "workarea.raw" => "01_原始资料",
            "workarea.registry" => ".magops", "workarea.coordinate" => "02_空间资料",
            "workarea.route" => "03_航线", "workarea.field" => "04_外业记录",
            "workarea.processing" => "05_数据处理", "workarea.figures" => "06_图件",
            "workarea.report" => "07_报告", _ => "99_待整理" };
        foreach (var op in layout.Operations) op.Subfolder = op.Key switch {
            "raw_import" => "{yyyyMMdd_HHmmssfff}", "project_terrain" => "地形",
            "processing_diurnal" => "日变", "field_data_integration" => "数据整理",
            "processing_excel" => "表格", "processing_oasis" => "外部成果", "processing_analysis" => "分析",
            "field_gemlink_bridge" => "GEMLink", "field_evidence" => "现场证据", _ => "" };
        TerrainLibraryService.Write(Path.Combine(root, ".magops", "folder-layout.json"), layout);
    }
}
