using System;
using Foundatio.Extensions;
using Foundatio.Logging;

namespace Foundatio.Lock {
    internal class DisposableLock : IDisposable {
        private readonly ILockProvider _lockProvider;
        private readonly string _name;

        public DisposableLock(string name, ILockProvider lockProvider) {
            _name = name;
            _lockProvider = lockProvider;
        }

        public async void Dispose() {
#if DEBUG
            Logger.Trace().Message($"Disposing lock: {_name}").Write();
#endif
            await _lockProvider.ReleaseLockAsync(_name).AnyContext();
#if DEBUG
            Logger.Trace().Message($"Disposed lock: {_name}").Write();
#endif
        }
    }
}