using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Nito.AsyncEx;

namespace Foundatio.Tests.Extensions {
    public static class TaskExtensions {
        /// <summary>
        /// Returns a <see cref="Task"/> that is canceled when this <see cref="CancellationToken"/> is canceled. This method will leak resources if the cancellation token is long-lived; use <see cref="ToCancellationTokenTaskSource"/> for a similar approach with proper resource management.
        /// </summary>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> to monitor.</param>
        /// <returns>A <see cref="Task"/> that is canceled when this <see cref="CancellationToken"/> is canceled.</returns>
        private static Task AsTask(this CancellationToken cancellationToken) {
            if (!cancellationToken.CanBeCanceled)
                return new TaskCompletionSource<int>().Task;
            if (cancellationToken.IsCancellationRequested)
                return TaskConstants.Canceled;
            var tcs = new TaskCompletionSource<int>();
            cancellationToken.Register(() => tcs.TrySetCanceled(), useSynchronizationContext: false);
            return tcs.Task;
        }

        public static Task WaitAsync(this AsyncCountdownEvent countdownEvent, CancellationToken cancellationToken = default(CancellationToken)) {
            return Task.WhenAny(countdownEvent.WaitAsync(), cancellationToken.AsTask());
        }

        public static Task WaitAsync(this AsyncCountdownEvent countdownEvent, TimeSpan timeout) {
            return countdownEvent.WaitAsync(timeout.ToCancellationToken());
        }
    }
}