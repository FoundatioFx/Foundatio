using System;

namespace Foundatio.Logging {
    public interface ILoggerProvider {
        ILogger CreateLogger(string categoryName);
    }
}
