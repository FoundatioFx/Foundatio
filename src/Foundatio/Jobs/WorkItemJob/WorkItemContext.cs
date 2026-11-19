using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Lock;

namespace Foundatio.Jobs;

public class WorkItemContext
{
    private readonly Func<int, string?, Task>? _progressCallback;
    private readonly WorkItemExecution? _execution;

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

    private WorkItemContext(object data, string jobId, ILock? workItemLock, CancellationToken cancellationToken)
    {
        Data = data;
        JobId = jobId;
        WorkItemLock = workItemLock;
        CancellationToken = cancellationToken;
    }

    public object Data { get; private set; }
    public string JobId { get; private set; }
    public ILock? WorkItemLock { get; private set; }
    public JobResult Result { get; set; } = JobResult.Success;
    public CancellationToken CancellationToken { get; private set; }

    /// <summary>Reports progress. During job processing, a failed automatic work-item lock renewal faults the report before publication.</summary>
    public Task ReportProgressAsync(int progress, string? message = null)
    {
        return _execution is not null ? _execution.ReportProgressAsync(progress, message) : _progressCallback!(progress, message);
    }

    /// <summary>Renews the work-item lock. A job abandons its unsettled entry if this renewal fails.</summary>
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
}
