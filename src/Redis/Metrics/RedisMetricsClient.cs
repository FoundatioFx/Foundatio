using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Metrics;
using StackExchange.Redis;

namespace Foundatio.Redis.Metrics {
    public class RedisMetricsClient : IMetricsClient {
        private readonly IDatabase _db;
        private readonly string _prefix;
        private readonly long _epoch = new DateTime(1970, 1, 1, 0, 0, 0, 0).Ticks;

        public RedisMetricsClient(ConnectionMultiplexer connection, string prefix = null) {
            _db = connection.GetDatabase();
            _prefix = !String.IsNullOrEmpty(prefix) ? prefix + ":" : String.Empty;
        }

        public async Task CounterAsync(string statName, int value = 1) {
            string key = GetMinuteKeyName(String.Concat("count:", statName));
            var result = await _db.StringIncrementAsync(key, value).AnyContext();
            if (result == value)
                await _db.KeyExpireAsync(key, TimeSpan.FromHours(24)).AnyContext();
        }

        public async Task GaugeAsync(string statName, double value) {
            await _db.StringSetAsync(GetKeyName(String.Concat("gauge:", statName)), value).AnyContext();
        }

        public async Task TimerAsync(string statName, long milliseconds) {
            string countKey = GetMinuteKeyName(String.Concat("timing:", statName, "-count"));
            string durationKey = GetMinuteKeyName(String.Concat("timing:", statName, "-duration"));

            var result = await _db.StringIncrementAsync(countKey).AnyContext();
            if (result == 1)
                await _db.KeyExpireAsync(countKey, TimeSpan.FromHours(24)).AnyContext();

            result = await _db.StringIncrementAsync(durationKey, milliseconds).AnyContext();
            if (result == milliseconds)
                await _db.KeyExpireAsync(durationKey, TimeSpan.FromHours(24)).AnyContext();
        }

        public async Task<TimerStats> GetTimerAsync(string statName, DateTime start, DateTime end) {
            RedisKey[] countKeys = GetMinuteKeyNames(String.Concat("timing:", statName, "-count"), start, end).Select(k => (RedisKey)k).ToArray();
            RedisKey[] durationKeys = GetMinuteKeyNames(String.Concat("timing:", statName, "-duration"), start, end).Select(k => (RedisKey)k).ToArray();
            var countResults = await _db.StringGetAsync(countKeys).AnyContext();
            var durationResults = await _db.StringGetAsync(durationKeys).AnyContext();

            return new TimerStats {
                StatName = statName,
                Count = countResults.Sum(r => (long)r),
                TotalDuration = durationResults.Sum(r => (long)r)
            };
        }

        public async Task<long> GetCountAsync(string statName, DateTime start, DateTime end) {
            RedisKey[] keys = GetMinuteKeyNames(String.Concat("count:", statName), start, end).Select(k => (RedisKey)k).ToArray();
            var results = await _db.StringGetAsync(keys).AnyContext();
            return results.Sum(r => (long)r);
        }

        public async Task<double> GetGaugeValueAsync(string statName) {
            var result = await _db.StringGetAsync(GetKeyName(String.Concat("gauge:", statName))).AnyContext();
            return (double)result;
        }

        private string GetKeyName(string statName) {
            return String.Concat(_prefix, statName);
        }

        private string GetMinuteKeyName(string statName, DateTime? dateTime = null) {
            if (dateTime == null)
                dateTime = DateTime.UtcNow;

            return String.Concat(_prefix, statName, (dateTime.Value.Ticks - _epoch) / TimeSpan.TicksPerMinute);
        }

        private string[] GetMinuteKeyNames(string statName, DateTime start, DateTime end) {
            var startMinute = (start.Ticks - _epoch) / TimeSpan.TicksPerMinute;
            var endMinute = (end.Ticks - _epoch) / TimeSpan.TicksPerMinute;

            var keys = new List<string>();
            for (long minute = startMinute; minute <= endMinute; minute++)
                keys.Add(String.Concat(_prefix, statName, minute));

            return keys.ToArray();
        }

        public void Dispose() {}
    }

    public class TimerStats {
        public string StatName { get; set; }
        public long Count { get; set; }
        public long TotalDuration { get; set; }
        public double AverageDuration => (double)TotalDuration / (double)Count;
    }
}
