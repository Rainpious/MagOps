using MagOps.Infrastructure.Services;
using System.Text.Json;

namespace MagOps.Application;

public sealed record FileScanItem(string Path, long SizeBytes, string Extension, string Category,
    ArtifactIdentityInspection Identity);
public sealed record FileScanResult(string Root, int FileCount, long TotalBytes, IReadOnlyList<FileScanItem> Files);
public sealed record LayoutValidationResult(string ConfigurationPath, int ProjectFolderCount, int WorkareaFolderCount, int OperationCount);
public sealed record MagneticFileInspection(string Path, string PathCategory, string SensorFamily, string DataRole,
    int RecordCount, DateTimeOffset? FirstTimeUtc, DateTimeOffset? LastTimeUtc, string Evidence,
    IReadOnlyList<string> Warnings, bool RequiresReview, ArtifactIdentityInspection Identity);
public sealed record MagneticAnalysisResult(string Root, int FileCount, int UavRawFileCount, int BaseRawFileCount, int DerivedFileCount, IReadOnlyList<string> Warnings,
    IReadOnlyList<MagneticFileInspection> Files, string ParserVersion, bool ReadOnly)
{
    // Operation success means the inspection completed, not production approval.
    public bool HasReadableRecords => Files.Any(file => file.RecordCount > 0);
    public bool RequiresReview => Files.Count == 0 || Files.Any(file => file.RequiresReview);
}
public sealed record ProcessingManifestItem(string Path, string Name, long SizeBytes, DateTimeOffset LastWriteTime, IReadOnlyList<string> TopLevelKeys);
public sealed record ProcessingInspectionResult(string Root, int ManifestCount, IReadOnlyList<ProcessingManifestItem> Manifests, IReadOnlyList<string> Warnings);
public sealed record SourceClassificationResult(string Root, int FileCount, IReadOnlyDictionary<string, int> Counts, IReadOnlyList<string> Warnings,
    IReadOnlyList<FileScanItem> Files);

public sealed class ReadOnlyAnalysis
{
    public ApplicationResult<FileScanResult> ScanInputFolder(string path, bool recursive = true, int maxFiles = 5000)
    {
        string operationId = $"op-{Guid.NewGuid():N}";
        if (string.IsNullOrWhiteSpace(path) || maxFiles < 1)
            return ApplicationResult<FileScanResult>.Failure(operationId,
                new(ApplicationErrorCodes.ValidationError, "需要输入目录和正数文件上限。"));
        try
        {
            string root = Path.GetFullPath(path);
            if (!Directory.Exists(root))
                return ApplicationResult<FileScanResult>.Failure(operationId,
                    new(ApplicationErrorCodes.ValidationError, $"输入目录不存在: {root}"));
            var files = new List<FileScanItem>();
            foreach (string file in Directory.EnumerateFiles(root, "*", new EnumerationOptions
            {
                RecurseSubdirectories = recursive,
                IgnoreInaccessible = false,
                AttributesToSkip = FileAttributes.ReparsePoint
            }))
            {
                if (files.Count >= maxFiles)
                    break;
                FileInfo info = new(file);
                files.Add(new(file, info.Length, info.Extension.ToLowerInvariant(), Categorize(file, root),
                    GeneratedArtifactIdentity.Inspect(file)));
            }
            var result = new FileScanResult(root, files.Count, files.Sum(x => x.SizeBytes), files);
            return ApplicationResult<FileScanResult>.Success(operationId, null, result,
                files.Count >= maxFiles ? [new("TRUNCATED", $"文件数达到上限 {maxFiles}，结果已截断。")] : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ApplicationResult<FileScanResult>.Failure(operationId, new(ApplicationErrorCodes.EnvironmentError, ex.Message));
        }
    }

    public ApplicationResult<SourceClassificationResult> ClassifySourceFiles(string path, int maxFiles = 5000)
    {
        ApplicationResult<FileScanResult> scan = ScanInputFolder(path, maxFiles: maxFiles);
        if (!scan.Ok)
            return ApplicationResult<SourceClassificationResult>.Failure(scan.OperationId,
                scan.Errors.FirstOrDefault() ?? new(ApplicationErrorCodes.EnvironmentError, "无法扫描输入目录。"), scan.Context);

        var counts = scan.Result!.Files
            .GroupBy(x => x.Category, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase);
        var warnings = scan.Warnings.Select(x => x.Message).ToArray();
        return ApplicationResult<SourceClassificationResult>.Success(
            scan.OperationId,
            scan.Context,
            new(scan.Result.Root, scan.Result.FileCount, counts, warnings, scan.Result.Files),
            scan.Warnings);
    }

    public ApplicationResult<LayoutValidationResult> ValidateProjectLayout()
    {
        string operationId = $"op-{Guid.NewGuid():N}";
        try
        {
            FolderLayoutService service = new();
            var document = service.Load();
            return ApplicationResult<LayoutValidationResult>.Success(operationId, null,
                new(service.ConfigurationPath, document.ProjectFolders.Count, document.WorkareaFolders.Count, document.Operations.Count));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or FileNotFoundException)
        {
            return ApplicationResult<LayoutValidationResult>.Failure(operationId, new(ApplicationErrorCodes.EnvironmentError, ex.Message));
        }
    }

    public ApplicationResult<MagneticAnalysisResult> InspectMagneticData(string path)
    {
        string operationId = $"op-{Guid.NewGuid():N}";
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return ApplicationResult<MagneticAnalysisResult>.Failure(operationId,
                new(ApplicationErrorCodes.ValidationError, "需要存在的磁测输入目录。"));
        try
        {
            string root = Path.GetFullPath(path);
            var analysis = new FieldMagDataService().AnalyzeFolder(root);
            var files = analysis.Files.Select(x => InspectMagneticFile(x, root)).ToArray();
            var warnings = files.SelectMany(x => x.Warnings).Distinct(StringComparer.Ordinal).ToList();
            if (!files.Any(file => file.RecordCount > 0))
                warnings.Add("本次检查已完成，但没有解析到可用观测记录；不能据此继续生产计算。");
            return ApplicationResult<MagneticAnalysisResult>.Success(operationId, null,
                new(root, analysis.Files.Count, analysis.UavRawFileCount, analysis.BaseRawFileCount, analysis.DerivedFileCount, warnings,
                    files, FieldMagDataService.IncrementalCacheParserVersion, true),
                warnings.Select(x => new ApplicationWarning("MAGNETIC_PARSE_WARNING", x)).ToArray());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException or ArgumentException)
        {
            return ApplicationResult<MagneticAnalysisResult>.Failure(operationId, new(ApplicationErrorCodes.EnvironmentError, ex.Message));
        }
    }

    private static MagneticFileInspection InspectMagneticFile(FieldMagFileScan file, string root)
    {
        var warnings = (file.Warnings ?? []).ToList();
        var identity = GeneratedArtifactIdentity.Inspect(file.Path);
        if (identity.IsHistoricalVersion) warnings.Add("这是同一成果的历史快照，不作为当前生产输入。");
        if (identity.Warning is not null) warnings.Add(identity.Warning);
        if (identity.Status == "VERIFIED" && identity.Metadata!.Role == "processed_magnetic")
            warnings.Add("配套身份元数据标记为处理成果；解析器角色不代表原始输入，不能自动进入生产输入集合。");
        if (file.SensorFamily != FieldMagSensorFamily.Unknown && file.RecordCount == 0)
            warnings.Add("检测到格式特征，但未解析出记录；不能将此文件视为可用生产输入。");
        bool processingPath = file.Path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(x => x.StartsWith("06_数据处理", StringComparison.Ordinal));
        if (processingPath && file.DataRole == FieldMagDataRole.Raw)
            warnings.Add("已有解析器标记为 Raw，但路径位于数据处理目录；业务角色需要复核，不自动进入生产输入集合。");
        return new(file.Path, Categorize(file.Path, root), file.SensorFamily.ToString(), file.DataRole.ToString(),
            file.RecordCount, file.FirstTimeUtc, file.LastTimeUtc, file.Evidence, warnings,
            file.SensorFamily == FieldMagSensorFamily.Unknown || warnings.Count > 0, identity);
    }

    public ApplicationResult<ProcessingInspectionResult> InspectProcessingDirectory(string path, int maxManifests = 200)
    {
        string operationId = $"op-{Guid.NewGuid():N}";
        try
        {
            string root = Path.GetFullPath(path);
            if (!Directory.Exists(root))
                return ApplicationResult<ProcessingInspectionResult>.Failure(operationId,
                    new(ApplicationErrorCodes.ValidationError, $"处理目录不存在: {root}"));
            var manifests = new List<ProcessingManifestItem>();
            var warnings = new List<string>();
            foreach (string file in Directory.EnumerateFiles(root, "*manifest*.json", SearchOption.AllDirectories))
            {
                if (manifests.Count >= maxManifests) break;
                try
                {
                    using JsonDocument document = JsonDocument.Parse(File.ReadAllText(file));
                    manifests.Add(new(file, Path.GetFileName(file), new FileInfo(file).Length,
                        File.GetLastWriteTimeUtc(file), document.RootElement.ValueKind == JsonValueKind.Object
                            ? document.RootElement.EnumerateObject().Select(x => x.Name).OrderBy(x => x).ToArray()
                            : []));
                }
                catch (Exception ex) when (ex is IOException or JsonException)
                {
                    warnings.Add($"无法读取 Manifest {file}: {ex.Message}");
                }
            }
            if (manifests.Count >= maxManifests) warnings.Add($"Manifest 数达到上限 {maxManifests}，结果已截断。");
            return ApplicationResult<ProcessingInspectionResult>.Success(operationId, null,
                new(root, manifests.Count, manifests, warnings),
                warnings.Select(x => new ApplicationWarning("MANIFEST_READ_WARNING", x)).ToArray());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ApplicationResult<ProcessingInspectionResult>.Failure(operationId, new(ApplicationErrorCodes.EnvironmentError, ex.Message));
        }
    }

    private static string Categorize(string file, string root)
    {
        string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
        string lower = relative.ToLowerInvariant();
        if (lower.Contains("05_外业") || lower.Contains("06_数据处理")) return "field_or_processing";
        if (lower.Contains("航线") || lower.EndsWith(".kml") || lower.EndsWith(".kmz")) return "route_or_spatial";
        if (lower.EndsWith(".csv") || lower.EndsWith(".txt") || lower.EndsWith(".dat")) return "text_data";
        return "other";
    }
}
