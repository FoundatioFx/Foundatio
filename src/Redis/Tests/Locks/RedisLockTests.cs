//using Foundatio.Caching;
//using Foundatio.Lock;
//using Foundatio.Redis.Cache;
//using Foundatio.Tests;
//using Foundatio.Tests.Utility;
//using StackExchange.Redis;
//using Xunit;

//namespace Foundatio.Redis.Tests.Locks {
//    public class RedisLockTests : LockTestBase {
//        protected override ILockProvider GetLockProvider() {
//            var muxer = ConnectionMultiplexer.Connect(ConnectionStrings.Get("RedisConnectionString"));
//            return new CacheLockProvider(new RedisCacheClient(muxer));
//        }

//        [Fact]
//        public override void CanAcquireAndReleaseLock() {
//            base.CanAcquireAndReleaseLock();
//        }

//        [Fact]
//        public override void LockWillTimeout() {
//            base.LockWillTimeout();
//        }
//    }
//}
