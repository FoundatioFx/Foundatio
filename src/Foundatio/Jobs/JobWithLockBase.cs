﻿using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Lock;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Jobs;

public abstract class JobWithLockBase : IJob, IHaveLogger, IHaveTimeProvider
{
    protected readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;

    public JobWithLockBase(ILoggerFactory loggerFactory = null)
    {
        _logger = loggerFactory?.CreateLogger(GetType()) ?? NullLogger.Instance;
    }

    public JobWithLockBase(TimeProvider timeProvider, ILoggerFactory loggerFactory = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = loggerFactory?.CreateLogger(GetType()) ?? NullLogger.Instance;
    }

    public string JobId { get; } = Guid.NewGuid().ToString("N").Substring(0, 10);
    ILogger IHaveLogger.Logger => _logger;
    TimeProvider IHaveTimeProvider.TimeProvider => _timeProvider;

    public virtual async Task<JobResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var lockValue = await GetLockAsync(cancellationToken).AnyContext();
        if (lockValue is null)
        {
            return JobResult.CancelledWithMessage("Unable to acquire job lock");
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
