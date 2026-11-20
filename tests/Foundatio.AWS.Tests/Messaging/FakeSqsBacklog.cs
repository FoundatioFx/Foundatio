using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using Foundatio.Messaging.Transport;
using SqsMessage = Amazon.SQS.Model.Message;

namespace Foundatio.AWS.Tests.Messaging;

/// <summary>
/// Plays an SQS queue's messages from memory for <see cref="InterceptingSqsClient.Intercept"/>, so a test can run
/// thousands of hides without the emulator and fake what it does not enforce: the in-flight count and the
/// <c>OverLimit</c> refusals of a queue over its quota. Receives take visible messages, hides keep them in flight,
/// releases (a visibility of 0) make them visible again, deletes remove them, and sends are recorded but not delivered.
/// When nothing is visible, receives go on to the emulator's empty queue.
/// </summary>
public sealed class FakeSqsBacklog
{
    private readonly ConcurrentQueue<Stored> _visible = new();
    private readonly ConcurrentDictionary<string, Stored> _inFlight = new(StringComparer.Ordinal);
    private long _receipts;
    private int _hides;
    private int _refusedReceives;

    /// <summary>Messages in flight that are not in the backlog, such as other consumers' deliveries.</summary>
    public long OtherInFlight { get; set; }

    /// <summary>How many of the next receives to refuse with <see cref="OverLimitException"/>.</summary>
    public int RefuseReceives { get; set; }

    /// <summary>Refuses to hide messages (a visibility above 0) with an <c>OverLimit</c> entry failure while true.</summary>
    public bool RefuseHides { get; set; }

    public int Visible => _visible.Count;

    public int InFlight => _inFlight.Count;

    /// <summary>How many hides SQS applied, over all receivers.</summary>
    public int Hides => Volatile.Read(ref _hides);

    public int RefusedReceives => Volatile.Read(ref _refusedReceives);

    /// <summary>The messages sent to the queue, with their delay in seconds.</summary>
    public ConcurrentQueue<(string MessageId, int DelaySeconds)> Sent { get; } = new();

    /// <summary>The receipt handles released with a visibility of 0.</summary>
    public ConcurrentQueue<string> Released { get; } = new();

    /// <summary>Adds messages due at <paramref name="deliverAt"/>, sent by the transport, with bodies of <paramref name="bodySize"/> characters.</summary>
    public void AddEarly(int count, DateTimeOffset deliverAt, int bodySize = 10, string idPrefix = "early")
    {
        for (int i = 0; i < count; i++)
        {
            string id = $"{idPrefix}-{i:D5}";
            var encoded = SqsMessageTransportCodec.Encode(new OutgoingMessage { MessageId = id, Body = Encoding.UTF8.GetBytes(new string('a', bodySize)), Headers = new Dictionary<string, string>() }, deliverAt);
            _visible.Enqueue(new Stored(id, encoded, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        }
    }

    public async Task<AmazonWebServiceResponse> InterceptAsync(AmazonWebServiceRequest request, Func<Task<AmazonWebServiceResponse>> send)
    {
        switch (request)
        {
            case ReceiveMessageRequest receive:
                if (RefuseReceives > 0)
                {
                    RefuseReceives--;
                    Interlocked.Increment(ref _refusedReceives);
                    throw new OverLimitException("Too many messages in flight.") { ErrorCode = "OverLimit" };
                }

                var messages = new List<SqsMessage>();
                while (messages.Count < (receive.MaxNumberOfMessages ?? 1) && _visible.TryDequeue(out var stored))
                    messages.Add(Lease(stored));

                return messages.Count > 0 ? new ReceiveMessageResponse { Messages = messages } : await send();
            case ChangeMessageVisibilityRequest change:
                if (ChangeVisibility(change.ReceiptHandle, change.VisibilityTimeout ?? 0) is not null)
                    throw new OverLimitException("Too many messages in flight.") { ErrorCode = "OverLimit" };

                return new ChangeMessageVisibilityResponse();
            case ChangeMessageVisibilityBatchRequest changes:
                var changed = changes.Entries.Select(e => (e.Id, Failure: ChangeVisibility(e.ReceiptHandle, e.VisibilityTimeout ?? 0))).ToList();
                return new ChangeMessageVisibilityBatchResponse
                {
                    Successful = changed.Where(c => c.Failure is null).Select(c => new ChangeMessageVisibilityBatchResultEntry { Id = c.Id }).ToList(),
                    Failed = changed.Where(c => c.Failure is not null).Select(c => new BatchResultErrorEntry { Id = c.Id, Code = c.Failure, SenderFault = true }).ToList()
                };
            case DeleteMessageRequest delete:
                _inFlight.TryRemove(delete.ReceiptHandle, out _);
                return new DeleteMessageResponse();
            case DeleteMessageBatchRequest deletes:
                foreach (var entry in deletes.Entries)
                    _inFlight.TryRemove(entry.ReceiptHandle, out _);

                return new DeleteMessageBatchResponse { Successful = deletes.Entries.Select(e => new DeleteMessageBatchResultEntry { Id = e.Id }).ToList(), Failed = [] };
            case SendMessageRequest sendOne:
                Sent.Enqueue((Decode(sendOne.MessageBody, sendOne.MessageAttributes), sendOne.DelaySeconds ?? 0));
                return new SendMessageResponse { MessageId = Guid.NewGuid().ToString("N") };
            case SendMessageBatchRequest sendMany:
                foreach (var entry in sendMany.Entries)
                    Sent.Enqueue((Decode(entry.MessageBody, entry.MessageAttributes), entry.DelaySeconds ?? 0));

                return new SendMessageBatchResponse { Successful = sendMany.Entries.Select(e => new SendMessageBatchResultEntry { Id = e.Id, MessageId = Guid.NewGuid().ToString("N") }).ToList(), Failed = [] };
            case GetQueueAttributesRequest attributes when attributes.AttributeNames.Contains(QueueAttributeName.ApproximateNumberOfMessagesNotVisible):
                return new GetQueueAttributesResponse
                {
                    Attributes = new Dictionary<string, string>
                    {
                        [QueueAttributeName.ApproximateNumberOfMessagesNotVisible] = (OtherInFlight + _inFlight.Count).ToString(CultureInfo.InvariantCulture)
                    }
                };
            default:
                return await send();
        }
    }

    private SqsMessage Lease(Stored stored)
    {
        string receiptHandle = "fake-" + Interlocked.Increment(ref _receipts).ToString(CultureInfo.InvariantCulture);
        stored.Receives++;
        stored.FirstReceived ??= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _inFlight[receiptHandle] = stored;
        return new SqsMessage
        {
            MessageId = stored.MessageId,
            ReceiptHandle = receiptHandle,
            Body = stored.Encoded.Body,
            MessageAttributes = SqsMessageTransportCodec.ToSqsAttributes(stored.Encoded),
            Attributes = new Dictionary<string, string>
            {
                [SqsMessageTransport.SentTimestampAttribute] = stored.SentAt.ToString(CultureInfo.InvariantCulture),
                [SqsMessageTransport.ReceiveCountAttribute] = stored.Receives.ToString(CultureInfo.InvariantCulture),
                [SqsMessageTransport.FirstReceiveTimestampAttribute] = stored.FirstReceived.Value.ToString(CultureInfo.InvariantCulture)
            }
        };
    }

    private string? ChangeVisibility(string receiptHandle, int seconds)
    {
        if (seconds > 0 && RefuseHides)
            return "OverLimit";

        if (seconds > 0)
        {
            Interlocked.Increment(ref _hides);
            return null;
        }

        Released.Enqueue(receiptHandle);
        if (_inFlight.TryRemove(receiptHandle, out var stored))
            _visible.Enqueue(stored);

        return null;
    }

    private static string Decode(string body, Dictionary<string, MessageAttributeValue>? attributes) =>
        SqsMessageTransportCodec.Decode(body, attributes?.GetValueOrDefault(SqsMessageTransportCodec.AttributeName), "unknown").MessageId;

    private sealed class Stored(string messageId, SqsEncodedMessage encoded, long sentAt)
    {
        public string MessageId { get; } = messageId;
        public SqsEncodedMessage Encoded { get; } = encoded;
        public long SentAt { get; } = sentAt;
        public int Receives { get; set; }
        public long? FirstReceived { get; set; }
    }
}
