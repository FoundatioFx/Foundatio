using Foundatio.Messaging.Transport;

namespace Foundatio.Testing;

/// <summary>Configures <see cref="TestHarnessServiceCollectionExtensions.AddFoundatioTestHarness"/>.</summary>
public sealed class FoundatioTestHarnessOptions
{
    /// <summary>
    /// The guarantees and limits of the in-memory transport the harness swaps in. Defaults to every capability with no
    /// limits. Set it to your production transport's capabilities so tests see what production would do: for example
    /// a group's messages handled out of order, or a repeated deduplication id delivered again.
    /// </summary>
    /// <example>
    /// <code>
    /// services.AddFoundatioTestHarness(o => o.TransportCapabilities = new TransportCapabilities
    /// {
    ///     MaxDelay = TimeSpan.FromMinutes(15),   // like SQS standard queues
    ///     MaxMessageSize = 256 * 1024,
    ///     MaxSendBatchSize = 10,
    ///     MaxReceiveBatchSize = 10
    /// });
    /// </code>
    /// </example>
    public TransportCapabilities? TransportCapabilities { get; set; }
}
