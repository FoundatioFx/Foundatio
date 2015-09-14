using System;
using Foundatio.Extensions;

namespace Foundatio.Lock {
    internal class DisposableLock : IDisposable {
        private readonly ILockProvider _lockProvider;
        private readonly string _name;

        public DisposableLock(string name, ILockProvider lockProvider) {
            _name = name;
            _lockProvider = lockProvider;
        }

        public async void Dispose() {
            await _lockProvider.ReleaseLockAsync(_name).AnyContext();
        }
    }
}