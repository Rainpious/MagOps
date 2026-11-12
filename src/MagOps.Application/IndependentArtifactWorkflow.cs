using System.Text.Json;
using MagOps.Core.Domain;
using MagOps.Infrastructure.Services;
using MagOps.Infrastructure.Storage;

namespace MagOps.Application;

public sealed record IndependentArtifactRequest(string Root, string Destination, string Stage,
    GeneratedArtifactContext Context, IReadOnlyList<ArtifactDependency> Dependencies, object ExportOptions);

/// <summary>One project-lock owner for staging, evidence, recoverable file pair and registry commit.</summary>
public sealed class IndependentArtifactWorkflow(JsonStore store, ArtifactService artifacts)
{
    public ArtifactRecord Execute(IndependentArtifactRequest request, Action<string> write,
        Action<string>? checkpoint = null)
    {
        using var projectLock = WorkspaceFileLock.AcquireForRoot(request.Root);
        IndependentArtifactTransaction.Recover(request.Root, store);
        string destination = IndependentArtifactTransaction.ManagedPath(request.Root, request.Destination);
        var workspace = store.Read(ProjectPaths.ExistingProject(request.Root), new WorkspaceRecord());
        if (workspace.ProjectId != request.Context.ProjectId)
            throw new InvalidOperationException("导出项目身份不匹配。");
        if (request.Context.ContextStatus == "WORKAREA")
            artifacts.ValidateGeneratedArtifactContext(request.Root, request.Context);
        RequireCurrent(request);
        string id = "export_" + Guid.NewGuid().ToString("N");
        string directory = Path.Combine(Path.GetDirectoryName(destination)!, ".magops-" + id);
        Directory.CreateDirectory(directory);
        string staged = Path.Combine(directory, Path.GetFileName(destination));
        bool journaled = false;
        try
        {
            write(staged);
            checkpoint?.Invoke("written");
            RequireCurrent(request);
            string? stableId = artifacts.ResolveStableArtifactIdForGeneratedPath(request.Root, destination);
            GeneratedArtifactIdentity.Write(staged, request.Stage.StartsWith("field_takeoff", StringComparison.Ordinal) ? "route" : "report", request.Stage,
                ArtifactDependencies.Digest(request.Dependencies),
                request.Dependencies.Where(x => x.Kind == "file").Select(x =>
                    ArtifactDependencies.Resolve(request.Root, x.Path)).ToArray(),
                stableId, context: request.Context,
                dependencies: request.Dependencies, exportOptions: JsonSerializer.SerializeToElement(request.ExportOptions),
                exportTransactionId: id);
            ArtifactRecord? previous = artifacts.List(request.Root).SingleOrDefault(x =>
                x.Path.Equals(Path.GetRelativePath(request.Root, destination).Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));
            // Quality evidence is tied to a content hash; preserve it without approving the new bytes.
            if (previous is not null)
            {
                var metadata = IndependentArtifactTransaction.ReadMetadata(staged);
                var prior = File.Exists(destination) ? GeneratedArtifactIdentity.Inspect(destination).Metadata : null;
                var history = prior?.PriorQualityEvidence?.ToList() ?? [];
                if (previous.Sha256 is { Length: 64 } && previous.QualityStatus != "UNREVIEWED")
                    history.Add(new(previous.Sha256, previous.QualityStatus));
                metadata = metadata with
                {
                    QualityStatus = previous.Sha256 == metadata.ContentSha256 ? previous.QualityStatus : "UNREVIEWED",
                    PriorQualityEvidence = history.Distinct().ToArray()
                };
                IndependentArtifactTransaction.WriteMetadata(staged, metadata);
            }
            // The pending marker is created only after staged bytes and their evidence are complete.
            var journal = IndependentArtifactTransaction.Prepare(request.Root, destination, staged, id, previous, store);
            journaled = true;
            checkpoint?.Invoke("prepared");
            IndependentArtifactTransaction.Install(request.Root, journal);
            checkpoint?.Invoke("installed");
            RequireCurrent(request);
            ArtifactRecord row = artifacts.RegisterGeneratedFile(request.Root, destination, request.Stage);
            checkpoint?.Invoke("registered");
            IndependentArtifactTransaction.Commit(request.Root, journal, store);
            artifacts.RefreshFreshnessStatus(request.Root);
            return artifacts.List(request.Root).Single(x => x.ArtifactId == row.ArtifactId);
        }
        catch (Exception original)
        {
            if (journaled)
            {
                try { IndependentArtifactTransaction.Recover(request.Root, store); }
                catch (Exception recovery)
                { throw new AggregateException("导出失败且恢复未完成；已保留备份，请先恢复项目。", original, recovery); }
            }
            throw;
        }
        finally
        {
            // A pending journal owns its backups; never remove them on an incomplete rollback.
            if (!IndependentArtifactTransaction.IsPending(request.Root, id) && Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    private static void RequireCurrent(IndependentArtifactRequest request)
    {
        var status = ArtifactDependencies.Check(request.Root, request.Dependencies);
        if (status.Status != "CURRENT") throw new InvalidOperationException("来源未就绪，请刷新后重新导出：" + status.Reason);
    }
}
