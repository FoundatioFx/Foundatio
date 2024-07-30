using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Foundatio.AsyncEx;
using Foundatio.Utility;

namespace Foundatio.Tests.Extensions;

public static class TaskExtensions
{
    [DebuggerStepThrough]
    public static async Task WaitAsync(this AsyncManualResetEvent resetEvent, TimeSpan timeout)
    {
        using var timeoutCancellationTokenSource = timeout.ToCancellationTokenSource();
        await resetEvent.WaitAsync(timeoutCancellationTokenSource.Token).AnyContext();
    }

    [DebuggerStepThrough]
    public static async Task WaitAsync(this AsyncAutoResetEvent resetEvent, TimeSpan timeout)
    {
        using var timeoutCancellationTokenSource = timeout.ToCancellationTokenSource();
        await resetEvent.WaitAsync(timeoutCancellationTokenSource.Token).AnyContext();
    }

    public static Task WaitAsync(this AsyncCountdownEvent countdownEvent, TimeSpan timeout)
    {
        return Task.WhenAny(countdownEvent.WaitAsync(), Task.Delay(timeout));
    }
}
