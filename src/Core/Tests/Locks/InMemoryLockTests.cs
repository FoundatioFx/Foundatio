using Foundatio.Caching;
using Foundatio.Lock;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests {
    public class InMemoryLockTests : LockTestBase {
        protected override ILockProvider GetLockProvider() {
            return new CacheLockProvider(new InMemoryCacheClient());
        }

        [Fact]
        public override void CanAcquireAndReleaseLock() {
            base.CanAcquireAndReleaseLock();
        }

        [Fact]
        public override void LockWillTimeout() {
            base.LockWillTimeout();
        }

        public InMemoryLockTests(ITestOutputHelper helper) : base(helper) {}
    }
}