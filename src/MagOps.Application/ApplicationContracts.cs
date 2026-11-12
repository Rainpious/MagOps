using System.Text.Json.Serialization;

namespace MagOps.Application;

public static class ApplicationErrorCodes
{
    public const string ValidationError = "VALIDATION_ERROR";
    public const string EnvironmentError = "ENVIRONMENT_ERROR";
    public const string AmbiguousIdentity = "AMBIGUOUS_IDENTITY";
}

public enum OperationMode
{
    ReadOnly,
    DryRun,
    WritePendingConfirmation,
    WriteConfirmed
}

public sealed record ApplicationRequest(
    string ProjectRoot,
    OperationMode Mode = OperationMode.ReadOnly,
    string? WorkareaId = null,
    string? ExpectedRevision = null,
    string? IdempotencyKey = null);

public sealed record ApplicationContext(
    string ProjectRoot,
    string ProjectId,
    string WorkareaId,
    string Revision);

public sealed record ApplicationWarning(string Code, string Message);

public sealed record ApplicationError(string Code, string Message);

public sealed record ApplicationResult<T>(
    bool Ok,
    string OperationId,
    ApplicationContext? Context,
    T? Result,
    IReadOnlyList<ApplicationWarning> Warnings,
    IReadOnlyList<ApplicationError> Errors,
    bool RequiresConfirmation,
    object Audit)
{
    public static ApplicationResult<T> Success(
        string operationId,
        ApplicationContext? context,
        T result,
        IReadOnlyList<ApplicationWarning>? warnings = null,
        bool requiresConfirmation = false) =>
        new(true, operationId, context, result, warnings ?? [], [], requiresConfirmation, new { });

    public static ApplicationResult<T> Failure(
        string operationId,
        ApplicationError error,
        ApplicationContext? context = null) =>
        new(false, operationId, context, default, [], [error], false, new { });
}
