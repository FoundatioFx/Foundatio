namespace Foundatio.Testing;

/// <summary>How a delivery recorded by <see cref="FoundatioTestHarness"/> ended.</summary>
public enum MessageOutcome
{
    /// <summary>Every handler returned, so the message was completed.</summary>
    Completed,

    /// <summary>A handler threw or called <c>Retry</c>, or a queue had no handler for the type; it will be delivered again.</summary>
    Retried,

    /// <summary>Moved to the dead-letter queue (dropped, for instance subscriptions) after the last attempt or a <c>DeadLetter</c> call.</summary>
    DeadLettered,

    /// <summary>A subscription had no handler for the type, so it was completed without handling.</summary>
    Skipped,

    /// <summary>Its time to live passed before it was handled, so it was discarded.</summary>
    Expired,

    /// <summary>Handling was interrupted by shutdown or a lost lease; it will be delivered again.</summary>
    Released
}
