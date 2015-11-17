using System;
using System.Collections.Generic;
using Foundatio.Caching;
using Foundatio.Elasticsearch.Configuration;
using Foundatio.Jobs;
using Foundatio.Queues;

namespace Foundatio.Elasticsearch.Tests.Repositories.Configuration {
    public class ElasticConfiguration : ElasticConfigurationBase {
        public ElasticConfiguration(IQueue<WorkItemData> workItemQueue, ICacheClient cacheClient) : base(workItemQueue, cacheClient) {}

        protected override IEnumerable<IElasticIndex> GetIndexes() {
            return new IElasticIndex[] {
                new EmployeeIndex(),
                new EmployeeWithDateIndex()
            };
        }
    }
}