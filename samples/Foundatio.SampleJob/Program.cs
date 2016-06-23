using System;
using Foundatio.Logging;
using Foundatio.Logging.NLog;
using Foundatio.Extensions;
using Foundatio.ServiceProviders;

namespace Foundatio.SampleJob {
    public class Program {
        public static int Main() {
            var loggerFactory = new LoggerFactory();
            loggerFactory.AddNLog();
            loggerFactory.DefaultLogLevel = LogLevel.Trace;

            var serviceProvider = ServiceProvider.FindAndGetServiceProvider(typeof(PingQueueJob), loggerFactory);

            return TopshelfJob.Run<PingQueueJob>(() => serviceProvider.GetService<PingQueueJob>(), loggerFactory: loggerFactory);
        }
    }
}
