using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Amazon.SQS;
using Amazon.SQS.Model;
using Foundatio.Extensions;
using Microsoft.Extensions.Logging;
using SqsMessage = Amazon.SQS.Model.Message;

namespace Foundatio.Messaging.Transport;

/// <summary>
/// Deletes the messages a queue held when the purge started, without deleting the queue. It sends a marker and
/// deletes what was sent before it, using the marker's send time on the queue's own clock; messages sent after it are
/// left in the queue. Until the marker is received, a message is deleted only if it was certainly sent earlier: a
/// first receive's timestamp, less the time since the marker was sent, bounds when that was. The purge ends once a
/// receive after the marker returns nothing older, or when no older message has turned up for
/// <see cref="MarkerTimeout"/> and the marker has not either.
/// </summary>
/// <remarks>
/// Newer messages the purge receives stay hidden until it ends and are then released, so they are not received again
/// and again; each such receive still counts toward the message's receive count. Older messages it does not see
/// (delayed, hidden or held by a receiver) are discarded by receivers using the returned marker time.
/// </remarks>
internal sealed class SqsMessageTransportPurge
{
    private const int VisibilityTimeoutSeconds = 60;
    private const int MaxHeldMessages = 1000;
    private static readonly TimeSpan MarkerTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ReleaseTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Allowance for SQS's approximate first-receive timestamps and their whole milliseconds.</summary>
    private static readonly TimeSpan TimestampTolerance = TimeSpan.FromSeconds(1);

    private readonly IAmazonSQS _sqs;
    private readonly string _queueUrl;
    private readonly bool _fifo;
    private readonly ILogger _logger;
    private readonly string _markerId = Guid.NewGuid().ToString("N");
    private readonly Dictionary<string, (string ReceiptHandle, long? SentTimestamp)> _held = new(StringComparer.Ordinal);
    private long _markerSendStarted;
    private long? _sentBeforeMarker;
    private long? _markerSentTimestamp;

    private SqsMessageTransportPurge(IAmazonSQS sqs, string queueUrl, bool fifo, ILogger logger)
    {
        _sqs = sqs;
        _queueUrl = queueUrl;
        _fifo = fifo;
        _logger = logger;
    }

    public int Deleted { get; private set; }

    /// <summary>The marker's send time in Unix milliseconds on the queue's clock, or <c>null</c> when it was not received.</summary>
    public long? PurgedBefore => _markerSentTimestamp;

    public static async Task<SqsMessageTransportPurge> RunAsync(IAmazonSQS sqs, string queueUrl, bool fifo, ILogger logger, CancellationToken cancellationToken)
    {
        var purge = new SqsMessageTransportPurge(sqs, queueUrl, fifo, logger);
        try
        {
            await purge.SendMarkerAsync(cancellationToken).AnyContext();
            await purge.DeleteOlderMessagesAsync(cancellationToken).AnyContext();
        }
        finally
        {
            await purge.ReleaseHeldAsync().AnyContext();
        }

        return purge;
    }

    private async Task SendMarkerAsync(CancellationToken cancellationToken)
    {
        var marker = SqsMessageTransportCodec.EncodePurgeMarker(_markerId);
        _markerSendStarted = Stopwatch.GetTimestamp();
        await _sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = _queueUrl,
            MessageBody = marker.Body,
            MessageAttributes = SqsMessageTransportCodec.ToSqsAttributes(marker),
            MessageGroupId = _fifo ? _markerId : null,
            MessageDeduplicationId = _fifo ? _markerId : null
        }, cancellationToken).AnyContext();
    }

    private async Task DeleteOlderMessagesAsync(CancellationToken cancellationToken)
    {
        long lastProgress = Stopwatch.GetTimestamp();
        while (true)
        {
            bool markerWasReceived = _markerSentTimestamp is not null;
            var response = await _sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = _queueUrl,
                MaxNumberOfMessages = SqsMessageTransport.MaxBatchSize,
                WaitTimeSeconds = 1,
                VisibilityTimeout = VisibilityTimeoutSeconds,
                MessageSystemAttributeNames = [SqsMessageTransport.SentTimestampAttribute, SqsMessageTransport.ReceiveCountAttribute, SqsMessageTransport.FirstReceiveTimestampAttribute],
                MessageAttributeNames = [SqsMessageTransportCodec.AttributeName],
                ReceiveRequestAttemptId = _fifo ? Guid.NewGuid().ToString("N") : null
            }, cancellationToken).AnyContext();

            long receivedAt = Stopwatch.GetTimestamp();
            var messages = (response.Messages ?? []).Select(m => (Message: m, IsMarker: IsOwnMarker(m), SentTimestamp: SqsMessageTransport.GetNumber(m.Attributes, SqsMessageTransport.SentTimestampAttribute))).ToList();
            foreach (var (message, isMarker, sentTimestamp) in messages)
                LearnClock(message, isMarker, sentTimestamp, receivedAt);

            var toDelete = new List<string>();
            bool progress = false;
            long? sentBefore = _markerSentTimestamp ?? _sentBeforeMarker;
            foreach (var (message, isMarker, sentTimestamp) in messages)
            {
                if (isMarker)
                {
                    toDelete.Add(message.ReceiptHandle);
                    progress = true;
                }
                else if (sentTimestamp <= sentBefore)
                {
                    _held.Remove(message.MessageId);
                    toDelete.Add(message.ReceiptHandle);
                    Deleted++;
                    progress = true;
                }
                else if (_held.Count < MaxHeldMessages || _held.ContainsKey(message.MessageId))
                {
                    progress |= sentBefore is null && !_held.ContainsKey(message.MessageId);
                    _held[message.MessageId] = (message.ReceiptHandle, sentTimestamp);
                }
            }

            if (!markerWasReceived && _markerSentTimestamp is { } markerSent)
                toDelete.AddRange(TakeHeld(markerSent));

            await DeleteAsync(toDelete, cancellationToken).AnyContext();

            if (markerWasReceived && !progress)
                return;

            if (progress)
                lastProgress = Stopwatch.GetTimestamp();
            else if (_markerSentTimestamp is null && Stopwatch.GetElapsedTime(lastProgress) > MarkerTimeout)
                return;
        }
    }

    /// <summary>
    /// Records the marker's send time when this is the marker, and otherwise, for a message received for the first
    /// time, a time on the queue's clock before the marker was sent: its first receive less how long ago the marker's
    /// send began.
    /// </summary>
    private void LearnClock(SqsMessage message, bool isMarker, long? sentTimestamp, long receivedAt)
    {
        if (isMarker)
        {
            _markerSentTimestamp ??= sentTimestamp;
            return;
        }

        if (SqsMessageTransport.GetNumber(message.Attributes, SqsMessageTransport.ReceiveCountAttribute) != 1
            || SqsMessageTransport.GetNumber(message.Attributes, SqsMessageTransport.FirstReceiveTimestampAttribute) is not { } firstReceived)
            return;

        long sentBefore = firstReceived - (long)Math.Ceiling((Stopwatch.GetElapsedTime(_markerSendStarted, receivedAt) + TimestampTolerance).TotalMilliseconds);
        if (_sentBeforeMarker is null || sentBefore > _sentBeforeMarker)
            _sentBeforeMarker = sentBefore;
    }

    private bool IsOwnMarker(SqsMessage message)
    {
        var decoded = SqsMessageTransportCodec.Decode(message.Body, message.MessageAttributes?.GetValueOrDefault(SqsMessageTransportCodec.AttributeName), message.MessageId);
        return decoded.IsPurgeMarker && decoded.MessageId == _markerId;
    }

    /// <summary>Removes and returns the receipts of held messages sent at or before <paramref name="sentBefore"/>.</summary>
    private List<string> TakeHeld(long sentBefore)
    {
        var older = _held.Where(h => h.Value.SentTimestamp <= sentBefore).ToList();
        foreach (var (messageId, _) in older)
            _held.Remove(messageId);

        Deleted += older.Count;
        return older.Select(h => h.Value.ReceiptHandle).ToList();
    }

    private async Task DeleteAsync(List<string> receiptHandles, CancellationToken cancellationToken)
    {
        foreach (var chunk in receiptHandles.Chunk(SqsMessageTransport.MaxBatchSize))
        {
            var response = await _sqs.DeleteMessageBatchAsync(new DeleteMessageBatchRequest
            {
                QueueUrl = _queueUrl,
                Entries = chunk.Select((receiptHandle, i) => new DeleteMessageBatchRequestEntry { Id = i.ToString(CultureInfo.InvariantCulture), ReceiptHandle = receiptHandle }).ToList()
            }, cancellationToken).AnyContext();

            if (response.Failed is { Count: > 0 } failed)
                _logger.LogDebug("Could not delete {Count} messages while purging {QueueUrl}: {Code}; receivers discard them", failed.Count, _queueUrl, failed[0].Code);
        }
    }

    /// <summary>Makes the newer messages the purge received visible again, even when the purge was cancelled.</summary>
    private async Task ReleaseHeldAsync()
    {
        if (_held.Count == 0)
            return;

        using var timeout = new CancellationTokenSource(ReleaseTimeout);
        try
        {
            foreach (var chunk in _held.Values.Chunk(SqsMessageTransport.MaxBatchSize))
            {
                await _sqs.ChangeMessageVisibilityBatchAsync(new ChangeMessageVisibilityBatchRequest
                {
                    QueueUrl = _queueUrl,
                    Entries = chunk.Select((held, i) => new ChangeMessageVisibilityBatchRequestEntry
                    {
                        Id = i.ToString(CultureInfo.InvariantCulture),
                        ReceiptHandle = held.ReceiptHandle,
                        VisibilityTimeout = 0
                    }).ToList()
                }, timeout.Token).AnyContext();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not release {Count} messages sent during the purge of {QueueUrl}; they are delivered within {Seconds} seconds", _held.Count, _queueUrl, VisibilityTimeoutSeconds);
        }

        _held.Clear();
    }
}
