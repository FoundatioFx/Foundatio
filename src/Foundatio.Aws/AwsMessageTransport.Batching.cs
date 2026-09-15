using System;
using System.Collections.Generic;
using System.Globalization;
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
    private sealed record PreparedMessage(int Index, string Body, string Envelope, MessageHeaders Headers, int Bytes, DateTimeOffset? DeliverAt);

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
        if (messages.Count == 1 && _options.EnableBatching)
            return await SendSingleAsync(destination, PrepareMessage(0, messages[0], options.DeliverAt), topic, maximumBytes, ct).ConfigureAwait(false);

        var results = new SendItemResult[messages.Count];
        var prepared = new List<PreparedMessage>(messages.Count);
        for (int index = 0; index < messages.Count; index++)
        {
            results[index] = new SendItemResult { Index = index, Status = MessageSendStatus.NotAttempted };
            var entry = PrepareMessage(index, messages[index], options.DeliverAt);
            if (entry.Bytes > maximumBytes)
            {
                results[index] = MessageTooLarge(index, maximumBytes);
                continue;
            }
            prepared.Add(entry);
        }
        if (prepared.Count == 0) return new SendResult { Items = results };
        string address = topic ? await ResolveTopicArnAsync(destination.Name, ct).ConfigureAwait(false) : await ResolveQueueUrlAsync(destination, ct).ConfigureAwait(false);
        for (int offset = 0; offset < prepared.Count;)
        {
            var batch = new List<PreparedMessage>(_options.MaxBatchSize);
            int bytes = 0;
            while (offset < prepared.Count && batch.Count < _options.MaxBatchSize && bytes + prepared[offset].Bytes <= maximumBytes)
            {
                var entry = prepared[offset++];
                batch.Add(entry);
                bytes += entry.Bytes;
            }
            if (ct.IsCancellationRequested) break;
            try
            {
                var response = await SendPreparedBatchAsync(topic, address, batch, ct).ConfigureAwait(false);
                for (int i = 0; i < batch.Count; i++)
                    results[batch[i].Index] = response[i].Index == batch[i].Index ? response[i] : response[i] with { Index = batch[i].Index };
            }
            catch (Exception ex)
            {
                InvalidateAddress(destination, ex);
                foreach (var entry in batch)
                    results[entry.Index] = SendFailure(entry.Index, ex);
                break;
            }
        }
        return new SendResult { Items = results };
    }

    private async Task<SendResult> SendSingleAsync(DestinationAddress destination, PreparedMessage message, bool topic, int maximumBytes, CancellationToken ct)
    {
        if (message.Bytes > maximumBytes)
            return new SendResult { Items = [MessageTooLarge(0, maximumBytes)] };

        string address = topic ? await ResolveTopicArnAsync(destination.Name, ct).ConfigureAwait(false) : await ResolveQueueUrlAsync(destination, ct).ConfigureAwait(false);
        if (ct.IsCancellationRequested)
            return new SendResult { Items = [new SendItemResult { Index = 0, Status = MessageSendStatus.NotAttempted }] };

        SendItemResult result;
        try
        {
            result = await GetSendBatcher(topic, address, maximumBytes).ExecuteAsync(message, ct).ConfigureAwait(false);
            if (result.Index != 0) result = result with { Index = 0 };
        }
        catch (Exception ex)
        {
            InvalidateAddress(destination, ex);
            result = SendFailure(0, ex);
        }
        return new SendResult { Items = [result] };
    }

    private static SendItemResult MessageTooLarge(int index, int maximumBytes) => new()
    {
        Index = index,
        Status = MessageSendStatus.Rejected,
        ErrorCode = "MessageTooLarge",
        ErrorMessage = $"Encoded message and attributes exceed {maximumBytes} bytes.",
        Retryable = false
    };

    private void InvalidateAddress(DestinationAddress destination, Exception exception)
    {
        if (exception is QueueDoesNotExistException) _queueUrls.TryRemove(destination.Key, out _);
        if (exception is Amazon.SimpleNotificationService.Model.NotFoundException) _topicArns.TryRemove(destination.Name, out _);
    }

    private static SendItemResult SendFailure(int index, Exception exception)
    {
        bool rejected = exception is AmazonServiceException aws && aws.StatusCode is >= HttpStatusCode.BadRequest and < HttpStatusCode.InternalServerError;
        return new SendItemResult
        {
            Index = index,
            Status = rejected ? MessageSendStatus.Rejected : MessageSendStatus.Unknown,
            ErrorCode = (exception as AmazonServiceException)?.ErrorCode ?? exception.GetType().Name,
            ErrorMessage = exception.Message.Length > 1024 ? exception.Message[..1024] : exception.Message,
            Retryable = exception is OperationCanceledException ? null : !rejected || (exception as AmazonServiceException)?.ErrorCode?.Contains("Throttl", StringComparison.OrdinalIgnoreCase) == true
        };
    }

    private async Task<SendItemResult[]> SendPreparedBatchAsync(bool topic, string address, IReadOnlyList<PreparedMessage> batch, CancellationToken ct)
    {
        var results = new SendItemResult[batch.Count];
        if (topic)
        {
            var entries = new List<PublishBatchRequestEntry>(batch.Count);
            for (int i = 0; i < batch.Count; i++)
            {
                var attributes = BuildAttributes(batch[i], static value => new SnsAttribute { DataType = "String", StringValue = value });
                entries.Add(new PublishBatchRequestEntry { Id = i.ToString(CultureInfo.InvariantCulture), Message = batch[i].Body, MessageAttributes = attributes });
            }
            var response = await _sns.Value.PublishBatchAsync(new PublishBatchRequest { TopicArn = address, PublishBatchRequestEntries = entries }, ct).ConfigureAwait(false);
            foreach (var success in response.Successful ?? [])
                SetOutcome(results, success.Id, MessageSendStatus.Accepted, success.MessageId);
            foreach (var failure in response.Failed ?? [])
                SetOutcome(results, failure.Id, MessageSendStatus.Rejected, null, failure.Code, failure.Message, failure.SenderFault is { } senderFault ? !senderFault : null);
        }
        else
        {
            var entries = new List<SendMessageBatchRequestEntry>(batch.Count);
            for (int i = 0; i < batch.Count; i++)
            {
                var attributes = BuildAttributes(batch[i], static value => new SqsAttribute { DataType = "String", StringValue = value });
                entries.Add(new SendMessageBatchRequestEntry
                {
                    Id = i.ToString(CultureInfo.InvariantCulture),
                    MessageBody = batch[i].Body,
                    DelaySeconds = ToDelaySeconds(batch[i].DeliverAt),
                    MessageAttributes = attributes
                });
            }
            var response = await _sqs.Value.SendMessageBatchAsync(new SendMessageBatchRequest { QueueUrl = address, Entries = entries }, ct).ConfigureAwait(false);
            foreach (var success in response.Successful ?? [])
                SetOutcome(results, success.Id, MessageSendStatus.Accepted, success.MessageId);
            foreach (var failure in response.Failed ?? [])
                SetOutcome(results, failure.Id, MessageSendStatus.Rejected, null, failure.Code, failure.Message, failure.SenderFault is { } senderFault ? !senderFault : null);
        }
        for (int i = 0; i < results.Length; i++)
            results[i] ??= new SendItemResult { Index = i, Status = MessageSendStatus.Unknown };
        return results;
    }

    private static void SetOutcome(SendItemResult[] results, string id, MessageSendStatus status, string? messageId, string? code = null, string? error = null, bool? retryable = null)
    {
        if (!Int32.TryParse(id, CultureInfo.InvariantCulture, out int index) || index < 0 || index >= results.Length)
            throw new MessageBusException("AWS returned an unknown batch entry ID.");
        if (results[index] is not null)
            throw new MessageBusException("AWS returned a duplicate batch entry ID.");
        results[index] = new SendItemResult { Index = index, Status = status, MessageId = messageId, ErrorCode = code, ErrorMessage = error, Retryable = retryable };
    }
}
