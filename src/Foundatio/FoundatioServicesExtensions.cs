using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Extensions;
using Foundatio.Jobs;
using Foundatio.Lock;
using Foundatio.Messaging;
using Legacy = Foundatio.Messaging.Legacy;
using Foundatio.Resilience;
using Foundatio.Serializer;
using Foundatio.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
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
            RegisterMessagingRuntime(factory);
            return _builder;
        }

        /// <summary>
        /// Registers a handler for messages of type <typeparamref name="TMessage"/>. Registration carries no topology
        /// decision — the caller's verb on <see cref="IMessageBus"/> decides delivery: a <c>SendAsync</c> is processed
        /// by exactly one handler instance across the fleet (competing consumers), and a <c>PublishAsync</c> is received
        /// once per subscribing service (a scaled service's instances compete), or by every instance when
        /// <see cref="MessageSubscriptionOptions.PerInstance"/> is set. The handler is resolved from DI in its own scope
        /// per message (so it can inject scoped dependencies); throwing triggers the retry/dead-letter policy. A single
        /// hosted service starts and stops all registered handlers — a running generic host (WebApplication/Host) is
        /// REQUIRED; in a process that never starts hosted services the handlers never attach.
        /// </summary>
        public FoundatioBuilder AddHandler<TMessage, THandler>(Action<MessageSubscriptionOptions>? configure = null)
            where TMessage : class where THandler : class, IMessageHandler<TMessage>
        {
            _services.TryAddScoped<THandler>();
            // Each handler class is its own subscriber group ("{service}.{handler}"), so every handler registered for
            // an event type receives its own copy of each published message.
            return AddHandlerRegistration<TMessage>(typeof(THandler).Name, static (sp, message, ct) => DispatchAsync<TMessage, THandler>(sp, message, ct),
                options =>
                {
                    configure?.Invoke(options);
                    options.SubscriptionQualifier ??= typeof(THandler).Name;
                });
        }

        /// <summary>
        /// Registers a delegate handler for messages of type <typeparamref name="TMessage"/>; see
        /// <see cref="AddHandler{TMessage, THandler}"/> for the delivery semantics.
        /// </summary>
        public FoundatioBuilder AddHandler<TMessage>(Func<IMessageContext<TMessage>, CancellationToken, Task> handler, Action<MessageSubscriptionOptions>? configure = null)
            where TMessage : class
        {
            ArgumentNullException.ThrowIfNull(handler);
            return AddHandlerRegistration<TMessage>(null, (_, message, ct) => handler(message, ct), configure);
        }

        private FoundatioBuilder AddHandlerRegistration<TMessage>(string? handlerName, Func<IServiceProvider, IMessageContext<TMessage>, CancellationToken, Task> dispatch, Action<MessageSubscriptionOptions>? configure)
            where TMessage : class
        {
            string suffix = handlerName is null ? String.Empty : $" -> {handlerName}";
            _services.AddSingleton(new MessageHandlerRegistration
            {
                Description = $"handler:{typeof(TMessage).Name}{suffix}",
                StartAsync = async (sp, ct) =>
                {
                    var options = new MessageSubscriptionOptions();
                    configure?.Invoke(options);
                    return await sp.GetRequiredService<IMessageBus>()
                        .SubscribeAsync<TMessage>((message, c) => dispatch(sp, message, c), options, ct).ConfigureAwait(false);
                }
            });

            // The validator must precede the handler host so a missing transport fails with the actionable message,
            // not the handler host's bare unresolved-service error.
            if (!_services.Any(s => s.ServiceType == typeof(IHostedService) && s.ImplementationType == typeof(FoundatioStartupValidationService)))
                _services.AddSingleton<IHostedService, FoundatioStartupValidationService>();
            if (!_services.Any(s => s.ServiceType == typeof(IHostedService) && s.ImplementationType == typeof(MessageHandlerHostedService)))
                _services.AddSingleton<IHostedService, MessageHandlerHostedService>();

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

            // Startup topology (Ensure/Validate) must run for publish-only apps too, so it is its own hosted service
            // rather than riding the handler host.
            if (!_services.Any(s => s.ServiceType == typeof(IHostedService) && s.ImplementationType == typeof(MessagingTopologyStartupService)))
                _services.AddSingleton<IHostedService, MessagingTopologyStartupService>();
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

        /// <summary>Uses the in-memory job runtime — the all-defaults setup for development and tests.</summary>
        public FoundatioBuilder UseInMemory()
        {
            _services.ReplaceSingleton<IJobRuntimeStore>(sp => new InMemoryJobRuntimeStore(sp.GetService<TimeProvider>()));
            RegisterJobServices();
            return _builder;
        }

        public FoundatioBuilder AddJobType<TJob>(string name) where TJob : IJob
        {
            ArgumentException.ThrowIfNullOrEmpty(name);
            _services.AddSingleton(new JobTypeRegistration(name, typeof(TJob)));
            return _builder;
        }

        /// <summary>
        /// Registers a recurring (CRON) job. The schedule is materialized once into the shared runtime store per
        /// occurrence, so <see cref="CronJobOptions.Scope"/> decides fan-out (Global = one instance per tick,
        /// PerNode = every instance per tick). Scheduled automatically when the runtime pump starts — no manual
        /// <see cref="IScheduledJobStore.ScheduleAsync"/> call needed. Requires a runtime store (<see cref="UseRuntimeStore(IJobRuntimeStore)"/>
        /// / <see cref="UseInMemory"/>).
        /// </summary>
        public FoundatioBuilder AddCronJob<TJob>(string cronSchedule, Action<CronJobOptions>? configure = null) where TJob : IJob
        {
            ArgumentException.ThrowIfNullOrEmpty(cronSchedule);

            var options = new CronJobOptions();
            configure?.Invoke(options);
            string name = options.Name ?? ScheduledJobDefinition.DefaultNameFor(typeof(TJob));

            // Fail at registration, not at pump start: a cron typo otherwise costs one scrolled-past ERROR line and a
            // job that silently never fires.
            JobScheduleProcessor.ValidateCron(cronSchedule);

            // Duplicate schedule names silently last-win at the scheduler; catch them here where both call sites are visible.
            if (_services.Any(d => d.ImplementationInstance is ScheduledJobDefinition existing && String.Equals(existing.Name, name, StringComparison.Ordinal)))
                throw new InvalidOperationException($"A CRON job named \"{name}\" is already registered. Give one of them an explicit CronJobOptions.Name.");

            _services.AddSingleton(new JobTypeRegistration(name, typeof(TJob)));
            _services.AddSingleton(new ScheduledJobDefinition
            {
                Name = name,
                Cron = cronSchedule,
                JobType = typeof(TJob),
                Scope = options.Scope,
                Overlap = options.Overlap,
                MisfireWindow = options.MisfireWindow,
                MaxAttempts = options.MaxAttempts,
                Enabled = options.Enabled,
                TimeZone = options.TimeZone,
                Arguments = options.Arguments
            });

            if (!_services.Any(s => s.ServiceType == typeof(IHostedService) && s.ImplementationType == typeof(FoundatioStartupValidationService)))
                _services.AddSingleton<IHostedService, FoundatioStartupValidationService>();

            return _builder;
        }

        /// <summary>
        /// Tunes the auto-registered runtime pump (cadence, batch size, or <see cref="JobRuntimePumpOptions.Enabled"/>
        /// to opt out of automatic pumping and take manual control).
        /// </summary>
        public FoundatioBuilder ConfigureRuntimePump(Action<JobRuntimePumpOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(configure);
            var options = new JobRuntimePumpOptions();
            configure(options);
            _services.ReplaceSingleton(_ => options);
            return _builder;
        }

        private void RegisterJobServices()
        {
            _services.ReplaceSingleton<IJobTypeRegistry>(sp => new JobTypeRegistry(sp.GetServices<JobTypeRegistration>()));
            _services.ReplaceSingleton<IJobMonitor>(sp => sp.GetRequiredService<IJobRuntimeStore>());
            _services.ReplaceSingleton<IJobClient>(sp => new JobClient(sp.GetRequiredService<IJobRuntimeStore>(), sp.GetService<TimeProvider>(), sp.GetRequiredService<IJobTypeRegistry>(), sp.GetService<ISerializer>()));
            _services.ReplaceSingleton<IJobWorker>(sp => new JobWorker(sp.GetRequiredService<IJobRuntimeStore>(), sp, sp.GetService<TimeProvider>(), jobTypes: sp.GetRequiredService<IJobTypeRegistry>(), serializer: sp.GetService<ISerializer>(),
                maxConcurrency: sp.GetService<JobRuntimePumpOptions>()?.WorkerConcurrency ?? 1));
            _services.ReplaceSingleton<IScheduledJobStore, InMemoryScheduledJobStore>();
            _services.ReplaceSingleton<IScheduledJobManager>(sp => new ScheduledJobManager(
                sp.GetRequiredService<IScheduledJobStore>(),
                sp.GetRequiredService<IJobRuntimeStore>(),
                sp.GetRequiredService<IJobTypeRegistry>(),
                sp.GetService<ISerializer>(),
                sp.GetService<TimeProvider>()));
            _services.ReplaceSingleton(sp => new JobScheduleProcessor(
                sp.GetRequiredService<IScheduledJobStore>(),
                sp.GetRequiredService<IJobRuntimeStore>(),
                sp.GetRequiredService<IJobWorker>(),
                sp.GetService<TimeProvider>(),
                transport: sp.GetService<IMessageTransport>(),
                jobTypes: sp.GetRequiredService<IJobTypeRegistry>(),
                serializer: sp.GetService<ISerializer>()));

            // A runtime store is inert without something draining it, so register the pump alongside the store: in a
            // hosted process it runs jobs and the messaging delayed-delivery fallback automatically (no separate
            // AddJobRuntimeService call); in a non-hosted process the IHostedService is simply never started. Guarded so
            // repeated UseRuntimeStore/UseInMemory calls don't stack multiple pumps. Options default unless
            // AddJobRuntimeService (or a registered JobRuntimePumpOptions) overrides them.
            if (!_services.Any(s => s.ServiceType == typeof(IHostedService) && s.ImplementationType == typeof(JobRuntimePumpService)))
                _services.AddSingleton<IHostedService, JobRuntimePumpService>();
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
