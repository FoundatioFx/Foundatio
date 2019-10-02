using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions.Hosting.Startup;
using Foundatio.Utility;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Extensions.Hosting.Jobs {
    public class ShutdownHostIfNoJobsRunningService : IHostedService, IDisposable {
        private Timer _timer;
        private readonly List<IJobStatus> _jobs = new List<IJobStatus>();
        private readonly IHostApplicationLifetime _lifetime;
        private readonly IServiceProvider _serviceProvider;
        private bool _isStarted = false;
        private readonly ILogger _logger;

        public ShutdownHostIfNoJobsRunningService(IHostApplicationLifetime applicationLifetime, IServiceProvider serviceProvider, ILogger<ShutdownHostIfNoJobsRunningService> logger) {
            _lifetime = applicationLifetime ?? throw new ArgumentNullException(nameof(applicationLifetime));
            _serviceProvider = serviceProvider;
            _logger = logger ?? NullLogger<ShutdownHostIfNoJobsRunningService>.Instance;

            _lifetime.ApplicationStarted.Register(() => {
                _timer = new Timer(e => CheckForShutdown(), null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(2));
            });
        }

        public Task StartAsync(CancellationToken cancellationToken) {
            // if there are startup actions, don't allow shutdown to happen until after the startup actions have completed
            _ = Task.Run(async () => {
                var startupContext = _serviceProvider.GetService<StartupActionsContext>();
                if (startupContext != null)
                    await startupContext.WaitForStartupAsync(cancellationToken).AnyContext();

                _isStarted = true;
            }, cancellationToken);

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) {
            _timer?.Change(Timeout.Infinite, 0);
            return Task.CompletedTask;
        }

        public void RegisterHostedJobInstance(IJobStatus job) {
            _jobs.Add(job);
        }

        public void CheckForShutdown() {
            if (!_isStarted)
                return;
            
            int runningJobCount = _jobs.Count(s => s.IsRunning);
            if (runningJobCount == 0) {
                _timer?.Change(Timeout.Infinite, 0);

                _logger.LogInformation("Stopping host due to no running jobs.");

                _lifetime.StopApplication();
            }
        }

        public void Dispose() {
            _timer?.Dispose();
        }
    }
}
