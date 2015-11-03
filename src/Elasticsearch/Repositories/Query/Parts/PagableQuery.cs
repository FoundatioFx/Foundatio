using Foundatio.Repositories;
using Foundatio.Repositories.Models;

namespace Foundatio.Elasticsearch.Repositories {
    public interface IPagableQuery {
        int? Limit { get; set; }
        int? Page { get; set; }
        bool UseSnapshotPaging { get; set; }
    }

    public static class PagableQueryExtensions {
        public static bool ShouldUseLimit<T>(this T query) where T : IPagableQuery {
            return query.Limit.HasValue;
        }

        public static bool ShouldUseSkip<T>(this T query) where T : IPagableQuery {
            return query.Page.HasValue && query.Page.Value > 1;
        }

        public static int GetLimit<T>(this T query) where T : IPagableQuery {
            if (!query.Limit.HasValue || query.Limit.Value < 1)
                return RepositoryConstants.DEFAULT_LIMIT;

            if (query.Limit.Value > RepositoryConstants.MAX_LIMIT)
                return RepositoryConstants.MAX_LIMIT;

            return query.Limit.Value;
        }

        public static int GetSkip<T>(this T query) where T : IPagableQuery {
            if (!query.Page.HasValue || query.Page.Value < 1)
                return 0;

            int skip = (query.Page.Value - 1) * query.GetLimit();
            if (skip < 0)
                skip = 0;

            return skip;
        }

        public static T WithLimit<T>(this T options, int? limit) where T : IPagableQuery {
            options.Limit = limit;
            return options;
        }

        public static T WithPage<T>(this T query, int? page) where T : IPagableQuery {
            query.Page = page;
            return query;
        }

        public static T WithSnapshotPaging<T>(this T query, bool useSnapshotPaging = true) where T : IPagableQuery {
            query.UseSnapshotPaging = useSnapshotPaging;
            return query;
        }

        public static T WithPaging<T>(this T query, PagingOptions paging) where T : IPagableQuery {
            if (paging == null)
                return query;

            query.Page = paging.Page;
            query.Limit = paging.Limit;
            query.UseSnapshotPaging = paging.UseSnapshotPaging;

            return query;
        }
    }
}
