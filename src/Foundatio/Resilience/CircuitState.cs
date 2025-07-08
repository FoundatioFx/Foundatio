namespace Foundatio.Resilience;

public enum CircuitState
{
    /// <summary>
    /// Attempts are allowed
    /// </summary>
    Closed,
    /// <summary>
    /// No attempts are allowed
    /// </summary>
    Open,
    /// <summary>
    /// Some attempts allowed to test
    /// </summary>
    HalfOpen,
    /// <summary>
    /// Circuit is manually opened and no attempts are allowed
    /// </summary>
    ManuallyOpen
}
