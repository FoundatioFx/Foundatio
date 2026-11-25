using Foundatio.Messaging;

namespace Foundatio.QuickstartSample;

// Contracts are plain records. Give them stable names so renaming a class never strands messages in flight.

/// <summary>An event: every subscribing service gets a copy.</summary>
[Message("orders.order-placed")]
public record OrderPlaced(int OrderId, string Email, decimal Total);

/// <summary>A command: exactly one consumer handles it.</summary>
[Message("orders.send-receipt")]
public record SendReceipt(int OrderId, string Email);

/// <summary>Arguments for a durable job. Producers enqueue these without referencing the job class.</summary>
[Foundatio.Jobs.Job(Name = "reports.generate")]
public record GenerateReport(string Format, int Days);
