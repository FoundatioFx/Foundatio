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
                  JobFactory = () => instance,
                  InitialDelay = initialDelay,
                  InstanceCount = instanceCount,
                  IterationLimit = iterationLimit,
                  RunContinuous = runContinuous,
                  Interval = interval
              }, loggerFactory) {
            }

        public JobRunner(Func<IJob> jobFactory, ILoggerFactory loggerFactory = null, TimeSpan? initialDelay = null, int instanceCount = 1, bool runContinuous = true, int iterationLimit = -1, TimeSpan? interval = null)
            : this(new JobOptions {
                JobFactory = jobFactory,
                InitialDelay = initialDelay,
                InstanceCount = instanceCount,
                IterationLimit = iterationLimit,
                RunContinuous = runContinuous,
                Interval = interval
            }, loggerFactory) {}

        public CancellationTokenSource CancellationTokenSource { get; private set; }

        public int RunInConsole() {
            int result;
            try {
                CancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(GetShutdownCancellationToken(_logger));
                var success = RunAsync(CancellationTokenSource.Token).GetAwaiter().GetResult();
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
            if (_options.JobFactory == null) {
                _logger.Error("JobFactory must be specified.");
                return false;
            }

            var job = _options.JobFactory();
            _jobName = TypeHelper.GetTypeDisplayName(job.GetType());

            if (_options.InitialDelay.HasValue && _options.InitialDelay.Value > TimeSpan.Zero)
                await SystemClock.SleepAsync(_options.InitialDelay.Value, cancellationToken).AnyContext();

            if (_options.RunContinuous && _options.InstanceCount > 1) {
                var tasks = new List<Task>();
                for (int i = 0; i < _options.InstanceCount; i++) {
                    var task = new Task(() => {
                        try {
                            var jobInstance = _options.JobFactory();
                            jobInstance.RunContinuousAsync(_options.Interval, _options.IterationLimit, cancellationToken).GetAwaiter().GetResult();
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
                await job.RunContinuousAsync(_options.Interval, _options.IterationLimit, cancellationToken).AnyContext();
            } else {
                using (_logger.BeginScope(s => s.Property("job", _jobName))) {
                    _logger.Trace("Job run \"{0}\" starting...", _jobName);
                    var result = await job.TryRunAsync(cancellationToken).AnyContext();
                    JobExtensions.LogResult(result, _logger, _jobName);

                    return result.IsSuccess;
                }
            }

            return true;
        }

        private static CancellationTokenSource _jobShutdownCancellationTokenSource;
        private static readonly object _lock = new object();
        public static CancellationToken GetShutdownCancellationToken(ILogger logger = null) {
            if (_jobShutdownCancellationTokenSource != null)
                return _jobShutdownCancellationTokenSource.Token;

            lock (_lock) {
                if (_jobShutdownCancellationTokenSource != null)
                    return _jobShutdownCancellationTokenSource.Token;

                _jobShutdownCancellationTokenSource = new CancellationTokenSource();
                ShutdownEventCatcher.Shutdown += args => {
                    _jobShutdownCancellationTokenSource.Cancel();
                    logger?.Info("Job shutdown event signaled: {0}", args.Reason);
                };

                var webJobsShutdownFile = Environment.GetEnvironmentVariable("WEBJOBS_SHUTDOWN_FILE");
                if (String.IsNullOrEmpty(webJobsShutdownFile))
                    return _jobShutdownCancellationTokenSource.Token;

                var handler = new FileSystemEventHandler((s, e) => {
                    if (e.FullPath.IndexOf(Path.GetFileName(webJobsShutdownFile), StringComparison.OrdinalIgnoreCase) < 0)
                        return;

                    _jobShutdownCancellationTokenSource.Cancel();
                    logger?.Info("Job shutdown signaled.");
                });

                var watcher = new FileSystemWatcher(Path.GetDirectoryName(webJobsShutdownFile));
                watcher.Created += handler;
                watcher.Changed += handler;
                watcher.NotifyFilter = NotifyFilters.CreationTime | NotifyFilters.FileName | NotifyFilters.LastWrite;
                watcher.IncludeSubdirectories = false;
                watcher.EnableRaisingEvents = true;

                return _jobShutdownCancellationTokenSource.Token;
            }
        }
    }
}
