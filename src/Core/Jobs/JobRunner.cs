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
    public class JobRunOptions {
        public string JobTypeName { get; set; }
        public Type JobType { get; set; }
        public string ServiceProviderTypeName { get; set; }
        public Type ServiceProviderType { get; set; }
        public bool RunContinuous { get; set; } = true;
        public TimeSpan? Interval { get; set; }
        public TimeSpan? InitialDelay { get; set; }
        public int IterationLimit { get; set; } = -1;
        public int InstanceCount { get; set; } = 1;
        public bool? NoServiceProvider { get; set; }
    }

    public class JobRunner {
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger _logger;

        public JobRunner(ILoggerFactory loggerFactory = null) {
            _loggerFactory = loggerFactory;
            _logger = loggerFactory?.CreateLogger<JobRunner>() ?? NullLogger.Instance;
        }

        public int RunInConsole<TJob>(Action<IServiceProvider> afterBootstrap = null) {
            return RunInConsole(new JobRunOptions { JobType = typeof(TJob) }, afterBootstrap);
        }

        public int RunInConsole<TJob, TServiceProvider>(Action<IServiceProvider> afterBootstrap = null) {
            return RunInConsole(new JobRunOptions { JobType = typeof(TJob), ServiceProviderType = typeof(TServiceProvider) }, afterBootstrap);
        }

        public int RunInConsole<TJob>(string serviceProviderType, Action<IServiceProvider> afterBootstrap = null) {
            return RunInConsole(new JobRunOptions { JobType = typeof(TJob), ServiceProviderTypeName = serviceProviderType }, afterBootstrap);
        }

        public int RunInConsole(JobRunOptions options, Action<IServiceProvider> afterBootstrap = null) {
            int result;
            string jobName = "N/A";
            try {
                ResolveTypes(options);
                if (options.JobType == null) {
                    Console.Error.WriteLine($"Unable to resolve job type {options.JobTypeName}.");
                    return -1;
                }

                jobName = options.JobType.Name;

                using (_logger.BeginScope(s => s.Property("Job", jobName))) {
                    if (!(options.NoServiceProvider.HasValue && options.NoServiceProvider.Value == false))
                        ServiceProvider.SetServiceProvider(options.ServiceProviderType ?? options.JobType);

                    IServiceProvider serviceProvider = ServiceProvider.Current;
                    // force bootstrap now so logging will be configured
                    var bootstrappedServiceProvider = ServiceProvider.Current as IBootstrappedServiceProvider;
                    if (bootstrappedServiceProvider != null) {
                        bootstrappedServiceProvider.LoggerFactory = _loggerFactory;
                        bootstrappedServiceProvider.Bootstrap();
                        serviceProvider = bootstrappedServiceProvider.ServiceProvider;
                    }

                    _logger.Info("Starting job...");

                    afterBootstrap?.Invoke(serviceProvider);

                    result = RunAsync(options).Result;

                    if (Debugger.IsAttached)
                        Console.ReadKey();
                }
            } catch (FileNotFoundException e) {
                Console.Error.WriteLine("{0} ({1})", e.GetMessage(), e.FileName);
                _logger.Error(() => $"{e.GetMessage()} ({ e.FileName})");

                if (Debugger.IsAttached)
                    Console.ReadKey();
                return 1;

            } catch (Exception e) {
                Console.Error.WriteLine(e.ToString());
                _logger.Error(e, "Job \"{jobName}\" error: {Message}", jobName, e.GetMessage());

                if (Debugger.IsAttached)
                    Console.ReadKey();

                return 1;
            }

            return result;

        }

        public Task RunUntilEmptyAsync<T>(CancellationToken cancellationToken = default(CancellationToken)) where T : IQueueProcessorJob {
            var jobInstance = CreateJobInstance(typeof(T)) as IQueueProcessorJob;
            if (jobInstance == null)
                throw new ArgumentException("Type T must derive from IQueueProcessorJob.");

            return jobInstance.RunUntilEmptyAsync(cancellationToken);
        }

        public async Task RunContinuousAsync(Type jobType, TimeSpan? interval = null, TimeSpan? initialDelay = null, int iterationLimit = -1, int instanceCount = 1, CancellationToken cancellationToken = default(CancellationToken)) {
            if (initialDelay.HasValue && initialDelay.Value > TimeSpan.Zero)
                await Task.Delay(initialDelay.Value, cancellationToken).AnyContext();

            var tasks = new List<Task>();
            for (int i = 0; i < instanceCount; i++)
                tasks.Add(Task.Run(async () => await CreateJobInstance(jobType).RunContinuousAsync(interval, iterationLimit, cancellationToken).AnyContext(), cancellationToken));

            await Task.WhenAll(tasks).AnyContext();
        }

        public Task RunContinuousAsync<T>(TimeSpan? interval = null, TimeSpan? initialDelay = null, int iterationLimit = -1, int instanceCount = 1, CancellationToken cancellationToken = default(CancellationToken)) {
            return RunContinuousAsync(typeof(T), interval, initialDelay, iterationLimit, instanceCount, cancellationToken);
        }

        public async Task<JobResult> RunAsync(Type jobType, TimeSpan? initialDelay = null, CancellationToken cancellationToken = default(CancellationToken)) {
            if (initialDelay.HasValue && initialDelay.Value > TimeSpan.Zero)
                await Task.Delay(initialDelay.Value, cancellationToken).AnyContext();

            var jobInstance = CreateJobInstance(jobType);
            if (jobInstance == null)
                return JobResult.FailedWithMessage("Could not create job instance: " + jobType);

            return await jobInstance.RunAsync(cancellationToken).AnyContext();
        }

        public async Task<int> RunAsync(JobRunOptions options, CancellationToken cancellationToken = default(CancellationToken)) {
            ResolveTypes(options);
            if (options.JobType == null)
                return -1;

            WatchForShutdown();
            var linkedCancellationToken = CancellationTokenSource.CreateLinkedTokenSource(_cancellationTokenSource.Token, cancellationToken).Token;
            if (options.RunContinuous)
                await RunContinuousAsync(options.JobType, options.Interval, options.InitialDelay, options.IterationLimit, options.InstanceCount, linkedCancellationToken).AnyContext();
            else {
                var result = await RunAsync(options.JobType, options.InitialDelay, linkedCancellationToken).AnyContext();
                return result.IsSuccess ? 0 : -1;
            }

            return 0;
        }

        public void ResolveTypes(JobRunOptions options) {
            if (options.JobType == null && !String.IsNullOrEmpty(options.JobTypeName))
                options.JobType = TypeHelper.ResolveType(options.JobTypeName, typeof(JobBase), _logger);

            if (options.ServiceProviderType == null && !String.IsNullOrEmpty(options.ServiceProviderTypeName))
                options.ServiceProviderType = TypeHelper.ResolveType(options.ServiceProviderTypeName, typeof(IServiceProvider), _logger);
        }

        public JobBase CreateJobInstance(string jobTypeName) {
            var jobType = TypeHelper.ResolveType(jobTypeName, typeof(JobBase), _logger);
            if (jobType == null)
                return null;

            return CreateJobInstance(jobType);
        }

        public JobBase CreateJobInstance(Type jobType) {
            if (!typeof(JobBase).IsAssignableFrom(jobType)) {
                _logger.Error("Job Type must derive from Job.");
                return null;
            }

            var job = ServiceProvider.Current.GetService(jobType) as JobBase;
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
