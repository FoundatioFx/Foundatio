using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.ServiceProviders;
using Foundatio.Utility;
using Foundatio.Logging;

namespace Foundatio.Jobs {
    public class JobRunOptions {
        public JobRunOptions() {
            IterationLimit = -1;
            InstanceCount = 1;
            RunContinuous = true;
        }

        public string JobTypeName { get; set; }
        public Type JobType { get; set; }
        public bool RunContinuous { get; set; }
        public TimeSpan? Interval { get; set; }
        public int IterationLimit { get; set; }
        public int InstanceCount { get; set; }
    }

    public class JobRunner {
        public static JobResult Run<T>(CancellationToken cancellationToken = default(CancellationToken)) where T: JobBase {
            return CreateJobInstance(typeof(T)).Run(cancellationToken);
        }
        
        public static Task<JobResult> RunAsync(Type jobType, CancellationToken cancellationToken = default(CancellationToken)) {
            return CreateJobInstance(jobType).RunAsync(cancellationToken);
        }

        public static void RunUntilEmpty<T>(CancellationToken cancellationToken = default(CancellationToken)) where T: IQueueProcessorJob {
            var jobInstance = CreateJobInstance(typeof(T)) as IQueueProcessorJob;
            if (jobInstance == null)
                throw new ArgumentException("Type T must derive from IQueueProcessorJob.");

            jobInstance.RunUntilEmpty(cancellationToken);
        }

        public static Task RunContinuousAsync(Type jobType, TimeSpan? interval = null, int iterationLimit = -1, int instanceCount = 1, CancellationToken cancellationToken = default(CancellationToken)) {
            if (instanceCount > 1) {
                var tasks = new List<Task>();
                for (int i = 0; i < instanceCount; i++) {
                    tasks.Add(Task.Factory.StartNew(() => {
                        CreateJobInstance(jobType).RunContinuous(interval, iterationLimit, cancellationToken);
                    }, cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default));
                }

                return Task.WhenAll(tasks);
            }

            return Task.Factory.StartNew(() => CreateJobInstance(jobType).RunContinuous(interval, iterationLimit, cancellationToken), cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        public static async Task<int> RunAsync(JobRunOptions options, CancellationToken cancellationToken = default(CancellationToken)) {
            ResolveJobType(options);
            if (options.JobType == null)
                return -1;

            Logger.Info().Message("Starting {0}job type \"{1}\" on machine \"{2}\"...", options.RunContinuous ? "continuous " : String.Empty, options.JobType.Name, Environment.MachineName).Write();

            WatchForShutdown();
            var linkedToken = CancellationTokenSource.CreateLinkedTokenSource(_cancellationTokenSource.Token, cancellationToken).Token;
            if (options.RunContinuous)
                await RunContinuousAsync(options.JobType, options.Interval,
                    options.IterationLimit, options.InstanceCount, linkedToken);
            else
                return (await RunAsync(options.JobType, linkedToken)).IsSuccess ? 0 : -1;

            return 0;
        }

        public static void ResolveJobType(JobRunOptions options) {
            if (options.JobType == null)
                options.JobType = TypeHelper.ResolveType(options.JobTypeName, typeof(JobBase));
        }

        public static JobBase CreateJobInstance(string jobTypeName) {
            var jobType = TypeHelper.ResolveType(jobTypeName, typeof(JobBase));
            if (jobType == null)
                return null;

            return CreateJobInstance(jobType);
        }

        public static JobBase CreateJobInstance(Type jobType) {
            if (!typeof(JobBase).IsAssignableFrom(jobType)) {
                Logger.Error().Message("Job Type must derive from Job.").Write();
                return null;
            }

            var job = ServiceProvider.Current.GetService(jobType) as JobBase;
            if (job == null) {
                Logger.Error().Message("Unable to create job instance.").Write();
                return null;
            }

            return job;
        }

        private static string _webJobsShutdownFile;
        private static readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();

        private static void WatchForShutdown() {
            ShutdownEventCatcher.Shutdown += args => {
                _cancellationTokenSource.Cancel();
                Logger.Info().Message("Job shutdown event signaled: {0}", args.Reason).Write();
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
                Logger.Info().Message("Job shutdown signaled.").Write();
            }
        }
    }
}
