using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Logging;
using Foundatio.Utility;

namespace Foundatio.Jobs {
    public abstract class JobBase : IDisposable {
        public JobBase() {
            JobId = Guid.NewGuid().ToString("N").Substring(0, 10);
        }

        protected virtual Task<IDisposable> GetJobLockAsync() {
            return Task.FromResult(Disposable.Empty);
        }

        public string JobId { get; private set; }

        private string _jobName;

        private void EnsureJobNameSet() {
            if (_jobName == null)
                _jobName = GetType().Name;
            Logger.ThreadProperties.Set("job", _jobName);
        }

        public async Task<JobResult> RunAsync(CancellationToken cancellationToken = default(CancellationToken)) {
            EnsureJobNameSet();
            Logger.Trace().Message("Job run \"{0}\" starting...", _jobName).Write();

            using (var lockValue = await GetJobLockAsync().AnyContext()) {
                if (lockValue == null)
                    return JobResult.SuccessWithMessage("Unable to acquire job lock.");

                var result = await TryRunAsync(cancellationToken).AnyContext();
                if (result != null) {
                    if (!result.IsSuccess)
                        Logger.Error().Message("Job run \"{0}\" failed: {1}", GetType().Name, result.Message).Exception(result.Error).Write();
                    else if (!String.IsNullOrEmpty(result.Message))
                        Logger.Info().Message("Job run \"{0}\" succeeded: {1}", GetType().Name, result.Message).Write();
                    else
                        Logger.Trace().Message("Job run \"{0}\" succeeded.", GetType().Name).Write();
                } else {
                    Logger.Error().Message("Null job run result for \"{0}\".", GetType().Name).Write();
                }

                return result;
            }
        }

        private async Task<JobResult> TryRunAsync(CancellationToken token) {
            try {
                return await RunInternalAsync(token).AnyContext();
            } catch (Exception ex) {
                return JobResult.FromException(ex);
            }
        }

        protected abstract Task<JobResult> RunInternalAsync(CancellationToken token);
        
        public async Task RunContinuousAsync(TimeSpan? interval = null, int iterationLimit = -1, CancellationToken cancellationToken = default(CancellationToken), Func<Task<bool>> continuationCallback = null) {
            int iterations = 0;

            EnsureJobNameSet();
            Logger.Info().Message("Starting continuous job type \"{0}\" on machine \"{1}\"...", GetType().Name, Environment.MachineName).Write();

            while (!cancellationToken.IsCancellationRequested && (iterationLimit < 0 || iterations < iterationLimit)) {
                try {
                    await RunAsync(cancellationToken).AnyContext();

                    iterations++;
                    if (interval.HasValue)
                        await Task.Delay(interval.Value, cancellationToken).AnyContext();
                    else if (iterations % 1000 == 0) // allow for cancellation token to get set
                        await Task.Delay(1).AnyContext();
                } catch (TaskCanceledException) {}

                if (continuationCallback == null)
                    continue;

                try {
                    if (!await continuationCallback().AnyContext())
                        break;
                } catch (Exception ex) {
                    Logger.Error().Message("Error in continuation callback: {0}", ex.Message).Exception(ex).Write();
                }
            }

            Logger.Info().Message("Stopping continuous job type \"{0}\" on machine \"{1}\"...", GetType().Name, Environment.MachineName).Write();

            if (cancellationToken.IsCancellationRequested)
                Logger.Trace().Message("Job cancellation requested.").Write();

            await Task.Delay(1).AnyContext(); // allow events to process
        }
        
        public virtual void Dispose() {}
    }
}