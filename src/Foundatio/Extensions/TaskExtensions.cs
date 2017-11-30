using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Foundatio.AsyncEx;

namespace Foundatio.Utility {
    internal static class TaskExtensions {
        [DebuggerStepThrough]
        public static Task WaitAsync(this AsyncManualResetEvent resetEvent, TimeSpan timeout) {
            return resetEvent.WaitAsync(timeout.ToCancellationToken());
        }

        [DebuggerStepThrough]
        public static Task WaitAsync(this AsyncAutoResetEvent resetEvent, TimeSpan timeout) {
            return resetEvent.WaitAsync(timeout.ToCancellationToken());
        }

        public static void TryStart(this Task task) {
            try {
                task.Start();
            } catch (InvalidOperationException) { }
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
