using System;
using System.Linq;
using System.Threading.Tasks;
using Foundatio.Hosting;
using Foundatio.Hosting.Jobs;
using Foundatio.Hosting.Startup;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Extensions.Logging;

namespace Foundatio.HostingSample {
    public class Program {
        public static int Main(string[] args) {
            try {
                CreateWebHostBuilder(args).Build().Run(Log.Logger.ToExtensionsLogger());
                return 0;
            } catch (Exception ex) {
                Log.Fatal(ex, "Job host terminated unexpectedly");
                return 1;
            } finally {
                Log.CloseAndFlush();
            }
        }
                
        public static IWebHostBuilder CreateWebHostBuilder(string[] args) {
            bool sample1 = args.Length == 0 || args.Contains("sample1", StringComparer.OrdinalIgnoreCase);
            bool sample2 = args.Length == 0 || args.Contains("sample2", StringComparer.OrdinalIgnoreCase);

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Verbose()
                .WriteTo.Console()
                .CreateLogger();

            var builder = WebHost.CreateDefaultBuilder(args)
                .UseSerilog(Log.Logger)
                .SuppressStatusMessages(true)
                .ConfigureServices(s => {
                    // will shutdown the host if no jobs are running
                    s.AddJobLifetimeService();

                    // insert a startup action that does not complete until the critical health checks are healthy
                    s.AddStartupActionToWaitForHealthChecks();

                    s.AddHealthChecks().AddCheck<MyCriticalHealthCheck>("My Critical Resource", tags: new[] { "Critical" });

                    // add health check that does not return healthy until the startup actions have completed
                    s.AddHealthChecks().AddCheckForStartupActionsComplete();

                    if (sample1)
                        s.AddJob<Sample1Job>(true);

                    if (sample2) {
                        s.AddHealthChecks().AddJobCheck<Sample2Job>();
                        s.AddJob<Sample2Job>(true);
                    }

                    s.AddStartupAction(async () => {
                        Log.Logger.Information("Running startup 1 action.");
                        for (int i = 0; i < 10; i++) {
                            await Task.Delay(1000);
                            Log.Logger.Information("Running startup 1 action...");
                        }

                        Log.Logger.Information("Done running startup 1 action.");
                    }, 1);

                    s.AddStartupAction(async () => {
                        Log.Logger.Information("Running startup 2 action.");
                        for (int i = 0; i < 5; i++) {
                            await Task.Delay(1500);
                            Log.Logger.Information("Running startup 2 action...");
                        }
                        Log.Logger.Information("Done running startup 2 action.");
                    }, 1);
                })
                .Configure(app => {
                    app.UseHealthChecks("/health");
                    app.UseHealthChecks("/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("Critical", StringComparer.OrdinalIgnoreCase) });

                    // this middleware will return Service Unavailable until the startup actions have completed
                    app.UseWaitForStartupActionsBeforeServingRequests();

                    // add mvc or other request middleware after the UseWaitForStartupActionsBeforeServingRequests call
                });

            return builder;
        }
    }
}
