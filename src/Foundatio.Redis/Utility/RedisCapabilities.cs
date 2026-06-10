using System;
using System.Threading;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Foundatio.Redis.Utility;

internal sealed class RedisCapabilities
{
    private static readonly Version MsetexMinVersion = new(8, 3, 224);

    private readonly IConnectionMultiplexer _muxer;
    private readonly ILogger _logger;

    private int _msetex; // 0 = unknown, 1 = supported, -1 = not supported

    public RedisCapabilities(IConnectionMultiplexer muxer, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(muxer);
        ArgumentNullException.ThrowIfNull(logger);

        _muxer = muxer;
        _logger = logger;
    }

    public bool SupportsMsetex => CheckVersion(ref _msetex, MsetexMinVersion, "MSETEX");

    public void Invalidate()
    {
        Volatile.Write(ref _msetex, 0);
    }

    private bool CheckVersion(ref int cached, Version minVersion, string featureName)
    {
        int value = Volatile.Read(ref cached);
        if (value is not 0)
            return value > 0;

        var endpoints = _muxer.GetEndPoints();
        if (endpoints is { Length: 0 })
        {
            _logger.LogDebug("{Feature}: No endpoints configured, feature not available", featureName);
            return false;
        }

        bool foundConnectedPrimary = false;
        foreach (var endpoint in endpoints)
        {
            var server = _muxer.GetServer(endpoint);
            if (server.IsConnected && !server.IsReplica)
            {
                foundConnectedPrimary = true;
                if (server.Version < minVersion)
                {
                    _logger.LogDebug("{Feature}: Server {Endpoint} version {Version} does not support feature (requires {MinVersion}+)",
                        featureName, endpoint, server.Version, minVersion);
                    Volatile.Write(ref cached, -1);
                    return false;
                }
            }
        }

        if (foundConnectedPrimary)
        {
            Volatile.Write(ref cached, 1);
            return true;
        }

        _logger.LogDebug("{Feature}: No connected primaries found, feature availability unknown", featureName);
        return false;
    }
}
