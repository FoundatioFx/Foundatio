using System;
using System.Threading;
using System.Threading.Tasks;

namespace Foundatio.Tests.Extensions {
    internal static class TaskFactoryExtensions {
        public static Task StartNewDelayed(this TaskFactory factory, int millisecondsDelay, CancellationToken cancellationToken = default(CancellationToken)) {
            // Validate arguments
            if (factory == null)
                throw new ArgumentNullException(nameof(factory));
            if (millisecondsDelay < 0)
                throw new ArgumentOutOfRangeException(nameof(millisecondsDelay));

            // Create the timed task
            var tcs = new TaskCompletionSource<object>(factory.CreationOptions);
            var ctr = default(CancellationTokenRegistration);

            // Create the timer but don't start it yet.  If we start it now,
            // it might fire before ctr has been set to the right registration.
            var timer = new Timer(self => {
                // Clean up both the cancellation token and the timer, and try to transition to completed
                ctr.Dispose();
                ((Timer)self).Dispose();
                tcs.TrySetResult(null);
            });

            // Register with the cancellation token.
            if (cancellationToken.CanBeCanceled) {
                // When cancellation occurs, cancel the timer and try to transition to canceled.
                // There could be a race, but it's benign.
                ctr = cancellationToken.Register(() => {
                    timer.Dispose();
                    tcs.TrySetCanceled();
                });
            }

            // Start the timer and hand back the task...
            timer.Change(millisecondsDelay, Timeout.Infinite);
            return tcs.Task;
        }

        public static Task StartNewDelayed(this TaskFactory factory, int millisecondsDelay, Action action) {
            if (factory == null)
                throw new ArgumentNullException(nameof(factory));
            return StartNewDelayed(factory, millisecondsDelay, action, factory.CancellationToken, factory.CreationOptions, factory.GetTargetScheduler());
        }
        
        public static Task StartNewDelayed(this TaskFactory factory, int millisecondsDelay, Action action, CancellationToken cancellationToken, TaskCreationOptions creationOptions, TaskScheduler scheduler) {
            if (factory == null)
                throw new ArgumentNullException(nameof(factory));
            if (millisecondsDelay < 0)
                throw new ArgumentOutOfRangeException(nameof(millisecondsDelay));
            if (action == null)
                throw new ArgumentNullException(nameof(action));
            if (scheduler == null)
                throw new ArgumentNullException(nameof(scheduler));

            return factory.StartNewDelayed(millisecondsDelay, cancellationToken).ContinueWith(_ => action(), cancellationToken, TaskContinuationOptions.OnlyOnRanToCompletion, scheduler);
        }
        
        /// <summary>Gets the TaskScheduler instance that should be used to schedule tasks.</summary>
        public static TaskScheduler GetTargetScheduler(this TaskFactory factory) {
            if (factory == null)
                throw new ArgumentNullException(nameof(factory));
            return factory.Scheduler ?? TaskScheduler.Current;
        }
    }
}