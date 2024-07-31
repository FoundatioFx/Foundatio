using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Jobs;

public abstract class JobBase : IJob, IHaveLogger, IHaveTimeProvider
{
    private readonly TimeProvider _timeProvider;
    protected readonly ILogger _logger;

    public JobBase(ILoggerFactory loggerFactory = null) : this(null, loggerFactory)
    {
    }

    public JobBase(TimeProvider timeProvider, ILoggerFactory loggerFactory = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = loggerFactory?.CreateLogger(GetType()) ?? NullLogger.Instance;
    }

    public string JobId { get; } = Guid.NewGuid().ToString("N").Substring(0, 10);
    ILogger IHaveLogger.Logger => _logger;
    TimeProvider IHaveTimeProvider.TimeProvider => _timeProvider;

    public virtual Task<JobResult> RunAsync(CancellationToken cancellationToken = default)
    {
        return RunInternalAsync(new JobContext(cancellationToken));
    }

    protected abstract Task<JobResult> RunInternalAsync(JobContext context);
}
