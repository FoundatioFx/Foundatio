using System;
using System.Threading.Tasks;
using Foundatio.Lock;

namespace Foundatio.Utility {
    public class EmptyDisposable : IDisposable {
        public void Dispose() {}
    }

    public class EmptyLock : ILock {
        public Task DisposeAsync() {
            return Task.CompletedTask;
        }

        public Task RenewAsync(TimeSpan? lockExtension = null) {
            return Task.CompletedTask;
        }

        public Task ReleaseAsync() {
            return Task.CompletedTask;
        }
    }

    public static class Disposable {
        public static IDisposable Empty = new EmptyDisposable();
        public static ILock EmptyLock = new EmptyLock();
    }
}
