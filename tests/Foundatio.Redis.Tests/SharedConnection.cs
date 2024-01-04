using System;
using Foundatio.Tests.Utility;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Foundatio.Redis.Tests {
    public static class SharedConnection {
        private static ConnectionMultiplexer _muxer;

        public static ConnectionMultiplexer GetMuxer(ILoggerFactory loggerFactory) {
            string connectionString = Configuration.GetConnectionString("RedisConnectionString");
            if (String.IsNullOrEmpty(connectionString))
                return null;

            if (_muxer == null)
                _muxer = ConnectionMultiplexer.Connect(connectionString, o => o.LoggerFactory = loggerFactory);

            return _muxer;
        }
    }
}
