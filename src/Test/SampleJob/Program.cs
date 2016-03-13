using System;
using Foundatio.Jobs;
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

            //var serviceProvider = new SampleServiceProvider(loggerFactory);
            //var serviceProvider = ServiceProvider.FindServiceProvider("Foundatio.SampleJob.SampleServiceProvider,Foundatio.SampleJob", loggerFactory);
            var serviceProvider = ServiceProvider.FindServiceProvider(typeof(PingQueueJob), loggerFactory);
            var job = serviceProvider.GetService<PingQueueJob>();
            return new JobRunner(job, loggerFactory).RunInConsole();
        }
    }
}
