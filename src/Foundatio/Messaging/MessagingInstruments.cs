using System.Diagnostics.Metrics;

namespace Foundatio.Messaging;

internal static class MessagingInstruments
{
    public static readonly Counter<long> Sent = FoundatioDiagnostics.Meter.CreateCounter<long>("foundatio.messaging.sent", description: "Messages sent or published");
    public static readonly Counter<long> Handled = FoundatioDiagnostics.Meter.CreateCounter<long>("foundatio.messaging.handled", description: "Messages handled, by outcome");
    public static readonly Histogram<double> Duration = FoundatioDiagnostics.Meter.CreateHistogram<double>("foundatio.messaging.handle.duration", "ms", "Time spent handling a message");
    public static readonly Counter<long> DeadLettered = FoundatioDiagnostics.Meter.CreateCounter<long>("foundatio.messaging.dead_lettered", description: "Messages moved to a dead-letter queue");
    public static readonly Counter<long> Unmatched = FoundatioDiagnostics.Meter.CreateCounter<long>("foundatio.messaging.unmatched", description: "Messages received without a matching handler");
    public static readonly Counter<long> Discarded = FoundatioDiagnostics.Meter.CreateCounter<long>("foundatio.messaging.discarded", description: "Messages discarded because their time to live passed");
}
