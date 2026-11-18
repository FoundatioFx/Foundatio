using System;
using System.Threading;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Foundatio.Redis.Utility;

internal sealed class RedisCapabilities
{
    private static readonly Version LMoveMinVersion = new(6, 2, 0);

    // Redis 8.4 shipped MSETEX, DELEX ... IFEQ, and SET ... IFEQ together. Valkey ships equivalent
    // commands under its own version numbering, and DELEX has no Valkey counterpart SE.Redis can call
    // (Valkey's compare-and-delete is the differently-shaped DELIFEQ key value, not DELEX key IFEQ value).
    private static readonly Version RedisCas84MinVersion = new(8, 4, 0);
    private static readonly Version ValkeySetIfEqMinVersion = new(8, 1, 0);
    private static readonly Version ValkeyMsetexMinVersion = new(9, 1, 0);

    private readonly IConnectionMultiplexer _muxer;
    private readonly ILogger _logger;

    private int _compareAndDelete; // 0 = unknown, 1 = supported, -1 = not supported
    private int _compareAndSwap; // 0 = unknown, 1 = supported, -1 = not supported
    private int _lMove; // 0 = unknown, 1 = supported, -1 = not supported
    private int _msetex; // 0 = unknown, 1 = supported, -1 = not supported

    public RedisCapabilities(IConnectionMultiplexer muxer, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(muxer);
        ArgumentNullException.ThrowIfNull(logger);

        _muxer = muxer;
        _logger = logger;
    }

    /// <summary>
    /// LMOVE requires Redis 6.2+. Callers should fall back to the (functionally identical) RPOPLPUSH
    /// variant when this returns <c>false</c>, e.g. against Azure Cache for Redis, which is pinned at 6.0.x.
    /// </summary>
    public bool SupportsLMove => CheckVersion(ref _lMove, LMoveMinVersion, "LMOVE");

    /// <summary>
    /// MSETEX shipped in Redis 8.4 and, under the same name and syntax, in Valkey 9.1.
    /// </summary>
    public bool SupportsMsetex => CheckVendorFeature(ref _msetex, "MSETEX", RedisCas84MinVersion, ValkeyMsetexMinVersion);

    /// <summary>
    /// <c>DELEX key IFEQ expected</c> (compare-and-delete, via SE.Redis's <see cref="ValueCondition"/>) shipped
    /// in Redis 8.4. Valkey has an equivalent (<c>DELIFEQ key expected</c>, since Valkey 9.0), but it's a
    /// differently-named/shaped command that SE.Redis's <c>ValueCondition</c> API does not target, so it can't
    /// be used here.
    /// </summary>
    public bool SupportsCompareAndDelete => CheckVendorFeature(ref _compareAndDelete, "DELEX IFEQ (compare-and-delete)", RedisCas84MinVersion, valkeyMinVersion: null);

    /// <summary>
    /// <c>SET key value IFEQ expected</c> (compare-and-swap, via SE.Redis's <see cref="ValueCondition"/>) shipped
    /// in Redis 8.4 and, under the same name and syntax, in Valkey 8.1.
    /// </summary>
    public bool SupportsCompareAndSwap => CheckVendorFeature(ref _compareAndSwap, "SET IFEQ (compare-and-swap)", RedisCas84MinVersion, ValkeySetIfEqMinVersion);

    public void Invalidate()
    {
        Volatile.Write(ref _compareAndDelete, 0);
        Volatile.Write(ref _compareAndSwap, 0);
        Volatile.Write(ref _lMove, 0);
        Volatile.Write(ref _msetex, 0);
    }

    /// <summary>
    /// Checks a feature that's gated purely by version, using the same threshold across every server product.
    /// This relies on <see cref="IServer.Version"/> (Redis's own <c>redis_version</c> compatibility field), which
    /// forks pin at a fixed value (e.g. Valkey always reports 7.2.4) rather than tracking their real releases -
    /// safe here because every current fork's pinned value already clears old thresholds like LMOVE's 6.2.0.
    /// </summary>
    private bool CheckVersion(ref int cached, Version minVersion, string featureName) =>
        CheckSupport(ref cached, featureName, server => server.Version >= minVersion);

    /// <summary>
    /// Checks a feature whose availability and/or minimum version genuinely differs per server product, using
    /// <see cref="IServer.GetProductVariant"/> to read each product's real, independently-tracked version
    /// instead of the pinned-compatibility <see cref="IServer.Version"/>.
    /// </summary>
    private bool CheckVendorFeature(ref int cached, string featureName, Version redisMinVersion, Version? valkeyMinVersion) =>
        CheckSupport(ref cached, featureName, server =>
        {
            var variant = server.GetProductVariant(out string versionString);
            return variant switch
            {
                ProductVariant.Redis => server.Version >= redisMinVersion,
                ProductVariant.Valkey => valkeyMinVersion is not null && Version.TryParse(versionString, out var valkeyVersion) && valkeyVersion >= valkeyMinVersion,
                _ => false,
            };
        });

    private bool CheckSupport(ref int cached, string featureName, Func<IServer, bool> isSupported)
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
                if (!isSupported(server))
                {
                    _logger.LogDebug("{Feature}: Server {Endpoint} does not support feature", featureName, endpoint);
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
