using System;
using System.Collections.Generic;
using Foundatio.Elasticsearch.Repositories.Queries;

namespace Foundatio.Elasticsearch.Tests.Repositories.Queries {
    public interface IAgeQuery {
        List<int> Ages { get; set; }
    }

    public class AgeQuery : ElasticQuery, IAgeQuery {
        public List<int> Ages { get; set; }
    }
}
