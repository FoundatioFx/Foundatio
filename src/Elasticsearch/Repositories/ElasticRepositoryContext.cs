using FluentValidation;
using Foundatio.Caching;
using Foundatio.Elasticsearch.Configuration;
using Foundatio.Messaging;
using Nest;

namespace Foundatio.Elasticsearch.Repositories {
    public class ElasticRepositoryContext<T> where T : class {
        public ElasticRepositoryContext(ICacheClient cache, IElasticClient elasticClient, ElasticConfigurationBase configuration, IMessagePublisher messagePublisher, IValidator<T> validator) {
            Cache = cache;
            ElasticClient = elasticClient;
            Configuration = configuration;
            Validator = validator;
            MessagePublisher = messagePublisher;
        }

        public ICacheClient Cache { get; }
        public IElasticClient ElasticClient { get; }
        public ElasticConfigurationBase Configuration { get; }
        public IValidator<T> Validator { get; }
        public IMessagePublisher MessagePublisher { get; }
        public int BulkBatchSize { get; set; } = 1000;
    }
}
