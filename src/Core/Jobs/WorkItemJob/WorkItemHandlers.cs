using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Foundatio.Utility;

namespace Foundatio.Jobs {
    public class WorkItemHandlers {
        private readonly ConcurrentDictionary<Type, Func<WorkItemContext, Task>> _handlers;

        public WorkItemHandlers() {
            _handlers = new ConcurrentDictionary<Type, Func<WorkItemContext, Task>>();
        }

        public void Register<T>(Func<WorkItemContext, Task> handler) where T : class {
            _handlers.TryAdd(typeof(T), handler);
        }

        public void Register<T>(Func<WorkItemContext> handler) where T : class {
            _handlers.TryAdd(typeof(T), ctx => {
                handler();
                return TaskHelper.Completed();
            });
        }

        public Func<WorkItemContext, Task> GetHandler(Type jobDataType) {
            Func<WorkItemContext, Task> handler;
            if (!_handlers.TryGetValue(jobDataType, out handler))
                return null;

            return handler;
        }
    }
}