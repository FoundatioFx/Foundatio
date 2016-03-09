using System;
using System.Threading.Tasks;
using Foundatio.Jobs;
using Foundatio.SampleJob.Jobs;
using Foundatio.Logging;
using Foundatio.Logging.NLog;
using SimpleInjector;

namespace Foundatio.SampleJob {
    public class Program {
        public static int Main() {
            var loggerFactory = new LoggerFactory();
            loggerFactory.AddNLog();

            return new JobRunner(loggerFactory).RunInConsole<PingQueueJob, Bootstrapper>(serviceProvider => {
                var container = serviceProvider as Container;
                if (container == null)
                    return;

                container.AddStartupAction<EnqueuePings>();

                Task.Run(() => container.RunStartupActionsAsync().GetAwaiter().GetResult());
            });
        }
    }
}
