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
        private readonly bool _waitForStartupActions;

        public HostedJobService(IServiceProvider serviceProvider, bool waitForStartupActions, ILoggerFactory loggerFactory) {
            _serviceProvider = serviceProvider;
            _loggerFactory = loggerFactory; 
            _logger = loggerFactory.CreateLogger<T>();
            var lifetime = serviceProvider.GetService<JobHostLifetime>();
            if (lifetime == null)
                throw new InvalidOperationException("You must call UseJobLifetime when registering jobs.");

            lifetime.RegisterHostedJobInstance(this);
            _waitForStartupActions = waitForStartupActions;
        }

        private async Task ExecuteAsync(CancellationToken stoppingToken) {
            if (_waitForStartupActions) {
                var startupContext = _serviceProvider.GetRequiredService<StartupContext>();
                bool success = await startupContext.WaitForStartupAsync(stoppingToken).ConfigureAwait(false);
                if (!success)
                    throw new ApplicationException("Failed to wait for startup actions to complete.");
            }

            var jobOptions = JobOptions.GetDefaults<T>(() => _serviceProvider.GetRequiredService<T>());
            var runner = new JobRunner(jobOptions, _loggerFactory);

            try {
                await runner.RunAsync(stoppingToken);
                _stoppingCts.Cancel();
            } finally {
                _logger.LogInformation("JobDone, calling token cancel.");
            }
        }

        public Task StartAsync(CancellationToken cancellationToken) {
            _executingTask = ExecuteAsync(_stoppingCts.Token);
            if (_executingTask.IsCompleted)
                return _executingTask;

            return Task.CompletedTask;
        }

        public async Task StopAsync(CancellationToken cancellationToken) {
            if (_executingTask == null)
                return;

            try {
                _stoppingCts.Cancel();
            } finally {
                var task = await Task.WhenAny(_executingTask, Task.Delay(-1, cancellationToken));
            }
        }

        public void Dispose() {
            _stoppingCts.Cancel();
        }

        public bool IsRunning => _executingTask != null && !_executingTask.IsCompleted;
    }

    public interface IJobStatus {
        bool IsRunning { get; }
    }
}
