using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.AsyncEx;

namespace Foundatio.Utility;

internal static class TaskExtensions
{
    [DebuggerStepThrough]
    public static ConfiguredTaskAwaitable<TResult> AnyContext<TResult>(this Task<TResult> task)
    {
        return task.ConfigureAwait(continueOnCapturedContext: false);
    }

    [DebuggerStepThrough]
    public static ConfiguredCancelableAsyncEnumerable<TResult> AnyContext<TResult>(this IAsyncEnumerable<TResult> source)
    {
        return source.ConfigureAwait(continueOnCapturedContext: false);
    }

    [DebuggerStepThrough]
    public static ConfiguredAsyncDisposable AnyContext(this IAsyncDisposable source)
    {
        return source.ConfigureAwait(continueOnCapturedContext: false);
    }

    [DebuggerStepThrough]
    public static ConfiguredTaskAwaitable AnyContext(this Task task)
    {
        return task.ConfigureAwait(continueOnCapturedContext: false);
    }

    [DebuggerStepThrough]
    public static ConfiguredValueTaskAwaitable AnyContext(this ValueTask task)
    {
        return task.ConfigureAwait(continueOnCapturedContext: false);
    }

    [DebuggerStepThrough]
    public static ConfiguredTaskAwaitable<TResult> AnyContext<TResult>(this AwaitableDisposable<TResult> task) where TResult : IDisposable
    {
        return task.ConfigureAwait(continueOnCapturedContext: false);
    }

    public static async Task SafeDelay(this TimeProvider timeProvider, TimeSpan delay, CancellationToken cancellationToken = default)
    {
        try
        {
            await timeProvider.Delay(delay, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
