using System.Text.Json;
using MagOps.Application;
using MagOps.Infrastructure.Storage;
using MagOps.Infrastructure.Services;

namespace MagOps.Cli;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static int Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        try { ValidateArguments(args); return Run(args, cancellation.Token); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or InvalidDataException or IOException or UnauthorizedAccessException or JsonException or OperationCanceledException or System.Net.Http.HttpRequestException or System.Security.Cryptography.CryptographicException)
        {
            var result = ApplicationResult<object?>.Failure("op-" + Guid.NewGuid().ToString("N"),
                new(ex is OperationCanceledException ? "CANCELLED" : ex is IOException or UnauthorizedAccessException ? "ENVIRONMENT_ERROR" : "VALIDATION_ERROR", ex.Message));
            if (args.Contains("--json", StringComparer.OrdinalIgnoreCase)) Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
            else PrintHuman("error", result);
            return 2;
        }
        finally { Console.CancelKeyPress -= cancel; }
    }

    private static ApplicationResult<object?> ServiceResult(Func<object> run) => ApplicationResult<object?>.Success("op-" + Guid.NewGuid().ToString("N"), null, run());
    private static object CopernicusCommand(string? path, bool download, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("需要 --input 范围 JSON。");
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        var b = json.RootElement;
        var names = new HashSet<string> { "west", "south", "east", "north" };
        int resolution = 30;
        if (b.ValueKind != JsonValueKind.Object) throw new ArgumentException("需要范围 JSON 对象。");
        if (b.TryGetProperty("resolution", out var r))
        {
            names.Add("resolution");
            if (r.ValueKind != JsonValueKind.Number || !r.TryGetInt32(out resolution)) throw new ArgumentException("resolution 须为 30 或 90。");
        }
        foreach (var p in b.EnumerateObject())
            if (!names.Remove(p.Name) || p.Value.ValueKind != JsonValueKind.Number) throw new ArgumentException("范围字段无效或重复。");
        if (names.Count != 0) throw new ArgumentException("需要 west、south、east、north 四个数字。");
        var request = new TerrainCoverageRequest(b.GetProperty("west").GetDouble(), b.GetProperty("south").GetDouble(),
            b.GetProperty("east").GetDouble(), b.GetProperty("north").GetDouble());
        return download ? CopernicusDem.EnsureAsync(request, resolution, token: token).GetAwaiter().GetResult()
            : CopernicusDem.Plan(request, resolution);
    }
    private static IReadOnlyList<Aw3d30Tile> ReadTerrainPlan(string path)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(path)); var b = json.RootElement;
        if (b.ValueKind != JsonValueKind.Object || b.EnumerateObject().Select(p => p.Name).Order().SequenceEqual(new[] { "east", "north", "south", "west" }) == false)
            throw new ArgumentException("范围 JSON 仅接受 west、south、east、north 四个数字字段。");
        return Aw3d30DownloadService.Plan(b.GetProperty("west").GetDouble(), b.GetProperty("south").GetDouble(), b.GetProperty("east").GetDouble(), b.GetProperty("north").GetDouble());
    }

    private static void ValidateArguments(string[] args)
    {
        if (args.Length == 0 || args.Contains("--help") || args.Contains("-h")) return;
        if (args[0].StartsWith('-')) throw new ArgumentException("命令必须位于第一个参数。");
        var flags = new HashSet<string>(["--json", "--include-records"], StringComparer.OrdinalIgnoreCase);
        var values = new HashSet<string>(["--project", "--root", "--input", "--mapping", "--output", "--max-files", "--workarea", "--run-id", "--decisions"], StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < args.Length; i++)
        {
            string option = args[i];
            if (!seen.Add(option)) throw new ArgumentException("重复选项：" + option);
            if (flags.Contains(option)) continue;
            if (!values.Contains(option) || ++i >= args.Length || args[i].StartsWith("--"))
                throw new ArgumentException("未知或缺值选项：" + option);
        }
        if (seen.Contains("--project") && seen.Contains("--root")) throw new ArgumentException("--project 与 --root 请只使用一个。");
        string cmd = args[0].ToLowerInvariant();
        foreach (string option in seen)
        {
            bool allowed = option.ToLowerInvariant() switch
            {
                "--mapping" => cmd is "preview-external-package" or "register-external-package" or "inspect-ground-magnetic-data" or "trial-ground-diurnal" or "export-ground-diurnal" or "inspect-intermediate-table" or "terrain-point-coverage",
                "--output" => cmd is "register-external-package" or "export-ground-diurnal" or "analyze-segmentation-candidates",
                "--decisions" => cmd == "analyze-segmentation-candidates",
                "--max-files" => cmd is "scan-input-folder" or "classify-source-files",
                "--include-records" => cmd is "inspect-ground-magnetic-data" or "trial-ground-diurnal" or "analyze-segmentation-candidates",
                "--run-id" => cmd == "production-run",
                _ => true
            };
            if (!allowed) throw new ArgumentException("此命令不接受选项：" + option);
        }
        string? max = GetOption(args, "--max-files");
        if (max is not null && (!int.TryParse(max, out int n) || n <= 0)) throw new ArgumentException("--max-files 必须为正整数。");
    }

    private static int Run(string[] args, CancellationToken cancellation)
    {
        bool json = args.Contains("--json", StringComparer.OrdinalIgnoreCase);
        string? command = args.FirstOrDefault(x => !x.StartsWith("-", StringComparison.Ordinal))?.ToLowerInvariant();
        string? root = GetOption(args, "--project") ?? GetOption(args, "--root");
        string? input = GetOption(args, "--input") ?? root;
        int maxFiles = GetIntOption(args, "--max-files", 5000);
        string? workareaId = GetOption(args, "--workarea");
        string? runId = GetOption(args, "--run-id");

        if (string.IsNullOrWhiteSpace(command) || command.Equals("help", StringComparison.OrdinalIgnoreCase) ||
            args.Contains("--help", StringComparer.OrdinalIgnoreCase) || args.Contains("-h", StringComparer.OrdinalIgnoreCase))
            return PrintUsage(json, 0);
        if (string.IsNullOrWhiteSpace(root) && command is not "analyze-segmentation-candidates" and not "scan-input-folder" and not "classify-source-files" and not "inspect-magnetic-data" and not "inspect-ground-magnetic-data" and not "trial-ground-diurnal" and not "export-ground-diurnal" and not "inspect-intermediate-table" and not "terrain-point-coverage" and not "version" and not "plan-dem" and not "ensure-dem" and not "plan-aw3d30" and not "download-aw3d30" and not "ensure-aw3d30" and not "inspect-processing-directory" and not "validate-project-layout" and not "preview-execution" and not "preview-coverage" and not "preview-production-selection" and not "preview-t0")
            return PrintUsage(json);

        var queries = new WorkspaceQueries(new JsonStore());
        var analysis = new ReadOnlyAnalysis();
        var preview = new ProductionPreviewService();
        var operational = new OperationalQueries(new JsonStore());
        var projectRequest = new ApplicationRequest(
            root ?? "", WorkareaId: workareaId);
        ApplicationResult<object?> result = command.ToLowerInvariant() switch
        {
            "analyze-segmentation-candidates" => RunSegmentationCandidates(args, input ?? ""),
            "version" => ApplicationResult<object?>.Success("op-" + Guid.NewGuid().ToString("N"), null,
                new { applicationVersion = MagOps.Core.AppConstants.AppVersion, groundReader = GsmGroundInputReader.ParserVersion, groundTrial = GroundDiurnalWorkflow.TrialAlgorithmVersion }),
            "preview-project-change" => ServiceResult(() => ProjectChangePreview.Inspect(root!, JsonSerializer.Deserialize<ProjectChangeRequest>(File.ReadAllText(input!), new JsonSerializerOptions(JsonSerializerDefaults.Web) { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow }) ?? throw new ArgumentException("预览配置为空。"), cancellation)),
            "preview-external-package" => ServiceResult(() => ExternalPackageService.Preview(root!, input!, ExternalPackageService.ReadDeclaration(GetOption(args, "--mapping") ?? throw new ArgumentException("需要 --mapping 声明。")), cancellation)),
            "register-external-package" => ServiceResult(() => ExternalPackageService.Register(root!, input!, GetOption(args, "--output") ?? throw new ArgumentException("需要项目内新输出目录 --output。"), ExternalPackageService.ReadDeclaration(GetOption(args, "--mapping") ?? throw new ArgumentException("需要 --mapping 声明。")), cancellation)),
            "inspect-external-package" => ServiceResult(() => ExternalPackageService.Inspect(root!, input!)),
            "plan-dem" => ServiceResult(() => CopernicusCommand(input, false, cancellation)),
            "ensure-dem" => ServiceResult(() => CopernicusCommand(input, true, cancellation)),
            "plan-aw3d30" => ServiceResult(() => ReadTerrainPlan(input!)),
            "ensure-aw3d30" => ServiceResult(() =>
            {
                var tiles = ReadTerrainPlan(input!);
                var request = new TerrainCoverageRequest(tiles.Min(t => t.Longitude), tiles.Min(t => t.Latitude), tiles.Max(t => t.Longitude) + 1, tiles.Max(t => t.Latitude) + 1);
                return new Aw3d30TerrainProvider().EnsureAvailableAsync(request, token: cancellation).GetAwaiter().GetResult();
            }),
            "download-aw3d30" => ServiceResult(() => new Aw3d30DownloadService().DownloadAsync(ReadTerrainPlan(input!), Aw3d30Credentials.Load() ?? throw new ArgumentException("请先在桌面 AW3D30 下载窗口设置本机账号。"), token: cancellation).GetAwaiter().GetResult()),
            "inspect-business-facts" => Box(new BusinessFactsInspection().Inspect(root!)),
            "get-project-context" => Box(queries.GetContext(projectRequest)),
            "list-workareas" => Box(queries.ListWorkareas(projectRequest)),
            "list-artifacts" => Box(queries.ListArtifacts(projectRequest)),
            "production-overview" => Box(
                operational.GetProductionOverview(projectRequest)),
            "production-run" => Box(
                operational.GetProductionRun(projectRequest, runId ?? "")),
            "scan-input-folder" => Box(analysis.ScanInputFolder(input!, maxFiles: maxFiles)),
            "classify-source-files" => Box(analysis.ClassifySourceFiles(input!, maxFiles)),
            "inspect-magnetic-data" => Box(analysis.InspectMagneticData(input!)),
            "inspect-ground-magnetic-data" => Box(new GroundMagInspection().Inspect(input!, GetOption(args, "--mapping"),
                args.Contains("--include-records", StringComparer.OrdinalIgnoreCase), cancellation)),
            "terrain-point-coverage" => Box(new TerrainPointCoverage().Inspect(input ?? "", GetOption(args, "--mapping"), cancellation)),
            "inspect-intermediate-table" => Box(new IntermediateTableInspection().Inspect(input ?? "", GetOption(args, "--mapping"), cancellation)),
            "export-ground-diurnal" => RunGroundExport(input ?? "", GetOption(args, "--mapping"), GetOption(args, "--output") ?? ""),
            "trial-ground-diurnal" => Box(new GroundDiurnalWorkflow().Trial(input!, GetOption(args, "--mapping"),
                args.Contains("--include-records", StringComparer.OrdinalIgnoreCase), cancellation)),
            "inspect-processing-directory" => Box(analysis.InspectProcessingDirectory(input!)),
            "validate-project-layout" => Box(analysis.ValidateProjectLayout()),
            "preview-execution" => Box(preview.Preview(command.ToLowerInvariant(), input!)),
            "preview-coverage" => Box(preview.Preview(command.ToLowerInvariant(), input!)),
            "preview-production-selection" => Box(preview.Preview(command.ToLowerInvariant(), input!)),
            "preview-t0" => Box(preview.Preview(command.ToLowerInvariant(), input!)),
            _ => ApplicationResult<object?>.Failure(
                $"op-{Guid.NewGuid():N}",
                new ApplicationError("VALIDATION_ERROR", $"未知命令: {command}"))
        };

        if (json)
            Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
        else
            PrintHuman(command, result);
        return result.Ok ? 0 : 2;
    }

    private static ApplicationResult<object?> RunGroundExport(string input, string? mapping, string output)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        try { return Box(new GroundOutputWorkflow().Export(input, mapping, output, cancellation: cancellation.Token)); }
        finally { Console.CancelKeyPress -= cancel; }
    }

    private static ApplicationResult<object?> Box<T>(ApplicationResult<T> result) =>
        new(result.Ok, result.OperationId, result.Context, result.Result, result.Warnings, result.Errors,
            result.RequiresConfirmation, result.Audit);

    private static string? GetOption(string[] args, string name)
    {
        int index = Array.FindIndex(args, x => x.Equals(name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static int PrintUsage(bool json, int exitCode = 2)
    {
        string usage = "地面检查/试算：inspect-ground-magnetic-data 或 trial-ground-diurnal --input <原始目录> [--mapping <JSON>] [--include-records]；导出：export-ground-diurnal --input <目录> --mapping <JSON> --output <新目录>。用法: MagOps.Cli <command> [--project <root>] [--input <path>] [--json]；" +
            "地形：plan-dem / ensure-dem --input <范围JSON>（Copernicus 匿名下载，resolution 默认 30，可选 90）；plan-aw3d30 / download-aw3d30 / ensure-aw3d30 --input <范围JSON>（账号读取本机加密存储）；外部登记：preview-external-package / register-external-package --project <项目> --input <文件族目录> --mapping <声明JSON> [--output <项目内新目录>]；inspect-external-package --project <项目> --input <external-package.json>；preview-project-change --project <项目> --input <预览JSON>。" +
            "analyze-segmentation-candidates --input <配置JSON> [--decisions <补丁JSON>] [--output <新目录>] [--include-records]。";
        var result = exitCode == 0 ? ApplicationResult<object?>.Success($"op-{Guid.NewGuid():N}", null,
            new { usage, commands = new[] { "version", "inspect-business-facts", "preview-project-change", "preview-external-package", "register-external-package", "inspect-external-package", "plan-dem", "ensure-dem", "plan-aw3d30", "download-aw3d30", "ensure-aw3d30", "get-project-context", "list-workareas", "list-artifacts", "production-overview", "production-run",
                "scan-input-folder", "classify-source-files", "inspect-magnetic-data", "inspect-ground-magnetic-data", "trial-ground-diurnal", "export-ground-diurnal", "inspect-intermediate-table", "terrain-point-coverage",
                "inspect-processing-directory", "validate-project-layout", "preview-execution", "preview-coverage",
                "preview-production-selection", "preview-t0", "analyze-segmentation-candidates" } }) :
            ApplicationResult<object?>.Failure($"op-{Guid.NewGuid():N}", new ApplicationError("VALIDATION_ERROR", usage));
        if (json) Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
        else if (exitCode == 0) Console.WriteLine(usage);
        else Console.Error.WriteLine(usage);
        return exitCode;
    }

    private static ApplicationResult<object?> RunSegmentationCandidates(string[] args, string input)
    {
        var valueOptions = new HashSet<string>(["--input", "--decisions", "--output"], StringComparer.OrdinalIgnoreCase);
        var flagOptions = new HashSet<string>(["--json", "--include-records"], StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < args.Length; i++)
        {
            string option = args[i];
            if (!seen.Add(option) || (!valueOptions.Contains(option) && !flagOptions.Contains(option)) ||
                (valueOptions.Contains(option) && (++i >= args.Length || args[i].StartsWith("--", StringComparison.Ordinal))))
                return ApplicationResult<object?>.Failure("op-" + Guid.NewGuid().ToString("N"),
                    new("VALIDATION_ERROR", "未知、重复或缺值选项：" + option));
        }
        if (string.IsNullOrWhiteSpace(input))
            return ApplicationResult<object?>.Failure("op-" + Guid.NewGuid().ToString("N"), new("VALIDATION_ERROR", "需要--input配置JSON。"));
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            var result = new SegmentationCandidateWorkflow().Analyze(input, GetOption(args, "--decisions"),
                GetOption(args, "--output"), cancellation.Token);
            if (!result.Ok || args.Contains("--include-records", StringComparer.OrdinalIgnoreCase)) return Box(result);
            var r = result.Result!;
            return ApplicationResult<object?>.Success(result.OperationId, result.Context, new
            {
                r.Status, r.ProductionEligible, r.ReadOnly, r.InputPath, r.InputKind, r.SourceFingerprint, r.RecognitionFingerprint,
                r.RequestFingerprint, r.TimeBasis, r.InputRecords, r.InvalidRecords, legs = r.Legs.Count,
                passes = r.Audit.Execution.Passes.Count, selectedRecords = r.Audit.SelectedSamples.Count,
                addedRecords = r.AddedSelection.Count, removedRecords = r.RemovedSelection.Count,
                blockedReasons = r.Audit.BlockedReasons, outputDirectory = GetOption(args, "--output")
            }, result.Warnings);
        }
        catch (OperationCanceledException)
        { return ApplicationResult<object?>.Failure("op-" + Guid.NewGuid().ToString("N"), new("CANCELLED", "已取消；旧项目和既有成果未改动。")); }
        finally { Console.CancelKeyPress -= cancel; }
    }

    private static void PrintHuman(string command, ApplicationResult<object?> result)
    {
        if (!result.Ok)
        {
            foreach (ApplicationError error in result.Errors)
                Console.Error.WriteLine($"[{error.Code}] {error.Message}");
            return;
        }
        Console.WriteLine($"{command}: OK ({result.OperationId})");
        if ((command.Equals("analyze-segmentation-candidates", StringComparison.OrdinalIgnoreCase) || command is "version" or "preview-project-change" or "preview-external-package" or "register-external-package" or "inspect-external-package" or "plan-dem" or "ensure-dem" or "plan-aw3d30" or "download-aw3d30" or "ensure-aw3d30"))
        { Console.WriteLine(JsonSerializer.Serialize(result.Result, JsonOptions)); return; }
        if (result.Result is WorkspaceContextResult context)
            Console.WriteLine($"项目: {context.Workspace.DisplayName}; 工区: {context.Workareas.Count}");
        else if (result.Result is IReadOnlyList<MagOps.Core.Domain.WorkareaRecord> workareas)
            Console.WriteLine($"工区: {workareas.Count}");
        else if (result.Result is IReadOnlyList<MagOps.Core.Domain.ArtifactRecord> artifacts)
            Console.WriteLine($"成果记录: {artifacts.Count}");
        else if (result.Result is FileScanResult scan)
            Console.WriteLine($"文件: {scan.FileCount}; 大小: {scan.TotalBytes} bytes");
        else if (result.Result is SourceClassificationResult classification)
            Console.WriteLine($"来源分类: {classification.FileCount} 个文件; {string.Join(", ", classification.Counts.Select(x => $"{x.Key}={x.Value}"))}");
        else if (result.Result is MagneticAnalysisResult magnetic)
        {
            Console.WriteLine($"磁测文件: {magnetic.FileCount}; UAV: {magnetic.UavRawFileCount}; Base: {magnetic.BaseRawFileCount}; 读到记录: {magnetic.HasReadableRecords}; 需要复核: {magnetic.RequiresReview}");
            foreach (string warning in magnetic.Warnings) Console.WriteLine($"提示: {warning}");
        }
        else if (result.Result is LayoutValidationResult layout)
            Console.WriteLine($"目录配置: {layout.ProjectFolderCount} 项目目录, {layout.WorkareaFolderCount} 工区目录, {layout.OperationCount} 操作");
        else if (result.Result is GroundMagInspectionResult ground)
        {
            Console.WriteLine($"地面读取: {ground.ParsedRecordCount}; Base: {ground.BaseRecordCount}; 移动: {ground.RoverRecordCount}; session: {ground.Sessions.Count}");
            Console.WriteLine($"时间已确认/假设/未决: {ground.ResolvedTimeRecordCount}/{ground.AssumedTimeRecordCount}/{ground.UnresolvedTimeRecordCount}; 需复核: {ground.RequiresReview}; 仅只读检查");
            foreach (var group in ground.Diagnostics.GroupBy(d => d.Code)) Console.WriteLine($"提示: {group.Key} ({group.Sum(d => d.Occurrences)})");
        }
        else if (result.Result is GroundDiurnalTrialResult trial)
        {
            Console.WriteLine($"地面日变试算: {trial.Status}; 已改正/未改正/未选择: {trial.CorrectedRecordCount}/{trial.UncorrectedRecordCount}/{trial.ExcludedRoverRecordCount}; 非正式生产成果");
            foreach (string reason in trial.BlockedReasons) Console.WriteLine($"提示: {reason}");
        }
        else if (result.Result is BusinessFactsResult facts)
            Console.WriteLine($"设备 {facts.EquipmentCount}；飞行 {facts.FlightCount}；日志 {facts.DailyLogCount}；待核对 {facts.Issues.Count}，原记录未修改。");
        else if (result.Result is TerrainPointCoverageResult coverage)
            Console.WriteLine($"原始点 {coverage.LocatedPoints}；候选瓦片 {coverage.Candidates.Count}；无覆盖点 {coverage.UncoveredPoints}。{coverage.CoverageMeaning}");
        else if (result.Result is IntermediateTableResult table)
            Console.WriteLine($"{table.Mapping.ProcessingStage}: {table.Rows.Count} 条，待复核 {table.InvalidCount}；原值保留，未再次改正。");
        else if (result.Result is GroundOutputResult output)
            Console.WriteLine($"{output.Status}: {output.OutputDirectory}; {output.RecordCount} 条记录 / {output.PointGroupCount} 个分组；需复核。");
        else if (result.Result is ProcessingInspectionResult processing)
            Console.WriteLine($"处理 Manifest: {processing.ManifestCount}; 警告: {processing.Warnings.Count}");
        else if (result.Result is ProductionPreviewResult preview)
            Console.WriteLine($"{preview.PreviewKind}: {preview.Status}; 阻塞: {preview.BlockedReasons.Count}");
        else if (result.Result is ProductionOverview overview)
            Console.WriteLine(
                $"生产运行: {overview.RunCount}; 完成: {overview.CompletedRunCount}; " +
                $"失败: {overview.FailedRunCount}; 进行中: {overview.StartedRunCount}; " +
                $"成果: {overview.ArtifactCount}");
        else if (result.Result is ProductionRunSummary run)
            Console.WriteLine(
                $"{run.ProcessingRunId}: {run.RunKind}/{run.Status}; " +
                $"工区: {run.WorkareaId}; 成果: {run.Artifacts.Count}");
    }

    private static int GetIntOption(string[] args, string name, int fallback)
    {
        string? value = GetOption(args, name);
        return int.TryParse(value, out int parsed) && parsed > 0 ? parsed : fallback;
    }
}
