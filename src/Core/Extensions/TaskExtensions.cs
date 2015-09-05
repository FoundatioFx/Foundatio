using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace Foundatio.Extensions {
    internal static class TaskCompletionSourceExtensions {
        public static Task IgnoreExceptions(this Task task) {
            task.ContinueWith(c => { var ignored = c.Exception; },
                TaskContinuationOptions.OnlyOnFaulted |
                TaskContinuationOptions.ExecuteSynchronously);
            return task;
        }

        public static Task<T> IgnoreExceptions<T>(this Task<T> task)
        {
            task.ContinueWith(c => { var ignored = c.Exception; },
                TaskContinuationOptions.OnlyOnFaulted |
                TaskContinuationOptions.ExecuteSynchronously);
            return task;
        }

        public static void SetFromTask<TResult>(this TaskCompletionSource<TResult> resultSetter, Task task) {
            switch (task.Status) {
                case TaskStatus.RanToCompletion: resultSetter.SetResult(task is Task<TResult> ? ((Task<TResult>)task).Result : default(TResult)); break;
                case TaskStatus.Faulted: resultSetter.SetException(task.Exception.InnerExceptions); break;
                case TaskStatus.Canceled: resultSetter.SetCanceled(); break;
                default: throw new InvalidOperationException("The task was not completed.");
            }
        }

        public static void SetFromTask<TResult>(this TaskCompletionSource<TResult> resultSetter, Task<TResult> task) {
            SetFromTask(resultSetter, (Task)task);
        }

        public static bool TrySetFromTask<TResult>(this TaskCompletionSource<TResult> resultSetter, Task task) {
            switch (task.Status) {
                case TaskStatus.RanToCompletion: return resultSetter.TrySetResult(task is Task<TResult> ? ((Task<TResult>)task).Result : default(TResult));
                case TaskStatus.Faulted: return resultSetter.TrySetException(task.Exception.InnerExceptions);
                case TaskStatus.Canceled: return resultSetter.TrySetCanceled();
                default: throw new InvalidOperationException("The task was not completed.");
            }
        }

        public static bool TrySetFromTask<TResult>(this TaskCompletionSource<TResult> resultSetter, Task<TResult> task) {
            return TrySetFromTask(resultSetter, (Task)task);
        }

        public static ConfiguredTaskAwaitable<TResult> AnyContext<TResult>(this Task<TResult> task) {
            return task.ConfigureAwait(continueOnCapturedContext: false);
        }

        public static ConfiguredTaskAwaitable AnyContext(this Task task) {
            return task.ConfigureAwait(continueOnCapturedContext: false);
        }
    }
}
