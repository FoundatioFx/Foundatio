using System;
using Foundatio.Tests.Utility;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Foundatio.Redis.Tests;

public static class SharedConnection
{
    private static readonly object _lock = new();
    private static ConnectionMultiplexer _muxerResp2;
    private static ConnectionMultiplexer _muxerResp3;

    /// <summary>
    /// Returns a shared ConnectionMultiplexer for the given protocol. Two instances are cached (RESP2 and RESP3)
    /// so the test suite can run the same tests under both protocols without reconnecting.
    /// </summary>
    public static ConnectionMultiplexer GetMuxer(ILoggerFactory loggerFactory, RedisProtocol? protocol = null)
    {
        string connectionString = Configuration.GetConnectionString("RedisConnectionString");
        if (String.IsNullOrEmpty(connectionString))
            return null;

        bool useResp3 = protocol >= RedisProtocol.Resp3;
        ref ConnectionMultiplexer muxer = ref useResp3 ? ref _muxerResp3 : ref _muxerResp2;

        if (muxer is not null)
            return muxer;

        lock (_lock)
        {
            if (muxer is not null)
                return muxer;

            muxer = ConnectionMultiplexer.Connect(connectionString, o =>
            {
                o.LoggerFactory = loggerFactory;
                if (useResp3)
                    o.Protocol = RedisProtocol.Resp3;
            });
            return muxer;
        }
    }
}
