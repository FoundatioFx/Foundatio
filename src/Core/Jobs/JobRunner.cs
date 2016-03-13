using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Logging;
using Foundatio.Utility;

namespace Foundatio.Jobs {
    public class JobRunner {
        private readonly ILogger _logger;
        private string _jobName;
        private readonly JobOptions _options;

        public JobRunner(JobOptions options, ILoggerFactory loggerFactory = null) {
            _logger = loggerFactory.CreateLogger<JobRunner>();
            _options = options;
        }

        public JobRunner(IJob instance, ILoggerFactory loggerFactory = null, TimeSpan? initialDelay = null, int instanceCount = 1, bool runContinuous = true, int iterationLimit = -1, TimeSpan? interval = null)
            : this(new JobOptions {
                Job = instance,
                InitialDelay = initialDelay,
                InstanceCount = instanceCount,
                IterationLimit = iterationLimit,
                RunContinuous = runContinuous,
                Interval = interval
            }, loggerFactory) {}

        public int RunInConsole() {
            int result;
            try {
                WatchForShutdown();
                var success = RunAsync(_cancellationTokenSource.Token).GetAwaiter().GetResult();
                result = success ? 0 : -1;

                if (Debugger.IsAttached)
                    Console.ReadKey();
            } catch (FileNotFoundException e) {
                _logger.Error(() => $"{e.GetMessage()} ({ e.FileName})");

                if (Debugger.IsAttached)
                    Console.ReadKey();

                return 1;
            } catch (Exception e) {
                _logger.Error(e, "Job \"{jobName}\" error: {Message}", _jobName, e.GetMessage());

                if (Debugger.IsAttached)
                    Console.ReadKey();

                return 1;
            }

            return result;
        }

        public void RunInBackground(CancellationToken cancellationToken = default(CancellationToken)) {
            if (_options.InstanceCount == 1) {
                new Task(() => {
                    try {
                        RunAsync(cancellationToken).GetAwaiter().GetResult();
                    } catch (Exception ex) {
                        _logger.Error(ex, () => $"Error running job in background: {ex.Message}");
                        throw;
                    }
                }, cancellationToken, TaskCreationOptions.LongRunning).Start();
            } else {
                var ignored = RunAsync(cancellationToken);
            }
        }

        public async Task<bool> RunAsync(CancellationToken cancellationToken = default(CancellationToken)) {
            if (_options.Job == null) {
                _logger.Error("Job must be specified.");
                return false;
            }
            
            _jobName = TypeHelper.GetTypeDisplayName(_options.Job.GetType());

            if (_options.InitialDelay.HasValue && _options.InitialDelay.Value > TimeSpan.Zero)
                await Task.Delay(_options.InitialDelay.Value, cancellationToken).AnyContext();

            if (_options.RunContinuous && _options.InstanceCount > 1) {
                var tasks = new List<Task>();
                for (int i = 0; i < _options.InstanceCount; i++) {
                    var task = new Task(() => {
                        try {
                            _options.Job.RunContinuousAsync(_options.Interval, _options.IterationLimit, cancellationToken).GetAwaiter().GetResult();
                        } catch (Exception ex) {
                            _logger.Error(ex, () => $"Error running job instance: {ex.Message}");
                            throw;
                        }
                    }, cancellationToken, TaskCreationOptions.LongRunning);
                    tasks.Add(task);
                    task.Start();
                }

                await Task.WhenAll(tasks).AnyContext();
            } else if (_options.RunContinuous && _options.InstanceCount == 1) {
                await _options.Job.RunContinuousAsync(_options.Interval, _options.IterationLimit, cancellationToken).AnyContext();
            } else {
                using (_logger.BeginScope(s => s.Property("job", _jobName))) {
                    _logger.Trace("Job run \"{0}\" starting...", _jobName);
                    var result = await _options.Job.TryRunAsync(cancellationToken).AnyContext();
                    JobExtensions.LogResult(result, _logger, _jobName);

                    return result.IsSuccess;
                }
            }

            return true;
        }

        private string _webJobsShutdownFile;
        private readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();

        private void WatchForShutdown() {
            ShutdownEventCatcher.Shutdown += args => {
                _cancellationTokenSource.Cancel();
                _logger.Info("Job shutdown event signaled: {0}", args.Reason);
            };

            _webJobsShutdownFile = Environment.GetEnvironmentVariable("WEBJOBS_SHUTDOWN_FILE");
            if (String.IsNullOrEmpty(_webJobsShutdownFile))
                return;

            var watcher = new FileSystemWatcher(Path.GetDirectoryName(_webJobsShutdownFile));
            watcher.Created += OnFileChanged;
            watcher.Changed += OnFileChanged;
            watcher.NotifyFilter = NotifyFilters.CreationTime | NotifyFilters.FileName | NotifyFilters.LastWrite;
            watcher.IncludeSubdirectories = false;
            watcher.EnableRaisingEvents = true;
        }

        private void OnFileChanged(object sender, FileSystemEventArgs e) {
            if (e.FullPath.IndexOf(Path.GetFileName(_webJobsShutdownFile), StringComparison.OrdinalIgnoreCase) >= 0) {
                _cancellationTokenSource.Cancel();
                _logger.Info("Job shutdown signaled.");
            }
        }
    }
}
