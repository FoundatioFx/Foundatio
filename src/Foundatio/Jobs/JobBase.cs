using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Utility;
using Foundatio.Utility.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Jobs;

public abstract class JobBase : IJob, IHaveLogger, IHaveLoggerFactory, IHaveTimeProvider, IHaveResiliencePipelineProvider
{
    protected readonly TimeProvider _timeProvider;
    protected readonly ILogger _logger;
    protected readonly ILoggerFactory _loggerFactory;
    protected readonly IResiliencePipelineProvider _resiliencePipelineProvider;

    public JobBase(ILoggerFactory loggerFactory = null) : this(null, null, loggerFactory)
    {
    }

    public JobBase(TimeProvider timeProvider, IResiliencePipelineProvider resiliencePipelineProvider, ILoggerFactory loggerFactory = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _resiliencePipelineProvider = resiliencePipelineProvider;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger(GetType());

    }

    public string JobId { get; } = Guid.NewGuid().ToString("N").Substring(0, 10);
    ILogger IHaveLogger.Logger => _logger;
    ILoggerFactory IHaveLoggerFactory.LoggerFactory => _loggerFactory;
    TimeProvider IHaveTimeProvider.TimeProvider => _timeProvider;
    IResiliencePipelineProvider IHaveResiliencePipelineProvider.ResiliencePipelineProvider => _resiliencePipelineProvider;

    public virtual Task<JobResult> RunAsync(CancellationToken cancellationToken = default)
    {
        return RunInternalAsync(new JobContext(cancellationToken));
    }

    protected abstract Task<JobResult> RunInternalAsync(JobContext context);
}
