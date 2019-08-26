using System;
using Microsoft.Extensions.Logging;

namespace Foundatio.Jobs {
    public class JobResult {
        public bool IsCancelled { get; set; }
        public Exception Error { get; set; }
        public string Message { get; set; }
        public bool IsSuccess { get; set; }

        public static readonly JobResult None = new JobResult {
            IsSuccess = true,
            Message = String.Empty
        };

        public static readonly JobResult Cancelled = new JobResult {
            IsCancelled = true
        };

        public static readonly JobResult Success = new JobResult {
            IsSuccess = true
        };

        public static JobResult FromException(Exception exception, string message = null) {
            return new JobResult {
                Error = exception,
                IsSuccess = false,
                Message = message ?? exception.Message
            };
        }

        public static JobResult CancelledWithMessage(string message) {
            return new JobResult {
                IsCancelled = true,
                Message = message
            };
        }

        public static JobResult SuccessWithMessage(string message) {
            return new JobResult {
                IsSuccess = true,
                Message = message
            };
        }

        public static JobResult FailedWithMessage(string message) {
            return new JobResult {
                IsSuccess = false,
                Message = message
            };
        }
    }

    public static class JobResultExtensions {
        public static void LogJobResult(this ILogger logger, JobResult result, string jobName) {
            if (result == null) {
                if (logger.IsEnabled(LogLevel.Error))
                    logger.LogError("Null job run result for {JobName}.", jobName);

                return;
            }

            if (result.IsCancelled)
                logger.LogWarning(result.Error, "Job run {JobName} cancelled: {Message}", jobName, result.Message);
            else if (!result.IsSuccess)
                logger.LogError(result.Error, "Job run {JobName} failed: {Message}", jobName, result.Message);
            else if (!String.IsNullOrEmpty(result.Message))
                logger.LogInformation("Job run {JobName} succeeded: {Message}", jobName, result.Message);
            else if (logger.IsEnabled(LogLevel.Debug))
                logger.LogDebug("Job run {JobName} succeeded.", jobName);
        }
    }
}