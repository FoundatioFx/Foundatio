using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Amazon.SQS.Model;

namespace Foundatio.Messaging;

public sealed partial class AwsMessageTransport
{
    private readonly object _batchersLock = new();
    private readonly Dictionary<string, AwsRequestBatcher<PreparedMessage, SendItemResult>> _sendBatchers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AwsRequestBatcher<string, Exception?>> _deleteBatchers = new(StringComparer.Ordinal);

    private AwsRequestBatcher<PreparedMessage, SendItemResult> GetSendBatcher(bool topic, string address, int maximumBytes)
    {
        string key = (topic ? "sns:" : "sqs:") + address;
        lock (_batchersLock)
        {
            ThrowIfDisposed();
            if (_sendBatchers.TryGetValue(key, out var batcher))
                return batcher;
            batcher = new AwsRequestBatcher<PreparedMessage, SendItemResult>(_options, maximumBytes, static message => message.Bytes,
                (batch, ct) => SendPreparedBatchAsync(topic, address, batch, ct), delayWhenIdle: topic);
            _sendBatchers.Add(key, batcher);
            return batcher;
        }
    }

    private AwsRequestBatcher<string, Exception?> GetDeleteBatcher(string queueUrl)
    {
        lock (_batchersLock)
        {
            ThrowIfDisposed();
            if (_deleteBatchers.TryGetValue(queueUrl, out var batcher))
                return batcher;
            batcher = new AwsRequestBatcher<string, Exception?>(_options, Int32.MaxValue, static _ => 0,
                (receipts, ct) => DeleteBatchAsync(queueUrl, receipts, ct));
            _deleteBatchers.Add(queueUrl, batcher);
            return batcher;
        }
    }

    private async Task<Exception?[]> DeleteBatchAsync(string queueUrl, IReadOnlyList<string> receipts, CancellationToken ct)
    {
        var entries = new List<DeleteMessageBatchRequestEntry>(receipts.Count);
        for (int i = 0; i < receipts.Count; i++)
            entries.Add(new DeleteMessageBatchRequestEntry(i.ToString(CultureInfo.InvariantCulture), receipts[i]));
        var response = await _sqs.Value.DeleteMessageBatchAsync(new DeleteMessageBatchRequest { QueueUrl = queueUrl, Entries = entries }, ct).ConfigureAwait(false);
        var results = new Exception?[receipts.Count];
        var seen = new bool[receipts.Count];
        foreach (var entry in response.Successful ?? [])
            MarkSeen(entry.Id);
        foreach (var entry in response.Failed ?? [])
            results[MarkSeen(entry.Id)] = new MessageBusException($"SQS did not acknowledge deletion ({entry.Code}): {entry.Message}");
        for (int i = 0; i < receipts.Count; i++)
            if (!seen[i]) results[i] = new MessageBusException("SQS did not return an acknowledgement for this receipt.");
        return results;

        int MarkSeen(string id)
        {
            if (!Int32.TryParse(id, CultureInfo.InvariantCulture, out int index) || index < 0 || index >= receipts.Count || seen[index])
                throw new MessageBusException("SQS returned an invalid or duplicate acknowledgement ID.");
            seen[index] = true;
            return index;
        }
    }

    private Task DisposeBatchersAsync()
    {
        var tasks = new List<Task>();
        lock (_batchersLock)
        {
            foreach (var batcher in _sendBatchers.Values)
                tasks.Add(batcher.DisposeAsync().AsTask());
            foreach (var batcher in _deleteBatchers.Values)
                tasks.Add(batcher.DisposeAsync().AsTask());
            _sendBatchers.Clear();
            _deleteBatchers.Clear();
        }
        return Task.WhenAll(tasks);
    }
}
