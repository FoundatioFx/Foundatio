using System;
using System.Collections.Generic;
using System.Threading;

namespace Foundatio.Caching;

/// <summary>
/// Decides whether a value learned from the distributed cache may still be stored in the local cache.
/// </summary>
/// <remarks>
/// <para>
/// A write opens a ticket before it changes the distributed cache and closes it after the local cache has been
/// updated. Opening a write advances the key's generation, so an older write or read that is still in flight can no
/// longer store its result. A read or write only stores its result when no other write was open when it started and
/// the generation has not moved since. Overlapping writes on a key therefore evict it instead of racing to store
/// results that may have committed in a different order.
/// </para>
/// <para>
/// Removals and expiration changes on this client call <see cref="Invalidate"/> after the distributed change and before
/// they touch the local cache. Flushes and prefix removals call <see cref="InvalidateAll"/>.
/// </para>
/// <para>
/// Invalidations from other clients call <see cref="InvalidateWrites"/> or <see cref="InvalidateAllWrites"/>. They only
/// make in-flight writes stale, so a local write that committed before the remote change can't overwrite it locally.
/// Read-throughs that overlap a remote invalidation still store their result, so a peer's write doesn't cost every
/// concurrent reader a miss. A read that fetched the value just before the remote write can therefore cache it until
/// the entry expires, which is the eventual consistency the hybrid cache already documents.
/// </para>
/// <para>
/// Keys share a fixed number of slots so memory stays bounded. A collision only costs an extra eviction or a skipped
/// local store, never a stale entry.
/// </para>
/// </remarks>
internal sealed class LocalCacheFence
{
    private const int SlotCount = 4096;
    private const int SlotMask = SlotCount - 1;

    private readonly long[] _generations = new long[SlotCount];
    private readonly long[] _remoteGenerations = new long[SlotCount];
    private readonly int[] _openWrites = new int[SlotCount];
    private long _epoch;
    private long _remoteEpoch;

    public Ticket BeginWrite(string key)
    {
        return BeginWrite(GetSlot(key));
    }

    /// <summary>
    /// Opens one write per slot used by the keys, so keys of one batch that share a slot don't block each other.
    /// </summary>
    public WriteBatch BeginWrites(IEnumerable<string> keys)
    {
        var tickets = new Dictionary<int, Ticket>();
        foreach (string key in keys)
        {
            int slot = GetSlot(key);
            if (!tickets.ContainsKey(slot))
                tickets[slot] = BeginWrite(slot);
        }

        return new WriteBatch(tickets);
    }

    private Ticket BeginWrite(int slot)
    {
        long remoteEpoch = Volatile.Read(ref _remoteEpoch);
        long remoteGeneration = Volatile.Read(ref _remoteGenerations[slot]);
        int openWrites = Interlocked.Increment(ref _openWrites[slot]);
        long generation = Interlocked.Increment(ref _generations[slot]);
        var kind = openWrites > 1 ? TicketKind.BlockedWrite : TicketKind.Write;
        return new Ticket(this, slot, generation, Volatile.Read(ref _epoch), remoteGeneration, remoteEpoch, kind);
    }

    public Ticket BeginRead(string key)
    {
        int slot = GetSlot(key);
        long epoch = Volatile.Read(ref _epoch);
        long generation = Volatile.Read(ref _generations[slot]);
        var kind = Volatile.Read(ref _openWrites[slot]) > 0 ? TicketKind.BlockedRead : TicketKind.Read;
        return new Ticket(this, slot, generation, epoch, 0, 0, kind);
    }

    /// <summary>
    /// Makes every in-flight ticket for the key stale.
    /// </summary>
    public void Invalidate(string key)
    {
        Interlocked.Increment(ref _generations[GetSlot(key)]);
    }

    /// <summary>
    /// Makes every in-flight ticket stale.
    /// </summary>
    public void InvalidateAll()
    {
        Interlocked.Increment(ref _epoch);
    }

    /// <summary>
    /// Makes in-flight writes for the key stale after another client changed it.
    /// </summary>
    public void InvalidateWrites(string key)
    {
        Interlocked.Increment(ref _remoteGenerations[GetSlot(key)]);
    }

    /// <summary>
    /// Makes every in-flight write stale after another client flushed or removed keys by prefix.
    /// </summary>
    public void InvalidateAllWrites()
    {
        Interlocked.Increment(ref _remoteEpoch);
    }

    private bool IsCurrent(in Ticket ticket)
    {
        if (ticket.Kind is not (TicketKind.Read or TicketKind.Write))
            return false;

        if (Volatile.Read(ref _epoch) != ticket.Epoch || Volatile.Read(ref _generations[ticket.Slot]) != ticket.Generation)
            return false;

        return ticket.Kind is TicketKind.Read
            || (Volatile.Read(ref _remoteEpoch) == ticket.RemoteEpoch
                && Volatile.Read(ref _remoteGenerations[ticket.Slot]) == ticket.RemoteGeneration);
    }

    private void End(in Ticket ticket)
    {
        if (ticket.Kind is TicketKind.Write or TicketKind.BlockedWrite)
            Interlocked.Decrement(ref _openWrites[ticket.Slot]);
    }

    private static int GetSlot(string key)
    {
        return StringComparer.Ordinal.GetHashCode(key) & SlotMask;
    }

    internal enum TicketKind
    {
        Read,
        BlockedRead,
        Write,
        BlockedWrite
    }

    /// <summary>
    /// Tracks one in-flight operation on a key. Dispose a write ticket after the local cache has been updated.
    /// </summary>
    internal readonly struct Ticket : IDisposable
    {
        private readonly LocalCacheFence _fence;

        public Ticket(LocalCacheFence fence, int slot, long generation, long epoch, long remoteGeneration, long remoteEpoch, TicketKind kind)
        {
            _fence = fence;
            Slot = slot;
            Generation = generation;
            Epoch = epoch;
            RemoteGeneration = remoteGeneration;
            RemoteEpoch = remoteEpoch;
            Kind = kind;
        }

        public int Slot { get; }
        public long Generation { get; }
        public long Epoch { get; }
        public long RemoteGeneration { get; }
        public long RemoteEpoch { get; }
        public TicketKind Kind { get; }
        public bool IsRead => Kind is TicketKind.Read or TicketKind.BlockedRead;

        /// <summary>
        /// Whether the ticket's result may still be stored. False once another write started on the key or the key
        /// was invalidated.
        /// </summary>
        public bool IsCurrent => _fence.IsCurrent(this);

        public void Dispose() => _fence.End(this);
    }

    /// <summary>
    /// Tracks a write to several keys. Dispose it after the local cache has been updated.
    /// </summary>
    internal sealed class WriteBatch : IDisposable
    {
        private readonly Dictionary<int, Ticket> _tickets;

        public WriteBatch(Dictionary<int, Ticket> tickets)
        {
            _tickets = tickets;
        }

        public bool IsCurrent(string key) => _tickets[GetSlot(key)].IsCurrent;

        public bool AreAllCurrent()
        {
            foreach (var ticket in _tickets.Values)
            {
                if (!ticket.IsCurrent)
                    return false;
            }

            return true;
        }

        public void Dispose()
        {
            foreach (var ticket in _tickets.Values)
                ticket.Dispose();
        }
    }
}
