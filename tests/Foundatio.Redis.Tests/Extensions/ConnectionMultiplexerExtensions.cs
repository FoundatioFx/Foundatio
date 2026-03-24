using System.Linq;
using System.Threading.Tasks;
using StackExchange.Redis;

namespace Foundatio.Redis.Tests.Extensions;

public static class ConnectionMultiplexerExtensions
{
    public static async Task FlushAllAsync(this ConnectionMultiplexer muxer)
    {
        int database = muxer.GetDatabase().Database;
        foreach (var server in muxer.GetEndPoints().Select(ep => muxer.GetServer(ep)).Where(s => !s.IsReplica))
            await server.FlushDatabaseAsync(database);
    }

    public static async Task<long> CountAllKeysAsync(this ConnectionMultiplexer muxer)
    {
        int database = muxer.GetDatabase().Database;
        long count = 0;
        foreach (var server in muxer.GetEndPoints().Select(ep => muxer.GetServer(ep)).Where(s => !s.IsReplica))
            count += await server.DatabaseSizeAsync(database);

        return count;
    }
}
