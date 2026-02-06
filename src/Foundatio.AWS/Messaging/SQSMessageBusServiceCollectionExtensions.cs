using System;
using Foundatio.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Foundatio.Extensions.Hosting.Messaging;

public static class SQSMessageBusServiceCollectionExtensions
{
    /// <summary>
    /// Adds an SQS message bus to the service collection.
    /// </summary>
    public static IServiceCollection AddSQSMessageBus(
        this IServiceCollection services,
        Action<SQSMessageBusOptionsBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        services.AddSingleton<IMessageBus>(sp =>
        {
            var builder = new SQSMessageBusOptionsBuilder();
            builder.LoggerFactory(sp.GetService<ILoggerFactory>());
            configure(builder);
            return new SQSMessageBus(builder.Build());
        });

        services.AddSingleton<IMessagePublisher>(sp => sp.GetRequiredService<IMessageBus>());
        services.AddSingleton<IMessageSubscriber>(sp => sp.GetRequiredService<IMessageBus>());

        return services;
    }

    /// <summary>
    /// Adds an SQS message bus to the service collection using a connection string.
    /// </summary>
    public static IServiceCollection AddSQSMessageBus(
        this IServiceCollection services,
        string connectionString,
        Action<SQSMessageBusOptionsBuilder> configure = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionString);

        return services.AddSQSMessageBus(builder =>
        {
            builder.ConnectionString(connectionString);
            configure?.Invoke(builder);
        });
    }
}
