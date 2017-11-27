using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Foundatio.Utility {
    public static class Run {
        public static async Task DelayedAsync(TimeSpan delay, Func<Task> action) {
            await Task.Run(async () => {
                if (delay.Ticks > 0)
                    await SystemClock.SleepAsync(delay).AnyContext();

                await action().AnyContext();
            }).AnyContext();
        }

        public static Task InParallelAsync(int iterations, Func<int, Task> work) {
            return Task.WhenAll(Enumerable.Range(1, iterations).Select(i => Task.Run(() => work(i))));
        }

        public static Task WithRetriesAsync(Func<Task> action, int maxAttempts = 5, TimeSpan? retryInterval = null, CancellationToken cancellationToken = default(CancellationToken), ILogger logger = null) {
            return WithRetriesAsync(async () => {
                await action().AnyContext();
                return Task.CompletedTask;
            }, maxAttempts, retryInterval, cancellationToken, logger);
        }

        public static async Task<T> WithRetriesAsync<T>(Func<Task<T>> action, int maxAttempts = 5, TimeSpan? retryInterval = null, CancellationToken cancellationToken = default(CancellationToken), ILogger logger = null) {
            if (action == null)
                throw new ArgumentNullException(nameof(action));

            int attempts = 1;
            var startTime = SystemClock.UtcNow;
            int currentBackoffTime = _defaultBackoffIntervals[0];
            if (retryInterval != null)
                currentBackoffTime = (int)retryInterval.Value.TotalMilliseconds;

            do {
                if (attempts > 1 && logger != null && logger.IsEnabled(LogLevel.Information))
                    logger.LogInformation("Retrying {Attempts} attempt after {Delay:g}...", attempts.ToOrdinal(), SystemClock.UtcNow.Subtract(startTime));

                try {
                    return await action().AnyContext();
                } catch (Exception ex) {
                    if (attempts >= maxAttempts)
                        throw;

                    if (logger != null && logger.IsEnabled(LogLevel.Error))
                        logger.LogError(ex, "Retry error: {Message}", ex.Message);

                    await SystemClock.SleepAsync(currentBackoffTime, cancellationToken).AnyContext();
                }

                if (retryInterval == null)
                    currentBackoffTime = _defaultBackoffIntervals[Math.Min(attempts, _defaultBackoffIntervals.Length - 1)];
                attempts++;
            } while (attempts <= maxAttempts && !cancellationToken.IsCancellationRequested);

            throw new TaskCanceledException("Should not get here.");
        }

        private static readonly int[] _defaultBackoffIntervals = new int[] { 100, 1000, 2000, 2000, 5000, 5000, 10000, 30000, 60000 };
    }
}