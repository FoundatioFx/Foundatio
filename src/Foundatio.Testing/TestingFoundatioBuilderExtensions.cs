using System;
using Foundatio.Jobs;
using Foundatio.Jobs.Testing;
using Foundatio.Messaging;
using Foundatio.Messaging.Testing;
using Foundatio.Serializer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Foundatio;

public static class TestingFoundatioBuilderExtensions
{
    /// <summary>
    /// Runs messaging over a recording in-memory transport for tests. Resolve <see cref="MessagingTestHarness"/> from
    /// the container to await quiescence (<see cref="MessagingTestHarness.WaitForIdleAsync"/>) and assert on the
    /// messages that were sent, published, handled, retried, or dead-lettered.
    /// </summary>
    public static FoundatioBuilder UseTestHarness(this FoundatioBuilder.MessagingBuilder builder)
    {
        var services = ((IFoundatioBuilder)builder).Services;
        services.TryAddSingleton(sp => new MessagingTestHarness(
            sp.GetService<ISerializer>(),
            sp.GetService<IMessageTypeRegistry>(),
            sp.GetService<TimeProvider>()));
        return builder.UseTransport(sp => sp.GetRequiredService<MessagingTestHarness>().Transport);
    }

    /// <summary>
    /// Runs jobs over the in-memory runtime with the auto pump disabled, so nothing races the test's manual drive.
    /// Resolve <see cref="JobsTestHarness"/> from the container to enqueue jobs, tick schedules deterministically,
    /// and run work to completion (<see cref="JobsTestHarness.RunAllQueuedAsync"/> /
    /// <see cref="JobsTestHarness.RunDueAsync"/> / <see cref="JobsTestHarness.RunToCompletionAsync"/>).
    /// </summary>
    public static FoundatioBuilder UseTestHarness(this FoundatioBuilder.JobsBuilder builder)
    {
        var services = ((IFoundatioBuilder)builder).Services;
        services.TryAddSingleton(sp => new JobsTestHarness(
            sp.GetRequiredService<IJobRuntimeStore>(),
            sp.GetRequiredService<IJobWorker>(),
            sp.GetRequiredService<JobScheduleProcessor>(),
            sp.GetRequiredService<IJobClient>(),
            sp.GetRequiredService<IScheduledJobManager>()));
        builder.UseInMemory();
        // The auto-registered pump must never race the harness's manual drive.
        return builder.ConfigureRuntimePump(options => options.Enabled = false);
    }
}
