using System;

namespace Foundatio.Logging {
    public interface IHaveLogger {
        ILogger Logger { get; }
    }
}
