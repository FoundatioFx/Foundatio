using System;
using System.IO;
using System.Linq;
using System.Threading;
using Foundatio.ServiceProvider;
using NLog.Fluent;

namespace Foundatio.Jobs {
    public class JobRunner {
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

        public static JobBase CreateJobInstance(string jobTypeName, string serviceProviderTypeName = null) {
            var jobType = Type.GetType(jobTypeName);
            if (jobType == null) {
                Log.Error().Message("Unable to resolve job type: \"{0}\".", jobTypeName).Write();
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

            var resolver = GetServiceProvider(serviceProviderType);
            if (resolver == null)
                return null;

            var job = resolver.GetService(jobType) as JobBase;
            if (job == null) {
                Log.Error().Message("Job Type must derive from Job.").Write();
                return null;
            }

            return job;
        }

        public static IServiceProvider GetServiceProvider(Type serviceProviderType) {
            if (!typeof (IServiceProvider).IsAssignableFrom(serviceProviderType)) {
                // prefer bootstrapped service provider
                serviceProviderType = serviceProviderType.Assembly.GetTypes()
                    .Where(typeof (IBootstrappedServiceProvider).IsAssignableFrom).FirstOrDefault();
                
                if (serviceProviderType == null)
                    serviceProviderType = serviceProviderType.Assembly.GetTypes()
                        .Where(typeof(IServiceProvider).IsAssignableFrom).FirstOrDefault();
            }

            if (serviceProviderType == null)
                return new ActivatorServiceProvider();

            var bootstrapper = Activator.CreateInstance(serviceProviderType) as IServiceProvider;
            if (bootstrapper != null)
                return bootstrapper;

            Log.Error().Message("Job Type must derive from Job.").Write();
            return null;
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
