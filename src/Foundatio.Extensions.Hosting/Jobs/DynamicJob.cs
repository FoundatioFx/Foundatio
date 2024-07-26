using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Jobs;
using Foundatio.Utility;

namespace Foundatio.Extensions.Hosting.Jobs;

internal class DynamicJob : IJob
{
    private readonly IServiceProvider _serviceProvider;
    private readonly Func<IServiceProvider, CancellationToken, Task> _action;

    public DynamicJob(IServiceProvider serviceProvider, Func<IServiceProvider, CancellationToken, Task> action)
    {
        _serviceProvider = serviceProvider;
        _action = action;
    }

    public async Task<JobResult> RunAsync(CancellationToken cancellationToken = default)
    {
        await _action(_serviceProvider, cancellationToken).AnyContext();

        return JobResult.Success;
    }
}
