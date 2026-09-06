using System.Diagnostics;

namespace Foundatio.Messaging.Benchmarks;

public sealed record LoadMessage(string RunId, int Sequence, long StartedTimestamp, string Payload);

public sealed class DeliveryTracker : IDisposable
{
    private readonly string _runId;
    private readonly string _payload;
    private readonly int _subscribers;
    private readonly int[] _remaining;
    private readonly int[] _seen;
    private readonly SemaphoreSlim _window;
    private long _unique, _duplicates, _invalid, _expected, _completed;
    private long _lastDelivery;
    public LatencyHistogram Latency { get; } = new();
    public long UniqueDeliveries => Volatile.Read(ref _unique);
    public long Duplicates => Volatile.Read(ref _duplicates);
    public long InvalidDeliveries => Volatile.Read(ref _invalid);
    public long ExpectedInputs => Volatile.Read(ref _expected);
    public long OutstandingInputs => ExpectedInputs - Volatile.Read(ref _completed);
    public long LastDeliveryTimestamp => Volatile.Read(ref _lastDelivery);

    public DeliveryTracker(string runId, int maxMessages, int subscribers, int window, string payload)
    {
        _runId = runId; _payload = payload; _subscribers = subscribers;
        _remaining = new int[maxMessages];
        _seen = new int[checked((int)(((long)maxMessages * subscribers + 31) / 32))];
        _window = new SemaphoreSlim(window, window);
    }

    public async Task ReserveAsync(int count, CancellationToken token)
    {
        int reserved = 0;
        try { for (; reserved < count; reserved++) await _window.WaitAsync(token).ConfigureAwait(false); }
        catch { if (reserved > 0) _window.Release(reserved); throw; }
    }

    public void ReleaseUnused(int count) => _window.Release(count);

    public void Expect(int sequence)
    {
        Volatile.Write(ref _remaining[sequence], _subscribers);
        Interlocked.Increment(ref _expected);
    }

    public void Record(int subscriber, LoadMessage message)
    {
        if (message.RunId != _runId || (uint)subscriber >= _subscribers || (uint)message.Sequence >= _remaining.Length
            || !String.Equals(message.Payload, _payload, StringComparison.Ordinal))
        { Interlocked.Increment(ref _invalid); return; }
        long bit = ((long)message.Sequence * _subscribers) + subscriber;
        int mask = 1 << (int)(bit % 32);
        if ((Interlocked.Or(ref _seen[bit / 32], mask) & mask) != 0)
        { Interlocked.Increment(ref _duplicates); return; }
        if (Volatile.Read(ref _remaining[message.Sequence]) <= 0)
        { Interlocked.Increment(ref _invalid); return; }
        long now = Stopwatch.GetTimestamp();
        Latency.RecordMicroseconds((long)(Stopwatch.GetElapsedTime(message.StartedTimestamp, now).TotalMicroseconds));
        Interlocked.Exchange(ref _lastDelivery, now);
        Interlocked.Increment(ref _unique);
        if (Interlocked.Decrement(ref _remaining[message.Sequence]) == 0)
        { Interlocked.Increment(ref _completed); _window.Release(); }
    }

    public void Dispose() => _window.Dispose();
}
