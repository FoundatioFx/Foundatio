using System;
using StackExchange.Redis;

namespace Foundatio.Redis.Utility;

internal static class RedisOptionsValidation
{
    internal static void ValidateReadMode(CommandFlags flags)
    {
        if (flags is not (CommandFlags.None or CommandFlags.PreferReplica or CommandFlags.PreferMaster or CommandFlags.DemandReplica or CommandFlags.DemandMaster))
            throw new ArgumentException($"ReadMode only accepts routing flags (None, PreferReplica, PreferMaster, DemandReplica, DemandMaster). Got: {flags}", nameof(flags));
    }
}
