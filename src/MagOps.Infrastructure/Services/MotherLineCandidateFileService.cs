using System.Security.Cryptography;
using System.Text.Json;

namespace MagOps.Infrastructure.Services;

public sealed record MotherLineCandidateRequest(string ProjectId, string WorkareaId,
    PartitionMotherLineIdentityCatalog Catalog, IReadOnlyList<MotherLineProductionBatch> Batches);

/// <summary>Read-only candidate exchange. File hashes identify bytes, not independent proof of parsing.</summary>
public static class MotherLineCandidateFileService
{
    public static MotherLineCandidateRequest Read(string path, string projectId, string workareaId,
        PartitionMotherLineIdentityCatalog catalog, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var request = JsonSerializer.Deserialize<MotherLineCandidateRequest>(File.ReadAllText(path))
            ?? throw new InvalidDataException("候选快照为空。");
        if (request.ProjectId != projectId || request.WorkareaId != workareaId || request.Catalog is null ||
            JsonSerializer.Serialize(request.Catalog) != JsonSerializer.Serialize(catalog))
            throw new InvalidDataException("候选的项目、工区或母线设计目录与当前工作区不一致。");
        ValidateShape(request);
        _ = MotherLineProductionAssemblyService.Build(projectId, workareaId, catalog, request.Batches);
        cancellationToken.ThrowIfCancellationRequested();
        VerifySources(request.Batches, cancellationToken);
        return request;
    }

    public static void VerifySources(IReadOnlyList<MotherLineProductionBatch> batches, CancellationToken cancellationToken = default)
    {
        foreach (var group in batches.SelectMany(b => b.Observations).Where(r => r.Assignment.IncludeInProcessing)
            .GroupBy(r => r.Sample.SourceFile, StringComparer.OrdinalIgnoreCase))
        {
            if (!Path.IsPathFullyQualified(group.Key)) throw new InvalidDataException("候选来源须为绝对路径。");
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = File.OpenRead(group.Key);
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[1024 * 1024];
            int count;
            while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                digest.AppendData(buffer, 0, count);
            }
            string hash = Convert.ToHexString(digest.GetHashAndReset());
            if (group.Any(r => !hash.Equals(r.SourceSha256, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("候选来源文件已变化，需重新解析和导出。");
        }
    }

    public static void ValidateShape(MotherLineCandidateRequest request)
    {
        if (request.Catalog is null || request.Batches is null || request.Batches.Count == 0 ||
            request.Batches.Any(b => b is null || b.Observations is null ||
                b.Observations.Any(r => r is null || r.Sample is null || r.SourceSha256 is null ||
                    r.Sample.QualityFlags is null || !r.Sample.Longitude.HasValue || !r.Sample.Latitude.HasValue ||
                    !WorkareaSpatialService.ValidCoordinate(r.Sample.Longitude.Value, r.Sample.Latitude.Value))))
            throw new InvalidDataException("候选缺少有效母线目录、批次或观测记录。");
    }
}
