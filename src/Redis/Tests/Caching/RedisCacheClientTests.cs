using System;
using Foundatio.Caching;
using Foundatio.Tests.Caching;
using Foundatio.Tests.Utility;
using StackExchange.Redis;

namespace Foundatio.Redis.Tests.Caching {
    public class RedisCacheClientTests: CacheClientTestsBase {
        protected override ICacheClient GetCache() {
            if (ConnectionStrings.Get("RedisConnectionString") == null)
                return null;

            var muxer = ConnectionMultiplexer.Connect(ConnectionStrings.Get("RedisConnectionString"));
            return new RedisCacheClient(muxer);
        }
    }
}
