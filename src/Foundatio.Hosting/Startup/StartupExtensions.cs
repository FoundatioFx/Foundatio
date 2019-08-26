using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Hosting.Startup {
    public class RunStartupActionsResult {
        public bool Success { get; set; }
        public string FailedActionName { get; set; }
        public string ErrorMessage { get; set; }
    }
    
    public static partial class StartupExtensions {
        public static async Task<RunStartupActionsResult> RunStartupActionsAsync(this IServiceProvider serviceProvider, CancellationToken shutdownToken = default) {
            using (var startupActionsScope = serviceProvider.CreateScope()) {
                var sw = Stopwatch.StartNew();
                var logger = startupActionsScope.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger("StartupActions") ?? NullLogger.Instance;
                var startupActions = startupActionsScope.ServiceProvider.GetServices<StartupActionRegistration>().ToArray();
                logger.LogInformation("Found {StartupActions} registered startup action(s).", startupActions.Length);

                var startupActionPriorityGroups = startupActions.GroupBy(s => s.Priority).OrderBy(s => s.Key).ToArray();
                foreach (var startupActionGroup in startupActionPriorityGroups) {
                    int startupActionsCount = startupActionGroup.Count();
                    string[] startupActionsNames = startupActionGroup.Select(a => a.Name).ToArray();
                    var swGroup = Stopwatch.StartNew();
                    string failedActionName = null;
                    string errorMessage = null;
                    try {
                        if (startupActionsCount == 1)
                            logger.LogInformation("Running {StartupActions} (priority {Priority}) startup action...",
                                startupActionsNames, startupActionGroup.Key);
                        else
                            logger.LogInformation(
                                "Running {StartupActions} (priority {Priority}) startup actions in parallel...",
                                startupActionsNames, startupActionGroup.Key);

                        await Task.WhenAll(startupActionGroup.Select(async a => {
                            try {
                                // ReSharper disable once AccessToDisposedClosure
                                await a.RunAsync(startupActionsScope.ServiceProvider, shutdownToken).AnyContext();
                            } catch (Exception ex) {
                                failedActionName = a.Name;
                                errorMessage = ex.Message;
                                logger.LogError(ex, "Error running {StartupAction} startup action: {Message}", a.Name,
                                    ex.Message);
                                throw;
                            }
                        })).AnyContext();
                        swGroup.Stop();

                        if (startupActionsCount == 1)
                            logger.LogInformation("Completed {StartupActions} startup action in {Duration:mm\\:ss}.",
                                startupActionsNames, swGroup.Elapsed);
                        else
                            logger.LogInformation("Completed {StartupActions} startup actions in {Duration:mm\\:ss}.",
                                startupActionsNames, swGroup.Elapsed);
                    } catch {
                        return new RunStartupActionsResult {
                            Success = false, FailedActionName = failedActionName, ErrorMessage = errorMessage
                        };
                    }
                }

                sw.Stop();
                logger.LogInformation("Completed all {StartupActions} startup action(s) in {Duration:mm\\:ss}.",
                    startupActions.Length, sw.Elapsed);

                return new RunStartupActionsResult {Success = true};
            }
        }

        public static void AddStartupAction<T>(this IServiceCollection services, int? priority = null) where T : IStartupAction {
            services.TryAddSingleton<StartupActionsContext>();
            if (!services.Any(s => s.ServiceType == typeof(IHostedService) && s.ImplementationType == typeof(RunStartupActionsService)))
                services.AddSingleton<IHostedService, RunStartupActionsService>();
            services.TryAddTransient(typeof(T));
            services.AddTransient(s => new StartupActionRegistration(typeof(T).Name, typeof(T), priority));
        }

        public static void AddStartupAction<T>(this IServiceCollection services, string name, int? priority = null) where T : IStartupAction {
            services.TryAddSingleton<StartupActionsContext>();
            if (!services.Any(s => s.ServiceType == typeof(IHostedService) && s.ImplementationType == typeof(RunStartupActionsService)))
                services.AddSingleton<IHostedService, RunStartupActionsService>();
            services.TryAddTransient(typeof(T));
            services.AddTransient(s => new StartupActionRegistration(name, typeof(T), priority));
        }

        public static void AddStartupAction(this IServiceCollection services, string name, Action action, int? priority = null) {
            services.AddStartupAction(name, ct => action(), priority);
        }

        public static void AddStartupAction(this IServiceCollection services, string name, Action<IServiceProvider> action, int? priority = null) {
            services.AddStartupAction(name, (sp, ct) => action(sp), priority);
        }

        public static void AddStartupAction(this IServiceCollection services, string name, Action<IServiceProvider, CancellationToken> action, int? priority = null) {
            services.TryAddSingleton<StartupActionsContext>();
            if (!services.Any(s => s.ServiceType == typeof(IHostedService) && s.ImplementationType == typeof(RunStartupActionsService)))
                services.AddSingleton<IHostedService, RunStartupActionsService>();
            services.AddTransient(s => new StartupActionRegistration(name, (sp, ct) => {
                action(sp, ct);
                return Task.CompletedTask;
            }, priority));
        }

        public static void AddStartupAction(this IServiceCollection services, string name, Func<Task> action, int? priority = null) {
            services.AddStartupAction(name, (sp, ct) => action(), priority);
        }

        public static void AddStartupAction(this IServiceCollection services, string name, Func<IServiceProvider, Task> action, int? priority = null) {
            services.AddStartupAction(name, (sp, ct) => action(sp), priority);
        }

        public static void AddStartupAction(this IServiceCollection services, string name, Func<IServiceProvider, CancellationToken, Task> action, int? priority = null) {
            services.TryAddSingleton<StartupActionsContext>();
            if (!services.Any(s => s.ServiceType == typeof(IHostedService) && s.ImplementationType == typeof(RunStartupActionsService)))
                services.AddSingleton<IHostedService, RunStartupActionsService>();
            services.AddTransient(s => new StartupActionRegistration(name, action, priority));
        }

        public const string CheckForStartupActionsName = "CheckForStartupActions";
        public static IHealthChecksBuilder AddCheckForStartupActions(this IHealthChecksBuilder builder, params string[] tags) {
            return builder.AddCheck<StartupActionsHealthCheck>(CheckForStartupActionsName, null, tags);
        }

        public static IApplicationBuilder UseWaitForStartupActionsBeforeServingRequests(this IApplicationBuilder builder) {
            return builder.UseMiddleware<WaitForStartupActionsBeforeServingRequestsMiddleware>();
        }

        public static IApplicationBuilder UseHealthChecks(this IApplicationBuilder builder, PathString path, params string[] tags) {
            if (tags == null)
                tags = new string[0];
            
            return builder.UseHealthChecks(path, new HealthCheckOptions { Predicate = c => c.Tags.Any(t => tags.Contains(t, StringComparer.OrdinalIgnoreCase)) });
        }

        public static IApplicationBuilder UseReadyHealthChecks(this IApplicationBuilder builder, params string[] tags) {
            if (tags == null)
                tags = new string[0];

            var options = new HealthCheckOptions {
                Predicate = c => c.Tags.Any(t => tags.Contains(t, StringComparer.OrdinalIgnoreCase))
            };
            return builder.UseHealthChecks("/ready", options);
        }

        public static void AddStartupActionToWaitForHealthChecks(this IServiceCollection services, params string[] tags) {
            if (tags == null)
                tags = new string[0];
            
            services.AddStartupActionToWaitForHealthChecks(c => c.Tags.Any(t => tags.Contains(t, StringComparer.OrdinalIgnoreCase)));
        }

        public static void AddStartupActionToWaitForHealthChecks(this IServiceCollection services, Func<HealthCheckRegistration, bool> shouldWaitForHealthCheck = null) {
            if (shouldWaitForHealthCheck == null)
                shouldWaitForHealthCheck = c => c.Tags.Contains("Critical", StringComparer.OrdinalIgnoreCase);

            services.AddStartupAction("WaitForHealthChecks", async (sp, t) => {
                if (t.IsCancellationRequested)
                    return;
                
                var healthCheckService = sp.GetService<HealthCheckService>();
                var logger = sp.GetService<ILoggerFactory>()?.CreateLogger("StartupActions") ?? NullLogger.Instance;
                var result = await healthCheckService.CheckHealthAsync(c => c.Name != CheckForStartupActionsName && shouldWaitForHealthCheck(c), t).AnyContext();
                while (result.Status == HealthStatus.Unhealthy && !t.IsCancellationRequested) {
                    logger.LogDebug("Last health check was unhealthy. Waiting 1s until next health check.");
                    await Task.Delay(1000, t).AnyContext();
                    result = await healthCheckService.CheckHealthAsync(c => c.Name != CheckForStartupActionsName && shouldWaitForHealthCheck(c), t).AnyContext();
                }
            }, -100);
        }
    }
}
