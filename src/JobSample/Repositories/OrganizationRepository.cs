using System;
using Foundatio.Elasticsearch.Repositories;
using Foundatio.Elasticsearch.Repositories.Queries;
using Foundatio.Elasticsearch.Repositories.Queries.Builders;
using Foundatio.Repositories.Models;

namespace Foundatio.JobSample.Repositories {
    public abstract class MyAppRepositoryBase<T> : ElasticRepositoryBase<T> where T : class, IIdentity, new() {
        public MyAppRepositoryBase(ElasticRepositoryContext<T> context) : base(context) { }
    }

    public class OrganizationRepository : MyAppRepositoryBase<Organization> {
        public OrganizationRepository(ElasticRepositoryContext<Organization> context) : base(context) {}
    }

    public class Organization : IIdentity {
        public string Id { get; set; }
        public int Age { get; set; }
    }

    public interface IAgeQuery {
        int Age { get; set; }
    }

    public class MyQuery : ElasticQuery, IAgeQuery {
        public int Age { get; set; }
    }

    public class AgeQueryBuilder : QueryBuilderBase {
    }
}
