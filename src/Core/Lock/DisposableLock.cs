using System;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Logging;

namespace Foundatio.Lock {
    internal class DisposableLock : ILock {
        private readonly ILockProvider _lockProvider;
        private readonly string _name;

        public DisposableLock(string name, ILockProvider lockProvider) {
            _name = name;
            _lockProvider = lockProvider;
        }

        public async void Dispose() {
            Logger.Trace().Message($"Disposing lock: {_name}").Write();
            await _lockProvider.ReleaseAsync(_name).AnyContext();
            Logger.Trace().Message($"Disposed lock: {_name}").Write();
        }

        public async Task RenewAsync(TimeSpan? lockExtension = null) {
            Logger.Trace().Message($"Renewing lock: {_name}").Write();
            await _lockProvider.RenewAsync(_name, lockExtension).AnyContext();
            Logger.Trace().Message($"Renewing lock: {_name}").Write();
        }
    }
}