using System;
using Foundatio.Jobs.Store;
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
    /// A transport to run on instead of a fresh one, shared with other hosts so a test can run several instances of an
    /// app (or several services) against one broker. Each host's harness records what that host does. You create and
    /// dispose it, and it keeps its own clock. Pausing is not supported on a shared transport, which every host would see.
    /// </summary>
    /// <example>
    /// <code>
    /// await using var transport = new InMemoryMessageTransport();
    /// var store = new InMemoryJobStore();
    /// // for each host:
    /// builder.Services.AddFoundatioTestHarness(o => { o.Transport = transport; o.JobStore = store; });
    /// </code>
    /// </example>
    public InMemoryMessageTransport? Transport { get; set; }

    /// <summary>A job store to run on instead of a fresh one, shared with other hosts; see <see cref="Transport"/>.</summary>
    public InMemoryJobStore? JobStore { get; set; }

    /// <summary>
    /// The clock messaging, jobs, the in-memory transport and the job store run on. Defaults to the system clock,
    /// whatever <see cref="System.TimeProvider"/> the app registers, so a test that moves the app's clock does not strand
    /// leases or postpone retries. Set a fake clock (for example Microsoft.Extensions.Time.Testing's FakeTimeProvider)
    /// to drive delays, retries and leases from the test; advance it, then call
    /// <see cref="FoundatioTestHarness.WaitForIdleAsync"/>. The harness's waits and timeouts always use real time.
    /// </summary>
    public System.TimeProvider? TimeProvider { get; set; }

    /// <summary>
    /// When true, handlers and the job worker start paused: messages and jobs wait until the test runs them with
    /// <see cref="FoundatioTestHarness.ProcessNextAsync(int, System.TimeSpan?, System.Threading.CancellationToken)"/> or
    /// <see cref="FoundatioTestHarness.ProcessPendingAsync(System.TimeSpan?, System.Threading.CancellationToken)"/>, or
    /// calls <see cref="FoundatioTestHarness.ResumeProcessing"/>. Use it to assert what is queued before it is handled,
    /// and to run a pipeline one step at a time. Defaults to false.
    /// </summary>
    public bool StartPaused { get; set; }

    /// <summary>
    /// Replaces the delay of every retry policy, messaging's and jobs' defaults and each handler's and job type's own,
    /// so a retried message or job runs again after this delay instead of its production backoff. Attempts and
    /// <see cref="Resilience.RetryPolicy.ShouldRetry"/> are kept, and handlers that share a subscription are still
    /// checked with their own policies. With <see cref="TimeSpan.Zero"/>, retries run within one
    /// <see cref="FoundatioTestHarness.ProcessPendingAsync(System.TimeSpan?, System.Threading.CancellationToken)"/> call,
    /// or one per <see cref="FoundatioTestHarness.ProcessNextAsync(int, System.TimeSpan?, System.Threading.CancellationToken)"/> call.
    /// Defaults to null, which keeps every policy's delay.
    /// </summary>
    public TimeSpan? RetryDelay { get; set; }
}
