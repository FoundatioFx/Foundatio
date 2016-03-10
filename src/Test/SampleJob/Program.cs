using System;
using Foundatio.Jobs;
using Foundatio.SampleJob.Jobs;
using Foundatio.Logging;
using Foundatio.Logging.NLog;

namespace Foundatio.SampleJob {
    public class Program {
        public static int Main() {
            var loggerFactory = new LoggerFactory();
            loggerFactory.AddNLog();
            loggerFactory.DefaultLogLevel = LogLevel.Trace;

            return new JobRunner(loggerFactory).RunInConsole<PingQueueJob, Bootstrapper>();
        }
    }
}
