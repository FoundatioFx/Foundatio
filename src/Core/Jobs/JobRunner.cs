using System;
using System.IO;
using System.Threading;
using Foundatio.ServiceProviders;
using NLog.Fluent;

namespace Foundatio.Jobs {
    public class JobRunner {
        public static int RunJob(JobBase job, bool runContinuous = false, bool quietMode = false, int delay = 0, Action showHeader = null) {
            if (job == null) {
                Log.Error().Message("Starting {0}job type <null> on machine \"{2}\"...", runContinuous ? "continuous " : String.Empty, Environment.MachineName).Write();
                return -1;
            }

            NLog.GlobalDiagnosticsContext.Set("job", job.GetType().FullName);
            if (!quietMode && showHeader != null) {
                showHeader();
            }

            Log.Info().Message("Starting {0}job type \"{1}\" on machine \"{2}\"...", runContinuous ? "continuous " : String.Empty, job.GetType().Name, Environment.MachineName).Write();

            WatchForShutdown();
            if (runContinuous)
                job.RunContinuous(TimeSpan.FromMilliseconds(delay), token: _cancellationTokenSource.Token);
            else
                job.Run(_cancellationTokenSource.Token);

            return 0;
        }

        public static JobBase CreateJobInstance(string jobTypeName, string serviceProviderTypeName = null) {
            var jobType = Type.GetType(jobTypeName);
            if (jobType == null) {
                Log.Error().Message("Unable to resolve job type: \"{0}\".", jobTypeName).Write();
                return null;
            }

            if (!typeof (JobBase).IsAssignableFrom(jobType)) {
                Log.Error().Message("Job Type must derive from Job.").Write();
                return null;
            }

            Type serviceProviderType = jobType;
            if (!String.IsNullOrEmpty(serviceProviderTypeName)) {
                serviceProviderType = Type.GetType(serviceProviderTypeName);
                if (serviceProviderType == null) {
                    Log.Error().Message("Unable to resolve service provider type: \"{0}\".", serviceProviderTypeName).Write();
                    return null;
                }
            }

            ServiceProvider.SetServiceProvider(serviceProviderType);
            var job = ServiceProvider.Current.GetService(jobType) as JobBase;
            if (job == null) {
                Log.Error().Message("Job Type must derive from Job.").Write();
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
