using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;

namespace Foundatio.Utility {
    internal static class Run {
        public static Task InParallel(int iterations, Func<int, Task> work) {
            return Task.WhenAll(Enumerable.Range(1, iterations).Select(i => Task.Run(() => work(i))));
        }
        
        public static async Task<T> WithRetriesAsync<T>(Func<Task<T>> action, int attempts = 3, TimeSpan? retryInterval = null, CancellationToken cancellationToken = default(CancellationToken)) {
            if (action == null)
                throw new ArgumentNullException(nameof(action));

            int retries = 1;
            do {
                try {
                    return await action().AnyContext();
                } catch {
                    if (retries > attempts)
                        throw;
                    
                    await Task.Delay(retryInterval ?? TimeSpan.FromMilliseconds(retries * 100), cancellationToken).AnyContext();
                }

                retries++;
            } while (retries <= attempts && !cancellationToken.IsCancellationRequested);

            throw new TaskCanceledException("Should not get here.");
        }
    }
}