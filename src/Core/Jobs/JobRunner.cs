using System;
using System.IO;
using System.Linq;
using System.Threading;
using Foundatio.Dependency;
using NLog.Fluent;

namespace Foundatio.Jobs {
    public class JobRunner {
        public static JobBase CreateJobInstance(string jobTypeName, string bootstrapperTypeName = null) {
            var jobType = Type.GetType(jobTypeName);
            if (jobType == null) {
                Log.Error().Message("Unable to resolve job type: \"{0}\".", jobTypeName).Write();
                return null;
            }

            Type bootstrapperType = jobType;
            if (!String.IsNullOrEmpty(bootstrapperTypeName)) {
                bootstrapperType = Type.GetType(bootstrapperTypeName);
                if (bootstrapperType == null) {
                    Log.Error().Message("Unable to resolve bootstrapper type: \"{0}\".", bootstrapperTypeName).Write();
                    return null;
                }
            }

            var resolver = GetResolver(bootstrapperType);
            if (resolver == null)
                return null;

            var job = resolver.GetService(jobType) as JobBase;
            if (job == null) {
                Log.Error().Message("Job Type must derive from Job.").Write();
                return null;
            }

            return job;
        }

        public static int RunJob(JobBase job, bool runContinuous = false, bool quietMode = false, int delay = 0, Action showHeader = null) {
            if (job == null)
                return -1;

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

        public static IDependencyResolver GetResolver(Type bootstrapperType) {
            if (!typeof(IBootstrapper).IsAssignableFrom(bootstrapperType))
                bootstrapperType = bootstrapperType.Assembly.GetTypes()
                    .Where(typeof(IBootstrapper).IsAssignableFrom).FirstOrDefault();

            if (bootstrapperType != null) {
                var bootstrapper = Activator.CreateInstance(bootstrapperType) as IBootstrapper;
                if (bootstrapper == null) {
                    Log.Error().Message("Job Type must derive from Job.").Write();
                    return null;
                }

                return bootstrapper.GetResolver();
            }

            return new DefaultDependencyResolver();
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
