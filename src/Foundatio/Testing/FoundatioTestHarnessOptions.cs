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
    ///     MaxDelay = TimeSpan.FromDays(13),   // like SQS
    ///     MaxMessageSize = 256 * 1024,
    ///     MaxSendBatchSize = 10,
    ///     MaxReceiveBatchSize = 10
    /// });
    /// </code>
    /// </example>
    public TransportCapabilities? TransportCapabilities { get; set; }

    /// <summary>
    /// The clock messaging, jobs, the in-memory transport and the job store run on. Defaults to the system clock,
    /// whatever <see cref="System.TimeProvider"/> the app registers, so a test that moves the app's clock does not strand
    /// leases or postpone retries. Set a fake clock (for example Microsoft.Extensions.Time.Testing's FakeTimeProvider)
    /// to drive delays, retries and leases from the test; advance it, then call
    /// <see cref="FoundatioTestHarness.WaitForIdleAsync"/>. The harness's waits and timeouts always use real time.
    /// </summary>
    public System.TimeProvider? TimeProvider { get; set; }
}
