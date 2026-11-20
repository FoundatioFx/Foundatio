using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Lock;

namespace Foundatio.Jobs;

/// <summary>Supplies a work-item handler with its data, lock, cancellation token and progress reporting.</summary>
public class WorkItemContext
{
    private readonly Func<int, string?, Task>? _progressCallback;
    private readonly WorkItemExecution? _execution;

    /// <summary>
    /// Creates a standalone context for a caller that owns the lock and cancellation token. Progress goes
    /// directly to <paramref name="progressCallback"/> and renewal goes directly to <paramref name="workItemLock"/>.
    /// </summary>
    public WorkItemContext(object data, string jobId, ILock? workItemLock, CancellationToken cancellationToken, Func<int, string?, Task> progressCallback)
        : this(data, jobId, workItemLock, cancellationToken)
    {
        _progressCallback = progressCallback;
    }

    internal WorkItemContext(object data, string jobId, ILock workItemLock, CancellationToken cancellationToken, WorkItemExecution execution)
        : this(data, jobId, workItemLock, execution.GetHandlerToken(cancellationToken))
    {
        _execution = execution;
    }

    public object Data { get; private set; }
    public string JobId { get; private set; }
    public ILock? WorkItemLock { get; private set; }

    /// <summary>
    /// The handler's result. <see cref="WorkItemJob"/> still fails this attempt if the entry was abandoned
    /// or the work-item lock could not be renewed.
    /// </summary>
    public JobResult Result { get; set; } = JobResult.Success;

    public CancellationToken CancellationToken { get; private set; }

    /// <summary>
    /// Reports progress even when automatic start and finish reports are disabled. During job processing,
    /// <see cref="IWorkItemHandler.AutoRenewLockOnProgress"/> renews both the queue entry and <see cref="WorkItemLock"/>
    /// before sending. An active handler also observes a known loss of ownership when delivery returns.
    /// A context constructed with a callback forwards the report directly to that callback.
    /// </summary>
    public Task ReportProgressAsync(int progress, string? message = null)
    {
        return _execution is not null ? _execution.ReportProgressAsync(progress, message) : _progressCallback!(progress, message);
    }

    /// <summary>
    /// Renews only <see cref="WorkItemLock"/>, the application lock returned by <see cref="IWorkItemHandler.GetWorkItemLockAsync"/>.
    /// During job processing, failure signals <see cref="CancellationToken"/> and causes the job to attempt abandonment.
    /// To renew the queue entry as well, enable <see cref="IWorkItemHandler.AutoRenewLockOnProgress"/> and report progress.
    /// </summary>
    public Task RenewLockAsync()
    {
        if (_execution is not null)
            return _execution.RenewLockAsync();

        if (WorkItemLock != null)
            return WorkItemLock.RenewAsync();

        return Task.CompletedTask;
    }

    public T? GetData<T>() where T : class
    {
        return Data as T;
    }

    private WorkItemContext(object data, string jobId, ILock? workItemLock, CancellationToken cancellationToken)
    {
        Data = data;
        JobId = jobId;
        WorkItemLock = workItemLock;
        CancellationToken = cancellationToken;
    }
}
