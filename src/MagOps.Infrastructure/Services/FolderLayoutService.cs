using System.Reflection;
using System.Text.Json;

namespace MagOps.Infrastructure.Services;

/// <summary>
/// Reads the user-maintainable project-folder layout and resolves managed output paths.
/// IMPORTANT: Resolve* methods never create business folders. Ensure* methods create
/// them only at the moment a file is actually going to be written.
/// </summary>
public sealed class FolderLayoutService
{
    public const string ConfigFileName = "project-folder-layout.v1.json";
    public const string EmbeddedResourceName =
        "MagOps.Infrastructure.Resources.project-folder-layout.v1.json";

    private readonly string? _explicitConfigPath;

    public FolderLayoutService(string? configPath = null)
    {
        _explicitConfigPath = string.IsNullOrWhiteSpace(configPath)
            ? null
            : Path.GetFullPath(configPath);
    }

    public string ConfigurationPath
    {
        get
        {
            if (_explicitConfigPath is not null)
                return _explicitConfigPath;

            string? env = Environment.GetEnvironmentVariable("MAGOPS_FOLDER_LAYOUT");
            if (!string.IsNullOrWhiteSpace(env))
                return Path.GetFullPath(env);

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MagOps",
                "config",
                ConfigFileName);
        }
    }

    public FolderLayoutDocument Load()
    {
        string json;
        string path = ConfigurationPath;

        if (File.Exists(path))
        {
            json = File.ReadAllText(path);
        }
        else if (UseLegacyApplicationDirectoryConfig() && File.Exists(LegacyApplicationDirectoryPath()))
        {
            // Preserve user customizations created by older releases that read the
            // editable JSON beside the application. The next explicit edit migrates
            // it into LocalAppData, so the installation directory can be read-only.
            json = File.ReadAllText(LegacyApplicationDirectoryPath());
        }
        else
        {
            Assembly assembly = typeof(FolderLayoutService).Assembly;
            using Stream stream = assembly.GetManifestResourceStream(EmbeddedResourceName)
                ?? throw new FileNotFoundException(
                    $"目录结构配置不存在，且内嵌默认配置不可用：{path}");
            using var reader = new StreamReader(stream);
            json = reader.ReadToEnd();
        }

        FolderLayoutDocument document = JsonSerializer.Deserialize<FolderLayoutDocument>(
            json,
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            }) ?? throw new InvalidDataException("目录结构配置无法解析。");

        Validate(document);
        return document;
    }

    private FolderLayoutDocument LoadForProject(string root)
    {
        string local = Path.Combine(Path.GetFullPath(root), ".magops", "folder-layout.json");
        return _explicitConfigPath is null && File.Exists(local)
            ? new FolderLayoutService(local).Load() : Load();
    }

    public void EnsureEditableConfigurationFile()
    {
        string path = ConfigurationPath;
        if (File.Exists(path)) return;

        string? parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(parent))
            Directory.CreateDirectory(parent);

        if (UseLegacyApplicationDirectoryConfig() && File.Exists(LegacyApplicationDirectoryPath()))
        {
            File.Copy(LegacyApplicationDirectoryPath(), path, overwrite: false);
            return;
        }

        FolderLayoutDocument defaults = Load();
        File.WriteAllText(path, JsonSerializer.Serialize(defaults, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(
                System.Text.Unicode.UnicodeRanges.All)
        }));
    }

    private bool UseLegacyApplicationDirectoryConfig() =>
        _explicitConfigPath is null &&
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MAGOPS_FOLDER_LAYOUT"));

    private static string LegacyApplicationDirectoryPath() => Path.Combine(
        AppContext.BaseDirectory,
        "config",
        ConfigFileName);

    public FolderOperationEntry GetOperation(string operationKey)
    {
        FolderLayoutDocument document = Load();
        return document.Operations.FirstOrDefault(x =>
                   x.Key.Equals(operationKey, StringComparison.OrdinalIgnoreCase))
               ?? throw new KeyNotFoundException($"目录配置中未定义操作：{operationKey}");
    }

    public string ResolveOperationDirectory(
        string projectRoot,
        string operationKey,
        string workareaRelativePath = "",
        DateTimeOffset? timestamp = null)
    {
        projectRoot = Path.GetFullPath(projectRoot);
        FolderLayoutDocument document = LoadForProject(projectRoot);
        FolderOperationEntry operation = document.Operations.FirstOrDefault(x =>
                   x.Key.Equals(operationKey, StringComparison.OrdinalIgnoreCase))
               ?? throw new KeyNotFoundException($"目录配置中未定义操作：{operationKey}");

        FolderLayoutEntry folder;
        string baseRoot;

        if (operation.Scope.Equals("project", StringComparison.OrdinalIgnoreCase))
        {
            folder = document.ProjectFolders.FirstOrDefault(x =>
                         x.Key.Equals(operation.FolderKey, StringComparison.OrdinalIgnoreCase))
                     ?? throw new KeyNotFoundException(
                         $"项目目录定义不存在：{operation.FolderKey}");
            baseRoot = projectRoot;
        }
        else if (operation.Scope.Equals("workarea", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(workareaRelativePath))
                throw new InvalidOperationException(
                    $"操作 {operationKey} 属于工区生产环节，必须指定目标工区。");

            folder = document.WorkareaFolders.FirstOrDefault(x =>
                         x.Key.Equals(operation.FolderKey, StringComparison.OrdinalIgnoreCase))
                     ?? throw new KeyNotFoundException(
                         $"工区目录定义不存在：{operation.FolderKey}");

            baseRoot = SafeCombine(projectRoot, workareaRelativePath);
        }
        else
        {
            throw new InvalidDataException(
                $"操作 {operationKey} 的 scope 必须是 project 或 workarea。");
        }

        string result = SafeCombine(baseRoot, folder.RelativePath);
        if (!string.IsNullOrWhiteSpace(operation.Subfolder))
        {
            string expanded = ExpandTokens(
                operation.Subfolder,
                timestamp ?? DateTimeOffset.Now);
            result = SafeCombine(result, expanded);
        }

        return result;
    }

    public string EnsureOperationDirectory(
        string projectRoot,
        string operationKey,
        string workareaRelativePath = "",
        DateTimeOffset? timestamp = null)
    {
        string path = ResolveOperationDirectory(
            projectRoot, operationKey, workareaRelativePath, timestamp);
        Directory.CreateDirectory(path);
        return path;
    }

    public string ResolveProjectFolder(string projectRoot, string folderKey)
    {
        FolderLayoutDocument document = LoadForProject(projectRoot);
        FolderLayoutEntry folder = document.ProjectFolders.FirstOrDefault(x =>
                   x.Key.Equals(folderKey, StringComparison.OrdinalIgnoreCase))
               ?? throw new KeyNotFoundException($"项目目录定义不存在：{folderKey}");
        return SafeCombine(Path.GetFullPath(projectRoot), folder.RelativePath);
    }

    public string ResolveWorkareaFolderFromRoot(
        string workareaRoot,
        string folderKey)
    {
        string? project = Path.GetFullPath(workareaRoot);
        while (project is not null && !MagOps.Infrastructure.Storage.ProjectPaths.HasProject(project)) project = Path.GetDirectoryName(project);
        FolderLayoutDocument doc = project is null ? Load() : LoadForProject(project);
        FolderLayoutEntry folder = doc.WorkareaFolders.FirstOrDefault(x =>
            x.Key.Equals(folderKey, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"未知工区目录键：{folderKey}");
        return Path.GetFullPath(Path.Combine(
            Path.GetFullPath(workareaRoot),
            folder.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
    }

    public string ResolveWorkareaFolder(
        string projectRoot,
        string workareaRelativePath,
        string folderKey)
    {
        FolderLayoutDocument document = LoadForProject(projectRoot);
        FolderLayoutEntry folder = document.WorkareaFolders.FirstOrDefault(x =>
                   x.Key.Equals(folderKey, StringComparison.OrdinalIgnoreCase))
               ?? throw new KeyNotFoundException($"工区目录定义不存在：{folderKey}");
        string workareaRoot = SafeCombine(Path.GetFullPath(projectRoot), workareaRelativePath);
        return SafeCombine(workareaRoot, folder.RelativePath);
    }

    public static string ExpandTokens(string template, DateTimeOffset timestamp)
    {
        return template
            .Replace("{yyyyMMdd}", timestamp.ToString("yyyyMMdd"), StringComparison.Ordinal)
            .Replace("{yyyyMMdd_HHmm}", timestamp.ToString("yyyyMMdd_HHmm"), StringComparison.Ordinal)
            .Replace("{yyyyMMdd_HHmmss}", timestamp.ToString("yyyyMMdd_HHmmss"), StringComparison.Ordinal)
            .Replace("{yyyyMMdd_HHmmssfff}", timestamp.ToString("yyyyMMdd_HHmmssfff"), StringComparison.Ordinal);
    }

    private static string SafeCombine(string basePath, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return Path.GetFullPath(basePath);

        if (Path.IsPathRooted(relativePath))
            throw new InvalidDataException($"目录配置必须使用相对路径：{relativePath}");

        string baseFull = Path.GetFullPath(basePath);
        string combined = Path.GetFullPath(
            Path.Combine(baseFull, relativePath.Replace('/', Path.DirectorySeparatorChar)));

        string prefix = baseFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                        + Path.DirectorySeparatorChar;
        if (!combined.Equals(baseFull, StringComparison.OrdinalIgnoreCase) &&
            !combined.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"目录配置越出了允许范围：{relativePath}");
        }

        return combined;
    }

    private static void Validate(FolderLayoutDocument document)
    {
        if (!document.SchemaVersion.Equals("folder-layout-1", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"不支持的目录结构配置版本：{document.SchemaVersion}");

        ValidateUnique(document.ProjectFolders.Select(x => x.Key), "project_folders");
        ValidateUnique(document.WorkareaFolders.Select(x => x.Key), "workarea_folders");
        ValidateUnique(document.Operations.Select(x => x.Key), "operations");

        foreach (FolderLayoutEntry folder in document.ProjectFolders.Concat(document.WorkareaFolders))
        {
            if (string.IsNullOrWhiteSpace(folder.Key) ||
                string.IsNullOrWhiteSpace(folder.RelativePath))
                throw new InvalidDataException("目录项必须填写 key 和 relative_path。");
            ValidateRelative(folder.RelativePath);
        }

        foreach (FolderOperationEntry operation in document.Operations)
        {
            if (string.IsNullOrWhiteSpace(operation.Key) ||
                string.IsNullOrWhiteSpace(operation.FolderKey))
                throw new InvalidDataException("操作项必须填写 key 和 folder_key。");
            if (operation.CreatePolicy != "on_write")
                throw new InvalidDataException(
                    $"当前仅支持 create_policy=on_write：{operation.Key}");
            if (!string.IsNullOrWhiteSpace(operation.Subfolder))
                ValidateRelative(operation.Subfolder
                    .Replace("{yyyyMMdd}", "date")
                    .Replace("{yyyyMMdd_HHmm}", "date")
                    .Replace("{yyyyMMdd_HHmmss}", "date")
                    .Replace("{yyyyMMdd_HHmmssfff}", "date"));
        }
    }

    private static void ValidateRelative(string relative)
    {
        if (Path.IsPathRooted(relative))
            throw new InvalidDataException($"必须使用相对路径：{relative}");

        string normalized = relative.Replace('\\', '/');
        if (normalized.Split('/').Any(x => x == ".."))
            throw new InvalidDataException($"目录配置不允许 '..'：{relative}");
    }

    private static void ValidateUnique(IEnumerable<string> keys, string section)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string key in keys)
        {
            if (!seen.Add(key))
                throw new InvalidDataException($"{section} 存在重复 key：{key}");
        }
    }
}
