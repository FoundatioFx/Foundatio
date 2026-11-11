using System;
using System.Linq;
using System.Reflection;
using Foundatio.Messaging;
using Foundatio.Messaging.Transport;
using Foundatio.Serializer;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Microsoft.Extensions.DependencyInjection;

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
            services.TryAddSingleton(sp => CreateRuntime(sp, registrations));
            services.TryAddSingleton<IMessageClient>(sp => new MessageClient(sp.GetRequiredService<MessagingRuntime>()));
            services.TryAddSingleton<IMessageAdmin>(sp => new MessageAdmin(sp.GetRequiredService<MessagingRuntime>()));
            services.TryAddSingleton(sp => new MessagingStatus(sp.GetRequiredService<MessagingRuntime>(), registrations));
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, MessagingHostedService>());
        }

        configure?.Invoke(new FoundatioMessagingBuilder(services, registrations));
        return services;
    }

    private static MessagingRuntime CreateRuntime(IServiceProvider services, MessagingRegistrations registrations)
    {
        var options = new MessagingOptions();
        foreach (var configure in registrations.ConfigureOptions)
            configure(options);
        options.Validate();
        options.DefaultRetryPolicy.Validate();

        var transport = services.GetService<IMessageTransport>()
            ?? throw new InvalidOperationException("No message transport is configured. Call UseInMemory() or a provider such as UseRedis() inside AddFoundatioMessaging.");

        var types = new MessageTypeRegistry();
        foreach (var (type, name, queue, topic) in registrations.Maps)
            types.Map(type, name, queue, topic);
        foreach (var handler in registrations.Handlers)
            types.GetRoute(handler.MessageType);

        string serviceName = options.ServiceName
            ?? services.GetService<IHostEnvironment>()?.ApplicationName
            ?? Assembly.GetEntryAssembly()?.GetName().Name
            ?? "foundatio";

        return new MessagingRuntime(transport, options, types,
            options.Serializer ?? services.GetService<ISerializer>() ?? DefaultSerializer.Instance,
            services.GetRequiredService<IServiceScopeFactory>(),
            services.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance,
            services.GetService<TimeProvider>() ?? TimeProvider.System,
            serviceName)
        {
            Observer = services.GetService<IMessagingObserver>()
        };
    }
}
