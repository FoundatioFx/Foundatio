using System;
using System.Configuration;
using System.Linq;
using System.Threading.Tasks;
using Exceptionless.DateTimeExtensions;
using Foundatio.Elasticsearch.Tests.Repositories.Builders;
using Foundatio.Elasticsearch.Tests.Repositories.Configuration;
using Foundatio.Elasticsearch.Tests.Repositories.Models;
using Foundatio.Caching;
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
        private readonly EmployeeWithDateIndex _employeeWithDateIndex = new EmployeeWithDateIndex();
        private readonly QueryBuilderRegistry _queryBuilder = new QueryBuilderRegistry();
        private readonly EmployeeRepository _repository;
        private readonly EmployeeWithDateBasedIndexRepository _repositoryWithDateBasedIndex;

        public EmployeeRepositoryTests() {
            _queryBuilder.RegisterDefaults();
            _queryBuilder.Register(new AgeQueryBuilder());

            _configuration = new ElasticConfiguration(_workItemQueue, _cache);
            _client = _configuration.GetClient(new[] { new Uri(ConfigurationManager.ConnectionStrings["ElasticConnectionString"].ConnectionString) });
            _repository = new EmployeeRepository(new ElasticRepositoryContext<Employee>(_cache, _client, _configuration, null, null, _queryBuilder));
            _repositoryWithDateBasedIndex = new EmployeeWithDateBasedIndexRepository(new ElasticRepositoryContext<EmployeeWithDate>(_cache, _client, _configuration, null, null, _queryBuilder), _employeeWithDateIndex);
        }
        
        [Fact]
        public async Task GetByDateBasedIndex() {
            await RemoveDataAsync();

            var indexes = await _client.GetIndicesPointingToAliasAsync(_employeeWithDateIndex.AliasName);
            Assert.Equal(0, indexes.Count);
            
            var alias = await _client.GetAliasAsync(descriptor => descriptor.Alias(_employeeWithDateIndex.AliasName));
            Assert.False(alias.IsValid);
            Assert.Equal(0, alias.Indices.Count);

            var employee = await _repositoryWithDateBasedIndex.AddAsync(EmployeeWithDateGenerator.Default);
            Assert.NotNull(employee?.Id);
            
            employee = await _repositoryWithDateBasedIndex.AddAsync(EmployeeWithDateGenerator.Generate(startDate: DateTimeOffset.Now.SubtractMonths(1)));
            Assert.NotNull(employee?.Id);

            await _client.RefreshAsync();
            alias = await _client.GetAliasAsync(descriptor => descriptor.Alias(_employeeWithDateIndex.AliasName));
            Assert.True(alias.IsValid);
            Assert.Equal(2, alias.Indices.Count);
            
            indexes = await _client.GetIndicesPointingToAliasAsync(_employeeWithDateIndex.AliasName);
            Assert.Equal(2, indexes.Count);
        }

        [Fact]
        public async Task AddWithDefaultGeneratedIdAsync() {
            await RemoveDataAsync();

            var employee = await _repository.AddAsync(EmployeeGenerator.Default);
            Assert.NotNull(employee?.Id);
            Assert.Equal(EmployeeGenerator.Default.Name, employee.Name);
            Assert.Equal(EmployeeGenerator.Default.Age, employee.Age);
            Assert.Equal(EmployeeGenerator.Default.CompanyName, employee.CompanyName);
            Assert.Equal(EmployeeGenerator.Default.CompanyId, employee.CompanyId);
        }

        [Fact]
        public async Task AddWithExistingIdAsync() {
            await RemoveDataAsync();

            string id = ObjectId.GenerateNewId().ToString();
            var employee = await _repository.AddAsync(EmployeeGenerator.Generate(id));
            Assert.Equal(id, employee?.Id);
        }

        [Fact]
        public async Task SaveAsync() {
            await RemoveDataAsync();

            var employee = await _repository.AddAsync(EmployeeGenerator.Default);
            Assert.NotNull(employee?.Id);
            Assert.Equal(EmployeeGenerator.Default.Name, employee.Name);

            employee.Name = Guid.NewGuid().ToString();

            var result = await _repository.SaveAsync(employee);
            Assert.Equal(employee.Name, result?.Name);
        }

        [Fact]
        public async Task AddDuplicateAsync() {
            await RemoveDataAsync();

            string id = ObjectId.GenerateNewId().ToString();
            var employee = await _repository.AddAsync(EmployeeGenerator.Generate(id));
            Assert.Equal(id, employee?.Id);

            employee = await _repository.AddAsync(EmployeeGenerator.Generate(id));
            Assert.Equal(id, employee?.Id);
        }

        [Fact]
        public async Task SetCreatedAndModifiedTimesAsync() {
            await RemoveDataAsync();

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
            await RemoveDataAsync();

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
        public async Task CanGetByIds() {
            var employee = await _repository.AddAsync(EmployeeGenerator.Generate());
            Assert.NotNull(employee.Id);

            var result = await _repository.GetByIdAsync(employee.Id);
            Assert.NotNull(result);
            Assert.Equal(employee.Id, result.Id);
            
            var employee2 = await _repository.AddAsync(EmployeeGenerator.Generate());
            Assert.NotNull(employee2.Id);

            var results = await _repository.GetByIdsAsync(new [] { employee.Id, employee2.Id });
            Assert.NotNull(results);
            Assert.Equal(2, results.Total);
        }

        [Fact]
        public async Task CanAddToCacheAsync() {
            await RemoveDataAsync();

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
            await RemoveDataAsync();

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
            await RemoveDataAsync();

            Assert.Equal(0, _cache.Count);
            var employee = await _repository.AddAsync(EmployeeGenerator.Default);
            Assert.Equal(0, _cache.Count);
            Assert.Equal(0, _cache.Hits);
            
            var cachedResult = await _repository.GetByIdAsync(employee.Id, useCache: true);
            Assert.NotNull(cachedResult);
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
            await RemoveDataAsync();
            
            var employee19 = await _repository.AddAsync(EmployeeGenerator.Generate(age: 19));
            var employee20 = await _repository.AddAsync(EmployeeGenerator.Generate(age: 20));
            await _client.RefreshAsync();

            var result = await _repository.GetByAgeAsync(employee19.Age);
            Assert.Equal(employee19.ToJson(), result.ToJson());

            var results = await _repository.GetAllByAgeAsync(employee19.Age);
            Assert.Equal(1, results.Total);
            Assert.Equal(employee19.ToJson(), results.Documents.First().ToJson());
        }

        private async Task RemoveDataAsync() {
            await _cache.RemoveAllAsync();
            _configuration.DeleteIndexes(_client);
            _configuration.ConfigureIndexes(_client);
            await _client.RefreshAsync();
        }
    }
}