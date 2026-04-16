using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Lock;
using Foundatio.Queues;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Jobs;

public class WorkItemHandlers
{
    private readonly ConcurrentDictionary<Type, Lazy<IWorkItemHandler>> _handlers;

    public WorkItemHandlers()
    {
        _handlers = new ConcurrentDictionary<Type, Lazy<IWorkItemHandler>>();
    }

    public void Register<T>(IWorkItemHandler handler)
    {
        _handlers.TryAdd(typeof(T), new Lazy<IWorkItemHandler>(() => handler));
    }

    public void Register<T>(Func<IWorkItemHandler> handler)
    {
        _handlers.TryAdd(typeof(T), new Lazy<IWorkItemHandler>(handler));
    }

    public void Register<T>(Func<WorkItemContext, Task> handler, ILogger? logger = null, Action<IQueueEntry<WorkItemData>, Type, object>? logProcessingWorkItem = null, Action<IQueueEntry<WorkItemData>, Type, object>? logAutoCompletedWorkItem = null) where T : class
    {
        _handlers.TryAdd(typeof(T), new Lazy<IWorkItemHandler>(() => new DelegateWorkItemHandler(handler, logger, logProcessingWorkItem, logAutoCompletedWorkItem)));
    }

    public IWorkItemHandler? GetHandler(Type jobDataType)
    {
        if (!_handlers.TryGetValue(jobDataType, out var handler))
            return null;

        return handler.Value;
    }
}

/// <summary>
/// Defines a handler that processes a specific type of work item dequeued by <see cref="WorkItemJob"/>.
/// Register handlers with <see cref="WorkItemHandlers"/> to map work item types to processing logic.
/// For simple cases, use <see cref="DelegateWorkItemHandler"/>; for complex scenarios, extend <see cref="WorkItemHandlerBase"/>.
/// </summary>
public interface IWorkItemHandler
{
    /// <summary>
    /// Acquires a lock for the given work item to prevent concurrent processing.
    /// </summary>
    /// <param name="workItem">The deserialized work item payload.</param>
    /// <param name="cancellationToken">Token to cancel the lock acquisition.</param>
    /// <returns>
    /// An <see cref="ILock"/> if the lock was acquired, or <c>null</c> if the work item
    /// should be abandoned (e.g., another instance is already processing it).
    /// The default implementation returns <see cref="Disposable.EmptyLock"/> (always succeeds).
    /// </returns>
    Task<ILock?> GetWorkItemLockAsync(object workItem, CancellationToken cancellationToken = default);

    /// <summary>
    /// Processes a single work item. The <paramref name="context"/> provides access to the
    /// deserialized payload via <see cref="WorkItemContext.GetData{T}"/> and supports reporting
    /// progress via <see cref="WorkItemContext.ReportProgressAsync(int, string?)"/>.
    /// If this method completes without calling <see cref="IQueueEntry.CompleteAsync"/>,
    /// the entry is auto-completed when <see cref="AutoRenewLockOnProgress"/> is <c>true</c>.
    /// </summary>
    /// <param name="context">Context containing the queue entry, work item data, and progress reporting.</param>
    Task HandleItemAsync(WorkItemContext context);

    /// <summary>
    /// When <c>true</c>, the lock on the queue entry is automatically renewed each time
    /// the handler reports progress. This prevents long-running work items from timing out.
    /// </summary>
    bool AutoRenewLockOnProgress { get; set; }

    /// <summary>
    /// The logger used by this handler for diagnostic output.
    /// </summary>
    ILogger Log { get; set; }

    /// <summary>
    /// Called when a work item begins processing. Override to customize logging behavior.
    /// </summary>
    /// <param name="queueEntry">The raw queue entry being processed.</param>
    /// <param name="workItemDataType">The CLR type of the deserialized work item payload.</param>
    /// <param name="workItem">The deserialized work item payload.</param>
    void LogProcessingQueueEntry(IQueueEntry<WorkItemData> queueEntry, Type workItemDataType, object workItem);

    /// <summary>
    /// Called when a work item is auto-completed after the handler returns without
    /// explicitly completing or abandoning the entry. Override to customize logging behavior.
    /// </summary>
    /// <param name="queueEntry">The raw queue entry that was auto-completed.</param>
    /// <param name="workItemDataType">The CLR type of the deserialized work item payload.</param>
    /// <param name="workItem">The deserialized work item payload.</param>
    void LogAutoCompletedQueueEntry(IQueueEntry<WorkItemData> queueEntry, Type workItemDataType, object workItem);
}

public abstract class WorkItemHandlerBase : IWorkItemHandler
{
    public WorkItemHandlerBase(ILoggerFactory? loggerFactory = null)
    {
        Log = loggerFactory?.CreateLogger(GetType()) ?? NullLogger.Instance;
    }
    public WorkItemHandlerBase(ILogger? logger)
    {
        Log = logger ?? NullLogger.Instance;
    }

    public virtual Task<ILock?> GetWorkItemLockAsync(object workItem, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<ILock?>(Disposable.EmptyLock);
    }

    public bool AutoRenewLockOnProgress { get; set; }
    public ILogger Log { get; set; }

    public virtual void LogProcessingQueueEntry(IQueueEntry<WorkItemData> queueEntry, Type workItemDataType, object workItem)
    {
        Log.LogInformation("Processing {TypeName} work item queue entry: {QueueEntryId}", workItemDataType.Name, queueEntry.Id);
    }

    public virtual void LogAutoCompletedQueueEntry(IQueueEntry<WorkItemData> queueEntry, Type workItemDataType, object workItem)
    {
        Log.LogInformation("Auto completed {TypeName} work item queue entry: {QueueEntryId}", workItemDataType.Name, queueEntry.Id);
    }

    public abstract Task HandleItemAsync(WorkItemContext context);

    protected int CalculateProgress(long total, long completed, int startProgress = 0, int endProgress = 100)
    {
        return startProgress + (int)((100 * (double)completed / total) * (((double)endProgress - startProgress) / 100));
    }
}

public class DelegateWorkItemHandler : WorkItemHandlerBase
{
    private readonly Func<WorkItemContext, Task> _handler;
    private readonly Action<IQueueEntry<WorkItemData>, Type, object>? _logProcessingWorkItem;
    private readonly Action<IQueueEntry<WorkItemData>, Type, object>? _logAutoCompletedWorkItem;

    public DelegateWorkItemHandler(Func<WorkItemContext, Task> handler, ILogger? logger = null, Action<IQueueEntry<WorkItemData>, Type, object>? logProcessingWorkItem = null, Action<IQueueEntry<WorkItemData>, Type, object>? logAutoCompletedWorkItem = null) : base(logger)
    {
        ArgumentNullException.ThrowIfNull(handler);

        _handler = handler;
        _logProcessingWorkItem = logProcessingWorkItem;
        _logAutoCompletedWorkItem = logAutoCompletedWorkItem;
    }

    public override Task HandleItemAsync(WorkItemContext context)
    {
        return _handler(context);
    }

    public override void LogProcessingQueueEntry(IQueueEntry<WorkItemData> queueEntry, Type workItemDataType, object workItem)
    {
        if (_logProcessingWorkItem != null)
            _logProcessingWorkItem(queueEntry, workItemDataType, workItem);
        else
            base.LogProcessingQueueEntry(queueEntry, workItemDataType, workItem);
    }

    public override void LogAutoCompletedQueueEntry(IQueueEntry<WorkItemData> queueEntry, Type workItemDataType, object workItem)
    {
        if (_logAutoCompletedWorkItem != null)
            _logAutoCompletedWorkItem(queueEntry, workItemDataType, workItem);
        else
            base.LogAutoCompletedQueueEntry(queueEntry, workItemDataType, workItem);
    }
}
