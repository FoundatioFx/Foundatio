using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Foundatio.Messaging;
using Xunit;

namespace Foundatio.Aws.Tests;

public class AwsMessageAdministrationTests
{
    [Fact]
    public async Task ReplayBeyondFirstReceiveBatch_ResetsRetryStateAndPreservesOtherDeadLetters()
    {
        string? connection = Environment.GetEnvironmentVariable("FOUNDATIO_AWS_CONNECTION_STRING");
        Assert.SkipWhen(String.IsNullOrEmpty(connection), "FOUNDATIO_AWS_CONNECTION_STRING not set.");
        var options = AwsMessageTransportOptions.FromConnectionString(connection);
        options.ResourcePrefix = $"admin-test-{Guid.NewGuid():N}-";
        await using var transport = new AwsMessageTransport(options);
        var token = TestContext.Current.CancellationToken;
        var source = DestinationAddress.ForQueue("work");
        var dead = DestinationAddress.ForQueue("work.deadletter");
        await transport.EnsureAsync([new() { Address = source }, new() { Address = dead }], token);
        try
        {
            var messages = Enumerable.Range(0, 15).Select(index => new TransportMessage
            {
                Body = "{}"u8.ToArray(),
                ContentType = "application/json",
                MessageId = $"original-{index}",
                Headers = MessageHeaders.Create(new Dictionary<string, string>
                {
                    [KnownHeaders.Attempts] = "9",
                    [KnownHeaders.DeadLetterReason] = "Original failure"
                })
            }).ToArray();
            (await transport.SendAsync(dead, messages, new(), token)).EnsureAccepted(messages.Length);
            var administration = new MessageAdministration(transport);
            var snapshot = await administration.PeekDeadLettersAsync(source, 15, token);
            Assert.Equal(15, snapshot.Count);
            var selected = snapshot[^1];
            Assert.True(await administration.ReplayDeadLetterAsync(source, selected.Id, cancellationToken: token));
            var replay = Assert.Single(await transport.ReceiveAsync(source, new() { MaxMessages = 1, MaxWaitTime = TimeSpan.FromSeconds(2) }, token));
            Assert.Equal(selected.ApplicationMessageId, replay.ApplicationMessageId);
            Assert.NotNull(replay.EnqueuedUtc);
            Assert.False(replay.Headers.ContainsKey(KnownHeaders.Attempts));
            Assert.False(replay.Headers.ContainsKey(KnownHeaders.DeadLetterReason));
            await transport.CompleteAsync(replay, token);
            var remaining = await administration.PeekDeadLettersAsync(source, 15, token);
            Assert.Equal(14, remaining.Count);
            Assert.DoesNotContain(remaining, entry => entry.Id == selected.Id);
        }
        finally
        {
            await transport.DeleteAsync(source, token);
            await transport.DeleteAsync(dead, token);
        }
    }
}
