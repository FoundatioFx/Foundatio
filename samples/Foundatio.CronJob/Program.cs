using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Jobs;
using Foundatio.SampleJob;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Foundatio.CronJob {
    public class Program {
        public static void Main() {
            var loggerFactory = new LoggerFactory().AddConsole();
            var logger = loggerFactory.CreateLogger<Program>();

            var serviceProvider = SampleServiceProvider.Create(loggerFactory);

            for (int i = 0; i < 5; i++) {
                Task.Run(() => {
                    var cronService = new CronService(serviceProvider.GetService<ICacheClient>(), loggerFactory);

                    // every minute
                    cronService.Add(serviceProvider.GetService<EveryMinuteJob>(), "* * * * *");

                    // every even minute
                    cronService.Add(() => serviceProvider.GetService<EvenMinuteJob>(), "*/2 * * * *");

                    if (logger.IsEnabled(LogLevel.Information))
                        logger.LogInformation("Cron Service ({Instance}) Running on {ManagedThreadId}", i, Thread.CurrentThread.ManagedThreadId);
                    cronService.Run();
                });
            }

            Console.ReadKey();
        }
    }

    public class EveryMinuteJob : IJob {
        private readonly ILogger _logger;

        public EveryMinuteJob(ILoggerFactory loggerFactory) {
            _logger = loggerFactory.CreateLogger<EveryMinuteJob>();
        }

        public Task<JobResult> RunAsync(CancellationToken cancellationToken = default(CancellationToken)) {
            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("EveryMinuteJob Run {ManagedThreadId}", Thread.CurrentThread.ManagedThreadId);
            return Task.FromResult(JobResult.Success);
        }
    }

    public class EvenMinuteJob : IJob {
        private readonly ILogger _logger;

        public EvenMinuteJob(ILoggerFactory loggerFactory) {
            _logger = loggerFactory.CreateLogger<EvenMinuteJob>();
        }

        public Task<JobResult> RunAsync(CancellationToken cancellationToken = default(CancellationToken)) {
            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("EvenMinuteJob Run {ManagedThreadId}", Thread.CurrentThread.ManagedThreadId);
            return Task.FromResult(JobResult.Success);
        }
    }
}
