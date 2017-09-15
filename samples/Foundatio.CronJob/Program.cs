using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Jobs;
using Foundatio.Logging;
using Foundatio.SampleJob;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Foundatio.CronJob {
    public class Program {
        public static void Main() {
            var loggerFactory = new LoggerFactory();
            var logger = loggerFactory.CreateLogger<Program>();

            var serviceProvider = SampleServiceProvider.Create(loggerFactory);

            for (int i = 0; i < 5; i++) {
                Task.Run(() => {
                    var cronService = new CronService(serviceProvider.GetService<ICacheClient>(), loggerFactory);

                    // every minute
                    cronService.Add(serviceProvider.GetService<EveryMinuteJob>(), "* * * * *");

                    // every even minute
                    cronService.Add(() => serviceProvider.GetService<EvenMinuteJob>(), "*/2 * * * *");

                    logger.LogInformation($"Cron Service ({i}) Running on {Thread.CurrentThread.ManagedThreadId}");
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
            _logger.LogInformation($"EveryMinuteJob Run {Thread.CurrentThread.ManagedThreadId}");
            return Task.FromResult(JobResult.Success);
        }
    }

    public class EvenMinuteJob : IJob {
        private readonly ILogger _logger;

        public EvenMinuteJob(ILoggerFactory loggerFactory) {
            _logger = loggerFactory.CreateLogger<EvenMinuteJob>();
        }

        public Task<JobResult> RunAsync(CancellationToken cancellationToken = default(CancellationToken)) {
            _logger.LogInformation($"EvenMinuteJob Run {Thread.CurrentThread.ManagedThreadId}");
            return Task.FromResult(JobResult.Success);
        }
    }
}
