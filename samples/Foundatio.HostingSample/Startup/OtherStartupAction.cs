using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions.Hosting.Startup;
using Microsoft.Extensions.Logging;

namespace Foundatio.HostingSample;

public class OtherStartupAction : IStartupAction
{
    private readonly ILogger _logger;

    public OtherStartupAction(ILogger<OtherStartupAction> logger)
    {
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        for (int i = 0; i < 5; i++)
        {
            _logger.LogTrace("OtherStartupAction Run Thread={ManagedThreadId}", Thread.CurrentThread.ManagedThreadId);
            await Task.Delay(900);
        }
    }
}
