using System;
using System.Configuration;
using System.Linq;
using System.Threading.Tasks;
using Foundatio.Elasticsearch.Tests.Repositories.Builders;
using Foundatio.Elasticsearch.Tests.Repositories.Configuration;
using Foundatio.Elasticsearch.Tests.Repositories.Models;
using Foundatio.Caching;
using Foundatio.Elasticsearch.Extensions;
using Foundatio.Elasticsearch.Repositories;
using Foundatio.Elasticsearch.Repositories.Queries.Builders;
using Foundatio.Elasticsearch.Tests.Extensions;
using Foundatio.Jobs;
using Foundatio.Queues;
using Foundatio.Utility;
using Nest;
using Xunit;

namespace Foundatio.Elasticsearch.Tests.Repositories {
    public class EmployeeRepositoryTests {
        private readonly InMemoryCacheClient _cache = new InMemoryCacheClient();
        private readonly IElasticClient _client;
        private readonly IQueue<WorkItemData> _workItemQueue = new InMemoryQueue<WorkItemData>();
        private readonly ElasticConfiguration _configuration;
        private readonly QueryBuilderRegistry _queryBuilder = new QueryBuilderRegistry();
        private readonly EmployeeRepository _repository;

        public EmployeeRepositoryTests() {
            _queryBuilder.RegisterDefaults();
            _queryBuilder.Register(new AgeQueryBuilder());

            _configuration = new ElasticConfiguration(_workItemQueue, _cache);
            _client = _configuration.GetClient(new[] { new Uri(ConfigurationManager.ConnectionStrings["ElasticConnectionString"].ConnectionString) });
            _repository = new EmployeeRepository(new ElasticRepositoryContext<Employee>(_cache, _client, _configuration, null, null, _queryBuilder));
        }

        [Fact]
        public async Task AddWithDefaultGeneratedIdAsync() {
            RemoveData();

            var employee = await _repository.AddAsync(EmployeeGenerator.Default);
            Assert.NotNull(employee?.Id);
            Assert.Equal(EmployeeGenerator.Default.Name, employee.Name);
            Assert.Equal(EmployeeGenerator.Default.Age, employee.Age);
            Assert.Equal(EmployeeGenerator.Default.CompanyName, employee.CompanyName);
            Assert.Equal(EmployeeGenerator.Default.CompanyId, employee.CompanyId);
        }

        [Fact]
        public async Task AddWithExistingIdAsync() {
            RemoveData();

            string id = ObjectId.GenerateNewId().ToString();
            var employee = await _repository.AddAsync(EmployeeGenerator.Generate(id));
            Assert.Equal(id, employee?.Id);
        }

        [Fact]
        public async Task SaveAsync() {
            RemoveData();

            var employee = await _repository.AddAsync(EmployeeGenerator.Default);
            Assert.NotNull(employee?.Id);
            Assert.Equal(EmployeeGenerator.Default.Name, employee.Name);

            employee.Name = Guid.NewGuid().ToString();

            var result = await _repository.SaveAsync(employee);
            Assert.Equal(employee.Name, result?.Name);
        }

        [Fact]
        public async Task AddDuplicateAsync() {
            RemoveData();

            string id = ObjectId.GenerateNewId().ToString();
            var employee = await _repository.AddAsync(EmployeeGenerator.Generate(id));
            Assert.Equal(id, employee?.Id);

            employee = await _repository.AddAsync(EmployeeGenerator.Generate(id));
            Assert.Equal(id, employee?.Id);
        }

        [Fact]
        public async Task SetCreatedAndModifiedTimesAsync() {
            RemoveData();

            DateTime nowUtc = DateTime.UtcNow;
            var employee = await _repository.AddAsync(EmployeeGenerator.Default);
            Assert.True(employee.CreatedUtc >= nowUtc);
            Assert.True(employee.UpdatedUtc >= nowUtc);

            DateTime createdUtc = employee.CreatedUtc;
            DateTime updatedUtc = employee.UpdatedUtc;

            employee.Name = Guid.NewGuid().ToString();
            employee = await _repository.SaveAsync(employee);
            Assert.Equal(createdUtc, employee.CreatedUtc);
            Assert.True(updatedUtc < employee.UpdatedUtc);
        }

        [Fact]
        public async Task CannotSetFutureCreatedAndModifiedTimesAsync() {
            RemoveData();

            var employee = await _repository.AddAsync(EmployeeGenerator.Generate(createdUtc: DateTime.MaxValue, updatedUtc: DateTime.MaxValue));
            Assert.True(employee.CreatedUtc != DateTime.MaxValue);
            Assert.True(employee.UpdatedUtc != DateTime.MaxValue);
            
            employee.CreatedUtc = DateTime.MaxValue;
            employee.UpdatedUtc = DateTime.MaxValue;

            employee = await _repository.SaveAsync(employee);
            Assert.True(employee.CreatedUtc != DateTime.MaxValue);
            Assert.True(employee.UpdatedUtc != DateTime.MaxValue);
        }

        [Fact]
        public async Task CanAddToCacheAsync() {
            RemoveData();

            Assert.Equal(0, _cache.Count);
            var employee = await _repository.AddAsync(EmployeeGenerator.Default, addToCache: true);
            Assert.Equal(1, _cache.Count);
            Assert.Equal(0, _cache.Hits);

            var cachedResult = await _repository.GetByIdAsync(employee.Id, useCache: true);
            Assert.Equal(1, _cache.Count);
            Assert.Equal(1, _cache.Hits);
            Assert.Equal(employee.ToJson(), cachedResult.ToJson());
        }

        [Fact]
        public async Task CanSaveToCacheAsync() {
            RemoveData();

            Assert.Equal(0, _cache.Count);
            var employee = await _repository.SaveAsync(EmployeeGenerator.Generate(ObjectId.GenerateNewId().ToString()), addToCache: true);
            Assert.Equal(1, _cache.Count);
            Assert.Equal(0, _cache.Hits);

            var cachedResult = await _repository.GetByIdAsync(employee.Id, useCache: true);
            Assert.Equal(1, _cache.Count);
            Assert.Equal(1, _cache.Hits);
            Assert.Equal(employee.ToJson(), cachedResult.ToJson());
        }
        
        [Fact]
        public async Task GetFromCacheAsync() {
            RemoveData();

            Assert.Equal(0, _cache.Count);
            var employee = await _repository.AddAsync(EmployeeGenerator.Default);
            Assert.Equal(0, _cache.Count);
            Assert.Equal(0, _cache.Hits);

            await _client.RefreshAsync();
            var cachedResult = await _repository.GetByIdAsync(employee.Id, useCache: true);
            Assert.Equal(1, _cache.Count);
            Assert.Equal(0, _cache.Hits);
            Assert.Equal(employee.ToJson(), cachedResult.ToJson());

            cachedResult = await _repository.GetByIdAsync(employee.Id, useCache: true);
            Assert.Equal(1, _cache.Count);
            Assert.Equal(1, _cache.Hits);
            Assert.Equal(employee.ToJson(), cachedResult.ToJson());
        }

        [Fact]
        public async Task GetByAgeAsync() {
            RemoveData();
            
            var employee19 = await _repository.AddAsync(EmployeeGenerator.Generate(age: 19));
            var employee20 = await _repository.AddAsync(EmployeeGenerator.Generate(age: 20));
            await _client.RefreshAsync();

            var result = await _repository.GetByAgeAsync(employee19.Age);
            Assert.Equal(employee19.ToJson(), result.ToJson());

            var results = await _repository.GetAllByAgeAsync(employee19.Age);
            Assert.Equal(1, results.Total);
            Assert.Equal(employee19.ToJson(), results.Documents.First().ToJson());
        }

        private void RemoveData() {

            _cache.RemoveAllAsync();
            _configuration.DeleteIndexes(_client);
            _configuration.ConfigureIndexes(_client);
        }
    }
}