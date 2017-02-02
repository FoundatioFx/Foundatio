using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Nito.AsyncEx;

namespace Foundatio.Extensions {
    internal static class TaskExtensions {
        public static Task WaitAsync(this AsyncManualResetEvent resetEvent, TimeSpan timeout) {
            return resetEvent.WaitAsync(timeout.ToCancellationToken());
        }

        public static Task WaitAsync(this AsyncAutoResetEvent resetEvent, TimeSpan timeout) {
            return resetEvent.WaitAsync(timeout.ToCancellationToken());
        }

        [DebuggerStepThrough]
        public static ConfiguredTaskAwaitable<TResult> AnyContext<TResult>(this Task<TResult> task) {
            return task.ConfigureAwait(continueOnCapturedContext: false);
        }

        [DebuggerStepThrough]
        public static ConfiguredTaskAwaitable AnyContext(this Task task) {
            return task.ConfigureAwait(continueOnCapturedContext: false);
        }

        [DebuggerStepThrough]
        public static ConfiguredTaskAwaitable<TResult> AnyContext<TResult>(this AwaitableDisposable<TResult> task) where TResult : IDisposable {
            return task.ConfigureAwait(continueOnCapturedContext: false);
        }
    }
}
