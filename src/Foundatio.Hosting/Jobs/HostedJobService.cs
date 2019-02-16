using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Foundatio.Jobs;
using Foundatio.Hosting.Startup;

namespace Foundatio.Hosting.Jobs {
    public class HostedJobService<T> : IHostedService, IJobStatus, IDisposable where T : class, IJob {
        private readonly CancellationTokenSource _stoppingCts = new CancellationTokenSource();
        private Task _executingTask;
        private readonly IServiceProvider _serviceProvider;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger _logger;
        private readonly HostedJobOptions _jobOptions;
        private bool _hasStarted = false;

        public HostedJobService(IServiceProvider serviceProvider, HostedJobOptions jobOptions, ILoggerFactory loggerFactory) {
            _serviceProvider = serviceProvider;
            _loggerFactory = loggerFactory; 
            _logger = loggerFactory.CreateLogger<T>();
            var lifetime = serviceProvider.GetService<ShutdownHostIfNoJobsRunningService>();
            if (lifetime == null)
                throw new InvalidOperationException("You must call UseJobLifetime when registering jobs.");

            lifetime.RegisterHostedJobInstance(this);
            _jobOptions = jobOptions;
        }

        private async Task ExecuteAsync(CancellationToken stoppingToken) {
            if (_jobOptions.WaitForStartupActions) {
                var startupContext = _serviceProvider.GetRequiredService<StartupContext>();
                bool success = await startupContext.WaitForStartupAsync(stoppingToken).ConfigureAwait(false);
                if (!success)
                    throw new ApplicationException("Failed to wait for startup actions to complete.");
            }

            var runner = new JobRunner(_jobOptions, _loggerFactory);

            try {
                await runner.RunAsync(stoppingToken);
                _stoppingCts.Cancel();
            } finally {
                _logger.LogInformation("JobDone, calling token cancel.");
            }
        }

        public Task StartAsync(CancellationToken cancellationToken) {
            _executingTask = ExecuteAsync(_stoppingCts.Token);
            _hasStarted = true;
            return _executingTask.IsCompleted ? _executingTask : Task.CompletedTask;
        }

        public async Task StopAsync(CancellationToken cancellationToken) {
            if (_executingTask == null)
                return;

            try {
                _stoppingCts.Cancel();
            } finally {
                await Task.WhenAny(_executingTask, Task.Delay(-1, cancellationToken));
            }
        }

        public void Dispose() {
            _stoppingCts.Cancel();
        }

        public bool IsRunning => _hasStarted == false || (_executingTask != null && !_executingTask.IsCompleted);
    }

    public interface IJobStatus {
        bool IsRunning { get; }
    }
}
