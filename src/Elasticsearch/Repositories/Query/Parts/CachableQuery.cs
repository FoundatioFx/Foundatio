using System;
using Foundatio.Repositories;

namespace Foundatio.Elasticsearch.Repositories.Queries {
    public interface ICachableQuery {
        string CacheKey { get; set; }
        TimeSpan? ExpiresIn { get; set; }
        DateTime? ExpiresAt { get; set; }
    }

    public static class CachableQueryExtensions {
        public static T WithCacheKey<T>(this T query, string cacheKey) where T : ICachableQuery {
            query.CacheKey = cacheKey;
            return query;
        }

        public static T WithExpiresAt<T>(this T query, DateTime? expiresAt) where T : ICachableQuery {
            query.ExpiresAt = expiresAt;
            return query;
        }

        public static T WithExpiresIn<T>(this T query, TimeSpan? expiresIn) where T : ICachableQuery {
            query.ExpiresIn = expiresIn;
            return query;
        }

        public static DateTime GetCacheExpirationDateUtc<T>(this T query) where T : ICachableQuery {
            if (query.ExpiresAt.HasValue && query.ExpiresAt.Value < DateTime.UtcNow)
                throw new ArgumentException("ExpiresAt can't be in the past.");

            if (query.ExpiresAt.HasValue)
                return query.ExpiresAt.Value;

            if (query.ExpiresIn.HasValue)
                return DateTime.UtcNow.Add(query.ExpiresIn.Value);

            return DateTime.UtcNow.AddSeconds(RepositoryConstants.DEFAULT_CACHE_EXPIRATION_SECONDS);
        }

        public static bool ShouldUseCache<T>(this T query) where T : ICachableQuery => !String.IsNullOrEmpty(query.CacheKey);
    }
}
