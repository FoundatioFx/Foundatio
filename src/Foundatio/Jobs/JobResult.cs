using System;
using Microsoft.Extensions.Logging;

namespace Foundatio.Jobs;

/// <summary>
/// The outcome of one job run. Immutable — the shared <see cref="Success"/>/<see cref="Cancelled"/> instances are
/// safe to return from any job; use the <c>*WithMessage</c>/<see cref="FromException"/> factories (or a
/// <c>with</c>-expression) to attach details.
/// </summary>
public sealed record JobResult
{
    public bool IsCancelled { get; init; }
    public Exception? Error { get; init; }
    public string Message { get; init; } = String.Empty;
    public bool IsSuccess { get; init; }

    public static readonly JobResult Cancelled = new()
    {
        IsCancelled = true
    };

    public static readonly JobResult Success = new()
    {
        IsSuccess = true
    };

    public static JobResult FromException(Exception exception, string? message = null)
    {
        return new JobResult
        {
            Error = exception,
            IsSuccess = false,
            Message = message ?? exception.Message
        };
    }

    public static JobResult CancelledWithMessage(string message)
    {
        return new JobResult
        {
            IsCancelled = true,
            Message = message
        };
    }

    public static JobResult SuccessWithMessage(string message)
    {
        return new JobResult
        {
            IsSuccess = true,
            Message = message
        };
    }

    public static JobResult FailedWithMessage(string message)
    {
        return new JobResult
        {
            IsSuccess = false,
            Message = message
        };
    }
}

public static class JobResultExtensions
{
    public static void LogJobResult(this ILogger logger, JobResult result, string? jobName)
    {
        if (result is null)
        {
            logger.LogError("Null job run result for {JobName}", jobName);
            return;
        }

        if (result.IsCancelled)
            logger.LogWarning(result.Error, "Job run {JobName} cancelled: {Message}", jobName, result.Message);
        else if (!result.IsSuccess)
            logger.LogError(result.Error, "Job run {JobName} failed: {Message}", jobName, result.Message);
        else if (!String.IsNullOrEmpty(result.Message))
            logger.LogInformation("Job run {JobName} succeeded: {Message}", jobName, result.Message);
        else
            logger.LogDebug("Job run {JobName} succeeded", jobName);
    }
}
