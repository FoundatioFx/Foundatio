using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Foundatio.Messaging.Transport;

/// <summary>
/// What a receiver knows about one partition beyond its committed offset, kept in the state topic so the partition's
/// next owner carries on from it instead of starting over at the committed offset: the offsets after an unfinished
/// one that are already finished, which are not delivered again, and the delivery counts and retry times of records
/// released in place.
/// </summary>
/// <remarks>
/// The finished ranges apply only once the anchor, the lowest unfinished offset when the state was written, is read
/// again with the same message id. That ties them to the records they describe, so they are never applied to a topic
/// that was deleted and created again. Each attempt carries its own message id for the same reason.
/// </remarks>
internal sealed class KafkaReceiverState
{
    private const byte Version = 1;

    public static KafkaReceiverState Empty { get; } = new() { Finished = [], Attempts = new Dictionary<long, KafkaReceiverAttempt>() };

    /// <summary>The lowest unfinished offset when the state was written, or -1 when there are no finished ranges.</summary>
    public long AnchorOffset { get; init; } = -1;

    public string? AnchorMessageId { get; init; }

    /// <summary>Finished offsets after the anchor, as ascending [Start, End) ranges.</summary>
    public required IReadOnlyList<(long Start, long End)> Finished { get; init; }

    public required IReadOnlyDictionary<long, KafkaReceiverAttempt> Attempts { get; init; }

    public bool IsEmpty => Finished.Count == 0 && Attempts.Count == 0;

    /// <summary>The offset after the last one the state says anything about.</summary>
    public long End
    {
        get
        {
            long end = Finished.Count > 0 ? Finished[^1].End : -1;
            foreach (long offset in Attempts.Keys)
                end = Math.Max(end, offset + 1);

            return end;
        }
    }

    public bool IsFinished(long offset)
    {
        int low = 0, high = Finished.Count - 1;
        while (low <= high)
        {
            int middle = (low + high) / 2;
            var (start, end) = Finished[middle];
            if (offset < start)
                high = middle - 1;
            else if (offset >= end)
                low = middle + 1;
            else
                return true;
        }

        return false;
    }

    public KafkaReceiverState WithoutFinished() => new() { Finished = [], Attempts = Attempts };

    public byte[] Encode()
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Version);
            writer.Write(AnchorOffset);
            writer.Write(AnchorMessageId ?? String.Empty);
            writer.Write(Finished.Count);
            foreach (var (start, end) in Finished)
            {
                writer.Write(start);
                writer.Write(end);
            }

            writer.Write(Attempts.Count);
            foreach (var (offset, attempt) in Attempts)
            {
                writer.Write(offset);
                writer.Write(attempt.MessageId);
                writer.Write(attempt.DeliveryCount);
                writer.Write(attempt.AvailableAt?.ToUnixTimeMilliseconds() ?? 0);
            }
        }

        return stream.ToArray();
    }

    /// <summary>Reads a state written by <see cref="Encode"/>, or returns <c>null</c> for one it cannot read.</summary>
    public static KafkaReceiverState? Decode(byte[] value)
    {
        try
        {
            using var reader = new BinaryReader(new MemoryStream(value), Encoding.UTF8);
            if (reader.ReadByte() != Version)
                return null;

            long anchorOffset = reader.ReadInt64();
            string anchorMessageId = reader.ReadString();
            int finishedCount = reader.ReadInt32();
            var finished = new List<(long Start, long End)>(finishedCount);
            for (int i = 0; i < finishedCount; i++)
                finished.Add((reader.ReadInt64(), reader.ReadInt64()));

            int attemptCount = reader.ReadInt32();
            var attempts = new Dictionary<long, KafkaReceiverAttempt>(attemptCount);
            for (int i = 0; i < attemptCount; i++)
            {
                long offset = reader.ReadInt64();
                string messageId = reader.ReadString();
                int deliveryCount = reader.ReadInt32();
                long availableAt = reader.ReadInt64();
                attempts[offset] = new KafkaReceiverAttempt(messageId, deliveryCount, availableAt > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(availableAt) : null);
            }

            return new KafkaReceiverState
            {
                AnchorOffset = anchorOffset,
                AnchorMessageId = anchorMessageId.Length > 0 ? anchorMessageId : null,
                Finished = finished,
                Attempts = attempts
            };
        }
        catch (Exception ex) when (ex is EndOfStreamException or IOException or ArgumentException or FormatException)
        {
            return null;
        }
    }
}
