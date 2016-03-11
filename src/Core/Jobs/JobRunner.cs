using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Logging;
using Foundatio.ServiceProviders;
using Foundatio.Utility;

namespace Foundatio.Jobs {
    public class JobRunner<TJob, TServiceProvider> : JobRunner where TJob : IJob where TServiceProvider : IServiceProvider {
        public JobRunner(ILoggerFactory loggerFactory = null, TimeSpan? initialDelay = null, int instanceCount = 1, bool runContinuous = true, int iterationLimit = -1, TimeSpan? interval = null)
            : base(new JobOptions {
                JobType = typeof(TJob),
                ServiceProviderType = typeof(TServiceProvider),
                InitialDelay = initialDelay,
                InstanceCount = instanceCount,
                RunContinuous = runContinuous,
                IterationLimit = iterationLimit,
                Interval = interval
            }, loggerFactory) { }
    }

    public class JobRunner<TJob> : JobRunner where TJob: IJob {
        public JobRunner(ILoggerFactory loggerFactory = null, TimeSpan ? initialDelay = null, int instanceCount = 1, bool runContinuous = true, int iterationLimit = -1, TimeSpan? interval = null)
            : base(new JobOptions {
                JobType = typeof(TJob),
                InitialDelay = initialDelay,
                InstanceCount = instanceCount,
                RunContinuous = runContinuous,
                IterationLimit = iterationLimit,
                Interval = interval
            }, loggerFactory) {}

        public JobRunner(string serviceProviderType = null, ILoggerFactory loggerFactory = null, TimeSpan? initialDelay = null, int instanceCount = 1, bool runContinuous = true, int iterationLimit = -1, TimeSpan? interval = null)
            : base(new JobOptions {
                JobType = typeof(TJob),
                ServiceProviderTypeName = serviceProviderType,
                InitialDelay = initialDelay,
                InstanceCount = instanceCount,
                RunContinuous = runContinuous,
                IterationLimit = iterationLimit,
                Interval = interval
            }, loggerFactory) { }
    }

    public class JobRunner {
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger _logger;
        private string _jobName;

        public JobRunner(JobOptions options, ILoggerFactory loggerFactory = null) {
            _loggerFactory = loggerFactory;
            _logger = loggerFactory.CreateLogger<JobRunner>();
            Options = options;
        }

        public JobRunner(IJob instance, ILoggerFactory loggerFactory = null, TimeSpan? initialDelay = null, int instanceCount = 1, bool runContinuous = true, int iterationLimit = -1, TimeSpan? interval = null) {
            _loggerFactory = loggerFactory;
            _logger = loggerFactory.CreateLogger<JobRunner>();
            Options = new JobOptions {
                JobInstance = instance,
                InitialDelay = initialDelay,
                InstanceCount = instanceCount,
                RunContinuous = runContinuous,
                IterationLimit = iterationLimit,
                Interval = interval
            };
        }

        public JobRunner(string jobType, ILoggerFactory loggerFactory = null)
            : this(new JobOptions { JobTypeName = jobType }, loggerFactory) {
        }

        public JobRunner(string jobType, string serviceProviderType, ILoggerFactory loggerFactory = null)
            : this(new JobOptions { JobTypeName = jobType, ServiceProviderTypeName = serviceProviderType }, loggerFactory) {
        }
        
        public JobOptions Options { get; }

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
            if (Options.InstanceCount == 1)
                new Task(() => {
                    try {
                        RunAsync(cancellationToken).GetAwaiter().GetResult();
                    } catch (Exception ex) {
                        _logger.Error(ex, () => $"Error running job in background: {ex.Message}");
                        throw;
                    }
                }, cancellationToken, TaskCreationOptions.LongRunning).Start();
            else
#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
                RunAsync(cancellationToken);
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
        }

        public async Task<bool> RunAsync(CancellationToken cancellationToken = default(CancellationToken)) {
            if (Options.JobType == null && Options.JobInstance == null) {
                _logger.Error("JobType or JobInstance must be specified.");
                return false;
            }
            
            ResolveTypes();
            _jobName = TypeHelper.GetTypeDisplayName(Options.JobType);

            if (Options.InitialDelay.HasValue && Options.InitialDelay.Value > TimeSpan.Zero)
                await Task.Delay(Options.InitialDelay.Value, cancellationToken).AnyContext();

            if (Options.RunContinuous && Options.InstanceCount > 1) {
                var tasks = new List<Task>();
                for (int i = 0; i < Options.InstanceCount; i++) {
                    var task = new Task(() => {
                        try {
                            var job = GetJobInstance();
                            job.RunContinuousAsync(Options.Interval, Options.IterationLimit, cancellationToken).GetAwaiter().GetResult();
                        } catch (Exception ex) {
                            _logger.Error(ex, () => $"Error running job instance: {ex.Message}");
                            throw;
                        }
                    }, cancellationToken, TaskCreationOptions.LongRunning);
                    tasks.Add(task);
                    task.Start();
                }

                await Task.WhenAll(tasks).AnyContext();
            } else if (Options.RunContinuous && Options.InstanceCount == 1) {
                var job = GetJobInstance();
                await job.RunContinuousAsync(Options.Interval, Options.IterationLimit, cancellationToken).AnyContext();
            } else {
                using (_logger.BeginScope(s => s.Property("job", _jobName))) {
                    _logger.Trace("Job run \"{0}\" starting...", _jobName);
                    var job = GetJobInstance();
                    var result = await job.TryRunAsync(cancellationToken).AnyContext();
                    JobExtensions.LogResult(result, _logger, _jobName);

                    return result.IsSuccess;
                }
            }

            return true;
        }

        private void ResolveTypes() {
            if (Options.JobType == null && !String.IsNullOrEmpty(Options.JobTypeName))
                Options.JobType = TypeHelper.ResolveType(Options.JobTypeName, typeof(IJob), _logger);

            if (Options.JobType == null && Options.JobInstance != null)
                Options.JobType = Options.JobInstance.GetType();

            if (Options.ServiceProviderType == null && !String.IsNullOrEmpty(Options.ServiceProviderTypeName))
                Options.ServiceProviderType = TypeHelper.ResolveType(Options.ServiceProviderTypeName, typeof(IServiceProvider), _logger);
        }

        public IJob GetJobInstance() {
            ResolveTypes();

            if (Options.JobInstance != null)
                return Options.JobInstance;
            
            if (Options.JobType == null)
                return null;

            return CreateJobInstance(Options.JobType);
        }

        private IJob CreateJobInstance(Type jobType) {
            if (!typeof(IJob).IsAssignableFrom(jobType)) {
                _logger.Error("Job Type must derive from IJob.");
                return null;
            }

            if (!(Options.NoServiceProvider.HasValue && Options.NoServiceProvider.Value == false))
                ServiceProvider.SetServiceProvider(Options.ServiceProviderType ?? Options.JobType);

            // force bootstrap now so logging will be configured
            var bootstrappedServiceProvider = ServiceProvider.Current as IBootstrappedServiceProvider;
            if (bootstrappedServiceProvider != null) {
                bootstrappedServiceProvider.LoggerFactory = _loggerFactory;
                bootstrappedServiceProvider.Bootstrap();
            }

            var job = ServiceProvider.Current.GetService(jobType) as IJob;
            if (job == null) {
                _logger.Error("Unable to create job instance.");
                return null;
            }

            return job;
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
