using System;

namespace Foundatio.Resilience;

public class BrokenCircuitException(string message = "The circuit is now open and is not allowing calls.") : Exception(message);
