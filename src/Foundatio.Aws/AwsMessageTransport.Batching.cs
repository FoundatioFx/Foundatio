using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Amazon.Runtime;
using Amazon.SQS.Model;
using Amazon.SimpleNotificationService.Model;
using SnsAttribute = Amazon.SimpleNotificationService.Model.MessageAttributeValue;
using SqsAttribute = Amazon.SQS.Model.MessageAttributeValue;

namespace Foundatio.Messaging;

public sealed partial class AwsMessageTransport
{
    private sealed record PreparedMessage(int Index, string Body, Dictionary<string, string> Attributes, int Bytes);

    public async Task<SendResult> SendAsync(DestinationAddress destination, IReadOnlyList<TransportMessage> messages, TransportSendOptions options, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(options);
        ct.ThrowIfCancellationRequested();
        bool topic = destination.Role == DestinationRole.Topic;
        if (topic && options.DeliverAt > DateTimeOffset.UtcNow)
            throw new NotSupportedException("SNS cannot delay publication. Configure Messaging.UseSchedulingStore(...).");
        int maximumBytes = topic ? 262144 : 1048576;
        var results = Enumerable.Range(0, messages.Count).Select(i => new SendItemResult { Index = i, Status = MessageSendStatus.NotAttempted }).ToArray();
        var prepared = new List<PreparedMessage>(messages.Count);
        for (int index = 0; index < messages.Count; index++)
        {
            var (body, encoding) = EncodeBody(messages[index]);
            var attributes = BuildAttributes(messages[index], encoding, static value => value);
            int bytes = Encoding.UTF8.GetByteCount(body);
            foreach (var pair in attributes)
                bytes = checked(bytes + Encoding.UTF8.GetByteCount(pair.Key) + Encoding.UTF8.GetByteCount(pair.Value) + 6);
            if (bytes > maximumBytes)
            {
                results[index] = results[index] with { Status = MessageSendStatus.Rejected, ErrorCode = "MessageTooLarge", ErrorMessage = $"Encoded message and attributes exceed {maximumBytes} bytes.", Retryable = false };
                continue;
            }
            prepared.Add(new PreparedMessage(index, body, attributes, bytes));
        }
        if (prepared.Count == 0) return new SendResult { Items = results };
        string address = topic ? await ResolveTopicArnAsync(destination.Name, ct).ConfigureAwait(false) : await ResolveQueueUrlAsync(destination, ct).ConfigureAwait(false);
        for (int offset = 0; offset < prepared.Count;)
        {
            var batch = new List<PreparedMessage>(10);
            int bytes = 0;
            while (offset < prepared.Count && batch.Count < 10 && bytes + prepared[offset].Bytes <= maximumBytes)
            {
                var entry = prepared[offset++];
                batch.Add(entry);
                bytes += entry.Bytes;
            }
            if (ct.IsCancellationRequested) break;
            foreach (var entry in batch)
                results[entry.Index] = results[entry.Index] with { Status = MessageSendStatus.Unknown };
            try
            {
                if (topic)
                {
                    var response = await _sns.Value.PublishBatchAsync(new PublishBatchRequest
                    {
                        TopicArn = address,
                        PublishBatchRequestEntries = batch.Select(entry => new PublishBatchRequestEntry
                        {
                            Id = entry.Index.ToString(CultureInfo.InvariantCulture),
                            Message = entry.Body,
                            MessageAttributes = entry.Attributes.ToDictionary(p => p.Key, p => new SnsAttribute { DataType = "String", StringValue = p.Value })
                        }).ToList()
                    }, ct).ConfigureAwait(false);
                    foreach (var success in response.Successful ?? [])
                        SetOutcome(results, batch, success.Id, MessageSendStatus.Accepted, success.MessageId);
                    foreach (var failure in response.Failed ?? [])
                        SetOutcome(results, batch, failure.Id, MessageSendStatus.Rejected, null, failure.Code, failure.Message, failure.SenderFault is { } senderFault ? !senderFault : null);
                }
                else
                {
                    var response = await _sqs.Value.SendMessageBatchAsync(new SendMessageBatchRequest
                    {
                        QueueUrl = address,
                        Entries = batch.Select(entry => new SendMessageBatchRequestEntry
                        {
                            Id = entry.Index.ToString(CultureInfo.InvariantCulture),
                            MessageBody = entry.Body,
                            DelaySeconds = ToDelaySeconds(options.DeliverAt),
                            MessageAttributes = entry.Attributes.ToDictionary(p => p.Key, p => new SqsAttribute { DataType = "String", StringValue = p.Value })
                        }).ToList()
                    }, ct).ConfigureAwait(false);
                    foreach (var success in response.Successful ?? [])
                        SetOutcome(results, batch, success.Id, MessageSendStatus.Accepted, success.MessageId);
                    foreach (var failure in response.Failed ?? [])
                        SetOutcome(results, batch, failure.Id, MessageSendStatus.Rejected, null, failure.Code, failure.Message, failure.SenderFault is { } senderFault ? !senderFault : null);
                }
            }
            catch (Exception ex)
            {
                bool rejected = ex is AmazonServiceException aws && aws.StatusCode is >= HttpStatusCode.BadRequest and < HttpStatusCode.InternalServerError;
                if (ex is QueueDoesNotExistException) _queueUrls.TryRemove(destination.Key, out _);
                if (ex is Amazon.SimpleNotificationService.Model.NotFoundException) _topicArns.TryRemove(destination.Name, out _);
                foreach (var entry in batch)
                    results[entry.Index] = results[entry.Index] with
                    {
                        Status = rejected ? MessageSendStatus.Rejected : MessageSendStatus.Unknown,
                        ErrorCode = (ex as AmazonServiceException)?.ErrorCode ?? ex.GetType().Name,
                        ErrorMessage = ex.Message.Length > 1024 ? ex.Message[..1024] : ex.Message,
                        Retryable = ex is OperationCanceledException ? null : !rejected || (ex as AmazonServiceException)?.ErrorCode?.Contains("Throttl", StringComparison.OrdinalIgnoreCase) == true
                    };
                break;
            }
        }
        return new SendResult { Items = results };
    }

    private static void SetOutcome(SendItemResult[] results, List<PreparedMessage> batch, string id, MessageSendStatus status, string? messageId, string? code = null, string? error = null, bool? retryable = null)
    {
        if (!Int32.TryParse(id, CultureInfo.InvariantCulture, out int index) || !batch.Any(e => e.Index == index))
            throw new MessageBusException("AWS returned an unknown batch entry ID.");
        if (results[index].Status != MessageSendStatus.Unknown)
            throw new MessageBusException("AWS returned a duplicate batch entry ID.");
        results[index] = new SendItemResult { Index = index, Status = status, MessageId = messageId, ErrorCode = code, ErrorMessage = error, Retryable = retryable };
    }
}
