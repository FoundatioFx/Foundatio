using System;
using System.Threading;
using System.Threading.Tasks;

namespace Foundatio.Lock {
    public class ScopedLockProvider : ILockProvider {
        private string _keyPrefix;
        private bool _isLocked;
        private readonly object _lock = new object();

        public ScopedLockProvider(ILockProvider lockProvider, string scope = null) {
            UnscopedLockProvider = lockProvider;
            _isLocked = scope != null;
            Scope = !String.IsNullOrWhiteSpace(scope) ? scope.Trim() : null;

            _keyPrefix = Scope != null ? String.Concat(Scope, ":") : String.Empty;
        }

        public ILockProvider UnscopedLockProvider { get; }

        public string Scope { get; private set; }

        public void SetScope(string scope) {
            if (_isLocked)
                throw new InvalidOperationException("Scope can't be changed after it has been set.");

            lock (_lock) {
                if (_isLocked)
                    throw new InvalidOperationException("Scope can't be changed after it has been set.");

                _isLocked = true;
                Scope = !String.IsNullOrWhiteSpace(scope) ? scope.Trim() : null;
                _keyPrefix = Scope != null ? String.Concat(Scope, ":") : String.Empty;
            }
        }

        protected string GetScopedLockProviderKey(string key) {
            return String.Concat(_keyPrefix, key);
        }

        public Task<ILock> AcquireAsync(string name, TimeSpan? lockTimeout = null, CancellationToken cancellationToken = default(CancellationToken)) {
            return UnscopedLockProvider.AcquireAsync(GetScopedLockProviderKey(name), lockTimeout, cancellationToken);
        }

        public Task<bool> IsLockedAsync(string name) {
            return UnscopedLockProvider.IsLockedAsync(GetScopedLockProviderKey(name));
        }

        public Task ReleaseAsync(string name) {
            return UnscopedLockProvider.ReleaseAsync(GetScopedLockProviderKey(name));
        }

        public Task RenewAsync(string name, TimeSpan? lockExtension = null) {
            return UnscopedLockProvider.RenewAsync(GetScopedLockProviderKey(name));
        }
    }
}