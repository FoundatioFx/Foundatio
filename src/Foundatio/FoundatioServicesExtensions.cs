using System;
using Foundatio.Caching;
using Foundatio.Extensions;
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
    public FoundatioBuilder AddResilience(Action<ResiliencePolicyProviderBuilder> builder = null)
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
    public FoundatioBuilder AddSerializer(Func<IServiceProvider, ITextSerializer> textSerializerFactory, Func<IServiceProvider, ISerializer> serializerFactory = null)
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
    public FoundatioBuilder AddSerializer(ITextSerializer textSerializer, ISerializer serializer = null)
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

        public FoundatioBuilder UseInMemory(InMemoryCacheClientOptions options = null)
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

        public FoundatioBuilder UseInMemory(InMemoryFileStorageOptions options = null)
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

        public FoundatioBuilder UseFolder(FolderFileStorageOptions options = null)
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

        public FoundatioBuilder UseInMemory(InMemoryMessageBusOptions options = null)
        {
            _services.ReplaceSingleton<IMessageBus>(sp => new InMemoryMessageBus(options.UseServices(sp)));
            _services.ReplaceSingleton<IMessagePublisher>(sp => sp.GetRequiredService<IMessageBus>());
            _services.ReplaceSingleton<IMessageSubscriber>(sp => sp.GetRequiredService<IMessageBus>());
            return _builder;
        }

        public FoundatioBuilder UseInMemory(Builder<InMemoryMessageBusOptionsBuilder, InMemoryMessageBusOptions> config)
        {
            _services.ReplaceSingleton<IMessageBus>(sp => new InMemoryMessageBus(b => b.Configure(config).UseServices(sp)));
            _services.ReplaceSingleton<IMessagePublisher>(sp => sp.GetRequiredService<IMessageBus>());
            _services.ReplaceSingleton<IMessageSubscriber>(sp => sp.GetRequiredService<IMessageBus>());
            return _builder;
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

        public FoundatioBuilder UseInMemory<T>(InMemoryQueueOptions<T> options = null) where T : class
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
                sp.GetService<IMessageBus>() // optional for more efficient lock release notifications
            ));
            _services.ReplaceSingleton<IThrottlingLockProviderFactory>(sp => new ThrottlingLockProviderFactory(sp.GetRequiredService<ICacheClient>()));
            _services.AddTransient(sp => new ThrottlingLockProvider(sp.GetRequiredService<ICacheClient>()));
            return _builder;
        }
    }
}

public interface IFoundatioBuilder
{
    IServiceCollection Services { get; }
    FoundatioBuilder Builder { get; }
}
