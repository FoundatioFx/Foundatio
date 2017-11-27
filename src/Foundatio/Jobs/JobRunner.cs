using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Jobs {
    public class JobRunner {
        private readonly ILogger _logger;
        private string _jobName;
        private readonly JobOptions _options;

        public JobRunner(JobOptions options, ILoggerFactory loggerFactory = null) {
            _logger = loggerFactory?.CreateLogger<JobRunner>() ?? NullLogger<JobRunner>.Instance;
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
                bool success = RunAsync(CancellationTokenSource.Token).GetAwaiter().GetResult();
                result = success ? 0 : -1;

                if (Debugger.IsAttached)
                    Console.ReadKey();
            } catch (TaskCanceledException) {
                return 0;
            } catch (FileNotFoundException e) {
                if (_logger.IsEnabled(LogLevel.Error))
                    _logger.LogError("{Message} ({FileName})", e.GetMessage(), e.FileName);

                if (Debugger.IsAttached)
                    Console.ReadKey();

                return 1;
            } catch (Exception e) {
                if (_logger.IsEnabled(LogLevel.Error))
                    _logger.LogError(e, "Job {JobName} error: {Message}", _jobName, e.GetMessage());

                if (Debugger.IsAttached)
                    Console.ReadKey();

                return 1;
            }

            return result;
        }

        public void RunInBackground(CancellationToken cancellationToken = default(CancellationToken)) {
            if (_options.InstanceCount == 1) {
                new Task(async () => {
                    try {
                        await RunAsync(cancellationToken).AnyContext();
                    } catch (TaskCanceledException) {
                    } catch (Exception ex) {
                        if (_logger.IsEnabled(LogLevel.Error))
                            _logger.LogError(ex, "Error running job in background: {Message}", ex.Message);
                        throw;
                    }
                }, cancellationToken, TaskCreationOptions.LongRunning).TryStart();
            } else {
                var ignored = RunAsync(cancellationToken);
            }
        }

        public async Task<bool> RunAsync(CancellationToken cancellationToken = default(CancellationToken)) {
            if (_options.JobFactory == null) {
                _logger.LogError("JobFactory must be specified.");
                return false;
            }

            var job = _options.JobFactory();
            if (job == null) {
                _logger.LogError("JobFactory returned null job instance.");
                return false;
            }

            _jobName = TypeHelper.GetTypeDisplayName(job.GetType());
            using (_logger.BeginScope(new Dictionary<string, object> {{ "job", _jobName }})) {
                if (_logger.IsEnabled(LogLevel.Information))
                    _logger.LogInformation("Starting job type {JobName} on machine {MachineName}...", _jobName, Environment.MachineName);

                if (_options.InitialDelay.HasValue && _options.InitialDelay.Value > TimeSpan.Zero)
                    await SystemClock.SleepAsync(_options.InitialDelay.Value, cancellationToken).AnyContext();

                if (_options.RunContinuous && _options.InstanceCount > 1) {
                    var tasks = new List<Task>();
                    for (int i = 0; i < _options.InstanceCount; i++) {
                        var task = new Task(() => {
                            try {
                                var jobInstance = _options.JobFactory();
                                jobInstance.RunContinuous(_options.Interval, _options.IterationLimit, cancellationToken);
                            } catch (TaskCanceledException) {
                            } catch (Exception ex) {
                                if (_logger.IsEnabled(LogLevel.Error))
                                    _logger.LogError(ex, "Error running job instance: {Message}", ex.Message);
                                throw;
                            }
                        }, cancellationToken, TaskCreationOptions.LongRunning);
                        tasks.Add(task);
                        task.TryStart();
                    }

                    await Task.WhenAll(tasks).AnyContext();
                } else if (_options.RunContinuous && _options.InstanceCount == 1) {
                    job.RunContinuous(_options.Interval, _options.IterationLimit, cancellationToken);
                } else {
                    var result = job.TryRun(cancellationToken);
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
                Console.CancelKeyPress += (sender, args) => {
                    _jobShutdownCancellationTokenSource.Cancel();
                    if (logger != null & logger.IsEnabled(LogLevel.Information))
                        logger.LogInformation("Job shutdown event signaled: {SpecialKey}", args.SpecialKey);
                    args.Cancel = true;
                };

                string webJobsShutdownFile = Environment.GetEnvironmentVariable("WEBJOBS_SHUTDOWN_FILE");
                if (String.IsNullOrEmpty(webJobsShutdownFile))
                    return _jobShutdownCancellationTokenSource.Token;

                var handler = new FileSystemEventHandler((s, e) => {
                    if (e.FullPath.IndexOf(Path.GetFileName(webJobsShutdownFile), StringComparison.OrdinalIgnoreCase) < 0)
                        return;

                    _jobShutdownCancellationTokenSource.Cancel();
                    logger?.LogInformation("Job shutdown signaled.");
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
