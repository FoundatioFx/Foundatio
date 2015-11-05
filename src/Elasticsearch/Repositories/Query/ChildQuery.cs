using System;

namespace Foundatio.Elasticsearch.Repositories.Queries {
    public class ChildQuery : QueryBuilderBase, ITypeQuery {
        public string Type { get; set; }
    }
}
