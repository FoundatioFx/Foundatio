using System;
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
}
