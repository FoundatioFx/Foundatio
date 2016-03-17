using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Logging;

namespace Foundatio.Utility {
    internal static class Run {
        public static async Task DelayedAsync(TimeSpan delay, Func<Task> action) {
            await Task.Run(async () => {
                await Task.Delay(delay).AnyContext();
                await action().AnyContext();
            }).AnyContext();
        }

        public static Task InParallel(int iterations, Func<int, Task> work) {
            return Task.WhenAll(Enumerable.Range(1, iterations).Select(i => Task.Run(() => work(i))));
        }

        public static async Task WithRetriesAsync(Func<Task> action, int maxAttempts = 5, TimeSpan? retryInterval = null, CancellationToken cancellationToken = default(CancellationToken), ILogger logger = null) {
            await Run.WithRetriesAsync(async () => {
                await action().AnyContext();
                return TaskHelper.Completed;
            }, maxAttempts, retryInterval, cancellationToken, logger).AnyContext();
        }

        public static async Task<T> WithRetriesAsync<T>(Func<Task<T>> action, int maxAttempts = 5, TimeSpan? retryInterval = null, CancellationToken cancellationToken = default(CancellationToken), ILogger logger = null) {
            if (action == null)
                throw new ArgumentNullException(nameof(action));

            int attempts = 1;
            var startTime = DateTime.UtcNow;
            do {
                if (attempts > 1)
                    logger?.Info($"Retrying {attempts.ToOrdinal()} attempt after {DateTime.UtcNow.Subtract(startTime).TotalMilliseconds}ms...");

                try {
                    return await action().AnyContext();
                } catch (Exception ex) {
                    if (attempts >= maxAttempts)
                        throw;

                    logger?.Error(ex, $"Retry error: {ex.Message}");
                    await Task.Delay(retryInterval ?? TimeSpan.FromMilliseconds(attempts * 100), cancellationToken).AnyContext();
                }

                attempts++;
            } while (attempts <= maxAttempts && !cancellationToken.IsCancellationRequested);

            throw new TaskCanceledException("Should not get here.");
        }
    }
}