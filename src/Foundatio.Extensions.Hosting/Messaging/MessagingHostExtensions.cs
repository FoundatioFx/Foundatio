using System;
using Foundatio.Jobs;
using Foundatio.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Foundatio.Extensions.Hosting.Messaging;

public static class MessagingHostExtensions
{
    /// <summary>Starts registered queue consumers and event subscribers for the host lifetime.</summary>
    public static IServiceCollection AddMessageConsumers(this IServiceCollection services)
    {
        services.AddMessagingTopology();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, MessageHandlerHostedService>());
        return services;
    }

    /// <summary>Ensures or validates declared messaging topology at startup using the configured topology mode.</summary>
    public static IServiceCollection AddMessagingTopology(this IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, MessagingTopologyStartupService>());
        return services;
    }

    /// <summary>Dispatches persisted delayed messages independently of job execution.</summary>
    public static IServiceCollection AddScheduledMessageDispatcher(this IServiceCollection services)
    {
        services.TryAddSingleton<FoundatioRuntimeHealth>();
        services.TryAddSingleton(sp => new ScheduledMessageDispatcher(
            sp.GetService<IScheduledDispatchStore>() ?? sp.GetRequiredService<IJobRuntimeStore>(),
            sp.GetRequiredService<IMessageTransport>(),
            new ScheduledMessageDispatcherOptions { TimeProvider = sp.GetService<TimeProvider>(), LoggerFactory = sp.GetService<ILoggerFactory>(), TopologyMode = sp.GetService<MessagingTopologyOptions>()?.Mode ?? TopologyMode.Ensure }));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, ScheduledMessageDispatcherService>());
        return services;
    }
}
