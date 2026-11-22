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
    /// <see cref="FoundatioTestHarness"/>. Call it before or after the app's own registrations: either way it replaces
    /// their transport and store, and a later <c>UseTransport</c>, <c>UseStore</c> or provider <c>UseRedis()</c> and the
    /// like are ignored. Then resolve <see cref="FoundatioTestHarness"/> from the service provider. CRON schedules only run when
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

        var timeProvider = options.TimeProvider ?? TimeProvider.System;
        services.RemoveAll<TestHarnessRegistration>();
        services.AddSingleton(_ => new TestHarnessRegistration(options));
        services.PostConfigure<MessagingOptions>(o => o.TimeProvider = timeProvider);
        services.PostConfigure<JobWorkerOptions>(o => o.TimeProvider = timeProvider);
        if (options.RetryDelay is { } retryDelay)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(retryDelay, TimeSpan.Zero, nameof(options.RetryDelay));
            var delay = Resilience.RetryPolicy.Fixed(retryDelay);
            services.PostConfigure<MessagingOptions>(o =>
            {
                o.TestRetryDelay = retryDelay;
                o.DefaultRetryPolicy = o.DefaultRetryPolicy with { Delay = delay };
            });
            services.PostConfigure<JobWorkerOptions>(o =>
            {
                o.TestRetryDelay = retryDelay;
                o.DefaultRetryPolicy = o.DefaultRetryPolicy with { Delay = delay };
            });
        }
        services.RemoveAll<IMessageTransport>();
        services.AddSingleton<IMessageTransport>(sp => sp.GetRequiredService<TestHarnessRegistration>().Transport);
        services.RemoveAll<IJobStore>();
        services.AddSingleton<IJobStore>(sp => sp.GetRequiredService<TestHarnessRegistration>().Store);
        services.TryAddSingleton(sp => new FoundatioTestHarness(sp));
        services.TryAddSingleton<IMessagingObserver>(sp => sp.GetRequiredService<FoundatioTestHarness>());
        services.TryAddSingleton<IJobsObserver>(sp => sp.GetRequiredService<FoundatioTestHarness>());
        return services;
    }
}
