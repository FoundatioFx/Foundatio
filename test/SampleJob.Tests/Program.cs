using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Jobs;
using Foundatio.Logging;
using Foundatio.Logging.NLog;
using Foundatio.Extensions;
using Foundatio.ServiceProviders;
using Nito.AsyncEx.Synchronous;
using Topshelf;

namespace Foundatio.SampleJob {
    public class Program {
        public static int Main() {
            var loggerFactory = new LoggerFactory();
            loggerFactory.AddNLog();
            loggerFactory.DefaultLogLevel = LogLevel.Trace;

            //var serviceProvider = new SampleServiceProvider(loggerFactory);
            //var serviceProvider = ServiceProvider.GetServiceProvider("Foundatio.SampleJob.SampleServiceProvider,Foundatio.SampleJob", loggerFactory);
            var serviceProvider = ServiceProvider.FindAndGetServiceProvider(typeof(PingQueueJob), loggerFactory);
            var cancellationTokenSource = new CancellationTokenSource();
            Task runTask = null;

            var result = HostFactory.Run(config => {
                config.Service<JobRunner>(s => {
                    s.ConstructUsing(() => new JobRunner(() => serviceProvider.GetService<PingQueueJob>(), loggerFactory, instanceCount: 1));
                    s.WhenStarted((service, control) => {
                        cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationTokenSource.Token, service.GetShutdownCancellationToken());
                        runTask = service.RunAsync(cancellationTokenSource.Token);
                        return true;
                    });
                    s.WhenStopped((service, control) => {
                        cancellationTokenSource.Cancel();
                        runTask?.WaitWithoutException(new CancellationTokenSource(5000).Token);
                        return true;
                    });
                });
            });

            return (int)result;
        }
    }
}
