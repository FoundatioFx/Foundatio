using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.ServiceProviders;
using Foundatio.Utility;
using NLog.Fluent;

namespace Foundatio.Jobs {
    public class JobRunOptions {
        public JobRunOptions() {
            IterationLimit = -1;
            InstanceCount = 1;
            RunContinuous = true;
        }

        public string JobTypeName { get; set; }
        public Type JobType { get; set; }
        public string ServiceProviderTypeName { get; set; }
        public Type ServiceProviderType { get; set; }
        public bool RunContinuous { get; set; }
        public TimeSpan? Interval { get; set; }
        public int IterationLimit { get; set; }
        public int InstanceCount { get; set; }
    }

    public class JobRunner {
        public static JobResult Run<T>(CancellationToken cancellationToken = default(CancellationToken)) where T: JobBase {
            return CreateJobInstance(typeof(T)).Run(cancellationToken);
        }
        
        public static Task<JobResult> RunAsync(Type jobType, Type serviceProviderType = null, CancellationToken cancellationToken = default(CancellationToken)) {
            return CreateJobInstance(jobType, serviceProviderType).RunAsync(cancellationToken);
        }

        public static void RunUntilEmpty<T>(CancellationToken cancellationToken = default(CancellationToken)) where T: IQueueProcessorJob {
            var jobInstance = CreateJobInstance(typeof(T)) as IQueueProcessorJob;
            if (jobInstance == null)
                throw new ArgumentException("Type T must derive from IQueueProcessorJob.");

            jobInstance.RunUntilEmpty(cancellationToken);
        }

        public static Task RunContinuousAsync(Type jobType, Type serviceProviderType = null, TimeSpan? interval = null, int iterationLimit = -1, int instanceCount = 1, CancellationToken cancellationToken = default(CancellationToken)) {
            if (instanceCount > 1) {
                var tasks = new List<Task>();
                for (int i = 0; i < instanceCount; i++) {
                    tasks.Add(Task.Factory.StartNew(() => {
                        CreateJobInstance(jobType, serviceProviderType).RunContinuous(interval, iterationLimit, cancellationToken);
                    }, cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default));
                }

                return Task.WhenAll(tasks);
            }

            return Task.Factory.StartNew(() => CreateJobInstance(jobType, serviceProviderType).RunContinuous(interval, iterationLimit, cancellationToken), cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        public static Task RunContinuousAsync<T>(TimeSpan? interval = null, int iterationLimit = -1, int instanceCount = 1, CancellationToken cancellationToken = default(CancellationToken)) {
            return RunContinuousAsync(typeof(T), null, interval, iterationLimit, instanceCount, cancellationToken);
        }

        public static async Task<int> RunAsync(JobRunOptions options, CancellationToken cancellationToken = default(CancellationToken)) {
            ResolveJobTypes(options);
            if (options.JobType == null)
                return -1;

            Log.Info().Message("Starting {0}job type \"{1}\" on machine \"{2}\"...", options.RunContinuous ? "continuous " : String.Empty, options.JobType.Name, Environment.MachineName).Write();

            WatchForShutdown();
            var linkedToken = CancellationTokenSource.CreateLinkedTokenSource(_cancellationTokenSource.Token, cancellationToken).Token;
            if (options.RunContinuous)
                await RunContinuousAsync(options.JobType, options.ServiceProviderType, options.Interval,
                    options.IterationLimit, options.InstanceCount, linkedToken);
            else
                return (await RunAsync(options.JobType, options.ServiceProviderType, linkedToken)).IsSuccess ? 0 : -1;

            return 0;
        }

        public static void ResolveJobTypes(JobRunOptions options) {
            if (options.JobType == null)
                options.JobType = TypeHelper.ResolveType(options.JobTypeName, typeof(JobBase));

            if (options.ServiceProviderType == null
                && !String.IsNullOrEmpty(options.ServiceProviderTypeName))
                options.ServiceProviderType = TypeHelper.ResolveType(options.ServiceProviderTypeName, typeof(IServiceProvider));
        }

        public static JobBase CreateJobInstance(string jobTypeName, string serviceProviderTypeName = null) {
            var jobType = TypeHelper.ResolveType(jobTypeName, typeof(JobBase));
            if (jobType == null)
                return null;

            Type serviceProviderType = null;
            if (!String.IsNullOrEmpty(serviceProviderTypeName)) {
                serviceProviderType = TypeHelper.ResolveType(serviceProviderTypeName, typeof (IServiceProvider));
                if (serviceProviderType == null)
                    return null;
            }

            return CreateJobInstance(jobType, serviceProviderType);
        }

        public static JobBase CreateJobInstance(Type jobType, Type serviceProviderType = null) {
            if (!typeof(JobBase).IsAssignableFrom(jobType)) {
                Log.Error().Message("Job Type must derive from Job.").Write();
                return null;
            }

            ServiceProvider.SetServiceProvider(serviceProviderType);
            var job = ServiceProvider.Current.GetService(jobType) as JobBase;
            if (job == null) {
                Log.Error().Message("Unable to create job instance.").Write();
                return null;
            }

            return job;
        }

        private static string _webJobsShutdownFile;
        private static readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();

        private static void WatchForShutdown() {
            ShutdownEventCatcher.Shutdown += args => {
                _cancellationTokenSource.Cancel();
                Log.Info().Message("Job shutdown event signaled: {0}", args.Reason).Write();
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

        private static void OnFileChanged(object sender, FileSystemEventArgs e) {
            if (e.FullPath.IndexOf(Path.GetFileName(_webJobsShutdownFile), StringComparison.OrdinalIgnoreCase) >= 0) {
                _cancellationTokenSource.Cancel();
                Log.Info().Message("Job shutdown signaled.").Write();
            }
        }
    }
}
