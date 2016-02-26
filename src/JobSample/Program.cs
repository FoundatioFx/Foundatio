using System;
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

            return new JobRunner(loggerFactory).RunInConsole(new JobRunOptions {
                JobType = typeof(HelloWorldJob),
                Interval = TimeSpan.Zero,
                RunContinuous = true
            }, serviceProvider => {
                ((Container)serviceProvider).RegisterSingleton<ILoggerFactory>(loggerFactory);
                ((Container)serviceProvider).Register(typeof(ILogger<>), typeof(Logger<>));
            });
        }
    }
}
