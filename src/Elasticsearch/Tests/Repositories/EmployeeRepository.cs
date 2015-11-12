using System;
using System.Threading.Tasks;
using Foundatio.Elasticsearch.Tests.Repositories.Models;
using Foundatio.Elasticsearch.Repositories;
using Foundatio.Elasticsearch.Tests.Repositories.Queries;
using Foundatio.Repositories.Models;

namespace Foundatio.Elasticsearch.Tests.Repositories {
    public class EmployeeRepository : AppRepositoryBase<Employee> {
        public EmployeeRepository(ElasticRepositoryContext<Employee> context) : base(context) { }

        public Task<Employee> GetByAgeAsync(int age) {
            return FindOneAsync(new AgeQuery().WithAge(age));
        }

        public Task<FindResults<Employee>> GetAllByAgeAsync(int age) {
            return FindAsync(new AgeQuery().WithAge(age));
        }
    }
}