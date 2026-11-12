using MagOps.Application;
using MagOps.Core.Domain;
using MagOps.Infrastructure.Services;

namespace MagOps.SmokeTests;

internal static partial class Program
{
    private static void CheckProductionPorts(FieldProductionRequest request, ArtifactService artifacts,
        ProcessingRunService runs)
    {
        var output = new FieldProductionOutputAdapter(new DiurnalCorrectionExportService());
        // Failure before output begins must leave existing files untouched.
        string protectedDirectory = Path.Combine(request.ProjectRoot, "port-output-failure.review-draft");
        Directory.CreateDirectory(protectedDirectory);
        string sentinel = Path.Combine(protectedDirectory, "previous.txt");
        File.WriteAllText(sentinel, "previous output");
        var outputFailure = new ThrowingProductionOutput();
        var failingOutputWorkflow = new FieldProductionWorkflow(new AuditProductionService(), outputFailure,
            new FieldProductionArtifactsAdapter(artifacts), new FieldProductionRunsAdapter(runs));
        int previousRuns = runs.List(request.ProjectRoot).Count;
        Throws<IOException>(() => failingOutputWorkflow.Execute(request with
            { OutputDirectory = protectedDirectory[..^13] }));
        Equal("previous output", File.ReadAllText(sentinel));
        Equal("FAILED", runs.List(request.ProjectRoot).Skip(previousRuns).Single().Status);

        // A second persistence failure must not hide the original production error.
        var lostFailureWorkflow = new FieldProductionWorkflow(new AuditProductionService(), outputFailure,
            new FieldProductionArtifactsAdapter(artifacts), new UnwritableFailureRuns(new FieldProductionRunsAdapter(runs)));
        previousRuns = runs.List(request.ProjectRoot).Count;
        try
        {
            lostFailureWorkflow.Execute(request with { OutputDirectory = Path.Combine(request.ProjectRoot, "port-failure-record") });
            throw new InvalidOperationException("Expected the combined production/persistence failure");
        }
        catch (AggregateException error)
        {
            Equal(2, error.InnerExceptions.Count);
            Equal("injected output failure", error.InnerExceptions[0].Message);
            Equal("injected failure record error", error.InnerExceptions[1].Message);
        }
        Equal("STARTED", runs.List(request.ProjectRoot).Skip(previousRuns).Single().Status);

        // A committed file bundle and the registry are not one transaction.
        // Existing behavior retains committed files/partial registration for diagnosis.
        var partial = new FailingProductionArtifacts(new FieldProductionArtifactsAdapter(artifacts));
        var partialWorkflow = new FieldProductionWorkflow(new AuditProductionService(), output, partial,
            new FieldProductionRunsAdapter(runs));
        string partialPath = Path.Combine(request.ProjectRoot, "port-partial-registration");
        previousRuns = runs.List(request.ProjectRoot).Count;
        Throws<IOException>(() => partialWorkflow.Execute(request with { OutputDirectory = partialPath }));
        Equal(2, partial.Attempts);
        Equal("FAILED", runs.List(request.ProjectRoot).Skip(previousRuns).Single().Status);
        True(Directory.GetFiles(partialPath + ".review-draft").Length > 0,
            "registration failure must retain committed files for diagnosis");
        True(artifacts.List(request.ProjectRoot).Any(a => a.Path.Contains("port-partial-registration")),
            "partial registration must remain observable, not reported as a completed run");
        string partialId = artifacts.List(request.ProjectRoot)
            .Single(a => a.Path.Contains("port-partial-registration")).ArtifactId;
        var retryWorkflow = new FieldProductionWorkflow(new AuditProductionService(), output,
            new FieldProductionArtifactsAdapter(artifacts), new FieldProductionRunsAdapter(runs));
        var recovered = retryWorkflow.Execute(request with { OutputDirectory = partialPath });
        Equal("COMPLETED", recovered.ProcessingRun.Status);
        True(recovered.RegisteredArtifacts.Any(a => a.ArtifactId == partialId),
            "retry must reuse the artifact already registered before failure");
        True(recovered.Export.Files.All(File.Exists), "recovery must return existing committed files");

        var completeFailure = new FailingProductionRuns(new FieldProductionRunsAdapter(runs));
        var completionWorkflow = new FieldProductionWorkflow(new AuditProductionService(), output,
            new FieldProductionArtifactsAdapter(artifacts), completeFailure);
        previousRuns = runs.List(request.ProjectRoot).Count;
        Throws<IOException>(() => completionWorkflow.Execute(request with
            { OutputDirectory = Path.Combine(request.ProjectRoot, "port-completion-failure") }));
        Equal("FAILED", runs.List(request.ProjectRoot).Skip(previousRuns).Single().Status);
    }

    private sealed class ThrowingProductionOutput : IFieldProductionOutput
    {
        public DiurnalCorrectionExportResult Write(FieldProductionRequest request, AuditProductionResult audit) =>
            throw new IOException("injected output failure");
    }

    private sealed class FailingProductionArtifacts(IFieldProductionArtifacts inner) : IFieldProductionArtifacts
    {
        public int Attempts { get; private set; }
        public void ValidateGeneratedArtifactContext(string root, GeneratedArtifactContext context) =>
            inner.ValidateGeneratedArtifactContext(root, context);
        public ArtifactRecord RegisterGeneratedFile(string root, string path, string kind) =>
            ++Attempts == 2 ? throw new IOException("injected second registration failure") :
                inner.RegisterGeneratedFile(root, path, kind);
    }

    private sealed class FailingProductionRuns(IFieldProductionRuns inner) : IFieldProductionRuns
    {
        public ProcessingRunRecord Begin(string root, string kind, GeneratedArtifactContext context) => inner.Begin(root, kind, context);
        public ProcessingRunRecord Complete(string root, string id, IEnumerable<string> artifactIds) =>
            throw new IOException("injected completion failure");
        public ProcessingRunRecord Fail(string root, string id, Exception error) => inner.Fail(root, id, error);
    }

    private sealed class UnwritableFailureRuns(IFieldProductionRuns inner) : IFieldProductionRuns
    {
        public ProcessingRunRecord Begin(string root, string kind, GeneratedArtifactContext context) => inner.Begin(root, kind, context);
        public ProcessingRunRecord Complete(string root, string id, IEnumerable<string> artifactIds) => inner.Complete(root, id, artifactIds);
        public ProcessingRunRecord Fail(string root, string id, Exception error) => throw new IOException("injected failure record error");
    }
}
