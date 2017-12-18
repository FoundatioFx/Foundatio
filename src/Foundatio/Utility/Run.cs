using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Utility {
    public static class Run {
        public static Task DelayedRunAsync(TimeSpan delay, Func<Task> action, CancellationToken cancellationToken = default(CancellationToken)) {
            return DelayedRunAsync(delay, ct => action(), cancellationToken);
        }

        public static Task DelayedRunAsync(TimeSpan delay, Func<CancellationToken, Task> action, CancellationToken cancellationToken = default(CancellationToken)) {
            return Task.Run(() => {
                if (delay.Ticks <= 0)
                    return action(cancellationToken);

                return SystemClock.SleepAsync(delay, cancellationToken)
                    .ContinueWith(t => action(cancellationToken), cancellationToken);
            }, cancellationToken);
        }

        public static Task InParallelAsync(int iterations, Func<int, Task> work) {
            return Task.WhenAll(Enumerable.Range(1, iterations).Select(i => Task.Run(() => work(i))));
        }

        public static Task WithRetriesAsync(Func<Task> action, CancellationToken cancellationToken = default(CancellationToken)) {
            return DefaultRetry.RunAsync(action, cancellationToken);
        }

        public static Task WithRetriesAsync(Func<Task> action, int maxAttempts, CancellationToken cancellationToken = default(CancellationToken)) {
            return DefaultRetry.RunAsync(action, maxAttempts, cancellationToken);
        }

        public static Task<T> WithRetriesAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken = default(CancellationToken)) {
            return DefaultRetry.RunAsync(action, cancellationToken);
        }

        public static Task<T> WithRetriesAsync<T>(Func<Task<T>> action, int maxAttempts, CancellationToken cancellationToken = default(CancellationToken)) {
            return DefaultRetry.RunAsync(action, maxAttempts, cancellationToken);
        }

        public static Retry DefaultRetry { get; set; } = new Retry();
    }

    public interface IRetryPolicy {
        int DefaultMaxAttempts { get; }
        Func<int, int> RetryIntervalFunc { get; }
        ILogger Logger { get; }
        Func<Exception, bool> ShouldRetryFunc { get; }
    }

    public class Retry {
        private readonly IRetryPolicy _policy;
        public static IRetryPolicy DefaultPolicy { get; set; } = new DefaultRetryPolicy();
       
        public Retry(IRetryPolicy policy = null) {
            _policy = policy ?? DefaultPolicy;
        }
        
        public Retry(ILogger logger) {
            _policy = new DefaultRetryPolicy { Logger = logger };
        }
       
        public Task RunAsync(Func<Task> action, CancellationToken cancellationToken = default(CancellationToken)) {
            return RunAsync(action, _policy.DefaultMaxAttempts, cancellationToken);
        }
       
        public Task RunAsync(Func<Task> action, int maxAttempts, CancellationToken cancellationToken = default(CancellationToken)) {
            return RunAsync(action, maxAttempts, cancellationToken);
        }

        public Task<T> RunAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken = default(CancellationToken)) {
            return RunAsync(action, _policy.DefaultMaxAttempts, cancellationToken);
        }

        public Task<T> RunAsync<T>(Func<Task<T>> action, int maxAttempts, CancellationToken cancellationToken = default(CancellationToken)) {
            if (action == null)
                throw new ArgumentNullException(nameof(action));

            var startTime = SystemClock.UtcNow;
            return action().ContinueWith(task => RetryContinuation(task, action, 1, maxAttempts, startTime, cancellationToken), cancellationToken);
        }

        private T RetryContinuation<T>(Task<T> task, Func<Task<T>> action, int attempts, int maxAttempts, DateTime startTime, CancellationToken cancellationToken) {
            if (!task.IsFaulted)
                return task.Result;
            
            if (_policy.Logger.IsEnabled(LogLevel.Error))
                _policy.Logger.LogError(task.Exception.InnerException, "Retry error: {Message}", task.Exception.InnerException.Message);

            if (attempts <= maxAttempts - 1 && _policy.ShouldRetryFunc(task.Exception.InnerException)) {
                return SystemClock.SleepAsync(_policy.RetryIntervalFunc(attempts), cancellationToken)
                    .ContinueWith(t => {
                        if (_policy.Logger.IsEnabled(LogLevel.Information))
                            _policy.Logger.LogInformation("Retrying {Attempts} attempt after {Delay:g}...", attempts.ToOrdinal(), SystemClock.UtcNow.Subtract(startTime));
                        
                        return action();
                    }, cancellationToken).Unwrap()
                    .ContinueWith(retryTask => RetryContinuation(retryTask, action, attempts++, maxAttempts, startTime, cancellationToken), cancellationToken)
                    .Result;
            }

            // will rethrow
            return task.Result;
        }
    }

    public class DefaultRetryPolicy : IRetryPolicy {
        private static readonly int[] _defaultBackoffIntervals = { 100, 1000, 2000, 2000, 5000, 5000, 10000, 30000, 60000 };
        private static readonly Random _rnd = new Random();

        public int DefaultMaxAttempts { get; set; } = 5;
        public Func<int, int> RetryIntervalFunc { get; set; } = attempt => {
            int delayVariance = _rnd.Next(0, 50);
            return _defaultBackoffIntervals[Math.Min(attempt, _defaultBackoffIntervals.Length - 1)] + delayVariance;
        };
        public ILogger Logger { get; set; } = NullLogger.Instance;
        public Func<Exception, bool> ShouldRetryFunc { get; set; } = e => true;
    }
}