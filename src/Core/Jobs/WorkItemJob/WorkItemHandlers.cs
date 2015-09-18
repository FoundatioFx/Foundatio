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
        Task<IDisposable> GetWorkItemLockAsync(WorkItemContext context, CancellationToken cancellationToken);

        Task HandleItemAsync(WorkItemContext context, CancellationToken cancellationToken);
    }

    public abstract class WorkItemHandlerBase : IWorkItemHandler {
        public virtual Task<IDisposable> GetWorkItemLockAsync(WorkItemContext context, CancellationToken cancellationToken) {
            return Task.FromResult(Disposable.Empty);
        }

        public abstract Task HandleItemAsync(WorkItemContext context, CancellationToken cancellationToken);
    }

    public class DelegateWorkItemHandler : IWorkItemHandler {
        private readonly Func<WorkItemContext, CancellationToken, Task> _handler;

        public DelegateWorkItemHandler(Func<WorkItemContext, CancellationToken, Task> handler, CancellationToken cancellationToken) {
            _handler = (context, token) => handler(context, cancellationToken);
        }

        public Task<IDisposable> GetWorkItemLockAsync(WorkItemContext context, CancellationToken cancellationToken) {
            return Task.FromResult(Disposable.Empty);
        }

        public Task HandleItemAsync(WorkItemContext context, CancellationToken cancellationToken) {
            if (_handler == null)
                return TaskHelper.Completed();

            return _handler(context, cancellationToken);
        }
    }
}