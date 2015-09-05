using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Foundatio.ServiceProviders;
using Foundatio.Utility;

namespace Foundatio.Jobs {
    public class WorkItemHandlers {
        private readonly ConcurrentDictionary<Type, Lazy<IWorkItemHandler>> _handlers;

        public WorkItemHandlers() {
            _handlers = new ConcurrentDictionary<Type, Lazy<IWorkItemHandler>>();
        }

        public void Register<TWorkItem, THandler>()
            where TWorkItem : class
            where THandler : IWorkItemHandler {

            _handlers.TryAdd(typeof (TWorkItem),
                new Lazy<IWorkItemHandler>(
                    () => ServiceProvider.Current.GetService(typeof(THandler)) as IWorkItemHandler));
        }

        public void Register<T>(IWorkItemHandler handler) {
            _handlers.TryAdd(typeof(T), new Lazy<IWorkItemHandler>(() => handler));
        }

        public void Register<T>(Func<WorkItemContext, Task> handler) where T : class {
            _handlers.TryAdd(typeof(T), new Lazy<IWorkItemHandler>(() => new DelegateWorkItemHandler(handler)));
        }

        public void Register<T>(Func<WorkItemContext> handler) where T : class {
            Register<T>(ctx => {
                handler();
                return TaskHelper.Completed();
            });
        }

        public IWorkItemHandler GetHandler(Type jobDataType) {
            Lazy<IWorkItemHandler> handler;
            if (!_handlers.TryGetValue(jobDataType, out handler))
                return null;

            return handler.Value;
        }
    }

    public interface IWorkItemHandler
    {
        IDisposable GetWorkItemLock(WorkItemContext context);
        Task HandleItem(WorkItemContext context);
    }

    public abstract class WorkItemHandlerBase : IWorkItemHandler
    {
        public virtual IDisposable GetWorkItemLock(WorkItemContext context)
        {
            return Disposable.Empty;
        }

        public abstract Task HandleItem(WorkItemContext context);
    }

    public class DelegateWorkItemHandler : IWorkItemHandler {
        private readonly Func<WorkItemContext, Task> _handler;

        public DelegateWorkItemHandler(Func<WorkItemContext, Task> handler) {
            _handler = handler;
        }

        public IDisposable GetWorkItemLock(WorkItemContext context)
        {
            return Disposable.Empty;
        }

        public Task HandleItem(WorkItemContext context) {
            if (_handler == null)
                return TaskHelper.Completed();

            return _handler(context);
        }
    }
}