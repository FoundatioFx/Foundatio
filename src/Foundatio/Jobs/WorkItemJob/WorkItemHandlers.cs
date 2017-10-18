using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Lock;
using Foundatio.Queues;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Jobs {
    public class WorkItemHandlers {
        private readonly ConcurrentDictionary<Type, Lazy<IWorkItemHandler>> _handlers;

        public WorkItemHandlers() {
            _handlers = new ConcurrentDictionary<Type, Lazy<IWorkItemHandler>>();
        }

        public void Register<T>(IWorkItemHandler handler) {
            _handlers.TryAdd(typeof(T), new Lazy<IWorkItemHandler>(() => handler));
        }

        public void Register<T>(Func<IWorkItemHandler> handler) {
            _handlers.TryAdd(typeof(T), new Lazy<IWorkItemHandler>(handler));
        }

        public void Register<T>(Func<WorkItemContext, Task> handler, ILogger logger = null, Action<IQueueEntry<WorkItemData>, Type, object> logProcessingWorkItem = null, Action<IQueueEntry<WorkItemData>, Type, object> logAutoCompletedWorkItem = null) where T : class {
            _handlers.TryAdd(typeof(T), new Lazy<IWorkItemHandler>(() => new DelegateWorkItemHandler(handler, logger, logProcessingWorkItem, logAutoCompletedWorkItem)));
        }

        public IWorkItemHandler GetHandler(Type jobDataType) {
            if (!_handlers.TryGetValue(jobDataType, out var handler))
                return null;

            return handler.Value;
        }
    }

    public interface IWorkItemHandler {
        Task<ILock> GetWorkItemLockAsync(object workItem, CancellationToken cancellationToken = default(CancellationToken));
        Task HandleItemAsync(WorkItemContext context);
        bool AutoRenewLockOnProgress { get; set; }
        ILogger Log { get; set; }
        void LogProcessingQueueEntry(IQueueEntry<WorkItemData> queueEntry, Type workItemDataType, object workItem);
        void LogAutoCompletedQueueEntry(IQueueEntry<WorkItemData> queueEntry, Type workItemDataType, object workItem);
    }

    public abstract class WorkItemHandlerBase : IWorkItemHandler {
        public WorkItemHandlerBase(ILoggerFactory loggerFactory = null) {
            Log = loggerFactory?.CreateLogger(GetType()) ?? NullLogger.Instance;
        }
        public WorkItemHandlerBase(ILogger logger) {
            Log = logger ?? NullLogger.Instance;
        }

        public virtual Task<ILock> GetWorkItemLockAsync(object workItem, CancellationToken cancellationToken = default(CancellationToken)) {
            return Task.FromResult(Disposable.EmptyLock);
        }

        public bool AutoRenewLockOnProgress { get; set; }
        public ILogger Log { get; set; }

        public virtual void LogProcessingQueueEntry(IQueueEntry<WorkItemData> queueEntry, Type workItemDataType, object workItem) {
            if (Log.IsEnabled(LogLevel.Information))
                Log.LogInformation("Processing {TypeName} work item queue entry: {Id}.", workItemDataType.Name, queueEntry.Id);
        }

        public virtual void LogAutoCompletedQueueEntry(IQueueEntry<WorkItemData> queueEntry, Type workItemDataType, object workItem) {
            if (Log.IsEnabled(LogLevel.Information))
                Log.LogInformation("Auto completed {TypeName} work item queue entry: {Id}.", workItemDataType.Name, queueEntry.Id);
        }

        public abstract Task HandleItemAsync(WorkItemContext context);

        protected int CalculateProgress(long total, long completed, int startProgress = 0, int endProgress = 100) {
            return startProgress + (int)((100 * (double)completed / total) * (((double)endProgress - startProgress) / 100));
        }
    }

    public class DelegateWorkItemHandler : WorkItemHandlerBase {
        private readonly Func<WorkItemContext, Task> _handler;
        private readonly Action<IQueueEntry<WorkItemData>, Type, object> _logProcessingWorkItem;
        private readonly Action<IQueueEntry<WorkItemData>, Type, object> _logAutoCompletedWorkItem;

        public DelegateWorkItemHandler(Func<WorkItemContext, Task> handler, ILogger logger = null, Action<IQueueEntry<WorkItemData>, Type, object> logProcessingWorkItem = null, Action<IQueueEntry<WorkItemData>, Type, object> logAutoCompletedWorkItem = null) : base(logger) {
            _handler = handler;
            _logProcessingWorkItem = logProcessingWorkItem;
            _logAutoCompletedWorkItem = logAutoCompletedWorkItem;
        }

        public override Task HandleItemAsync(WorkItemContext context) {
            if (_handler == null)
                return Task.CompletedTask;

            return _handler(context);
        }

        public override void LogProcessingQueueEntry(IQueueEntry<WorkItemData> queueEntry, Type workItemDataType, object workItem) {
            if (_logProcessingWorkItem != null)
                _logProcessingWorkItem(queueEntry, workItemDataType, workItem);
            else
                base.LogProcessingQueueEntry(queueEntry, workItemDataType, workItem);
        }

        public override void LogAutoCompletedQueueEntry(IQueueEntry<WorkItemData> queueEntry, Type workItemDataType, object workItem) {
            if (_logAutoCompletedWorkItem != null)
                _logAutoCompletedWorkItem(queueEntry, workItemDataType, workItem);
            else
                base.LogAutoCompletedQueueEntry(queueEntry, workItemDataType, workItem);
        }
    }
}