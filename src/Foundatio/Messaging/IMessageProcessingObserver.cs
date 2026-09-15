namespace Foundatio.Messaging;

/// <summary>Optional transport decoration for deterministic drains through processing, settlement, and observer updates.</summary>
public interface IMessageProcessingObserver
{
    /// <summary>Called when hosted processing begins. Implementations must not throw.</summary>
    void ProcessingStarted(TransportEntry entry) { }

    /// <summary>Called once when core releases a delivered entry. Implementations must not throw.</summary>
    void ProcessingFinished(TransportEntry entry);
}
