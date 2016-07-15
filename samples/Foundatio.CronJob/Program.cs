using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Extensions;
using Foundatio.Jobs;
using Foundatio.Logging;
using Foundatio.Logging.NLog;
using Foundatio.ServiceProviders;

namespace Foundatio.CronJob {
    public class Program {
        public static void Main() {
            var loggerFactory = new LoggerFactory();
            loggerFactory.AddNLog();
            loggerFactory.DefaultLogLevel = LogLevel.Trace;
            var logger = loggerFactory.CreateLogger<Program>();

            var serviceProvider = ServiceProvider.FindAndGetServiceProvider(typeof(EveryMinuteJob), loggerFactory);

            for (int i = 0; i < 5; i++) {
                Task.Run(() => {
                    var cronService = new CronService(serviceProvider.GetService<ICacheClient>(), loggerFactory);

                    // every minute
                    cronService.Add(serviceProvider.GetService<EveryMinuteJob>(), "* * * * *");

                    // every even minute
                    cronService.Add(() => serviceProvider.GetService<EvenMinuteJob>(), "*/2 * * * *");

                    logger.Info($"Cron Service ({i}) Running on {Thread.CurrentThread.ManagedThreadId}");
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
            _logger.Info($"EveryMinuteJob Run {Thread.CurrentThread.ManagedThreadId}");
            return Task.FromResult(JobResult.Success);
        }
    }

    public class EvenMinuteJob : IJob {
        private readonly ILogger _logger;

        public EvenMinuteJob(ILoggerFactory loggerFactory) {
            _logger = loggerFactory.CreateLogger<EvenMinuteJob>();
        }

        public Task<JobResult> RunAsync(CancellationToken cancellationToken = default(CancellationToken)) {
            _logger.Info($"EvenMinuteJob Run {Thread.CurrentThread.ManagedThreadId}");
            return Task.FromResult(JobResult.Success);
        }
    }
}
