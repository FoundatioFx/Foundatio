using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Foundatio.Utility.Resilience;

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

    [Obsolete("Use ResiliencePolicy instead.")]
    public static Task WithRetriesAsync(Func<Task> action, int maxAttempts = 5, TimeSpan? retryInterval = null, TimeProvider timeProvider = null, CancellationToken cancellationToken = default, ILogger logger = null)
    {
        return WithRetriesAsync<object>(async () =>
        {
            await action().AnyContext();
            return null;
        }, maxAttempts, retryInterval, timeProvider, cancellationToken, logger);
    }

    [Obsolete("Use ResiliencePolicy instead.")]
    public static async Task<T> WithRetriesAsync<T>(Func<Task<T>> action, int maxAttempts = 5, TimeSpan? retryInterval = null, TimeProvider timeProvider = null, CancellationToken cancellationToken = default, ILogger logger = null)
    {
        var resiliencePolicy = new ResiliencePolicy(logger ?? NullLogger.Instance, timeProvider ?? TimeProvider.System) { MaxAttempts = maxAttempts, Delay = retryInterval };
        return await resiliencePolicy.ExecuteAsync(async _ => await action(), cancellationToken).AnyContext();
    }
}
