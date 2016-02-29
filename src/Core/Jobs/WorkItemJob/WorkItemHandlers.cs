using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Lock;
using Foundatio.Logging;
using Foundatio.ServiceProviders;
using Foundatio.Utility;

namespace Foundatio.Jobs {
    public class WorkItemHandlers {
        private readonly ConcurrentDictionary<Type, Lazy<IWorkItemHandler>> _handlers;

        public WorkItemHandlers() {
            _handlers = new ConcurrentDictionary<Type, Lazy<IWorkItemHandler>>();
        }

        public void Register<TWorkItem, THandler>() where TWorkItem : class where THandler : IWorkItemHandler {
            _handlers.TryAdd(typeof(TWorkItem), new Lazy<IWorkItemHandler>(() => ServiceProvider.Current.GetService(typeof(THandler)) as IWorkItemHandler));
        }

        public void Register<T>(IWorkItemHandler handler) {
            _handlers.TryAdd(typeof(T), new Lazy<IWorkItemHandler>(() => handler));
        }

        public void Register<T>(Func<WorkItemContext, Task> handler, CancellationToken cancellationToken = default(CancellationToken)) where T : class {
            _handlers.TryAdd(typeof(T), new Lazy<IWorkItemHandler>(() => new DelegateWorkItemHandler(handler)));
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
    }

    public interface IOneTimeWorkItemHandler : IWorkItemHandler {
        string GetKey();
    }

    public abstract class OneTimeWorkItemHandlerBase : WorkItemHandlerBase, IOneTimeWorkItemHandler {
        public OneTimeWorkItemHandlerBase(ILoggerFactory loggerFactory = null) : base(loggerFactory) {}

        public abstract string GetKey();
    }

    public abstract class WorkItemHandlerBase : IWorkItemHandler {
        protected readonly ILogger _logger;

        public WorkItemHandlerBase(ILoggerFactory loggerFactory = null) {
            _logger = loggerFactory?.CreateLogger(GetType()) ?? NullLogger.Instance;
        }

        public virtual Task<ILock> GetWorkItemLockAsync(object workItem, CancellationToken cancellationToken = default(CancellationToken)) {
            return Task.FromResult(Disposable.EmptyLock);
        }
        
        public bool AutoRenewLockOnProgress { get; set; }

        public abstract Task HandleItemAsync(WorkItemContext context);
        
        protected int CalculateProgress(long total, long completed, int startProgress = 0, int endProgress = 100) {
            return startProgress + (int)((100 * (double)completed / total) * (((double)endProgress - startProgress) / 100));
        }
    }

    public class DelegateWorkItemHandler : WorkItemHandlerBase {
        private readonly Func<WorkItemContext, Task> _handler;

        public DelegateWorkItemHandler(Func<WorkItemContext, Task> handler, ILoggerFactory loggerFactory = null) : base(loggerFactory) {
            _handler = handler;
        }

        public override Task HandleItemAsync(WorkItemContext context) {
            if (_handler == null)
                return TaskHelper.Completed();

            return _handler(context);
        }
    }
}