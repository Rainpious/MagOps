using System.Text.Json;
using MagOps.Infrastructure.Storage;

namespace MagOps.Application;

public sealed record PreviewEvidence(string Code, string Message, int Count = 0);
public sealed record ProductionPreviewResult(
    string PreviewKind,
    string Status,
    bool ReadOnly,
    bool RequiresConfirmation,
    IReadOnlyList<PreviewEvidence> Evidence,
    IReadOnlyList<string> BlockedReasons,
    IReadOnlyList<string> CandidateInputs);

public sealed class ProductionPreviewService
{
    public ApplicationResult<ProductionPreviewResult> Preview(string kind, string inputPath)
    {
        string operationId = $"op-{Guid.NewGuid():N}";
        try
        {
            string root = Path.GetFullPath(inputPath);
            if (!Directory.Exists(root))
                return ApplicationResult<ProductionPreviewResult>.Failure(operationId,
                    new(ApplicationErrorCodes.ValidationError, $"预览输入目录不存在: {root}"));

            var evidence = new List<PreviewEvidence>();
            var blocked = new List<string>();
            var candidates = new List<string>();
            string[] files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToArray();
            int manifests = files.Count(x => Path.GetFileName(x).Contains("manifest", StringComparison.OrdinalIgnoreCase));
            int routeFiles = files.Count(x => Path.GetExtension(x).Equals(".kml", StringComparison.OrdinalIgnoreCase) ||
                                               Path.GetExtension(x).Equals(".kmz", StringComparison.OrdinalIgnoreCase) ||
                                               Path.GetExtension(x).Equals(".ply", StringComparison.OrdinalIgnoreCase));
            int magneticFiles = files.Count(x => Path.GetExtension(x).Equals(".txt", StringComparison.OrdinalIgnoreCase) ||
                                                  Path.GetExtension(x).Equals(".csv", StringComparison.OrdinalIgnoreCase) ||
                                                  Path.GetExtension(x).Equals(".gz", StringComparison.OrdinalIgnoreCase));
            evidence.Add(new("INPUT_FILES", $"发现 {files.Length} 个输入文件。", files.Length));
            evidence.Add(new("MANIFESTS", $"发现 {manifests} 个 Manifest 文件。", manifests));
            evidence.Add(new("ROUTE_EVIDENCE", $"发现 {routeFiles} 个 KML/KMZ/PLY 航线或空间证据文件。", routeFiles));
            evidence.Add(new("MAGNETIC_EVIDENCE", $"发现 {magneticFiles} 个文本或压缩磁测相关文件。", magneticFiles));

            if (!ProjectPaths.HasProject(root))
                blocked.Add("缺少 MagOps project.json（兼容旧 workspace.json），无法确认项目/工区上下文。");
            if (routeFiles == 0)
                blocked.Add("没有发现可用于执行匹配的航线空间证据。");
            if (magneticFiles == 0)
                blocked.Add("没有发现可用于生产预览的磁测输入。");

            switch (kind)
            {
                case "preview-execution":
                    blocked.Add("Mission 时间窗和 PhysicalFlight 边界尚未作为明确输入提供。");
                    candidates.AddRange(files.Where(x => Path.GetExtension(x).Equals(".kml", StringComparison.OrdinalIgnoreCase) ||
                                                          Path.GetExtension(x).Equals(".kmz", StringComparison.OrdinalIgnoreCase)).Take(20));
                    break;
                case "preview-coverage":
                    blocked.Add("缺少已确认的 SurveyExecutionResult，不能计算正式覆盖区间。");
                    break;
                case "preview-production-selection":
                    blocked.Add("缺少已确认的 SurveyLinePass、QC 状态和人工覆盖规则。");
                    break;
                case "preview-t0":
                    blocked.Add("缺少已确认的最终 Production Selection，不能确定 T0 输入集合。");
                    break;
                default:
                    return ApplicationResult<ProductionPreviewResult>.Failure(operationId,
                        new(ApplicationErrorCodes.ValidationError, $"未知预览类型: {kind}"));
            }

            string status = blocked.Count == 0 ? "CANDIDATE_ONLY" : "BLOCKED_REVIEW";
            var result = new ProductionPreviewResult(kind, status, true, true, evidence, blocked, candidates);
            var warnings = blocked.Select(x => new ApplicationWarning("REVIEW_REQUIRED", x)).ToArray();
            return ApplicationResult<ProductionPreviewResult>.Success(operationId, null, result, warnings, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ApplicationResult<ProductionPreviewResult>.Failure(operationId,
                new(ApplicationErrorCodes.EnvironmentError, ex.Message));
        }
    }
}
