using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.ServiceProviders;
using Foundatio.Utility;
using Foundatio.Logging;
using Foundatio.Metrics;

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
        public bool? NoServiceProvider { get; set; }
    }

    public class JobRunner {
        public static int RunInConsole(JobRunOptions options) {
            int result;
            string jobName = "N/A";
            try {
                ResolveTypes(options);
                var jobType = options.JobType;
                jobName = jobType.Name;

                Logger.GlobalProperties.Set("job", jobName);
                if (!(options.NoServiceProvider.HasValue && options.NoServiceProvider.Value == false))
                    ServiceProvider.SetServiceProvider(options.ServiceProviderType);

                // force bootstrap now so logging will be configured
                if (ServiceProvider.Current is IBootstrappedServiceProvider)
                    ((IBootstrappedServiceProvider)ServiceProvider.Current).Bootstrap();

                Logger.Info().Message("Starting job...").Write();

                var metricsClient = ServiceProvider.Current.GetService<IMetricsClient>() as InMemoryMetricsClient;
                metricsClient?.StartDisplayingStats(TimeSpan.FromSeconds(5), new LoggerTextWriter());

                result = JobRunner.RunAsync(options).Result;

                if (Debugger.IsAttached)
                    Console.ReadKey();
            } catch (FileNotFoundException e) {
                Console.Error.WriteLine("{0} ({1})", e.GetMessage(), e.FileName);
                Logger.Error().Message($"{e.GetMessage()} ({e.FileName})").Write();

                if (Debugger.IsAttached)
                    Console.ReadKey();
                return 1;
            } catch (Exception e) {
                Console.Error.WriteLine(e.ToString());
                Logger.Error().Exception(e).Message($"Job \"{jobName}\" error: {e.GetMessage()}").Write();

                if (Debugger.IsAttached)
                    Console.ReadKey();

                return 1;
            }

            return result;
        }

        public static Task<JobResult> RunAsync(Type jobType, CancellationToken cancellationToken = default(CancellationToken)) {
            return CreateJobInstance(jobType).RunAsync(cancellationToken);
        }

        public static Task RunUntilEmptyAsync<T>(CancellationToken cancellationToken = default(CancellationToken)) where T: IQueueProcessorJob {
            var jobInstance = CreateJobInstance(typeof(T)) as IQueueProcessorJob;
            if (jobInstance == null)
                throw new ArgumentException("Type T must derive from IQueueProcessorJob.");

            return jobInstance.RunUntilEmptyAsync(cancellationToken);
        }

        public static async Task RunContinuousAsync(Type jobType, TimeSpan? interval = null, int iterationLimit = -1, int instanceCount = 1, CancellationToken cancellationToken = default(CancellationToken)) {
            var tasks = new List<Task>();
            for (int i = 0; i < instanceCount; i++)
                tasks.Add(Task.Run(async () => await CreateJobInstance(jobType).RunContinuousAsync(interval, iterationLimit, cancellationToken).AnyContext(), cancellationToken));

            await Task.WhenAll(tasks).AnyContext();
        }

        public static Task RunContinuousAsync<T>(TimeSpan? interval = null, int iterationLimit = -1, int instanceCount = 1, CancellationToken cancellationToken = default(CancellationToken)) {
            return RunContinuousAsync(typeof(T), interval, iterationLimit, instanceCount, cancellationToken);
        }

        public static async Task<int> RunAsync(JobRunOptions options, CancellationToken cancellationToken = default(CancellationToken)) {
            ResolveTypes(options);
            if (options.JobType == null)
                return -1;

            WatchForShutdown();
            var linkedCancellationToken = CancellationTokenSource.CreateLinkedTokenSource(_cancellationTokenSource.Token, cancellationToken).Token;
            if (options.RunContinuous)
                await RunContinuousAsync(options.JobType, options.Interval, options.IterationLimit, options.InstanceCount, linkedCancellationToken).AnyContext();
            else
                return (await RunAsync(options.JobType, linkedCancellationToken).AnyContext()).IsSuccess ? 0 : -1;

            return 0;
        }

        public static void ResolveTypes(JobRunOptions options) {
            if (options.JobType == null)
                options.JobType = TypeHelper.ResolveType(options.JobTypeName, typeof(JobBase));

            if (options.ServiceProviderType == null)
                options.ServiceProviderType = TypeHelper.ResolveType(options.ServiceProviderTypeName, typeof(IServiceProvider));
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
