using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;

namespace Foundatio.Utility {
    internal static class Run {
        public static async Task DelayedAsync(TimeSpan delay, Func<Task> action) {
            await Task.Run(async () => {
                await Task.Delay(delay).AnyContext();
                await action().AnyContext();
            }).AnyContext();
        }

        public static Task InParallel(int iterations, Func<int, Task> work) {
            return Task.WhenAll(Enumerable.Range(1, iterations).Select(i => Task.Run(() => work(i))));
        }
        
        public static async Task<T> WithRetriesAsync<T>(Func<Task<T>> action, int maxAttempts = 3, TimeSpan? retryInterval = null, CancellationToken cancellationToken = default(CancellationToken)) {
            if (action == null)
                throw new ArgumentNullException(nameof(action));

            int attempts = 1;
            do {
                try {
                    return await action().AnyContext();
                } catch {
                    if (attempts > maxAttempts)
                        throw;
                    
                    await Task.Delay(retryInterval ?? TimeSpan.FromMilliseconds(attempts * 100), cancellationToken).AnyContext();
                }

                attempts++;
            } while (attempts <= maxAttempts && !cancellationToken.IsCancellationRequested);

            throw new TaskCanceledException("Should not get here.");
        }
    }
}