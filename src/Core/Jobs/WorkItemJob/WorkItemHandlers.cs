using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
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

        public void Register<T>(Func<WorkItemContext, CancellationToken, Task> handler, CancellationToken cancellationToken = default(CancellationToken)) where T : class {
            _handlers.TryAdd(typeof(T), new Lazy<IWorkItemHandler>(() => new DelegateWorkItemHandler(handler, cancellationToken)));
        }

        public IWorkItemHandler GetHandler(Type jobDataType) {
            Lazy<IWorkItemHandler> handler;
            if (!_handlers.TryGetValue(jobDataType, out handler))
                return null;

            return handler.Value;
        }
    }

    public interface IWorkItemHandler {
        Task<IDisposable> GetWorkItemLockAsync(WorkItemContext context, CancellationToken cancellationToken = default(CancellationToken));

        Task HandleItemAsync(WorkItemContext context, CancellationToken cancellationToken = default(CancellationToken));
    }

    public abstract class WorkItemHandlerBase : IWorkItemHandler {
        public virtual Task<IDisposable> GetWorkItemLockAsync(WorkItemContext context, CancellationToken cancellationToken = default(CancellationToken)) {
            return Task.FromResult(Disposable.Empty);
        }

        public abstract Task HandleItemAsync(WorkItemContext context, CancellationToken cancellationToken = default(CancellationToken));
        
        protected int CalculateProgress(long total, long completed, int startProgress = 0, int endProgress = 100) {
            return Math.Min(startProgress + (int)((100 * (double)completed / total) * (((double)endProgress - startProgress) / 100)), endProgress);
        }
    }

    public class DelegateWorkItemHandler : IWorkItemHandler {
        private readonly Func<WorkItemContext, CancellationToken, Task> _handler;

        public DelegateWorkItemHandler(Func<WorkItemContext, CancellationToken, Task> handler, CancellationToken cancellationToken = default(CancellationToken)) {
            _handler = (context, token) => handler(context, cancellationToken);
        }

        public Task<IDisposable> GetWorkItemLockAsync(WorkItemContext context, CancellationToken cancellationToken = default(CancellationToken)) {
            return Task.FromResult(Disposable.Empty);
        }

        public Task HandleItemAsync(WorkItemContext context, CancellationToken cancellationToken = default(CancellationToken)) {
            if (_handler == null)
                return TaskHelper.Completed();

            return _handler(context, cancellationToken);
        }
    }
}