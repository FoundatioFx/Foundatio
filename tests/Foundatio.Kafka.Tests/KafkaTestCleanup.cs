using System;
using System.Linq;
using System.Threading.Tasks;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Xunit;

[assembly: AssemblyFixture(typeof(Foundatio.Kafka.Tests.KafkaTestCleanup))]

namespace Foundatio.Kafka.Tests;

/// <summary>Deletes the topics and consumer groups of this run's <see cref="KafkaTestConnection.Prefix"/> when the run ends.</summary>
public sealed class KafkaTestCleanup : IAsyncDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public async ValueTask DisposeAsync()
    {
        string prefix = KafkaTestConnection.Prefix;
        try
        {
            using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = KafkaTestConnection.BootstrapServers }).Build();
            var groups = (await admin.ListConsumerGroupsAsync(new ListConsumerGroupsOptions { RequestTimeout = Timeout })).Valid
                .Select(g => g.GroupId).Where(g => g.StartsWith(prefix, StringComparison.Ordinal)).ToList();
            if (groups.Count > 0)
            {
                try
                {
                    await admin.DeleteGroupsAsync(groups, new DeleteGroupsOptions { RequestTimeout = Timeout });
                }
                catch (DeleteGroupsException)
                {
                }
            }

            var topics = admin.GetMetadata(Timeout).Topics.Select(t => t.Topic).Where(t => t.StartsWith(prefix, StringComparison.Ordinal)).ToList();
            if (topics.Count > 0)
            {
                try
                {
                    await admin.DeleteTopicsAsync(topics, new DeleteTopicsOptions { RequestTimeout = Timeout });
                }
                catch (DeleteTopicsException)
                {
                }
            }
        }
        catch (KafkaException)
        {
            // No broker: the tests failed or were skipped, and left nothing behind.
        }
    }
}
