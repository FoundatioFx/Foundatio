using Foundatio.Caching;
using Foundatio.Lock;
using Foundatio.Tests.Utility;
using StackExchange.Redis;

namespace Foundatio.Tests {
    public class RedisLockTests : LockTests {
        public RedisLockTests() {
            var muxer = ConnectionMultiplexer.Connect(ConnectionStrings.Get("RedisConnectionString"));
            _cacheClient = new RedisCacheClient(muxer);
            _locker = new CacheLockProvider(_cacheClient);
        }
    }
}
