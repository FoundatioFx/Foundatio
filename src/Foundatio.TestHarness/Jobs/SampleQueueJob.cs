#pragma warning disable 612, 618

using System;
using System.Threading;
using System.Threading.Tasks;
using Exceptionless;
using Foundatio.Jobs;
using Foundatio.Lock;
using Foundatio.Queues;
using Microsoft.Extensions.Logging;

namespace Foundatio.Tests.Jobs;

public class SampleQueueWithRandomErrorsAndAbandonsJob : QueueJobBase<SampleQueueWorkItem>
{
    public SampleQueueWithRandomErrorsAndAbandonsJob(IQueue<SampleQueueWorkItem> queue, TimeProvider timeProvider, ILoggerFactory loggerFactory = null) : base(queue, timeProvider, loggerFactory)
    {
    }

    protected override Task<JobResult> ProcessQueueEntryAsync(QueueEntryContext<SampleQueueWorkItem> context)
    {
        if (RandomData.GetBool(10))
        {
            throw new Exception("Boom!");
        }

        if (RandomData.GetBool(10))
        {
            return Task.FromResult(JobResult.FailedWithMessage("Abandoned"));
        }

        return Task.FromResult(JobResult.Success);
    }
}

public class SampleQueueJob : QueueJobBase<SampleQueueWorkItem>
{
    public SampleQueueJob(IQueue<SampleQueueWorkItem> queue, TimeProvider timeProvider, ILoggerFactory loggerFactory = null) : base(queue, timeProvider, loggerFactory)
    {
    }

    protected override Task<JobResult> ProcessQueueEntryAsync(QueueEntryContext<SampleQueueWorkItem> context)
    {
        return Task.FromResult(JobResult.Success);
    }
}

public class SampleQueueJobWithLocking : QueueJobBase<SampleQueueWorkItem>
{
    private readonly ILockProvider _lockProvider;

    public SampleQueueJobWithLocking(IQueue<SampleQueueWorkItem> queue, ILockProvider lockProvider, TimeProvider timeProvider, ILoggerFactory loggerFactory = null) : base(queue, timeProvider, loggerFactory)
    {
        _lockProvider = lockProvider;
    }

    protected override Task<ILock> GetQueueEntryLockAsync(IQueueEntry<SampleQueueWorkItem> queueEntry, CancellationToken cancellationToken = default(CancellationToken))
    {
        if (_lockProvider != null)
            return _lockProvider.AcquireAsync("job", TimeSpan.FromMilliseconds(100), TimeSpan.Zero);

        return base.GetQueueEntryLockAsync(queueEntry, cancellationToken);
    }

    protected override Task<JobResult> ProcessQueueEntryAsync(QueueEntryContext<SampleQueueWorkItem> context)
    {
        return Task.FromResult(JobResult.Success);
    }
}

public class SampleQueueWorkItem
{
    public string Path { get; set; }
    public DateTime Created { get; set; }
}

public class SampleJob : JobBase
{
    public SampleJob(TimeProvider timeProvider, ILoggerFactory loggerFactory) : base(timeProvider, loggerFactory)
    {
    }

    protected override Task<JobResult> RunInternalAsync(JobContext context)
    {
        if (RandomData.GetBool(10))
        {
            throw new Exception("Boom!");
        }

        if (RandomData.GetBool(10))
        {
            return Task.FromResult(JobResult.FailedWithMessage("Failed"));
        }

        return Task.FromResult(JobResult.Success);
    }
}

#pragma warning restore 612, 618
