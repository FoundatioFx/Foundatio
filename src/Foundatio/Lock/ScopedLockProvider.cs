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

        public Task<ILock> AcquireAsync(string resource, TimeSpan? timeUntilExpires = null, CancellationToken cancellationToken = default) {
            return UnscopedLockProvider.AcquireAsync(GetScopedLockProviderKey(resource), timeUntilExpires, cancellationToken);
        }

        public Task<bool> IsLockedAsync(string resource) {
            return UnscopedLockProvider.IsLockedAsync(GetScopedLockProviderKey(resource));
        }

        public Task ReleaseAsync(ILock @lock) {
            return UnscopedLockProvider.ReleaseAsync(@lock);
        }

        public Task RenewAsync(ILock @lock, TimeSpan? lockExtension = null) {
            return UnscopedLockProvider.RenewAsync(@lock, lockExtension);
        }
    }
}