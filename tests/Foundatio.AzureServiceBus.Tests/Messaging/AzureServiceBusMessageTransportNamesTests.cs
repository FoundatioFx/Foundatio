using System;
using System.Linq;
using Foundatio.Messaging.Transport;
using Xunit;

namespace Foundatio.AzureServiceBus.Tests.Messaging;

public class AzureServiceBusMessageTransportNamesTests
{
    private const string LegalEntityName = "^[A-Za-z0-9]([A-Za-z0-9._-]*[A-Za-z0-9])?$";

    [Fact]
    public void QueueAndTopic_WithALegalName_KeepTheNameAfterThePrefixAndTheirKind()
    {
        // Arrange
        var names = new AzureServiceBusMessageTransportNames("dev-");

        // Act
        string queue = names.Queue("Orders.Placed_v2");
        string topic = names.Topic("Orders.Placed_v2");

        // Assert
        Assert.Equal("dev-queue.Orders.Placed_v2", queue);
        Assert.Equal("dev-topic.Orders.Placed_v2", topic);
        Assert.Equal("queue.-leading", new AzureServiceBusMessageTransportNames(String.Empty).Queue("-leading"));
    }

    [Theory]
    [InlineData("trailing-")]
    [InlineData("trailing.")]
    [InlineData("_")]
    public void Queue_EndingInACharacterServiceBusDoesNotAllowThere_IsTrimmedAndHashed(string name)
    {
        // Arrange
        var names = new AzureServiceBusMessageTransportNames(String.Empty);

        // Act
        string queue = names.Queue(name);

        // Assert
        Assert.Matches(LegalEntityName, queue);
        Assert.Matches("-[0-9a-f]{12}$", queue);
        Assert.Equal(queue, names.Queue(name));
    }

    [Fact]
    public void QueueAndTopic_LongerThanServiceBusAllows_AreShortenedAndDoNotCollide()
    {
        // Arrange
        var names = new AzureServiceBusMessageTransportNames("dev-");
        string tooLong = new('x', 300);

        // Act
        string[] mapped = [names.Queue(tooLong + "1"), names.Queue(tooLong + "2"), names.Topic(tooLong + "1"), names.Topic(tooLong + "2")];

        // Assert
        Assert.All(mapped, name => Assert.InRange(name.Length, 1, AzureServiceBusMessageTransportNames.MaxEntityNameLength));
        Assert.All(mapped, name => Assert.Matches(LegalEntityName, name));
        Assert.Equal(mapped.Length, mapped.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Subscription_UpToFiftyCharacters_IsUsedAsIsAndLongerOnesAreHashed()
    {
        // Arrange
        string fits = new('s', 50);
        string tooLong = new('s', 51);

        // Act
        string kept = AzureServiceBusMessageTransportNames.Subscription(fits);
        string shortened = AzureServiceBusMessageTransportNames.Subscription(tooLong);

        // Assert
        Assert.Equal(fits, kept);
        Assert.Equal(AzureServiceBusMessageTransportNames.MaxSubscriptionNameLength, shortened.Length);
        Assert.Matches("^s+-[0-9a-f]{12}$", shortened);
        Assert.NotEqual(shortened, AzureServiceBusMessageTransportNames.Subscription(tooLong + "x"));
    }

    [Fact]
    public void NewInstance_IsUniqueLegalAndStartsWithTheHint()
    {
        // Act
        string first = AzureServiceBusMessageTransportNames.NewInstance("billing-service.worker-with-a-very-long-name");
        string second = AzureServiceBusMessageTransportNames.NewInstance("billing-service.worker-with-a-very-long-name");

        // Assert
        Assert.NotEqual(first, second);
        Assert.All(new[] { first, second }, name =>
        {
            Assert.StartsWith("billing-service.worker", name);
            Assert.InRange(name.Length, 1, AzureServiceBusMessageTransportNames.MaxSubscriptionNameLength);
            Assert.Matches(LegalEntityName, name);
        });
    }

    [Fact]
    public void SubscriptionDeduplicationId_DependsOnTheSubscriptionAndFitsAServiceBusMessageId()
    {
        // Act
        string first = AzureServiceBusMessageTransportNames.SubscriptionDeduplicationId("billing", "order-42");
        string other = AzureServiceBusMessageTransportNames.SubscriptionDeduplicationId("shipping", "order-42");

        // Assert
        Assert.Equal(first, AzureServiceBusMessageTransportNames.SubscriptionDeduplicationId("billing", "order-42"));
        Assert.NotEqual(first, other);
        Assert.NotEqual("order-42", first);
        Assert.InRange(first.Length, 1, 128);
    }

    [Theory]
    [InlineData("-dev")]
    [InlineData("dev/")]
    [InlineData("this-prefix-is-longer-than-thirty-two")]
    public void Constructor_WithAnInvalidPrefix_ThrowsArgumentException(string prefix)
    {
        Assert.Throws<ArgumentException>(() => new AzureServiceBusMessageTransportNames(prefix));
    }
}
