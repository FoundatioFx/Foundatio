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
    public class JobRunner<TJob, TServiceProvider> : JobRunner {
        public JobRunner(ILoggerFactory loggerFactory = null) : base(new JobOptions { JobType = typeof(TJob), ServiceProviderType = typeof(TServiceProvider) }, loggerFactory) { }
    }

    public class JobRunner<TJob> : JobRunner {
        public JobRunner(ILoggerFactory loggerFactory = null) : base(new JobOptions { JobType = typeof(TJob) }, loggerFactory) {}
        public JobRunner(string serviceProviderType, ILoggerFactory loggerFactory = null) : base(new JobOptions { JobType = typeof(TJob), ServiceProviderTypeName = serviceProviderType }, loggerFactory) { }
    }

    public class JobRunner {
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger _logger;
        private readonly string _jobName;

        public JobRunner(JobOptions options, ILoggerFactory loggerFactory = null) {
            _loggerFactory = loggerFactory;
            Options = options;
            ResolveTypes();
            _logger = loggerFactory.CreateLogger(Options.JobType ?? GetType());
            _jobName = TypeHelper.GetTypeDisplayName(Options.JobType);
        }

        public JobRunner(IJob instance, ILoggerFactory loggerFactory = null) {
            _loggerFactory = loggerFactory;
            Options = new JobOptions { JobInstance = instance };
            _logger = loggerFactory.CreateLogger(instance.GetType());
            _jobName = TypeHelper.GetTypeDisplayName(instance.GetType());
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
                result = Run();

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

        public int Run() {
            return RunAsync().GetAwaiter().GetResult();
        }

        public async Task<int> RunAsync(CancellationToken cancellationToken = default(CancellationToken)) {
            if (Options.JobType == null)
                return -1;

            if (Options.InitialDelay.HasValue && Options.InitialDelay.Value > TimeSpan.Zero)
                await Task.Delay(Options.InitialDelay.Value, cancellationToken).AnyContext();

            WatchForShutdown();
            var linkedCancellationToken = CancellationTokenSource.CreateLinkedTokenSource(_cancellationTokenSource.Token, cancellationToken).Token;
            var job = GetJobInstance();
            if (Options.RunContinuous) {
                var tasks = new List<Task>();
                for (int i = 0; i < Options.InstanceCount; i++)
                    tasks.Add(Task.Run(async () => await GetJobInstance().RunContinuousAsync(Options.Interval, Options.IterationLimit, cancellationToken).AnyContext(), cancellationToken));

                await Task.WhenAll(tasks).AnyContext();
            } else {
                using (_logger.BeginScope(s => s.Property("job", _jobName))) {
                    _logger.Trace("Job run \"{0}\" starting...", _jobName);
                    var result = await job.TryRunAsync(linkedCancellationToken).AnyContext();
                    JobExtensions.LogResult(result, _logger, _jobName);

                    return result.IsSuccess ? 0 : -1;
                }
            }

            return 0;
        }

        private void ResolveTypes() {
            if (Options.JobType == null && !String.IsNullOrEmpty(Options.JobTypeName))
                Options.JobType = TypeHelper.ResolveType(Options.JobTypeName, typeof(JobBase), _logger);

            if (Options.ServiceProviderType == null && !String.IsNullOrEmpty(Options.ServiceProviderTypeName))
                Options.ServiceProviderType = TypeHelper.ResolveType(Options.ServiceProviderTypeName, typeof(IServiceProvider), _logger);
        }

        public IJob GetJobInstance() {
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
