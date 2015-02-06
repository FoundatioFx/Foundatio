using Foundatio.Caching;
using Foundatio.Lock;
using Foundatio.Tests;
using Foundatio.Tests.Utility;
using StackExchange.Redis;

namespace Foundatio.Redis.Tests.Locks {
    public class RedisLockTests : LockTests {
        public RedisLockTests() {
            var muxer = ConnectionMultiplexer.Connect(ConnectionStrings.Get("RedisConnectionString"));
            _cacheClient = new RedisCacheClient(muxer);
            _locker = new CacheLockProvider(_cacheClient);
        }
    }
}
