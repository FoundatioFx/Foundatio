using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Internal;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Hosting.Jobs {
    /// <summary>
    /// Listens for Ctrl+C or SIGTERM or for all hosted jobs to stop running and initiates shutdown.
    /// </summary>
    public class JobHostLifetime : Microsoft.Extensions.Hosting.IHostedService, IDisposable {
        private readonly ManualResetEvent _shutdownBlock = new ManualResetEvent(false);
        private Timer _timer;
        private readonly List<IJobStatus> _jobs = new List<IJobStatus>();
        private readonly IApplicationLifetime _lifetime;
        private readonly ILogger _logger;
        private readonly bool _useConsoleOutput;

        public JobHostLifetime(IHostingEnvironment environment, IApplicationLifetime applicationLifetime, IServiceProvider serviceProvider, ILogger<JobHostLifetime> logger) {
            if (environment == null)
                throw new ArgumentNullException(nameof(environment));
            _lifetime = applicationLifetime ?? throw new ArgumentNullException(nameof(applicationLifetime));
            _logger = logger ?? NullLogger<JobHostLifetime>.Instance;
            if (logger != NullLogger<JobHostLifetime>.Instance)
                _useConsoleOutput = false;
            
            _lifetime.ApplicationStarted.Register(() => {
                _timer = new Timer(e => CheckForShutdown(), null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(2));

                var addresses = serviceProvider.GetService<IServerAddressesFeature>()?.Addresses;
                if (_useConsoleOutput) {
                    Console.WriteLine("Application started. Press Ctrl+C to shut down.");
                    Console.WriteLine($"Hosting environment: {environment.EnvironmentName}");
                    Console.WriteLine($"Content root path: {environment.ContentRootPath}");
                    if (addresses != null) {
                        foreach (string address in addresses)
                            Console.WriteLine($"Now listening on: {address}");
                    }
                } else {
                    _logger.LogInformation("Application started. Press Ctrl+C to shut down.");
                    _logger.LogInformation($"Hosting environment: {environment.EnvironmentName}");
                    _logger.LogInformation($"Content root path: {environment.ContentRootPath}");
                    if (addresses != null) {
                        foreach (string address in addresses)
                            _logger.LogInformation("Now listening on: {Address}", address);
                    }
                }
            });
        }

        public Task StartAsync(CancellationToken cancellationToken) {
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