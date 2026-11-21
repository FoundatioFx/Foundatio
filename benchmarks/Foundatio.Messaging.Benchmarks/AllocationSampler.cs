using System;
using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using System.Linq;

namespace Foundatio.Messaging.Benchmarks;

/// <summary>Samples allocations by type from the runtime's AllocationTick event (about one per 100 KB allocated).</summary>
public sealed class AllocationSampler : EventListener
{
    private readonly ConcurrentDictionary<string, long> _bytes = new();
    private volatile bool _enabled;

    public void Start()
    {
        _bytes.Clear();
        _enabled = true;
    }

    public string Stop(int top = 30)
    {
        _enabled = false;
        long total = _bytes.Values.Sum();
        return String.Join(Environment.NewLine, _bytes.OrderByDescending(p => p.Value).Take(top)
            .Select(p => $"  {100.0 * p.Value / Math.Max(total, 1),5:F1}%  {p.Key}"));
    }

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (eventSource.Name == "Microsoft-Windows-DotNETRuntime")
            EnableEvents(eventSource, EventLevel.Verbose, (EventKeywords)0x1);
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        if (!_enabled || eventData.EventName != "GCAllocationTick_V4" || eventData.Payload is null)
            return;

        int typeIndex = eventData.PayloadNames!.IndexOf("TypeName");
        int amountIndex = eventData.PayloadNames.IndexOf("AllocationAmount64");
        if (typeIndex < 0 || amountIndex < 0)
            return;

        string type = eventData.Payload[typeIndex] as string ?? "?";
        long amount = Convert.ToInt64(eventData.Payload[amountIndex]);
        _bytes.AddOrUpdate(type, amount, (_, b) => b + amount);
    }
}
