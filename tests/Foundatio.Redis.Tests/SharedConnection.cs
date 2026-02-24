using System;
using Foundatio.Tests.Utility;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Foundatio.Redis.Tests;

public static class SharedConnection
{
    private static readonly object _lock = new();
    private static ConnectionMultiplexer _muxer;

    public static ConnectionMultiplexer GetMuxer(ILoggerFactory loggerFactory)
    {
        string connectionString = Configuration.GetConnectionString("RedisConnectionString");
        if (String.IsNullOrEmpty(connectionString))
            return null;

        if (_muxer is not null)
            return _muxer;

        lock (_lock)
        {
            if (_muxer is not null)
                return _muxer;

            _muxer = ConnectionMultiplexer.Connect(connectionString, o => o.LoggerFactory = loggerFactory);
            return _muxer;
        }
    }
}
