using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Foundatio.Jobs.Hosting {
    /// <summary>
    /// Listens for Ctrl+C or SIGTERM or for all hosted jobs to stop running and initiates shutdown.
    /// </summary>
    public class JobHostLifetime : IHostLifetime, IDisposable {
        private readonly ManualResetEvent _shutdownBlock = new ManualResetEvent(false);
        private Timer _timer;
        private readonly List<IJobStatus> _jobs = new List<IJobStatus>();
        private readonly JobHostLifetimeOptions _options;
        private readonly IHostingEnvironment _environment;
        private readonly IApplicationLifetime _lifetime;
        private readonly ILogger _logger;
        private readonly bool _useConsoleOutput;

        public JobHostLifetime(IOptions<JobHostLifetimeOptions> options, IHostingEnvironment environment, IApplicationLifetime applicationLifetime, ILogger<JobHostLifetime> logger) {
            _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
            _environment = environment ?? throw new ArgumentNullException(nameof(environment));
            _lifetime = applicationLifetime ?? throw new ArgumentNullException(nameof(applicationLifetime));
            _logger = logger ?? NullLogger<JobHostLifetime>.Instance;
            if (logger != NullLogger<JobHostLifetime>.Instance)
                _useConsoleOutput = false;
        }

        public Task WaitForStartAsync(CancellationToken cancellationToken) {
            _lifetime.ApplicationStarted.Register(() => {
                _timer = new Timer(e => CheckForShutdown(), null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(2));

                if (_useConsoleOutput) {
                    Console.WriteLine("Application started. Press Ctrl+C to shut down.");
                    Console.WriteLine($"Hosting environment: {_environment.EnvironmentName}");
                    Console.WriteLine($"Content root path: {_environment.ContentRootPath}");
                } else {
                    _logger.LogInformation("Application started. Press Ctrl+C to shut down.");
                    _logger.LogInformation($"Hosting environment: {_environment.EnvironmentName}");
                    _logger.LogInformation($"Content root path: {_environment.ContentRootPath}");
                }
            });
            AppDomain.CurrentDomain.ProcessExit += (sender, eventArgs) => {
                _lifetime.StopApplication();
                _shutdownBlock.WaitOne();
            };
            Console.CancelKeyPress += (sender, e) => {
                e.Cancel = true;
                _lifetime.StopApplication();
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
            int runningJobCount = _jobs.Count(s => s.IsRunning);
            if (runningJobCount == 0) {
                _timer?.Change(Timeout.Infinite, 0);
                
                if (_useConsoleOutput)
                    Console.WriteLine("Stopping host due to no running jobs.");
                else
                    _logger.LogInformation("Stopping host due to no running jobs.");

                _lifetime.StopApplication();
            }
        }

        public void Dispose() {
            _timer?.Dispose();
            _shutdownBlock.Set();
        }
    }
}