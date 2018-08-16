using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Foundatio.Jobs.Hosting {
    /// <summary>
    /// Listens for Ctrl+C or SIGTERM or for all hosted jobs to stop running and initiates shutdown.
    /// </summary>
    public class JobHostLifetime : IHostLifetime, IDisposable {
        private readonly ManualResetEvent _shutdownBlock = new ManualResetEvent(false);
        private Timer _timer;
        private readonly List<IJobStatus> _jobs = new List<IJobStatus>();

        public JobHostLifetime(IOptions<JobHostLifetimeOptions> options, IHostingEnvironment environment,
            IApplicationLifetime applicationLifetime) {
            Options = options?.Value ?? throw new ArgumentNullException(nameof(options));
            Environment = environment ?? throw new ArgumentNullException(nameof(environment));
            ApplicationLifetime = applicationLifetime ?? throw new ArgumentNullException(nameof(applicationLifetime));
        }

        private JobHostLifetimeOptions Options { get; }

        private IHostingEnvironment Environment { get; }

        private IApplicationLifetime ApplicationLifetime { get; }

        public Task WaitForStartAsync(CancellationToken cancellationToken) {
            ApplicationLifetime.ApplicationStarted.Register(() => {
                _timer = new Timer(e => CheckForShutdown(), null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(2));
                if (!Options.SuppressStatusMessages) return;

                Console.WriteLine("Application started. Press Ctrl+C to shut down.");
                Console.WriteLine($"Hosting environment: {Environment.EnvironmentName}");
                Console.WriteLine($"Content root path: {Environment.ContentRootPath}");
            });
            AppDomain.CurrentDomain.ProcessExit += (sender, eventArgs) => {
                ApplicationLifetime.StopApplication();
                _shutdownBlock.WaitOne();
            };
            Console.CancelKeyPress += (sender, e) => {
                e.Cancel = true;
                ApplicationLifetime.StopApplication();
            };

            // Console applications start immediately.
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) {
            // There's nothing to do here
            _timer?.Change(Timeout.Infinite, 0);
            return Task.CompletedTask;
        }

        public void RegisterHostedJobInstance(IJobStatus job) {
            _jobs.Add(job);
        }

        public void CheckForShutdown() {
            var runningJobCount = _jobs.Count(s => s.IsRunning);
            if (runningJobCount == 0) {
                _timer?.Change(Timeout.Infinite, 0);
                Console.WriteLine("Stopping host due to no running jobs.");
                ApplicationLifetime.StopApplication();
            }
        }

        public void Dispose() {
            _timer?.Dispose();
            _shutdownBlock.Set();
        }
    }
}