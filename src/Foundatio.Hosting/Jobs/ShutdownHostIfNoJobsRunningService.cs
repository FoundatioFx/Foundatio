using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Hosting.Jobs {
    public class ShutdownHostIfNoJobsRunningService : IHostedService, IDisposable {
        private Timer _timer;
        private readonly List<IJobStatus> _jobs = new List<IJobStatus>();
        private readonly IApplicationLifetime _lifetime;
        private readonly ILogger _logger;

        public ShutdownHostIfNoJobsRunningService(IApplicationLifetime applicationLifetime, ILogger<ShutdownHostIfNoJobsRunningService> logger) {
            _lifetime = applicationLifetime ?? throw new ArgumentNullException(nameof(applicationLifetime));
            _logger = logger ?? NullLogger<ShutdownHostIfNoJobsRunningService>.Instance;

            _lifetime.ApplicationStarted.Register(() => {
                _timer = new Timer(e => CheckForShutdown(), null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(2));
            });
        }

        public Task StartAsync(CancellationToken cancellationToken) {
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
