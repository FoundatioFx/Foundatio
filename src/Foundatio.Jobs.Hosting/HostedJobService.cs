using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Foundatio.Jobs.Hosting {
    public class HostedJobService<T> : IHostedService, IJobStatus, IDisposable where T : class, IJob {
        private readonly CancellationTokenSource _stoppingCts = new CancellationTokenSource();
        private Task _executingTask;
        private readonly IServiceProvider _serviceProvider;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger _logger;

        public HostedJobService(IServiceProvider serviceProvider, ILoggerFactory loggerFactory, IHostLifetime lifetime) {
            _serviceProvider = serviceProvider;
            _loggerFactory = loggerFactory;
            _logger = loggerFactory.CreateLogger<T>();
            var hostedJobServiceLifetime = lifetime as JobHostLifetime;
            hostedJobServiceLifetime?.RegisterHostedJobInstance(this);
        }

        private Task ExecuteAsync(CancellationToken stoppingToken) {
            var jobOptions = JobOptions.GetDefaults<T>(() => _serviceProvider.GetRequiredService<T>());
            var runner = new JobRunner(jobOptions, _loggerFactory);
            var jobTask = runner.RunAsync(stoppingToken);
            return jobTask.ContinueWith(t => {
                try {
                    _stoppingCts.Cancel();
                } finally {
                    _logger.LogInformation("JobDone, calling token cancel.");
                }
            }, stoppingToken);
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
