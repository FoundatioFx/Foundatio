using System;

namespace Foundatio.Logging {
    public interface ILoggerFactory {
        ILogger CreateLogger(string categoryName);
    }
}
