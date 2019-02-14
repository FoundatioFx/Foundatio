using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Foundatio.Hosting.Jobs;
using Foundatio.Hosting.Startup;
using Foundatio.Startup;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Serilog;

namespace Foundatio.JobCommands {
    public class Program {
        public static int Main(string[] args) {
            try {
                CreateWebHostBuilder(args).RunJobHost();
                return 0;
            } catch (Exception ex) {
                Log.Fatal(ex, "Job host terminated unexpectedly");
                return 1;
            } finally {
                Log.CloseAndFlush();
                if (Debugger.IsAttached)
                    Console.ReadKey();
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
                .SuppressStatusMessages(false)
                .ConfigureServices(s => {
                    s.AddSingleton<StartupHealthCheck>();
                    var healthCheckBuilder = s.AddHealthChecks()
                        .Add(new HealthCheckRegistration("Startup", p => p.GetRequiredService<StartupHealthCheck>(), null, new[] { "Core" }));

                    if (sample1)
                        s.AddJob<Sample1Job>(true);

                    if (sample2) {
                        healthCheckBuilder.AddJobCheck<Sample2Job>();
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
                        for (int i = 0; i < 10; i++) {
                            await Task.Delay(1000);
                            Log.Logger.Information("Running startup 2 action...");
                        }
                        Log.Logger.Information("Done running startup 2 action.");
                    }, 1);

                    s.AddStartupTaskService();
                })
                .Configure(app => {
                    app.UseHealthChecks("/health");
                    app.UseStartupMiddleware();
                });

            return builder;
        }
    }
}
