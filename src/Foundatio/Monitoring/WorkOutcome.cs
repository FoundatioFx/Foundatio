namespace Foundatio.Monitoring;

/// <summary>How a message delivery or a job run ended.</summary>
public enum WorkOutcome
{
    /// <summary>The handlers returned and the message was completed, or the job succeeded.</summary>
    Succeeded,

    /// <summary>It failed, or asked to be retried, and will run again after its retry delay.</summary>
    Retried,

    /// <summary>
    /// A job failed for good. For a message, the outcome could not be saved (completing or dead-lettering it failed),
    /// so the transport delivers it again.
    /// </summary>
    Failed,

    /// <summary>The message was moved to its dead-letter queue, or dropped from an instance subscription.</summary>
    DeadLettered,

    /// <summary>
    /// It stopped before finishing: a job was cancelled, or the work was interrupted by shutdown or a lost lease. Work
    /// interrupted by shutdown or a lost lease runs again.
    /// </summary>
    Cancelled,

    /// <summary>A message was completed without a handler: its subscription has no handler for the type, or it expired.</summary>
    Skipped
}
