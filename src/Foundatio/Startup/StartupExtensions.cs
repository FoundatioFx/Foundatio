using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Utility;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Foundatio.Startup {
    public static partial class StartupExtensions {
        public static async Task RunStartupActionsAsync(this IServiceProvider container, CancellationToken shutdownToken = default) {
            foreach (var startupAction in container.GetServices<StartupActionRegistration>().GroupBy(s => s.Priority).OrderBy(s => s.Key))
                await Task.WhenAll(startupAction.Select(a => a.RunAsync(container, shutdownToken))).AnyContext();
        }

        public static void AddStartupAction<T>(this IServiceCollection container, int? priority = null) where T : IStartupAction {
            container.AddTransient(s => new StartupActionRegistration(typeof(T), priority));
        }

        public static void AddStartupAction(this IServiceCollection container, Action action, int? priority = null) {
            AddStartupAction(container, ct => action(), priority);
        }

        public static void AddStartupAction(this IServiceCollection container, Action<IServiceProvider> action, int? priority = null) {
            AddStartupAction(container, (sp, ct) => action(sp), priority);
        }

        public static void AddStartupAction(this IServiceCollection container, Action<IServiceProvider, CancellationToken> action, int? priority = null) {
            container.AddTransient(s => new StartupActionRegistration((sp, ct) => {
                action(sp, ct);
                return Task.CompletedTask;
            }, priority));
        }

        public static void AddStartupAction(this IServiceCollection container, Func<Task> action, int? priority = null) {
            container.AddStartupAction((sp, ct) => action(), priority);
        }

        public static void AddStartupAction(this IServiceCollection container, Func<IServiceProvider, Task> action, int? priority = null) {
            container.AddStartupAction((sp, ct) => action(sp), priority);
        }

        public static void AddStartupAction(this IServiceCollection container, Func<IServiceProvider, CancellationToken, Task> action, int? priority = null) {
            container.AddTransient(s => new StartupActionRegistration(action, priority));
        }
    }
}
