using System;
using System.Collections.Generic;
using System.Threading;

namespace Foundatio.Caching;

/// <summary>
/// Prevents an in-flight operation from storing a local result after another write or invalidation.
/// Keys share slots to bound memory. A collision can skip a local store or cause an extra eviction.
/// </summary>
internal sealed class LocalCacheFence
{
    private const int SlotCount = 4096;

    private readonly long[] _generations = new long[SlotCount];
    private readonly int[] _openWrites = new int[SlotCount];
    private long _epoch;

    public Ticket BeginRead(string key)
    {
        int slot = GetSlot(key);
        long epoch = Volatile.Read(ref _epoch);
        long generation = Volatile.Read(ref _generations[slot]);
        bool canStore = Volatile.Read(ref _openWrites[slot]) == 0;
        return new Ticket(this, slot, generation, epoch, isRead: true, canStore);
    }

    public Ticket BeginWrite(string key) => BeginWrite(GetSlot(key));

    private Ticket BeginWrite(int slot)
    {
        // Register before advancing the generation so later writers can see this one.
        // Check for overlap afterward because another writer can advance its generation first.
        Interlocked.Increment(ref _openWrites[slot]);
        long generation = Interlocked.Increment(ref _generations[slot]);
        bool canStore = Volatile.Read(ref _openWrites[slot]) == 1;
        return new Ticket(this, slot, generation, Volatile.Read(ref _epoch), isRead: false, canStore);
    }

    public WriteBatch BeginWrites(string[] keys)
    {
        var tickets = new Dictionary<int, Ticket>(Math.Min(keys.Length, SlotCount));
        foreach (string key in keys)
        {
            int slot = GetSlot(key);
            if (!tickets.ContainsKey(slot))
                tickets.Add(slot, BeginWrite(slot));
        }

        return new WriteBatch(tickets);
    }

    public void Invalidate(string key) => Interlocked.Increment(ref _generations[GetSlot(key)]);

    public void InvalidateAll() => Interlocked.Increment(ref _epoch);

    private static int GetSlot(string key) => StringComparer.Ordinal.GetHashCode(key) & (SlotCount - 1);

    internal readonly struct Ticket(LocalCacheFence fence, int slot, long generation, long epoch, bool isRead, bool canStore) : IDisposable
    {
        public bool IsRead { get; } = isRead;
        public bool IsCurrent => canStore && Volatile.Read(ref fence._epoch) == epoch && Volatile.Read(ref fence._generations[slot]) == generation;

        public void Dispose()
        {
            if (!IsRead)
                Interlocked.Decrement(ref fence._openWrites[slot]);
        }
    }

    internal sealed class WriteBatch(Dictionary<int, Ticket> tickets) : IDisposable
    {
        public bool IsCurrent(string key) => tickets[GetSlot(key)].IsCurrent;

        public bool AreAllCurrent()
        {
            foreach (var ticket in tickets.Values)
            {
                if (!ticket.IsCurrent)
                    return false;
            }

            return true;
        }

        public void Dispose()
        {
            foreach (var ticket in tickets.Values)
                ticket.Dispose();
        }
    }
}
