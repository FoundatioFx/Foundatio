namespace Foundatio.Utility;

/// <summary>What a background loop (a message processor, the job worker or the job scheduler) is doing.</summary>
public enum ProcessingState
{
    /// <summary>Waiting for the <see cref="IStartupGate"/>, or connecting for the first time.</summary>
    Starting,

    /// <summary>Receiving messages, claiming jobs or scheduling. Errors may still be retried; see <see cref="ProcessingStatus.FailingSince"/>.</summary>
    Running,

    /// <summary>Stopped by host shutdown, or a runtime subscription was disposed.</summary>
    Stopped,

    /// <summary>Stopped unexpectedly, or startup failed, so it will not process anything until the process restarts.</summary>
    Faulted
}
