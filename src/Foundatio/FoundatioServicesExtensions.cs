using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Extensions;
using Foundatio.Jobs;
using Foundatio.Lock;
using Foundatio.Messaging;
using Foundatio.Resilience;
using Foundatio.Serializer;
using Foundatio.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Legacy = Foundatio.Messaging.Legacy;

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
        ArgumentNullException.ThrowIfNull(services);
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
        private TopologyMode _topologyMode = TopologyMode.Ensure;

        internal MessagingBuilder(IFoundatioBuilder builder)
        {
            _builder = builder.Builder;
            _services = builder.Services;
        }

        /// <summary>
        /// Selects how the messaging client administers topology: <see cref="TopologyMode.Ensure"/> creates missing
        /// destinations on use and at handler-host startup (default), <see cref="TopologyMode.Validate"/> only checks
        /// they exist and throws when missing, and <see cref="TopologyMode.None"/> never touches topology.
        /// </summary>
        public MessagingBuilder ConfigureTopology(TopologyMode mode)
        {
            if (!Enum.IsDefined(mode))
                throw new ArgumentOutOfRangeException(nameof(mode));
            _topologyMode = mode;
            return this;
        }

        IServiceCollection IFoundatioBuilder.Services => _services;
        FoundatioBuilder IFoundatioBuilder.Builder => _builder;

        /// <summary>
        /// Registers the legacy <see cref="Legacy.IMessageBus"/>/<see cref="Legacy.IMessagePublisher"/>/
        /// <see cref="Legacy.IMessageSubscriber"/> interfaces as a thin adapter over the redesigned
        /// <see cref="IMessageBus"/>, so existing consuming code keeps compiling while it migrates. There is no
        /// legacy bus behind it — remove this call once call sites are on the new API.
        /// </summary>
        public FoundatioBuilder AddLegacyAdapter()
        {
            _services.ReplaceSingleton<Legacy.IMessageBus>(sp => new Legacy.LegacyMessageBusAdapter(sp.GetRequiredService<IMessageBus>()));
            _services.ReplaceSingleton<Legacy.IMessagePublisher>(sp => sp.GetRequiredService<Legacy.IMessageBus>());
            _services.ReplaceSingleton<Legacy.IMessageSubscriber>(sp => sp.GetRequiredService<Legacy.IMessageBus>());
            return _builder;
        }

        public MessagingBuilder ConfigureRouting(Action<MessageRoutingOptionsBuilder> configure)
        {
            ArgumentNullException.ThrowIfNull(configure);

            _services.AddSingleton<Action<MessageRoutingOptionsBuilder>>(configure);
            RegisterRoutingServices();
            return this;
        }

        // The core owns retry and dead-letter behavior so it is identical across transports. This configures the
        // default policy applied to queue and pub/sub consumers; a consumer can still override MaxAttempts/backoff.
        public MessagingBuilder ConfigureRetry(RetryPolicy policy)
        {
            ArgumentNullException.ThrowIfNull(policy);
            _services.ReplaceSingleton(_ => policy);
            return this;
        }

        public MessagingBuilder ConfigureRetry(Func<RetryPolicy, RetryPolicy> configure)
        {
            ArgumentNullException.ThrowIfNull(configure);
            return ConfigureRetry(configure(new RetryPolicy()));
        }

        // Registers a stable wire name for a message type so the discriminator survives assembly/namespace moves and
        // grouped/interface consumers can resolve and deserialize the concrete payload type.
        public MessagingBuilder AddMessageType<T>(string name) where T : class
        {
            ArgumentException.ThrowIfNullOrEmpty(name);
            _services.AddSingleton(new MessageTypeRegistration(name, typeof(T)));
            return this;
        }

        /// <summary>Uses the in-memory transport — the all-defaults setup for development and tests.</summary>
        public FoundatioBuilder UseInMemory()
        {
            RegisterMessagingRuntime(sp => new InMemoryMessageTransport(sp.GetService<TimeProvider>(), sp.GetService<ILoggerFactory>()));
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
            ArgumentNullException.ThrowIfNull(factory);
            RegisterMessagingRuntime(factory);
            return _builder;
        }

        /// <summary>Runs queued work in a scoped handler. Replicas compete for the same queue.</summary>
        public FoundatioBuilder AddConsumer<TMessage, THandler>(Action<MessageConsumerOptions>? configure = null)
            where TMessage : class where THandler : class, IMessageHandler<TMessage>
        {
            _services.TryAddScoped<THandler>();
            return AddConsumer<TMessage>((sp, message, ct) => DispatchAsync<TMessage, THandler>(sp, message, ct), configure);
        }

        /// <summary>Runs queued work in a delegate handler.</summary>
        public FoundatioBuilder AddConsumer<TMessage>(Func<IMessageContext<TMessage>, CancellationToken, Task> handler, Action<MessageConsumerOptions>? configure = null)
            where TMessage : class
        {
            ArgumentNullException.ThrowIfNull(handler);
            return AddConsumer<TMessage>((_, message, ct) => handler(message, ct), configure);
        }

        private FoundatioBuilder AddConsumer<TMessage>(Func<IServiceProvider, IMessageContext<TMessage>, CancellationToken, Task> dispatch, Action<MessageConsumerOptions>? configure)
            where TMessage : class
        {
            var options = new MessageConsumerOptions();
            configure?.Invoke(options);
            options.Validate();
            return AddHandlerRegistration($"consumer:{typeof(TMessage).Name}", (sp, ct) =>
                sp.GetRequiredService<IMessageBus>().ConsumeAsync<TMessage>((message, token) => dispatch(sp, message, token), options, ct));
        }

        /// <summary>
        /// Receives published events in a scoped handler. Supply a stable subscription name for durable delivery
        /// shared by replicas. Use AddTemporarySubscriber for a temporary subscription on every instance.
        /// </summary>
        public FoundatioBuilder AddSubscriber<TMessage, THandler>(string subscription, Action<MessageSubscriptionOptions>? configure = null)
            where TMessage : class where THandler : class, IMessageHandler<TMessage>
        {
            _services.TryAddScoped<THandler>();
            return AddSubscriber<TMessage>((sp, message, ct) => DispatchAsync<TMessage, THandler>(sp, message, ct), subscription, configure);
        }

        /// <summary>Receives published events in a delegate handler on a named durable subscription.</summary>
        public FoundatioBuilder AddSubscriber<TMessage>(Func<IMessageContext<TMessage>, CancellationToken, Task> handler, string subscription, Action<MessageSubscriptionOptions>? configure = null)
            where TMessage : class
        {
            ArgumentNullException.ThrowIfNull(handler);
            return AddSubscriber<TMessage>((_, message, ct) => handler(message, ct), subscription, configure);
        }

        /// <summary>Receives a copy of each event for this process using an expiring subscription. Requires provider support.</summary>
        public FoundatioBuilder AddTemporarySubscriber<TMessage, THandler>(Action<MessageSubscriptionOptions>? configure = null)
            where TMessage : class where THandler : class, IMessageHandler<TMessage>
        {
            _services.TryAddScoped<THandler>();
            return AddSubscriber<TMessage>((sp, message, ct) => DispatchAsync<TMessage, THandler>(sp, message, ct), null, configure, temporary: true);
        }

        /// <summary>Receives events in a delegate using an expiring subscription. Requires provider support.</summary>
        public FoundatioBuilder AddTemporarySubscriber<TMessage>(Func<IMessageContext<TMessage>, CancellationToken, Task> handler, Action<MessageSubscriptionOptions>? configure = null)
            where TMessage : class
        {
            ArgumentNullException.ThrowIfNull(handler);
            return AddSubscriber<TMessage>((_, message, ct) => handler(message, ct), null, configure, temporary: true);
        }

        private FoundatioBuilder AddSubscriber<TMessage>(Func<IServiceProvider, IMessageContext<TMessage>, CancellationToken, Task> dispatch, string? subscription, Action<MessageSubscriptionOptions>? configure, bool temporary = false)
            where TMessage : class
        {
            if (!temporary)
                ArgumentException.ThrowIfNullOrWhiteSpace(subscription);
            var options = new MessageSubscriptionOptions { Subscription = subscription };
            configure?.Invoke(options);
            options.Validate();
            if (options.Subscription != subscription)
                throw new ArgumentException("Set the durable name with the subscription argument. Use AddTemporarySubscriber for a temporary subscription.", nameof(configure));
            return AddHandlerRegistration($"subscriber:{typeof(TMessage).Name}", (sp, ct) =>
                sp.GetRequiredService<IMessageBus>().SubscribeAsync<TMessage>((message, token) => dispatch(sp, message, token), options, ct));
        }

        private FoundatioBuilder AddHandlerRegistration(string description, Func<IServiceProvider, CancellationToken, Task<IMessageSubscription>> start)
        {
            _services.AddSingleton(new MessageHandlerRegistration
            {
                Description = description,
                StartAsync = async (sp, ct) => await start(sp, ct).ConfigureAwait(false)
            });
            return _builder;
        }

        private static async Task DispatchAsync<TMessage, THandler>(IServiceProvider serviceProvider, IMessageContext<TMessage> message, CancellationToken cancellationToken)
            where TMessage : class where THandler : class, IMessageHandler<TMessage>
        {
            await using var scope = serviceProvider.CreateAsyncScope();
            var handler = scope.ServiceProvider.GetRequiredService<THandler>();
            await handler.HandleAsync(message, cancellationToken).ConfigureAwait(false);
        }

        private void RegisterMessagingRuntime(Func<IServiceProvider, IMessageTransport> factory)
        {
            _services.ReplaceSingleton(factory);
            // Resolved lazily so ConfigureTopology can be called before or after the Use* transport registration.
            _services.ReplaceSingleton(_ => new MessagingTopologyOptions(_topologyMode));
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
            _services.ReplaceSingleton<IMessageTypeRegistry>(sp => new MessageTypeRegistry(sp.GetServices<MessageTypeRegistration>()));
            _services.ReplaceSingleton<IMessageBus>(sp => new MessageBus(sp.GetRequiredService<IMessageTransport>(), new MessageBusOptions
            {
                Serializer = sp.GetService<ISerializer>() ?? DefaultSerializer.Instance,
                Router = sp.GetService<IMessageRouter>() ?? DefaultMessageRouter.Instance,
                MessageTypes = sp.GetService<IMessageTypeRegistry>() ?? new MessageTypeRegistry(),
                RuntimeStore = sp.GetService<IScheduledDispatchStore>() ?? sp.GetService<IJobRuntimeStore>(),
                RetryPolicy = sp.GetService<RetryPolicy>() ?? new RetryPolicy(),
                Topology = sp.GetService<MessagingTopologyOptions>()?.Mode ?? TopologyMode.Ensure,
                // The transport is a shared DI singleton owned by the container; the bus must not dispose it.
                OwnsTransport = false,
                TimeProvider = sp.GetService<TimeProvider>() ?? TimeProvider.System,
                LoggerFactory = sp.GetService<ILoggerFactory>()
            }));
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
            ArgumentNullException.ThrowIfNull(store);
            _services.ReplaceSingleton(_ => store);
            RegisterJobServices();
            return _builder;
        }

        public FoundatioBuilder UseRuntimeStore(Func<IServiceProvider, IJobRuntimeStore> factory)
        {
            ArgumentNullException.ThrowIfNull(factory);
            _services.ReplaceSingleton(factory);
            RegisterJobServices();
            return _builder;
        }

        /// <summary>Uses the in-memory job runtime — the all-defaults setup for development and tests.</summary>
        public FoundatioBuilder UseInMemory()
        {
            _services.ReplaceSingleton<IJobRuntimeStore>(sp => new InMemoryJobRuntimeStore(sp.GetService<TimeProvider>()));
            RegisterJobServices();
            return _builder;
        }

        public FoundatioBuilder AddJobType<TJob>(string? name = null) where TJob : IJob
        {
            JobArgumentContract.ValidateType(typeof(TJob));
            if (name is not null)
                ArgumentException.ThrowIfNullOrWhiteSpace(name);
            _services.AddSingleton(new JobTypeRegistration(name ?? typeof(TJob).FullName ?? typeof(TJob).Name, typeof(TJob)));
            return _builder;
        }

        /// <summary>
        /// Registers a recurring (CRON) job. The schedule is materialized once into the shared runtime store per
        /// occurrence, so <see cref="CronJobOptions.Scope"/> decides fan-out (Global = one instance per tick,
        /// PerNode = every instance per tick). Scheduled when the job scheduler starts — no manual
        /// <see cref="IScheduledJobStore.ScheduleAsync"/> call needed. Requires a runtime store (<see cref="UseRuntimeStore(IJobRuntimeStore)"/>
        /// / <see cref="UseInMemory"/>).
        /// </summary>
        public FoundatioBuilder AddCronJob<TJob>(string cronSchedule, Action<CronJobOptions>? configure = null) where TJob : IJob
            => AddCronJob(typeof(TJob), cronSchedule, null, configure);

        /// <summary>Declares a recurring job with arguments constrained to its typed job contract.</summary>
        public FoundatioBuilder AddCronJob<TJob, TArgs>(string cronSchedule, TArgs arguments, Action<CronJobOptions>? configure = null)
            where TJob : IJob<TArgs> where TArgs : class
            => AddCronJob(typeof(TJob), cronSchedule, arguments, configure);

        private FoundatioBuilder AddCronJob(Type jobType, string cronSchedule, object? arguments, Action<CronJobOptions>? configure)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(cronSchedule);
            JobScheduleProcessor.ValidateCron(cronSchedule);
            JobArgumentContract.Validate(jobType, arguments);
            var options = new CronJobOptions();
            configure?.Invoke(options);
            var registration = new ScheduledJobRegistration(jobType, cronSchedule, options, arguments);
            registration.Validate();
            if (_services.Any(d => d.ImplementationInstance is ScheduledJobRegistration existing && existing.Name == registration.Name))
                throw new InvalidOperationException($"A CRON job named {registration.Name} is already registered. Give one an explicit CronJobOptions.Name.");
            if (!_services.Any(d => d.ImplementationInstance is JobTypeRegistration existing && existing.JobType == jobType))
                _services.AddSingleton(new JobTypeRegistration(jobType.FullName ?? jobType.Name, jobType));
            _services.AddSingleton(registration);
            _services.AddSingleton(sp => registration.Create(sp.GetRequiredService<IJobTypeRegistry>(), sp.GetService<ISerializer>() ?? DefaultSerializer.Instance));
            return _builder;
        }

        private void RegisterJobServices()
        {
            _services.ReplaceSingleton<IJobTypeRegistry>(sp => new JobTypeRegistry(sp.GetServices<JobTypeRegistration>()));
            _services.ReplaceSingleton<IJobMonitor>(sp => sp.GetRequiredService<IJobRuntimeStore>());
            _services.ReplaceSingleton<IJobClient>(sp => new JobClient(sp.GetRequiredService<IJobRuntimeStore>(), sp.GetService<TimeProvider>(), sp.GetRequiredService<IJobTypeRegistry>(), sp.GetService<ISerializer>()));
            _services.ReplaceSingleton<IJobWorker>(sp => new JobWorker(sp.GetRequiredService<IJobRuntimeStore>(), sp, new JobWorkerOptions { TimeProvider = sp.GetService<TimeProvider>(), JobTypes = sp.GetRequiredService<IJobTypeRegistry>(), Serializer = sp.GetService<ISerializer>(), MaxConcurrency = sp.GetService<JobWorkerOptions>()?.MaxConcurrency ?? 1 }));
            _services.ReplaceSingleton<IScheduledJobStore>(sp => sp.GetRequiredService<IJobRuntimeStore>());
            _services.ReplaceSingleton<IScheduledJobManager>(sp => new ScheduledJobManager(
                sp.GetRequiredService<IScheduledJobStore>(),
                sp.GetRequiredService<IJobRuntimeStore>(),
                sp.GetRequiredService<IJobTypeRegistry>(),
                sp.GetService<ISerializer>(),
                sp.GetService<TimeProvider>()));
            _services.ReplaceSingleton(sp => new JobScheduleProcessor(sp.GetRequiredService<IScheduledJobStore>(), sp.GetRequiredService<IJobRuntimeStore>(), new JobScheduleProcessorOptions { TimeProvider = sp.GetService<TimeProvider>() }));


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
