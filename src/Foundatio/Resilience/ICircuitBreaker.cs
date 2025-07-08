using System;

namespace Foundatio.Resilience;

public interface ICircuitBreaker
{
    CircuitState State { get; }
    void BeforeCall();
    void RecordCallSuccess();
    void RecordCallFailure(Exception ex);
}
