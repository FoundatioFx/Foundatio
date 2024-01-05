using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;

namespace Foundatio.Lock
{
    public class ScopedLockProvider : ILockProvider, IHaveLogger
    {
        private string _keyPrefix;
        private bool _isLocked;
        private readonly object _lock = new();

        public ScopedLockProvider(ILockProvider lockProvider, string scope = null)
        {
            UnscopedLockProvider = lockProvider;
            _isLocked = scope != null;
            Scope = !String.IsNullOrWhiteSpace(scope) ? scope.Trim() : null;

            _keyPrefix = Scope != null ? String.Concat(Scope, ":") : String.Empty;
        }

        public ILockProvider UnscopedLockProvider { get; }
        public string Scope { get; private set; }
        ILogger IHaveLogger.Logger => UnscopedLockProvider.GetLogger();

        public void SetScope(string scope)
        {
            if (_isLocked)
                throw new InvalidOperationException("Scope can't be changed after it has been set");

            lock (_lock)
            {
                if (_isLocked)
                    throw new InvalidOperationException("Scope can't be changed after it has been set");

                _isLocked = true;
                Scope = !String.IsNullOrWhiteSpace(scope) ? scope.Trim() : null;
                _keyPrefix = Scope != null ? String.Concat(Scope, ":") : String.Empty;
            }
        }

        protected string GetScopedLockProviderKey(string key)
        {
            return String.Concat(_keyPrefix, key);
        }

        public Task<ILock> AcquireAsync(string resource, TimeSpan? timeUntilExpires = null, bool releaseOnDispose = true, CancellationToken cancellationToken = default)
        {
            return UnscopedLockProvider.AcquireAsync(GetScopedLockProviderKey(resource), timeUntilExpires, releaseOnDispose, cancellationToken);
        }

        public Task<bool> IsLockedAsync(string resource)
        {
            return UnscopedLockProvider.IsLockedAsync(GetScopedLockProviderKey(resource));
        }

        public Task ReleaseAsync(string resource, string lockId)
        {
            return UnscopedLockProvider.ReleaseAsync(resource, lockId);
        }

        public Task RenewAsync(string resource, string lockId, TimeSpan? timeUntilExpires = null)
        {
            return UnscopedLockProvider.RenewAsync(resource, lockId, timeUntilExpires);
        }
    }
}
