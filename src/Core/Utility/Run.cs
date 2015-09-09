using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;

namespace Foundatio.Utility {
    internal static class Run {
        private static readonly ConcurrentDictionary<Delegate, object> _onceCalls = new ConcurrentDictionary<Delegate, object>(new LambdaComparer<Delegate>(CompareDelegates));
        public static void Once(Action action) {
            if (_onceCalls.TryAdd(action, null))
                action();
        }

        private static int CompareDelegates(Delegate del1, Delegate del2) {
            if (del1 == null)
                return -1;
            if (del2 == null)
                return 1;

            return GetDelegateHashCode(del1).CompareTo(GetDelegateHashCode(del2));
        }

        private static int GetDelegateHashCode(Delegate obj) {
            if (obj == null)
                return 0;

            return obj.Method.GetHashCode() ^ obj.GetType().GetHashCode();
        }

        public static Task WithRetriesAsync(Action action, int attempts = 3, TimeSpan? retryInterval = null) {
            return WithRetriesAsync<object>(() => {
                action();
                return null;
            }, attempts, retryInterval);
        }

        public static async Task<T> WithRetriesAsync<T>(Func<Task<T>> action, int attempts = 3, TimeSpan? retryInterval = null) {
            if (action == null)
                throw new ArgumentNullException(nameof(action));

            do {
                try {
                    return await action().AnyContext();
                } catch {
                    if (attempts <= 0)
                        throw;

                    if (retryInterval != null)
                        await Task.Delay(retryInterval.Value).AnyContext();
                    else
                        await SleepBackOffMultiplierAsync(attempts).AnyContext();
                }
            } while (attempts-- > 1);

            throw new ApplicationException("Should not get here.");
        }

        public static async Task UntilTrueAsync(Func<Task<bool>> action, TimeSpan? timeOut, TimeSpan? intervalDelay = null) {
            var i = 0;
            var firstAttempt = DateTime.UtcNow;

            // if zero timeout, then only try action once.
            if (timeOut.HasValue && timeOut.Value == TimeSpan.Zero) {
                if (await action().AnyContext())
                    return;

                throw new TimeoutException($"Exceeded timeout of {timeOut}");
            }

            while (timeOut == null || DateTime.UtcNow - firstAttempt < timeOut.Value) {
                i++;
                if (await action().AnyContext())
                    return;

                if (intervalDelay.HasValue)
                    await Task.Delay(intervalDelay.Value).AnyContext();
                else
                    await SleepBackOffMultiplierAsync(i).AnyContext();
            }

            throw new TimeoutException(String.Format("Exceeded timeout of {0}", timeOut.Value));
        }

        public static void Delayed(Action action, TimeSpan delay, CancellationToken token = default(CancellationToken))
        {
            Task.Delay(delay, token)
                .ContinueWith(t => action(), TaskContinuationOptions.OnlyOnRanToCompletion);
        }

        private static async Task SleepBackOffMultiplierAsync(int i) {
            var rand = new Random(Guid.NewGuid().GetHashCode());
            var nextTry = rand.Next(
                (int)Math.Pow(i, 2), (int)Math.Pow(i + 1, 2) + 1);

            await Task.Delay(nextTry).AnyContext();
        }

        public static Task InBackground(Action action, int? maxFaults = null, TimeSpan? restartInterval = null) {
            return InBackground(t => action(), default(CancellationToken), maxFaults, restartInterval);
        }

        public static Task InBackground(Action<CancellationToken> action, CancellationToken token = default(CancellationToken), int? maxFaults = null, TimeSpan? restartInterval = null) {
            if (!maxFaults.HasValue)
                maxFaults = Int32.MaxValue;

            if (!restartInterval.HasValue)
                restartInterval = TimeSpan.FromMilliseconds(100);

            if (action == null)
                throw new ArgumentNullException("action");

            return Task.Run(() => {
                do {
                    try {
                        action(token);
                    } catch {
                        if (maxFaults <= 0)
                            throw;

                        Task.Delay(restartInterval.Value, token).Wait(token);
                    }
                } while (!token.IsCancellationRequested && maxFaults-- > 0);
            }, token);
        }
    }
}
