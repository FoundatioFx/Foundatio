using System;
using System.Collections.Generic;
using System.Reflection;
using Azure.Identity;
using Foundatio.Messaging.Transport;
using Xunit;

namespace Foundatio.AzureServiceBus.Tests.Messaging;

public class AzureServiceBusMessageTransportOptionsTests
{
    private const string ConnectionString = "Endpoint=sb://example.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=c2VjcmV0LWtleQ==";

    [Fact]
    public void ToString_WithSecretsInTheConnectionStrings_RedactsOnlyTheSecrets()
    {
        // Arrange
        var options = new AzureServiceBusMessageTransportOptions
        {
            ConnectionString = ConnectionString,
            AdministrationConnectionString = "Endpoint=sb://example.servicebus.windows.net/;SharedAccessSignature=SharedAccessSignature sr=x&sig=c2lnbmF0dXJl",
            Prefix = "dev-"
        };

        // Act
        string text = options.ToString();

        // Assert
        Assert.DoesNotContain("c2VjcmV0LWtleQ", text);
        Assert.DoesNotContain("c2lnbmF0dXJl", text);
        Assert.Contains("SharedAccessKey=***", text);
        Assert.Contains("SharedAccessSignature=***", text);
        Assert.Contains("Endpoint=sb://example.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;", text);
        foreach (var property in typeof(AzureServiceBusMessageTransportOptions).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            Assert.Contains($"{property.Name} = ", text);
    }

    public static TheoryData<string, Func<AzureServiceBusMessageTransportOptions, AzureServiceBusMessageTransportOptions>> InvalidOptions => new()
    {
        { "lock under 5 seconds", o => o with { LockDuration = TimeSpan.FromSeconds(4) } },
        { "lock over 5 minutes", o => o with { LockDuration = TimeSpan.FromMinutes(6) } },
        { "max delivery count 0", o => o with { MaxDeliveryCount = 0 } },
        { "message size under 16 KiB", o => o with { MaxMessageSize = 1024 } },
        { "instance timeout under 5 minutes", o => o with { InstanceSubscriptionIdleTimeout = TimeSpan.FromMinutes(1) } },
        { "duplicate window under 20 seconds", o => o with { DuplicateDetectionWindow = TimeSpan.FromSeconds(10) } },
        { "duplicate window over 7 days", o => o with { DuplicateDetectionWindow = TimeSpan.FromDays(8) } },
        { "no sessions", o => o with { MaxConcurrentSessions = 0 } },
        { "prefix", o => o with { Prefix = "-dev" } },
        { "no connection", o => o with { ConnectionString = null } },
        { "connection string and namespace", o => o with { FullyQualifiedNamespace = "example.servicebus.windows.net" } },
        { "namespace without credential", o => o with { ConnectionString = null, FullyQualifiedNamespace = "example.servicebus.windows.net" } }
    };

    [Theory]
    [MemberData(nameof(InvalidOptions))]
    public void Constructor_WithInvalidOptions_ThrowsArgumentException(string invalid, Func<AzureServiceBusMessageTransportOptions, AzureServiceBusMessageTransportOptions> change)
    {
        _ = invalid;
        var options = change(new AzureServiceBusMessageTransportOptions { ConnectionString = ConnectionString });

        Assert.ThrowsAny<ArgumentException>(() => new AzureServiceBusMessageTransport(options));
    }

    [Fact]
    public void Constructor_WithANamespaceAndCredential_Succeeds()
    {
        var transport = new AzureServiceBusMessageTransport(new AzureServiceBusMessageTransportOptions
        {
            FullyQualifiedNamespace = "example.servicebus.windows.net",
            Credential = new DefaultAzureCredential()
        });

        Assert.False(transport.IsEmulator);
    }

    [Theory]
    [InlineData("Endpoint=sb://localhost;SharedAccessKeyName=k;SharedAccessKey=v;UseDevelopmentEmulator=true", "Endpoint=sb://localhost:5300;SharedAccessKeyName=k;SharedAccessKey=v;UseDevelopmentEmulator=true")]
    [InlineData("Endpoint=sb://localhost:5672/;SharedAccessKeyName=k;SharedAccessKey=v;UseDevelopmentEmulator=true;", "Endpoint=sb://localhost:5300;SharedAccessKeyName=k;SharedAccessKey=v;UseDevelopmentEmulator=true;")]
    [InlineData(ConnectionString, ConnectionString)]
    public void GetAdministrationConnectionString_ForTheEmulator_UsesItsManagementPort(string connectionString, string expected)
    {
        Assert.Equal(expected, AzureServiceBusMessageTransport.GetAdministrationConnectionString(connectionString));
    }

    public static TheoryData<bool, bool> Modes => new() { { false, false }, { true, false }, { false, true }, { true, true } };

    [Theory]
    [MemberData(nameof(Modes))]
    public void Capabilities_FollowTheModeAndTheMessageSizeLimit(bool sessions, bool duplicateDetection)
    {
        // Act
        var capabilities = new AzureServiceBusMessageTransport(new AzureServiceBusMessageTransportOptions
        {
            ConnectionString = ConnectionString,
            RequiresSession = sessions,
            RequiresDuplicateDetection = duplicateDetection,
            MaxMessageSize = 1024 * 1024
        }).Capabilities;

        // Assert
        Assert.Equal(sessions, capabilities.OrderedGroups);
        Assert.Equal(duplicateDetection, capabilities.Deduplication);
        Assert.Null(capabilities.MaxDelay);
        Assert.Null(capabilities.MaxPublishDelay);
        Assert.Null(capabilities.MaxRetryDelay);
        Assert.True(capabilities.LeaseExpiration);
        Assert.Equal(TimeSpan.FromMinutes(5), capabilities.MaxLeaseDuration);
        Assert.Equal(1024 * 1024 - AzureServiceBusMessageTransportCodec.ReservedBytes, capabilities.MaxMessageSize);
        Assert.Equal(new List<int> { 100, 100 }, new List<int> { capabilities.MaxSendBatchSize, capabilities.MaxReceiveBatchSize });
    }
}
