using FluentValidation;
using Foundatio.Caching;
using Foundatio.Elasticsearch.Configuration;
using Foundatio.Messaging;
using Nest;

namespace Foundatio.Repositories {
    public class RepositoryContext<T> where T : class {
        public RepositoryContext(IElasticClient elasticClient, ElasticsearchConfiguration configuration, ICacheClient cache, IMessagePublisher messagePublisher, IValidator<T> validator) {
            ElasticClient = elasticClient;
            Configuration = configuration;
            Validator = validator;
            Cache = cache;
            MessagePublisher = messagePublisher;
        }

        public IElasticClient ElasticClient { get; }
        public ElasticsearchConfiguration Configuration { get; }
        public IValidator<T> Validator { get; }
        public ICacheClient Cache { get; }
        public IMessagePublisher MessagePublisher { get; }
        public int BulkBatchSize { get; set; } = 1000;
    }
}
