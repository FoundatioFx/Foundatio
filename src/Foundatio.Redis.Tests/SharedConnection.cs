using System;
using Foundatio.Tests.Utility;
using StackExchange.Redis;

namespace Foundatio.Redis.Tests {
    public static class SharedConnection {
        private static ConnectionMultiplexer _muxer;

        public static ConnectionMultiplexer GetMuxer() {
            if (String.IsNullOrEmpty(ConnectionStrings.Get("RedisConnectionString")))
                return null;

            if (_muxer == null)
                _muxer = ConnectionMultiplexer.Connect(ConnectionStrings.Get("RedisConnectionString"));
           
            _muxer.PreserveAsyncOrder = false;

            return _muxer;
        }
    }
}
