using System;
using Foundatio.Logging;
using Foundatio.Extensions;
using Foundatio.ServiceProviders;

namespace Foundatio.SampleJob {
    public class Program {
        public static int Main() {
            var loggerFactory = new LoggerFactory();
            loggerFactory.DefaultLogLevel = LogLevel.Trace;

            var serviceProvider = ServiceProvider.FindAndGetServiceProvider(typeof(PingQueueJob), loggerFactory);

            return TopshelfJob.Run<PingQueueJob>(() => serviceProvider.GetService<PingQueueJob>(), loggerFactory: loggerFactory);
        }
    }
}
