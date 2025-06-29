﻿using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Lock;
using Foundatio.Utility;
using Foundatio.Utility.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Jobs;

public abstract class JobWithLockBase : IJobWithOptions, IHaveLogger, IHaveLoggerFactory, IHaveTimeProvider, IHaveResiliencePolicyProvider
{
    protected readonly ILogger _logger;
    protected readonly ILoggerFactory _loggerFactory;
    private readonly TimeProvider _timeProvider;
    protected readonly IResiliencePolicyProvider _resiliencePolicyProvider;
    private readonly string _jobName;

    public JobWithLockBase(ILoggerFactory loggerFactory = null) : this(null, null, loggerFactory)
    {
    }

    public JobWithLockBase(TimeProvider timeProvider, IResiliencePolicyProvider resiliencePolicyProvider, ILoggerFactory loggerFactory = null)
    {
        _jobName = GetType().Name;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _resiliencePolicyProvider = resiliencePolicyProvider;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger(GetType());
    }

    public string JobId { get; } = Guid.NewGuid().ToString("N").Substring(0, 10);
    ILogger IHaveLogger.Logger => _logger;
    ILoggerFactory IHaveLoggerFactory.LoggerFactory => _loggerFactory;
    TimeProvider IHaveTimeProvider.TimeProvider => _timeProvider;
    IResiliencePolicyProvider IHaveResiliencePolicyProvider.ResiliencePolicyProvider => _resiliencePolicyProvider;

    public JobOptions Options { get; set; }

    public virtual async Task<JobResult> RunAsync(CancellationToken cancellationToken = default)
    {
        ILock lockValue;
        using (var lockActivity = FoundatioDiagnostics.ActivitySource.StartActivity("Job Lock: " + Options?.Name ?? _jobName))
        {
            lockActivity?.AddTag("job.id", JobId);

            lockValue = await GetLockAsync(cancellationToken).AnyContext();
            if (lockValue is null)
            {
                return JobResult.CancelledWithMessage("Unable to acquire job lock");
            }
        }

        try
        {
            return await RunInternalAsync(new JobContext(cancellationToken, lockValue)).AnyContext();
        }
        finally
        {
            await lockValue.ReleaseAsync().AnyContext();
        }
    }

    protected abstract Task<JobResult> RunInternalAsync(JobContext context);

    protected abstract Task<ILock> GetLockAsync(CancellationToken cancellationToken = default);
}
