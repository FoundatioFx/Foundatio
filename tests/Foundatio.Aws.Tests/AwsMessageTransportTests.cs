using System;
using System.Text;
using System.Threading.Tasks;
using Foundatio.Messaging;
using Xunit;

namespace Foundatio.Aws.Tests;

public class AwsMessageTransportTests
{
    private static AwsMessageTransport? CreateTransport(string testName)
    {
        string? connectionString = Environment.GetEnvironmentVariable("FOUNDATIO_AWS_CONNECTION_STRING");
        if (String.IsNullOrEmpty(connectionString))
            return null;

        var options = AwsMessageTransportOptions.FromConnectionString(connectionString);
        options.ResourcePrefix = $"fnd-{testName}-{Guid.NewGuid():N}"[..24] + "-";
        return new AwsMessageTransport(options);
    }

    [Fact]
    public async Task TextContentBody_RoundTripsThroughSqsAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var transport = CreateTransport("text");
        if (transport is null)
        {
            Assert.Skip("FOUNDATIO_AWS_CONNECTION_STRING not set.");
            return;
        }

        // Non-ASCII JSON exercises UTF-8 round-trip through the SQS string body (the text-content path that avoids base64).
        string json = "{\"greeting\":\"héllo wörld\",\"n\":42}";
        await transport.EnsureAsync([new DestinationDeclaration { Name = "text-body", Role = DestinationRole.Queue }], cancellationToken);

        await transport.SendAsync("text-body",
            [new TransportMessage { Body = Encoding.UTF8.GetBytes(json), ContentType = "application/json" }],
            new TransportSendOptions(), cancellationToken);

        var entries = await transport.ReceiveAsync("text-body", new ReceiveRequest { MaxWaitTime = TimeSpan.FromSeconds(2) }, cancellationToken);
        var entry = Assert.Single(entries);
        Assert.Equal(json, Encoding.UTF8.GetString(entry.Body.Span));
        await transport.CompleteAsync(entry, cancellationToken);
    }

    [Fact]
    public async Task BinaryContentBody_RoundTripsThroughSqsAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var transport = CreateTransport("binary");
        if (transport is null)
        {
            Assert.Skip("FOUNDATIO_AWS_CONNECTION_STRING not set.");
            return;
        }

        // Non-UTF-8 bytes must still round-trip (via base64) when no text content type is declared.
        byte[] payload = [0x00, 0x01, 0xFF, 0xFE, 0x10, 0x80];
        await transport.EnsureAsync([new DestinationDeclaration { Name = "binary-body", Role = DestinationRole.Queue }], cancellationToken);

        await transport.SendAsync("binary-body",
            [new TransportMessage { Body = payload }],
            new TransportSendOptions(), cancellationToken);

        var entries = await transport.ReceiveAsync("binary-body", new ReceiveRequest { MaxWaitTime = TimeSpan.FromSeconds(2) }, cancellationToken);
        var entry = Assert.Single(entries);
        Assert.Equal(payload, entry.Body.ToArray());
        await transport.CompleteAsync(entry, cancellationToken);
    }
}
