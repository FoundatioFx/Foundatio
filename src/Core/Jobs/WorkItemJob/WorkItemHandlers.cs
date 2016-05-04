using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Lock;
using Foundatio.Logging;
using Foundatio.Utility;

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

        public void Register<T>(Func<WorkItemContext, Task> handler, ILogger logger = null, bool autoLogQueueProcessingEvents = true) where T : class {
            _handlers.TryAdd(typeof(T), new Lazy<IWorkItemHandler>(() => new DelegateWorkItemHandler(handler, logger, autoLogQueueProcessingEvents)));
        }

        public IWorkItemHandler GetHandler(Type jobDataType) {
            Lazy<IWorkItemHandler> handler;
            if (!_handlers.TryGetValue(jobDataType, out handler))
                return null;

            return handler.Value;
        }
    }

    public interface IWorkItemHandler {
        Task<ILock> GetWorkItemLockAsync(object workItem, CancellationToken cancellationToken = default(CancellationToken));
        Task HandleItemAsync(WorkItemContext context);
        bool AutoRenewLockOnProgress { get; set; }
        bool AutoLogQueueProcessingEvents { get; set; }
        ILogger Log { get; set; }
    }
    
    public abstract class WorkItemHandlerBase : IWorkItemHandler {
        public WorkItemHandlerBase(ILoggerFactory loggerFactory = null) {
            Log = loggerFactory.CreateLogger(GetType());
        }
        public WorkItemHandlerBase(ILogger logger) {
            Log = logger ?? NullLogger.Instance;
        }

        public virtual Task<ILock> GetWorkItemLockAsync(object workItem, CancellationToken cancellationToken = default(CancellationToken)) {
            return Task.FromResult(Disposable.EmptyLock);
        }
        
        public bool AutoRenewLockOnProgress { get; set; }
        public bool AutoLogQueueProcessingEvents { get; set; } = true;
        public ILogger Log { get; set; }

        public abstract Task HandleItemAsync(WorkItemContext context);
        
        protected int CalculateProgress(long total, long completed, int startProgress = 0, int endProgress = 100) {
            return startProgress + (int)((100 * (double)completed / total) * (((double)endProgress - startProgress) / 100));
        }
    }

    public class DelegateWorkItemHandler : WorkItemHandlerBase {
        private readonly Func<WorkItemContext, Task> _handler;

        public DelegateWorkItemHandler(Func<WorkItemContext, Task> handler, ILogger logger = null, bool autoLogQueueProcessingEvents = true) : base(logger) {
            _handler = handler;
            AutoLogQueueProcessingEvents = autoLogQueueProcessingEvents;
        }

        public override Task HandleItemAsync(WorkItemContext context) {
            if (_handler == null)
                return TaskHelper.Completed;

            return _handler(context);
        }
    }
}