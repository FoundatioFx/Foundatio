using System;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;

namespace Foundatio.Logging {
    public class NullLogger : ILogger {
        public static ILogger Instance = new NullLogger();

        public void Log(LogLevel logLevel, int eventId, object state, Exception exception, Func<object, Exception, string> formatter) { }

        public bool IsEnabled(LogLevel logLevel) {
            return false;
        }

        public IDisposable BeginScopeImpl(object state) {
            return new EmptyDisposable();
        }
    }
}
