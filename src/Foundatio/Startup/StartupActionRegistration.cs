using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Utility;
using Microsoft.Extensions.DependencyInjection;

namespace Foundatio.Startup {
    public class StartupActionRegistration {
        private readonly Func<IServiceProvider, CancellationToken, Task> _action;
        private readonly Type _actionType;
        private static int _currentAutoPriority;

        public StartupActionRegistration(Type startupType, int? priority = null) {
            _actionType = startupType;
            if (!priority.HasValue) {
                var priorityAttribute = _actionType.GetCustomAttributes(typeof(StartupPriorityAttribute), true).FirstOrDefault() as StartupPriorityAttribute;
                Priority = priorityAttribute?.Priority ?? Interlocked.Increment(ref _currentAutoPriority);
            } else {
                Priority = priority.Value;
            }
        }

        public StartupActionRegistration(Func<IServiceProvider, CancellationToken, Task> action, int? priority = null) {
            _action = action;
            if (!priority.HasValue)
                priority = Interlocked.Increment(ref _currentAutoPriority);

            Priority = priority.Value;
        }

        public int Priority { get; private set; }

        public async Task RunAsync(IServiceProvider serviceProvider, CancellationToken shutdownToken = default) {
            if (_actionType != null) {
                if (serviceProvider.GetRequiredService(_actionType) is IStartupAction startup)
                    await startup.RunAsync(shutdownToken).AnyContext();
            } else {
                await _action(serviceProvider, shutdownToken).AnyContext();
            }
        }
    }
}
