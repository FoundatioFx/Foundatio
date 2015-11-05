using System.Collections.Generic;
using Foundatio.Repositories.Queries;
using Nest;

namespace Foundatio.Elasticsearch.Repositories.Queries {
    public class ParentQuery : QueryBuilderBase, IIdentityQuery, ITypeQuery {
        public ParentQuery() {
            Ids = new List<string>();
        }

        public List<string> Ids { get; }
        public string Type { get; set; }

        protected override FilterContainer ApplyFilter(FilterContainer container, bool supportSoftDeletes) {
            if (container == null)
                container = new MatchAllFilter();

            if (Ids.Count > 0)
                container &= new IdsFilter { Values = Ids };

            return base.ApplyFilter(container, supportSoftDeletes);
        }
    }
}
