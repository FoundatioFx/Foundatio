using System;
using System.IO;
using Foundatio.Jobs.Commands;
using Foundatio.Jobs.Hosting;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Events;

namespace Exceptionless.Web {
    public class Program {
        public static int Main(string[] args) {
            try {
                CreateWebHostBuilder(args).Build().Run();
                return 0;
            } catch (Exception ex) {
                Log.Fatal(ex, "Job host terminated unexpectedly");
                return 1;
            } finally {
                Log.CloseAndFlush();
            }
        }

        public static IWebHostBuilder CreateWebHostBuilder(string[] args) {
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Verbose()
                .WriteTo.Console()
                .CreateLogger();

            var builder = WebHost.CreateDefaultBuilder(args)
                .UseSerilog(Log.Logger)
                .SuppressStatusMessages(true)
                .ConfigureServices(s => {
                    s.AddJobLifetime();
                    s.AddHealthChecks()
                        .AddCheck<SampleHealthCheck>("sample_check", failureStatus: HealthStatus.Degraded, tags: new[] { "sample" });
                    s.AddJob<Sample1Job>();
                    s.AddJob<Sample2Job>();
                })
                .Configure(app => {
                    app.UseHealthChecks("/health");
                });

            return builder;
        }
    }
}
