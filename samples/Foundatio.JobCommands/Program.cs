using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Jobs;
using Foundatio.Jobs.Commands;
using Foundatio.Jobs.Hosting;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
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
            string environment = Environment.GetEnvironmentVariable("AppMode");
            if (String.IsNullOrWhiteSpace(environment))
                environment = "Production";

            string currentDirectory = Directory.GetCurrentDirectory();
            var config = new ConfigurationBuilder()
                .SetBasePath(currentDirectory)
                .AddCommandLine(args)
                .Build();

            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(config);
            var container = services.BuildServiceProvider();

            var loggerConfig = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Console();
            Log.Logger = loggerConfig.CreateLogger();

            var builder = WebHost.CreateDefaultBuilder(args)
                .UseEnvironment(environment)
                .UseKestrel(c => {
                    c.AddServerHeader = false;
                })
                .UseSerilog(Log.Logger)
                .SuppressStatusMessages(true)
                .UseConfiguration(config)
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

    [Job(Description = "Sample 1 job", Interval = "10s", IterationLimit = 10)]
    public class Sample1Job : IJob {
        private readonly Microsoft.Extensions.Logging.ILogger _logger;
        private int _iterationCount = 0;

        public Sample1Job(ILoggerFactory loggerFactory) {
            _logger = loggerFactory.CreateLogger<Sample1Job>();
        }

        public Task<JobResult> RunAsync(CancellationToken cancellationToken = default(CancellationToken)) {
            Interlocked.Increment(ref _iterationCount);
            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("Sample1Job Run #{IterationCount} Thread={ManagedThreadId}", _iterationCount, Thread.CurrentThread.ManagedThreadId);
            return Task.FromResult(JobResult.Success);
        }
    }

    [Job(Description = "Sample 2 job", Interval = "2s", IterationLimit = 10)]
    public class Sample2Job : IJob {
        private readonly Microsoft.Extensions.Logging.ILogger _logger;
        private int _iterationCount = 0;

        public Sample2Job(ILoggerFactory loggerFactory) {
            _logger = loggerFactory.CreateLogger<Sample2Job>();
        }

        public string CustomArg { get; set; }

        public Task<JobResult> RunAsync(CancellationToken cancellationToken = default(CancellationToken)) {
            Interlocked.Increment(ref _iterationCount);
            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("Sample2Job Run #{IterationCount} CustomArg={CustomArg} Thread={ManagedThreadId}", _iterationCount, CustomArg, Thread.CurrentThread.ManagedThreadId);
            return Task.FromResult(JobResult.Success);
        }
    }

    public class SampleHealthCheck : IHealthCheck {
        public string Name => "sample_check";

        public bool StartupTaskCompleted { get; set; } = false;

        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default(CancellationToken)) {
            return Task.FromResult(HealthCheckResult.Healthy("The startup task is finished."));
        }
    }
}
