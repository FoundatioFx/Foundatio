using System;
using Foundatio.Repositories.Queries;
using Nest;

namespace Foundatio.Elasticsearch.Repositories.Queries.Builders {
    public class PagableQueryBuilder : QueryBuilderBase {
        public override void BuildSearch<T>(object query, object options, ref SearchDescriptor<T> descriptor) {
            var pagableQuery = query as IPagableQuery;
            if (pagableQuery == null)
                return;

            // add 1 to limit if not auto paging so we can know if we have more results
            if (pagableQuery.ShouldUseLimit())
                descriptor.Size(pagableQuery.GetLimit() + (!pagableQuery.UseSnapshotPaging ? 1 : 0));
            if (pagableQuery.ShouldUseSkip())
                descriptor.Skip(pagableQuery.GetSkip());
        }
    }
}
