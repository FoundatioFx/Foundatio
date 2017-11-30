using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Jobs;
using Foundatio.Jobs.Commands;
using Microsoft.Extensions.CommandLineUtils;
using Microsoft.Extensions.Logging;
using SimpleInjector;

namespace Foundatio.CronJob {
    public class Program {
        public static int Main(string[] args) {
            var loggerFactory = new LoggerFactory().AddConsole();

            var getServiceProvider = new Func<IServiceProvider>(() => {
                var container = new Container();
                container.RegisterSingleton(loggerFactory);
                container.RegisterSingleton(typeof(ILogger<>), typeof(Logger<>));

                return container;
            });

            return JobCommands.Run(args, getServiceProvider, app => {
                app.Name = "Foundatio.JobCommands";
                app.FullName = "Foundation JobCommands Sample";
                app.ShortVersionGetter = () => "1.0.0";
            }, loggerFactory);
        }
    }

    [Job(Description = "Sample 1 job", Interval = "5s")]
    public class Sample1Job : IJob {
        private readonly ILogger _logger;

        public Sample1Job(ILoggerFactory loggerFactory) {
            _logger = loggerFactory.CreateLogger<Sample1Job>();
        }

        public Task<JobResult> RunAsync(CancellationToken cancellationToken = default(CancellationToken)) {
            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("Sample1Job Run {ManagedThreadId}", Thread.CurrentThread.ManagedThreadId);
            return Task.FromResult(JobResult.Success);
        }
    }

    [Job(Description = "Sample 2 job")]
    public class Sample2Job : IJob {
        private readonly ILogger _logger;

        public Sample2Job(ILoggerFactory loggerFactory) {
            _logger = loggerFactory.CreateLogger<Sample2Job>();
        }

        public string CustomArg { get; set; }

        public Task<JobResult> RunAsync(CancellationToken cancellationToken = default(CancellationToken)) {
            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("Sample2Job Run  CustomArg={CustomArg} {ManagedThreadId}", CustomArg, Thread.CurrentThread.ManagedThreadId);
            return Task.FromResult(JobResult.Success);
        }

        public static void Configure(JobCommandContext context) {
            var app = context.Application;
            var argOption = app.Option("-c|--custom-arg", "This is a custom job argument.", CommandOptionType.SingleValue);

            app.OnExecute(() => {
                var job = context.ServiceProvider.Value.GetService(typeof(Sample2Job)) as Sample2Job;
                job.CustomArg = argOption.Value();
                return new JobRunner(job, context.LoggerFactory, runContinuous: true, interval: TimeSpan.FromSeconds(1)).RunInConsole();
            });
        }
    }

    [Job(Description = "Excluded job", IsContinuous = false)]
    public class ExcludeMeJob : IJob {
        private readonly ILogger _logger;

        public ExcludeMeJob(ILoggerFactory loggerFactory) {
            _logger = loggerFactory.CreateLogger<Sample2Job>();
        }

        public Task<JobResult> RunAsync(CancellationToken cancellationToken = default(CancellationToken)) {
            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("ExcludeMeJob Run {ManagedThreadId}", Thread.CurrentThread.ManagedThreadId);
            return Task.FromResult(JobResult.Success);
        }
    }
}
