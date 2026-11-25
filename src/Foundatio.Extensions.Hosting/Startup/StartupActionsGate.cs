using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Utility;

namespace Foundatio.Extensions.Hosting.Startup;

/// <summary>
/// Holds back Foundatio message handlers and jobs until startup actions have completed, however long they take, so a
/// slow migration delays processing instead of disabling it. Processing does not start if a startup action fails.
/// </summary>
internal sealed class StartupActionsGate : IStartupGate
{
    private readonly StartupActionsContext _context;

    public StartupActionsGate(StartupActionsContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
    }

    public async Task<bool> WaitAsync(CancellationToken cancellationToken)
    {
        return (await _context.Completion.WaitAsync(cancellationToken).AnyContext()).Success;
    }
}
