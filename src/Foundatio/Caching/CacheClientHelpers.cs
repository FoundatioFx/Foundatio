using System;
using System.Threading.Tasks;

namespace Foundatio.Caching
{
    public static class CacheClientHelpers
    {
        public static async Task<CacheValue<T>> GetOrAddAsync<T>(this ICacheClient client, string key, Func<T> addFunc,
            TimeSpan? expiresIn = null)
        {
            var cachedValue = await client.GetAsync<T>(key);
            if (cachedValue.HasValue) return cachedValue;

            var value = addFunc();

            var addResult = await client.AddAsync(key, value, expiresIn);
            return addResult ? new CacheValue<T>(value, true) : CacheValue<T>.NoValue;
        }
    }
}
