using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Utility;

namespace Foundatio.Extensions.Hosting.Startup;

/// <summary>Holds back Foundatio message handlers and jobs until startup actions have completed.</summary>
public sealed class StartupActionsGate : IStartupGate
{
    private readonly StartupActionsContext _context;
    private readonly TimeSpan? _timeout;

    public StartupActionsGate(StartupActionsContext context, TimeSpan? timeout = null)
    {
        _context = context;
        _timeout = timeout;
    }

    public async Task<bool> WaitAsync(CancellationToken cancellationToken)
    {
        var result = await _context.WaitForStartupAsync(cancellationToken, _timeout).AnyContext();
        return result.Success;
    }
}
