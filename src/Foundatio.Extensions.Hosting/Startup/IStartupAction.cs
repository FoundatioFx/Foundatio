using System;
using System.Threading;
using System.Threading.Tasks;

namespace Foundatio.Extensions.Hosting.Startup;

public interface IStartupAction
{
    Task RunAsync(CancellationToken shutdownToken = default);
}
