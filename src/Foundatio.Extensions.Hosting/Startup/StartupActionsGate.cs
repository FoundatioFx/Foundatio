using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;

namespace Foundatio.Extensions.Hosting.Startup;

/// <summary>
/// Holds back Foundatio message handlers and jobs until startup actions have completed, however long they take, so a
/// slow migration delays processing instead of disabling it. Processing does not start if a startup action fails.
/// </summary>
public sealed class StartupActionsGate : IStartupGate
{
    private readonly StartupActionsContext _context;
    private readonly TimeSpan? _timeout;

    /// <param name="context">The startup actions to wait for.</param>
    /// <param name="timeout">
    /// When startup actions are still running after this long, an error is logged; the gate keeps waiting and opens
    /// once they complete. Null logs nothing.
    /// </param>
    public StartupActionsGate(StartupActionsContext context, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(context);

        _context = context;
        _timeout = timeout;
    }

    public async Task<bool> WaitAsync(CancellationToken cancellationToken)
    {
        var completion = _context.Completion;
        if (_timeout is { } timeout && !completion.IsCompleted)
        {
            try
            {
                return (await completion.WaitAsync(timeout, cancellationToken).AnyContext()).Success;
            }
            catch (TimeoutException)
            {
                _context.Logger.LogError("Startup actions are still running after {Timeout}; message handlers and jobs will start once they complete", timeout);
            }
        }

        return (await completion.WaitAsync(cancellationToken).AnyContext()).Success;
    }
}
