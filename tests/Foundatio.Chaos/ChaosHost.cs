using Confluent.Kafka;
using Foundatio.Jobs.Store;
using Foundatio.Messaging;
using Foundatio.Messaging.Transport;
using Foundatio.Resilience;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Foundatio.Chaos;

/// <summary>Builds a host on the transport the settings name, with the handlers a role needs.</summary>
public static class ChaosHost
{
    public static IHost Build(ChaosSettings settings, Ledger ledger, bool consume)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(o =>
        {
            o.SingleLine = true;
            o.TimestampFormat = "HH:mm:ss.fff ";
            o.UseUtcTimestamp = true;
        });
        builder.Logging.SetMinimumLevel(Enum.Parse<LogLevel>(settings.Get("log-level", "Information")));
        builder.Logging.AddFilter("Microsoft", LogLevel.Warning);

        var services = builder.Services;
        var handlers = new ChaosHandlers(ledger, settings);
        services.AddSingleton(ledger);
        services.AddSingleton(settings);
        services.AddSingleton(handlers);
        services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(settings.GetInt("host-shutdown-seconds", 30)));

        if (settings.Get("clock-file") is { Length: > 0 } clockFile)
            services.AddSingleton<TimeProvider>(new ShiftableTimeProvider(clockFile));

        var retryPolicy = new RetryPolicy
        {
            MaxAttempts = settings.GetInt("max-attempts", 3),
            Delay = RetryPolicy.Fixed(TimeSpan.FromMilliseconds(settings.GetInt("retry-delay-ms", 2000)))
        };

        services.AddFoundatioMessaging(m =>
        {
            UseTransport(m, settings, services);
            m.UseServiceName(Streams.ServiceName);
            m.Configure(o =>
            {
                o.DefaultRetryPolicy = retryPolicy;
                o.ShutdownTimeout = TimeSpan.FromSeconds(settings.GetInt("shutdown-seconds", 10));
            });

            if (!consume)
                return;

            var options = new HandlerOptions
            {
                MaxConcurrency = settings.GetInt("concurrency", 16),
                LeaseDuration = TimeSpan.FromSeconds(settings.GetInt("lease-seconds", 15)),
                RetryPolicy = retryPolicy
            };

            m.AddConsumer<WorkMessage>((message, context, ct) => handlers.HandleAsync(Streams.Work, message.Id, message.Fail, message.SlowMs, context.MessageId, context, ct), options);
            m.AddConsumer<GroupedMessage>((message, context, ct) => handlers.HandleAsync(Streams.Grouped, message.Id, message.Fail, 0, context.MessageId, context, ct), options);
            m.AddConsumer<DelayedMessage>((message, context, ct) => handlers.HandleAsync(Streams.Delayed, message.Id, FailMode.None, 0, context.MessageId, context, ct), options);
            m.AddSubscriber<ChaosEvent>((message, context, ct) => handlers.HandleAsync(Streams.Event, message.Id, message.Fail, 0, context.MessageId, context, ct), new SubscriptionOptions
            {
                MaxConcurrency = options.MaxConcurrency,
                LeaseDuration = options.LeaseDuration,
                RetryPolicy = retryPolicy
            });
        });

        if (settings.Jobs)
        {
            services.AddFoundatioJobs(j =>
            {
                j.UseRedis(new RedisJobStoreOptions { Prefix = settings.Prefix + ":jobs" });
                j.Configure(o =>
                {
                    o.MaxConcurrency = settings.GetInt("job-concurrency", 8);
                    o.LeaseDuration = TimeSpan.FromSeconds(settings.GetInt("job-lease-seconds", 15));
                    o.DefaultRetryPolicy = retryPolicy;
                    o.ShutdownTimeout = TimeSpan.FromSeconds(settings.GetInt("shutdown-seconds", 10));
                });

                if (consume)
                    j.AddJob<ChaosJobHandler>();
            });
        }

        if (settings.GetBool("metrics", true))
            services.AddHostedService<MetricsSampler>();
        return builder.Build();
    }

    private static void UseTransport(FoundatioMessagingBuilder m, ChaosSettings settings, IServiceCollection services)
    {
        string prefix = settings.Prefix;
        switch (settings.Transport)
        {
            case "memory":
                m.UseInMemory();
                break;
            case "rabbitmq":
                m.UseRabbitMQ(new RabbitMQMessageTransportOptions
                {
                    ConnectionString = settings.Get("connection", "amqp://guest:guest@127.0.0.1:5672"),
                    Hosts = settings.Get("hosts")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                    ManagementEndpoint = settings.Get("management") is { Length: > 0 } management ? new Uri(management) : null,
                    Prefix = prefix
                });
                break;
            case "redis":
                AddRedis(settings, services);
                m.UseRedis(new RedisMessageTransportOptions { Prefix = prefix + ":messaging" });
                break;
            case "kafka":
                bool kip848 = settings.Get("kafka-protocol", "classic") == "consumer";
                m.UseKafka(new KafkaMessageTransportOptions
                {
                    BootstrapServers = settings.Get("connection", "127.0.0.1:9092"),
                    Prefix = prefix,
                    Partitions = settings.GetInt("partitions", 6),
                    ReplicationFactor = (short)settings.GetInt("replication-factor", 1),
                    ConfigureConsumer = kip848 ? c => c.GroupProtocol = GroupProtocol.Consumer : null
                });
                break;
            case "sqs":
                m.UseSqs(new SqsMessageTransportOptions
                {
                    ConnectionString = settings.Get("connection", "serviceurl=http://127.0.0.1:4566;AccessKey=xxx;SecretKey=xxx"),
                    Prefix = prefix + "-",
                    Fifo = settings.GetBool("fifo")
                });
                break;
            case "asb":
                m.UseAzureServiceBus(new AzureServiceBusMessageTransportOptions
                {
                    ConnectionString = settings.Require("connection"),
                    Prefix = prefix + ".",
                    RequiresSession = settings.GetBool("sessions")
                });
                break;
            default:
                throw new ArgumentException($"Unknown transport '{settings.Transport}'");
        }

        if (settings.Jobs && settings.Transport != "redis")
            AddRedis(settings, services);
    }

    private static void AddRedis(ChaosSettings settings, IServiceCollection services)
    {
        if (services.Any(d => d.ServiceType == typeof(IConnectionMultiplexer)))
            return;

        string connection = settings.Get("redis-connection") ?? (settings.Transport == "redis" ? settings.Get("connection", "127.0.0.1:6379") : "127.0.0.1:6379");
        services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(connection));
    }
}
