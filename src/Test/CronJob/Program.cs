using System;
using System.Threading;
using Foundatio.Caching;
using Foundatio.Extensions;
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
            var cronService = new CronService(serviceProvider.GetService<ICacheClient>(), loggerFactory);

            // every minute
            cronService.Add<EveryMinuteJob>(() => serviceProvider.GetService<EveryMinuteJob>(), "* * * * *");

            // every even minute
            cronService.Add<EvenMinuteJob>(() => serviceProvider.GetService<EvenMinuteJob>(), "*/2 * * * *");

            logger.Info($"Cron Service Running on {Thread.CurrentThread.ManagedThreadId}");
            cronService.Run();

            Console.ReadKey();
        }
    }
}
