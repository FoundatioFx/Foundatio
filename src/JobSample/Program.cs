using System;
using System.Threading.Tasks;
using Foundatio.Jobs;
using Foundatio.JobSample.Jobs;
using Foundatio.Logging;
using Foundatio.Logging.NLog;
using SimpleInjector;

namespace Foundatio.JobSample {
    public class Program {
        public static int Main() {
            var loggerFactory = new LoggerFactory();
            loggerFactory.AddNLog();

            return new JobRunner(loggerFactory).RunInConsole<PingQueueJob>(serviceProvider => {
                var container = serviceProvider as Container;
                if (container == null)
                    return;

                container.RegisterSingleton<ILoggerFactory>(loggerFactory);
                container.Register(typeof(ILogger<>), typeof(Logger<>));

                container.AddStartupAction<EnqueuePings>();

                Task.Run(() => container.RunStartupActionsAsync().GetAwaiter().GetResult());
            });
        }
    }
}
