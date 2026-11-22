using System;
using System.Linq;
using System.Reflection;
using Foundatio.Messaging;
using Foundatio.Messaging.Transport;
using Foundatio.Monitoring;
using Foundatio.Serializer;
using Foundatio.Testing;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers Foundatio messaging.</summary>
public static class MessagingServiceCollectionExtensions
{
    /// <summary>
    /// Adds Foundatio messaging: <see cref="IMessageClient"/> for sending and publishing, plus any handlers you register.
    /// Registered handlers start receiving when the host starts. Call as many times as you like; registrations accumulate.
    /// </summary>
    /// <example>
    /// <code>
    /// services.AddFoundatioMessaging(m => m
    ///     .UseInMemory()
    ///     .AddConsumer&lt;SendReceiptHandler&gt;()
    ///     .AddSubscriber&lt;OrderPlacedHandler&gt;());
    /// </code>
    /// </example>
    public static IServiceCollection AddFoundatioMessaging(this IServiceCollection services, Action<FoundatioMessagingBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var registrations = services.FirstOrDefault(d => d.ServiceType == typeof(MessagingRegistrations))?.ImplementationInstance as MessagingRegistrations;
        if (registrations is null)
        {
            registrations = new MessagingRegistrations();
            services.AddSingleton(registrations);
            services.AddOptions();
            services.TryAddSingleton(WorkTracker.Create);
            services.TryAddSingleton(sp => CreateRuntime(sp, registrations));
            services.TryAddSingleton<IMessageClient>(sp => new MessageClient(sp.GetRequiredService<MessagingRuntime>(), registrations.Handlers));
            services.TryAddSingleton<IMessageAdmin>(sp => new MessageAdmin(sp.GetRequiredService<MessagingRuntime>()));
            services.TryAddSingleton(sp => new MessagingStatus(sp.GetRequiredService<MessagingRuntime>(), registrations));
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, MessagingHostedService>());
        }

        configure?.Invoke(new FoundatioMessagingBuilder(services, registrations));
        return services;
    }

    private static MessagingRuntime CreateRuntime(IServiceProvider services, MessagingRegistrations registrations)
    {
        var options = services.GetRequiredService<IOptions<MessagingOptions>>().Value;
        options.Validate();
        options.DefaultRetryPolicy.Validate();

        var transport = services.GetService<TestHarnessRegistration>()?.Transport
            ?? services.GetService<IMessageTransport>()
            ?? throw new InvalidOperationException("No message transport is configured. Inside AddFoundatioMessaging, call UseInMemory(), a provider's UseRedis(), UseSqs(), UseKafka(), UseAzureServiceBus() or UseRabbitMQ() (from Foundatio.Redis, Foundatio.AWS, Foundatio.Kafka, Foundatio.AzureServiceBus or Foundatio.RabbitMQ), or UseTransport().");

        foreach (var handler in registrations.Handlers)
            handler.Resolve(services, handler.Kind == MessageHandlerKind.Consumer ? FoundatioMessagingBuilder.ValidateConsumer : FoundatioMessagingBuilder.Validate, options.TestRetryDelay);

        var types = new MessageTypeRegistry();
        foreach (var (type, name, queue, topic) in registrations.Maps)
            types.Map(type, name, queue, topic);
        foreach (var handler in registrations.Handlers)
            types.GetRoute(handler.MessageType);

        string serviceName = options.ServiceName
            ?? services.GetService<IHostEnvironment>()?.ApplicationName
            ?? Assembly.GetEntryAssembly()?.GetName().Name
            ?? "foundatio";

        var runtime = new MessagingRuntime(transport, options, types,
            options.Serializer ?? services.GetService<ISerializer>() ?? DefaultSerializer.Instance,
            services.GetRequiredService<IServiceScopeFactory>(),
            services.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance,
            options.TimeProvider ?? services.GetService<TimeProvider>() ?? TimeProvider.System,
            serviceName, serviceNameIsDefault: options.ServiceName is null)
        {
            Observer = services.GetService<IMessagingObserver>(),
            Tracker = services.GetRequiredService<WorkTracker>()
        };

        foreach (var messageType in registrations.Handlers.Select(h => h.MessageType).Distinct())
            runtime.RegisterDerivedTypes(messageType);

        HandlerRegistrationValidator.Validate(runtime, registrations.Handlers);
        return runtime;
    }
}
