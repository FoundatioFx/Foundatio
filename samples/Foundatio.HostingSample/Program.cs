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
                        s.AddJob<Sample1Job>();

                    if (sample2) {
                        healthCheckBuilder.AddJobCheck<Sample2Job>();
                        s.AddJob<Sample2Job>();
                    }

                    s.AddStartupAction(async () => {
                        Log.Logger.Information("Running startup action.");
                        await Task.Delay(10000);
                        Log.Logger.Information("Done running startup action.");
                    });

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
