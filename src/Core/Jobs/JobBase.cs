using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Utility;
using Foundatio.Logging;

namespace Foundatio.Jobs {
    public abstract class JobBase : IDisposable {
        protected virtual IDisposable GetJobLock() {
            return Disposable.Empty;
        }

        private string _jobName;
        private void EnsureJobNameSet()
        {
            if (_jobName == null)
                _jobName = GetType().Name;
            Logger.ThreadProperties.Set("job", _jobName);
        }

        public async Task<JobResult> RunAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            EnsureJobNameSet();
            Logger.Trace().Message("Job run \"{0}\" starting...", _jobName).Write();

            try {
                var lockValue = GetJobLock();
                if (lockValue == null)
                    return JobResult.SuccessWithMessage("Unable to acquire job lock.");

                using (lockValue) {
                    var result = await TryRunAsync(cancellationToken);
                    if (result != null) {
                        if (!result.IsSuccess)
                            Logger.Error().Message("Job run \"{0}\" failed: {1}", GetType().Name, result.Message).Exception(result.Error).Write();
                        else if (!String.IsNullOrEmpty(result.Message))
                            Logger.Info().Message("Job run \"{0}\" succeeded: {1}", GetType().Name, result.Message).Write();
                        else
                            Logger.Trace().Message("Job run\"{0}\" succeeded", GetType().Name).Write();
                    } else {
                        Logger.Error().Message("Null job run result for \"{0}\".", GetType().Name).Write();
                    }

                    return result;
                }
            } catch (TimeoutException) {
                return JobResult.SuccessWithMessage("Timeout attempting to acquire lock.");
            }
        }

        private async Task<JobResult> TryRunAsync(CancellationToken token) {
            try {
                return await RunInternalAsync(token);
            } catch (Exception ex) {
                return JobResult.FromException(ex);
            }
        }

        protected abstract Task<JobResult> RunInternalAsync(CancellationToken token);

        public JobResult Run(CancellationToken token = default(CancellationToken)) {
            return RunAsync(token).Result;
        }

        public async Task RunContinuousAsync(TimeSpan? interval = null, int iterationLimit = -1, CancellationToken cancellationToken = default(CancellationToken)) {
            int iterations = 0;

            EnsureJobNameSet();
            Logger.Info().Message("Starting continuous job type \"{0}\" on machine \"{1}\"...", GetType().Name, Environment.MachineName).Write();

            while (!cancellationToken.IsCancellationRequested && (iterationLimit < 0 || iterations < iterationLimit)) {
                await RunAsync(cancellationToken);

                iterations++;
                if (!interval.HasValue || interval.Value <= TimeSpan.Zero)
                    continue;

                try {
                    await Task.Delay(interval.Value, cancellationToken);
                } catch (TaskCanceledException) {}
            }

            Logger.Info().Message("Stopping continuous job type \"{0}\" on machine \"{1}\"...", GetType().Name, Environment.MachineName).Write();

            if (cancellationToken.IsCancellationRequested)
                Logger.Trace().Message("Job cancellation requested.").Write();
        }

        public void RunContinuous(TimeSpan? delay = null, int iterationLimit = -1, CancellationToken token = default(CancellationToken)) {
            RunContinuousAsync(delay, iterationLimit, token).Wait(token);
        }

        public virtual void Dispose() {}
    }
}