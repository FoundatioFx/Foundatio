using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Foundatio.SampleJob {
    public class Program {
        public static int Main() {
            var loggerFactory = new LoggerFactory().AddConsole();

            var serviceProvider = SampleServiceProvider.Create(loggerFactory);
            return TopshelfJob.Run<PingQueueJob>(() => serviceProvider.GetService<PingQueueJob>(), loggerFactory: loggerFactory);
        }
    }
}