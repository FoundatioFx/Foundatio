using System;

namespace Foundatio.Logging {
    public interface ILoggerFactory : IDisposable {
        ILogger CreateLogger(string categoryName);
    }
}