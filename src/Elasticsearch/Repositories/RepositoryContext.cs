using FluentValidation;
using Foundatio.Caching;
using Foundatio.Elasticsearch.Configuration;
using Foundatio.Messaging;
using Nest;

namespace Foundatio.Elasticsearch.Repositories {
    public class RepositoryContext<T> where T : class {
        public RepositoryContext(ICacheClient cache, IElasticClient elasticClient, ElasticsearchConfigurationBase configuration, IMessagePublisher messagePublisher, IValidator<T> validator) {
            Cache = cache;
            ElasticClient = elasticClient;
            Configuration = configuration;
            Validator = validator;
            MessagePublisher = messagePublisher;
        }

        public ICacheClient Cache { get; }
        public IElasticClient ElasticClient { get; }
        public ElasticsearchConfigurationBase Configuration { get; }
        public IValidator<T> Validator { get; }
        public IMessagePublisher MessagePublisher { get; }
        public int BulkBatchSize { get; set; } = 1000;
    }
}
