using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Foundatio.Utility;

public static class Run
{
    public static Task DelayedAsync(TimeSpan delay, Func<Task> action, TimeProvider timeProvider = null, CancellationToken cancellationToken = default)
    {
        timeProvider ??= TimeProvider.System;

        if (cancellationToken.IsCancellationRequested)
            return Task.CompletedTask;

        return Task.Run(async () =>
        {
            if (delay.Ticks > 0)
                await timeProvider.Delay(delay, cancellationToken).AnyContext();

            if (cancellationToken.IsCancellationRequested)
                return;

            await action().AnyContext();
        }, cancellationToken);
    }

    [Obsolete("Use Parallel.ForEachAsync")]
    public static Task InParallelAsync(int iterations, Func<int, Task> work)
    {
        return Task.WhenAll(Enumerable.Range(1, iterations).Select(i => Task.Run(() => work(i))));
    }

    public static Task WithRetriesAsync(Func<Task> action, int maxAttempts = 5, TimeSpan? retryInterval = null, TimeProvider timeProvider = null, CancellationToken cancellationToken = default, ILogger logger = null)
    {
        return WithRetriesAsync<object>(async () =>
        {
            await action().AnyContext();
            return null;
        }, maxAttempts, retryInterval, timeProvider, cancellationToken, logger);
    }

    public static async Task<T> WithRetriesAsync<T>(Func<Task<T>> action, int maxAttempts = 5, TimeSpan? retryInterval = null, TimeProvider timeProvider = null, CancellationToken cancellationToken = default, ILogger logger = null)
    {
        if (action == null)
            throw new ArgumentNullException(nameof(action));

        timeProvider ??= TimeProvider.System;
        int attempts = 1;
        var startTime = timeProvider.GetUtcNow();
        int currentBackoffTime = _defaultBackoffIntervals[0];
        if (retryInterval != null)
            currentBackoffTime = (int)retryInterval.Value.TotalMilliseconds;

        do
        {
            if (attempts > 1 && logger != null && logger.IsEnabled(LogLevel.Information))
                logger.LogInformation("Retrying {Attempts} attempt after {Delay:g}...", attempts.ToOrdinal(), timeProvider.GetUtcNow().Subtract(startTime));

            try
            {
                return await action().AnyContext();
            }
            catch (Exception ex)
            {
                if (attempts >= maxAttempts)
                    throw;

                if (logger != null && logger.IsEnabled(LogLevel.Error))
                    logger.LogError(ex, "Retry error: {Message}", ex.Message);

                await timeProvider.SafeDelay(TimeSpan.FromMilliseconds(currentBackoffTime), cancellationToken).AnyContext();
            }

            if (retryInterval == null)
                currentBackoffTime = _defaultBackoffIntervals[Math.Min(attempts, _defaultBackoffIntervals.Length - 1)];
            attempts++;
        } while (attempts <= maxAttempts && !cancellationToken.IsCancellationRequested);

        throw new TaskCanceledException("Should not get here");
    }

    private static readonly int[] _defaultBackoffIntervals = [ 100, 1000, 2000, 2000, 5000, 5000, 10000, 30000, 60000 ];
}
