using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MagOps.Core.Domain;
using MagOps.Infrastructure.Services;

namespace MagOps.Application;

public sealed record SegmentationCandidateInput(string InputPath, string InputKind)
{
    public string? ReviewPath { get; init; }
    public string? ReferencePath { get; init; }
    public double? AxisDegrees { get; init; }
    public double? CentralMeridian { get; init; }
    public IReadOnlyList<PlannedSurveyLineTask> Tasks { get; init; } = [];
    public string RouteFingerprint { get; init; } = "";
    public string? ExpectedRecognitionFingerprint { get; init; }
}
public sealed record SegmentationCandidatePatch(string ExpectedSourceFingerprint, string ExpectedRecognitionFingerprint,
    string ExpectedRequestFingerprint)
{
    public IReadOnlyList<string> IncludeLegIds { get; init; } = [];
    public IReadOnlyList<string> ExcludeLegIds { get; init; } = [];
}
public sealed record SegmentationCandidateLeg(FlightLeg Leg, IReadOnlyList<FlightLegSample> Samples);
public sealed record SegmentationCandidateSource(string Path, string Sha256);
public sealed record SegmentationCandidateResult(string Status, string InputPath, string InputKind,
    string SourceFingerprint, string RecognitionFingerprint, string RequestFingerprint, string TimeBasis,
    int InputRecords, int InvalidRecords, AuditProductionRequest Review,
    AuditProductionResult Audit, IReadOnlyList<SegmentationCandidateLeg> Legs,
    IReadOnlyList<SegmentationCandidateSource> Sources, IReadOnlyList<ExecutionSampleReference> AddedSelection,
    IReadOnlyList<ExecutionSampleReference> RemovedSelection)
{
    public bool ProductionEligible => false;
    public bool ReadOnly => true;
    public SegmentationCandidatePatch? AppliedPatch { get; init; }
}

/// <summary>Real computation through the desktop audit authority. Never exports production CSV,
/// approves quality or changes an existing project. Review JSON uses the desktop's existing contract.</summary>
public sealed class SegmentationCandidateWorkflow
{
    public const string Version = "segmentation-candidate-workflow-1";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public ApplicationResult<SegmentationCandidateResult> Analyze(string inputFile, string? patchFile = null,
        string? outputDirectory = null, CancellationToken token = default)
    {
        string operation = "op-" + Guid.NewGuid().ToString("N");
        try
        {
            var input = JsonSerializer.Deserialize<SegmentationCandidateInput>(File.ReadAllText(inputFile), Json)
                ?? throw new InvalidDataException("候选输入配置为空。");
            if (string.IsNullOrWhiteSpace(input.InputPath)) throw new InvalidDataException("需要inputPath输入路径。");
            string configDirectory = Path.GetDirectoryName(Path.GetFullPath(inputFile))!;
            string Resolve(string value) => Path.GetFullPath(value, configDirectory);
            input = input with { InputPath = Resolve(input.InputPath), ReviewPath = input.ReviewPath is null ? null : Resolve(input.ReviewPath),
                ReferencePath = input.ReferencePath is null ? null : Resolve(input.ReferencePath) };
            var patch = patchFile is null ? null : JsonSerializer.Deserialize<SegmentationCandidatePatch>(File.ReadAllText(patchFile), Json)
                ?? throw new InvalidDataException("决定补丁为空。");
            var result = Compute(input, patch, token);
            if (outputDirectory is not null) WriteNew(outputDirectory, input, result, token);
            return ApplicationResult<SegmentationCandidateResult>.Success(operation, null, result,
                [new("CANDIDATE_ONLY", "仅为候选与决定差异，不是生产成果或质量验收；核验输入可在桌面加载。")]);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return ApplicationResult<SegmentationCandidateResult>.Failure(operation,
                new(ex is UnauthorizedAccessException or IOException
                    ? ApplicationErrorCodes.EnvironmentError : ApplicationErrorCodes.ValidationError, ex.Message));
        }
    }

    public SegmentationCandidateResult Compute(SegmentationCandidateInput input, SegmentationCandidatePatch? patch = null,
        CancellationToken token = default)
    {
        if (input.Tasks is null || input.Tasks.Any(t => t is null) || string.IsNullOrWhiteSpace(input.InputPath))
            throw new InvalidDataException("需要输入路径和无空元素的任务集合。");
        token.ThrowIfCancellationRequested();
        string path = Path.GetFullPath(input.InputPath);
        FieldMagFolderAnalysis analysis;
        IReadOnlyList<MissionTimeWindow> windows;
        int invalid = 0;
        var sources = new List<SegmentationCandidateSource>();
        bool spatial = input.InputKind == "intermediate-csv";
        if (spatial)
        {
            var csv = IntermediateCsvSegmentationService.Read(path);
            analysis = csv.Analysis; windows = csv.Windows; invalid = csv.InvalidRecords;
            sources.Add(new(path, csv.Sha256));
        }
        else if (input.InputKind == "raw-folder")
        {
            analysis = new FieldMagDataService().AnalyzeFolder(path);
            windows = new FlightWindowInferenceService().Infer(analysis.UavSamples).Candidates.Select(c => c.Window).ToArray();
            sources.AddRange(analysis.Files.Select(f => new SegmentationCandidateSource(f.Path, f.SourceContentSha256)));
            if (sources.Any(s => s.Sha256.Length != 64))
                throw new InvalidDataException("目录含读取失败或缺少内容哈希的文件；请将可读取的原始副本放入独立输入目录后重试，未跳过未知来源。");
        }
        else throw new InvalidDataException("inputKind须为intermediate-csv或raw-folder，不自动猜测数据阶段。");
        if (analysis.UavSamples.Count == 0 || windows.Count == 0) throw new InvalidDataException("没有可计算的轨迹点或时间窗口。");
        token.ThrowIfCancellationRequested();
        var request = input.ReviewPath is not null ? SegmentationReviewStore.LoadFile(input.ReviewPath) :
            new AuditProductionRequest(analysis.Fingerprint, windows, [], [], true)
            {
                RouteFingerprint = input.RouteFingerprint, RequirePassConfirmation = true,
                ProjectCentralMeridian = input.CentralMeridian,
                AllowActualLineRegrouping = spatial, RenumberActualLines = spatial,
                MotherLineRestoration = spatial ? new MotherLineRestorationState { Enabled = false } : null,
                TrackSegmentation = new() { Enabled = true, UseDesignTasks = input.Tasks.Count > 0, AxisDegrees = input.AxisDegrees }
            };
        if (request.SourceFingerprint != analysis.Fingerprint || request.RouteFingerprint != input.RouteFingerprint)
            throw new InvalidDataException("CONTEXT_MISMATCH：核验记录与当前输入内容或设计版本不同；旧决定未套用。");
        if (input.ReviewPath is not null && (input.ReferencePath is not null || input.AxisDegrees is not null || input.CentralMeridian is not null))
            throw new InvalidDataException("加载核验输入时沿用其参数；请勿同时提供轴方向、参考文件或中央经线覆盖。");
        if (request.MissionWindows.Any(w => w is null) || request.QualityConfirmations.Any(q => q is null) ||
            request.ManualLegAssignments.Any(a => a is null) || request.RecognitionVersion is null)
            throw new InvalidDataException("核验输入存在空元素或缺少识别版本。");
        if (input.ReferencePath is not null)
        {
            string referenceHash = Hash(input.ReferencePath);
            var imported = TrackSegmentationReferenceService.Import(Path.GetFullPath(input.ReferencePath), input.CentralMeridian);
            if (imported.ReferenceFileHash != referenceHash || Hash(input.ReferencePath) != referenceHash)
                throw new InvalidDataException("设计参考在读取期间改变，请重新生成候选。");
            sources.Add(new(Path.GetFullPath(input.ReferencePath), referenceHash));
            request = request with { TrackSegmentation = imported with { Enabled = true, UseDesignTasks = input.Tasks.Count > 0,
                AxisDegrees = input.AxisDegrees } };
        }
        if (request.TrackSegmentation is not { } track) throw new InvalidDataException("需要显式轨迹识别参数。");
        SegmentationParameterStore.Validate(track);
        if (spatial && IntermediateSegmentationEvidence.Problem(track) is { } problem) throw new InvalidDataException(problem);
        string recognition = AuditProductionService.RecognitionInputFingerprint(analysis, input.Tasks, request, input.RouteFingerprint);
        string currentVersion = AuditProductionService.RecognitionVersion(request);
        if (input.ReviewPath is not null && request.RecognitionVersion != currentVersion)
            throw new InvalidDataException("识别版本已变化；本命令不静默迁移旧决定，请在桌面复核或新建候选。");
        if (input.ReviewPath is not null &&
            ((input.Tasks.Count > 0 && string.IsNullOrWhiteSpace(input.ExpectedRecognitionFingerprint)) ||
             (input.ExpectedRecognitionFingerprint is not null && input.ExpectedRecognitionFingerprint != recognition)))
            throw new InvalidDataException("CONTEXT_MISMATCH：核验输入的任务集或识别参数与期望指纹不同；旧决定未套用。");
        request = request with { RecognitionVersion = currentVersion,
            TrackSegmentationFingerprint = TrackSegmentationReferenceService.Fingerprint(track) };
        var service = new AuditProductionService();
        var baseline = service.Analyze(analysis, input.Tasks, request, input.RouteFingerprint);
        var audit = baseline;
        if (patch is not null)
        {
            if (patch.IncludeLegIds is null || patch.ExcludeLegIds is null ||
                patch.ExpectedSourceFingerprint != analysis.Fingerprint || patch.ExpectedRecognitionFingerprint != recognition ||
                patch.ExpectedRequestFingerprint != ProductionSnapshotService.RequestFingerprint(request))
                throw new InvalidDataException("CONTEXT_MISMATCH：决定补丁缺集合或输入/识别指纹不符。");
            var known = baseline.Execution.Legs.ToDictionary(l => l.FlightLegId, StringComparer.OrdinalIgnoreCase);
            if (patch.IncludeLegIds.Concat(patch.ExcludeLegIds).Any(id => !known.ContainsKey(id)) ||
                patch.IncludeLegIds.Intersect(patch.ExcludeLegIds, StringComparer.OrdinalIgnoreCase).Any() ||
                patch.IncludeLegIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() != patch.IncludeLegIds.Count ||
                patch.ExcludeLegIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() != patch.ExcludeLegIds.Count)
                throw new InvalidDataException("决定补丁含未知、重复或相互冲突的航段。");
            var included = request.ManualIncludedFlightLegIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var confirmed = request.ConfirmedPassIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (string id in patch.IncludeLegIds)
            {
                included.Add(id);
                if (!string.IsNullOrWhiteSpace(known[id].SurveyTaskStableId)) confirmed.Add(id + "/pass");
            }
            foreach (string id in patch.ExcludeLegIds) { included.Remove(id); confirmed.Remove(id + "/pass"); }
            // A selection-only patch does not own QC signatures, identity reasons or interval choices.
            request = request with { ManualIncludedFlightLegIds = included.Order(StringComparer.Ordinal).ToArray(),
                ConfirmedPassIds = confirmed.Order(StringComparer.Ordinal).ToArray(), DefaultMatchedSelectionsApplied = true };
            audit = service.Analyze(analysis, input.Tasks, request, input.RouteFingerprint, reuseRecognition: true);
        }
        token.ThrowIfCancellationRequested();
        foreach (var source in sources)
            if (source.Sha256.Length != 64 || !Hash(source.Path).Equals(source.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("输入来源在分析期间改变或缺少内容哈希，候选未发布。");
        var before = baseline.SelectedSamples.ToDictionary(s => (s.SourceFile, s.SourceRecord));
        var after = audit.SelectedSamples.ToDictionary(s => (s.SourceFile, s.SourceRecord));
        return new("CANDIDATE_ONLY", path, input.InputKind, analysis.Fingerprint, recognition,
            ProductionSnapshotService.RequestFingerprint(request), analysis.ProjectTimeZoneId,
            analysis.UavSamples.Count, invalid, request, audit,
            audit.Execution.Legs.Select(l => new SegmentationCandidateLeg(l, l.Samples)).ToArray(), sources,
            after.Where(p => !before.ContainsKey(p.Key)).Select(p => p.Value).ToArray(),
            before.Where(p => !after.ContainsKey(p.Key)).Select(p => p.Value).ToArray()) { AppliedPatch = patch };
    }

    private static void WriteNew(string output, SegmentationCandidateInput input, SegmentationCandidateResult result, CancellationToken token)
    {
        string target = Path.GetFullPath(output);
        if (Directory.Exists(target) || File.Exists(target)) throw new IOException("候选输出目录已存在；请选择新路径。");
        string parent = Path.GetDirectoryName(target)!;
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("候选输出父目录不存在。");
        string stage = Path.Combine(parent, ".candidate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            SegmentationReviewStore.Save(Path.Combine(stage, SegmentationReviewStore.SavedInputName), result.Review);
            File.WriteAllText(Path.Combine(stage, "candidate.json"), JsonSerializer.Serialize(result, Json));
            if (result.AppliedPatch is not null)
                File.WriteAllText(Path.Combine(stage, "decision-patch.json"), JsonSerializer.Serialize(result.AppliedPatch, Json));
            var replay = input with { ReviewPath = Path.Combine(target, SegmentationReviewStore.SavedInputName),
                ReferencePath = null, AxisDegrees = null, CentralMeridian = null,
                ExpectedRecognitionFingerprint = result.RecognitionFingerprint };
            File.WriteAllText(Path.Combine(stage, "replay.json"), JsonSerializer.Serialize(replay, Json));
            File.WriteAllText(Path.Combine(stage, "manifest.json"), JsonSerializer.Serialize(new
            { schema = Version, status = "CANDIDATE_ONLY", production_eligible = false, source_fingerprint = result.SourceFingerprint,
                recognition_fingerprint = result.RecognitionFingerprint, sources = result.Sources,
                files = Directory.GetFiles(stage).Select(p => new { name = Path.GetFileName(p), sha256 = Hash(p) }).ToArray() }, Json));
            token.ThrowIfCancellationRequested();
            foreach (var source in result.Sources)
                if (Hash(source.Path) != source.Sha256) throw new InvalidDataException("来源在保存期间变化，候选未发布。");
            Directory.Move(stage, target);
        }
        catch { if (Directory.Exists(stage)) Directory.Delete(stage, true); throw; }
    }

    private static string Hash(string path)
    { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
}
