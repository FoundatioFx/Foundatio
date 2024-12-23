using System;
using System.Threading;
using System.Threading.Tasks;

namespace Foundatio.Utility;

/// <summary>
/// A class that will call an <see cref="Func{TResult}"/> when Disposed.
/// </summary>
public sealed class AsyncDisposableAction : IAsyncDisposable
{
    private Func<Task> _exitTask;

    /// <summary>
    /// Initializes a new instance of the <see cref="DisposableAction"/> class.
    /// </summary>
    /// <param name="exitTask">The exit action.</param>
    public AsyncDisposableAction(Func<Task> exitTask)
    {
        _exitTask = exitTask;
    }

    /// <summary>
    /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
    /// </summary>
    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        var exitAction = Interlocked.Exchange(ref _exitTask, null);
        if (exitAction is not null)
            await exitAction().AnyContext();
    }
}
