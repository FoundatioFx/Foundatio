using System;
using Foundatio.Elasticsearch.Tests.Repositories.Models;
using Foundatio.Elasticsearch.Repositories;

namespace Foundatio.Elasticsearch.Tests.Repositories {
    public class EmployeeRepository : AppRepositoryBase<Employee> {
        public EmployeeRepository(ElasticRepositoryContext<Employee> context) : base(context) { }
    }
}