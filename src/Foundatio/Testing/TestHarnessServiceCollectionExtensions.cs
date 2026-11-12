using System;
using Foundatio.Extensions;
using Foundatio.Jobs;
using Foundatio.Jobs.Store;
using Foundatio.Messaging;
using Foundatio.Messaging.Transport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Foundatio.Testing;

/// <summary>Registers <see cref="FoundatioTestHarness"/> for an app's integration tests.</summary>
public static class TestHarnessServiceCollectionExtensions
{
    /// <summary>
    /// Runs the app's messaging and jobs on a fresh in-memory transport and job store, and records what they do in
    /// <see cref="FoundatioTestHarness"/>. Call it after the app's own registrations so it replaces their transport and
    /// store, then resolve <see cref="FoundatioTestHarness"/> from the service provider. CRON schedules only run when
    /// triggered with <see cref="FoundatioTestHarness.TriggerCronJobAsync{TJobOrArgs}"/>. Set
    /// <see cref="FoundatioTestHarnessOptions.TransportCapabilities"/> to emulate your production transport's guarantees.
    /// </summary>
    /// <example>
    /// <code>
    /// var builder = Host.CreateApplicationBuilder();
    /// builder.Services.AddMyApp();                  // the app's AddFoundatioMessaging / AddFoundatioJobs calls
    /// builder.Services.AddFoundatioTestHarness();
    /// using var host = builder.Build();
    /// await host.StartAsync();
    /// var harness = host.Services.GetRequiredService&lt;FoundatioTestHarness&gt;();
    /// </code>
    /// </example>
    public static IServiceCollection AddFoundatioTestHarness(this IServiceCollection services, Action<FoundatioTestHarnessOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new FoundatioTestHarnessOptions();
        configure?.Invoke(options);
        var capabilities = options.TransportCapabilities;
        if (capabilities is not null)
            InMemoryMessageTransport.ValidateCapabilities(capabilities);

        services.ReplaceSingleton<IMessageTransport>(sp => capabilities is null
            ? new InMemoryMessageTransport(sp.GetService<TimeProvider>())
            : new InMemoryMessageTransport(capabilities, sp.GetService<TimeProvider>()));
        services.ReplaceSingleton<IJobStore>(sp => new InMemoryJobStore(sp.GetService<TimeProvider>()));
        services.TryAddSingleton(sp => new FoundatioTestHarness(sp));
        services.TryAddSingleton<IMessagingObserver>(sp => sp.GetRequiredService<FoundatioTestHarness>());
        services.TryAddSingleton<IJobsObserver>(sp => sp.GetRequiredService<FoundatioTestHarness>());
        return services;
    }
}
