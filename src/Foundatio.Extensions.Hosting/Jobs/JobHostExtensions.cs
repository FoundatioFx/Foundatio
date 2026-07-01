using System;
using Foundatio.Jobs;
using Microsoft.Extensions.DependencyInjection;

namespace Foundatio.Extensions.Hosting.Jobs;

public static class JobHostExtensions
{
    /// <summary>
    /// Registers the hosted pump that drives the durable job runtime (<see cref="Foundatio.Jobs.IJobRuntimeStore"/>):
    /// materializing CRON occurrences, dispatching delayed/scheduled work, recovering stale occurrences, and running
    /// jobs submitted via <see cref="Foundatio.Jobs.IJobClient"/>. Register the runtime store and job services first
    /// (e.g. <c>services.AddFoundatio().Jobs.UseInMemoryRuntime()</c>).
    /// </summary>
    public static IServiceCollection AddJobRuntimeService(this IServiceCollection services, Action<JobRuntimeServiceOptions>? configure = null)
    {
        var options = new JobRuntimeServiceOptions();
        configure?.Invoke(options);

        // Registering a runtime store (AddFoundatio().Jobs.UseRuntimeStore()/UseInMemoryRuntime()) is the precondition
        // for this call, and that already auto-registers the single runtime pump (JobRuntimePumpService). So this method
        // only carries options onto that pump — it never starts a second pump — which keeps a single pump regardless of
        // the order AddJobRuntimeService and UseRuntimeStore are called in.
        services.AddSingleton(new JobRuntimePumpOptions
        {
            Enabled = options.Enabled,
            PollInterval = options.PollInterval,
            BatchSize = options.BatchSize,
            MaxJobAttempts = options.MaxJobAttempts
        });

        return services;
    }
}
