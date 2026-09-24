using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Amazon.SQS.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Messaging;

public sealed partial class AwsMessageTransport
{
    private const string NodeRoleTag = "fnd:role";
    private const string NodeTopicTag = "fnd:topic";
    private const string NodeHeartbeatTag = "fnd:heartbeat";

    /// <summary>Owns a tagged SQS subscription for one node; active nodes renew it and startup reaps stale peers.</summary>
    public async Task<IManagedNodeSubscription> OpenNodeSubscriptionAsync(MessageNodeSubscriptionOptions options, CancellationToken cancellationToken = default)
    {
        options.Validate();
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MessageRetention, TimeSpan.FromMinutes(1));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.MessageRetention, TimeSpan.FromDays(14));
        // A fresh identity avoids queue-deleted-recently failures and prevents two processes adopting one receipt namespace.
        var source = DestinationAddress.ForSubscription(options.Topic, $"node-{options.NodeId}-{Guid.NewGuid():N}");
        await EnsureSubscriptionAsync(source, cancellationToken).ConfigureAwait(false);
        string url = await ResolveQueueUrlAsync(source, cancellationToken).ConfigureAwait(false);
        try
        {
            await _sqs.Value.SetQueueAttributesAsync(new SetQueueAttributesRequest
            {
                QueueUrl = url,
                Attributes = new Dictionary<string, string>
                {
                    ["MessageRetentionPeriod"] = Math.Ceiling(options.MessageRetention.TotalSeconds).ToString(CultureInfo.InvariantCulture)
                }
            }, cancellationToken).ConfigureAwait(false);
            await TagNodeAsync(url, options.Topic, cancellationToken).ConfigureAwait(false);
            await SweepNodesAsync(options.Topic, options.StaleAfter, cancellationToken).ConfigureAwait(false);
            return new NodeOwner(this, source, url, options);
        }
        catch
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await DeleteAsync(source, cleanup.Token).ConfigureAwait(false); } catch { }
            throw;
        }
    }

    private Task TagNodeAsync(string url, string topic, CancellationToken cancellationToken)
        => _sqs.Value.TagQueueAsync(new TagQueueRequest
        {
            QueueUrl = url,
            Tags = new Dictionary<string, string>
            {
                [NodeRoleTag] = "node-subscription",
                [NodeTopicTag] = topic,
                [NodeHeartbeatTag] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
            }
        }, cancellationToken);

    private async Task SweepNodesAsync(string topic, TimeSpan staleAfter, CancellationToken cancellationToken)
    {
        string physicalPrefix = SanitizeResourceName(_options.ResourcePrefix + topic + "/node-");
        physicalPrefix = physicalPrefix[..Math.Min(32, physicalPrefix.Length)];
        string? cursor = null;
        do
        {
            var page = await _sqs.Value.ListQueuesAsync(new ListQueuesRequest
            {
                QueueNamePrefix = physicalPrefix,
                MaxResults = 100,
                NextToken = cursor
            }, cancellationToken).ConfigureAwait(false);
            foreach (string url in page.QueueUrls ?? [])
            {
                try
                {
                    var tags = (await _sqs.Value.ListQueueTagsAsync(new ListQueueTagsRequest { QueueUrl = url }, cancellationToken).ConfigureAwait(false)).Tags;
                    if (tags is null || !tags.TryGetValue(NodeRoleTag, out var role) || role != "node-subscription"
                        || !tags.TryGetValue(NodeTopicTag, out var ownedTopic) || ownedTopic != topic
                        || !tags.TryGetValue(NodeHeartbeatTag, out var heartbeat)
                        || !DateTimeOffset.TryParse(heartbeat, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var lastSeen)
                        || lastSeen >= DateTimeOffset.UtcNow - staleAfter) continue;
                    // Re-read ownership immediately before deletion; a peer may have resumed during the scan.
                    var current = (await _sqs.Value.ListQueueTagsAsync(new ListQueueTagsRequest { QueueUrl = url }, cancellationToken).ConfigureAwait(false)).Tags;
                    if (current is null || !current.TryGetValue(NodeHeartbeatTag, out var value) || value != heartbeat) continue;
                    var attributes = await _sqs.Value.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["QueueArn"] }, cancellationToken).ConfigureAwait(false);
                    string? topicArn = await FindTopicArnAsync(topic, cancellationToken).ConfigureAwait(false);
                    if (topicArn is not null && await FindSubscriptionArnAsync(topicArn, attributes.QueueARN, cancellationToken).ConfigureAwait(false) is { } arn)
                        await _sns.Value.UnsubscribeAsync(arn, cancellationToken).ConfigureAwait(false);
                    await _sqs.Value.DeleteQueueAsync(url, cancellationToken).ConfigureAwait(false);
                }
                catch (QueueDoesNotExistException) { }
            }
            cursor = page.NextToken;
        } while (!String.IsNullOrEmpty(cursor));
    }

    private sealed class NodeOwner : IManagedNodeSubscription
    {
        private readonly AwsMessageTransport _owner;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _heartbeat;
        private readonly ILogger _logger;
        private int _disposed;
        public DestinationAddress Source { get; }

        public NodeOwner(AwsMessageTransport owner, DestinationAddress source, string url, MessageNodeSubscriptionOptions options)
        {
            _owner = owner;
            _logger = owner._options.LoggerFactory?.CreateLogger<AwsMessageTransport>() ?? NullLogger<AwsMessageTransport>.Instance;
            Source = source;
            _heartbeat = HeartbeatAsync(url, options);
        }

        private async Task HeartbeatAsync(string url, MessageNodeSubscriptionOptions options)
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(options.HeartbeatInterval, _stop.Token).ConfigureAwait(false);
                    await _owner.TagNodeAsync(url, options.Topic, _stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return; }
                catch (Exception exception) { _logger.LogWarning(exception, "Unable to renew node subscription {Source}; retrying on the next heartbeat", Source); }
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            await _stop.CancelAsync().ConfigureAwait(false);
            await _heartbeat.ConfigureAwait(false);
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await _owner.DeleteAsync(Source, cleanup.Token).ConfigureAwait(false); }
            finally { _stop.Dispose(); }
        }
    }
}
