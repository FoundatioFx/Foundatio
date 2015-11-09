using FluentValidation;
using Foundatio.Caching;
using Foundatio.Elasticsearch.Configuration;
using Foundatio.Elasticsearch.Repositories.Queries.Builders;
using Foundatio.Messaging;
using Nest;

namespace Foundatio.Elasticsearch.Repositories {
    public class ElasticRepositoryContext<T> where T : class {
        public ElasticRepositoryContext(ICacheClient cache, IElasticClient elasticClient, ElasticConfigurationBase configuration, IMessagePublisher messagePublisher, IValidator<T> validator, QueryBuilderRegistry queryBuilder) {
            Cache = cache;
            ElasticClient = elasticClient;
            Configuration = configuration;
            Validator = validator;
            MessagePublisher = messagePublisher;
            QueryBuilder = queryBuilder;
        }

        public ICacheClient Cache { get; }
        public IElasticClient ElasticClient { get; }
        public ElasticConfigurationBase Configuration { get; }
        public IValidator<T> Validator { get; }
        public IMessagePublisher MessagePublisher { get; }
        public int BulkBatchSize { get; set; } = 1000;
        public QueryBuilderRegistry QueryBuilder { get;}
    }
}
