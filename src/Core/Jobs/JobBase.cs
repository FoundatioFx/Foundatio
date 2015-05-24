using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Utility;
using NLog.Fluent;

namespace Foundatio.Jobs {
    public abstract class JobBase : IDisposable {
        protected virtual IDisposable GetJobLock() {
            return Disposable.Empty;
        }

        private bool _jobNameSet = false;
        private void EnsureJobNameSet() {
            if (_jobNameSet)
                return;

            NLog.GlobalDiagnosticsContext.Set("job", GetType().FullName);
            _jobNameSet = true;
        }

        public async Task<JobResult> RunAsync(CancellationToken cancellationToken = default(CancellationToken)) {
            EnsureJobNameSet();

            Log.Trace().Message("Job \"{0}\" starting...", GetType().Name).Write();

            try {
                var lockValue = GetJobLock();
                if (lockValue == null)
                    return JobResult.SuccessWithMessage("Unable to acquire job lock.");

                using (lockValue) {
                    var result = await TryRunAsync(cancellationToken);
                    if (result != null) {
                        if (!result.IsSuccess)
                            Log.Error().Message("Job \"{0}\" failed: {1}", GetType().Name, result.Message).Exception(result.Error).Write();
                        else if (!String.IsNullOrEmpty(result.Message))
                            Log.Info().Message("Job \"{0}\" succeeded: {1}", GetType().Name, result.Message).Write();
                        else
                            Log.Trace().Message("Job \"{0}\" succeeded", GetType().Name).Write();
                    } else {
                        Log.Error().Message("Null job result for \"{0}\".", GetType().Name).Write();
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

            while (!cancellationToken.IsCancellationRequested && (iterationLimit < 0 || iterations < iterationLimit)) {
                await RunAsync(cancellationToken);

                iterations++;
                if (!interval.HasValue || interval.Value <= TimeSpan.Zero)
                    continue;

                try {
                    await Task.Delay(interval.Value, cancellationToken);
                } catch (TaskCanceledException) {}
            }

            if (cancellationToken.IsCancellationRequested)
                Log.Trace().Message("Job cancellation requested.").Write();
        }

        public void RunContinuous(TimeSpan? delay = null, int iterationLimit = -1, CancellationToken token = default(CancellationToken)) {
            RunContinuousAsync(delay, iterationLimit, token).Wait(token);
        }

        public virtual void Dispose() {}
    }
}