using System;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Foundatio.Utility;
using Foundatio.Xunit;
using RabbitMQ.Client;
using Xunit;

namespace Foundatio.RabbitMQ.Tests;

public class RabbitMqEndpointResolverTests(ITestOutputHelper output) : TestWithLoggingBase(output)
{
    [Fact]
    public void CreateEndpoints_WithCustomServerValidation_RejectsUnsupportedPolicy()
    {
        // Arrange
        var factory = new ConnectionFactory { Uri = new Uri("amqps://localhost") };
        factory.Ssl.CertificateValidationCallback = (_, _, _, _) => true;

        // Act
        var exception = Record.Exception(() => RabbitMQEndpointResolver.CreateEndpoints(factory));

        // Assert
        Assert.IsType<ArgumentException>(exception);
    }

    [Theory]
    [InlineData("broker:0")]
    [InlineData("broker:65536")]
    [InlineData("broker:")]
    [InlineData("broker:invalid")]
    [InlineData("[::1")]
    [InlineData("[::1]unexpected")]
    [InlineData(" ")]
    public void CreateEndpoints_WithInvalidHost_RejectsConfiguration(string host)
    {
        // Arrange
        var factory = new ConnectionFactory { Uri = new Uri("amqps://localhost") };

        // Act
        var exception = Record.Exception(() => RabbitMQEndpointResolver.CreateEndpoints(factory, [host]));

        // Assert
        Assert.IsAssignableFrom<ArgumentException>(exception);
    }

    [Fact]
    public void CreateEndpoints_WithNullFactory_ThrowsArgumentNullException()
    {
        // Arrange
        ConnectionFactory factory = null!;

        // Act
        var exception = Record.Exception(() => RabbitMQEndpointResolver.CreateEndpoints(factory));

        // Assert
        Assert.IsType<ArgumentNullException>(exception);
    }

    [Fact]
    public void CreateEndpoints_WithOutOfRangeFactoryPort_ThrowsArgumentOutOfRangeException()
    {
        // Arrange
        var factory = new ConnectionFactory { Uri = new Uri("amqps://localhost:0") };

        // Act
        var exception = Record.Exception(() => RabbitMQEndpointResolver.CreateEndpoints(factory));

        // Assert
        Assert.IsType<ArgumentOutOfRangeException>(exception);
    }

    [Theory]
    [InlineData("broker:0")]
    [InlineData("broker:65536")]
    public void CreateEndpoints_WithOutOfRangeReplacementPort_ThrowsArgumentOutOfRangeException(string host)
    {
        // Arrange
        var factory = new ConnectionFactory { Uri = new Uri("amqps://localhost") };

        // Act
        var exception = Record.Exception(() => RabbitMQEndpointResolver.CreateEndpoints(factory, [host]));

        // Assert
        Assert.IsType<ArgumentOutOfRangeException>(exception);
    }

    [Theory]
    [InlineData("amqp://localhost", 5672, false)]
    [InlineData("amqps://localhost", 5671, true)]
    [InlineData("amqps://localhost:5680", 5680, true)]
    public void CreateEndpoints_WithoutReplacementHosts_UsesFactoryEndpoint(string uri, int port, bool useTls)
    {
        // Arrange
        var factory = new ConnectionFactory { Uri = new Uri(uri) };

        // Act
        var endpoints = RabbitMQEndpointResolver.CreateEndpoints(factory);

        // Assert
        var endpoint = Assert.Single(endpoints);
        Assert.Equal("localhost", endpoint.HostName);
        Assert.Equal(port, endpoint.Port);
        Assert.Equal(useTls, endpoint.Ssl.Enabled);
        Assert.Equal("localhost", endpoint.Ssl.ServerName);
        Assert.Equal(SslPolicyErrors.None, endpoint.Ssl.AcceptablePolicyErrors);
    }

    [Theory]
    [InlineData("broker", "broker", 5671)]
    [InlineData("broker:5680", "broker", 5680)]
    [InlineData("[::1]:5680", "::1", 5680)]
    [InlineData("::1", "::1", 5671)]
    public void CreateEndpoints_WithReplacementHost_PreservesTlsIdentity(string host, string hostname, int port)
    {
        // Arrange
        var factory = new ConnectionFactory { Uri = new Uri("amqps://localhost") };

        // Act
        var endpoints = RabbitMQEndpointResolver.CreateEndpoints(factory, [host, host, " "]);

        // Assert
        var endpoint = Assert.Single(endpoints);
        Assert.Equal(hostname, endpoint.HostName);
        Assert.Equal(port, endpoint.Port);
        Assert.True(endpoint.Ssl.Enabled);
        Assert.Equal(hostname, endpoint.Ssl.ServerName);
        Assert.Equal(SslPolicyErrors.None, endpoint.Ssl.AcceptablePolicyErrors);
    }

    [Fact]
    public void CreateEndpoints_WithTlsClientCertificate_PreservesClientAuthentication()
    {
        // Arrange
        var certificates = new X509CertificateCollection();
        var factory = new ConnectionFactory { Uri = new Uri("amqps://localhost") };
        factory.Ssl.Certs = certificates;
        factory.Ssl.CertPath = "client.pfx";
        factory.Ssl.CertPassphrase = "test-password";

        // Act
        var endpoint = Assert.Single(RabbitMQEndpointResolver.CreateEndpoints(factory, ["broker"]));

        // Assert
        Assert.Same(certificates, endpoint.Ssl.Certs);
        Assert.Equal("client.pfx", endpoint.Ssl.CertPath);
        Assert.Equal("test-password", endpoint.Ssl.CertPassphrase);
        Assert.Equal("broker", endpoint.Ssl.ServerName);
        Assert.Equal(SslPolicyErrors.None, endpoint.Ssl.AcceptablePolicyErrors);
    }
}
