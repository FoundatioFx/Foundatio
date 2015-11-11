using System;
using Foundatio.Elasticsearch.Repositories;
using Foundatio.Repositories.Models;

namespace Foundatio.Elasticsearch.Tests.Repositories {
    public abstract class AppRepositoryBase<T> : ElasticRepositoryBase<T> where T : class, IIdentity, new() {
        public AppRepositoryBase(ElasticRepositoryContext<T> context) : base(context) { }
    }
}