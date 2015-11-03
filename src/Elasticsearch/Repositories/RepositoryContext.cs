using FluentValidation;
using Foundatio.Caching;
using Foundatio.Messaging;
using Foundatio.Repositories.Configuration;
using Nest;

namespace Foundatio.Repositories {
    public class RepositoryContext<T> where T : class {
        public RepositoryContext(IElasticClient elasticClient, ElasticSearchConfiguration configuration, ICacheClient cache, IMessagePublisher messagePublisher, IValidator<T> validator) {
            ElasticClient = elasticClient;
            Configuration = configuration;
            Validator = validator;
            Cache = cache;
            MessagePublisher = messagePublisher;
        }

        public IElasticClient ElasticClient { get; }
        public ElasticSearchConfiguration Configuration { get; }
        public IValidator<T> Validator { get; }
        public ICacheClient Cache { get; }
        public IMessagePublisher MessagePublisher { get; }
    }
}
