using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Foundatio.Utility {
    internal static class TaskHelper {
        private static readonly Task _defaultCompleted = FromResult(new AsyncVoid());
        
        public static Task Completed() {
            return _defaultCompleted;
        }

        public static Task<TResult> FromResult<TResult>(TResult result) {
            var completionSource = new TaskCompletionSource<TResult>();
            completionSource.SetResult(result);
            return completionSource.Task;
        }

        [StructLayout(LayoutKind.Sequential, Size = 1)]
        private struct AsyncVoid {}
    }
}
