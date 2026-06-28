using System;
using Foundatio.Caching;
using Foundatio.Extensions;
using Foundatio.Jobs;
using Foundatio.Lock;
using Foundatio.Messaging;
using Foundatio.Queues;
using Foundatio.Resilience;
using Foundatio.Serializer;
using Foundatio.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Foundatio;

public static class FoundatioServicesExtensions
{
    /// <summary>
    /// Adds and configures Foundatio services.
    /// </summary>
    /// <param name="services"></param>
    /// <returns></returns>
    public static FoundatioBuilder AddFoundatio(this IServiceCollection services)
    {
        return new FoundatioBuilder(services);
    }
}

public class FoundatioBuilder : IFoundatioBuilder
{
    private readonly IServiceCollection _services;

    internal FoundatioBuilder(IServiceCollection services)
    {
        _services = services;
        Caching = new CachingBuilder(this);
        Storage = new StorageBuilder(this);
        Messaging = new MessagingBuilder(this);
        Queueing = new QueueingBuilder(this);
        Jobs = new JobsBuilder(this);
        Locking = new LockingBuilder(this);
    }

    IServiceCollection IFoundatioBuilder.Services => _services;
    FoundatioBuilder IFoundatioBuilder.Builder => this;

    /// <summary>
    /// Configure caching services for Foundatio.
    /// </summary>
    public CachingBuilder Caching { get; }

    /// <summary>
    /// Configure storage services for Foundatio.
    /// </summary>
    public StorageBuilder Storage { get; }

    /// <summary>
    /// Configure messaging services for Foundatio.
    /// </summary>
    public MessagingBuilder Messaging { get; }

    /// <summary>
    /// Configure queueing services for Foundatio.
    /// </summary>
    public QueueingBuilder Queueing { get; }

    /// <summary>
    /// Configure background job runtime services for Foundatio.
    /// </summary>
    public JobsBuilder Jobs { get; }

    /// <summary>
    /// Configure locking services for Foundatio.
    /// </summary>
    public LockingBuilder Locking { get; }

    /// <summary>
    /// Configure resilience services for Foundatio.
    /// </summary>
    /// <param name="policyProvider"></param>
    /// <returns></returns>
    public FoundatioBuilder AddResilience(IResiliencePolicyProvider policyProvider)
    {
        _services.AddSingleton(policyProvider);
        return this;
    }

    /// <summary>
    /// Configure resilience services for Foundatio.
    /// </summary>
    /// <param name="factory"></param>
    /// <returns></returns>
    public FoundatioBuilder AddResilience(Func<IServiceProvider, IResiliencePolicyProvider> factory)
    {
        _services.AddSingleton(factory);
        return this;
    }

    /// <summary>
    /// Configure resilience services for Foundatio.
    /// </summary>
    /// <param name="builder"></param>
    /// <returns></returns>
    public FoundatioBuilder AddResilience(Action<ResiliencePolicyProviderBuilder>? builder = null)
    {
        _services.AddSingleton<IResiliencePolicyProvider>(sp =>
        {
            var provider = new ResiliencePolicyProviderBuilder(sp.GetService<TimeProvider>(), sp.GetService<ILoggerFactory>());
            builder?.Invoke(provider);
            return provider.Build();
        });

        return this;
    }

    /// <summary>
    /// Configure serializer used by Foundatio.
    /// </summary>
    /// <param name="textSerializerFactory">The serializer to use.</param>
    /// <param name="serializerFactory">The serializer to use. Defaults to the ITextSerializer instance</param>
    /// <returns></returns>
    public FoundatioBuilder AddSerializer(Func<IServiceProvider, ITextSerializer> textSerializerFactory, Func<IServiceProvider, ISerializer>? serializerFactory = null)
    {
        _services.ReplaceSingleton(textSerializerFactory);
        _services.ReplaceSingleton(serializerFactory ?? (sp => sp.GetRequiredService<ITextSerializer>()));
        return this;
    }

    /// <summary>
    /// Configure serializer used by Foundatio.
    /// </summary>
    /// <param name="textSerializer">The serializer to use.</param>
    /// <param name="serializer">The serializer to use. Defaults to the ITextSerializer instance</param>
    /// <returns></returns>
    public FoundatioBuilder AddSerializer(ITextSerializer textSerializer, ISerializer? serializer = null)
    {
        _services.ReplaceSingleton(_ => textSerializer);

        if (serializer != null)
            _services.ReplaceSingleton(_ => serializer);
        else
            _services.ReplaceSingleton(sp => sp.GetRequiredService<ITextSerializer>());

        return this;
    }

    public class CachingBuilder : IFoundatioBuilder
    {
        private readonly FoundatioBuilder _builder;
        private readonly IServiceCollection _services;

        internal CachingBuilder(IFoundatioBuilder builder)
        {
            _builder = builder.Builder;
            _services = builder.Services;
        }

        IServiceCollection IFoundatioBuilder.Services => _services;
        FoundatioBuilder IFoundatioBuilder.Builder => _builder;

        public FoundatioBuilder Use(ICacheClient storage)
        {
            _services.ReplaceSingleton(_ => storage);
            return _builder;
        }

        public FoundatioBuilder Use(Func<IServiceProvider, ICacheClient> factory)
        {
            _services.ReplaceSingleton(factory);
            return _builder;
        }

        public FoundatioBuilder UseInMemory(InMemoryCacheClientOptions? options = null)
        {
            _services.ReplaceSingleton<ICacheClient>(sp => new InMemoryCacheClient(options.UseServices(sp)));
            return _builder;
        }

        public FoundatioBuilder UseInMemory(Builder<InMemoryCacheClientOptionsBuilder, InMemoryCacheClientOptions> config)
        {
            _services.ReplaceSingleton<ICacheClient>(sp => new InMemoryCacheClient(b => b.Configure(config).UseServices(sp)));
            return _builder;
        }
    }

    public class StorageBuilder : IFoundatioBuilder
    {
        private readonly FoundatioBuilder _builder;
        private readonly IServiceCollection _services;

        internal StorageBuilder(IFoundatioBuilder builder)
        {
            _builder = builder.Builder;
            _services = builder.Services;
        }

        IServiceCollection IFoundatioBuilder.Services => _services;
        FoundatioBuilder IFoundatioBuilder.Builder => _builder;

        public FoundatioBuilder Use(IFileStorage storage)
        {
            _services.ReplaceSingleton(_ => storage);
            return _builder;
        }

        public FoundatioBuilder Use(Func<IServiceProvider, IFileStorage> factory)
        {
            _services.ReplaceSingleton(factory);
            return _builder;
        }

        public FoundatioBuilder UseInMemory(InMemoryFileStorageOptions? options = null)
        {
            _services.ReplaceSingleton<IFileStorage>(sp => new InMemoryFileStorage(options.UseServices(sp)));
            return _builder;
        }

        public FoundatioBuilder UseInMemory(Builder<InMemoryFileStorageOptionsBuilder, InMemoryFileStorageOptions> config)
        {
            _services.ReplaceSingleton<IFileStorage>(sp => new InMemoryFileStorage(b => b.Configure(config).UseServices(sp)));
            return _builder;
        }

        public FoundatioBuilder UseFolder(string folder)
        {
            _services.ReplaceSingleton<IFileStorage>(sp => new FolderFileStorage(b => b.UseServices(sp).Folder(folder)));
            return _builder;
        }

        public FoundatioBuilder UseFolder(FolderFileStorageOptions? options = null)
        {
            _services.ReplaceSingleton<IFileStorage>(sp => new FolderFileStorage(options.UseServices(sp)));
            return _builder;
        }

        public FoundatioBuilder UseFolder(Builder<FolderFileStorageOptionsBuilder, FolderFileStorageOptions> config)
        {
            _services.ReplaceSingleton<IFileStorage>(sp => new FolderFileStorage(b => b.Configure(config).UseServices(sp)));
            return _builder;
        }
    }

    public class MessagingBuilder : IFoundatioBuilder
    {
        private readonly FoundatioBuilder _builder;
        private readonly IServiceCollection _services;
        private bool _routingServicesRegistered;
        private bool _topologyServicesRegistered;

        internal MessagingBuilder(IFoundatioBuilder builder)
        {
            _builder = builder.Builder;
            _services = builder.Services;
        }

        IServiceCollection IFoundatioBuilder.Services => _services;
        FoundatioBuilder IFoundatioBuilder.Builder => _builder;

        public FoundatioBuilder Use(IMessageBus messageBus)
        {
            _services.ReplaceSingleton(_ => messageBus);
            _services.ReplaceSingleton<IMessagePublisher>(sp => sp.GetRequiredService<IMessageBus>());
            _services.ReplaceSingleton<IMessageSubscriber>(sp => sp.GetRequiredService<IMessageBus>());
            return _builder;
        }

        public FoundatioBuilder Use(Func<IServiceProvider, IMessageBus> factory)
        {
            _services.ReplaceSingleton(factory);
            _services.ReplaceSingleton<IMessagePublisher>(sp => sp.GetRequiredService<IMessageBus>());
            _services.ReplaceSingleton<IMessageSubscriber>(sp => sp.GetRequiredService<IMessageBus>());
            return _builder;
        }

        public MessagingBuilder ConfigureRouting(Action<MessageRoutingOptionsBuilder> configure)
        {
            ArgumentNullException.ThrowIfNull(configure);

            _services.AddSingleton<Action<MessageRoutingOptionsBuilder>>(configure);
            RegisterRoutingServices();
            return this;
        }

        public FoundatioBuilder UseInMemory(InMemoryMessageBusOptions? options = null)
        {
            _services.ReplaceSingleton<IMessageBus>(sp => new InMemoryMessageBus(options.UseServices(sp)));
            _services.ReplaceSingleton<IMessagePublisher>(sp => sp.GetRequiredService<IMessageBus>());
            _services.ReplaceSingleton<IMessageSubscriber>(sp => sp.GetRequiredService<IMessageBus>());
            RegisterMessagingRuntime(sp => new InMemoryMessageTransport(sp.GetService<TimeProvider>()));
            return _builder;
        }

        public FoundatioBuilder UseInMemory(Builder<InMemoryMessageBusOptionsBuilder, InMemoryMessageBusOptions> config)
        {
            _services.ReplaceSingleton<IMessageBus>(sp => new InMemoryMessageBus(b => b.Configure(config).UseServices(sp)));
            _services.ReplaceSingleton<IMessagePublisher>(sp => sp.GetRequiredService<IMessageBus>());
            _services.ReplaceSingleton<IMessageSubscriber>(sp => sp.GetRequiredService<IMessageBus>());
            RegisterMessagingRuntime(sp => new InMemoryMessageTransport(sp.GetService<TimeProvider>()));
            return _builder;
        }

        public FoundatioBuilder UseTransport(IMessageTransport transport)
        {
            ArgumentNullException.ThrowIfNull(transport);
            RegisterMessagingRuntime(_ => transport);
            return _builder;
        }

        public FoundatioBuilder UseTransport(Func<IServiceProvider, IMessageTransport> factory)
        {
            RegisterMessagingRuntime(factory);
            return _builder;
        }

        private void RegisterMessagingRuntime(Func<IServiceProvider, IMessageTransport> factory)
        {
            _services.ReplaceSingleton(factory);
            RegisterMessageTopology();
            RegisterMessageClients();
        }

        private void RegisterRoutingServices()
        {
            if (_routingServicesRegistered)
                return;

            _routingServicesRegistered = true;
            _services.ReplaceSingleton<MessageRoutingOptions>(sp =>
            {
                var options = new MessageRoutingOptions();
                var builder = new MessageRoutingOptionsBuilder(options);
                foreach (var configure in sp.GetServices<Action<MessageRoutingOptionsBuilder>>())
                    configure(builder);

                return options;
            });
            _services.ReplaceSingleton<IMessageRouter>(sp => new DefaultMessageRouter(sp.GetRequiredService<MessageRoutingOptions>()));
        }

        private void RegisterMessageTopology()
        {
            RegisterRoutingServices();

            if (_topologyServicesRegistered)
                return;

            _topologyServicesRegistered = true;
            _services.ReplaceSingleton<IMessageTopology>(sp => new MessageTopology(
                sp.GetRequiredService<IMessageTransport>(),
                sp.GetRequiredService<MessageRoutingOptions>()));
        }

        private void RegisterMessageClients()
        {
            RegisterRoutingServices();
            _services.ReplaceSingleton<Messaging.IQueue>(sp => new MessageQueue(sp.GetRequiredService<IMessageTransport>(), CreateQueueOptions(sp)));
            _services.ReplaceSingleton<IPubSub>(sp => new PubSub(sp.GetRequiredService<IMessageTransport>(), CreatePubSubOptions(sp)));
        }

        private static QueueOptions CreateQueueOptions(IServiceProvider serviceProvider)
        {
            return new QueueOptions
            {
                Serializer = serviceProvider.GetService<ISerializer>() ?? DefaultSerializer.Instance,
                Router = serviceProvider.GetService<IMessageRouter>() ?? DefaultMessageRouter.Instance,
                RuntimeStore = serviceProvider.GetService<IJobRuntimeStore>(),
                TimeProvider = serviceProvider.GetService<TimeProvider>() ?? TimeProvider.System,
                LoggerFactory = serviceProvider.GetService<ILoggerFactory>()
            };
        }

        private static PubSubOptions CreatePubSubOptions(IServiceProvider serviceProvider)
        {
            return new PubSubOptions
            {
                Serializer = serviceProvider.GetService<ISerializer>() ?? DefaultSerializer.Instance,
                Router = serviceProvider.GetService<IMessageRouter>() ?? DefaultMessageRouter.Instance,
                RuntimeStore = serviceProvider.GetService<IJobRuntimeStore>(),
                TimeProvider = serviceProvider.GetService<TimeProvider>() ?? TimeProvider.System,
                LoggerFactory = serviceProvider.GetService<ILoggerFactory>()
            };
        }
    }

    public class JobsBuilder : IFoundatioBuilder
    {
        private readonly FoundatioBuilder _builder;
        private readonly IServiceCollection _services;

        internal JobsBuilder(IFoundatioBuilder builder)
        {
            _builder = builder.Builder;
            _services = builder.Services;
        }

        IServiceCollection IFoundatioBuilder.Services => _services;
        FoundatioBuilder IFoundatioBuilder.Builder => _builder;

        public FoundatioBuilder UseRuntimeStore(IJobRuntimeStore store)
        {
            _services.ReplaceSingleton(_ => store);
            RegisterJobServices();
            return _builder;
        }

        public FoundatioBuilder UseRuntimeStore(Func<IServiceProvider, IJobRuntimeStore> factory)
        {
            _services.ReplaceSingleton(factory);
            RegisterJobServices();
            return _builder;
        }

        public FoundatioBuilder UseInMemoryRuntime()
        {
            _services.ReplaceSingleton<IJobRuntimeStore>(sp => new InMemoryJobRuntimeStore(sp.GetService<TimeProvider>()));
            RegisterJobServices();
            return _builder;
        }

        public FoundatioBuilder Register<TJob>(string name) where TJob : IJob
        {
            ArgumentException.ThrowIfNullOrEmpty(name);
            _services.AddSingleton(new JobTypeRegistration(name, typeof(TJob)));
            return _builder;
        }

        private void RegisterJobServices()
        {
            _services.ReplaceSingleton<IJobTypeRegistry>(sp => new JobTypeRegistry(sp.GetServices<JobTypeRegistration>()));
            _services.ReplaceSingleton<IJobMonitor>(sp => sp.GetRequiredService<IJobRuntimeStore>());
            _services.ReplaceSingleton<IJobClient>(sp => new JobClient(sp.GetRequiredService<IJobRuntimeStore>(), sp.GetService<TimeProvider>(), sp.GetRequiredService<IJobTypeRegistry>()));
            _services.ReplaceSingleton<IJobWorker>(sp => new JobWorker(sp.GetRequiredService<IJobRuntimeStore>(), sp, sp.GetService<TimeProvider>(), jobTypes: sp.GetRequiredService<IJobTypeRegistry>()));
            _services.ReplaceSingleton<IJobScheduler, InMemoryJobScheduler>();
            _services.ReplaceSingleton(sp => new JobScheduleProcessor(
                sp.GetRequiredService<IJobScheduler>(),
                sp.GetRequiredService<IJobRuntimeStore>(),
                sp.GetRequiredService<IJobWorker>(),
                sp.GetService<TimeProvider>(),
                transport: sp.GetService<IMessageTransport>(),
                jobTypes: sp.GetRequiredService<IJobTypeRegistry>()));
        }
    }

    public class QueueingBuilder : IFoundatioBuilder
    {
        private readonly FoundatioBuilder _builder;
        private readonly IServiceCollection _services;

        internal QueueingBuilder(IFoundatioBuilder builder)
        {
            _builder = builder.Builder;
            _services = builder.Services;
        }

        IServiceCollection IFoundatioBuilder.Services => _services;
        FoundatioBuilder IFoundatioBuilder.Builder => _builder;

        public FoundatioBuilder Use<T>(IQueue<T> storage) where T : class
        {
            _services.ReplaceSingleton(_ => storage);
            return _builder;
        }

        public FoundatioBuilder Use<T>(Func<IServiceProvider, IQueue<T>> factory) where T : class
        {
            _services.ReplaceSingleton(factory);
            return _builder;
        }

        public FoundatioBuilder UseInMemory<T>(InMemoryQueueOptions<T>? options = null) where T : class
        {
            _services.ReplaceSingleton<IQueue<T>>(sp => new InMemoryQueue<T>(options.UseServices(sp)));
            return _builder;
        }

        public FoundatioBuilder UseInMemory<T>(Builder<InMemoryQueueOptionsBuilder<T>, InMemoryQueueOptions<T>> config) where T : class
        {
            _services.ReplaceSingleton<IQueue<T>>(sp => new InMemoryQueue<T>(b => b.Configure(config).UseServices(sp)));
            return _builder;
        }
    }

    public class LockingBuilder : IFoundatioBuilder
    {
        private readonly FoundatioBuilder _builder;
        private readonly IServiceCollection _services;

        internal LockingBuilder(IFoundatioBuilder builder)
        {
            _builder = builder.Builder;
            _services = builder.Services;
        }

        IServiceCollection IFoundatioBuilder.Services => _services;
        FoundatioBuilder IFoundatioBuilder.Builder => _builder;

        public FoundatioBuilder Use(ILockProvider lockProvider)
        {
            _services.ReplaceSingleton(_ => lockProvider);
            return _builder;
        }

        public FoundatioBuilder Use(Func<IServiceProvider, ILockProvider> factory)
        {
            _services.ReplaceSingleton(factory);
            return _builder;
        }

        public FoundatioBuilder UseCache()
        {
            // gets all services from the ICacheClient instance
            _services.ReplaceSingleton<ILockProvider>(sp => new CacheLockProvider(
                sp.GetRequiredService<ICacheClient>(),
                sp.GetService<IMessageBus>(), // optional for more efficient lock release notifications
                sp.GetService<TimeProvider>(),
                sp.GetService<IResiliencePolicyProvider>(),
                sp.GetService<ILoggerFactory>()
            ));
            _services.ReplaceSingleton<IThrottlingLockProviderFactory>(sp => new ThrottlingLockProviderFactory(
                sp.GetRequiredService<ICacheClient>(), sp.GetService<TimeProvider>(),
                sp.GetService<IResiliencePolicyProvider>(),
                sp.GetService<ILoggerFactory>()));
            _services.AddTransient(sp => new ThrottlingLockProvider(sp.GetRequiredService<ICacheClient>(),
                timeProvider: sp.GetService<TimeProvider>(),
                resiliencePolicyProvider: sp.GetService<IResiliencePolicyProvider>(),
                loggerFactory: sp.GetService<ILoggerFactory>()));
            return _builder;
        }
    }
}

public interface IFoundatioBuilder
{
    IServiceCollection Services { get; }
    FoundatioBuilder Builder { get; }
}
