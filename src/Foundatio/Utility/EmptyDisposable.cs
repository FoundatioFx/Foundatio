using System;
using System.Threading.Tasks;
using Foundatio.Lock;

namespace Foundatio.Utility {
    public class EmptyDisposable : IDisposable {
        public void Dispose() {}
    }

    public class EmptyLock : ILock {
        public string LockId => String.Empty;

        public string Resource => String.Empty;

        public DateTime AcquiredTimeUtc => DateTime.MinValue;

        public TimeSpan TimeWaitedForLock => TimeSpan.Zero;

        public int RenewalCount => 0;

        public ValueTask DisposeAsync() {
            return new ValueTask();
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
