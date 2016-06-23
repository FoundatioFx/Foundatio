using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Foundatio.Utility {
    internal static class TaskHelper {
        public static Task Completed { get; } = FromResult(new AsyncVoid());

        public static Task<TResult> FromResult<TResult>(TResult result) {
            var completionSource = new TaskCompletionSource<TResult>();
            completionSource.SetResult(result);
            return completionSource.Task;
        }

        [StructLayout(LayoutKind.Sequential, Size = 1)]
        private struct AsyncVoid {}
    }
}
