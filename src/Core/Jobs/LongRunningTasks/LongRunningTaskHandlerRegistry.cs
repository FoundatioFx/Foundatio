using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace Foundatio.Jobs {
    public class LongRunningTaskHandlerRegistry {
        private readonly ConcurrentDictionary<Type, Func<LongRunningTaskContext, Task>> _handlers;

        public LongRunningTaskHandlerRegistry() {
            _handlers = new ConcurrentDictionary<Type, Func<LongRunningTaskContext, Task>>();
        }

        public void RegisterHandler<T>(Func<LongRunningTaskContext, Task> handler) where T : class {
            _handlers.TryAdd(typeof(T), handler);
        }

        public Func<LongRunningTaskContext, Task> GetHandler(Type jobDataType) {
            Func<LongRunningTaskContext, Task> handler;
            if (!_handlers.TryGetValue(jobDataType, out handler))
                return null;

            return handler;
        }
    }
}