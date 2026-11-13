using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Azure.Core.Amqp;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Foundatio.Messaging;
using Foundatio.Messaging.Transport;
using Foundatio.Tests.Messaging;
using Foundatio.Tests.Utility;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Foundatio.AzureServiceBus.Tests.Messaging;

/// <summary>
/// Runs the conformance suite against queues, topics and subscriptions without sessions or duplicate detection, plus what
/// only the Service Bus transport does. Each test gets its own name prefix, and its entities, topics included, are
/// deleted when it ends, so the emulator's limit of 50 queues and topics is never reached.
/// </summary>
public class AzureServiceBusMessageTransportTests(ITestOutputHelper output) : MessageTransportTestsBase(output)
{
    /// <summary>The Service Bus emulator accepts at most 10.</summary>
    private const int EmulatorMaxDeliveryCount = 10;

    private readonly string _prefix = $"ft{Guid.NewGuid():N}"[..10] + "-";
    private ServiceBusClient? _client;

    /// <summary>Runs every test against session-enabled queues and subscriptions when true.</summary>
    protected virtual bool RequiresSession => false;

    /// <summary>Runs every test against queues and topics with duplicate detection when true.</summary>
    protected virtual bool RequiresDuplicateDetection => false;

    /// <summary>The lock duration of every entity the tests create, so lease tests wait for it.</summary>
    protected override TimeSpan ShortLease => TimeSpan.FromSeconds(5);

    protected override IMessageTransport CreateTransport() => new AzureServiceBusMessageTransport(CreateOptions());

    /// <summary>A client of its own, as another producer or consumer of the namespace would use.</summary>
    protected ServiceBusClient Client => _client ??= new ServiceBusClient(GetConnectionString());

    protected static ServiceBusAdministrationClient CreateAdministrationClient() =>
        new(AzureServiceBusMessageTransport.GetAdministrationConnectionString(GetConnectionString()));

    protected string Prefix => _prefix;

    protected AzureServiceBusMessageTransportOptions CreateOptions() => new()
    {
        ConnectionString = GetConnectionString(),
        Prefix = _prefix,
        RequiresSession = RequiresSession,
        RequiresDuplicateDetection = RequiresDuplicateDetection,
        DuplicateDetectionWindow = TimeSpan.FromMinutes(1),
        LockDuration = ShortLease,
        MaxDeliveryCount = EmulatorMaxDeliveryCount,
        LoggerFactory = Log
    };

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (_client is not null)
            await _client.DisposeAsync();

        await DeleteEntitiesAsync();
    }

    /// <summary>Deletes every queue and topic with this test's prefix; the emulator lowercases names.</summary>
    private async Task DeleteEntitiesAsync()
    {
        try
        {
            var admin = CreateAdministrationClient();
            var queues = new List<string>();
            await foreach (var queue in admin.GetQueuesAsync())
            {
                if (queue.Name.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase))
                    queues.Add(queue.Name);
            }

            var topics = new List<string>();
            await foreach (var topic in admin.GetTopicsAsync())
            {
                if (topic.Name.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase))
                    topics.Add(topic.Name);
            }

            foreach (string queue in queues)
                await admin.DeleteQueueAsync(queue);
            foreach (string topic in topics)
                await admin.DeleteTopicAsync(topic);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not delete the entities of prefix {Prefix}", _prefix);
        }
    }

    protected static string GetConnectionString()
    {
        string? connectionString = Configuration.GetConnectionString("AzureServiceBusConnectionString");
        Assert.SkipWhen(String.IsNullOrEmpty(connectionString), "ConnectionStrings:AzureServiceBusConnectionString is not configured.");
        return connectionString;
    }
}
