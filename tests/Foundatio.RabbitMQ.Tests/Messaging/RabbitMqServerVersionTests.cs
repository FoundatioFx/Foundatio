using System;
using System.Collections.Generic;
using System.Text;
using Foundatio.Messaging;
using Xunit;

namespace Foundatio.RabbitMQ.Tests.Messaging;

public class RabbitMqServerVersionTests
{
    [Theory]
    [InlineData("3.13.7", 3, 13)]
    [InlineData("4.0.5", 4, 0)]
    [InlineData("4.2.0", 4, 2)]
    [InlineData("4.3.0", 4, 3)]
    [InlineData("4.3.1", 4, 3)]
    [InlineData("5.0.0", 5, 0)]
    public void ParseServerVersion_WithValidVersionBytes_ReturnsVersion(string versionString, int major, int minor)
    {
        // Arrange
        var properties = new Dictionary<string, object?>
        {
            ["version"] = Encoding.UTF8.GetBytes(versionString)
        };

        // Act
        var result = RabbitMQMessageBus.ParseServerVersion(properties);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(major, result.Major);
        Assert.Equal(minor, result.Minor);
    }

    [Fact]
    public void ParseServerVersion_WithNullProperties_ReturnsNull()
    {
        // Act
        var result = RabbitMQMessageBus.ParseServerVersion(null);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ParseServerVersion_WithMissingVersionKey_ReturnsNull()
    {
        // Arrange
        var properties = new Dictionary<string, object?>
        {
            ["product"] = Encoding.UTF8.GetBytes("RabbitMQ")
        };

        // Act
        var result = RabbitMQMessageBus.ParseServerVersion(properties);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ParseServerVersion_WithNonByteArrayValue_ReturnsNull()
    {
        // Arrange
        var properties = new Dictionary<string, object?>
        {
            ["version"] = "4.2.0"
        };

        // Act
        var result = RabbitMQMessageBus.ParseServerVersion(properties);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ParseServerVersion_WithInvalidVersionBytes_ReturnsNull()
    {
        // Arrange
        var properties = new Dictionary<string, object?>
        {
            ["version"] = Encoding.UTF8.GetBytes("not-a-version")
        };

        // Act
        var result = RabbitMQMessageBus.ParseServerVersion(properties);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void VersionComparison_WithLowerVersion_DetectsAsBelow()
    {
        // Arrange
        var rmq42 = new Version(4, 2, 0);
        var rmq43 = new Version(4, 3);

        // Act
        bool isBelow = rmq42 < rmq43;

        // Assert
        Assert.True(isBelow);
    }

    [Fact]
    public void VersionComparison_WithExactThreshold_DetectsAsAtOrAbove()
    {
        // Arrange
        var rmq43 = new Version(4, 3, 0);
        var threshold = new Version(4, 3);

        // Act
        bool isAtOrAbove = rmq43 >= threshold;

        // Assert
        Assert.True(isAtOrAbove);
    }

    [Fact]
    public void VersionComparison_WithHigherMajor_DetectsAsAbove()
    {
        // Arrange
        var rmq50 = new Version(5, 0, 0);
        var threshold = new Version(4, 3);

        // Act
        bool isAbove = rmq50 >= threshold;

        // Assert
        Assert.True(isAbove);
    }
}
