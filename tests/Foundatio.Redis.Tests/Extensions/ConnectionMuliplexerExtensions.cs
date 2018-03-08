using System;
using System.Threading.Tasks;
using StackExchange.Redis;

namespace Foundatio.Redis.Tests.Extensions {
    public static class ConnectionMuliplexerExtensions {
        public static async Task FlushAllAsync(this ConnectionMultiplexer muxer) {
            var endpoints = muxer.GetEndPoints(true);
            if (endpoints.Length == 0)
                return;

            int database = muxer.GetDatabase().Database;
            foreach (var endpoint in endpoints) {
                var server = muxer.GetServer(endpoint);
                if (!server.IsSlave) {
                    await server.FlushDatabaseAsync(database);
                }
            }
        }

        public static async Task<long> CountAllKeysAsync(this ConnectionMultiplexer muxer) {
            var endpoints = muxer.GetEndPoints(true);
            if (endpoints.Length == 0)
                return 0;

            int database = muxer.GetDatabase().Database;
            long count = 0;
            foreach (var endpoint in endpoints) {
                var server = muxer.GetServer(endpoint);
                count += await server.DatabaseSizeAsync(database);
            }

            return count;
        }
    }
}